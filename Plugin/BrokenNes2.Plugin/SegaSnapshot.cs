using NesEmulator.Sega;

namespace BrokenNes2;

/// <summary>
/// One sound the Genesis DAC played, found in the stream of writes the game made to the YM2612's data register: the 8-bit samples as they were written, the rate they were written at, and, for a steady or repeating sound (a
/// sampled waveform streamed in a loop), the loop. Immutable once made. The DAC is a single register: the game's Z80 driver streams a sample by writing one value after another at a fixed rate; a drum is a stretch of such writes that
/// begins and ends, an instrument a stretch that goes on and repeats.
/// </summary>
public sealed class DacSample
{
    public const int MaxLength = 65536;
    /// <summary>The rate the game wrote the samples at, in Hz.</summary>
    public readonly int Rate;
    /// <summary>The samples, unsigned 8-bit, 128 the middle (the chip's own data).</summary>
    public readonly byte[] Data;
    /// <summary>The loop in sample numbers (end exclusive), or -1 for a sound that plays once.</summary>
    public readonly int LoopStart, LoopEnd;
    /// <summary>How many times the game played it in the stretch that was recorded.</summary>
    public readonly int Repeats;

    public DacSample(int rate, byte[] data, int loopStart, int loopEnd, int repeats)
    {
        Rate = Math.Clamp(rate, 1000, 100000); Data = data; LoopStart = loopStart; LoopEnd = loopEnd; Repeats = Math.Max(1, repeats);
        if (loopEnd <= loopStart || loopStart < 0 || loopEnd > data.Length) { LoopStart = LoopEnd = -1; }
    }

    public bool Loops => LoopEnd > LoopStart;
    public int LoopLength => Loops ? LoopEnd - LoopStart : 0;
    public double Seconds => Data.Length / (double)Rate;
    /// <summary>The pitch of a looped sound in Hz: the rate over the loop's period for a single cycle, 0 when the loop holds several periods or the sound plays once. See <see cref="DacAnalyzer"/>, which records the period itself.</summary>
    public double CycleHz { get; internal set; }
}

/// <summary>
/// What a Sega game's sound chips were doing when Instrument Runaway froze it: the PSG's registers, and, for the Master System's FM unit, the YM2413's 64 registers, and for the Genesis, the YM2612's six channel patches and the
/// DAC sounds found in its recent writes. The sound is rebuilt from this on private chips, one set per FL instance (<see cref="PsgSampler"/>, <see cref="OpllSampler"/>, <see cref="OpnSampler"/>).
/// Saved with the project as <c>PSG1</c> (Master System and Game Gear on the PSG chip), <c>OPL1</c> (the Master System FM unit: PSG and YM2413) or <c>OPN1</c> (the Genesis: PSG, YM2612 and DAC sounds).
/// </summary>
public sealed class SegaSnapshot
{
    public const int KindPsg = 1, KindOpll = 2, KindOpn = 3;
    public const uint TagPsg = 0x31475350, TagOpll = 0x314C504F, TagOpn = 0x314E504F;   // "PSG1", "OPL1", "OPN1" (little-endian, like "SPC1" and "GBS1")
    private const int Version = 1;

    public readonly int Kind;
    // the PSG (every Sega game has one)
    public readonly int PsgClock;
    public readonly PsgVariant PsgVariant;
    public readonly int[] PsgPeriod = new int[3];
    public readonly int[] PsgVolume = [15, 15, 15, 15];
    public int PsgNoise, PsgStereo = 0xFF;
    // the YM2413
    public readonly byte[] Opll = new byte[64];
    // the YM2612: the chip's clock and per channel the patch (4 operators x 7 registers in algorithm order, then $B0, $B4), whether the channel was sounding, and the LFO register
    public int OpnClock;
    public readonly byte[][] OpnPatch = Enumerable.Range(0, 6).Select(_ => new byte[OpnPatchBytes]).ToArray();
    public readonly bool[] OpnKeyed = new bool[6];
    public int OpnLfo;
    public DacSample[] Dac = [];

    public const int OpnPatchBytes = 30;

    public SegaSnapshot(int kind, int psgClock, PsgVariant variant) { Kind = kind; PsgClock = psgClock; PsgVariant = variant; }

