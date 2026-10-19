// Round 9 (capture): an FL-faithful host for "Capture from FL". It plays a scripted FL song (piano-roll notes in FL ticks, velocities, fine pitch, slide notes, per-note pitch automation) into a rack
// of BrokenNes2 instances the way FL Studio does, as far as the user's real FL log (`Documents\BrokenNes2 host log.txt`, FL 26.0.1, 2026-10-18) and the SDK show it:
//
//   creation   the Dispatcher calls of the log, in its order: SetBlockSize 512, SetTimeSig (a pointer to FL's TTimeSigInfo), SetSamplesPerTick (a float's bits) twice, SetNumSends 0, SetPlaying 0,
//              ProcessMode 3072, UseIncreasedMIDIResolution 0, 55, SetEnabled 1, Flush, SetIdleTime 16, SetIOLatency, 31, SetFitTime, PluginLoaded; SetSampleRate first (the log cannot show it:
//              the plugin answers it before logging)
//   Play       per instance SetSamplesPerTick, SongPosChanged 0, SetPlaying 1, SetSamplesPerTick (the log's order at 18:56:40.031); FPE_Tempo through ProcessEvent when asked (the SDK says FL sends
//              it; the log could not show ProcessEvent)
//   ticks      one FL tick (1/PPQ of a beat) at a time: NewTick, the releases and the note-ons of that tick, every sounding voice's FinalLevels for that tick, then Gen_Render of the tick's samples
//              (floor((k+1) x samples per tick) - floor(k x samples per tick)), every instance every tick
//   notes      InitLevels.Pitch = key x 100 + fine, cents from MIDI note 0 (the log: keys 71, 73, 75 arrived as 7100, 7300, 7500); FinalLevels.Pitch = the pitch to play, cents from C5 (what Direct
//              mode reads and real FL certified): (key - 60) x 100 + fine + the slide + the per-note automation; FPV_GetVelocity = velocity / 128 (the log: 0.632 for 81, 0.5 for 64); slide notes do
//              not start voices: the parent voice glides (FL slide notes lie inside the parent note); FPV_GetLength -1; no song position (FHD_GetMixingTime answers nothing, as in the log)
//   Stop       SetPlaying 0 to every instance, then the voices still held are released
//
// What it records is the truth the checks compare a capture with: each note's start and end sample, and the pitch FL put in each sounding voice at every tick.
using System.Runtime.InteropServices;
using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

internal sealed class FlSlide
{
    public int StartTick, LengthTicks;   // from the parent note's start
    public int ToKey;
}

internal sealed class FlNote
{
    /// <summary>The rack index of the instance whose channel holds the note (0 Pulse 1, 1 Pulse 2, 2 Triangle, 3 Noise).</summary>
    public int Inst;
    public int Key, Start, Length;           // FL ticks at the song's PPQ
    /// <summary>FL's piano-roll velocity 0..128 (FL hands the plugin velocity / 128).</summary>
    public int Velocity = 100;
    /// <summary>The note's fine pitch, cents.</summary>
    public int Fine;
    public List<FlSlide>? Slides;
    /// <summary>Per-note pitch automation: (tick from the note's start, cents), linear between points.</summary>
    public List<(int Tick, double Cents)>? Bend;
    public int End => Start + Length;
    public string Label = "";
}

internal sealed class FlSong
{
    public string Name = "";
    public double Bpm = 141.3;
    public int Ppq = 96, StepsPerBar = 16, StepsPerBeat = 4;
    public readonly List<FlNote> Notes = new();
    /// <summary>Where FL is stopped (ticks).</summary>
    public int StopTick;
    /// <summary>The instances' Coarse (semitones) and Fine (cents) parameters.</summary>
    public int[] Coarse = new int[4], Fine = new int[4];

