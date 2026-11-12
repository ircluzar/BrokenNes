// Instrument extraction for the SNES, the Master System / Game Gear and the Genesis (2026-11-12): the channel layout of each console, what a channel's level and period are, the register block kept at a key-on
// (and the snapshot it becomes, for the same SnesInstruments / SegaInstruments readers Instrument Runaway uses), and the feeds that read the running chips. NES and Game Boy: ExtractFeeds.cs.
//
//   SNES       channels 0-7 = the S-DSP's eight voices. key-on: a write of KON ($4C) with the voice's bit. level = ENVX x the voice volume / 63 (0-255; 0 when the envelope or the volume is 0),
//              period = the 14-bit pitch register, duty = the voice volume max(|VOL L|, |VOL R|). Block: the 128 DSP registers (the other voices cleared) + the voice's sample (the BRR chain from its
//              directory entry to its end block) at its address.
//   Sega PSG   channels 0-2 tones, 3 noise (every Sega console). level = 15 - attenuation, period = the 10-bit period (noise: the control register). key-on: the level rises (the PSG has no key).
//   YM2413     channels 4-12 = FM 1-9 (the Master System FM unit). key-on: the key bit ($20+ch bit 4) goes up. level = 16 - volume while keyed (1-16), 0 keyed off; period = block << 9 | F-number.
//              The rhythm voices (rhythm mode, channels 7-9) are not captured.
//   YM2612     channels 4-9 = FM 1-6 (the Genesis). key-on: a write of $28 with operator bits for the channel. level = 128 - the loudest carrier's total level while keyed, 0 keyed off; period = block << 11 | F-number.
//   DAC        channel 10 (the Genesis): a stretch of frames in which the game writes changing values to $2A with the DAC on; the note's block carries the 8-bit sample it streamed and its rate.
//
// Blocks are opaque bytes in the captured note (CapturedNote.Regs, format 2); MakeInstrument turns one back into the instrument and the preset context.
using BrokenNes2.Instruments;
using BrokenNes2.Presets;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Snes;

namespace BrokenNes2.Extract;

public static class ExtractChips
{
    public const int PsgNoise = 3, Fm1 = 4, GenDac = 10, MaxBlock = 1 << 20;
    public const double PsgClockNtsc = 3579545, OpnClockNtsc = 7670454;

    /// <summary>The video frame rate a console's capture is sampled at (NTSC).</summary>
    public static double FrameHz(ConsoleId c) => c switch { ConsoleId.Snes or ConsoleId.Nes => 60.0988, ConsoleId.GameBoy => 59.7275, _ => 59.9227 };

    public static int ChannelCount(ConsoleId c) => c switch { ConsoleId.Snes => 8, ConsoleId.MasterSystem => 13, ConsoleId.Genesis => 11, _ => 4 };

    public static string ChannelName(ConsoleId c, int ch) => c switch
    {
        ConsoleId.Snes => $"Voice {ch + 1}",
        _ when ch < 3 => $"PSG tone {ch + 1}",
        _ when ch == PsgNoise => "PSG noise",
        ConsoleId.Genesis when ch == GenDac => "DAC",
        _ => $"FM {ch - Fm1 + 1}",
    };

    /// <summary>The frequency of a period on a pitched channel (the SNES: the register itself, only ratios are used).</summary>
    public static double Hz(ConsoleId c, int ch, int period)
    {
        if (c == ConsoleId.Snes) return period;
        if (ch < 3) return PsgClockNtsc / (32.0 * Math.Max(1, period == 0 ? 1024 : period));
        if (c == ConsoleId.MasterSystem) return (period & 511) * (double)(1 << (period >> 9 & 7)) * (PsgClockNtsc / 72.0) / 524288.0;
        return (period & 2047) * ((1 << (period >> 11 & 7)) / 2.0) * (OpnClockNtsc / 144.0) / 1048576.0;
    }

