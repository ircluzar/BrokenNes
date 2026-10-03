// A rack of BrokenNes2 plugin instances played live: what FL's mixer does for a project, driven by note events instead of a piano roll.
using System.Collections.Concurrent;
using BrokenNes.Fruity;
using BrokenNes.FruityHost;

namespace BrokenNes.SynthHost;

public enum SynthEventKind : byte { NoteOn, NoteOff, PitchBend, AllNotesOff }

/// <summary>One thing the player did. <see cref="Channel"/> is the rack channel (0 Pulse 1 ... 3 Noise), -1 for "all" in AllNotesOff.
/// <see cref="Value"/> is the velocity 0..1 of a NoteOn, or the bend -1..1 of a PitchBend.</summary>
public readonly record struct SynthEvent(SynthEventKind Kind, int Channel, int Key, float Value);

/// <summary>
/// The standalone synth's engine. BrokenNes2 is one instance per NES channel, so the rack is four instances (Pulse 1, Pulse 2, Triangle, Noise)
/// that share one emulator, each a voice of its own; a note goes to the instance of its channel and the instances' outputs are summed.
/// <para>
/// <b>Threads.</b> Events may be queued from any thread (MIDI callbacks, the keyboard, the UI); everything that touches a voice happens in
/// <see cref="Render"/>, on the audio thread, once per block of at most <see cref="MaxBlock"/> frames, as FL's mixer does: events, voice levels
/// (pitch in cents from C5 = MIDI 60, volume, pan), each plugin's NewTick (which is when it hands finished voices back), then Gen_Render.
/// Rendering allocates nothing; a note-on allocates its voice.
/// </para>
/// <para>
/// <b>Shutdown:</b> Render and Dispose exclude each other, so a device thread still rendering when the window closes cannot touch destroyed plugin
/// instances; stop the audio first all the same. The editors and state calls belong to the UI thread, like in FL.
/// </para>
/// </summary>
public sealed unsafe class SynthRack : IDisposable
{
    public const int ChannelCount = 4;
    public static readonly string[] ChannelNames = ["Pulse 1", "Pulse 2", "Triangle", "Noise"];
    public const int MaxBlock = 512;

    private const int ParamChannel = 6;   // the plugin's "Channel" parameter (Bn2Params.P.Channel): 0 is Auto, n is channel n-1

    private readonly PluginInstance[] instances = new PluginInstance[ChannelCount];
    private readonly HostSim[] hosts = new HostSim[ChannelCount];
    private readonly Dictionary<int, SimVoice>[] held = new Dictionary<int, SimVoice>[ChannelCount];   // key -> the voice sounding while the key is down
    private readonly float[] bendCents = new float[ChannelCount];
    private readonly List<int> dead = new();
    private readonly ConcurrentQueue<SynthEvent> queue = new();
    private readonly float[] scratch = new float[MaxBlock * 2];
    private volatile float peak;
    private readonly object renderGate = new();
    private bool disposed;   // read and written under renderGate

    public int SampleRate { get; private set; }

    /// <summary>Tells every plugin the rate the audio device runs at. Stop rendering first (a device change is what calls for it).</summary>
    public void SetSampleRate(int sampleRate)
    {
        SampleRate = sampleRate;
        foreach (var p in instances) p.Dispatcher(Fpd.SetSampleRate, 0, sampleRate);
    }

    /// <summary>How far a full pitch bend goes, in semitones (the MIDI default is 2).</summary>
    public float BendRangeSemitones { get; set; } = 2;

    /// <summary>Gain on the summed output. Four full-level channels add up to more than a device takes, and the output is clamped to +-1 after it.</summary>
    public float MasterGain { get; set; } = 0.8f;

    /// <summary>The four plugin instances, one per channel: their editors, parameters and saved state are the plugin's own.</summary>
    public IReadOnlyList<PluginInstance> Instances => instances;

