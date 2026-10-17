using System.Text.Json;
using System.Text.Json.Serialization;
using NesEmulator.Plugin;

namespace BrokenNes.Nano;

// The Nano instrument layer a second time, in C#, on Core's NesApuInstrument. It is written from the same description as Nano/src/engine.js (not
// generated from it): note events in, chip register writes out. tools/NanoParity runs it on the same scripts as the web module and requires
// the two to write the same registers at the same samples and to produce the same PCM.

public sealed class RefPreset
{
    public string Name { get; set; } = "";
    public int Kind { get; set; }
    public double Note { get; set; }
    public int Duty { get; set; }
    public int[]? DutySteps { get; set; }
    public double DutyRate { get; set; }
    public double Gain { get; set; }
    public double A { get; set; }
    public double D { get; set; }
    public double S { get; set; }
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
    public double NoiseIdx { get; set; }
    public double NoiseTo { get; set; }
    public double NoiseMs { get; set; }
    public int NoiseMode { get; set; }
    public int Sweep { get; set; }
    public int DmcAddr { get; set; }
    public int DmcLen { get; set; }
    public int DmcRate { get; set; }
    public bool DmcLoop { get; set; }
    public bool DmcPitched { get; set; }
    public int DmcStart { get; set; }
}

public sealed class RefVoice
{
    public int Id;
    public RefPreset P = null!;
    public double Note, Vel;
    public long Start;
    public int Channel;
    public bool Fresh = true, Released, Dead;
    public long ReleasedAt;
    public double RelLevel;
    public double Bend;
    public int Duty = -1, NoiseMode = -1;
}

public sealed class RefEvent
{
    public long Frame;
    public int Seq;
    public string T = "";
    public int Id;
    public int RefId = -1;        // off / bend by voice id
    public double RefNote = double.NaN;  // off / bend by note (the newest held voice with it)
    public double Note, Vel, Cents;
    public RefPreset? Preset;
    public int? Channel, Duty, NoiseMode;
    public double Bend, Duration;
    public string Name = "";
    public double Value;
}

public sealed class RefEngine
{
    const double CpuHz = 1789773.0;
    const int Chunk = 64, MaxStack = 8;
    static readonly int[][] KindChannels = [[0, 1], [2], [3], [4]];
    static readonly int[] DmcRates = [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54];
    const int Channels = 5;

    readonly int sampleRate;
    readonly IChipDriver chip;
    long frame, nextTick;
    readonly List<RefEvent> events = new();
    int seq;
    readonly List<RefVoice>[] stacks = [new(), new(), new(), new(), new()];
    readonly List<RefVoice> voices = new();   // insertion order, like the JS Map
    readonly bool[] gate = new bool[Channels];
    readonly int[] curPeriod = new int[Channels];
    readonly RefVoice?[] lastVoice = new RefVoice?[Channels];
    double pitchBend, tune;
    public readonly List<int[]> Trace = new();

    public RefEngine(int rate, byte[]? sampleRom)
    {
        sampleRate = rate;
        chip = sampleRom == null ? new InstrumentChip("FIX", rate, 0x0F) : new DmcChip(rate, sampleRom, 0x0F);
    }

    static double RoundEven(double x) => Math.Round(x, MidpointRounding.ToEven);

    void W(int reg, int value) { if (chip.Shadow(reg) != (byte)value) Wf(reg, value); }
    void Wf(int reg, int value) { chip.Write(reg, (byte)value); Trace.Add([0, reg, value]); }

    public void Schedule(long f, RefEvent ev)
    {
        ev.Frame = f; ev.Seq = seq++;
        int i = events.Count;
        while (i > 0 && events[i - 1].Frame > f) i--;
        events.Insert(i, ev);
    }

    RefVoice? FindVoice(RefEvent ev)
    {
        if (ev.RefId >= 0) return voices.FirstOrDefault(v => v.Id == ev.RefId);
        RefVoice? best = null;
        foreach (var v in voices) if (v.Note == ev.RefNote && !v.Released && (best == null || v.Start >= best.Start)) best = v;
        return best;
    }

