using System;
using System.Collections.Generic;
using System.Text.Json;

namespace NesEmulator.Gb;

// =====================================================================================================================
//  BrokenNes Nano GB's instrument engine, in C#: notes -> Game Boy sound registers -> gnuboy's sound (GnubSound) -> samples.
//
//  A line-for-line twin of Nano/gb/src/engine.js (the web library). The BrokenNes2 plugin's Nano GB chip plays through this class, so a song
//  composed in FL and played by the web library is the same notes on the same chip with the same presets. Nano/gb/test/parity.mjs runs both on
//  the same scripts and compares the register writes and the samples (tools: Nano/gb/tools/NanoGbParity). Change one, change both.
//  The arithmetic is plain doubles in the JS order of operations, no Math.Pow / Log / Sin (Exp2 is the same polynomial), so the bits match.
//  Nothing allocates once constructed (voices and events are pooled): the plugin calls it on the audio thread.
// =====================================================================================================================

/// <summary>A normalized Nano GB preset (the fields of engine.js's normalizePreset; the built-ins come from <see cref="NanoGbPresets"/>).</summary>
public sealed class NanoGbPreset
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Desc { get; set; } = "";
    public int Kind { get; set; }
    public double Note { get; set; } = 60;
    public int Duty { get; set; } = 2;
    public int[]? DutySteps { get; set; }
    public double DutyRate { get; set; }
    public double Gain { get; set; } = 1;
    public double A { get; set; }
    public double D { get; set; }
    public double S { get; set; } = 1;
    public double R { get; set; }
    public bool OneShot { get; set; }
    public int HwEnv { get; set; } = -1;
    public double VibRate { get; set; }
    public double VibDepth { get; set; }
    public double VibDelay { get; set; }
    public double VibFade { get; set; }
    public double SlideFrom { get; set; }
    public double SlideTo { get; set; }
    public double SlideMs { get; set; }
    public double[]? ArpSteps { get; set; }
    public double ArpRate { get; set; }
    public bool ArpOnce { get; set; }
    public double NoiseShift { get; set; } = -1;
    public double NoiseTo { get; set; }
    public double NoiseMs { get; set; }
    public int NoiseRatio { get; set; }
    public bool NoiseShort { get; set; }
    public int Sweep { get; set; } = -1;
    public int[]? Wave { get; set; }
    public string WaveKey { get; set; } = "";
    public int Pan { get; set; }
}

/// <summary>The built-in presets in program order (generated from Nano/gb/src/presets.js into <c>NanoGbPresets.Json</c> by Nano/gb/build.mjs).</summary>
public static partial class NanoGbPresets
{
    private sealed class Table { public NanoGbPreset[] Presets { get; set; } = []; public Dictionary<string, string> Drums { get; set; } = new(); }
    private static readonly Table table = JsonSerializer.Deserialize<Table>(Json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    /// <summary>Program number n = All[n] (the web library's PRESETS order).</summary>
    public static IReadOnlyList<NanoGbPreset> All => table.Presets;
    public static int Count => table.Presets.Length;
    public static NanoGbPreset Program(int n) => table.Presets[((n % Count) + Count) % Count];
    public static int IndexOf(string name) { for (int i = 0; i < table.Presets.Length; i++) if (table.Presets[i].Name == name) return i; return -1; }
    /// <summary>General MIDI drum note (channel 10) -> preset, or null.</summary>
    public static NanoGbPreset? Drum(int note) => table.Drums.TryGetValue(note.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n) ? table.Presets[IndexOf(n)] : null;
}

public sealed class NanoGbEngine
{
    public const int Chunk = 64;
    private const int MaxStack = 8;
    private static readonly int[] Reg1 = { 0x11, 0x16, 0x1B, 0x20 }, Reg2 = { 0x12, 0x17, 0x1C, 0x21 }, Reg3 = { 0x13, 0x18, 0x1D, 0x22 }, Reg4 = { 0x14, 0x19, 0x1E, 0x23 };
    private static readonly int[] FreqTab = { 32768, 16384, 8192, 5461, 4096, 3276, 2730, 2340 };