    /// <summary>Voices alive in the plugins (held, or released and still fading).</summary>
    public int LiveVoices => hosts.Sum(h => h.LiveVoices);

    /// <summary>Loads the plugin DLL and creates the four instances at <paramref name="sampleRate"/>.</summary>
    public SynthRack(string pluginDll, int sampleRate)
    {
        SampleRate = sampleRate;
        var lib = new PluginLibrary(pluginDll);
        for (int i = 0; i < ChannelCount; i++)
        {
            held[i] = new Dictionary<int, SimVoice>();
            hosts[i] = new HostSim { Record = false, DeferFree = true };
            var p = lib.Create(hosts[i], (nint)(0x200 + i));
            instances[i] = p;
            p.RackChannel = i;
            p.SetParam(ParamChannel, i + 1);                  // Auto emulator: the four instances fill one emulator, a channel each
            p.Dispatcher(Fpd.SetSampleRate, 0, sampleRate);
        }
    }

    // ---- what the player does (any thread) ----

    /// <summary>Whether events are queued at all. A host with no audio device running turns this off: nothing would ever drain the queue, and a burst of
    /// stale notes must not sound the moment a device appears. (Events already queued are still played by the next <see cref="Render"/>.)</summary>
    public volatile bool Accepting = true;

    public void NoteOn(int channel, int key, float velocity) { if (Accepting && InRange(channel)) queue.Enqueue(new(SynthEventKind.NoteOn, channel, key, Math.Clamp(velocity, 0f, 1f))); }
    public void NoteOff(int channel, int key) { if (Accepting && InRange(channel)) queue.Enqueue(new(SynthEventKind.NoteOff, channel, key, 0)); }
    /// <summary>-1..1, full scale = <see cref="BendRangeSemitones"/>.</summary>
    public void PitchBend(int channel, float bend) { if (Accepting && InRange(channel)) queue.Enqueue(new(SynthEventKind.PitchBend, channel, 0, Math.Clamp(bend, -1f, 1f))); }
    /// <summary>Releases every held note of a channel, or of all of them with -1. Always accepted: ending notes is never wrong.</summary>
    public void AllNotesOff(int channel = -1) { if (channel == -1 || InRange(channel)) queue.Enqueue(new(SynthEventKind.AllNotesOff, channel, 0, 0)); }

    private static bool InRange(int channel) => (uint)channel < ChannelCount;

    // ---- audio thread ----

    /// <summary>Fills <paramref name="stereo"/> (interleaved L, R) with the next stretch of sound. Any length; works in blocks of at most <see cref="MaxBlock"/> frames.</summary>
    public void Render(Span<float> stereo)
    {
        // Dispose takes this same lock: a device thread that is still inside Render when the window closes finishes its block first, and any
        // later call gets silence. (The lock is uncontended in play; Monitor does not allocate.)
        lock (renderGate)
        {
            if (disposed) { stereo.Clear(); return; }
            RenderLocked(stereo);
        }
    }

    private void RenderLocked(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        fixed (float* basePtr = stereo)
        fixed (float* tmp = scratch)
        {
            for (int done = 0; done < frames;)
            {
                int n = Math.Min(MaxBlock, frames - done);
                foreach (var h in hosts) h.FreeDeferred();
                DrainEvents();
                UpdateLevels();
                float* dst = basePtr + done * 2;
                for (int k = 0; k < ChannelCount; k++)
                {
                    instances[k].NewTick();
                    if (k == 0) instances[0].GenRender(dst, n);
                    else
                    {
                        instances[k].GenRender(tmp, n);
                        for (int i = 0; i < n * 2; i++) dst[i] += tmp[i];
                    }
                }
                done += n;
            }
            float pk = 0, g = MasterGain;
            for (int i = 0; i < frames * 2; i++)
            {
                float s = Math.Clamp(basePtr[i] * g, -1f, 1f);
                basePtr[i] = s;
                float a = Math.Abs(s);
                if (a > pk) pk = a;
            }
            if (pk > peak) peak = pk;
        }
    }

