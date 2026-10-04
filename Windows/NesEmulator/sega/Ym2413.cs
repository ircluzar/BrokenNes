using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The Yamaha YM2413 (OPLL): nine two-operator FM channels (or six plus five rhythm voices), the 15 built-in instruments plus one user instrument, vibrato and tremolo, envelope and key scaling,
/// operator feedback, and a per-channel mute mask. It is on the Japanese Master System and the Mark III behind ports <c>$F0</c> (address), <c>$F1</c> (data) and <c>$F2</c> (audio control, see <see cref="SmsFmSound"/>).
/// </summary>
/// <remarks>
/// <para><b>Ported from emu2413 v1.6.2</b> (Mitsutaka Okazaki, MIT; local copy <c>port-sources\emu2413</c>): the slot and channel structure, the phase generator, the envelope generator with its rate and step
/// tables, the key-scale and total-level table, the tremolo (amplitude LFO) pattern, the pitch-modulation table, the noise shift register and the rhythm voices' phase tricks, and the 8-byte patch format of the instrument ROM.
/// The ledger (<c>sega/THIRD_PARTY_NOTICES.md</c>) says what changed, all of it measured against Nuked-OPLL (<c>Workshop/Sega/FmOracleTestCli.cs</c>, <c>--fm-oracle-test</c>): the key-scale level is computed in integer envelope steps (emu2413
/// truncated it twice), the silent half of a half-sine wave keeps its sign bit, a key-on at the largest attenuation starts its attack without waiting for the envelope clock, the vibrato and tremolo counters start one sample behind the reset,
/// each envelope phase runs on a clock with its own offset (<see cref="EnvelopeClock"/>), five of the fifteen instruments carry the oracle's bytes instead of emu2413's, a masked channel is silent instead of frozen at its last value, switching
/// rhythm mode clears the channels it stops using, the slots keep their instrument as an index and their waveform as a number (so the whole chip can be saved and loaded), and the amplitude phase wraps at the length of its pattern. The VRC7 and
/// YMF281B tone sets, the stereo panning and emu2413's own sample-rate converter were not taken (the rate conversion is <see cref="SmsFmSound"/>'s).</para>
/// <para>Against the oracle, sample for sample: steady tones, all multipliers, feedback, waveforms, levels, key scaling, tremolo and vibrato are <b>exact</b> on every channel; the envelope and the instruments are exact on most samples and within
/// 0.5 dB in level on average (the oracle's fast envelope patterns repeat every 15 samples in places, which this chip's binary counter cannot follow); the bass drum and tom match, the snare, cymbal and hi-hat match in level and not in
/// noise. See the record, <c>docs/projects/sega-track-a.md</c>, for the numbers.</para>
/// <para>One call to <see cref="Calc"/> is one chip sample: 72 clocks of the chip's clock (49,716 Hz from the Z80 clock of an NTSC console). The mixed output is the sum of the 14 channel outputs (nine melodic or six melodic and
/// five rhythm), as emu2413 has it: a full-volume channel peaks at about 2,042, nine at once near 18,400.</para>
/// <para>Written for the stems and the plugin: <see cref="Mask"/> mutes any of the 14 outputs, <see cref="ChannelOutput"/> reads them one by one, and <see cref="DescribeVoices"/> says what the chip is playing.</para>
/// </remarks>
public sealed partial class Ym2413 : IHubVoiceSource
{
    /// <summary>One chip sample is this many chip clocks.</summary>
    public const int ClocksPerSample = 72;

    /// <summary>The mask bit of melodic channel <paramref name="channel"/> (0 to 8).</summary>
    public static uint MaskChannel(int channel) => 1u << channel;
    public const uint MaskHiHat = 1u << 9, MaskCymbal = 1u << 10, MaskTom = 1u << 11, MaskSnare = 1u << 12, MaskBass = 1u << 13;
    public const uint MaskRhythm = MaskHiHat | MaskCymbal | MaskTom | MaskSnare | MaskBass;

    // phase generator: 19-bit phase, 10-bit sine table
    private const int DpBits = 19, DpWidth = 1 << DpBits, PgBits = 10, PgWidth = 1 << PgBits, DpBaseBits = DpBits - PgBits;
    // envelope: 7-bit attenuation in 0.375 dB steps; 127 is mute and 123 the largest level that is still heard
    private const int EgMute = 127, EgMax = EgMute - 4;
    private const double EgStep = 0.375;
    private const int DamperRate = 12;   // speed of the damp phase before a key-on (key scale affects it)

    private const int Attack = 0, Decay = 1, Sustain = 2, Release = 3, Damp = 4;
    private const int SlotBd1 = 12, SlotBd2 = 13, SlotHh = 14, SlotSd = 15, SlotTom = 16, SlotCym = 17;

    private const int UpdateWs = 1, UpdateTll = 2, UpdateRks = 4, UpdateEg = 8, UpdateAll = 255;

    // ---------------------------------------------------------------------------------------- tables (built once)

    private static readonly ushort[] FullSin = new ushort[PgWidth];
    private static readonly ushort[] HalfSin = new ushort[PgWidth];
    private static readonly ushort[][] WaveTables = { FullSin, HalfSin };

    // offset to fnum, a rough approximation of 14 cents of vibrato depth, by the top three bits of fnum and the eight LFO steps
    private static readonly sbyte[,] PmTable =
    {
        { 0, 0, 0, 0, 0, 0, 0, 0 },
        { 0, 0, 1, 0, 0, 0, -1, 0 },
        { 0, 1, 2, 1, 0, -1, -2, -1 },
        { 0, 1, 3, 1, 0, -1, -3, -1 },
        { 0, 2, 4, 2, 0, -2, -4, -2 },
        { 0, 2, 5, 2, 0, -2, -5, -2 },
        { 0, 3, 6, 3, 0, -3, -6, -3 },
        { 0, 3, 7, 3, 0, -3, -7, -3 },
    };

    // envelope step patterns by the low two bits of the rate offset (andete's measurements)
    private static readonly byte[,] EgStepTables =
    {
        { 0, 1, 0, 1, 0, 1, 0, 1 },
        { 0, 1, 0, 1, 1, 1, 0, 1 },
        { 0, 1, 1, 1, 0, 1, 1, 1 },
        { 0, 1, 1, 1, 1, 1, 1, 1 },
    };

    // frequency multiplier, times two
    private static readonly uint[] MlTable = { 1, 1 * 2, 2 * 2, 3 * 2, 4 * 2, 5 * 2, 6 * 2, 7 * 2, 8 * 2, 9 * 2, 10 * 2, 10 * 2, 12 * 2, 12 * 2, 15 * 2, 15 * 2 };

