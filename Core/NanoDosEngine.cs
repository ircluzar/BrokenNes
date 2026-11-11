using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NesEmulator.X86;

// =====================================================================================================================
//  BrokenNes Nano DOS's instrument engine, in C#: notes -> AdLib / speaker writes -> Core's Mame036Opl + the speaker (NanoDosSound) -> samples.
//  A line-for-line twin of Nano/dos/src/engine.js; Nano/dos/test/parity.mjs compares the two bit for bit. Change one, change both.
//  Plain doubles in the JS order of operations, no Math.Pow / Log / Sin. Nothing allocates once constructed (voices pooled).
// =====================================================================================================================

public sealed class NanoDosPreset
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Desc { get; set; } = "";
    public int Kind { get; set; }             // 0 fm, 1 speaker
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
    public int[]? Regs { get; set; }
}

public static partial class NanoDosPresets
{
    internal sealed class Table { public NanoDosPreset[] Presets { get; set; } = []; public Dictionary<string, string> Drums { get; set; } = new(); }
    private static readonly Table table = JsonSerializer.Deserialize(Json, NanoDosJson.Default.Table)!;
    public static IReadOnlyList<NanoDosPreset> All => table.Presets;
    public static int Count => table.Presets.Length;
    public static NanoDosPreset Program(int n) => table.Presets[((n % Count) + Count) % Count];
    public static int IndexOf(string name) { for (int i = 0; i < table.Presets.Length; i++) if (table.Presets[i].Name == name) return i; return -1; }
    public static NanoDosPreset? Named(string name) { int i = IndexOf(name); return i < 0 ? null : table.Presets[i]; }
    public static NanoDosPreset? Drum(int note) => table.Drums.TryGetValue(note.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n) ? table.Presets[IndexOf(n)] : null;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NanoDosPresets.Table))]
[JsonSerializable(typeof(NanoDosPreset))]
internal partial class NanoDosJson : JsonSerializerContext { }

public sealed class NanoDosEngine : INanoEngine
{
    public const int Chunk = 64, Channels = 10;
    private const int MaxStack = 8;
    private static readonly int[] OpMod = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    private const double OplRate = 3579545.0 / 72;
    private static readonly double[] TlMid = BuildTlMid();
    private static double[] BuildTlMid() { var m = new double[63]; for (int k = 0; k < 63; k++) m[k] = Exp2(-(k + 0.5) * 0.0375 * 3.321928094887362); return m; }

    private sealed class Voice
    {
        public int Id, Channel;
        public NanoDosPreset P = null!;
        public double Note, Vel, RelLevel, Bend;
        public long Start, ReleasedAt;
        public bool Fresh, Released, Dead;
    }
    private enum T { On, Off, OffNote, Bend, AllOff, Kill, Volume, PitchBend, Tune }
    private struct Ev { public long Frame; public T Type; public int Id; public double Note, Vel, Value; public NanoDosPreset? Preset; public int Channel; public double BendCents, Duration; }

    public readonly NanoDosSound Chip;
    public readonly int SampleRate;
    public double Volume = 1;
    public long Frame;
    private long nextTick;
    private readonly List<Ev> events = new(512);
    private readonly List<Voice>[] stacks = new List<Voice>[Channels];
    private readonly bool[] gate = new bool[Channels], keyed = new bool[9];
    public readonly int[] CurLevel = new int[Channels], CurNote = new int[Channels];
    private readonly Voice?[] lastVoice = new Voice?[Channels];
    private readonly NanoDosPreset?[] regPatch = new NanoDosPreset?[9];
    private readonly int[] regTl = new int[18], regFreq = new int[9];
    private bool spkOn; private int spkCount = -1;
    private double pitchBend, tune;
    private readonly Dictionary<int, Voice> voices = new(64);
    private readonly Stack<Voice> pool = new(64);
    private readonly Voice?[] temp = new Voice?[MaxStack + 2];

    public NanoDosEngine(int sampleRate)
    {
        SampleRate = sampleRate;
        Chip = new NanoDosSound(sampleRate);
        for (int c = 0; c < Channels; c++) { stacks[c] = new List<Voice>(MaxStack + 2); CurNote[c] = -1; }
        Array.Fill(regTl, -1); Array.Fill(regFreq, -1);
        for (int i = 0; i < 64; i++) pool.Push(new Voice());
        Chip.OplWrite(0x01, 0x20);
    }