    /// <summary>FinalLevels.Pitch of a note at song tick <paramref name="tick"/> (cents from C5).</summary>
    public static double FinalPitch(FlNote n, double tick)
    {
        double rel = tick - n.Start;
        double key = n.Key;
        if (n.Slides is { Count: > 0 })
        {
            double from = n.Key;
            foreach (var s in n.Slides)
            {
                if (rel < s.StartTick) break;
                double f = s.LengthTicks <= 0 ? 1 : Math.Clamp((rel - s.StartTick) / s.LengthTicks, 0, 1);
                key = from + (s.ToKey - from) * f;
                from = s.ToKey;
            }
        }
        double cents = (key - 60) * 100 + n.Fine;
        if (n.Bend is { Count: > 0 } pts)
        {
            if (rel <= pts[0].Tick) cents += pts[0].Cents;
            else if (rel >= pts[^1].Tick) cents += pts[^1].Cents;
            else
                for (int i = 1; i < pts.Count; i++)
                    if (rel <= pts[i].Tick) { cents += pts[i - 1].Cents + (pts[i].Cents - pts[i - 1].Cents) * (rel - pts[i - 1].Tick) / (pts[i].Tick - pts[i - 1].Tick); break; }
        }
        return cents;
    }

    /// <summary>InitLevels.Pitch of a note (cents from MIDI note 0, as FL hands it over).</summary>
    public static double InitPitch(FlNote n) => n.Key * 100.0 + n.Fine;
}

/// <summary>What FL delivered, for the comparison: per note its first and last sample; per tick and instance, the voice FL would call the newest sounding one and the pitch it had then.</summary>
internal sealed class FlTruth
{
    public readonly Dictionary<FlNote, (long Start, long End)> Samples = new();
    /// <summary>(tick, instance) -> (note, the pitch the instance plays: FinalLevels + Coarse x 100 + Fine).</summary>
    public readonly List<(int Tick, int Inst, FlNote Note, double Played)> Owners = new();
    public long TotalSamples;
}

internal sealed unsafe class FlRun : IDisposable
{
    public int Rate = 48000;
    /// <summary>FL sends FPE_Tempo (ProcessEvent 0) at Play (the SDK's word; the log could not show it).</summary>
    public bool SendTempo = true;
    /// <summary>FL exports (File > Export > WAV): ProcessMode carries PM_IsRendering (16).</summary>
    public bool Rendering;
    /// <summary>Ticks rendered after Stop (FL's mixer runs on).</summary>
    public int AfterStopTicks = 8;
    /// <summary>Rack indexes that get no FPD_SetPlaying 1 (a channel FL does not tell: the edge case of a lane that joins late).</summary>
    public int[] NoPlay = [];
    private readonly int* timeSig = (int*)NativeMemory.AllocZeroed(16);
    public FlTruth Truth = new();
    /// <summary>Called after every tick (the test reads allocation counters, probes registers...).</summary>
    public Action<int>? AfterTick;

    public double SamplesPerTick(FlSong s) => Rate * 60.0 / (s.Bpm * s.Ppq);
    private static nint FloatBits(double v) => (nint)(uint)BitConverter.SingleToInt32Bits((float)v);

    /// <summary>What FL does when it makes an instance (the log's sequence for #2 at 18:53:14).</summary>
    public void Create(PluginInstance p, FlSong s)
    {
        timeSig[0] = s.StepsPerBar; timeSig[1] = s.StepsPerBeat; timeSig[2] = s.Ppq;
        nint spt = FloatBits(SamplesPerTick(s));
        p.Dispatcher(Fpd.SetSampleRate, 0, Rate);
        p.Dispatcher(Fpd.SetBlockSize, 0, 512);
        p.Dispatcher(14, 0, (nint)timeSig);                    // FPD_SetTimeSig
        p.Dispatcher(Fpd.SetSamplesPerTick, 0, spt);
        p.Dispatcher(Fpd.SetSamplesPerTick, 0, spt);
        p.Dispatcher(17, 0, 0);                                // FPD_SetNumSends
        p.Dispatcher(Fpd.SetPlaying, 0, 0);
        p.Dispatcher(1, 0, Rendering ? 3072 | 16 : 3072);      // FPD_ProcessMode
        p.Dispatcher(50, 0, 0);                                // FPD_UseIncreasedMIDIResolution
        p.Dispatcher(55, 0, 14456112);
        p.Dispatcher(Fpd.SetEnabled, 0, 1);
        p.Dispatcher(2, 0, 0);                                 // FPD_Flush
        p.Dispatcher(21, 0, 16);                               // FPD_SetIdleTime
        p.Dispatcher(30, 1070, 1422);                          // FPD_SetIOLatency
        p.Dispatcher(31, 0, 6691736);
        p.Dispatcher(19, 0, 0);                                // FPD_SetFitTime
        p.Dispatcher(Fpd.PluginLoaded, 0, 0);
    }