    private sealed class Voice
    {
        public int Id, Channel, Duty, Pan, TrigLevel;
        public NanoGbPreset P = null!;
        public double Note, Vel, RelLevel, Bend;
        public long Start, ReleasedAt;
        public bool Fresh, Released, Dead;
    }

    private enum T { On, Off, OffNote, Bend, AllOff, Kill, Volume, PitchBend, Tune, Pan }
    private struct Ev
    {
        public long Frame; public T Type; public int Id; public double Note, Vel, Value; public NanoGbPreset? Preset;
        public int Channel, Duty, Pan, P0, P1, P2, P3; public double BendCents, Duration;
    }

    public readonly GnubSound Chip;
    public readonly int SampleRate;
    private readonly double k;
    public double Volume = 1;
    public long Frame;
    private long nextTick;
    private readonly List<Ev> events = new(512);
    private readonly List<Voice>[] stacks = { new(MaxStack + 2), new(MaxStack + 2), new(MaxStack + 2), new(MaxStack + 2) };
    private readonly bool[] gate = new bool[4];
    public readonly int[] CurLevel = new int[4], CurNote = { -1, -1, -1, -1 };
    private readonly Voice?[] lastVoice = new Voice?[4];
    private string loadedWave = "";
    public readonly int[] ChanPan = new int[4];
    private double pitchBend, tune;
    private readonly Dictionary<int, Voice> voices = new(64);
    private readonly Stack<Voice> pool = new(64);
    private readonly Voice?[] temp = new Voice?[MaxStack + 2];
    private readonly int[] noiseCode = new int[112], noiseFreq = new int[112];
    /// <summary>The register writes and sample counts, as engine.js's trace ([0, reg, value] / [1, n]); null unless asked for.</summary>
    public readonly List<int[]>? Trace;
    private float[] outL = [];
    private float[]? outR;
    private int outAt;

    public NanoGbEngine(int sampleRate, bool trace = false)
    {
        SampleRate = sampleRate;
        Chip = new GnubSound(false, sampleRate);
        Chip.sound_reset();
        Chip.Output = Put;
        k = (double)sampleRate * Chip.RATE / 2097152;
        Trace = trace ? new List<int[]>() : null;
        for (int i = 0; i < 64; i++) pool.Push(new Voice());
        for (int sh = 0, n = 0; sh < 14; sh++) for (int ra = 0; ra < 8; ra++, n++)
        {
            int f = (FreqTab[ra] >> sh) * Chip.RATE; if ((f >> 18) != 0) f = 1 << 18;
            noiseCode[n] = (sh << 4) | ra; noiseFreq[n] = f;
        }
        Wf(0x25, 0xFF);
    }

    private void Put(int l, int r)
    {
        if (outR != null) { outL[outAt] = l / 128f; outR[outAt] = r / 128f; }
        else outL[outAt] = (l + r) / 256f;
        outAt++;
    }

    private void W(int reg, int value) { if (Chip.Hi[reg] != value) Wf(reg, value); }
    private void Wf(int reg, int value) { Chip.sound_write(reg, (byte)value); Trace?.Add([0, reg, value]); }