    // key-scale level by the top four bits of fnum, in envelope steps (0.375 dB) at block 7: 0, 9, 12, 13.875, 15, 16.125, 16.875, 17.625, 18, 18.75, 19.125, 19.5, 19.875, 20.25, 20.625 and 21 dB
    private static readonly int[] KslRom = { 0, 24, 32, 37, 40, 43, 45, 47, 48, 50, 51, 52, 53, 54, 55, 56 };

    private static readonly ushort[,] TllTable = new ushort[8 * 16, 4];
    private static readonly int[,] RksTable = new int[8 * 2, 2];
    private static readonly Patch[] RomPatchSet = new Patch[19 * 2];   // [0, 1] the (empty) user patch, [2 .. 31] instruments 1-15, [32 .. 37] the rhythm patches

    static Ym2413()
    {
        for (int x = 0; x < PgWidth / 4; x++) FullSin[x] = FullSinQuarter[x];
        for (int x = 0; x < PgWidth / 4; x++) FullSin[PgWidth / 4 + x] = FullSin[PgWidth / 4 - x - 1];
        for (int x = 0; x < PgWidth / 2; x++) FullSin[PgWidth / 2 + x] = (ushort)(0x8000 | FullSin[x]);
        for (int x = 0; x < PgWidth / 2; x++) HalfSin[x] = FullSin[x];
        for (int x = PgWidth / 2; x < PgWidth; x++) HalfSin[x] = 0x8fff;   // the silent half of a half-sine wave keeps the sign bit: a "negative zero" (-1 in the chip's one's-complement output), which is what the oracle shows

        // key-scale attenuation in envelope steps: the level at this octave (the block-7 level less 3 dB, 8 steps, per octave down, never below 0), then 0 / 1.5 / 3 / 6 dB per octave by the instrument's KL field
        for (int fnum = 0; fnum < 16; fnum++)
            for (int block = 0; block < 8; block++)
            {
                int level = Math.Max(0, KslRom[fnum] - 8 * (7 - block));
                TllTable[(block << 4) | fnum, 0] = 0;
                TllTable[(block << 4) | fnum, 1] = (ushort)(level >> 1);
                TllTable[(block << 4) | fnum, 2] = (ushort)level;
                TllTable[(block << 4) | fnum, 3] = (ushort)(level * 2);
            }
        for (int fnum8 = 0; fnum8 < 2; fnum8++)
            for (int block = 0; block < 8; block++)
            {
                RksTable[(block << 1) | fnum8, 1] = (block << 1) + fnum8;
                RksTable[(block << 1) | fnum8, 0] = block >> 1;
            }
        for (int i = 0; i < 19; i++)
        {
            RomPatchSet[i * 2] = new Patch(); RomPatchSet[i * 2 + 1] = new Patch();
            DumpToPatch(RomPatches, i * 8, RomPatchSet[i * 2], RomPatchSet[i * 2 + 1]);
        }
    }

    // ---------------------------------------------------------------------------------------- data

    /// <summary>One instrument half (a modulator or a carrier): the 13 parameters of the patch format.</summary>
    private sealed class Patch
    {
        public int TL, FB, EG, ML, AR, DR, SL, RR, KR, KL, AM, PM, WS;
        public void CopyFrom(Patch o) { TL = o.TL; FB = o.FB; EG = o.EG; ML = o.ML; AR = o.AR; DR = o.DR; SL = o.SL; RR = o.RR; KR = o.KR; KL = o.KL; AM = o.AM; PM = o.PM; WS = o.WS; }
    }

    private static void DumpToPatch(byte[] d, int o, Patch m, Patch c)
    {
        m.AM = (d[o] >> 7) & 1; c.AM = (d[o + 1] >> 7) & 1;
        m.PM = (d[o] >> 6) & 1; c.PM = (d[o + 1] >> 6) & 1;
        m.EG = (d[o] >> 5) & 1; c.EG = (d[o + 1] >> 5) & 1;
        m.KR = (d[o] >> 4) & 1; c.KR = (d[o + 1] >> 4) & 1;
        m.ML = d[o] & 15; c.ML = d[o + 1] & 15;
        m.KL = (d[o + 2] >> 6) & 3; c.KL = (d[o + 3] >> 6) & 3;
        m.TL = d[o + 2] & 63; c.TL = 0;
        m.FB = d[o + 3] & 7; c.FB = 0;
        m.WS = (d[o + 3] >> 3) & 1; c.WS = (d[o + 3] >> 4) & 1;
        m.AR = (d[o + 4] >> 4) & 15; c.AR = (d[o + 5] >> 4) & 15;
        m.DR = d[o + 4] & 15; c.DR = d[o + 5] & 15;
        m.SL = (d[o + 6] >> 4) & 15; c.SL = (d[o + 7] >> 4) & 15;
        m.RR = d[o + 6] & 15; c.RR = d[o + 7] & 15;
    }

    /// <summary>One operator: the phase generator, the envelope generator and the last two outputs (the feedback).</summary>
    private sealed class Slot
    {
        public int Number;
        /// <summary>Bit 0: carrier. Bit 1: single-slot mode (a rhythm voice that stands alone: snare, tom, hi-hat, cymbal).</summary>
        public int Type;
        public Patch Patch = NullPatch;
        public int PatchIndex = -1;       // the slot's patch in <c>patches</c> (-1: none yet)
        public int Output0, Output1;      // latest and previous output
        public int WaveSel;               // 0 full sine, 1 half sine
        public uint PgPhase, PgOut;
        public bool PgKeep;               // the phase is kept at key-on (the rhythm slots that share a phase)
        public int BlkFnum, Fnum, Blk;
        public int EgState;
        public bool DampSkip;             // keyed on while the envelope was already at its largest attenuation: nothing to damp, so the attack does not wait for the envelope clock
        public int Volume;                // the carrier's 4-bit volume register, shifted left two
        public bool KeyFlag, SusFlag;
        public int Tll;                   // total level plus the key-scale level
        public int Rks;                   // key-scale offset of the envelope speed
        public int EgRateH, EgRateL, EgShift;
        public uint EgOut;
        public int UpdateRequests;
    }

    private static readonly Patch NullPatch = new();

    private readonly double clockHz;
    private readonly Patch[] patches = new Patch[19 * 2];
    private readonly Slot[] slot = new Slot[18];
    private readonly byte[] reg = new byte[0x40];
    private readonly int[] patchNumber = new int[9];
    private readonly short[] chOut = new short[14];   // 0-8 melodic, 9 bass drum, 10 hi-hat, 11 snare, 12 tom, 13 cymbal
    private uint adr;
    private int testFlag;
    private uint slotKeyStatus;
    private bool rhythmMode;
    private uint egCounter;
    private uint pmPhase;
    private int amPhase;           // wraps at the length of the tremolo pattern times 64
    private int lfoAm;
    private uint noise;
    private bool shortNoise;
    private uint mask;