    /// <summary>What the plugin's FM samplers multiply the register frequency by to name a note (the carriers' common multiple): an FM note's key is the register's frequency times it. 1 elsewhere.</summary>
    public static double SoundingMultiple(CapturedNote n)
    {
        try
        {
            if (n.Console == ConsoleId.Genesis && n.Channel >= Fm1 && n.Channel < GenDac && SegaOf(n.Regs) is { } g) return OpnSampler.CarrierFundamental(g.OpnPatch[n.Channel - Fm1]);
            if (n.Console == ConsoleId.MasterSystem && n.Channel >= Fm1 && SegaOf(n.Regs) is { Kind: SegaSnapshot.KindOpll } o)
            {
                int inst = o.Opll[0x30 + n.Channel - Fm1] >> 4;
                return inst == 0 ? OpllSampler.FundamentalOf(o.Opll[0] & 15, o.Opll[1] & 15) : OpllPatchBank.Patches[inst - 1].Fundamental;
            }
        }
        catch { }
        return 1;
    }

    // ---- the sessions (the plugin's ROM mode, the desktop, the Workshop and the tests make their feeds here) ----

    private static NesEmulator.Systems.IConsoleSession Unwrap(NesEmulator.Systems.IConsoleSession s) => s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;

    /// <summary>The console a feed can watch on a session's sound unit: SNES on the S-DSP, Master System / Game Gear on the PSG or the FM unit, Genesis on its sound unit; null otherwise (NES and Game Boy have their own feeds).</summary>
    public static ConsoleId? ConsoleOf(NesEmulator.Systems.IConsoleSession s) => Unwrap(s) switch
    {
        NesEmulator.Systems.SnesSession { Apu: APU_SFC } => ConsoleId.Snes,
        SmsSession { Board.Sound: SmsSound or SmsFmSound } => ConsoleId.MasterSystem,
        MdSession { Board.Sound: MdSoundUnit } => ConsoleId.Genesis,
        _ => null,
    };

    /// <summary>A new feed on a session's sound unit (see <see cref="ConsoleOf"/>), or null.</summary>
    public static IExtractFeed? FeedFor(NesEmulator.Systems.IConsoleSession s)
    {
        switch (Unwrap(s))
        {
            case NesEmulator.Systems.SnesSession { Apu: APU_SFC spc }: return new SnesExtractFeed(spc);
            case SmsSession sms: { var t = sms.Board.Timeline; return SmsExtractFeed.Of(sms.Board.Sound, t.MasterHz / t.Z80.Divider); }
            case MdSession { Board.Sound: MdSoundUnit unit } md: return new GenesisExtractFeed(unit, md.Board.Timeline.MasterHz);
        }
        return null;
    }

    /// <summary>The channels a session's feed records that make sense to offer (a Master System without the FM unit: the PSG's four).</summary>
    public static int OfferedChannels(NesEmulator.Systems.IConsoleSession s) => Unwrap(s) switch
    {
        SmsSession { Board.Sound: SmsFmSound } => 13,
        SmsSession => 4,
        var u => ConsoleOf(u) is { } c ? ChannelCount(c) : 0,
    };

    // ---- the key-on blocks ----

    /// <summary>The SNES block of voice <paramref name="voice"/>: u8 voice, the 128 DSP registers (the other voices' cleared, so the instrument's envelope is this voice's), u16 start, u16 loop (the directory entry), u16 length, the sample bytes.</summary>
    public static byte[] SnesBlock(byte[] aram, byte[] dsp, int voice)
    {
        var regs = (byte[])dsp.Clone();
        for (int v = 0; v < 8; v++) if (v != voice) for (int r = 0; r < 10; r++) regs[v * 16 + r] = 0;
        regs[0x4C] = 0; regs[0x5C] = 0;
        int srcn = dsp[voice * 16 + 4], dir = dsp[0x5D] * 0x100 + srcn * 4;
        int start = aram[dir & 0xFFFF] | aram[(dir + 1) & 0xFFFF] << 8, loop = aram[(dir + 2) & 0xFFFF] | aram[(dir + 3) & 0xFFFF] << 8;
        int len = 0;
        for (int p = start; len < 0x8000 && p + 9 <= 0x10000; p += 9) { len += 9; if ((aram[p] & 1) != 0) break; }
        var o = new List<byte>(140 + len) { (byte)voice };
        o.AddRange(regs);
        o.Add((byte)start); o.Add((byte)(start >> 8)); o.Add((byte)loop); o.Add((byte)(loop >> 8)); o.Add((byte)len); o.Add((byte)(len >> 8));
        o.AddRange(aram.AsSpan(start, len).ToArray());
        return o.ToArray();
    }

