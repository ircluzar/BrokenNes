// The host side of the Fruity SDK: a native TFruityPlugHost object (vtable of function pointers) that
// a plugin calls back into, plus the voice bookkeeping FL does around Voice_Release / Voice_Kill.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

/// <summary>One voice as FL's mixer holds it: the TVoiceParams block the plugin keeps a pointer to.</summary>
public sealed unsafe class SimVoice
{
    public nint Tag;               // FL's own handle for the voice (what the plugin passes back to Voice_Kill)
    public nint Handle;            // the plugin's handle (TriggerVoice's return value)
    public TVoiceParams* Params;   // live levels, rewritten by the mixer every block
    public int Color;
    public float Velocity;
    public volatile bool Released, Killed;   // the plugin can kill a voice from any thread (Reset Console does, from the editor's)
}

public sealed unsafe class HostSim : IDisposable
{
    private static readonly Dictionary<nint, HostSim> Hosts = new();
    private static void** vtable;

    private readonly nint self;
    private readonly Dictionary<nint, SimVoice> voices = new();
    private readonly object gate = new();                    // guards voices; held only for dictionary operations, never while calling out
    private readonly ConcurrentQueue<nint> pendingFree = new();
    private nint nextTag = 0x1000;

    /// <summary>Keep the call logs (<see cref="ParamChanges"/>, <see cref="DispatcherCalls"/>) the tests read. A host that runs for hours turns this
    /// off: nothing would ever trim them.</summary>
    public bool Record { get; set; } = true;

    /// <summary>Free a killed voice's native levels block later, in <see cref="FreeDeferred"/>, instead of at once. A live host sets this and calls
    /// FreeDeferred on its audio thread: the plugin may kill a voice from another thread while the audio thread is still writing its levels.</summary>
    public bool DeferFree { get; set; }

    /// <summary>The plugin instance this host serves (set by <see cref="PluginInstance"/>): Voice_Kill / Voice_Release relay to it.</summary>
    public PluginInstance? Plugin { get; set; }

    /// <summary>OnParamChanged calls from the plugin: what FL would record as automation / "last tweaked".</summary>
    public List<(int Index, int Value)> ParamChanges { get; } = new();
    /// <summary>Plugin -> host Dispatcher IDs seen.</summary>
    public List<long> DispatcherCalls { get; } = new();
    public int VoiceKillCalls { get; private set; }
    public int VoiceReleaseCalls { get; private set; }

    public nint Ptr => self;
    public int LiveVoices { get { lock (gate) return voices.Count; } }

    public HostSim()
    {
        EnsureVTable();
        // TFruityPlugHost: vptr, then HostVersion, Flags, AppHandle, WaveTables[10], TempBuffers[4], Reserved[30]. Zeroed, 512 bytes is plenty.
        self = (nint)NativeMemory.AllocZeroed(512);
        *(void***)self = vtable;
        ((int*)self)[2] = 26_01_00_00; // HostVersion, FL 26.1
        lock (Hosts) Hosts[self] = this;
    }

    private static void EnsureVTable()
    {
        if (vtable != null) return;
        var v = (void**)NativeMemory.AllocZeroed(80, (nuint)sizeof(void*));   // zero slots = functions we do not offer
        v[0] = (delegate* unmanaged<nint, nint, nint, nint, nint, nint>)&Dispatcher;
        v[1] = (delegate* unmanaged<nint, nint, int, int, void>)&OnParamChanged;
        v[2] = (delegate* unmanaged<nint, nint, byte*, void>)&OnHint;
        v[4] = (delegate* unmanaged<nint, nint, void>)&VoiceRelease;
        v[5] = (delegate* unmanaged<nint, nint, int, void>)&VoiceKill;
        v[6] = (delegate* unmanaged<nint, nint, nint, nint, nint, int>)&VoiceProcessEvent;
        v[9] = (delegate* unmanaged<nint, nint, nint, void>)&MidiOutDelayed;
        v[10] = (delegate* unmanaged<nint, nint, nint, void>)&MidiOutDirect;
        vtable = v;
    }

    private static HostSim? Of(nint self)
    {
        lock (Hosts) return Hosts.TryGetValue(self, out var h) ? h : null;
    }

    /// <summary>A new voice, as the mixer creates it for a note-on.</summary>
    public SimVoice NewVoice(int color, float velocity)
    {
        var v = new SimVoice
        {
            Params = (TVoiceParams*)NativeMemory.AllocZeroed((nuint)sizeof(TVoiceParams)),
            Color = color,
            Velocity = velocity,
        };
        lock (gate) { v.Tag = nextTag++; voices[v.Tag] = v; }
        return v;
    }