    private const int AmPeriod = 210 * 64;
    private const uint EgStartOffset = 0;   // the envelope counts samples from the reset; the offsets of the phases are in EnvelopeClock
    private const int LfoStartDelay = 1;   // the oracle's vibrato and tremolo counters run one sample behind a count from reset (Workshop/Sega/FmOracleTestCli.cs measures it: zero mismatches over 4,000 samples of each, on all nine channels)

    /// <param name="clockHz">The chip's clock, in Hz (the Z80 clock on a Master System): only used to say what pitch a voice has; the chip runs one <see cref="Calc"/> per sample.</param>
    public Ym2413(double clockHz)
    {
        this.clockHz = clockHz;
        for (int i = 0; i < patches.Length; i++) patches[i] = new Patch();
        for (int i = 0; i < slot.Length; i++) slot[i] = new Slot();
        for (int i = 0; i < patches.Length; i++) patches[i].CopyFrom(NullPatch);
        Reset();
        ResetPatch();
    }

    /// <summary>The chip's sample rate: its clock over 72.</summary>
    public double SampleRate => clockHz / ClocksPerSample;
    public double ClockHz => clockHz;

    // ---------------------------------------------------------------------------------------- slot bookkeeping

    private int GetParameterRate(Slot s)
    {
        if ((s.Type & 1) == 0 && !s.KeyFlag) return 0;
        switch (s.EgState)
        {
            case Attack: return s.Patch.AR;
            case Decay: return s.Patch.DR;
            case Sustain: return s.Patch.EG != 0 ? 0 : s.Patch.RR;
            case Release:
                if (s.SusFlag) return 5;
                return s.Patch.EG != 0 ? s.Patch.RR : 7;
            case Damp: return DamperRate;
            default: return 0;
        }
    }

    private static void RequestUpdate(Slot s, int flag) => s.UpdateRequests |= flag;

    private void CommitSlotUpdate(Slot s)
    {
        if ((s.UpdateRequests & UpdateWs) != 0) s.WaveSel = s.Patch.WS;
        if ((s.UpdateRequests & UpdateTll) != 0)
        {
            int tl = (s.Type & 1) == 0 ? s.Patch.TL : s.Volume;
            s.Tll = TllTable[s.BlkFnum >> 5, s.Patch.KL] + (tl << 1);
        }
        if ((s.UpdateRequests & UpdateRks) != 0) s.Rks = RksTable[s.BlkFnum >> 8, s.Patch.KR];
        if ((s.UpdateRequests & (UpdateRks | UpdateEg)) != 0)
        {
            int pRate = GetParameterRate(s);
            if (pRate == 0) { s.EgShift = 0; s.EgRateH = 0; s.EgRateL = 0; }
            else
            {
                s.EgRateH = Math.Min(15, pRate + (s.Rks >> 2));
                s.EgRateL = s.Rks & 3;
                if (s.EgState == Attack) s.EgShift = 0 < s.EgRateH && s.EgRateH < 12 ? 13 - s.EgRateH : 0;
                else s.EgShift = s.EgRateH < 13 ? 13 - s.EgRateH : 0;
            }
        }
        s.UpdateRequests = 0;
    }

    private static void ResetSlot(Slot s, int number)
    {
        s.Number = number;
        s.Type = number % 2;
        s.PgKeep = false;
        s.WaveSel = 0;
        s.PgPhase = 0;
        s.Output0 = 0; s.Output1 = 0;
        s.EgState = Release;
        s.EgShift = 0; s.Rks = 0; s.Tll = 0;
        s.KeyFlag = false; s.SusFlag = false; s.DampSkip = false;
        s.BlkFnum = 0; s.Blk = 0; s.Fnum = 0;
        s.Volume = 0; s.PgOut = 0;
        s.EgRateH = 0; s.EgRateL = 0;
        s.EgOut = EgMute;
        s.UpdateRequests = 0;
        s.Patch = NullPatch; s.PatchIndex = -1;
    }

    private void SlotOn(int i)
    {
        var s = slot[i];
        s.KeyFlag = true;
        s.DampSkip = s.EgOut >= EgMax;
        s.EgState = Damp;
        RequestUpdate(s, UpdateEg);
    }

    private void SlotOff(int i)
    {
        var s = slot[i];
        s.KeyFlag = false;
        if ((s.Type & 1) != 0) { s.EgState = Release; RequestUpdate(s, UpdateEg); }
    }

    private void UpdateKeyStatus()
    {
        int r14 = reg[0x0e];
        bool rhythm = ((r14 >> 5) & 1) != 0;
        uint newStatus = 0;
        for (int ch = 0; ch < 9; ch++) if ((reg[0x20 + ch] & 0x10) != 0) newStatus |= 3u << (ch * 2);
        if (rhythm)
        {
            if ((r14 & 0x10) != 0) newStatus |= 3u << SlotBd1;
            if ((r14 & 0x01) != 0) newStatus |= 1u << SlotHh;
            if ((r14 & 0x08) != 0) newStatus |= 1u << SlotSd;
            if ((r14 & 0x04) != 0) newStatus |= 1u << SlotTom;
            if ((r14 & 0x02) != 0) newStatus |= 1u << SlotCym;
        }
        uint updated = slotKeyStatus ^ newStatus;
        if (updated != 0)
            for (int i = 0; i < 18; i++)
                if (((updated >> i) & 1) != 0)
                {
                    if (((newStatus >> i) & 1) != 0) SlotOn(i); else SlotOff(i);
                }
        slotKeyStatus = newStatus;
    }

    private void SetPatch(int ch, int num)
    {
        patchNumber[ch] = num;
        slot[ch << 1].Patch = patches[num * 2]; slot[ch << 1].PatchIndex = num * 2;
        slot[(ch << 1) | 1].Patch = patches[num * 2 + 1]; slot[(ch << 1) | 1].PatchIndex = num * 2 + 1;
        RequestUpdate(slot[ch << 1], UpdateAll);
        RequestUpdate(slot[(ch << 1) | 1], UpdateAll);
    }

    private void SetSusFlag(int ch, bool flag)
    {
        var car = slot[(ch << 1) | 1]; var mod = slot[ch << 1];
        car.SusFlag = flag;
        RequestUpdate(car, UpdateEg);
        if ((mod.Type & 1) != 0) { mod.SusFlag = flag; RequestUpdate(mod, UpdateEg); }
    }