    // ---- events ----
    private void Schedule(long frame, Ev ev)
    {
        ev.Frame = frame;
        int i = events.Count;
        while (i > 0 && events[i - 1].Frame > frame) i--;
        events.Insert(i, ev);
    }
    /// <param name="channel">-1: the engine picks; duty -1: the preset's; pan 2: the preset's / the channel's.</param>
    public void NoteOn(long frame, int id, double note, double vel, NanoGbPreset preset, int channel = -1, int duty = -1, int pan = 2, double bend = 0, double duration = 0)
        => Schedule(frame, new Ev { Type = T.On, Id = id, Note = note, Vel = vel, Preset = preset, Channel = channel, Duty = duty, Pan = pan, BendCents = bend, Duration = duration });
    public void NoteOff(long frame, int id) => Schedule(frame, new Ev { Type = T.Off, Id = id });
    public void NoteOffByNote(long frame, double note) => Schedule(frame, new Ev { Type = T.OffNote, Note = note });
    public void BendVoice(long frame, int id, double cents) => Schedule(frame, new Ev { Type = T.Bend, Id = id, Value = cents });
    public void AllOff(long frame) => Schedule(frame, new Ev { Type = T.AllOff });
    public void Kill(long frame) => Schedule(frame, new Ev { Type = T.Kill });
    public void SetVolume(long frame, double v) => Schedule(frame, new Ev { Type = T.Volume, Value = v });
    public void SetPitchBend(long frame, double cents) => Schedule(frame, new Ev { Type = T.PitchBend, Value = cents });
    public void SetTune(long frame, double cents) => Schedule(frame, new Ev { Type = T.Tune, Value = cents });
    /// <summary>The channels' own pans (-1 left, 0 both, 1 right) for pulse 1, pulse 2, wave, noise: engine.js's setParam('pan').</summary>
    public void SetPan(long frame, int p0, int p1, int p2, int p3) => Schedule(frame, new Ev { Type = T.Pan, P0 = p0, P1 = p1, P2 = p2, P3 = p3 });
    public bool IsSounding(int id) => voices.TryGetValue(id, out var v) && !v.Dead;
    public int SoundingChannels { get { int n = 0; for (int c = 0; c < 4; c++) if (gate[c]) n++; return n; } }

    private Voice? FindByNote(double note)
    {
        Voice? best = null;
        for (int c = 0; c < 4; c++) foreach (var v in stacks[c]) if (!v.Dead && v.Note == note && !v.Released && (best == null || v.Start >= best.Start)) best = v;
        return best;
    }
    private Voice? FindById(int id) => voices.TryGetValue(id, out var v) ? v : null;

    private void Apply(in Ev ev)
    {
        switch (ev.Type)
        {
            case T.On: DoNoteOn(ev); break;
            case T.Off: { var v = FindById(ev.Id); if (v != null) Release(v); break; }
            case T.OffNote: { var v = FindByNote(ev.Note); if (v != null) Release(v); break; }
            case T.Bend: { var v = FindById(ev.Id); if (v != null) v.Bend = ev.Value; break; }
            case T.AllOff:
                for (int c = 0; c < 4; c++)
                {
                    int n = stacks[c].Count;
                    for (int i = 0; i < n; i++) temp[i] = stacks[c][i];
                    for (int i = 0; i < n; i++) { Release(temp[i]); temp[i] = null; }
                }
                break;
            case T.Kill:
                for (int c = 0; c < 4; c++) { foreach (var v in stacks[c]) { v.Dead = true; Forget(v); if (!ReferenceEquals(lastVoice[c], v)) pool.Push(v); } stacks[c].Clear(); }
                for (int c = 0; c < 4; c++) ApplyChannel(c);
                break;
            case T.Volume: Volume = ev.Value; break;
            case T.PitchBend: pitchBend = ev.Value; break;
            case T.Tune: tune = ev.Value; break;
            case T.Pan: ChanPan[0] = ev.P0; ChanPan[1] = ev.P1; ChanPan[2] = ev.P2; ChanPan[3] = ev.P3; break;
        }
    }

    private void Forget(Voice v)
    {
        if (voices.TryGetValue(v.Id, out var have) && ReferenceEquals(have, v)) voices.Remove(v.Id);
    }