    /// <summary>The largest output sample since the last call (0..1), for a level meter. Cleared by reading.</summary>
    public float ReadPeak() { float p = peak; peak = 0; return p; }

    private void DrainEvents()
    {
        while (queue.TryDequeue(out var e))
        {
            switch (e.Kind)
            {
                case SynthEventKind.NoteOn: NoteOnNow(e.Channel, e.Key, e.Value); break;
                case SynthEventKind.NoteOff: NoteOffNow(e.Channel, e.Key); break;
                case SynthEventKind.PitchBend: bendCents[e.Channel] = e.Value * BendRangeSemitones * 100f; break;
                case SynthEventKind.AllNotesOff:
                    for (int c = 0; c < ChannelCount; c++) if (e.Channel == -1 || e.Channel == c) ReleaseAll(c);
                    break;
            }
        }
    }

    private void NoteOnNow(int ch, int key, float velocity)
    {
        if (held[ch].Remove(key, out var again)) hosts[ch].ReleaseFromMixer(again);   // the same key struck again while down: the old note ends
        var v = hosts[ch].NewVoice(0, velocity);
        SetLevels(v, key, ch);
        v.Handle = instances[ch].TriggerVoice(v.Params, v.Tag);
        held[ch][key] = v;
    }

    private void NoteOffNow(int ch, int key)
    {
        if (held[ch].Remove(key, out var v)) hosts[ch].ReleaseFromMixer(v);
    }

    private void ReleaseAll(int ch)
    {
        foreach (var v in held[ch].Values) hosts[ch].ReleaseFromMixer(v);
        held[ch].Clear();
    }

    /// <summary>The live levels FL's mixer rewrites every block; the plugin re-reads them every 64 samples, so a bend is smooth.</summary>
    private void UpdateLevels()
    {
        for (int ch = 0; ch < ChannelCount; ch++)
        {
            var map = held[ch];
            if (map.Count == 0) continue;
            dead.Clear();
            foreach (var (key, v) in map)
            {
                if (v.Killed) { dead.Add(key); continue; }   // the plugin handed it back already (Reset Console, a refused channel)
                SetLevels(v, key, ch);
            }
            foreach (int key in dead) map.Remove(key);
        }
    }

    private void SetLevels(SimVoice v, int key, int ch)
    {
        ref var f = ref v.Params->FinalLevels;
        f.Pitch = (key - 60) * 100f + bendCents[ch];   // cents from C5 = MIDI 60: key 69 is 440 Hz
        f.Vol = v.Velocity;
        f.Pan = 0;
        v.Params->InitLevels = f;
    }

    // ---- the editors and saved settings (UI thread, as in FL) ----

    public void ShowEditor(int channel, nint parentWindow) => instances[channel].ShowEditor(parentWindow);
    public void HideEditor(int channel) => instances[channel].HideEditor();
    public nint EditorWindow(int channel) => instances[channel].EditorHandle;

    /// <summary>Each instance's saved state (what FL stores in a project): sound chip, mode, game, volume... ready for <see cref="LoadStates"/>.</summary>
    public byte[][] SaveStates() => instances.Select(i => i.SaveState()).ToArray();

    /// <summary>Restores states saved by <see cref="SaveStates"/>. The plugin survives foreign or damaged state (it loads defaults).</summary>
    public void LoadStates(byte[][] states)
    {
        for (int i = 0; i < Math.Min(states.Length, ChannelCount); i++)
            if (states[i] is { Length: > 0 }) instances[i].LoadState(states[i]);
    }

    public void Dispose()
    {
        lock (renderGate)   // waits for a Render in progress
        {
            if (disposed) return;
            disposed = true;
            foreach (var p in instances) p.Destroy();   // ends their voices (Voice_Kill) while the hosts are still there to answer
            foreach (var h in hosts) h.Dispose();
        }
    }
}