    private void SetVolume(int ch, int volume)
    {
        var car = slot[(ch << 1) | 1];
        car.Volume = volume;
        RequestUpdate(car, UpdateTll);
    }

    private static void SetSlotVolume(Slot s, int volume) { s.Volume = volume; RequestUpdate(s, UpdateTll); }

    private void SetFnumber(int ch, int fnum)
    {
        var car = slot[(ch << 1) | 1]; var mod = slot[ch << 1];
        car.Fnum = fnum; car.BlkFnum = (car.BlkFnum & 0xe00) | (fnum & 0x1ff);
        mod.Fnum = fnum; mod.BlkFnum = (mod.BlkFnum & 0xe00) | (fnum & 0x1ff);
        RequestUpdate(car, UpdateEg | UpdateRks | UpdateTll);
        RequestUpdate(mod, UpdateEg | UpdateRks | UpdateTll);
    }

    private void SetBlock(int ch, int blk)
    {
        var car = slot[(ch << 1) | 1]; var mod = slot[ch << 1];
        car.Blk = blk; car.BlkFnum = ((blk & 7) << 9) | (car.BlkFnum & 0x1ff);
        mod.Blk = blk; mod.BlkFnum = ((blk & 7) << 9) | (mod.BlkFnum & 0x1ff);
        RequestUpdate(car, UpdateEg | UpdateRks | UpdateTll);
        RequestUpdate(mod, UpdateEg | UpdateRks | UpdateTll);
    }

    private void UpdateRhythmMode()
    {
        bool now = ((reg[0x0e] >> 5) & 1) != 0;
        if (rhythmMode != now)
        {
            if (now)
            {
                slot[SlotHh].Type = 3; slot[SlotHh].PgKeep = true;
                slot[SlotSd].Type = 3;
                slot[SlotTom].Type = 3;
                slot[SlotCym].Type = 3; slot[SlotCym].PgKeep = true;
                SetPatch(6, 16); SetPatch(7, 17); SetPatch(8, 18);
                SetSlotVolume(slot[SlotHh], ((reg[0x37] >> 4) & 15) << 2);
                SetSlotVolume(slot[SlotTom], ((reg[0x38] >> 4) & 15) << 2);
                chOut[6] = chOut[7] = chOut[8] = 0;   // the melodic channels 7-9 are gone: they must not keep sounding their last value
            }
            else
            {
                slot[SlotHh].Type = 0; slot[SlotHh].PgKeep = false;
                slot[SlotSd].Type = 1;
                slot[SlotTom].Type = 0;
                slot[SlotCym].Type = 1; slot[SlotCym].PgKeep = false;
                SetPatch(6, reg[0x36] >> 4); SetPatch(7, reg[0x37] >> 4); SetPatch(8, reg[0x38] >> 4);
                for (int i = 9; i < 14; i++) chOut[i] = 0;
            }
        }
        rhythmMode = now;
    }

    // ---------------------------------------------------------------------------------------- the generators

    private void UpdateAmPm()
    {
        if ((testFlag & 2) != 0) { pmPhase = 0; amPhase = 0; }
        else
        {
            pmPhase += (testFlag & 8) != 0 ? 1024u : 1u;
            amPhase += (testFlag & 8) != 0 ? 64 : 1;
            if (amPhase >= AmPeriod) amPhase -= AmPeriod;
        }
        lfoAm = AmTable[(amPhase >> 6) % AmTable.Length];
    }

    private void UpdateNoise(int cycles)
    {
        for (int i = 0; i < cycles; i++)
        {
            if ((noise & 1) != 0) noise ^= 0x800200;
            noise >>= 1;
        }
    }

    private void UpdateShortNoise()
    {
        uint pgHh = slot[SlotHh].PgOut, pgCym = slot[SlotCym].PgOut;
        uint hBit2 = (pgHh >> (PgBits - 8)) & 1, hBit7 = (pgHh >> (PgBits - 3)) & 1, hBit3 = (pgHh >> (PgBits - 7)) & 1;
        uint cBit3 = (pgCym >> (PgBits - 7)) & 1, cBit5 = (pgCym >> (PgBits - 5)) & 1;
        shortNoise = ((hBit2 ^ hBit7) | (hBit3 ^ cBit5) | (cBit3 ^ cBit5)) != 0;
    }

    private void CalcPhase(Slot s, uint pmPhase, bool reset)
    {
        int pm = s.Patch.PM != 0 ? PmTable[(s.Fnum >> 6) & 7, (int)((pmPhase >> 10) & 7)] : 0;
        if (reset) s.PgPhase = 0;
        s.PgPhase += ((uint)((s.Fnum & 0x1ff) * 2 + pm) * MlTable[s.Patch.ML]) << s.Blk >> 2;
        s.PgPhase &= DpWidth - 1;
        s.PgOut = s.PgPhase >> DpBaseBits;
    }

    private static int LookupAttackStep(Slot s, uint counter)
    {
        int index;
        switch (s.EgRateH)
        {
            case 12: index = (int)((counter & 0xc) >> 1); return 4 - EgStepTables[s.EgRateL, index];
            case 13: index = (int)((counter & 0xc) >> 1); return 3 - EgStepTables[s.EgRateL, index];
            case 14: index = (int)((counter & 0xc) >> 1); return 2 - EgStepTables[s.EgRateL, index];
            case 0:
            case 15: return 0;
            default: index = (int)(counter >> s.EgShift); return EgStepTables[s.EgRateL, index & 7] != 0 ? 4 : 0;
        }
    }

    private static int LookupDecayStep(Slot s, uint counter)
    {
        int index;
        switch (s.EgRateH)
        {
            case 0: return 0;
            case 13: index = (int)(((counter & 0xc) >> 1) | (counter & 1)); return EgStepTables[s.EgRateL, index];
            case 14: index = (int)((counter & 0xc) >> 1); return EgStepTables[s.EgRateL, index] + 1;
            case 15: return 2;
            default: index = (int)(counter >> s.EgShift); return EgStepTables[s.EgRateL, index & 7];
        }
    }

    private void StartEnvelope(Slot s)
    {
        if (Math.Min(15, s.Patch.AR + (s.Rks >> 2)) == 15) { s.EgState = Decay; s.EgOut = 0; }
        else s.EgState = Attack;
        RequestUpdate(s, UpdateEg);
    }