    // ---- the instruments a snapshot offers (what the Instrument picker lists) ----
    // PSG:  0 square tones (3 voices), 1 white noise, 2 periodic noise.
    // OPLL: 0 the game's user instrument, 1 to 15 the chip's own, then 16 PSG tones, 17 white noise, 18 periodic noise.
    // OPN:  0 to 5 the six channels' patches, then one entry per DAC sound, then the PSG's tones, white noise and periodic noise.
    // The Instrument parameter is 0 for "the game's choice" (see Resolve) and the instrument number + 1 otherwise.

    /// <summary>The instruments the YM2413 channels had selected (instrument number 0 is the user instrument, 1 to 15 the chip's own) when the game froze, with whether the channel was keyed on.</summary>
    public IEnumerable<(int Instrument, bool Keyed)> OpllInPlay()
    {
        for (int ch = 0; ch < 9; ch++)
            yield return (Opll[0x30 + ch] >> 4, (Opll[0x20 + ch] & 0x10) != 0);
    }

    /// <summary>Whether the game had a user instrument (its registers are not all zero).</summary>
    public bool OpllUserUsed => Opll.Take(8).Any(b => b != 0);

    public int InstrumentCount => Kind switch { KindPsg => 3, KindOpll => 19, _ => 6 + Dac.Length + 3 };

    /// <summary>The first PSG instrument's index: 16 on the YM2413 unit, after the DAC sounds on the Genesis, 0 on the PSG alone. The next two are its white and periodic noise.</summary>
    public int PsgInstrument => Kind switch { KindPsg => 0, KindOpll => 16, _ => 6 + Dac.Length };

    public bool IsPsgInstrument(int i) => i >= PsgInstrument && i < PsgInstrument + 3;
    public bool IsNoiseInstrument(int i) => i == PsgInstrument + 1 || i == PsgInstrument + 2;
    public bool IsWhiteNoise(int i) => i == PsgInstrument + 1;
    public bool IsDacInstrument(int i) => Kind == KindOpn && i >= 6 && i < 6 + Dac.Length;

    /// <summary>How many notes an instrument can play at once.</summary>
    public int Slots(int i) => IsPsgInstrument(i) ? (IsNoiseInstrument(i) ? 1 : 3) : Kind == KindOpll ? 9 : IsDacInstrument(i) ? 8 : 6;

    /// <summary>The first of the sampler's slots an instrument's notes go to (the samplers number them: FM channels first, then the DAC voices, then the PSG's three tones and its noise).</summary>
    public int SlotBase(int i)
    {
        if (IsPsgInstrument(i)) return (Kind switch { KindOpll => OpllSampler.PsgBase, KindOpn => OpnSampler.PsgBase, _ => 0 }) + (IsNoiseInstrument(i) ? 3 : 0);
        return IsDacInstrument(i) ? OpnSampler.DacBase : 0;
    }

    /// <summary>How many slots the sampler for this kind of snapshot has.</summary>
    public int TotalSlots => Kind switch { KindOpll => OpllSampler.Slots, KindOpn => OpnSampler.Slots, _ => PsgSampler.Slots };

    /// <summary>The instrument an Instrument parameter value plays: 1 and up are the instrument number + 1; 0 (what a new instance has) is the game's choice: what it was playing when it froze (the user instrument if a YM2413 channel was keyed with it,
    /// else the first instrument a channel was playing, else the first FM channel of the YM2612 that sounded, else the first that was set up).</summary>
    public int Resolve(int value)
    {
        if (value >= 1) return Math.Clamp(value - 1, 0, InstrumentCount - 1);
        if (Kind == KindOpll)
        {
            var play = OpllInPlay().ToArray();
            if (play.Any(p => p.Instrument == 0 && p.Keyed) && OpllUserUsed) return 0;
            foreach (var p in play) if (p.Keyed && p.Instrument != 0) return p.Instrument;
            foreach (var p in play) if (p.Instrument != 0) return p.Instrument;
            return OpllUserUsed ? 0 : 1;
        }
        if (Kind == KindOpn)
        {
            for (int c = 0; c < 6; c++) if (OpnKeyed[c]) return c;
            for (int c = 0; c < 6; c++) if (OpnPatchUsed(c)) return c;
        }
        return 0;
    }