    private void DoNoteOn(in Ev ev)
    {
        var p = ev.Preset!;
        int ch = PickChannel(p, ev.Channel);
        var v = pool.Count > 0 ? pool.Pop() : new Voice();
        v.Id = ev.Id; v.P = p; v.Note = ev.Note; v.Vel = ev.Vel; v.Start = Frame; v.Channel = ch;
        v.Fresh = true; v.Released = false; v.ReleasedAt = 0; v.RelLevel = 0; v.Dead = false; v.Bend = 0; v.Duty = -1; v.Pan = 2; v.TrigLevel = -1;
        if (ev.Duty >= 0) v.Duty = ev.Duty;
        if (ev.Pan != 2) v.Pan = ev.Pan;
        if (ev.BendCents != 0) v.Bend = ev.BendCents;
        var st = stacks[ch];
        st.Add(v);
        if (st.Count > MaxStack) { var old = st[0]; st.RemoveAt(0); old.Dead = true; Forget(old); pool.Push(old); }
        if (voices.TryGetValue(v.Id, out var prev)) prev.Dead = true;
        voices[v.Id] = v;
        ApplyChannel(ch);
        if (ev.Duration > 0) Schedule(Frame + (long)Math.Floor(ev.Duration * SampleRate + 0.5), new Ev { Type = T.Off, Id = v.Id });
    }

    private int PickChannel(NanoGbPreset p, int pin)
    {
        int c0, c1;
        if (p.Sweep >= 0) { c0 = 0; c1 = 0; } else if (p.Kind == 0) { c0 = 0; c1 = 1; } else if (p.Kind == 1) { c0 = c1 = 2; } else { c0 = c1 = 3; }
        if (pin >= 0)
        {
            if (pin < c0 || pin > c1) throw new ArgumentOutOfRangeException(nameof(pin), $"this preset cannot play on channel {pin}");
            c0 = c1 = pin;
        }
        for (int c = c0; c <= c1; c++) Prune(c);
        for (int c = c0; c <= c1; c++) if (stacks[c].Count == 0) return c;
        int best = -1; double bestAt = double.PositiveInfinity;
        for (int c = c0; c <= c1; c++) { var top = stacks[c][^1]; if (top.Released && top.ReleasedAt < bestAt) { best = c; bestAt = top.ReleasedAt; } }
        if (best >= 0) return best;
        best = c0; bestAt = double.PositiveInfinity;
        for (int c = c0; c <= c1; c++) { var top = stacks[c][^1]; if (top.Start < bestAt) { best = c; bestAt = top.Start; } }
        return best;
    }

    private void Prune(int c)
    {
        var st = stacks[c];
        for (int i = st.Count - 1; i >= 0; i--) if (st[i].Dead) { var v = st[i]; Forget(v); st.RemoveAt(i); if (!ReferenceEquals(lastVoice[c], v)) pool.Push(v); }
    }

    private double Tms(Voice v, long frame) => (double)(frame - v.Start) * 1000 / SampleRate;

    private double Env(Voice v, long frame)
    {
        var p = v.P;
        if (v.Released)
        {
            if (p.R <= 0) return 0;
            double rt = (double)(frame - v.ReleasedAt) * 1000 / SampleRate;
            return v.RelLevel * Math.Max(0, 1 - rt / p.R);
        }
        double t = Tms(v, frame);
        if (t < p.A) return t / p.A;
        double td = t - p.A;
        if (p.D > 0 && td < p.D) return 1 - (1 - p.S) * td / p.D;
        return p.S;
    }

    private void Release(Voice v)
    {
        if (v.Released || v.Dead || v.P.OneShot) return;
        v.RelLevel = Env(v, Frame);
        v.Released = true; v.ReleasedAt = Frame;
        if (v.P.R <= 0 || v.P.HwEnv >= 0) { v.Dead = true; Forget(v); }
        ApplyChannel(v.Channel);
    }

    private bool Finished(Voice v, long frame)
    {
        var p = v.P;
        if (v.Dead) return true;
        if (v.Released) return p.R <= 0 || (double)(frame - v.ReleasedAt) * 1000 / SampleRate >= p.R;
        return p.S == 0 && Tms(v, frame) >= p.A + p.D;
    }

