using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  BrokenNes Nano SNES's instrument engine, in C#: notes -> S-DSP writes -> Core's DSP_SFC (NanoSnesSound) -> samples.
//
//  A line-for-line twin of Nano/snes/src/engine.js (the web library). The BrokenNes2 plugin's Nano SNES chip plays through this class.
//  Nano/snes/test/parity.mjs runs both on the same scripts and compares the samples bit for bit (Nano/snes/tools/NanoSnesParity "notes" and "midi").
//  Change one, change both. Plain doubles in the JS order of operations, no Math.Pow / Log / Sin. Nothing allocates once constructed (voices pooled),
//  except adding a preset whose sample is not in the sound RAM yet (never on the plugin's audio thread: its presets are the built-in ones).
// =====================================================================================================================

/// <summary>A normalized Nano SNES preset (engine.js's normalizePreset).</summary>
public sealed class NanoSnesPreset
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Desc { get; set; } = "";
    public int Kind { get; set; }             // 0 brr, 1 noise
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
    public int Pan { get; set; }
    public string Brr { get; set; } = "";
    public int Loop { get; set; } = -1;
    public double Base { get; set; } = 1000;
    public int Adsr1 { get; set; } = 0x8F;
    public int Adsr2 { get; set; } = 0xE0;
    public int GainReg { get; set; } = 0x7F;
    public int NoiseRate { get; set; } = -1;
}

/// <summary>The built-in presets in program order (generated from Nano/snes/src/presets.js into <c>NanoSnesPresets.Json</c> by Nano/snes/build.mjs).</summary>
public static partial class NanoSnesPresets
{
    internal sealed class Table { public NanoSnesPreset[] Presets { get; set; } = []; public Dictionary<string, string> Drums { get; set; } = new(); }
    private static readonly Table table = JsonSerializer.Deserialize(Json, NanoSnesJson.Default.Table)!;
    public static IReadOnlyList<NanoSnesPreset> All => table.Presets;
    public static int Count => table.Presets.Length;
    public static NanoSnesPreset Program(int n) => table.Presets[((n % Count) + Count) % Count];
    public static int IndexOf(string name) { for (int i = 0; i < table.Presets.Length; i++) if (table.Presets[i].Name == name) return i; return -1; }
    public static NanoSnesPreset? Named(string name) { int i = IndexOf(name); return i < 0 ? null : table.Presets[i]; }
    public static NanoSnesPreset? Drum(int note) => table.Drums.TryGetValue(note.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n) ? table.Presets[IndexOf(n)] : null;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NanoSnesPresets.Table))]
[JsonSerializable(typeof(NanoSnesPreset))]
internal partial class NanoSnesJson : JsonSerializerContext { }

public sealed class NanoSnesEngine : INanoEngine
{
    public const int Chunk = 64, Channels = 8;
    private const int MaxStack = 8, DirPage = 0x02, SampleBase = 0x0600, RamTop = 0xFF00, MainVolume = 0x30;

    private sealed class Voice
    {
        public int Id, Channel, Pan;
        public NanoSnesPreset P = null!;
        public double Note, Vel, RelLevel, Bend;
        public long Start, ReleasedAt;
        public bool Fresh, Released, Dead;
    }

    private enum T { On, Off, OffNote, Bend, AllOff, Kill, Volume, PitchBend, Tune, Pan }
    private struct Ev
    {
        public long Frame; public T Type; public int Id; public double Note, Vel, Value; public NanoSnesPreset? Preset;
        public int Channel, Pan; public double BendCents, Duration;
    }

    public readonly NanoSnesSound Chip;
    public readonly int SampleRate;
    public double Volume = 1;
    public long Frame;
    private long nextTick;
    private readonly List<Ev> events = new(512);
    private readonly List<Voice>[] stacks = new List<Voice>[Channels];
    private readonly bool[] gate = new bool[Channels], keyed = new bool[Channels];
    public readonly int[] CurLevel = new int[Channels], CurNote = new int[Channels];
    private readonly Voice?[] lastVoice = new Voice?[Channels];
    private readonly int[] regSrc = new int[Channels], regPitch = new int[Channels], regVolL = new int[Channels], regVolR = new int[Channels], regEnv = new int[Channels];
    private int konBits; private long konPoll = -1; private int koff, non, noiseRate = -1;
    public readonly int[] ChanPan = new int[Channels];
    private double pitchBend, tune;
    private readonly Dictionary<string, int> srcOf = new();
    private int nextSrc, ramAt = SampleBase;
    private readonly Dictionary<int, Voice> voices = new(64);
    private readonly Stack<Voice> pool = new(64);
    private readonly Voice?[] temp = new Voice?[MaxStack + 2];

