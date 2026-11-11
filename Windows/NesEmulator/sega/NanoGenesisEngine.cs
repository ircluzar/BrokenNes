using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NesEmulator.Sega;

// =====================================================================================================================
//  BrokenNes Nano Genesis's instrument engine, in C#: notes -> YM2612 / PSG writes -> Core's Ym2612 + Sn76489 (NanoGenesisSound) -> samples.
//
//  A line-for-line twin of Nano/genesis/src/engine.js (the web library). The BrokenNes2 plugin's Nano Genesis chip plays through this class, so a
//  song composed in FL and played by the web library is the same notes on the same chips with the same presets. Nano/genesis/test/parity.mjs runs
//  both on the same scripts and compares the samples bit for bit (Nano/genesis/tools/NanoGenesisParity, "notes" and "midi"). Change one, change both.
//  The arithmetic is plain doubles in the JS order of operations, no Math.Pow / Log / Sin (Exp2 is the same polynomial), so the bits match.
//  Nothing allocates once constructed (voices are pooled): the plugin calls it on the audio thread.
// =====================================================================================================================

/// <summary>One FM operator of a Nano Genesis preset (register fields, algorithm order).</summary>
public sealed class NanoGenesisOp
{
    public int Mul { get; set; } = 1;
    public int Tl { get; set; }
    public int Dt { get; set; }
    public int Ar { get; set; } = 31;
    public int D1r { get; set; }
    public int Sl { get; set; }
    public int D2r { get; set; }
    public int Rr { get; set; } = 10;
    public int Rs { get; set; }
    public int Am { get; set; }
    public int Ssg { get; set; }
}

/// <summary>A normalized Nano Genesis preset (the fields of engine.js's normalizePreset; the built-ins come from <see cref="NanoGenesisPresets"/>).</summary>
public sealed class NanoGenesisPreset
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Desc { get; set; } = "";
    public int Kind { get; set; }             // 0 fm, 1 tone, 2 noise
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
    public int Alg { get; set; }
    public int Fb { get; set; }
    public int Ams { get; set; }
    public int Pms { get; set; }
    public NanoGenesisOp[]? Ops { get; set; }
}

/// <summary>The built-in presets in program order (generated from Nano/genesis/src/presets.js into <c>NanoGenesisPresets.Json</c> by Nano/genesis/build.mjs).</summary>
public static partial class NanoGenesisPresets
{
    internal sealed class Table { public NanoGenesisPreset[] Presets { get; set; } = []; public Dictionary<string, string> Drums { get; set; } = new(); }
    private static readonly Table table = JsonSerializer.Deserialize(Json, NanoGenesisJson.Default.Table)!;   // source-generated: the plugin is an AOT DLL
    public static IReadOnlyList<NanoGenesisPreset> All => table.Presets;
    public static int Count => table.Presets.Length;
    public static NanoGenesisPreset Program(int n) => table.Presets[((n % Count) + Count) % Count];
    public static int IndexOf(string name) { for (int i = 0; i < table.Presets.Length; i++) if (table.Presets[i].Name == name) return i; return -1; }
    public static NanoGenesisPreset? Named(string name) { int i = IndexOf(name); return i < 0 ? null : table.Presets[i]; }
    public static NanoGenesisPreset? Drum(int note) => table.Drums.TryGetValue(note.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n) ? table.Presets[IndexOf(n)] : null;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NanoGenesisPresets.Table))]
[JsonSerializable(typeof(NanoGenesisPreset))]
internal partial class NanoGenesisJson : JsonSerializerContext { }

public sealed class NanoGenesisEngine : INanoEngine
{
    public const int Chunk = 64;
    public const int Channels = 10;
    private const int MaxStack = 8;
    private static readonly int[] PsgTable = { 32767, 26028, 20675, 16422, 13045, 10362, 8231, 6568, 5193, 4125, 3277, 2603, 2067, 1642, 1304, 0 };
    private static readonly int[] Slot = { 0, 8, 4, 12 };
    private static readonly int[] Carriers = { 8, 8, 8, 8, 10, 14, 14, 15 };
    private static readonly double[] AttMid = BuildAttMid();
    private static readonly double[] TlMid = BuildTlMid();
    private static double[] BuildAttMid()
    {
        var m = new double[15];
        for (int a = 0; a < 14; a++) m[a] = Math.Sqrt((double)PsgTable[a] * PsgTable[a + 1]) / 32767;
        m[14] = PsgTable[14] / 2.0 / 32767;
        return m;
    }
    private static double[] BuildTlMid()
    {
        var m = new double[127];
        for (int k = 0; k < 127; k++) m[k] = Exp2(-(k + 0.5) * 0.0375 * 3.321928094887362);
        return m;
    }