    // The envelope clock offsets, by rate class and the low two bits of the rate (the key-scale remainder), found by sweeping them against the oracle for every rate of a lone carrier (Workshop/Sega/FmOracleTestCli.cs).
    private static readonly int[] AttackFastOffset = { 7, 7, 11, 19 };      // attack rates 12 to 14
    private static readonly int[] Decay12Offset = { 2, 6, 2, 2 };           // decay, sustain, release and damp at rate 12
    private static readonly int[] Decay13Offset = { 7, 7, 11, 11 };         // rate 13
    private static readonly int[] Decay14Offset = { 7, 6, 10, 11 };         // rate 14 (and 15, which does not depend on it)
    private const int AttackSlowOffset = -1, DecaySlowOffset = -4;

    /// <summary>
    /// The envelope clock a slot's current phase runs on: the sample count with this phase's own offset. The oracle's attack steps fall one sample before the count and its decay, sustain, release and damp steps four samples before for the slower rates
    /// (11 and under in the decay, 11 and under in the attack); the fast rates, which step on every sample or every second sample by a pattern four samples long, have offsets that depend on the rate and on the pattern chosen by its low two bits (the tables above).
    /// The oracle's fast patterns repeat every 15 samples in some cases (a 4-bit pseudo-random counter rather than a binary one), which an offset cannot follow exactly: see the record for the error that leaves.
    /// </summary>
    private static ushort EnvelopeClock(uint count, Slot s)
    {
        int rate = s.EgRateH, low = s.EgRateL & 3;
        int offset;
        if (s.EgState == Attack) offset = rate is >= 12 and <= 14 ? AttackFastOffset[low] : AttackSlowOffset;
        else offset = rate switch { 12 => Decay12Offset[low], 13 => Decay13Offset[low], >= 14 => Decay14Offset[low], _ => DecaySlowOffset };
        return unchecked((ushort)(count + (uint)offset));
    }

    private void CalcEnvelope(Slot s, Slot? buddy, uint count, bool test)
    {
        // nothing to do while sustaining or releasing at a fixed level (muted, or a zero rate)
        if (!test && (s.EgState == Sustain || s.EgState == Release) && (s.EgOut >= EgMute || s.EgRateH == 0)) return;
        ushort counter = EnvelopeClock(count, s);

        uint mask = (1u << s.EgShift) - 1;
        if (s.EgState == Attack)
        {
            if (0 < s.EgOut && 0 < s.EgRateH && (counter & mask & ~3u) == 0)
            {
                int step = LookupAttackStep(s, counter);
                if (0 < step) s.EgOut = (uint)Math.Max(0, (int)s.EgOut - (int)(s.EgOut >> step) - 1);
            }
        }
        else if (s.EgRateH > 0 && (counter & mask) == 0)
            s.EgOut = (uint)Math.Min(EgMute, (int)s.EgOut + LookupDecayStep(s, counter));

        switch (s.EgState)
        {
            case Damp:
                // damp turns to attack when the envelope reaches the largest level that is not yet mute, in step with the envelope's own progress
                if (s.EgOut >= EgMax && ((counter & mask) == 0 || s.DampSkip))
                {
                    s.DampSkip = false;
                    StartEnvelope(s);
                    if ((s.Type & 1) != 0)
                    {
                        if (!s.PgKeep) s.PgPhase = 0;
                        if (buddy != null && !buddy.PgKeep) buddy.PgPhase = 0;
                    }
                }
                break;
            case Attack:
                if (s.EgOut == 0) { s.EgState = Decay; RequestUpdate(s, UpdateEg); }
                break;
            case Decay:
                // decay to sustain is checked at every cycle, not in step with the envelope
                if ((int)(s.EgOut >> 3) == s.Patch.SL) { s.EgState = Sustain; RequestUpdate(s, UpdateEg); }
                break;
        }
        if (test) s.EgOut = 0;
    }

    private void UpdateSlots()
    {
        egCounter++;
        for (int i = 0; i < 18; i++)
        {
            var s = slot[i];
            Slot? buddy = null;
            if (s.Type == 0) buddy = slot[i + 1];
            if (s.Type == 1) buddy = slot[i - 1];
            if (s.UpdateRequests != 0) CommitSlotUpdate(s);
            CalcEnvelope(s, buddy, egCounter, (testFlag & 1) != 0);
            CalcPhase(s, pmPhase, (testFlag & 4) != 0);
        }
    }

    /// <summary>The exponential table: input a log-sine value plus attenuation, output -4095 to 4095.</summary>
    private static int LookupExp(int i)
    {
        i &= 0xffff;
        int t = ExpTable[(i & 0xff) ^ 0xff] + 1024;
        int res = t >> ((i & 0x7f00) >> 8);
        return ((i & 0x8000) != 0 ? ~res : res) << 1;
    }

    private static int ToLinear(int h, Slot s, int am)
    {
        if (s.EgOut > EgMax) return 0;
        int att = Math.Min(EgMute, (int)s.EgOut + s.Tll + am) << 4;
        return LookupExp(h + att);
    }

    private int CalcSlotCar(int ch, int fm)
    {
        var s = slot[(ch << 1) | 1];
        int am = s.Patch.AM != 0 ? lfoAm : 0;
        s.Output1 = s.Output0;
        s.Output0 = ToLinear(WaveTables[s.WaveSel][(int)((s.PgOut + (uint)(2 * (fm >> 1))) & (PgWidth - 1))], s, am);
        return s.Output0;
    }

    private int CalcSlotMod(int ch)
    {
        var s = slot[ch << 1];
        int fm = s.Patch.FB > 0 ? (s.Output1 + s.Output0) >> (9 - s.Patch.FB) : 0;
        int am = s.Patch.AM != 0 ? lfoAm : 0;
        s.Output1 = s.Output0;
        s.Output0 = ToLinear(WaveTables[s.WaveSel][(int)((s.PgOut + (uint)fm) & (PgWidth - 1))], s, am);
        return s.Output0;
    }

    private int CalcSlotTom()
    {
        var s = slot[8 << 1];
        return ToLinear(WaveTables[s.WaveSel][s.PgOut], s, 0);
    }

    private int CalcSlotSnare()
    {
        var s = slot[(7 << 1) | 1];
        int phase = ((s.PgOut >> (PgBits - 2)) & 1) != 0 ? ((noise & 1) != 0 ? 0x300 : 0x200) : ((noise & 1) != 0 ? 0x0 : 0x100);
        return ToLinear(WaveTables[s.WaveSel][phase], s, 0);
    }

    private int CalcSlotCym()
    {
        var s = slot[(8 << 1) | 1];
        int phase = shortNoise ? 0x300 : 0x100;
        return ToLinear(WaveTables[s.WaveSel][phase], s, 0);
    }