    /// <summary>Whether the game ever set a channel's patch up (its registers are not all zero; a YM2612 channel that was never programmed has only its pan bits).</summary>
    public bool OpnPatchUsed(int channel) => OpnPatch[channel].Take(28).Any(b => b != 0);

    public (string Name, string? Note) Instrument(int i)
    {
        i = Math.Clamp(i, 0, InstrumentCount - 1);
        if (IsPsgInstrument(i))
        {
            if (!IsNoiseInstrument(i)) return ("PSG square tones", "3 voices");
            bool white = IsWhiteNoise(i), game = ((PsgNoise & 4) != 0) == white;
            return (white ? "PSG white noise" : "PSG periodic noise", game ? "as the game had it" : null);
        }
        if (Kind == KindOpll)
        {
            var channels = OpllInPlay().Select((p, ch) => (p, ch)).Where(x => x.p.Instrument == i && x.p.Keyed).Select(x => x.ch + 1).ToArray();
            string? note = channels.Length > 0 ? "playing on channel " + string.Join(", ", channels) : i == 0 && !OpllUserUsed ? "empty" : null;
            return (i == 0 ? "User instrument" : NesEmulator.Mix.OpllPatchBank.Patches[i - 1].Name, note);
        }
        if (i < 6) return (OpnPatchName(i), OpnKeyed[i] ? "the game was playing it" : OpnPatchUsed(i) ? null : "never set up");
        var d = Dac[i - 6];
        return (DacName(i - 6), d.Repeats > 1 ? $"played {d.Repeats} times" : null);
    }

    public string OpnPatchName(int channel)
    {
        var p = OpnPatch[channel];
        return $"FM {channel + 1}: algorithm {p[28] & 7}, feedback {p[28] >> 3 & 7}";
    }

    public string DacName(int index)
    {
        var d = Dac[index];
        return d.Loops ? $"DAC {index + 1}: {d.CycleHz:0} Hz loop" : $"DAC {index + 1}: one shot, {d.Seconds * 1000:0} ms";
    }

    // ---- saved with the project ----