    private void LoadPatch(int ch, NanoDosPreset p)
    {
        if (ReferenceEquals(regPatch[ch], p)) return;
        regPatch[ch] = p;
        int m = OpMod[ch], c = m + 3; var r = p.Regs!;
        void W(int a, int v) => Chip.OplWrite(a, (byte)v);
        W(0x20 + m, r[0]); W(0x20 + c, r[1]); W(0x60 + m, r[4]); W(0x60 + c, r[5]); W(0x80 + m, r[6]); W(0x80 + c, r[7]); W(0xE0 + m, r[8]); W(0xE0 + c, r[9]); W(0xC0 + ch, r[10]);
        regTl[ch * 2] = regTl[ch * 2 + 1] = -1;
    }
    private void SetLevel(int ch, NanoDosPreset p, int extra)
    {
        int m = OpMod[ch]; var r = p.Regs!; bool additive = (r[10] & 1) != 0;
        int mv = (r[2] & 0xC0) | (additive ? Math.Min(63, (r[2] & 63) + extra) : r[2] & 63), cv = (r[3] & 0xC0) | Math.Min(63, (r[3] & 63) + extra);
        if (regTl[ch * 2] != mv) { regTl[ch * 2] = mv; Chip.OplWrite(0x40 + m, (byte)mv); }
        if (regTl[ch * 2 + 1] != cv) { regTl[ch * 2 + 1] = cv; Chip.OplWrite(0x43 + m, (byte)cv); }
    }
    private void SetFreq(int ch, int bf, bool key)
    {
        int v = bf | (key ? 0x2000 : 0);
        if (regFreq[ch] == v) return;
        int old = regFreq[ch];
        regFreq[ch] = v;
        if (old < 0 || (old & 0xFF) != (v & 0xFF)) Chip.OplWrite(0xA0 + ch, (byte)(v & 0xFF));
        Chip.OplWrite(0xB0 + ch, (byte)(v >> 8));
        keyed[ch] = key;
    }
    private void SetSpeaker(bool on, int count)
    {
        if (on == spkOn && (!on || count == spkCount)) return;
        spkOn = on; if (on) spkCount = count;
        Chip.Speaker(on, on ? count : Chip.SpkCount);
    }

    private void Schedule(long frame, Ev ev)
    {
        ev.Frame = frame;
        int i = events.Count;
        while (i > 0 && events[i - 1].Frame > frame) i--;
        events.Insert(i, ev);
    }
    public void NoteOn(long frame, int id, double note, double vel, NanoDosPreset preset, int channel = -1, double bend = 0, double duration = 0)
        => Schedule(frame, new Ev { Type = T.On, Id = id, Note = note, Vel = vel, Preset = preset, Channel = channel, BendCents = bend, Duration = duration });
    public void NoteOff(long frame, int id) => Schedule(frame, new Ev { Type = T.Off, Id = id });
    public void NoteOffByNote(long frame, double note) => Schedule(frame, new Ev { Type = T.OffNote, Note = note });
    public void BendVoice(long frame, int id, double cents) => Schedule(frame, new Ev { Type = T.Bend, Id = id, Value = cents });
    public void AllOff(long frame) => Schedule(frame, new Ev { Type = T.AllOff });
    public void Kill(long frame) => Schedule(frame, new Ev { Type = T.Kill });
    public void SetVolume(long frame, double v) => Schedule(frame, new Ev { Type = T.Volume, Value = v });
    public void SetPitchBend(long frame, double cents) => Schedule(frame, new Ev { Type = T.PitchBend, Value = cents });
    public void SetTune(long frame, double cents) => Schedule(frame, new Ev { Type = T.Tune, Value = cents });
    public bool IsSounding(int id) => voices.TryGetValue(id, out var v) && !v.Dead;
    public int SoundingChannels { get { int n = 0; for (int c = 0; c < Channels; c++) if (gate[c]) n++; return n; } }
    long INanoEngine.Frame => Frame;
    public void NoteOnProgram(long frame, int id, double note, double vel, int program, double bend) => NoteOn(frame, id, note, vel, NanoDosPresets.Program(program), bend: bend);