    void Apply(RefEvent ev)
    {
        switch (ev.T)
        {
            case "on": NoteOn(ev); break;
            case "off": { var v = FindVoice(ev); if (v != null) Release(v); break; }
            case "bend": { var v = FindVoice(ev); if (v != null) v.Bend = ev.Cents; break; }
            case "alloff": foreach (var v in voices.ToArray()) Release(v); break;
            case "kill":
                foreach (var v in voices.ToArray()) Remove(v);
                for (int c = 0; c < Channels; c++) ApplyChannel(c);
                if ((chip.Shadow(0x15) & 0x10) != 0) Wf(0x15, 0x0F);
                break;
            case "param":
                if (ev.Name == "pitchBend") pitchBend = ev.Value;
                else if (ev.Name == "tune") tune = ev.Value;
                break;
        }
    }

    void NoteOn(RefEvent ev)
    {
        var p = ev.Preset!;
        int ch = PickChannel(p.Kind, ev.Channel);
        var v = new RefVoice { Id = ev.Id, P = p, Note = ev.Note, Vel = ev.Vel, Start = frame, Channel = ch };
        if (ev.Duty.HasValue) v.Duty = ev.Duty.Value;
        if (ev.NoiseMode.HasValue) v.NoiseMode = ev.NoiseMode.Value;
        if (ev.Bend != 0) v.Bend = ev.Bend;
        var st = stacks[ch];
        st.Add(v);
        if (st.Count > MaxStack) { var old = st[0]; st.RemoveAt(0); voices.Remove(old); }
        voices.Add(v);
        ApplyChannel(ch);
        if (ev.Duration > 0)
            Schedule(frame + (long)Math.Floor(ev.Duration * sampleRate + 0.5), new RefEvent { T = "off", RefId = v.Id });
    }

    int PickChannel(int kind, int? pin)
    {
        int[] cand = KindChannels[kind];
        if (pin.HasValue)
        {
            if (!cand.Contains(pin.Value)) throw new ArgumentException($"channel {pin} does not suit kind {kind}");
            cand = [pin.Value];
        }
        foreach (int c in cand) Prune(c);
        foreach (int c in cand) if (stacks[c].Count == 0) return c;
        int best = -1; long bestAt = long.MaxValue;
        foreach (int c in cand)
        {
            var top = stacks[c][^1];
            if (top.Released && top.ReleasedAt < bestAt) { best = c; bestAt = top.ReleasedAt; }
        }
        if (best >= 0) return best;
        best = cand[0]; bestAt = long.MaxValue;
        foreach (int c in cand)
        {
            var top = stacks[c][^1];
            if (top.Start < bestAt) { best = c; bestAt = top.Start; }
        }
        return best;
    }

    void Prune(int c)
    {
        var st = stacks[c];
        for (int i = st.Count - 1; i >= 0; i--)
            if (st[i].Dead) { voices.Remove(st[i]); st.RemoveAt(i); }
    }

    void Remove(RefVoice v)
    {
        v.Dead = true;
        voices.Remove(v);
        stacks[v.Channel].Remove(v);
    }

    double Tms(RefVoice v, long f) => (double)(f - v.Start) * 1000.0 / sampleRate;

    double Env(RefVoice v, long f)
    {
        var p = v.P;
        if (v.Released)
        {
            if (p.R <= 0) return 0;
            double rt = (double)(f - v.ReleasedAt) * 1000.0 / sampleRate;
            return v.RelLevel * Math.Max(0, 1 - rt / p.R);
        }
        double t = Tms(v, f);
        if (t < p.A) return t / p.A;
        double td = t - p.A;
        if (p.D > 0 && td < p.D) return 1 - (1 - p.S) * td / p.D;
        return p.S;
    }

    void Release(RefVoice v)
    {
        if (v.Released || v.Dead) return;
        if (v.P.OneShot) return;
        v.RelLevel = Env(v, frame);
        v.Released = true; v.ReleasedAt = frame;
        if (v.P.R <= 0) { v.Dead = true; voices.Remove(v); }
        ApplyChannel(v.Channel);
    }

    bool Finished(RefVoice v, long f)
    {
        var p = v.P;
        if (v.Dead) return true;
        if (v.Released) return p.R <= 0 || (double)(f - v.ReleasedAt) * 1000.0 / sampleRate >= p.R;
        return p.S == 0 && Tms(v, f) >= p.A + p.D;
    }