    public SimVoice? Voice(nint tag) { lock (gate) return voices.TryGetValue(tag, out var v) ? v : null; }

    private void Remove(SimVoice v)
    {
        lock (gate)
        {
            if (v.Killed) return;
            v.Killed = true;
            voices.Remove(v.Tag);
        }
        if (DeferFree) pendingFree.Enqueue((nint)v.Params);
        else NativeMemory.Free(v.Params);
    }

    /// <summary>Frees the levels blocks of voices killed while <see cref="DeferFree"/> was set. Call it where the levels are written (the audio thread).</summary>
    public void FreeDeferred()
    {
        while (pendingFree.TryDequeue(out var p)) NativeMemory.Free((void*)p);
    }

    [UnmanagedCallersOnly]
    private static nint Dispatcher(nint self, nint sender, nint id, nint index, nint value)
    {
        var h = Of(self);
        if (h != null && h.Record) lock (h.DispatcherCalls) h.DispatcherCalls.Add(id);
        if (h != null && (id == Fhd.GetMixingTime || id == Fhd.GetPlaybackTime) && value != 0)
        {
            // FHD_GetMixingTime / FHD_GetPlaybackTime: "Value is a pointer to a TFPTime, which is filled with an optional offset in samples" (fp_plugclass.h): the time at the block's start plus that
            // offset (NaN in, NaN out: what FL 2026 answered the plugin's NaN offset with, every note of the user's session). No song position unless the test set one (FL gave none): the TFPTime
            // is left as the plugin filled it.
            Interlocked.Increment(ref h.mixingTimeCalls);
            double b = h.SongBeats;
            if (!double.IsNaN(b) && (index & Gt.TimeFormatMask) == Gt.Beats) { var t = (TFPTime*)value; t->T = b + t->T / Math.Max(1, h.SampleRate) * h.Tempo / 60.0; t->T2 = 0; }
            return 0;
        }
        if (h != null && id == Fhd.GetContextInfo)
        {
            // FHD_GetContextInfo (relink): the channel's name as FL would give it for CI_TrackName (a UTF-8 string the host owns; 0: none), or whatever raw answer a test set
            Interlocked.Increment(ref h.contextInfoCalls);
            if ((index & 0xFFFF) != Ci.TrackName) return 0;
            return h.RawContextAnswer ?? h.trackNamePtr;
        }
        if (h != null && id == Fhd.EditorResized)
        {
            // FHD_EditorResized: the plugin resized its editor window; the host fits its frame around it again (what FL does)
            Interlocked.Increment(ref h.editorResizedCalls);
            try { h.EditorResized?.Invoke(); } catch (Exception) { /* a host callback must never take the plugin down */ }
        }
        return 0;
    }

    private int editorResizedCalls, contextInfoCalls, mixingTimeCalls;

    /// <summary>Calls of FHD_GetMixingTime / FHD_GetPlaybackTime seen.</summary>
    public int MixingTimeCalls => Volatile.Read(ref mixingTimeCalls);

    /// <summary>The rate and tempo the song position's offset (in samples) is converted at.</summary>
    public int SampleRate = 48000;
    public double Tempo = 120;

    /// <summary>The channel's root note: FL hands a voice's pitch in cents above it (FL 2026, the user's session of 2026-10-18: MIDI note 0 on a channel FL made by importing a MIDI file and the user
    /// replaced by BrokenNes2; C5 = 60 on a channel made in FL, which the FL render certification measured). The plugin is not told which: it finds it (Record/FlNotes.cs).</summary>
    public int RootNote = 60;

    /// <summary>A note-on as FL 2026 makes it (Record/FlNotes.cs): the pitch in cents above the channel's root note (plus a bend in cents FL applies), FPV_GetVelocity = the MIDI velocity / 128,
    /// the level the same (FL's InitLevels.Vol for a note: not measured; the velocity's float is the assumption), the colour = the imported note's MIDI channel - 1.</summary>
    public SimVoice FlNoteOn(int key, int velocity, int colour, double bendCents = 0, double levelScale = 1)
    {
        var v = NewVoice(colour, velocity / 128f);
        ref var f = ref v.Params->FinalLevels;
        f.Pitch = (float)((key - RootNote) * 100 + bendCents);
        f.Vol = (float)(velocity / 128.0 * levelScale);
        f.Pan = 0;
        v.Params->InitLevels = f;
        return v;
    }
    private nint trackNamePtr;
    private string? trackName;

    /// <summary>The channel's name FHD_GetContextInfo answers for CI_TrackName (null: 0, no name). The string is kept in native memory the host owns, as FL's is.</summary>
    public string? TrackName
    {
        get => trackName;
        set
        {
            var old = trackNamePtr;
            trackName = value;
            trackNamePtr = value == null ? 0 : Marshal.StringToCoTaskMemUTF8(value);
            if (old != 0) Marshal.FreeCoTaskMem(old);
        }
    }