    private Voice? FindByNote(double note)
    {
        Voice? best = null;
        for (int c = 0; c < Channels; c++) foreach (var v in stacks[c]) if (!v.Dead && v.Note == note && !v.Released && (best == null || v.Start >= best.Start)) best = v;
        return best;
    }
    private Voice? FindById(int id) => voices.TryGetValue(id, out var v) ? v : null;
    private void Forget(Voice v) { if (voices.TryGetValue(v.Id, out var have) && ReferenceEquals(have, v)) voices.Remove(v.Id); }

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
        }
    }

    private void DoNoteOn(in Ev ev)
    {
        var p = ev.Preset!;
        int ch = PickChannel(p, ev.Channel);
        var v = pool.Count > 0 ? pool.Pop() : new Voice();
        v.Id = ev.Id; v.P = p; v.Note = ev.Note; v.Vel = ev.Vel; v.Start = Frame; v.Channel = ch;
        v.Fresh = true; v.Released = false; v.ReleasedAt = 0; v.RelLevel = 0; v.Dead = false; v.Bend = 0;
        if (ev.BendCents != 0) v.Bend = ev.BendCents;
        var st = stacks[ch];
        st.Add(v);
        if (st.Count > MaxStack) { var old = st[0]; st.RemoveAt(0); old.Dead = true; Forget(old); if (!ReferenceEquals(lastVoice[ch], old)) pool.Push(old); }
        if (voices.TryGetValue(v.Id, out var prev)) prev.Dead = true;
        voices[v.Id] = v;
        ApplyChannel(ch);
        if (ev.Duration > 0) Schedule(Frame + (long)Math.Floor(ev.Duration * SampleRate + 0.5), new Ev { Type = T.Off, Id = v.Id });
    }

    private int PickChannel(NanoDosPreset p, int pin)
    {
        int c0, c1;
        if (p.Kind == 0) { c0 = 0; c1 = 8; } else { c0 = c1 = 9; }
        if (pin >= 0) { if (pin < c0 || pin > c1) throw new ArgumentOutOfRangeException(nameof(pin)); c0 = c1 = pin; }
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
        if (v.P.R <= 0 || v.P.Kind == 1) { v.Dead = true; Forget(v); }
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
        if (p.SlideMs > 0 || p.SlideFrom != 0 || p.SlideTo != 0) { double f = p.SlideMs > 0 ? Math.Min(1, t / p.SlideMs) : 1; c += p.SlideFrom + (p.SlideTo - p.SlideFrom) * f; }
        if (p.ArpSteps != null) { int i = (int)Math.Floor(t / 1000 * p.ArpRate); i = p.ArpOnce ? Math.Min(i, p.ArpSteps.Length - 1) : i % p.ArpSteps.Length; c += p.ArpSteps[i] * 100; }
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

    public static double RoundEven(double x) { double f = Math.Floor(x), d = x - f; if (d < 0.5) return f; if (d > 0.5) return f + 1; return f % 2 == 0 ? f : f + 1; }
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
    private static int TlSteps(double amp) { for (int k = 0; k < 63; k++) if (amp >= TlMid[k]) return k; return 63; }
    public static int BlockFnum(double hz)
    {
        double scale = 1048576;
        for (int b = 0; b < 8; b++) { double f = RoundEven(hz * scale / OplRate); if (f <= 1023) return (b << 10) | (f < 1 ? 1 : (int)f); scale = scale * 0.5; }
        return (7 << 10) | 1023;
    }
    public static int SpeakerCount(double hz) => Clamp(RoundEven(1193182 / hz), 1, 65535);

    private void ApplyChannel(int ch)
    {
        Prune(ch);
        var st = stacks[ch];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null)
        {
            if (gate[ch])
            {
                if (ch < 9) { if (keyed[ch]) SetFreq(ch, regFreq[ch] & 0x1FFF, false); }
                else SetSpeaker(false, 0);
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
        if (ch < 9)
        {
            double level = v.Vel * p.Gain * (v.Released ? v.RelLevel : Env(v, Frame));
            int extra = TlSteps(level);
            if (trig) { if (keyed[ch]) SetFreq(ch, regFreq[ch] & 0x1FFF, false); LoadPatch(ch, p); }
            SetLevel(ch, p, extra);
            SetFreq(ch, BlockFnum(MidiHz(VoiceCents(v, t))), !v.Released);
            gate[ch] = true; CurLevel[ch] = 63 - extra; CurNote[ch] = (int)v.Note;
            return;
        }
        double lev = v.Vel * p.Gain * Env(v, Frame);
        SetSpeaker(lev > 0, SpeakerCount(MidiHz(VoiceCents(v, t))));
        gate[ch] = true; CurLevel[ch] = lev > 0 ? 63 : 0; CurNote[ch] = (int)v.Note;
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
