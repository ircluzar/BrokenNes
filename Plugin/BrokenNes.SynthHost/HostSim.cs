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
        return 0;
    }

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
    }
}