    private int CalcSlotHat()
    {
        var s = slot[7 << 1];
        int phase = shortNoise ? ((noise & 1) != 0 ? 0x2d0 : 0x234) : ((noise & 1) != 0 ? 0x34 : 0xd0);
        return ToLinear(WaveTables[s.WaveSel][phase], s, 0);
    }

    private static short MelodicOut(int x) => (short)(-x >> 1);   // melodic channels are inverted and halved
    private static short RhythmOut(int x) => (short)x;

    private void UpdateOutput()
    {
        UpdateAmPm();
        UpdateShortNoise();
        UpdateSlots();

        for (int i = 0; i < 6; i++)
            chOut[i] = (mask & MaskChannel(i)) == 0 ? MelodicOut(CalcSlotCar(i, CalcSlotMod(i))) : (short)0;

        if (!rhythmMode) chOut[6] = (mask & MaskChannel(6)) == 0 ? MelodicOut(CalcSlotCar(6, CalcSlotMod(6))) : (short)0;
        else chOut[9] = (mask & MaskBass) == 0 ? RhythmOut(CalcSlotCar(6, CalcSlotMod(6))) : (short)0;
        UpdateNoise(14);

        if (!rhythmMode) chOut[7] = (mask & MaskChannel(7)) == 0 ? MelodicOut(CalcSlotCar(7, CalcSlotMod(7))) : (short)0;
        else
        {
            chOut[10] = (mask & MaskHiHat) == 0 ? RhythmOut(CalcSlotHat()) : (short)0;
            chOut[11] = (mask & MaskSnare) == 0 ? RhythmOut(CalcSlotSnare()) : (short)0;
        }
        UpdateNoise(2);

        if (!rhythmMode) chOut[8] = (mask & MaskChannel(8)) == 0 ? MelodicOut(CalcSlotCar(8, CalcSlotMod(8))) : (short)0;
        else
        {
            chOut[12] = (mask & MaskTom) == 0 ? RhythmOut(CalcSlotTom()) : (short)0;
            chOut[13] = (mask & MaskCymbal) == 0 ? RhythmOut(CalcSlotCym()) : (short)0;
        }
        UpdateNoise(2);
    }

    // ---------------------------------------------------------------------------------------- the interface

    /// <summary>Clears the registers and the generators (the instrument ROM stays; the user instrument is cleared with the registers).</summary>
    public void Reset()
    {
        adr = 0;
        Array.Clear(reg);
        pmPhase = unchecked((uint)-LfoStartDelay); amPhase = AmPeriod - LfoStartDelay;
        noise = 1; shortNoise = false;
        mask = 0;
        testFlag = 0; lfoAm = 0;
        rhythmMode = false;
        slotKeyStatus = 0;
        egCounter = EgStartOffset;
        for (int i = 0; i < 18; i++) ResetSlot(slot[i], i);
        for (int i = 0; i < 9; i++) SetPatch(i, 0);
        for (int i = 0; i < 0x40; i++) WriteRegister(i, 0);
        Array.Clear(chOut);
    }

    /// <summary>Loads the instrument ROM (the 15 melodic patches, the three rhythm patches and an empty user patch).</summary>
    public void ResetPatch()
    {
        for (int i = 0; i < patches.Length; i++) patches[i].CopyFrom(RomPatchSet[i]);
        ForceRefresh();
    }

    private void ForceRefresh()
    {
        for (int i = 0; i < 9; i++) SetPatch(i, patchNumber[i]);
        for (int i = 0; i < 18; i++) RequestUpdate(slot[i], UpdateAll);
    }

    /// <summary>Which of the 14 outputs are muted (<see cref="MaskChannel"/>, <see cref="MaskBass"/> ...). Returns the previous mask.</summary>
    public uint Mask
    {
        get => mask;
        set => mask = value;
    }

    /// <summary>The latest output of one of the 14 voices: 0 to 8 melodic, 9 bass drum, 10 hi-hat, 11 snare, 12 tom, 13 cymbal.</summary>
    public int ChannelOutput(int voice) => chOut[voice];

    /// <summary>Sets the vibrato and tremolo counters (in samples; both stand at minus one after a reset). For tools that line the chip up with another one.</summary>
    public void SetLfoPhase(uint vibrato, int tremolo) { pmPhase = vibrato; amPhase = ((tremolo % AmPeriod) + AmPeriod) % AmPeriod; }

    /// <summary>Advances the percussion noise generator by <paramref name="shifts"/> steps (it takes 18 per sample). For tools that line the chip up with another one.</summary>
    public void AdvanceNoise(int shifts) => UpdateNoise(shifts);

    /// <summary>The envelope generator's global counter (counts samples from the reset).</summary>
    public uint EnvelopeCounter { get => egCounter; set => egCounter = value; }

    public bool RhythmMode => rhythmMode;
    public byte Register(int index) => reg[index & 0x3f];
    public int AddressLatch => (int)adr;

    /// <summary>A write to the address port (bit 0 of the port clear) or the data port (set).</summary>
    public void WriteIo(uint port, byte value)
    {
        if ((port & 1) != 0) WriteRegister((int)adr, value); else adr = value;
    }

    public void WriteAddress(byte value) => adr = value;
    public void WriteData(byte value) => WriteRegister((int)adr, value);