    private sealed class Voice
    {
        public int Id, Channel, Pan;
        public NanoGenesisPreset P = null!;
        public double Note, Vel, RelLevel, Bend;
        public long Start, ReleasedAt;
        public bool Fresh, Released, Dead;
    }

    private enum T { On, Off, OffNote, Bend, AllOff, Kill, Volume, PitchBend, Tune, Pan, Lfo }
    private struct Ev
    {
        public long Frame; public T Type; public int Id; public double Note, Vel, Value; public NanoGenesisPreset? Preset;
        public int Channel, Pan; public double BendCents, Duration;
    }

    public readonly NanoGenesisSound Chip;
    public readonly int SampleRate;
    public double Volume = 1;
    public long Frame;
    private long nextTick;
    private readonly List<Ev> events = new(512);
    private readonly List<Voice>[] stacks = new List<Voice>[Channels];
    private readonly bool[] gate = new bool[Channels];
    public readonly int[] CurLevel = new int[Channels], CurNote = new int[Channels];
    private readonly Voice?[] lastVoice = new Voice?[Channels];
    private readonly int[] regTone = { -1, -1, -1 }, regAtt = { 15, 15, 15, 15 };
    private int regNoise = -1;
    private readonly int[] regFreq = { -1, -1, -1, -1, -1, -1 }, regPan = { -1, -1, -1, -1, -1, -1 }, regTl = new int[24];
    private readonly NanoGenesisPreset?[] regPatch = new NanoGenesisPreset?[6];
    private readonly bool[] keyed = new bool[6];
    private readonly long[] keyAt = { -1, -1, -1, -1, -1, -1 };
    public readonly int[] ChanPan = new int[Channels];
    private readonly int[] panTemp = new int[Channels];
    private double pitchBend, tune;
    private readonly Dictionary<int, Voice> voices = new(64);
    private readonly Stack<Voice> pool = new(64);
    private readonly Voice?[] temp = new Voice?[MaxStack + 2];

    public NanoGenesisEngine(int sampleRate)
    {
        SampleRate = sampleRate;
        Chip = new NanoGenesisSound(sampleRate);
        for (int c = 0; c < Channels; c++) { stacks[c] = new List<Voice>(MaxStack + 2); CurNote[c] = -1; }
        Array.Fill(regTl, -1);
        for (int i = 0; i < 64; i++) pool.Push(new Voice());
    }