    /// <summary>The snapshot a SNES block stands for: audio RAM with the sample and its directory entry (start and loop as the game had them) and the DSP registers.</summary>
    private const int SnesHead = 135;

    public static SpcSnapshot? SnesOf(byte[] b, out int voice)
    {
        voice = -1;
        if (b.Length < SnesHead) return null;
        voice = b[0] & 7;
        var dsp = b.AsSpan(1, 128).ToArray();
        int start = b[129] | b[130] << 8, loop = b[131] | b[132] << 8, len = b[133] | b[134] << 8;
        if (SnesHead + len != b.Length || start + len > SpcSnapshot.AramSize) return null;
        var aram = new byte[SpcSnapshot.AramSize];
        Array.Copy(b, SnesHead, aram, start, len);
        int dir = dsp[0x5D] * 0x100 + dsp[voice * 16 + 4] * 4;
        if (dir + 3 < SpcSnapshot.AramSize && (dir + 4 <= start || dir >= start + len)) { aram[dir] = (byte)start; aram[dir + 1] = (byte)(start >> 8); aram[dir + 2] = (byte)loop; aram[dir + 3] = (byte)(loop >> 8); }
        return new SpcSnapshot(aram, dsp);
    }

    /// <summary>The Sega block: u8 kind, i32 PSG clock, u8 variant, i32 YM2612 clock, the PSG (3 x u16 period, 4 volumes, noise, stereo), u8 YM2612 LFO, 64 YM2413 registers, 6 x 30 YM2612 patch bytes,
    /// u8 keyed mask, then a DAC sample: i32 rate, i32 length, the bytes (length 0: none).</summary>
    public static byte[] SegaBlock(SegaSnapshot s, DacSample? dac = null)
    {
        var o = new List<byte>(300) { (byte)s.Kind };
        o.AddRange(BitConverter.GetBytes(s.PsgClock)); o.Add((byte)s.PsgVariant); o.AddRange(BitConverter.GetBytes(s.OpnClock));
        for (int i = 0; i < 3; i++) { o.Add((byte)s.PsgPeriod[i]); o.Add((byte)(s.PsgPeriod[i] >> 8)); }
        for (int i = 0; i < 4; i++) o.Add((byte)s.PsgVolume[i]);
        o.Add((byte)s.PsgNoise); o.Add((byte)s.PsgStereo); o.Add((byte)s.OpnLfo);
        o.AddRange(s.Opll);
        int mask = 0;
        for (int c = 0; c < 6; c++) { o.AddRange(s.OpnPatch[c]); if (s.OpnKeyed[c]) mask |= 1 << c; }
        o.Add((byte)mask);
        o.AddRange(BitConverter.GetBytes(dac?.Rate ?? 0)); o.AddRange(BitConverter.GetBytes(dac?.Data.Length ?? 0));
        if (dac != null) o.AddRange(dac.Data);
        return o.ToArray();
    }

    private const int SegaFixed = 1 + 4 + 1 + 4 + 6 + 4 + 3 + 64 + 180 + 1 + 8;

    public static SegaSnapshot? SegaOf(byte[] b)
    {
        if (b.Length < SegaFixed) return null;
        int kind = b[0];
        if (kind is < SegaSnapshot.KindPsg or > SegaSnapshot.KindOpn || b[5] > 2) return null;
        var s = new SegaSnapshot(kind, BitConverter.ToInt32(b, 1), (PsgVariant)b[5]) { OpnClock = BitConverter.ToInt32(b, 6) };
        int p = 10;
        for (int i = 0; i < 3; i++, p += 2) s.PsgPeriod[i] = b[p] | b[p + 1] << 8;
        for (int i = 0; i < 4; i++) s.PsgVolume[i] = b[p++];
        s.PsgNoise = b[p++]; s.PsgStereo = b[p++]; s.OpnLfo = b[p++];
        Array.Copy(b, p, s.Opll, 0, 64); p += 64;
        for (int c = 0; c < 6; c++) { Array.Copy(b, p, s.OpnPatch[c], 0, SegaSnapshot.OpnPatchBytes); p += SegaSnapshot.OpnPatchBytes; }
        int mask = b[p++];
        for (int c = 0; c < 6; c++) s.OpnKeyed[c] = (mask >> c & 1) != 0;
        int rate = BitConverter.ToInt32(b, p), len = BitConverter.ToInt32(b, p + 4); p += 8;
        if (len < 0 || p + len != b.Length) return null;
        if (len > 0) s.Dac = [new DacSample(rate, b.AsSpan(p, len).ToArray(), -1, -1, 1)];
        return s;
    }