    /// <summary>Writes a register: 0-7 the user instrument, $0E rhythm and key-on of the five voices, $0F test, $10-$18 low fnum, $20-$28 key, sustain, block and high fnum bit, $30-$38 instrument and volume.</summary>
    public void WriteRegister(int r, byte data)
    {
        if (r >= 0x40 || r < 0) return;
        // the registers $19-$1F, $29-$2F and $39-$3F mirror $10-$16, $20-$26 and $30-$36 (nine apart)
        if ((0x19 <= r && r <= 0x1f) || (0x29 <= r && r <= 0x2f) || (0x39 <= r && r <= 0x3f)) r -= 9;
        reg[r] = data;
        Patch u0 = patches[0], u1 = patches[1];
        switch (r)
        {
            case 0x00:
                u0.AM = (data >> 7) & 1; u0.PM = (data >> 6) & 1; u0.EG = (data >> 5) & 1; u0.KR = (data >> 4) & 1; u0.ML = data & 15;
                for (int i = 0; i < 9; i++) if (patchNumber[i] == 0) RequestUpdate(slot[i << 1], UpdateRks | UpdateEg);
                break;
            case 0x01:
                u1.AM = (data >> 7) & 1; u1.PM = (data >> 6) & 1; u1.EG = (data >> 5) & 1; u1.KR = (data >> 4) & 1; u1.ML = data & 15;
                for (int i = 0; i < 9; i++) if (patchNumber[i] == 0) RequestUpdate(slot[(i << 1) | 1], UpdateRks | UpdateEg);
                break;
            case 0x02:
                u0.KL = (data >> 6) & 3; u0.TL = data & 63;
                for (int i = 0; i < 9; i++) if (patchNumber[i] == 0) RequestUpdate(slot[i << 1], UpdateTll);
                break;
            case 0x03:
                u1.KL = (data >> 6) & 3; u1.WS = (data >> 4) & 1; u0.WS = (data >> 3) & 1; u0.FB = data & 7;
                for (int i = 0; i < 9; i++)
                    if (patchNumber[i] == 0)
                    {
                        RequestUpdate(slot[i << 1], UpdateWs);
                        RequestUpdate(slot[(i << 1) | 1], UpdateWs | UpdateTll);
                    }
                break;
            case 0x04:
                u0.AR = (data >> 4) & 15; u0.DR = data & 15;
                for (int i = 0; i < 9; i++) if (patchNumber[i] == 0) RequestUpdate(slot[i << 1], UpdateEg);
                break;
            case 0x05:
                u1.AR = (data >> 4) & 15; u1.DR = data & 15;
                for (int i = 0; i < 9; i++) if (patchNumber[i] == 0) RequestUpdate(slot[(i << 1) | 1], UpdateEg);
                break;
            case 0x06:
                u0.SL = (data >> 4) & 15; u0.RR = data & 15;
                for (int i = 0; i < 9; i++) if (patchNumber[i] == 0) RequestUpdate(slot[i << 1], UpdateEg);
                break;
            case 0x07:
                u1.SL = (data >> 4) & 15; u1.RR = data & 15;
                for (int i = 0; i < 9; i++) if (patchNumber[i] == 0) RequestUpdate(slot[(i << 1) | 1], UpdateEg);
                break;
            case 0x0e:
                UpdateRhythmMode();
                UpdateKeyStatus();
                break;
            case 0x0f:
                testFlag = data;
                break;
            case >= 0x10 and <= 0x18:
            {
                int ch = r - 0x10;
                SetFnumber(ch, data + ((reg[0x20 + ch] & 1) << 8));
                break;
            }
            case >= 0x20 and <= 0x28:
            {
                int ch = r - 0x20;
                SetFnumber(ch, ((data & 1) << 8) + reg[0x10 + ch]);
                SetBlock(ch, (data >> 1) & 7);
                SetSusFlag(ch, ((data >> 5) & 1) != 0);
                UpdateKeyStatus();
                break;
            }
            case >= 0x30 and <= 0x38:
                if ((reg[0x0e] & 32) != 0 && r >= 0x36)
                {
                    switch (r)
                    {
                        case 0x37: SetSlotVolume(slot[7 << 1], ((data >> 4) & 15) << 2); break;
                        case 0x38: SetSlotVolume(slot[8 << 1], ((data >> 4) & 15) << 2); break;
                    }
                }
                else SetPatch(r - 0x30, (data >> 4) & 15);
                SetVolume(r - 0x30, (data & 15) << 2);
                break;
        }
    }

    /// <summary>Runs one chip sample (72 chip clocks) and returns the mixed output: the sum of the 14 voice outputs.</summary>
    public int Calc()
    {
        UpdateOutput();
        int sum = 0;
        for (int i = 0; i < 14; i++) sum += chOut[i];
        return sum;
    }

    // ---------------------------------------------------------------------------------------- what the chip is playing

    /// <summary>Number of voices <see cref="DescribeVoices"/> writes: nine melodic channels, then bass drum, snare, tom, cymbal, hi-hat.</summary>
    public const int VoiceCount = 14;

    /// <summary>The fundamental of melodic channel <paramref name="ch"/> as its carrier plays it, in Hz.</summary>
    public double ChannelHz(int ch, bool carrier = true)
    {
        var s = slot[(ch << 1) | (carrier ? 1 : 0)];
        // phase step per chip sample: fnum * multiplier * 2^block / 2 (multiplier table is doubled), in 2^19 steps per cycle
        double step = s.Fnum * (double)MlTable[s.Patch.ML] * (1 << s.Blk) / 2.0;
        return step / DpWidth * SampleRate;
    }

    /// <summary>The attenuation of channel <paramref name="ch"/>'s carrier as a linear amplitude (1.0 = no attenuation) from the envelope and the total level; 0 when the envelope is muted.</summary>
    public double ChannelAmplitude(int ch)
    {
        var s = slot[(ch << 1) | 1];
        if (s.EgOut > EgMax) return 0;
        int att = Math.Min(EgMute, (int)s.EgOut + s.Tll);
        return Math.Pow(10.0, -att * EgStep / 20.0);
    }

    /// <summary>What one operator of a melodic channel is doing, for verifiers and displays: the phase generator's output (10 bits), the envelope's attenuation (0 to 127), the total level and key-scale attenuation added to it, and the envelope state (0 attack, 1 decay, 2 sustain, 3 release, 4 damp).</summary>
    public (int Phase, int Envelope, int Level, int State) Operator(int ch, bool carrier)
    {
        var s = slot[(ch << 1) | (carrier ? 1 : 0)];
        return ((int)s.PgOut, (int)s.EgOut, s.Tll, s.EgState);
    }

    public bool ChannelKeyOn(int ch) => ((slotKeyStatus >> (ch * 2 + 1)) & 1) != 0;