    public NanoSnesEngine(int sampleRate, IEnumerable<NanoSnesPreset>? extra = null)
    {
        SampleRate = sampleRate;
        Chip = new NanoSnesSound(sampleRate);
        for (int c = 0; c < Channels; c++) { stacks[c] = new List<Voice>(MaxStack + 2); CurNote[c] = -1; regSrc[c] = regPitch[c] = regVolL[c] = regVolR[c] = regEnv[c] = -1; }
        void W(int r, int v) => Chip.Write(r, (byte)v);
        W(0x6C, 0x20); W(0x0C, MainVolume); W(0x1C, MainVolume); W(0x2C, 0); W(0x3C, 0); W(0x0D, 0); W(0x2D, 0); W(0x3D, 0); W(0x4D, 0); W(0x5D, DirPage); W(0x5C, 0);
        foreach (var p in NanoSnesPresets.All) Upload(p);
        if (extra != null) foreach (var p in extra) Upload(p);
        for (int i = 0; i < 64; i++) pool.Push(new Voice());
    }

    /// <summary>A preset's sample into the sound RAM, once per sample (engine.js addPreset).</summary>
    public void Upload(NanoSnesPreset p)
    {
        if (p.Kind != 0 || srcOf.ContainsKey(p.Brr)) return;
        if (nextSrc >= 256) throw new InvalidOperationException("the sound RAM directory is full (256 samples)");
        var bytes = Convert.FromBase64String(p.Brr);
        if (ramAt + bytes.Length > RamTop) throw new InvalidOperationException("the samples do not fit the 64 KB sound RAM");
        int at = ramAt, loop = p.Loop >= 0 ? at + p.Loop * 9 : at, src = nextSrc++;
        Chip.WriteRam(at, bytes);
        Chip.WriteRam(DirPage * 0x100 + src * 4, [(byte)(at & 0xFF), (byte)(at >> 8), (byte)(loop & 0xFF), (byte)(loop >> 8)]);
        ramAt += bytes.Length;
        srcOf[p.Brr] = src;
    }

    private void SetKey(int ch, bool on)
    {
        int bit = 1 << ch;
        if (on)
        {
            long poll = Chip.Polls;
            if (poll != konPoll) { konPoll = poll; konBits = 0; }
            konBits |= bit;
            if ((koff & bit) != 0) { koff &= ~bit; Chip.Write(0x5C, (byte)koff); }
            Chip.Write(0x4C, (byte)konBits);
        }
        else if ((koff & bit) == 0) { koff |= bit; Chip.Write(0x5C, (byte)koff); }
        keyed[ch] = on;
    }
    private void VReg(int ch, int r, int v) => Chip.Write((ch << 4) | r, (byte)v);

