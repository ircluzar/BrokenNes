using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  BrokenNes Nano SMS's instrument engine, in C#: notes -> SN76489 writes -> Core's Sn76489 (variant Sega) -> samples.
//
//  A line-for-line twin of Nano/sms/src/engine.js (the web library). The BrokenNes2 plugin's Nano SMS chip plays through this class, so a song
//  composed in FL and played by the web library is the same notes on the same chip with the same presets. Nano/sms/test/parity.mjs runs both on
//  the same scripts and compares the samples bit for bit (tools: Nano/sms/tools/NanoSmsParity, "notes" and "midi"). Change one, change both.
//  The arithmetic is plain doubles in the JS order of operations, no Math.Pow / Log / Sin (Exp2 is the same polynomial), so the bits match.
//  Nothing allocates once constructed (voices are pooled): the plugin calls it on the audio thread.
// =====================================================================================================================

/// <summary>A normalized Nano SMS preset (the fields of engine.js's normalizePreset; the built-ins come from <see cref="NanoSmsPresets"/>).</summary>
public sealed class NanoSmsPreset
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Desc { get; set; } = "";
    public int Kind { get; set; }             // 0 tone, 1 noise
    public double Note { get; set; } = 60;
    public double Gain { get; set; } = 1;
    public double A { get; set; }
    public double D { get; set; }
    public double S { get; set; } = 1;
    public double R { get; set; }
    public bool OneShot { get; set; }
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
    public int NoiseRate { get; set; } = -1;
    public int NoiseTo { get; set; }
    public double NoiseMs { get; set; }
    public bool NoiseWhite { get; set; } = true;
    public int Pan { get; set; }
}

/// <summary>The built-in presets in program order (generated from Nano/sms/src/presets.js into <c>NanoSmsPresets.Json</c> by Nano/sms/build.mjs).</summary>
public static partial class NanoSmsPresets
{
    internal sealed class Table { public NanoSmsPreset[] Presets { get; set; } = []; public Dictionary<string, string> Drums { get; set; } = new(); }
    private static readonly Table table = JsonSerializer.Deserialize(Json, NanoSmsJson.Default.Table)!;   // source-generated: the plugin is an AOT DLL
    /// <summary>Program number n = All[n] (the web library's PRESETS order).</summary>
    public static IReadOnlyList<NanoSmsPreset> All => table.Presets;
    public static int Count => table.Presets.Length;
    public static NanoSmsPreset Program(int n) => table.Presets[((n % Count) + Count) % Count];
    public static int IndexOf(string name) { for (int i = 0; i < table.Presets.Length; i++) if (table.Presets[i].Name == name) return i; return -1; }
    public static NanoSmsPreset? Named(string name) { int i = IndexOf(name); return i < 0 ? null : table.Presets[i]; }
    /// <summary>General MIDI drum note (channel 10) -> preset, or null.</summary>
    public static NanoSmsPreset? Drum(int note) => table.Drums.TryGetValue(note.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n) ? table.Presets[IndexOf(n)] : null;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NanoSmsPresets.Table))]
[JsonSerializable(typeof(NanoSmsPreset))]
internal partial class NanoSmsJson : JsonSerializerContext { }

public sealed class NanoSmsEngine
{
    public const int Chunk = 64;
    private const int MaxStack = 8;
    public const double NtscZ80 = 53693175.0 / 15;
    private static readonly int[] PsgTable = { 32767, 26028, 20675, 16422, 13045, 10362, 8231, 6568, 5193, 4125, 3277, 2603, 2067, 1642, 1304, 0 };
    private static readonly double[] AttMid = BuildAttMid();
    private static double[] BuildAttMid()
    {
        var m = new double[15];
        for (int a = 0; a < 14; a++) m[a] = Math.Sqrt((double)PsgTable[a] * PsgTable[a + 1]) / 32767;
        m[14] = PsgTable[14] / 2.0 / 32767;
        return m;
    }

    private sealed class Voice
    {
        public int Id, Channel, Pan;
        public NanoSmsPreset P = null!;
        public double Note, Vel, RelLevel, Bend;
        public long Start, ReleasedAt;
        public bool Fresh, Released, Dead;
    }

    private enum T { On, Off, OffNote, Bend, AllOff, Kill, Volume, PitchBend, Tune, Pan }
    private struct Ev
    {
        public long Frame; public T Type; public int Id; public double Note, Vel, Value; public NanoSmsPreset? Preset;
        public int Channel, Pan, P0, P1, P2, P3; public double BendCents, Duration;
    }