    /// <summary>FL plays the song from its start into the rack (index = FlNote.Inst) and stops at <see cref="FlSong.StopTick"/>.</summary>
    public void Play(IReadOnlyList<PluginInstance> rack, FlSong s)
    {
        Truth = new FlTruth();
        double spt = SamplesPerTick(s);
        nint sptBits = FloatBits(spt);
        for (int i = 0; i < rack.Count; i++) { rack[i].SetParam(3, s.Coarse[i]); rack[i].SetParam(4, s.Fine[i]); }
        foreach (var p in rack) p.Dispatcher(Fpd.SetSamplesPerTick, 0, sptBits);
        foreach (var p in rack) p.Dispatcher(13, 0, 0);                                     // FPD_SongPosChanged
        for (int i = 0; i < rack.Count; i++) if (!NoPlay.Contains(i)) rack[i].Dispatcher(Fpd.SetPlaying, 0, 1);
        foreach (var p in rack) p.Dispatcher(Fpd.SetSamplesPerTick, 0, sptBits);
        if (SendTempo) foreach (var p in rack) p.ProcessEvent(Fpe.Tempo, BitConverter.SingleToInt32Bits((float)s.Bpm), (int)Math.Round(spt));
        var buf = new float[8192 * 2];
        var active = new List<(FlNote N, SimVoice V, PluginInstance P)>();
        var byStart = s.Notes.OrderBy(n => n.Start).ToList();
        int next = 0;
        long sample = 0;
        int last = s.StopTick + AfterStopTicks;
        fixed (float* b = buf)
        {
            for (int k = 0; k < last; k++)
            {
                if (k == s.StopTick)
                {
                    foreach (var p in rack) p.Dispatcher(Fpd.SetPlaying, 0, 0);
                    foreach (var (n, v, p) in active) { p.Host.ReleaseFromMixer(v); Truth.Samples[n] = (Truth.Samples[n].Start, sample); }
                    active.Clear();
                }
                foreach (var p in rack) p.NewTick();
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var (n, v, p) = active[i];
                    if (n.End > k) continue;
                    p.Host.ReleaseFromMixer(v);
                    Truth.Samples[n] = (Truth.Samples[n].Start, sample);
                    active.RemoveAt(i);
                }
                while (k < s.StopTick && next < byStart.Count && byStart[next].Start <= k)
                {
                    var n = byStart[next++];
                    var p = rack[n.Inst];
                    var v = p.Host.NewVoice(n.Inst, n.Velocity / 128f);
                    v.Params->InitLevels.Pitch = (float)FlSong.InitPitch(n);
                    v.Params->InitLevels.Vol = n.Velocity / 128f;
                    v.Params->FinalLevels.Pitch = (float)FlSong.FinalPitch(n, k);
                    v.Params->FinalLevels.Vol = n.Velocity / 128f;
                    v.Handle = p.TriggerVoice(v.Params, v.Tag);
                    active.Add((n, v, p));
                    Truth.Samples[n] = (sample, -1);
                }
                foreach (var (n, v, p) in active) v.Params->FinalLevels.Pitch = (float)FlSong.FinalPitch(n, k);
                if (k < s.StopTick)
                    for (int inst = 0; inst < rack.Count; inst++)
                    {
                        // the newest sounding note of each instance (BrokenNes2's channel plays it)
                        FlNote? owner = null; float pitch = 0;
                        foreach (var (n, v, p) in active) if (n.Inst == inst && (owner == null || n.Start >= owner.Start)) { owner = n; pitch = v.Params->FinalLevels.Pitch; }
                        if (owner != null) Truth.Owners.Add((k, inst, owner, pitch + s.Coarse[inst] * 100 + s.Fine[inst]));
                    }
                int len = (int)(Math.Floor((k + 1) * spt) - Math.Floor(k * spt));
                foreach (var p in rack) p.GenRender(b, len);
                sample += len;
                AfterTick?.Invoke(k);
            }
        }
        foreach (var (n, v, p) in active) p.Host.ReleaseFromMixer(v);
        Truth.TotalSamples = sample;
    }

    public void Dispose() => NativeMemory.Free(timeSig);
}