    // ---- events ----
    private void Schedule(long frame, Ev ev)
    {
        ev.Frame = frame;
        int i = events.Count;
        while (i > 0 && events[i - 1].Frame > frame) i--;
        events.Insert(i, ev);
    }
    public void NoteOn(long frame, int id, double note, double vel, NanoSnesPreset preset, int channel = -1, int pan = 2, double bend = 0, double duration = 0)
    {
        Upload(preset);
        Schedule(frame, new Ev { Type = T.On, Id = id, Note = note, Vel = vel, Preset = preset, Channel = channel, Pan = pan, BendCents = bend, Duration = duration });
    }
    public void NoteOff(long frame, int id) => Schedule(frame, new Ev { Type = T.Off, Id = id });
    public void NoteOffByNote(long frame, double note) => Schedule(frame, new Ev { Type = T.OffNote, Note = note });
    public void BendVoice(long frame, int id, double cents) => Schedule(frame, new Ev { Type = T.Bend, Id = id, Value = cents });
    public void AllOff(long frame) => Schedule(frame, new Ev { Type = T.AllOff });
    public void Kill(long frame) => Schedule(frame, new Ev { Type = T.Kill });
    public void SetVolume(long frame, double v) => Schedule(frame, new Ev { Type = T.Volume, Value = v });
    public void SetPitchBend(long frame, double cents) => Schedule(frame, new Ev { Type = T.PitchBend, Value = cents });
    public void SetTune(long frame, double cents) => Schedule(frame, new Ev { Type = T.Tune, Value = cents });
    public void SetPan(long frame, ReadOnlySpan<int> pans) { int x = 0; for (int c = 0; c < Channels; c++) x |= (pans[c] + 1) << (2 * c); Schedule(frame, new Ev { Type = T.Pan, Channel = x }); }
    public void SetChannelPans(int pan) { for (int c = 0; c < Channels; c++) ChanPan[c] = pan; }
    public bool IsSounding(int id) => voices.TryGetValue(id, out var v) && !v.Dead;
    public int SoundingChannels { get { int n = 0; for (int c = 0; c < Channels; c++) if (gate[c]) n++; return n; } }
    long INanoEngine.Frame => Frame;
    public void NoteOnProgram(long frame, int id, double note, double vel, int program, double bend) => NoteOn(frame, id, note, vel, NanoSnesPresets.Program(program), bend: bend);

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
            case T.Pan: for (int c = 0; c < Channels; c++) ChanPan[c] = ((ev.Channel >> (2 * c)) & 3) - 1; break;
        }
    }

    private void Forget(Voice v) { if (voices.TryGetValue(v.Id, out var have) && ReferenceEquals(have, v)) voices.Remove(v.Id); }

    private void DoNoteOn(in Ev ev)
    {
        var p = ev.Preset!;
        int ch = PickChannel(ev.Channel);
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

    private int PickChannel(int pin)
    {
        int c0 = 0, c1 = Channels - 1;
        if (pin >= 0)
        {
            if (pin > c1) throw new ArgumentOutOfRangeException(nameof(pin), $"no channel {pin}");
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

    private int PanOf(int c)
    {
        var st = stacks[c];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null || v.Dead) return ChanPan[c];
        return v.Pan != 2 ? v.Pan : v.P.Pan != 0 ? v.P.Pan : ChanPan[c];
    }

    private void ApplyChannel(int ch)
    {
        Prune(ch);
        var st = stacks[ch];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null)
        {
            if (gate[ch]) { if (keyed[ch]) SetKey(ch, false); gate[ch] = false; CurLevel[ch] = 0; CurNote[ch] = -1; }
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
        int bit = 1 << ch;
        if (trig)
        {
            int src = p.Kind == 0 ? srcOf[p.Brr] : 0;
            if (regSrc[ch] != src) { regSrc[ch] = src; VReg(ch, 4, src); }
            int envKey = p.Adsr1 << 16 | p.Adsr2 << 8 | p.GainReg;
            if (regEnv[ch] != envKey) { regEnv[ch] = envKey; VReg(ch, 5, p.Adsr1); VReg(ch, 6, p.Adsr2); VReg(ch, 7, p.GainReg); }
            int nn = p.Kind == 1 ? non | bit : non & ~bit;
            if (nn != non) { non = nn; Chip.Write(0x3D, (byte)nn); }
        }
        if (p.Kind == 1)
        {
            int rate = p.NoiseRate;
            if (rate < 0) rate = Clamp(Math.Floor(VoiceCents(v, t) / 400) + 1, 0, 31);
            if (rate != noiseRate) { noiseRate = rate; Chip.Write(0x6C, (byte)(0x20 | rate)); }
        }
        else
        {
            int pitch = Clamp(RoundEven(4096 * MidiHz(VoiceCents(v, t)) / p.Base), 0, 0x3FFF);
            if (regPitch[ch] != pitch) { regPitch[ch] = pitch; VReg(ch, 2, pitch & 0xFF); VReg(ch, 3, pitch >> 8); }
        }
        double level = v.Vel * p.Gain * Env(v, Frame);
        int vol = Clamp(RoundEven(level * 127), 0, 127), pan = PanOf(ch);
        int vl = pan > 0 ? 0 : vol, vr = pan < 0 ? 0 : vol;
        if (regVolL[ch] != vl) { regVolL[ch] = vl; VReg(ch, 0, vl); }
        if (regVolR[ch] != vr) { regVolR[ch] = vr; VReg(ch, 1, vr); }
        if (trig) SetKey(ch, true);
        gate[ch] = true; CurLevel[ch] = vol; CurNote[ch] = (int)v.Note;
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