    public readonly Sn76489 Chip;
    public readonly int SampleRate;
    public readonly bool GameGear;
    private readonly double cpuClockHz;
    public double Volume = 1;
    public long Frame;
    private long nextTick;
    private readonly List<Ev> events = new(512);
    private readonly List<Voice>[] stacks = { new(MaxStack + 2), new(MaxStack + 2), new(MaxStack + 2), new(MaxStack + 2) };
    private readonly bool[] gate = new bool[4];
    public readonly int[] CurLevel = new int[4], CurNote = { -1, -1, -1, -1 };
    private readonly Voice?[] lastVoice = new Voice?[4];
    private readonly int[] regTone = { -1, -1, -1 }, regAtt = { 15, 15, 15, 15 };
    private int regNoise = -1, stereo = 0xFF;
    public readonly int[] ChanPan = new int[4];
    private double pitchBend, tune;
    private readonly Dictionary<int, Voice> voices = new(64);
    private readonly Stack<Voice> pool = new(64);
    private readonly Voice?[] temp = new Voice?[MaxStack + 2];
    private short[] buf = new short[2 * 512];

    public NanoSmsEngine(int sampleRate, bool gameGear = false, double clockHz = NtscZ80)
    {
        SampleRate = sampleRate;
        GameGear = gameGear;
        cpuClockHz = clockHz;
        Chip = new Sn76489(clockHz, sampleRate, PsgVariant.Sega);
        for (int i = 0; i < 64; i++) pool.Push(new Voice());
    }