    public uint Tag => Kind switch { KindOpn => TagOpn, KindOpll => TagOpll, _ => TagPsg };

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Version); w.Write((byte)Kind);
        w.Write(PsgClock); w.Write((byte)PsgVariant);
        foreach (int p in PsgPeriod) w.Write((ushort)p);
        foreach (int v in PsgVolume) w.Write((byte)v);
        w.Write((byte)PsgNoise); w.Write((byte)PsgStereo);
        if (Kind >= KindOpll) w.Write(Opll);
        if (Kind == KindOpn)
        {
            w.Write(OpnClock); w.Write((byte)OpnLfo);
            for (int c = 0; c < 6; c++) { w.Write(OpnPatch[c]); w.Write(OpnKeyed[c]); }
            w.Write((byte)Dac.Length);
            foreach (var d in Dac)
            {
                w.Write(d.Rate); w.Write(d.Data.Length); w.Write(d.LoopStart); w.Write(d.LoopEnd); w.Write((ushort)Math.Min(d.Repeats, 65535)); w.Write(d.CycleHz);
                w.Write(d.Data);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Reads a saved snapshot; null for anything that is not one (a damaged or foreign stream is survived, never trusted).</summary>
    public static SegaSnapshot? FromBytes(byte[] b)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(b));
            if (r.ReadByte() != Version) return null;
            int kind = r.ReadByte();
            if (kind is < KindPsg or > KindOpn) return null;
            int clock = r.ReadInt32(); int variant = r.ReadByte();
            if (clock < 1_000_000 || clock > 10_000_000 || variant > 2) return null;
            var s = new SegaSnapshot(kind, clock, (PsgVariant)variant);
            for (int i = 0; i < 3; i++) s.PsgPeriod[i] = r.ReadUInt16() & 0x3FF;
            for (int i = 0; i < 4; i++) s.PsgVolume[i] = r.ReadByte() & 15;
            s.PsgNoise = r.ReadByte() & 7; s.PsgStereo = r.ReadByte();
            if (kind >= KindOpll) r.Read(s.Opll, 0, 64);
            if (kind == KindOpn)
            {
                s.OpnClock = r.ReadInt32(); s.OpnLfo = r.ReadByte();
                if (s.OpnClock < 1_000_000 || s.OpnClock > 20_000_000) return null;
                for (int c = 0; c < 6; c++) { r.Read(s.OpnPatch[c], 0, OpnPatchBytes); s.OpnKeyed[c] = r.ReadBoolean(); }
                int n = r.ReadByte();
                var list = new List<DacSample>();
                long total = 0;
                for (int i = 0; i < n; i++)
                {
                    int rate = r.ReadInt32(), len = r.ReadInt32(), ls = r.ReadInt32(), le = r.ReadInt32(), reps = r.ReadUInt16(); double hz = r.ReadDouble();
                    if (len is < 1 or > DacSample.MaxLength || (total += len) > 4 * DacSample.MaxLength || rate is < 1000 or > 100000) return null;
                    var data = r.ReadBytes(len);
                    if (data.Length != len) return null;
                    list.Add(new DacSample(rate, data, ls, le, reps) { CycleHz = double.IsFinite(hz) ? hz : 0 });
                }
                s.Dac = list.ToArray();
            }
            return s;
        }
        catch (Exception) { return null; }
    }

    // ---- taking one from the running sound unit ----

    private static void CopyPsg(SegaSnapshot s, Sn76489 psg)
    {
        for (int i = 0; i < 3; i++) s.PsgPeriod[i] = psg.Period(i);
        for (int i = 0; i < 4; i++) s.PsgVolume[i] = psg.Volume(i);
        s.PsgNoise = psg.NoiseControl; s.PsgStereo = psg.StereoMask;
    }

    /// <summary>The Master System / Game Gear stock unit: the PSG's registers.</summary>
    public static SegaSnapshot FromPsg(Sn76489 psg, double clockHz)
    {
        var s = new SegaSnapshot(KindPsg, (int)Math.Round(clockHz), psg.Variant);
        CopyPsg(s, psg);
        return s;
    }

    /// <summary>The Master System FM unit: the PSG's registers and the YM2413's 64.</summary>
    public static SegaSnapshot FromOpll(SmsFmSound unit, double clockHz)
    {
        var s = new SegaSnapshot(KindOpll, (int)Math.Round(clockHz), unit.Psg.Variant);
        CopyPsg(s, unit.Psg);
        for (int i = 0; i < 64; i++) s.Opll[i] = unit.Fm.Register(i);
        return s;
    }

    /// <summary>The Genesis unit: the PSG's registers, the YM2612's six channel patches (read from its registers) with which of them were sounding, and the DAC sounds found in the writes the recorder saw.</summary>
    public static SegaSnapshot FromOpn(MdSoundUnit unit, double masterHz, DacRecorder? recorder)
    {
        var s = new SegaSnapshot(KindOpn, (int)Math.Round(masterHz / 15), unit.Psg.Variant);
        CopyPsg(s, unit.Psg);
        s.OpnClock = (int)Math.Round(masterHz / MdSoundUnit.MasterPerYmClock);
        var ym = unit.Fm;
        s.OpnLfo = ym.Register(0x22);
        int[] off = [0, 8, 4, 12];       // operators 1, 2, 3, 4 in the chip's register order (algorithm order is 1, 2, 3, 4)
        Span<NesEmulator.Sega.HubVoice> voices = stackalloc NesEmulator.Sega.HubVoice[Ym2612.VoiceCount];
        ym.DescribeVoices(voices);
        for (int ch = 0; ch < 6; ch++)
        {
            int part = ch / 3 * 0x100, c = ch % 3;
            var p = s.OpnPatch[ch];
            for (int n = 0; n < 4; n++)
                for (int k = 0; k < 7; k++) p[n * 7 + k] = ym.Register(part + 0x30 + k * 0x10 + off[n] + c);
            p[28] = ym.Register(part + 0xB0 + c); p[29] = ym.Register(part + 0xB4 + c);
            s.OpnKeyed[ch] = voices[ch].Level > 0;
        }
        s.Dac = recorder != null ? DacAnalyzer.Analyze(recorder, masterHz) : [];
        return s;
    }
}