    private double VoiceCents(Voice v, double t)
    {
        var p = v.P;
        double c = v.Note * 100 + v.Bend + pitchBend + tune;
        if (p.SlideMs > 0 || p.SlideFrom != 0 || p.SlideTo != 0)
        {
            double f = p.SlideMs > 0 ? Math.Min(1, t / p.SlideMs) : 1;
            c += p.SlideFrom + (p.SlideTo - p.SlideFrom) * f;
        }
        if (p.ArpSteps != null)
        {
            int i = (int)Math.Floor(t / 1000 * p.ArpRate);
            i = p.ArpOnce ? Math.Min(i, p.ArpSteps.Length - 1) : i % p.ArpSteps.Length;
            c += p.ArpSteps[i] * 100;
        }
        if (p.VibDepth > 0 && p.VibRate > 0)
        {
            double vt = t - p.VibDelay;
            if (vt > 0)
            {
                double amp = p.VibDepth * (p.VibFade > 0 ? Math.Min(1, vt / p.VibFade) : 1);
                double ph = vt / 1000 * p.VibRate;
                ph -= Math.Floor(ph);
                c += amp * (ph < 0.25 ? 4 * ph : ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4);
            }
        }
        return c;
    }

    public static double RoundEven(double x)
    {
        double f = Math.Floor(x), d = x - f;
        if (d < 0.5) return f;
        if (d > 0.5) return f + 1;
        return f % 2 == 0 ? f : f + 1;
    }
    private static int Clamp(double x, int lo, int hi) => x < lo ? lo : x > hi ? hi : (int)x;
    public static double Exp2(double x)
    {
        double i = Math.Floor(x);
        double f = (x - i) * 0.6931471805599453;
        double t = 1, s = 1;
        for (int k = 1; k <= 12; k++) { t = t * f / k; s = s + t; }
        while (i > 0) { s = s * 2; i--; }
        while (i < 0) { s = s * 0.5; i++; }
        return s;
    }
    public static double MidiHz(double cents) => 440 * Exp2((cents - 6900) / 1200);

    private void ApplyChannel(int ch)
    {
        Prune(ch);
        var st = stacks[ch];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null)
        {
            if (gate[ch]) { Silence(ch); gate[ch] = false; CurLevel[ch] = 0; CurNote[ch] = -1; }
            if (lastVoice[ch] is { Dead: true } lv0) pool.Push(lv0);
            lastVoice[ch] = null;
            return;
        }
        var p = v.P;
        double t = Tms(v, Frame);
        bool trig = v.Fresh || !gate[ch] || !ReferenceEquals(lastVoice[ch], v);
        v.Fresh = false;
        if (lastVoice[ch] is { Dead: true } lv && !ReferenceEquals(lv, v)) pool.Push(lv);
        lastVoice[ch] = v;
        int level;
        if (p.HwEnv >= 0) { if (trig) v.TrigLevel = Clamp(RoundEven(15 * v.Vel * p.Gain), 0, 15); level = v.TrigLevel; }
        else level = Clamp(RoundEven(15 * (v.Vel * p.Gain * Env(v, Frame))), 0, 15);
        int pan = v.Pan != 2 ? v.Pan : p.Pan != 0 ? p.Pan : ChanPan[ch];
        int r1 = Reg1[ch], r2 = Reg2[ch], r3 = Reg3[ch], r4 = Reg4[ch];
        if (ch == 3)
        {
            int code;
            if (p.NoiseShift >= 0)
            {
                double f = p.NoiseMs > 0 ? Math.Min(1, t / p.NoiseMs) : 1;
                code = (Clamp(Math.Floor(p.NoiseShift + (p.NoiseTo - p.NoiseShift) * f), 0, 13) << 4) | p.NoiseRatio;
            }
            else code = NoiseCode(VoiceCents(v, t));
            W(r1, 0);
            W(r2, p.HwEnv >= 0 ? (level << 4) | p.HwEnv : level << 4);
            W(r3, code | (p.NoiseShort ? 8 : 0));
            if (trig) Wf(r4, 0x80);
        }
        else
        {
            double cents = p.Sweep >= 0 ? v.Note * 100 : VoiceCents(v, t);
            double f = MidiHz(cents);
            int d = Clamp(RoundEven((ch == 2 ? 65536 : 131072) * k / f), 1, 2048);
            int x = 2048 - d, lo = x & 0xFF, hi = x >> 8;
            if (ch == 2)
            {
                if (trig || loadedWave != p.WaveKey)
                {
                    if (loadedWave != p.WaveKey)
                    {
                        Wf(0x1A, 0);
                        for (int i = 0; i < 16; i++) Wf(0x30 + i, (p.Wave![2 * i] << 4) | p.Wave[2 * i + 1]);
                        loadedWave = p.WaveKey;
                    }
                    trig = true;
                    W(0x1A, 0x80);
                }
                W(r1, 0);
                W(r2, (level == 0 ? 0 : level < 6 ? 3 : level < 12 ? 2 : 1) << 5);
            }
            else
            {
                int duty = p.DutySteps != null ? p.DutySteps[(int)(Math.Floor(t / 1000 * p.DutyRate) % p.DutySteps.Length)] : (v.Duty >= 0 ? v.Duty : p.Duty);
                W(r1, duty << 6);
                W(r2, p.HwEnv >= 0 ? (level << 4) | p.HwEnv : level << 4);
                if (ch == 0) W(0x10, p.Sweep >= 0 ? p.Sweep : 0);
            }
            if (trig) { W(r3, lo); Wf(r4, 0x80 | hi); }
            else if (p.Sweep < 0) { W(r3, lo); if ((Chip.Hi[r4] & 0x47) != hi) Wf(r4, hi); }
        }
        int bits = pan < 0 ? 0x10 << ch : pan > 0 ? 1 << ch : 0x11 << ch;
        W(0x25, (Chip.Hi[0x25] & ~(0x11 << ch)) | bits);
        gate[ch] = true; CurLevel[ch] = level; CurNote[ch] = (int)v.Note;
    }