    // ---- register access (only what changed) ----
    private void SetTone(int i, int n) { if (regTone[i] == n) return; regTone[i] = n; Chip.PsgWrite((byte)(0x80 | (i << 5) | (n & 15))); Chip.PsgWrite((byte)((n >> 4) & 0x3F)); }
    private void SetAtt(int i, int a) { if (regAtt[i] == a) return; regAtt[i] = a; Chip.PsgWrite((byte)(0x90 | (i << 5) | a)); }
    private void SetNoise(int v, bool force) { if (!force && regNoise == v) return; regNoise = v; Chip.PsgWrite((byte)(0xE0 | v)); }
    private void FmReg(int ch, int reg, int v) => Chip.YmWrite((ch >= 3 ? 0x100 : 0) | (reg + ch % 3), (byte)v);
    private void SetKey(int ch, bool on)
    {
        keyed[ch] = on; keyAt[ch] = Chip.YmSamples;
        Chip.YmWrite(0x28, (byte)((on ? 0xF0 : 0) | (ch >= 3 ? 4 : 0) | (ch % 3)));
    }
    private void LoadPatch(int ch, NanoGenesisPreset p)
    {
        if (ReferenceEquals(regPatch[ch], p)) return;
        regPatch[ch] = p;
        for (int n = 0; n < 4; n++)
        {
            var o = p.Ops![n]; int s = Slot[n];
            FmReg(ch, 0x30 + s, (o.Dt << 4) | o.Mul);
            FmReg(ch, 0x50 + s, (o.Rs << 6) | o.Ar);
            FmReg(ch, 0x60 + s, (o.Am << 7) | o.D1r);
            FmReg(ch, 0x70 + s, o.D2r);
            FmReg(ch, 0x80 + s, (o.Sl << 4) | o.Rr);
            FmReg(ch, 0x90 + s, o.Ssg);
        }
        FmReg(ch, 0xB0, (p.Fb << 3) | p.Alg);
        regPan[ch] = -1;
        for (int n = 0; n < 4; n++) regTl[ch * 4 + n] = -1;
    }
    private void SetFmLevel(int ch, NanoGenesisPreset p, int extra)
    {
        int car = Carriers[p.Alg];
        for (int n = 0; n < 4; n++)
        {
            int tl = ((car >> n) & 1) != 0 ? Math.Min(127, p.Ops![n].Tl + extra) : p.Ops![n].Tl;
            if (regTl[ch * 4 + n] == tl) continue;
            regTl[ch * 4 + n] = tl; FmReg(ch, 0x40 + Slot[n], tl);
        }
    }
    private void SetFmPan(int ch, NanoGenesisPreset p, int pan)
    {
        int v = (pan <= 0 ? 0x80 : 0) | (pan >= 0 ? 0x40 : 0) | (p.Ams << 4) | p.Pms;
        if (regPan[ch] == v) return;
        regPan[ch] = v; FmReg(ch, 0xB4, v);
    }
    private void SetFreq(int ch, int bf) { if (regFreq[ch] == bf) return; regFreq[ch] = bf; FmReg(ch, 0xA4, bf >> 8); FmReg(ch, 0xA0, bf & 0xFF); }
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
    public void NoteOn(long frame, int id, double note, double vel, NanoGenesisPreset preset, int channel = -1, int pan = 2, double bend = 0, double duration = 0)
        => Schedule(frame, new Ev { Type = T.On, Id = id, Note = note, Vel = vel, Preset = preset, Channel = channel, Pan = pan, BendCents = bend, Duration = duration });
    public void NoteOff(long frame, int id) => Schedule(frame, new Ev { Type = T.Off, Id = id });
    public void NoteOffByNote(long frame, double note) => Schedule(frame, new Ev { Type = T.OffNote, Note = note });
    public void BendVoice(long frame, int id, double cents) => Schedule(frame, new Ev { Type = T.Bend, Id = id, Value = cents });
    public void AllOff(long frame) => Schedule(frame, new Ev { Type = T.AllOff });
    public void Kill(long frame) => Schedule(frame, new Ev { Type = T.Kill });
    public void SetVolume(long frame, double v) => Schedule(frame, new Ev { Type = T.Volume, Value = v });
    public void SetPitchBend(long frame, double cents) => Schedule(frame, new Ev { Type = T.PitchBend, Value = cents });
    public void SetTune(long frame, double cents) => Schedule(frame, new Ev { Type = T.Tune, Value = cents });
    public void SetLfo(long frame, int rate) => Schedule(frame, new Ev { Type = T.Lfo, Value = rate });
    /// <summary>The channels' own pans (-1 left, 0 both, 1 right), ten values: engine.js's setParam('pan'). Copied now; applied at the frame.</summary>
    public void SetPan(long frame, ReadOnlySpan<int> pans) { var e = new Ev { Type = T.Pan, Channel = PackPans(pans) }; Schedule(frame, e); }
    private static int PackPans(ReadOnlySpan<int> p) { int x = 0; for (int c = 0; c < Channels; c++) x |= (p[c] + 1) << (2 * c); return x; }
    /// <summary>The channels' pans set now, before anything plays (song.js's mk: a sidecar channel's pan).</summary>
    public void SetChannelPans(int pan) { for (int c = 0; c < Channels; c++) ChanPan[c] = pan; }
    long INanoEngine.Frame => Frame;
    public void NoteOnProgram(long frame, int id, double note, double vel, int program, double bend) => NoteOn(frame, id, note, vel, NanoGenesisPresets.Program(program), bend: bend);
    public bool IsSounding(int id) => voices.TryGetValue(id, out var v) && !v.Dead;
    public int SoundingChannels { get { int n = 0; for (int c = 0; c < Channels; c++) if (gate[c]) n++; return n; } }