/// <summary>Records the writes a Genesis game makes to the YM2612's DAC data register ($2A): the master clock and the value of each, in a ring of the last 65,536. Fed by the sound unit's port tap; read when Instrument Runaway freezes the game.</summary>
public sealed class DacRecorder
{
    public const int Capacity = 1 << 16;
    private readonly long[] clock = new long[Capacity];
    private readonly byte[] value = new byte[Capacity];
    private long count;
    private int address;

    /// <summary>Total DAC writes seen.</summary>
    public long Count => count;

    /// <summary>The tap: (master clock, YM2612 port 0 to 3, value). Port 0 sets the register address of the first part, port 1 writes its data.</summary>
    public void OnWrite(long master, int port, byte v)
    {
        if (port == 0) address = v;
        else if (port == 1 && address == 0x2A)
        {
            int i = (int)(count & (Capacity - 1));
            clock[i] = master; value[i] = v; count++;
        }
    }

    /// <summary>The recorded writes in order (oldest first), the master clock and the value of each.</summary>
    public (long[] Clocks, byte[] Values) Recent()
    {
        int n = (int)Math.Min(count, Capacity);
        var c = new long[n]; var v = new byte[n];
        for (int i = 0; i < n; i++) { int at = (int)((count - n + i) & (Capacity - 1)); c[i] = clock[at]; v[i] = value[at]; }
        return (c, v);
    }
}

/// <summary>
/// Finds the sounds in a stream of DAC writes. A sound is a stretch of writes at a steady rate (the gaps between writes stay near their median); a gap of more than four times the median, and over 0.8 ms, ends it. Silence at either end
/// is cut away, a stretch that never leaves the middle is no sound, and the same sound played twice (the same first and last bytes and length) is listed once with its count. A sound with a strong cycle in its last part (the
/// normalised autocorrelation of the final few thousand samples peaks above 0.9) loops over whole cycles from its end; its cycle gives its pitch.
/// </summary>
public static class DacAnalyzer
{
    public const int MaxSounds = 12;

    public static DacSample[] Analyze(DacRecorder recorder, double masterHz)
    {
        var (clocks, values) = recorder.Recent();
        return Analyze(clocks, values, masterHz);
    }

    public static DacSample[] Analyze(long[] clocks, byte[] values, double masterHz)
    {
        int n = clocks.Length;
        var found = new List<(DacSample Sample, long Key, long Last)>();
        if (n < 96) return [];
        // 1. the stretches: writes at a steady pace; a gap of more than four times the recent median, and over 0.8 ms, ends one
        int start = 0;
        var window = new long[32]; int filled = 0;
        var sorted = new long[32];
        long limit = (long)(masterHz * 0.0008);
        for (int i = 1; i <= n; i++)
        {
            bool boundary = i == n;
            long gap = 0;
            if (!boundary)
            {
                gap = clocks[i] - clocks[i - 1];
                if (filled >= 8)
                {
                    Array.Copy(window, sorted, filled); Array.Sort(sorted, 0, filled);
                    boundary = gap > 4 * sorted[filled / 2] && gap > limit;
                }
                else boundary = gap > limit && filled >= 2;
            }
            if (boundary) { AddStretch(clocks, values, start, i, masterHz, found); start = i; filled = 0; continue; }
            if (filled < 32) window[filled++] = gap;
            else { Array.Copy(window, 1, window, 0, 31); window[31] = gap; }
        }
        // 2. the most played and the longest first
        var list = found.OrderByDescending(f => f.Sample.Repeats).ThenByDescending(f => f.Sample.Data.Length).Take(MaxSounds).Select(f => f.Sample).ToArray();
        return list;
    }