    /// <summary>The instrument a captured note of these consoles plays (read by the Runaway readers from the snapshot of its block) and the preset context; null with the reason.</summary>
    public static Instrument? MakeInstrument(CapturedNote n, out byte[] context, out string? problem)
    {
        context = []; problem = null;
        if (n.Console == ConsoleId.Snes)
        {
            var snap = SnesOf(n.Regs, out int voice);
            if (snap == null) { problem = "the key-on registers are damaged"; return null; }
            int srcn = snap.Dsp[voice * 16 + 4];
            var inst = new SnesInstruments(snap).Read(srcn);
            if (inst == null) { problem = $"sample {srcn} is not a well-formed BRR sample (no end block, or a loop outside it)"; return null; }
            context = PresetContext.Of(snap, inst);
            return inst;
        }
        var s = SegaOf(n.Regs);
        if (s == null) { problem = "the key-on registers are damaged"; return null; }
        int index;
        if (n.Channel < 3) index = s.PsgInstrument;
        else if (n.Channel == PsgNoise) index = s.PsgInstrument + ((s.PsgNoise & 4) != 0 ? 1 : 2);
        else if (n.Console == ConsoleId.Genesis && n.Channel == GenDac)
        {
            if (s.Dac.Length == 0) { problem = "the DAC stream was not recorded to its end"; return null; }
            index = 6;
        }
        else if (s.Kind == SegaSnapshot.KindOpn) index = n.Channel - Fm1;
        else if (s.Kind == SegaSnapshot.KindOpll) index = s.Opll[0x30 + n.Channel - Fm1] >> 4;
        else { problem = "this Master System has no FM unit"; return null; }
        var i2 = new SegaInstruments(s).Read(index);
        if (i2 == null) { problem = "the channel has no instrument"; return null; }
        context = PresetContext.Of(s, i2);
        return i2;
    }

    /// <summary>A copy of the note whose key-on block keeps the registers but not the sample (the SNES voice's BRR, the DAC stream): what the preset's note behaviour holds; the instrument has the sample.</summary>
    public static CapturedNote Slim(CapturedNote n)
    {
        var r = n.Regs;
        if (n.Console == ConsoleId.Snes && r.Length > SnesHead) { r = r[..SnesHead]; r[SnesHead - 2] = r[SnesHead - 1] = 0; }
        else if (n.Console is ConsoleId.Genesis or ConsoleId.MasterSystem && r.Length > SegaFixed) { r = r[..SegaFixed]; Array.Clear(r, SegaFixed - 8, 8); }
        return new CapturedNote { Console = n.Console, Channel = n.Channel, KeyOnFrame = n.KeyOnFrame, End = n.End, Regs = r, Life = n.Life };
    }

    // ---- playback helpers (the plugin) ----

    /// <summary>The level ratio of frame <paramref name="i"/> to the key-on frame, as a velocity factor in the chip's own steps (PSG 2 dB, YM2413 3 dB, YM2612 0.75 dB per step; the SNES the voice volume, linear).</summary>
    public static double LevelFactor(CapturedNote n, int i)
    {
        if (n.Life.Length == 0) return 1;
        var f = n.Life[Math.Clamp(i, 0, n.Life.Length - 1)];
        var k = n.Life[0];
        if (n.Console == ConsoleId.Snes) return k.Duty > 0 ? Math.Min(1.0, f.Duty / (double)k.Duty) : 1;
        if (n.Console == ConsoleId.Genesis && n.Channel == GenDac) return f.Level > 0 ? 1 : 0;
        double stepDb = n.Channel <= PsgNoise ? 2 : n.Console == ConsoleId.MasterSystem ? 3 : 0.75;
        return f.Level == 0 ? 0 : Math.Pow(10, -(stepDb * (k.Level - f.Level)) / 20.0);
    }