    private Voice? FindByNote(double note)
    {
        Voice? best = null;
        for (int c = 0; c < Channels; c++) foreach (var v in stacks[c]) if (!v.Dead && v.Note == note && !v.Released && (best == null || v.Start >= best.Start)) best = v;
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
                for (int c = 0; c < Channels; c++)
                {
                    int n = stacks[c].Count;
                    for (int i = 0; i < n; i++) temp[i] = stacks[c][i];
                    for (int i = 0; i < n; i++) { Release(temp[i]!); temp[i] = null; }
                }
                break;
            case T.Kill:
                for (int c = 0; c < Channels; c++) { foreach (var v in stacks[c]) { v.Dead = true; Forget(v); if (!ReferenceEquals(lastVoice[c], v)) pool.Push(v); } stacks[c].Clear(); }
                for (int c = 0; c < Channels; c++) ApplyChannel(c);
                break;
            case T.Volume: Volume = ev.Value; break;
            case T.PitchBend: pitchBend = ev.Value; break;
            case T.Tune: tune = ev.Value; break;
            case T.Lfo: { int r = (int)ev.Value; Chip.YmWrite(0x22, (byte)(r > 0 ? 8 | (r - 1) : 0)); break; }
            case T.Pan:
                for (int c = 0; c < Channels; c++) ChanPan[c] = ((ev.Channel >> (2 * c)) & 3) - 1;
                for (int c = 0; c < 6; c++) if (regPatch[c] != null) SetFmPan(c, regPatch[c]!, PanOf(c));
                break;
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

    private int PickChannel(NanoGenesisPreset p, int pin)
    {
        int c0, c1;
        if (p.Kind == 0) { c0 = 0; c1 = 5; } else if (p.Kind == 1) { c0 = 6; c1 = 8; } else { c0 = c1 = 9; }
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
    public static int TlSteps(double amp) { for (int k = 0; k < 127; k++) if (amp >= TlMid[k]) return k; return 127; }
    public static int BlockFnum(double hz)
    {
        double scale = 2097152;
        for (int b = 0; b < 8; b++)
        {
            double f = RoundEven(hz * scale / NanoGenesisSound.YmRate);
            if (f <= 2047) return (b << 11) | (f < 1 ? 1 : (int)f);
            scale = scale * 0.5;
        }
        return (7 << 11) | 2047;
    }

    private void ApplyChannel(int ch)
    {
        Prune(ch);
        var st = stacks[ch];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null)
        {
            if (gate[ch])
            {
                if (ch < 6) { if (keyed[ch]) SetKey(ch, false); }
                else SetAtt(ch - 6, 15);
                gate[ch] = false; CurLevel[ch] = 0; CurNote[ch] = -1;
            }
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
        if (ch < 6)
        {
            double level = v.Vel * p.Gain * (v.Released ? v.RelLevel : Env(v, Frame));
            int extra = TlSteps(level);
            if (trig)
            {
                if (keyed[ch]) SetKey(ch, false);
                if (keyAt[ch] == Chip.YmSamples) Chip.YmRun(1);
                LoadPatch(ch, p);
            }
            SetFmPan(ch, p, PanOf(ch));
            SetFmLevel(ch, p, extra);
            SetFreq(ch, BlockFnum(MidiHz(VoiceCents(v, t))));
            if (trig && !v.Released) SetKey(ch, true);
            else if (v.Released && keyed[ch]) SetKey(ch, false);
            gate[ch] = true; CurLevel[ch] = 127 - extra; CurNote[ch] = (int)v.Note;
            return;
        }
        int att = Attenuation(v.Vel * p.Gain * Env(v, Frame));
        int i = ch - 6;
        if (ch == 9)
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
            int n = Clamp(RoundEven(NanoGenesisSound.PsgClockHz / (32 * MidiHz(VoiceCents(v, t)))), 2, 1023);
            SetTone(i, n);
        }
        SetAtt(i, att);
        gate[ch] = true; CurLevel[ch] = 15 - att; CurNote[ch] = (int)v.Note;
    }

    private void Tick()
    {
        for (int c = 0; c < Channels; c++)
        {
            var st = stacks[c];
            for (int i = st.Count - 1; i >= 0; i--) { var v = st[i]; if (!v.Dead && Finished(v, Frame)) { v.Dead = true; Forget(v); } }
            ApplyChannel(c);
        }
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
            Chip.Render(left, right, o, seg);
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