    private static void AddStretch(long[] clocks, byte[] values, int a, int b, double masterHz, List<(DacSample Sample, long Key, long Last)> found)
    {
        int len = b - a;
        if (len < 96) return;
        // the median gap gives the rate
        var gaps = new long[len - 1];
        for (int i = 1; i < len; i++) gaps[i - 1] = clocks[a + i] - clocks[a + i - 1];
        Array.Sort(gaps);
        long med = Math.Max(1, gaps[gaps.Length / 2]);
        int rate = (int)Math.Clamp(Math.Round(masterHz / med), 2000, 100000);
        // the samples, trimmed of silence at both ends
        int s0 = a, s1 = b;
        while (s0 < s1 && Math.Abs(values[s0] - 128) <= 3) s0++;
        while (s1 > s0 && Math.Abs(values[s1 - 1] - 128) <= 3) s1--;
        int m = s1 - s0;
        if (m < 96) return;
        if (m > DacSample.MaxLength) { s0 = s1 - DacSample.MaxLength; m = DacSample.MaxLength; }
        int lo = 255, hi = 0;
        for (int i = s0; i < s1; i++) { lo = Math.Min(lo, values[i]); hi = Math.Max(hi, values[i]); }
        if (hi - lo < 20) return;
        var data = new byte[m];
        Array.Copy(values, s0, data, 0, m);
        // the same sound again?
        long key = m;
        for (int i = 0; i < Math.Min(64, m); i++) key = key * 1099511628211L + data[i];
        for (int i = Math.Max(0, m - 64); i < m; i++) key = key * 1099511628211L + data[i];
        for (int k = 0; k < found.Count; k++)
            if (found[k].Key == key)
            {
                var old = found[k].Sample;
                found[k] = (new DacSample(old.Rate, old.Data, old.LoopStart, old.LoopEnd, old.Repeats + 1) { CycleHz = old.CycleHz }, key, a);
                return;
            }
        // a cycle at its end? The loop holds a whole number of cycles whose total is a whole number of samples (a sound whose period is not a whole number of samples, 31.3 say,
        // loops on 94 samples = 3 cycles): the pitch of the loop is the cycles over its length, as close to the game's as the samples allow
        int loopStart = -1, loopEnd = -1; double cycleHz = 0;
        if (m >= 256 && FindCycle(data, out double period))
        {
            int cycles = 1, length = Math.Max(2, (int)Math.Round(period));
            double bestErr = double.MaxValue;
            for (int k = 1; k <= 8; k++)
            {
                int n = (int)Math.Round(k * period);
                if (n < 2 || n > m / 2) break;
                double err = Math.Abs(n - k * period) / (k * period);          // relative pitch error of looping on n samples
                if (err < bestErr - 1e-12) { bestErr = err; cycles = k; length = n; }
                if (err < 0.0005) break;                                      // under a mere 0.9 cent: good enough, and the shortest loop wins
            }
            loopEnd = m; loopStart = m - length;
            cycleHz = cycles * (double)rate / length;
        }
        found.Add((new DacSample(rate, data, loopStart, loopEnd, 1) { CycleHz = cycleHz }, key, a));
    }

    /// <summary>The length of the cycle the end of a sound repeats (the first strong peak of its autocorrelation, refined to a fraction of a sample by the parabola through the peak and its neighbours), if there is one strong enough to loop on.</summary>
    internal static bool FindCycle(byte[] data, out double period)
    {
        period = 0;
        int w = Math.Min(data.Length, 4096), from = data.Length - w;
        double mean = 0;
        for (int i = 0; i < w; i++) mean += data[from + i];
        mean /= w;
        var x = new double[w];
        double r0 = 0;
        for (int i = 0; i < w; i++) { x[i] = data[from + i] - mean; r0 += x[i] * x[i]; }
        if (r0 < 1e-6) return false;
        int maxLag = Math.Min(w / 3, 1024);
        var r = new double[maxLag + 2];
        double best = 0;
        for (int lag = 8; lag <= maxLag; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < w; i++) s += x[i] * x[i + lag];
            r[lag] = s / (r0 * (w - lag) / w);          // normalised for the shorter overlap
            best = Math.Max(best, r[lag]);
        }
        if (best < 0.9) return false;
        for (int lag = 8; lag <= maxLag; lag++)
            if (r[lag] >= 0.95 * best && r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1])
            {
                double denom = r[lag - 1] - 2 * r[lag] + r[lag + 1];
                double delta = lag > 8 && Math.Abs(denom) > 1e-12 ? 0.5 * (r[lag - 1] - r[lag + 1]) / denom : 0;
                period = lag + Math.Clamp(delta, -0.5, 0.5);
                return true;
            }
        return false;
    }
}