    /// <summary>A PSG noise note's shift rate as the frequency the plugin's PSG sampler takes for its noise at FL's key 60 (it clocks the noise from tone 3: a rate of N plays as a tone period of N):
    /// the captured control register's rate (16, 32 or 64), or tone 3's period when the game had the noise follow it. 0 for other channels.</summary>
    public static double NoiseHz(CapturedNote n, int i)
    {
        if (n.Console is not (ConsoleId.MasterSystem or ConsoleId.Genesis) || n.Channel != PsgNoise || n.Life.Length == 0) return 0;
        int ctrl = n.Life[Math.Clamp(i, 0, n.Life.Length - 1)].Period;
        int period = (ctrl & 3) == 3 ? (n.Regs.Length > 15 ? n.Regs[14] | n.Regs[15] << 8 : 16) : 16 << (ctrl & 3);      // (tone 3 read from the block in place: no allocation on the audio thread)
        return PsgClockNtsc / (32.0 * 16 * Math.Max(1, period));
    }

    /// <summary>The absolute level of the key-on frame as a velocity (the PSG's attenuation, the FM channel's volume / carrier level), so FL's full velocity plays as loud as the game keyed it.</summary>
    public static double KeyVelocity(CapturedNote n)
    {
        if (n.Life.Length == 0 || n.Console == ConsoleId.Snes) return 1;
        if (n.Console == ConsoleId.Genesis && n.Channel == GenDac) return 1;
        int lv = n.Life[0].Level;
        if (n.Channel <= PsgNoise) return Math.Pow(10, -2.0 * (15 - lv) / 20.0);
        if (n.Console == ConsoleId.MasterSystem) return Math.Pow(10, -3.0 * (16 - lv) / 20.0);
        return 1;      // (a YM2612 patch keeps its carriers' levels: the key-on level is in it)
    }

    // ---- the per-frame rules ----

    /// <summary>A PSG channel's frame. The PSG has no key: a new note is a level that comes up from silence (the recorder's own rule), jumps up by 3 steps or more (6 dB), or comes up while the period
    /// changes; a driver's attack ramp or tremolo (a step or two at a time on the same pitch) stays one note.</summary>
    public static CaptureFrame Psg(Sn76489 psg, int ch, CaptureFrame prev, bool first)
    {
        int level = 15 - psg.Volume(ch);
        int period = ch < 3 ? psg.Period(ch) : psg.NoiseControl;
        bool key = !first && level > 0 && (level - prev.Level >= 3 || level > prev.Level && period != prev.Period);
        return new CaptureFrame { Level = (byte)level, Period = (ushort)period, Duty = (byte)(ch == 3 ? psg.NoiseControl >> 2 & 1 : 0), KeyOn = key };
    }

    public static CaptureFrame Opll(Ym2413 fm, int ch, bool wasKeyed)
    {
        int r20 = fm.Register(0x20 + ch);
        bool key = (r20 & 0x10) != 0 && !((fm.Register(0x0E) & 0x20) != 0 && ch >= 6);
        int vol = fm.Register(0x30 + ch) & 15, period = (r20 >> 1 & 7) << 9 | (r20 & 1) << 8 | fm.Register(0x10 + ch);
        return new CaptureFrame { Level = (byte)(key ? 16 - vol : 0), Period = (ushort)period, KeyOn = key && !wasKeyed };
    }

    private static readonly int[] OpOff = [0, 8, 4, 12];

    public static CaptureFrame Opn(Ym2612 ym, int ch, bool keyed, bool keyWrite)
    {
        int part = ch / 3 * 0x100, c = ch % 3;
        int mask = FmPatchBank.CarriersOf(ym.Register(part + 0xB0 + c) & 7), tl = 127;
        for (int op = 0; op < 4; op++) if ((mask >> op & 1) != 0) tl = Math.Min(tl, ym.Register(part + 0x40 + OpOff[op] + c) & 0x7F);
        int a4 = ym.Register(part + 0xA4 + c), period = (a4 >> 3 & 7) << 11 | (a4 & 7) << 8 | ym.Register(part + 0xA0 + c);
        return new CaptureFrame { Level = (byte)(keyed ? 128 - tl : 0), Period = (ushort)period, KeyOn = keyWrite && keyed };
    }
}