    /// <summary>A raw answer to FHD_GetContextInfo instead of the name's pointer (a test of what the plugin does with an answer that is not a pointer to text).</summary>
    public nint? RawContextAnswer { get; set; }

    /// <summary>Calls of FHD_GetContextInfo seen.</summary>
    public int ContextInfoCalls => Volatile.Read(ref contextInfoCalls);

    /// <summary>Calls of FHD_EditorResized seen (the plugin changed the size of its editor window).</summary>
    public int EditorResizedCalls => Volatile.Read(ref editorResizedCalls);

    /// <summary>Raised on the calling (GUI) thread when the plugin reports its editor was resized: the owner of the frame window resizes it.</summary>
    public Action? EditorResized { get; set; }

    [UnmanagedCallersOnly]
    private static void OnParamChanged(nint self, nint sender, int index, int value)
    {
        var h = Of(self);
        if (h != null && h.Record) lock (h.ParamChanges) h.ParamChanges.Add((index, value));
    }

    [UnmanagedCallersOnly]
    private static void OnHint(nint self, nint sender, byte* text) { }

    [UnmanagedCallersOnly]
    private static void VoiceRelease(nint self, nint tag)
    {
        var h = Of(self);
        if (h == null) return;
        h.VoiceReleaseCalls++;
        var v = h.Voice(tag);
        if (v != null && !v.Released) { v.Released = true; h.Plugin?.VoiceRelease(v.Handle); }
    }

    /// <summary>The plugin says a voice is finished: like FL, relay Voice_Kill to it and free the voice.</summary>
    [UnmanagedCallersOnly]
    private static void VoiceKill(nint self, nint tag, int killHandle)
    {
        var h = Of(self);
        if (h == null) return;
        h.VoiceKillCalls++;
        var v = h.Voice(tag);
        if (v == null) return;
        if (killHandle != 0) h.Plugin?.VoiceKill(v.Handle);
        h.Remove(v);
    }

    [UnmanagedCallersOnly]
    private static int VoiceProcessEvent(nint self, nint tag, nint id, nint value, nint flags)
    {
        var v = Of(self)?.Voice(tag);
        if (v == null) return 0;
        return (int)id switch
        {
            Fpv.GetColor => v.Color,
            Fpv.GetVelocity => BitConverter.SingleToInt32Bits(v.Velocity),
            Fpv.GetLength => -1,
            _ => 0,
        };
    }

    /// <summary>The song position (beats) FHD_GetMixingTime answers with; NaN: the host gives none (the plugin's time stays as it was).</summary>
    public double SongBeats = double.NaN;

    /// <summary>MIDIOut_Delayed / MIDIOut calls from the plugin (round 8's live MIDI out): each TMIDIOutMsg (Status | Data1 &lt;&lt; 8 | Data2 &lt;&lt; 16 | Port &lt;&lt; 24) and whether it was the
    /// delayed kind, in the order they came; kept while <see cref="Record"/> is on. <see cref="MidiOutSink"/> gets them too (a live host can route them).</summary>
    public List<(uint Msg, bool Delayed)> MidiOutLog { get; } = new();
    public Action<uint>? MidiOutSink { get; set; }

    [UnmanagedCallersOnly]
    private static void MidiOutDelayed(nint self, nint sender, nint msg) => OnMidiOut(self, msg, true);

    [UnmanagedCallersOnly]
    private static void MidiOutDirect(nint self, nint sender, nint msg) => OnMidiOut(self, msg, false);

    private static void OnMidiOut(nint self, nint msg, bool delayed)
    {
        var h = Of(self);
        if (h == null) return;
        if (h.Record) lock (h.MidiOutLog) h.MidiOutLog.Add(((uint)msg, delayed));
        try { h.MidiOutSink?.Invoke((uint)msg); } catch (Exception) { /* a host callback must never take the plugin down */ }
    }

    /// <summary>The mixer ends a voice itself (note off): Voice_Release to the plugin.</summary>
    public void ReleaseFromMixer(SimVoice v)
    {
        if (v.Released || v.Killed) return;
        v.Released = true;
        Plugin?.VoiceRelease(v.Handle);
    }

    public void Dispose()
    {
        List<SimVoice> all;
        lock (gate) all = voices.Values.ToList();
        foreach (var v in all) Remove(v);
        FreeDeferred();
        lock (Hosts) Hosts.Remove(self);
        NativeMemory.Free((void*)self);
        TrackName = null;
    }
}