    /// <summary>The nine melodic channels as pulse voices (a sine is nearer a triangle, but the pulses have a volume register, and there are more of them), then bass drum (a triangle at channel 7's pitch),
    /// snare and hi-hat and cymbal (noises) and tom (a pulse at channel 8's pitch). A voice that is not sounding is reported silent; the rhythm voices are silent outside rhythm mode and the melodic channels 7 to 9
    /// are silent in it. The level is the carrier's amplitude, 1.0 for the largest the chip makes (a full-volume channel).</summary>
    public int DescribeVoices(Span<HubVoice> voices)
    {
        int n = Math.Min(voices.Length, VoiceCount);
        for (int ch = 0; ch < 9 && ch < n; ch++)
        {
            bool melodic = !(rhythmMode && ch >= 6);
            double amp = melodic && (mask & MaskChannel(ch)) == 0 ? ChannelAmplitude(ch) : 0;
            voices[ch] = new HubVoice(HubVoiceKind.Pulse, ChannelHz(ch), (float)amp, Duty: 2);
        }
        if (n > 9)
        {
            // bass drum: channel 7 in rhythm mode; snare/hi-hat: channel 8's pair; tom/cymbal: channel 9's pair
            double bd = rhythmMode && (mask & MaskBass) == 0 && slot[SlotBd2].EgOut <= EgMax ? SlotAmplitude(slot[SlotBd2]) : 0;
            double sd = rhythmMode && (mask & MaskSnare) == 0 ? SlotAmplitude(slot[SlotSd]) : 0;
            double tom = rhythmMode && (mask & MaskTom) == 0 ? SlotAmplitude(slot[SlotTom]) : 0;
            double cym = rhythmMode && (mask & MaskCymbal) == 0 ? SlotAmplitude(slot[SlotCym]) : 0;
            double hh = rhythmMode && (mask & MaskHiHat) == 0 ? SlotAmplitude(slot[SlotHh]) : 0;
            double hzBd = ChannelHz(6), hzTom = ChannelHz(8, carrier: false);
            if (n > 9) voices[9] = new HubVoice(HubVoiceKind.Triangle, hzBd, (float)bd);
            if (n > 10) voices[10] = new HubVoice(HubVoiceKind.Noise, 4000, (float)sd);
            if (n > 11) voices[11] = new HubVoice(HubVoiceKind.Pulse, hzTom, (float)tom, Duty: 2);
            if (n > 12) voices[12] = new HubVoice(HubVoiceKind.Noise, 8000, (float)cym);
            if (n > 13) voices[13] = new HubVoice(HubVoiceKind.Noise, 12000, (float)hh);
        }
        return n;
    }

    private static double SlotAmplitude(Slot s)
    {
        if (s.EgOut > EgMax) return 0;
        int att = Math.Min(EgMute, (int)s.EgOut + s.Tll);
        return Math.Pow(10.0, -att * EgStep / 20.0);
    }

    /// <summary>A one-line description for probes and logs.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder("YM2413");
        sb.Append(rhythmMode ? " rhythm" : " melodic");
        for (int ch = 0; ch < 9; ch++)
            if (ChannelKeyOn(ch)) sb.Append($" ch{ch + 1}:inst{patchNumber[ch]}/{ChannelHz(ch):F0}Hz");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------- state

    public void SaveState(BinaryWriter w)
    {
        w.Write(adr); w.Write(reg);
        foreach (int p in patchNumber) w.Write(p);
        w.Write(testFlag); w.Write(slotKeyStatus); w.Write(rhythmMode); w.Write(egCounter); w.Write(pmPhase); w.Write(amPhase); w.Write(lfoAm);
        w.Write(noise); w.Write(shortNoise); w.Write(mask);
        for (int i = 0; i < 2; i++) SavePatch(w, patches[i]);   // the user instrument (the instrument ROM is constant)
        foreach (var s in slot)
        {
            w.Write(s.Number); w.Write(s.Type); w.Write(s.PatchIndex); w.Write(s.Output0); w.Write(s.Output1); w.Write(s.WaveSel);
            w.Write(s.PgPhase); w.Write(s.PgOut); w.Write(s.PgKeep); w.Write(s.BlkFnum); w.Write(s.Fnum); w.Write(s.Blk);
            w.Write(s.EgState); w.Write(s.Volume); w.Write(s.KeyFlag); w.Write(s.SusFlag); w.Write(s.DampSkip); w.Write(s.Tll); w.Write(s.Rks);
            w.Write(s.EgRateH); w.Write(s.EgRateL); w.Write(s.EgShift); w.Write(s.EgOut); w.Write(s.UpdateRequests);
        }
        foreach (short c in chOut) w.Write(c);
    }

    public void LoadState(BinaryReader r)
    {
        adr = r.ReadUInt32(); r.Read(reg, 0, reg.Length);
        for (int i = 0; i < 9; i++) patchNumber[i] = r.ReadInt32();
        testFlag = r.ReadInt32(); slotKeyStatus = r.ReadUInt32(); rhythmMode = r.ReadBoolean(); egCounter = r.ReadUInt32(); pmPhase = r.ReadUInt32(); amPhase = r.ReadInt32(); lfoAm = r.ReadInt32();
        noise = r.ReadUInt32(); shortNoise = r.ReadBoolean(); mask = r.ReadUInt32();
        for (int i = 0; i < 2; i++) LoadPatch(r, patches[i]);
        for (int i = 2; i < patches.Length; i++) patches[i].CopyFrom(RomPatchSet[i]);
        foreach (var s in slot)
        {
            s.Number = r.ReadInt32(); s.Type = r.ReadInt32(); s.PatchIndex = r.ReadInt32(); s.Output0 = r.ReadInt32(); s.Output1 = r.ReadInt32(); s.WaveSel = r.ReadInt32();
            s.PgPhase = r.ReadUInt32(); s.PgOut = r.ReadUInt32(); s.PgKeep = r.ReadBoolean(); s.BlkFnum = r.ReadInt32(); s.Fnum = r.ReadInt32(); s.Blk = r.ReadInt32();
            s.EgState = r.ReadInt32(); s.Volume = r.ReadInt32(); s.KeyFlag = r.ReadBoolean(); s.SusFlag = r.ReadBoolean(); s.DampSkip = r.ReadBoolean(); s.Tll = r.ReadInt32(); s.Rks = r.ReadInt32();
            s.EgRateH = r.ReadInt32(); s.EgRateL = r.ReadInt32(); s.EgShift = r.ReadInt32(); s.EgOut = r.ReadUInt32(); s.UpdateRequests = r.ReadInt32();
            s.Patch = s.PatchIndex >= 0 ? patches[s.PatchIndex] : NullPatch;
        }
        for (int i = 0; i < 14; i++) chOut[i] = r.ReadInt16();
    }

    private static void SavePatch(BinaryWriter w, Patch p)
    {
        w.Write(p.TL); w.Write(p.FB); w.Write(p.EG); w.Write(p.ML); w.Write(p.AR); w.Write(p.DR); w.Write(p.SL); w.Write(p.RR); w.Write(p.KR); w.Write(p.KL); w.Write(p.AM); w.Write(p.PM); w.Write(p.WS);
    }

    private static void LoadPatch(BinaryReader r, Patch p)
    {
        p.TL = r.ReadInt32(); p.FB = r.ReadInt32(); p.EG = r.ReadInt32(); p.ML = r.ReadInt32(); p.AR = r.ReadInt32(); p.DR = r.ReadInt32(); p.SL = r.ReadInt32(); p.RR = r.ReadInt32();
        p.KR = r.ReadInt32(); p.KL = r.ReadInt32(); p.AM = r.ReadInt32(); p.PM = r.ReadInt32(); p.WS = r.ReadInt32();
    }
}