/// <summary>The SNES: the S-DSP's eight voices, keyed by KON writes (the sound unit's DSP write tap).</summary>
public sealed class SnesExtractFeed : IExtractFeed
{
    private readonly APU_SFC apu;
    private readonly Action<byte, byte, long>? before;
    private int keyed;
    private readonly CaptureFrame[] row = new CaptureFrame[8];
    private readonly Func<int, byte[]> block;
    public InstrumentRecorder Recorder { get; } = new(ConsoleId.Snes);

    public SnesExtractFeed(APU_SFC apu)
    {
        this.apu = apu;
        before = apu.DspWriteTap;
        apu.DspWriteTap = (r, v, t) => { before?.Invoke(r, v, t); if (r == 0x4C) keyed |= v; };
        block = v => ExtractChips.SnesBlock(apu.Aram, apu.Dsp.Regs, v);
    }

    public void EndFrame()
    {
        var d = apu.Dsp.Regs;
        for (int v = 0; v < 8; v++)
        {
            int r = v * 16, envx = d[r + 8] & 0x7F, vol = Math.Min(127, Math.Max(Math.Abs((int)(sbyte)d[r]), Math.Abs((int)(sbyte)d[r + 1])));
            int level = envx > 0 && vol > 0 ? Math.Clamp(envx * vol / 63, 1, 255) : 0;
            row[v] = new CaptureFrame { Level = (byte)level, Period = (ushort)((d[r + 2] | d[r + 3] << 8) & 0x3FFF), Duty = (byte)vol, KeyOn = (keyed >> v & 1) != 0 };
        }
        keyed = 0;
        Recorder.Frame(row, block);
    }

    public void Detach() => apu.DspWriteTap = before;
}

/// <summary>The Master System and the Game Gear: the PSG, and the YM2413 when the FM unit is there. Read from the chips' registers at each frame's end (the PSG has no key: a note starts where its level rises).</summary>
public sealed class SmsExtractFeed : IExtractFeed
{
    private readonly Sn76489 psg;
    private readonly Ym2413? fm;
    private readonly int clock;
    private readonly CaptureFrame[] row = new CaptureFrame[13];
    private readonly CaptureFrame[] prev = new CaptureFrame[4];
    private bool started;
    private readonly bool[] fmKeyed = new bool[9];
    private readonly Func<int, byte[]> block;
    public InstrumentRecorder Recorder { get; } = new(ConsoleId.MasterSystem);

    public SmsExtractFeed(Sn76489 psg, Ym2413? fm, double clockHz)
    {
        this.psg = psg; this.fm = fm; clock = (int)Math.Round(clockHz);
        block = _ => ExtractChips.SegaBlock(Snapshot());
    }

    /// <summary>The feed of a Master System / Game Gear sound unit (the PSG alone or the FM unit), or null for another one.</summary>
    public static SmsExtractFeed? Of(ISmsSound sound, double clockHz) => sound switch
    {
        SmsFmSound f => new SmsExtractFeed(f.Psg, f.Fm, clockHz),
        SmsSound s => new SmsExtractFeed(s.Psg, null, clockHz),
        _ => null,
    };

    private SegaSnapshot Snapshot()
    {
        var s = new SegaSnapshot(fm != null ? SegaSnapshot.KindOpll : SegaSnapshot.KindPsg, clock, psg.Variant);
        for (int i = 0; i < 3; i++) s.PsgPeriod[i] = psg.Period(i);
        for (int i = 0; i < 4; i++) s.PsgVolume[i] = psg.Volume(i);
        s.PsgNoise = psg.NoiseControl; s.PsgStereo = psg.StereoMask;
        if (fm != null) for (int i = 0; i < 64; i++) s.Opll[i] = fm.Register(i);
        return s;
    }

    public void EndFrame()
    {
        for (int c = 0; c < 4; c++) { row[c] = ExtractChips.Psg(psg, c, prev[c], !started); prev[c] = row[c]; }
        started = true;
        for (int c = 0; c < 9; c++)
        {
            if (fm == null) { row[ExtractChips.Fm1 + c] = default; continue; }
            row[ExtractChips.Fm1 + c] = ExtractChips.Opll(fm, c, fmKeyed[c]);
            fmKeyed[c] = row[ExtractChips.Fm1 + c].Level > 0;
        }
        Recorder.Frame(row, block);
    }