    // ---- register access (only what changed) ----
    private void SetTone(int ch, int n) { if (regTone[ch] == n) return; regTone[ch] = n; Chip.Write((byte)(0x80 | (ch << 5) | (n & 15))); Chip.Write((byte)((n >> 4) & 0x3F)); }
    private void SetAtt(int ch, int a) { if (regAtt[ch] == a) return; regAtt[ch] = a; Chip.Write((byte)(0x90 | (ch << 5) | a)); }
    private void SetNoise(int v, bool force) { if (!force && regNoise == v) return; regNoise = v; Chip.Write((byte)(0xE0 | v)); }
    private void SetPan()
    {
        if (!GameGear) return;
        int st = 0;
        for (int c = 0; c < 4; c++) { int p = PanOf(c); st |= (p <= 0 ? 0x10 << c : 0) | (p >= 0 ? 1 << c : 0); }
        if (st != stereo) { stereo = st; Chip.WriteStereo((byte)st); }
    }
    private int PanOf(int c)
    {
        var st = stacks[c];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null || v.Dead) return ChanPan[c];
        return v.Pan != 2 ? v.Pan : v.P.Pan != 0 ? v.P.Pan : ChanPan[c];
    }

    // ---- events ----
    private void Schedule(long frame, Ev ev)
    {
        ev.Frame = frame;
        int i = events.Count;
        while (i > 0 && events[i - 1].Frame > frame) i--;
        events.Insert(i, ev);
    }
    /// <param name="channel">-1: the engine picks; pan 2: the preset's / the channel's.</param>
    public void NoteOn(long frame, int id, double note, double vel, NanoSmsPreset preset, int channel = -1, int pan = 2, double bend = 0, double duration = 0)
        => Schedule(frame, new Ev { Type = T.On, Id = id, Note = note, Vel = vel, Preset = preset, Channel = channel, Pan = pan, BendCents = bend, Duration = duration });
    public void NoteOff(long frame, int id) => Schedule(frame, new Ev { Type = T.Off, Id = id });
    public void NoteOffByNote(long frame, double note) => Schedule(frame, new Ev { Type = T.OffNote, Note = note });
    public void BendVoice(long frame, int id, double cents) => Schedule(frame, new Ev { Type = T.Bend, Id = id, Value = cents });
    public void AllOff(long frame) => Schedule(frame, new Ev { Type = T.AllOff });
    public void Kill(long frame) => Schedule(frame, new Ev { Type = T.Kill });
    public void SetVolume(long frame, double v) => Schedule(frame, new Ev { Type = T.Volume, Value = v });
    public void SetPitchBend(long frame, double cents) => Schedule(frame, new Ev { Type = T.PitchBend, Value = cents });
    public void SetTune(long frame, double cents) => Schedule(frame, new Ev { Type = T.Tune, Value = cents });
    /// <summary>The channels' own pans (-1 left, 0 both, 1 right) for tone 1-3 and noise: engine.js's setParam('pan').</summary>
    public void SetPan(long frame, int p0, int p1, int p2, int p3) => Schedule(frame, new Ev { Type = T.Pan, P0 = p0, P1 = p1, P2 = p2, P3 = p3 });
    /// <summary>The channels' pans set now, before anything plays (song.js's mk: a sidecar channel's pan).</summary>
    public void SetChannelPans(int pan) { for (int c = 0; c < 4; c++) ChanPan[c] = pan; }
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
                    for (int i = 0; i < n; i++) { Release(temp[i]!); temp[i] = null; }
                }
                break;
            case T.Kill:
                for (int c = 0; c < 4; c++) { foreach (var v in stacks[c]) { v.Dead = true; Forget(v); if (!ReferenceEquals(lastVoice[c], v)) pool.Push(v); } stacks[c].Clear(); }
                for (int c = 0; c < 4; c++) ApplyChannel(c);
                break;
            case T.Volume: Volume = ev.Value; break;
            case T.PitchBend: pitchBend = ev.Value; break;
            case T.Tune: tune = ev.Value; break;
            case T.Pan: ChanPan[0] = ev.P0; ChanPan[1] = ev.P1; ChanPan[2] = ev.P2; ChanPan[3] = ev.P3; SetPan(); break;
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
        v.Fresh = true; v.Released = false; v.ReleasedAt = 0; v.RelLevel = 0; v.Dead = false; v.Bend = 0; v.Pan = 2;
        if (ev.Pan != 2) v.Pan = ev.Pan;
        if (ev.BendCents != 0) v.Bend = ev.BendCents;
        var st = stacks[ch];
        st.Add(v);
        if (st.Count > MaxStack) { var old = st[0]; st.RemoveAt(0); old.Dead = true; Forget(old); if (!ReferenceEquals(lastVoice[ch], old)) pool.Push(old); }
        if (voices.TryGetValue(v.Id, out var prev)) prev.Dead = true;
        voices[v.Id] = v;
        ApplyChannel(ch);
        if (ev.Duration > 0) Schedule(Frame + (long)Math.Floor(ev.Duration * SampleRate + 0.5), new Ev { Type = T.Off, Id = v.Id });
    }

    private int PickChannel(NanoSmsPreset p, int pin)
    {
        int c0, c1;
        if (p.Kind == 0) { c0 = 0; c1 = 2; } else { c0 = c1 = 3; }
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
        if (v.P.R <= 0) { v.Dead = true; Forget(v); }
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
    private static int Attenuation(double amp) { for (int a = 0; a < 15; a++) if (amp >= AttMid[a]) return a; return 15; }

    private void ApplyChannel(int ch)
    {
        Prune(ch);
        var st = stacks[ch];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null)
        {
            if (gate[ch]) { SetAtt(ch, 15); gate[ch] = false; CurLevel[ch] = 0; CurNote[ch] = -1; }
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
        int att = Attenuation(v.Vel * p.Gain * Env(v, Frame));
        if (ch == 3)
        {
            int rate;
            if (p.NoiseRate >= 0)
            {
                double f = p.NoiseMs > 0 ? Math.Min(1, t / p.NoiseMs) : 1;
                rate = Clamp(Math.Floor(p.NoiseRate + (p.NoiseTo - p.NoiseRate) * f), 0, 2);
            }
            else
            {
                double semis = VoiceCents(v, t) / 100;
                rate = semis >= 72 ? 0 : semis >= 60 ? 1 : 2;
            }
            SetNoise((p.NoiseWhite ? 4 : 0) | rate, trig);
        }
        else
        {
            int n = Clamp(RoundEven(cpuClockHz / (32 * MidiHz(VoiceCents(v, t)))), 2, 1023);
            SetTone(ch, n);
        }
        SetAtt(ch, att);
        if (trig) SetPan();
        gate[ch] = true; CurLevel[ch] = 15 - att; CurNote[ch] = (int)v.Note;
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

    private void ChipRender(float[] left, float[]? right, int off, int n)
    {
        Chip.RunForSamples(n);
        if (buf.Length < 2 * n) buf = new short[2 * n];
        int got = Chip.ReadSamples(buf.AsSpan(0, 2 * n));
        if (got != 2 * n) throw new InvalidOperationException($"the chip had {got / 2} of {n} frames");
        if (right != null) for (int i = 0; i < n; i++) { left[off + i] = buf[2 * i] / 32768f; right[off + i] = buf[2 * i + 1] / 32768f; }
        else for (int i = 0; i < n; i++) left[off + i] = buf[2 * i] / 32768f;
    }

    /// <summary>Fills <paramref name="left"/>[off..off+n) and <paramref name="right"/> (null: mono into left = the left side), as engine.js's render.</summary>
    public void Render(float[] left, float[]? right, int off, int n)
    {
        int end = off + n, o = off;
        while (o < end)
        {
            while (events.Count > 0 && events[0].Frame <= Frame) { var ev = events[0]; events.RemoveAt(0); Apply(ev); }
            if (Frame >= nextTick) { Tick(); nextTick = Frame - (Frame % Chunk) + Chunk; }
            int seg = (int)Math.Min(end - o, nextTick - Frame);
            if (events.Count > 0) seg = (int)Math.Min(seg, Math.Max(1, events[0].Frame - Frame));
            ChipRender(left, right, o, seg);
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