    private int NoiseCode(double cents)
    {
        double target = 16 * MidiHz(cents) * 131072 / SampleRate;
        int best = 0; double bq = double.PositiveInfinity;
        for (int n = 0; n < 112; n++) { int f = noiseFreq[n]; if (f <= 0) continue; double q = f > target ? f / target : target / f; if (q < bq) { bq = q; best = noiseCode[n]; } }
        return best;
    }

    private void Silence(int ch)
    {
        if (ch == 2) { W(0x1A, 0); return; }
        W(Reg2[ch], 0);
        W(Reg1[ch], (Chip.Hi[Reg1[ch]] & 0xC0) | 63);
        Wf(Reg4[ch], 0x40 | (Chip.Hi[Reg4[ch]] & 7));
    }

    private void Tick()
    {
        for (int c = 0; c < 4; c++)
        {
            var st = stacks[c];
            for (int i = st.Count - 1; i >= 0; i--) { var v = st[i]; if (!v.Dead && Finished(v, Frame)) { v.Dead = true; Forget(v); } }
            ApplyChannel(c);
        }
    }

    /// <summary>Fills <paramref name="left"/>[off..off+n) and <paramref name="right"/> (null: mono into left), as engine.js's render.</summary>
    public void Render(float[] left, float[]? right, int off, int n)
    {
        int end = off + n, o = off;
        outL = left; outR = right;
        while (o < end)
        {
            while (events.Count > 0 && events[0].Frame <= Frame) { var ev = events[0]; events.RemoveAt(0); Apply(ev); }
            if (Frame >= nextTick) { Tick(); nextTick = Frame - (Frame % Chunk) + Chunk; }
            int seg = (int)Math.Min(end - o, nextTick - Frame);
            if (events.Count > 0) seg = (int)Math.Min(seg, Math.Max(1, events[0].Frame - Frame));
            if (Trace != null) { if (Trace.Count > 0 && Trace[^1][0] == 1) Trace[^1][1] += seg; else Trace.Add([1, seg]); }
            outAt = o;
            Chip.Advance(seg * Chip.RATE);
            o += seg; Frame += seg;
        }
        if (Volume != 1)
        {
            double g = Volume;
            for (int i = off; i < end; i++) left[i] = (float)(left[i] * g);
            if (right != null) for (int i = off; i < end; i++) right[i] = (float)(right[i] * g);
        }
    }
}