    public void Detach() { }
}

/// <summary>The Genesis: the YM2612 (keyed by $28 writes), its DAC (the stream of $2A writes) and the PSG, through the sound unit's port tap.</summary>
public sealed class GenesisExtractFeed : IExtractFeed
{
    private readonly MdSoundUnit unit;
    private readonly double masterHz;
    private readonly Action<long, int, byte>? before;
    private readonly CaptureFrame[] row = new CaptureFrame[11];
    private readonly CaptureFrame[] prev = new CaptureFrame[4];
    private bool started;
    private readonly bool[] key = new bool[6], keyWrite = new bool[6];
    private int addr0, addr1;
    private bool dacOn;
    private int dacWrites, dacMin = 255, dacMax;
    private readonly List<byte> stream = new();
    private long streamFirst, streamLast;
    private int streamKey = -1;
    private bool dacWasOn;
    private readonly Func<int, byte[]> block;
    public InstrumentRecorder Recorder { get; } = new(ConsoleId.Genesis);

    public GenesisExtractFeed(MdSoundUnit unit, double masterHz)
    {
        this.unit = unit; this.masterHz = masterHz;
        before = unit.YmPortTap;
        unit.YmPortTap = (m, port, v) => { before?.Invoke(m, port, v); OnWrite(m, port, v); };
        block = _ => ExtractChips.SegaBlock(SegaSnapshot.FromOpn(unit, masterHz, null));
    }

    private void OnWrite(long master, int port, byte v)
    {
        switch (port)
        {
            case 0: addr0 = v; break;
            case 2: addr1 = v; break;
            case 1:
                if (addr0 == 0x28)
                {
                    int c = v & 3; if (c == 3) break;
                    int ch = c + ((v & 4) != 0 ? 3 : 0);
                    bool on = (v & 0xF0) != 0;
                    if (on) keyWrite[ch] = true;
                    key[ch] = on;
                }
                else if (addr0 == 0x2B) dacOn = (v & 0x80) != 0;
                else if (addr0 == 0x2A)
                {
                    dacWrites++; dacMin = Math.Min(dacMin, v); dacMax = Math.Max(dacMax, v);
                    if (stream.Count < DacSample.MaxLength) { if (stream.Count == 0) streamFirst = master; stream.Add(v); streamLast = master; }
                }
                break;
        }
    }

    public void EndFrame()
    {
        var psg = unit.Psg;
        for (int c = 0; c < 4; c++) { row[c] = ExtractChips.Psg(psg, c, prev[c], !started); prev[c] = row[c]; }
        started = true;
        for (int c = 0; c < 6; c++)
        {
            row[ExtractChips.Fm1 + c] = ExtractChips.Opn(unit.Fm, c, key[c] && !(c == 5 && dacOn), keyWrite[c]);
            keyWrite[c] = false;
        }
        bool dac = dacOn && dacWrites > 0 && dacMax - dacMin >= 4;
        row[ExtractChips.GenDac] = new CaptureFrame { Level = (byte)(dac ? 15 : 0) };
        int frame = Recorder.FrameCount;
        if (!dac && dacWasOn) FinishStream();
        Recorder.Frame(row, block);
        if (dac && !dacWasOn) streamKey = frame;          // (the stream holds this frame's writes: the sound starts with them)
        else if (!dac) stream.Clear();
        dacWasOn = dac;
        dacWrites = 0; dacMin = 255; dacMax = 0;
    }

    /// <summary>The stream that just ended becomes the DAC note's sample (its rate from the master clock of its first and last write).</summary>
    private void FinishStream()
    {
        if (streamKey < 0 || stream.Count < 16) { streamKey = -1; stream.Clear(); return; }
        double seconds = (streamLast - streamFirst) / masterHz;
        int rate = seconds > 0 ? (int)Math.Round((stream.Count - 1) / seconds) : 8000;
        var snap = SegaSnapshot.FromOpn(unit, masterHz, null);
        Recorder.SetKeyBlock(ExtractChips.GenDac, streamKey, ExtractChips.SegaBlock(snap, new DacSample(rate, stream.ToArray(), -1, -1, 1)));
        streamKey = -1;
        stream.Clear();
    }

    public void Detach() { if (dacWasOn) FinishStream(); unit.YmPortTap = before; }
}