    double VoiceCents(RefVoice v, double t)
    {
        var p = v.P;
        double c = (v.Note - 60) * 100 + v.Bend + pitchBend + tune;
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
                double tri = ph < 0.25 ? 4 * ph : ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4;
                c += amp * tri;
            }
        }
        return c;
    }

    void ApplyChannel(int ch)
    {
        Prune(ch);
        var st = stacks[ch];
        var v = st.Count > 0 ? st[^1] : null;
        if (v == null)
        {
            if (gate[ch]) { Silence(ch); gate[ch] = false; }
            lastVoice[ch] = null;
            return;
        }
        var p = v.P;
        double t = Tms(v, frame);
        double cents = VoiceCents(v, t);
        double vol = v.Vel * p.Gain * Env(v, frame);
        float volF = (float)vol;   // the plugin's fl.Vol is a float and 15 * it is a float product
        int level = (int)Math.Clamp(RoundEven((double)(15 * volF)), 0, 15);
        if (ch == 4) level = v.Vel * p.Gain > 0 ? 15 : 0;
        var lv = lastVoice[ch];
        bool retrigger = v.Fresh || !gate[ch] || (lv != v && ((lv != null && lv.P.Sweep >= 0) || p.Sweep >= 0));
        v.Fresh = false;
        lastVoice[ch] = v;
        int duty = p.DutySteps != null ? p.DutySteps[(int)Math.Floor(t / 1000 * p.DutyRate) % p.DutySteps.Length] : (v.Duty >= 0 ? v.Duty : p.Duty);

        if (ch < 2)
        {
            int b = ch * 4;
            double freq = 440.0 * Math.Pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
            int period = (int)RoundEven(CpuHz / (16.0 * freq) - 1.0);
            if (period > 0x7FF) period = 0x7FF;
            if (period < 8) level = 0;
            period = Math.Max(period, 8);
            W(b, (duty << 6) | 0x30 | level);
            W(b + 1, p.Sweep >= 0 ? p.Sweep : 0x08);
            int hi = (period >> 8) & 7, lo = period & 0xFF;
            if (retrigger || (p.Sweep < 0 && hi != ((curPeriod[ch] >> 8) & 7)))
            {
                W(b + 2, lo);
                Wf(b + 3, hi | 0xF8);
            }
            else if (p.Sweep < 0) W(b + 2, lo);
            curPeriod[ch] = period;
        }
        else if (ch == 2)
        {
            double freq = 440.0 * Math.Pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
            int period = (int)RoundEven(CpuHz / (32.0 * freq) - 1.0);
            period = Math.Clamp(period, 2, 0x7FF);
            W(8, level > 0 ? 0xFF : 0x80);
            int hi = (period >> 8) & 7, lo = period & 0xFF;
            if (retrigger || hi != ((curPeriod[2] >> 8) & 7))
            {
                W(0x0A, lo);
                Wf(0x0B, hi | 0xF8);
            }
            else W(0x0A, lo);
            curPeriod[2] = period;
        }
        else if (ch == 3)
        {
            int idx;
            if (p.NoiseIdx >= 0)
            {
                double f = p.NoiseMs > 0 ? Math.Min(1, t / p.NoiseMs) : 1;
                idx = (int)Math.Clamp(Math.Floor(p.NoiseIdx + (p.NoiseTo - p.NoiseIdx) * f), 0, 15);
            }
            else
            {
                double semis = 60.0 + cents / 100.0;
                idx = (int)Math.Clamp(15 - Math.Floor((semis - 24.0) / 3.0), 0, 15);
            }
            int mode = v.NoiseMode >= 0 ? v.NoiseMode : p.NoiseMode;
            W(0x0C, 0x30 | level);
            W(0x0E, (mode << 7) | idx);
            if (retrigger) Wf(0x0F, 0xF8);
            curPeriod[3] = idx;
        }
        else
        {
            if (level == 0)
            {
                if (gate[4]) { Silence(4); gate[4] = false; }
                return;
            }
            if (retrigger || lv != v)
            {
                int rate = p.DmcRate;
                if (p.DmcPitched)
                {
                    double target = CpuHz / DmcRates[p.DmcRate] * Math.Pow(2.0, (cents / 100.0 - (p.Note - 60)) / 12.0);
                    double bd = double.PositiveInfinity;
                    for (int i = 0; i < 16; i++) { double d = Math.Abs(Math.Log2(CpuHz / DmcRates[i] / target)); if (d < bd) { bd = d; rate = i; } }
                }
                Wf(0x10, rate | (p.DmcLoop ? 0x40 : 0));
                Wf(0x11, p.DmcStart);
                Wf(0x12, p.DmcAddr);
                Wf(0x13, p.DmcLen);
                Wf(0x15, 0x0F);
                Wf(0x15, 0x1F);
            }
        }
        gate[ch] = true;
    }

    void Silence(int ch)
    {
        if (ch < 2) W(ch * 4, (((chip.Shadow(ch * 4) >> 6) & 3) << 6) | 0x30);
        else if (ch == 2) W(8, 0x80);
        else if (ch == 3) W(0x0C, 0x30);
        else if (lastVoice[4] != null && lastVoice[4]!.P.DmcLoop) Wf(0x15, 0x0F);
    }

    void Tick()
    {
        for (int c = 0; c < Channels; c++)
        {
            var st = stacks[c];
            for (int i = st.Count - 1; i >= 0; i--)
            {
                var v = st[i];
                if (!v.Dead && Finished(v, frame)) { v.Dead = true; voices.Remove(v); }
            }
            ApplyChannel(c);
        }
    }

    public float[] Render(long total)
    {
        var pcm = new float[total];
        long o = 0;
        while (o < total)
        {
            while (events.Count > 0 && events[0].Frame <= frame) { var ev = events[0]; events.RemoveAt(0); Apply(ev); }
            if (frame >= nextTick) { Tick(); nextTick = frame - (frame % Chunk) + Chunk; }
            long seg = Math.Min(total - o, nextTick - frame);
            if (events.Count > 0) seg = Math.Min(seg, Math.Max(1, events[0].Frame - frame));
            if (Trace.Count > 0 && Trace[^1][0] == 1) Trace[^1][1] += (int)seg; else Trace.Add([1, (int)seg]);
            chip.Render(pcm.AsSpan((int)o, (int)seg));
            o += seg; frame += seg;
        }
        return pcm;
    }

    // ---- scenario loading ----
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static (RefEngine engine, long total) FromScenario(JsonElement root)
    {
        int rate = root.GetProperty("sampleRate").GetInt32();
        byte[]? rom = root.TryGetProperty("dmcRom", out var romEl) && romEl.ValueKind == JsonValueKind.String ? Convert.FromBase64String(romEl.GetString()!) : null;
        var eng = new RefEngine(rate, rom);
        var presets = new Dictionary<string, RefPreset>();
        foreach (var kv in root.GetProperty("presets").EnumerateObject())
            presets[kv.Name] = kv.Value.Deserialize<RefPreset>(Json)!;
        foreach (var e in root.GetProperty("events").EnumerateArray())
        {
            var ev = new RefEvent { T = e.GetProperty("t").GetString()! };
            long f = e.GetProperty("frame").GetInt64();
            switch (ev.T)
            {
                case "on":
                    ev.Id = e.GetProperty("id").GetInt32(); ev.Note = e.GetProperty("note").GetDouble(); ev.Vel = e.GetProperty("vel").GetDouble();
                    ev.Preset = presets[e.GetProperty("preset").GetString()!];
                    var o = e.GetProperty("o");
                    if (o.TryGetProperty("channel", out var ch)) ev.Channel = ch.GetInt32();
                    if (o.TryGetProperty("duty", out var du)) ev.Duty = du.GetInt32();
                    if (o.TryGetProperty("noiseMode", out var nm)) ev.NoiseMode = nm.GetInt32();
                    if (o.TryGetProperty("bend", out var be)) ev.Bend = be.GetDouble();
                    if (o.TryGetProperty("duration", out var dr)) ev.Duration = dr.GetDouble();
                    break;
                case "off":
                case "bend":
                    var r = e.GetProperty("ref");
                    if (r.ValueKind == JsonValueKind.Number) ev.RefId = r.GetInt32(); else ev.RefNote = r.GetProperty("note").GetDouble();
                    if (ev.T == "bend") ev.Cents = e.GetProperty("cents").GetDouble();
                    break;
                case "param":
                    ev.Name = e.GetProperty("name").GetString()!; ev.Value = e.GetProperty("value").GetDouble();
                    break;
            }
            eng.Schedule(f, ev);
        }
        return (eng, root.GetProperty("frames").GetInt64());
    }
}
