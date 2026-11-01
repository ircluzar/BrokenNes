using System;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  ZSNES 1.51 S-DSP sound engine, ported to C#.
//
//  Source: ZSNES 1.51 (2007, the final release), src/cpu/dspproc.asm + src/cpu/dsp.asm (the WDSPRegXX write handlers),
//  plus the noise table builder setnoise() from src/ui.c and the MusicVol computation from src/init.asm.
//  https://github.com/emillon/zsnes (tag upstream/1.510)
//  Copyright (C) 1997-2007 ZSNES Team (zsKnight, _Demo_, pagefault, Nach and others).
//  License: GPL-2.0 (GPL-2.0-or-later as distributed). This is a documented exception to the repository's license policy -
//  see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  ZSNES 1.51 defaults (cfg.psr): SoundQuality 5 = 32,000 Hz, StereoSound 1, SoundInterpType 1 = Gaussian (the MMX
//  path, DSPInterpolate_4_mmx), LowPassFilterType 0, Surround 0, RevStereo 0, EchoDis 0, MusicRelVol 100 (MusicVol 127).
//  Quick port, hand-translated from x86 asm. The original's bugs and shortcuts are kept on purpose; they are the sound:
//   - mixed in blocks of 160 stereo frames; key-on goes through ZSNES's two-step scanline pipeline (KeyOnStA -> B)
//   - envelopes are ZSNES's own: piecewise-linear segments chasing bend tables (AdsrBendData / GainDecBendData) with
//     its own rate tables (1/11025 s units rescaled to 32 kHz), not the hardware's counter
//   - a key-on of a playing voice first fades it out over 127 samples (state 210), then restarts it
//   - key-off is a 255-sample fade; FLG reset OR mute (bits 7/6) kills every voice
//   - a KON written while KOF = $FF is ignored; EON is only sampled at key-on; noise voices never echo
//   - volumes go through VolumeTableD (a loudness curve) and a signed 8x8>>7 table; master volume is |MVOL|
//   - echo: kept in its own buffer (ESA is ignored, sound RAM is never written), EVOL scales the echo INPUT (and
//     its sign is dropped), EFB's sign is dropped, the FIR taps are 2 samples apart, an identity FIR
//     ($7F,0,0,...) takes a separate unfiltered path
//   - Gaussian interpolation reads one sample AHEAD (a 4-sample look-ahead from the next/loop block)
//   - pitch modulation only when the modulator plays a different SRCN; the modulated voice is not interpolated and
//     reads its modulator one frame late; PModBuffer is never cleared
//   - the "ProcessPMod" multiply uses whatever CH held (the high byte of the last volume factor)
//   - BRR: the hardware's filter shifts, shift ranges 13-15 give 0/-2048 deltas, 15-bit wrap
//   - a game-specific hack: a key-on with SRCN 7 / ADSR $FF,$50 / GAIN 0 / DIR 6 and both volumes < $40 sets the
//     volumes to 15 and does not start the voice
//  Left out (logged in KNOWN_GAPS): mono, the non-interpolated / cubic / 8-tap mixers, the low-pass filters, surround,
//  the decoded-block cache, the unreachable ADSR states 1/2 ("Decay"/"Sustain"), MuteVoiceF / voice-disable / ENVDisable
//  UI switches, and the SPC700 (BrokenNes's own SMP drives this engine through the DSP register writes).
// =====================================================================================================================

/// <summary>The ZSNES 1.51 S-DSP: feed it the DSP register writes and the sound RAM, pull 32 kHz stereo blocks.</summary>
public sealed class Zs151Dsp
{
    public const int Rate = 32000;          // SBToSPCSpeeds[SoundQuality = 5]
    public const int BlockFrames = 160;     // BufferSizeB = 320 dwords, stereo
    private const int BufSizeB = BlockFrames * 2;
    private const byte MusicVol = 127;      // MusicRelVol 100: 100*128/100 -> clamped to 127

    private readonly byte[] ram;            // SPCRAM (shared with the SMP)
    public readonly byte[] Regs = new byte[256];   // DSPMem

    // ---------------------------------------------------------------- tables
    private static readonly short[] Gaussian = {
        1305, 1305, 1304, 1304, 1304, 1304, 1304, 1303, 1303, 1303, 1302, 1302, 1301, 1300, 1300, 1299,
        1298, 1297, 1297, 1296, 1295, 1294, 1293, 1292, 1291, 1290, 1288, 1287, 1286, 1284, 1283, 1282,
        1280, 1279, 1277, 1275, 1274, 1272, 1270, 1269, 1267, 1265, 1263, 1261, 1259, 1257, 1255, 1253,
        1251, 1248, 1246, 1244, 1241, 1239, 1237, 1234, 1232, 1229, 1227, 1224, 1221, 1219, 1216, 1213,
        1210, 1207, 1205, 1202, 1199, 1196, 1193, 1190, 1186, 1183, 1180, 1177, 1174, 1170, 1167, 1164,
        1160, 1157, 1153, 1150, 1146, 1143, 1139, 1136, 1132, 1128, 1125, 1121, 1117, 1113, 1109, 1106,
        1102, 1098, 1094, 1090, 1086, 1082, 1078, 1074, 1070, 1066, 1061, 1057, 1053, 1049, 1045, 1040,
        1036, 1032, 1027, 1023, 1019, 1014, 1010, 1005, 1001, 997, 992, 988, 983, 978, 974, 969,
        965, 960, 955, 951, 946, 941, 937, 932, 927, 923, 918, 913, 908, 904, 899, 894,
        889, 884, 880, 875, 870, 865, 860, 855, 851, 846, 841, 836, 831, 826, 821, 816,
        811, 806, 802, 797, 792, 787, 782, 777, 772, 767, 762, 757, 752, 747, 742, 737,
        732, 728, 723, 718, 713, 708, 703, 698, 693, 688, 683, 678, 674, 669, 664, 659,
        654, 649, 644, 640, 635, 630, 625, 620, 615, 611, 606, 601, 596, 592, 587, 582,
        577, 573, 568, 563, 559, 554, 550, 545, 540, 536, 531, 527, 522, 517, 513, 508,
        504, 499, 495, 491, 486, 482, 477, 473, 469, 464, 460, 456, 451, 447, 443, 439,
        434, 430, 426, 422, 418, 414, 410, 405, 401, 397, 393, 389, 385, 381, 378, 374,
        370, 366, 362, 358, 354, 351, 347, 343, 339, 336, 332, 328, 325, 321, 318, 314,
        311, 307, 304, 300, 297, 293, 290, 286, 283, 280, 276, 273, 270, 267, 263, 260,
        257, 254, 251, 248, 245, 242, 239, 236, 233, 230, 227, 224, 221, 218, 215, 212,
        210, 207, 204, 201, 199, 196, 193, 191, 188, 186, 183, 180, 178, 175, 173, 171,
        168, 166, 163, 161, 159, 156, 154, 152, 150, 147, 145, 143, 141, 139, 137, 134,
        132, 130, 128, 126, 124, 122, 120, 118, 117, 115, 113, 111, 109, 107, 106, 104,
        102, 100, 99, 97, 95, 94, 92, 90, 89, 87, 86, 84, 83, 81, 80, 78,
        77, 76, 74, 73, 71, 70, 69, 67, 66, 65, 64, 62, 61, 60, 59, 58,
        56, 55, 54, 53, 52, 51, 50, 49, 48, 47, 46, 45, 44, 43, 42, 41,
        40, 39, 38, 37, 36, 36, 35, 34, 33, 32, 32, 31, 30, 29, 29, 28,
        27, 27, 26, 25, 24, 24, 23, 23, 22, 21, 21, 20, 20, 19, 19, 18,
        17, 17, 16, 16, 15, 15, 15, 14, 14, 13, 13, 12, 12, 11, 11, 11,
        10, 10, 10, 9, 9, 9, 8, 8, 8, 7, 7, 7, 6, 6, 6, 6,
        5, 5, 5, 5, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 2, 2,
        2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };
    // DSPInterP as AdjustFrequency lays it out for DSPInterpolate_4_mmx: 4 taps per 8-bit fraction
    private static readonly short[] InterP = BuildInterP();

    private static readonly byte[] VolumeTableD = {
        0, 3, 6, 9, 12, 15, 17, 18, 19, 21, 22, 23, 24, 24, 26, 28,
        30, 31, 33, 35, 36, 38, 40, 41, 43, 45, 46, 48, 49, 51, 52, 54,
        56, 57, 58, 60, 61, 63, 64, 66, 67, 68, 70, 71, 72, 74, 75, 76,
        78, 79, 80, 81, 82, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94,
        96, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 106, 107, 108, 109,
        110, 110, 111, 112, 112, 113, 114, 114, 115, 116, 116, 117, 117, 118, 118, 119,
        120, 120, 120, 121, 121, 122, 122, 123, 123, 123, 124, 124, 124, 125, 125, 125,
        126, 126, 126, 126, 126, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 129, 129, 129, 129, 129,
        130, 130, 130, 131, 131, 131, 132, 132, 132, 133, 133, 134, 134, 135, 135, 135,
        136, 137, 137, 138, 138, 139, 139, 140, 141, 141, 142, 143, 143, 144, 145, 145,
        146, 147, 148, 149, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159, 159,
        161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 173, 174, 175, 176, 177,
        179, 180, 181, 183, 184, 185, 187, 188, 189, 191, 192, 194, 195, 197, 198, 199,
        201, 203, 204, 206, 207, 209, 210, 212, 214, 215, 217, 219, 220, 222, 224, 225,
        227, 229, 231, 231, 232, 233, 234, 236, 237, 238, 240, 243, 246, 249, 252, 255,
    };

    private static readonly int[] Filter = { 0, 0, 240, 0, 488, -240, 460, -208 };
    private static readonly byte[] SustainValue = { 15, 31, 47, 63, 79, 95, 111, 127 };
    private static readonly byte[] AdsrSustLevLoc = { 58, 39, 27, 19, 13, 8, 3, 1 };
    private static readonly byte[] GainDecBendData = {
        118, 110, 102, 95, 89, 83, 77, 72, 67, 62, 58, 54, 50, 47, 44, 41, 38, 35,
        33, 30, 28, 26, 24, 23, 21, 20, 18, 17, 16, 15, 14, 13,
        12, 11, 10, 9, 9, 8, 7, 7, 6, 6, 5, 5, 5, 4, 4, 4, 3, 3, 3, 3, 2, 2, 2, 2, 2, 1, 1, 1, 1,
        255 };
    private static readonly byte[] AdsrBendData = {
        122, 118, 114, 110, 106, 102, 99, 95, 92, 89, 86, 83, 80, 77, 74, 72, 69, 67,
        64, 62, 60, 58, 56, 54, 52, 50, 48, 47, 45, 44, 42, 41, 39, 38, 36, 35, 34, 33,
        32, 30, 29, 28, 27, 26, 25, 24, 24, 23, 22, 21, 20, 20, 19, 18, 18, 17, 16, 16,
        15, 15, 14, 14, 13, 13, 12, 12, 11, 11, 11, 10, 10, 9, 9, 9, 8, 8, 8, 7, 7, 7, 7, 6,
        6, 6, 6, 5, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 255 };
    private static readonly uint[] NoiseSpeeds = {
        1, 16, 21, 25, 31, 42, 50, 63, 83, 100, 125, 167, 200, 250, 333, 400, 500,
        667, 800, 1000, 1333, 1600, 2000, 2667, 3200, 4000, 5333, 6400, 8000, 10667, 16000, 32000 };

    // the "O" (original, 1/11025 s) tables through conv2speed (x SBToSPC / 11025)
    private static readonly uint[] EchoRate = Conv(new uint[] { 2, 172, 344, 517, 689, 861, 1033, 1205, 1378, 1550, 1722, 1895, 2067, 2239, 2412, 2584 }, 0);
    private static readonly uint[] AttackRate = Conv(new uint[] { 45202, 28665, 16537, 11025, 7056, 4189, 2866, 1764, 1058, 705, 441, 264, 176, 110, 66, 4 }, 0);
    private static readonly uint[] DecayRate = Conv(new uint[] { 13230, 8158, 4851, 2697, 2284, 1212, 815, 407 }, 0);
    private static readonly uint[] SustainRate = Conv(new uint[] {
        0xFFFFFFFF, 418950, 308700, 265600, 209475, 154350, 132300, 103635, 78277, 65047,
        51817, 38587, 31972, 26460, 19845, 16537, 13230, 9702, 8158, 6504, 4851, 3879, 2697,
        2050, 1572, 1212, 1014, 815, 606, 407, 202, 125 }, 1);
    private static readonly uint[] Increase = Conv(new uint[] {
        0xFFFFFFFF, 45202, 34177, 28665, 22050, 16537, 14332, 11025, 8489, 7056, 5622, 4189,
        3528, 2866, 2094, 1764, 1433, 1058, 882, 705, 529, 441, 352, 264, 220, 176, 132, 110, 88,
        66, 44, 22 }, 1);
    private static readonly uint[] Decrease = Conv(new uint[] {
        0xFFFFFFFF, 45202, 34177, 28665, 22050, 16537, 14332, 11025, 8489, 7056, 5622, 4189,
        3528, 2866, 2094, 1764, 1433, 1058, 882, 705, 529, 441, 352, 264, 220, 176, 132, 110, 88, 66,
        44, 22 }, 1);
    private static readonly uint[] DecreaseRateExp = Conv(new uint[] {
        0xFFFFFFFF, 418950, 308700, 264600, 209470, 154350, 132300, 103635, 78277, 65047,
        51817, 38587, 31972, 26460, 19845, 16537, 13230, 9702, 8158, 6504, 4851, 4079, 3197,
        2425, 1984, 1653, 1212, 1014, 815, 606, 407, 198 }, 1);

    private static readonly short[] VolumeConvTable = BuildVolumeConvTable();
    private static readonly short[] NoiseData = BuildNoise();

    private static uint[] Conv(uint[] t, int from)
    {
        var r = (uint[])t.Clone();
        for (int i = from; i < r.Length; i++) r[i] = (uint)((ulong)r[i] * Rate / 11025);
        return r;
    }

    private static short[] BuildInterP()
    {
        var t = new short[1024];
        for (int f = 0; f < 256; f++)
        {
            t[f * 4 + 0] = Gaussian[256 + f];
            t[f * 4 + 1] = Gaussian[f];
            t[f * 4 + 2] = Gaussian[255 - f];
            t[f * 4 + 3] = Gaussian[511 - f];
        }
        return t;
    }

    private static short[] BuildVolumeConvTable()
    {
        // InitSPC: al = dh; imul dl; shr ax,7; movsx bx,al. The original builds 32,768 entries; the rest is computed
        // the same way (the original would read whatever followed the table).
        var t = new short[65536];
        for (int e = 0; e < 65536; e++)
        {
            ushort ax = (ushort)(short)((sbyte)(e >> 8) * (sbyte)(e & 0xFF));
            t[e] = (sbyte)(byte)(ax >> 7);
        }
        return t;
    }

    private static short[] BuildNoise()
    {
        // ui.c setnoise(): 32,768 bytes, read by the mixer as 16,384 little-endian words
        byte[] samplenoise = {
            27,232,234,138,187,246,176, 81, 25,241,  1,127,154,190,195,103,
            231,165,220,238,232,189, 57,201,123, 75, 63,143,145,159, 13,236,
            191,142, 56,164,222, 80, 88, 13,148,118,162,212,157,146,176,  0,
            241, 88,244,238, 51,235,149, 50, 77,212,186,241, 88, 32, 23,206,
              1, 24, 48,244,248,210,253, 77, 19,100, 83,222,108, 68, 11, 58,
            152,161,223,245,  4,105,  3, 82, 15,130,171,242,141,  2,172,218,
            152, 97,223,157, 93, 75, 83,238,104,238,131, 70, 22,252,180, 82,
            110,123,106,133,183,209, 48,230,157,205, 27, 21,107, 63, 85,164 };
        var bytes = new byte[32768];
        int p1 = 0, p2 = 0, p3 = 0;
        for (int c1 = 256; c1 > 0; c1--)
        {
            for (int c2 = 128; c2 > 0; c2--, p1++)
            {
                bytes[p1] = (byte)(samplenoise[p2] + samplenoise[p3]);
                p2 = (p2 + 1) & 0x7F;
                p3 = (p3 - 1) & 0x7F;
            }
            p3 = (p3 - 1) & 0x7F;
        }
        var w = new short[16384];
        for (int i = 0; i < w.Length; i++) w[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
        return w;
    }

    private static int VolTableB(byte al) => al < 128 ? al : 255 - al;   // VolumeTableb
    private static byte Vct(int hi, int lo) => (byte)VolumeConvTable[((hi & 0xFF) << 8) | (lo & 0xFF)];

    // ---------------------------------------------------------------- per-voice state (Voice0xxx + n)
    private sealed class Voice
    {
        public uint Freq, Ptr, LoopPtr, Time, IncNumber, EnvInc, BrrPlace, GdbTime, AdsrNextTimeDepth;
        public uint TimeTemp, IncNTemp, EnvITemp;
        public int Prev0, Prev1;
        public ushort Pitch = 0xFFFE;
        public byte Status, State, Loop, End, VolumeR, VolumeL, VolumeRe, VolumeLe, EchoOn, GdbPos, GdbDat, AdsrBlocksLeft, StatTemp;
        public readonly int[] Psb = new int[24];   // PSampleBuf: [0..2] previous block tail, [3..18] block, [19..22] read-ahead
    }

    private readonly Voice[] v = new Voice[8];
    private readonly int[] dspBuffer = new int[BufSizeB];
    private readonly int[] echoBuffer = new int[BufSizeB];
    private readonly byte[] pmodBuffer = new byte[BufSizeB * 4];
    private readonly int[] echobuf = new int[16384];
    private readonly int[] filtLoop = new int[16], filtLoopR = new int[16];
    private readonly short[] output = new short[BufSizeB];
    private readonly int[] fir = new int[8];
    private byte globalVL, globalVR, echoVL, echoVR;
    private int echoFB;
    private uint maxEcho, cEchoPtr, curFiltPtr, noiseInc, noisePointer;
    private bool echoWrittenTo;
    private byte keyOnStA, keyOnStB;
    private ushort lastCx;                    // what CX held: ProcessPMod multiplies by CH:CL with only CL loaded

    public Zs151Dsp(byte[] soundRam) { ram = soundRam; Reset(); }

    public void Reset()
    {
        Array.Clear(Regs);
        for (int i = 0; i < 8; i++) v[i] = new Voice();
        Array.Clear(dspBuffer); Array.Clear(echoBuffer); Array.Clear(pmodBuffer); Array.Clear(echobuf);
        Array.Clear(filtLoop); Array.Clear(filtLoopR); Array.Clear(fir);
        globalVL = globalVR = echoVL = echoVR = 0; echoFB = 0;
        maxEcho = 172; cEchoPtr = 0; curFiltPtr = 0; noiseInc = 0; noisePointer = 0;
        echoWrittenTo = false; keyOnStA = keyOnStB = 0; lastCx = 0;
    }

    private ushort Ram16(int a) => (ushort)(ram[a & 0xFFFF] | ram[(a + 1) & 0xFFFF] << 8);
    private static uint PitchToFreq(ushort pitch) => (uint)(((ulong)(pitch & 0x3FFF) * 0x100000u) >> 8);   // * dspPAdj, shrd 8
    private static uint Div(uint a, uint b) => b == 0 ? 0xFFFFFFFF : a / b;   // the original would fault on 0

    // =================================================================================================================
    //  Register writes (dsp.asm WDSPRegXX)
    // =================================================================================================================

    public void Write(int reg, byte al)
    {
        reg &= 0x7F;
        int i = reg >> 4;
        switch (reg)
        {
            case 0x0C: Regs[reg] = al; globalVL = Vct(MusicVol, VolTableB(al)); return;
            case 0x1C: Regs[reg] = al; globalVR = Vct(MusicVol, VolTableB(al)); return;
            case 0x2C: Regs[reg] = al; echoVL = Vct(MusicVol, VolTableB(al)); return;
            case 0x3C: Regs[reg] = al; echoVR = Vct(MusicVol, VolTableB(al)); return;
            case 0x0D: Regs[reg] = al; echoFB = VolTableB(al); return;
            case 0x4C:   // key on: queued for the scanline pipeline
            {
                byte bl = al;
                if (Regs[0x5C] == 0xFF) bl = 0;   // (xor 5C,FF; jnz; and bl,[5C]=0)
                keyOnStA |= bl;
                Regs[0x4C] = al;
                Regs[0x7C] &= (byte)~al;
                return;
            }
            case 0x5C:   // key off: a 255-sample fade
                keyOnStA &= (byte)~al; keyOnStB &= (byte)~al;
                for (int k = 0; k < 8; k++)
                {
                    if ((al & (1 << k)) == 0) continue;
                    var x = v[k];
                    x.Time = 255;
                    x.IncNumber = (uint)-(int)(x.EnvInc >> 8);
                    x.State = 200;
                    Regs[k * 16 + 8] = 0;
                    Regs[0x7C] |= (byte)(1 << k);
                }
                Regs[0x5C] = al;
                return;
            case 0x6C:
                Regs[0x6C] = (byte)(al & 0x7F);
                if ((al & 0xC0) != 0) for (int k = 0; k < 8; k++) v[k].Status = 0;
                noiseInc = (uint)(((ulong)NoiseSpeeds[al & 0x1F] * 0x100000u) >> 17);
                return;
            case 0x7C: Regs[0x7C] = 0; return;
            case 0x7D: Regs[0x7D] = al; maxEcho = EchoRate[al & 0x0F]; return;
        }
        switch (reg & 0x0F)
        {
            case 0x0F: Regs[reg] = al; fir[i] = (sbyte)al; return;
            case 0x05: VoiceAdsr(i, al); return;
            case 0x06: VoiceAdsr2(i, al); return;
            case 0x07: VoiceGain(i, al); return;
        }
        Regs[reg] = al;
    }

    /// <summary>One scanline step of execute.asm's key-on pipeline (KeyOnStB -> ProcessKeyOn, KeyOnStA -> KeyOnStB).</summary>
    public void Scanline()
    {
        if (keyOnStB != 0) for (int k = 0; k < 8; k++) if ((keyOnStB & (1 << k)) != 0) VoiceStart(k);
        keyOnStB = keyOnStA;
        keyOnStA = 0;
    }

    private void VoiceAdsr(int i, byte al)
    {
        var x = v[i];
        if (x.State == 200 || Regs[i * 16 + 5] == al) { Regs[i * 16 + 5] = al; return; }
        if ((al & 0x80) != 0) { Regs[i * 16 + 5] = al; SwitchSustain(i); return; }
        if (x.Status != 1 || (Regs[i * 16 + 6] | Regs[i * 16 + 7] << 8) == 0xA0E0) { Regs[i * 16 + 5] = al; return; }
        if ((Regs[i * 16 + 5] & 0x80) != 0 && (x.State == 8 || x.State < 2))
        {
            Regs[i * 16 + 5] = al;
            ProcessGain(i);
            return;
        }
        Regs[i * 16 + 5] = al;
        if (x.State == 210) { VoiceStarter(i); x.EnvInc = 0x7FFFFF; }
        ProcessGain2(i);
    }

    private void VoiceAdsr2(int i, byte al)
    {
        var x = v[i];
        if (x.State == 200 || Regs[i * 16 + 6] == al) { Regs[i * 16 + 6] = al; return; }
        Regs[i * 16 + 6] = al;
        if ((Regs[i * 16 + 5] & 0x80) != 0) SwitchSustain(i);
    }

    private void VoiceGain(int i, byte al)
    {
        var x = v[i];
        if (x.State == 200 || Regs[i * 16 + 7] == al) { Regs[i * 16 + 7] = al; return; }
        Regs[i * 16 + 7] = al;
        if (x.Status != 1 || (Regs[i * 16 + 5] & 0x80) != 0) return;
        if (x.State == 210) VoiceStarter(i);
        ProcessGain2(i);
    }

    // ---- envelope set-ups shared by VoiceStart / ProcessGain / ProcessNextEnvelope

    private void AdsrDecayStart(int i)
    {
        var x = v[i];
        byte r5 = Regs[i * 16 + 5], r6 = Regs[i * 16 + 6];
        uint edx = DecayRate[(r5 >> 4) & 7], ebx = SustainRate[r6 & 0x1F];
        if (edx < ebx)
        {
            byte al = AdsrSustLevLoc[r6 >> 5];
            x.AdsrBlocksLeft = al;
            uint rest = ebx - edx;
            x.Time = Div(edx, al); x.GdbTime = x.Time;
            x.AdsrNextTimeDepth = Div(rest, (uint)(byte)(64 - al));
            x.EnvInc = 0x7FFFFF;
            x.GdbPos = 0; x.GdbDat = 127;
            x.IncNumber = (uint)-(int)Div(127 * 65536 - 122 * 65536, x.Time);
            x.State = 9;
        }
        else
        {
            DecayOver(i, edx, ebx);
            x.EnvInc = 0x7FFFFF;
            x.GdbPos = 0; x.GdbDat = 127;
            x.IncNumber = (uint)-(int)Div(127 * 65536 - 118 * 65536, x.Time);
            x.State = 7;
        }
    }

    private void DecayOver(int i, uint edx, uint ebx)
    {
        var x = v[i];
        uint d = edx - ebx;
        uint sl = (uint)((Regs[i * 16 + 6] >> 5) ^ 7);
        ebx += (uint)((ulong)d * sl / 7);
        ebx >>= 5;
        x.Time = ebx; x.GdbTime = ebx;
    }

    private void GainExpStart(int i)   // GAIN exponential decrease from full
    {
        var x = v[i];
        uint ebx = DecreaseRateExp[Regs[i * 16 + 7] & 0x1F] >> 5;
        x.EnvInc = 0x7FFFFF;
        x.Time = ebx; x.GdbTime = ebx;
        x.GdbPos = 0; x.GdbDat = 127;
        x.IncNumber = (uint)-(int)Div(127 * 65536 - 118 * 65536, ebx);
        x.State = 7;
    }

    private void ProcessGain(int i)
    {
        var x = v[i];
        byte g = Regs[i * 16 + 7];
        int r = g & 0x1F;
        if ((g & 0x80) == 0) { GainDirect(i); return; }
        if ((g & 0x40) == 0)
        {
            if ((g & 0x20) != 0) { GainExpStart(i); return; }
            uint ebx = Decrease[r];
            x.EnvInc = 0x7FFFFF; x.Time = ebx;
            x.IncNumber = (uint)-(int)Div(127 * 65536, ebx);
            x.State = 5;
            return;
        }
        uint inc = Increase[r];
        x.EnvInc = 0; x.Time = inc;
        x.IncNumber = Div(127 * 65536, inc);
        if ((g & 0x20) != 0) { x.Time = inc - (inc >> 2) - 1; x.State = 6; }
        else x.State = 3;
    }

    private void GainDirect(int i)
    {
        var x = v[i];
        x.EnvInc = (uint)(Regs[i * 16 + 7] & 0x7F) << 16;
        x.Time = 0xFFFFFFFF; x.IncNumber = 0;
        x.State = 4;
    }

    private void ProcessGain2(int i)   // a GAIN change while the voice plays: from the current level
    {
        var x = v[i];
        byte g = Regs[i * 16 + 7];
        int r = g & 0x1F;
        byte env = (byte)(x.EnvInc >> 16);
        if ((g & 0x80) == 0) { GainDirect(i); return; }
        if ((g & 0x40) == 0)
        {
            if ((g & 0x20) != 0)
            {
                uint ebx = DecreaseRateExp[r] >> 5;
                x.Time = ebx; x.GdbTime = ebx;
                uint eax = ((uint)env << 16) - ((uint)Vct(118, env) << 16);
                x.GdbPos = 0;
                x.IncNumber = (uint)-(int)Div(eax, ebx);
                x.GdbDat = env;
                x.State = 7;
                return;
            }
            uint dec = Decrease[r];
            x.Time = dec;
            x.IncNumber = (uint)-(int)Div((uint)env << 16, dec);
            x.State = 5;
            return;
        }
        uint inc = Increase[r];
        x.Time = inc;
        byte al = (byte)(env + 1);
        if ((al & 0x80) != 0) al = 127;
        al ^= 127;
        x.IncNumber = Div((uint)al << 16, inc);
        if ((g & 0x20) != 0) { x.Time = inc - (inc >> 2) - 1; x.State = 6; }
        else x.State = 3;
    }

    private void SwitchSustain(int i)
    {
        var x = v[i];
        x.GdbDat = (byte)(x.EnvInc >> 16);
        if (x.State == 8 || x.State < 2) x.GdbDat = 0x7F;
        byte r5 = Regs[i * 16 + 5], r6 = Regs[i * 16 + 6];
        uint edx = DecayRate[(r5 >> 4) & 7], ebx = SustainRate[r6 & 0x1F];
        if (edx < ebx)
        {
            byte al = AdsrSustLevLoc[r6 >> 5];
            x.AdsrBlocksLeft = al;
            uint rest = ebx - edx;
            x.Time = al == 0 ? edx : edx / al;   // .oopszero
            x.GdbTime = x.Time;
            x.AdsrNextTimeDepth = Div(rest, (uint)(byte)(64 - al));
            x.EnvInc = (uint)x.GdbDat << 16;
            byte env = x.GdbDat;
            uint eax = ((uint)env << 16) - ((uint)Vct(122, env) << 16);
            x.GdbPos = 0;
            x.IncNumber = (uint)-(int)Div(eax, x.Time);
            x.State = 9;
        }
        else
        {
            DecayOver(i, edx, ebx);
            x.EnvInc = 0x7FFFFF;
            byte env = 0x7F;
            uint eax = ((uint)env << 16) - ((uint)Vct(118, env) << 16);
            x.GdbPos = 0;
            x.IncNumber = (uint)-(int)Div(eax, x.Time);
            x.State = 7;
        }
    }

    // ---- key-on (VoiceStart macro / VoiceStarterM)

    private void VoiceStart(int i)
    {
        var x = v[i];
        bool fade = false;
        uint oldEnv = 0;
        if (x.Status != 0) { oldEnv = x.EnvInc; fade = true; }
        x.Status = 0;
        int b = i * 16;
        if (Regs[b] < 0x40 && Regs[b + 1] < 0x40
            && (Regs[b + 4] | Regs[b + 5] << 8 | Regs[b + 6] << 16 | Regs[b + 7] << 24) == 0x0050FF07 && Regs[0x5D] == 6)
        {
            Regs[b] = 15; Regs[b + 1] = 15;   // a game hack in the original
            return;
        }

        if ((Regs[b + 5] & 0x80) != 0)
        {
            int a = Regs[b + 5] & 0x0F;
            if (a != 0x0F)
            {
                uint ebx = AttackRate[a];
                x.Time = ebx;
                x.IncNumber = Div(127 * 65536, ebx);
                x.State = 8;
                x.EnvInc = 0;
                x.GdbDat = 0x7F;
            }
            else AdsrDecayStart(i);
        }
        else ProcessGain(i);   // the same five GAIN set-ups as VoiceStart's .gain
        x.Status = 1;

        if (fade)
        {
            x.TimeTemp = x.Time; x.IncNTemp = x.IncNumber; x.EnvITemp = x.EnvInc; x.StatTemp = x.State;
            x.EnvInc = oldEnv;
            x.Time = 127;
            x.IncNumber = (uint)-(int)(oldEnv >> 7);
            x.State = 210;
        }
        else
        {
            ushort pitch = (ushort)(Regs[b + 2] | Regs[b + 3] << 8);
            if (x.Pitch != pitch) { x.Pitch = pitch; x.Freq = PitchToFreq(pitch); }
            ResetSample(i);
            x.EchoOn = (byte)((Regs[0x4D] & (1 << i)) != 0 ? 1 : 0);
        }
        int dir = ((Regs[0x5D] << 8) + (Regs[b + 4] << 2)) & 0xFFFF;   // ax = ah:5D + dx (16-bit)
        x.Ptr = Ram16(dir);
        x.LoopPtr = Ram16(dir + 2);
    }

    private void ResetSample(int i)
    {
        var x = v[i];
        x.BrrPlace = 0x10000000;
        x.Prev0 = 0; x.Prev1 = 0;
        x.End = 0; x.Loop = 0;
        x.Psb[16] = x.Psb[17] = x.Psb[18] = 0;
    }

    private void VoiceStarter(int i)   // the end of the 127-sample fade: the new note for real
    {
        var x = v[i];
        x.Time = x.TimeTemp; x.IncNumber = x.IncNTemp; x.EnvInc = x.EnvITemp; x.State = x.StatTemp;
        x.EchoOn = (byte)((Regs[0x4D] & (1 << i)) != 0 ? 1 : 0);
        int b = i * 16;
        int dir = ((Regs[0x5D] << 8) + (Regs[b + 4] << 2)) & 0xFFFF;
        x.Ptr = Ram16(dir);
        x.LoopPtr = Ram16(dir + 2);
        ushort pitch = (ushort)(Regs[b + 2] | Regs[b + 3] << 8);
        if (x.Pitch != pitch) { x.Pitch = pitch; x.Freq = PitchToFreq(pitch); }
        ResetSample(i);
    }

    // =================================================================================================================
    //  Mixing (ProcessSoundBuffer / ProcessVoiceHandler16 / ProcessVoiceStuff / *StereoInterpolated / *StereoPM)
    // =================================================================================================================

    /// <summary>Mix one block of <see cref="BlockFrames"/> interleaved stereo frames (L, R) at <see cref="Rate"/>.</summary>
    public ReadOnlySpan<short> RenderBlock()
    {
        Array.Clear(dspBuffer);
        bool echoWrite = (Regs[0x6C] & 0x20) == 0;
        if (echoWrite) Array.Clear(echoBuffer);

        for (int i = 0; i < 8; i++) ProcessVoice(i);

        if (echoWrite)
        {
            echoWrittenTo = true;
            if (fir[0] == 0x7F && fir[1] == 0 && fir[2] == 0 && fir[3] == 0 && fir[4] == 0 && fir[5] == 0 && fir[6] == 0 && fir[7] == 0)
                MixEcho2();
            else
                MixEcho();
        }
        else if (echoWrittenTo)
        {
            Array.Clear(echobuf, 0, (int)Math.Min(maxEcho * 2, (uint)echobuf.Length));
            echoWrittenTo = false;
        }

        for (int k = 0; k < BufSizeB; k++)
        {
            int s = dspBuffer[k];
            output[k] = (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
        }
        return output;
    }

    private static int Mul(int sample, int factor) => (short)((short)sample * factor >> 7);   // imul cx; shr ax,7; or ah,dl*2

    private void SetVolumes(int i)
    {
        var x = v[i];
        byte l = VolumeTableD[Regs[i * 16 + 0]], r = VolumeTableD[Regs[i * 16 + 1]];
        x.VolumeRe = l; x.VolumeLe = r;
        x.VolumeR = Vct(globalVL, l);
        x.VolumeL = Vct(globalVR, r);
    }

    private void ProcessVoice(int i)
    {
        var x = v[i];
        if (x.Status != 1) return;
        int bit = 1 << i;

        // ProcessVoiceHandler16: which mixer
        bool pm = i > 0 && v[i - 1].Status == 1 && (Regs[0x2D] & bit) != 0 && Regs[i * 16 + 4] != Regs[(i - 1) * 16 + 4];
        bool echo = (Regs[0x3D] & bit) == 0 && x.EchoOn == 1;

        ushort pitch = (ushort)(Regs[i * 16 + 2] | Regs[i * 16 + 3] << 8);
        if (x.Pitch != pitch) { x.Pitch = pitch; x.Freq = PitchToFreq(pitch); }
        int esi = 0;

    skipProcess2:
        SetVolumes(i);
        bool unique = (Regs[0x3D] & bit) != 0 || (i < 7 && (Regs[0x2D] & (bit << 1)) != 0);

        while (true)
        {
            if (x.BrrPlace >= 0x10000000)
            {
                if (!ProcessBrr(i)) return;
                continue;
            }
            x.EnvInc += x.IncNumber;
            if (--x.Time == 0)
            {
                int r = NextEnvelope(i);
                if (r == 1) return;
                if (r == 2) goto skipProcess2;
            }
            Mix(i, esi, unique, pm, echo);
            esi += 2;
            if (esi == BufSizeB) break;
        }
        Regs[i * 16 + 8] = (byte)(x.EnvInc >> 16);   // ENVX
    }

    /// <returns>0 = go on mixing, 1 = the voice ended (next voice), 2 = restarted (back to SkipProcess2)</returns>
    private int NextEnvelope(int i)
    {
        var x = v[i];
        switch (x.State)
        {
            case 10:   // ADSRSustain: down the bend table towards zero
            {
                x.EnvInc = 0;
                int pos = x.GdbPos;
                byte al = Vct(AdsrBendData[pos], x.GdbDat);
                x.EnvInc = (uint)al << 16;
                byte dh = AdsrBendData[pos + 1];
                if (dh == 255) { x.State = 5; MuteGain(x); return 0; }
                byte dl = Vct(dh, x.GdbDat);
                x.Time = x.AdsrNextTimeDepth;
                uint eax = (uint)(byte)(al - dl) << 16;
                x.GdbPos++;
                x.IncNumber = (uint)-(int)Div(eax, x.Time);
                return 0;
            }
            case 9:    // ADSRDecayProc: the decay part of the bend table
            {
                x.EnvInc = 0;
                int pos = x.GdbPos;
                byte al = Vct(AdsrBendData[pos], x.GdbDat);
                x.EnvInc = (uint)al << 16;
                al = (byte)(al - Vct(AdsrBendData[pos + 1], x.GdbDat));
                x.Time = x.GdbTime;
                x.GdbPos++;
                x.IncNumber = (uint)-(int)Div((uint)al << 16, x.Time);
                if (--x.AdsrBlocksLeft == 0) x.State = 10;
                return 0;
            }
            case 7:    // DecreaseBent (GAIN exp. decrease, or a decay longer than the sustain)
            {
                int pos = x.GdbPos;
                byte dh = GainDecBendData[pos];
                x.EnvInc = 0;
                byte al = Vct(dh, x.GdbDat);
                x.EnvInc = (uint)al << 16;
                dh = GainDecBendData[pos + 1];
                if (dh == 255) { x.State = 5; MuteGain(x); return 0; }
                x.Time = x.GdbTime;
                al = (byte)(al - Vct(dh, x.GdbDat));
                x.GdbPos++;
                x.IncNumber = (uint)-(int)Div((uint)al << 16, x.Time);
                return 0;
            }
            case 8:    // attack done
                AdsrDecayStart(i);
                return 0;
            case 3:    // Blank: hold full
                x.EnvInc = 0x7F0000; x.IncNumber = 0; x.Time = 0xFFFFFFFF;
                return 0;
            case 5:
                MuteGain(x);
                return 0;
            case 6:    // IncreaseBent: the last quarter slower
                x.Time = Increase[Regs[i * 16 + 7] & 0x1F];
                x.IncNumber >>= 2;
                x.State = 3;
                return 0;
            case 210:  // EndofSamp2: the fade before a re-key-on is over
                x.EnvInc = 0; x.IncNumber = 0; x.State = 0;
                Regs[i * 16 + 8] = 0; Regs[i * 16 + 9] = 0;
                VoiceStarter(i);
                return 2;
            default:   // 4, 200 and the rest: EndofSamp
                x.EnvInc = 0; x.IncNumber = 0;
                x.Status = 0; x.State = 0;
                Regs[i * 16 + 8] = 0; Regs[i * 16 + 9] = 0;
                Regs[0x7C] |= (byte)(1 << i);
                return 1;
        }
    }

    private static void MuteGain(Voice x) { x.EnvInc = 0; x.IncNumber = 0; x.Time = 0xFFFFFFFF; }

    /// <returns>false when the voice ended.</returns>
    private bool ProcessBrr(int i)
    {
        var x = v[i];
        if (x.End == 1)
        {
            if (x.Loop != 1)
            {
                Regs[0x7C] |= (byte)(1 << i);
                Regs[i * 16 + 8] = 0;
                x.EnvInc = 0; x.IncNumber = 0; x.Status = 0;
                return false;
            }
            Regs[0x7C] |= (byte)(1 << i);
            x.Ptr = x.LoopPtr;
        }
        x.BrrPlace -= 0x10000000;
        var p = x.Psb;
        p[0] = p[16]; p[1] = p[17]; p[2] = p[18];
        BrrDecode(i, (int)x.Ptr);
        x.Ptr += 9;
        lastCx = (ushort)i;   // ecx = voice number around BRRDecode
        return true;
    }

    private void BrrDecode(int i, int ptr)
    {
        var x = v[i];
        byte h = ram[ptr & 0xFFFF];
        byte lastbl = (byte)(h & 1);
        byte loopbl = (byte)(lastbl == 1 && (h & 2) != 0 ? 1 : 0);
        int prev0 = x.Prev0, prev1 = x.Prev1;
        int filter0 = Filter[(h & 0x0C) >> 1];
        int shift = h >> 4;
        for (int o = 0; o < 16; o++)
        {
            byte d = ram[(ptr + 1 + (o >> 1)) & 0xFFFF];
            x.Psb[3 + o] = BrrSample((o & 1) == 0 ? d >> 4 : d & 0x0F, shift, filter0, ref prev0, ref prev1);
        }
        x.Prev0 = prev0; x.Prev1 = prev1;

        // BRR_decode_ahead (interpolation on): 4 samples of the next block, or of the loop block, or zeros
        int next = ptr + 9;
        bool clear = false;
        if (lastbl == 1)
        {
            if (loopbl == 1) next = (int)x.LoopPtr;
            else clear = true;
        }
        if (clear) { x.Psb[19] = x.Psb[20] = x.Psb[21] = x.Psb[22] = 0; }
        else
        {
            byte nh = ram[next & 0xFFFF];
            int nf = Filter[(nh & 0x0C) >> 1], ns = nh >> 4;
            byte d0 = ram[(next + 1) & 0xFFFF], d1 = ram[(next + 2) & 0xFFFF];
            x.Psb[19] = BrrSample(d0 >> 4, ns, nf, ref prev0, ref prev1);
            x.Psb[20] = BrrSample(d0 & 0x0F, ns, nf, ref prev0, ref prev1);
            x.Psb[21] = BrrSample(d1 >> 4, ns, nf, ref prev0, ref prev1);
            x.Psb[22] = BrrSample(d1 & 0x0F, ns, nf, ref prev0, ref prev1);
        }
        x.Loop = loopbl; x.End = lastbl;
    }

    private static int BrrSample(int nib, int shift, int filter0, ref int prev0, ref int prev1)
    {
        int eax = (nib ^ 8) - 8;
        if (shift <= 12) eax = (eax << shift) >> 1;
        else eax &= ~0x7FF;
        int edx = eax;
        switch (filter0)
        {
            case 240:
                edx += prev0 >> 1;
                edx += (-prev0) >> 5;
                break;
            case 488:
                edx += prev0;
                edx += (-(prev0 + (prev0 >> 1))) >> 5;
                edx -= prev1 >> 1;
                edx += prev1 >> 5;
                break;
            case 460:
                edx += prev0;
                edx += (-(((prev0 << 1) + prev0 << 2) + prev0)) >> 7;
                edx -= prev1 >> 1;
                edx += ((prev1 >> 1) + prev1) >> 4;
                break;
        }
        if (edx < -32768) edx = -32768;
        if (edx > 32767) edx = 32767;
        prev1 = prev0;
        edx = (short)(edx << 1);
        prev0 = edx;
        return edx;
    }

    private int Interpolate(Voice x, int idx)   // DSPInterpolate_4_mmx
    {
        int f = (int)(x.BrrPlace >> 16) & 0xFF;
        var p = x.Psb;
        int sum = p[idx + 2] * InterP[f * 4] + p[idx + 3] * InterP[f * 4 + 1] + p[idx + 4] * InterP[f * 4 + 2] + p[idx + 5] * InterP[f * 4 + 3];
        sum >>= 11;
        return sum > 32767 ? 32767 : sum < -32768 ? -32768 : sum;
    }

    private void Mix(int i, int esi, bool unique, bool pm, bool echo)
    {
        var x = v[i];
        int idx = (int)(x.BrrPlace >> 24);
        byte env = (byte)(x.EnvInc >> 16);
        if (pm) lastCx = (ushort)VolumeConvTable[(env << 8) | x.VolumeR];   // the PM mixers load CX first
        int s;
        bool noise = false;
        if (unique)
        {
            if ((Regs[0x3D] & (1 << i)) != 0)
            {
                noisePointer += noiseInc;
                s = NoiseData[noisePointer >> 18];
                noise = true;
            }
            else
            {
                // ProcessPMod: this voice's output (x env, CH left as it was) as a signed byte for the next voice
                short cx = (short)((lastCx & 0xFF00) | env);
                int prod = (short)x.Psb[3 + idx] * cx;
                pmodBuffer[esi] = (byte)(prod >> 15);
                s = 0;
            }
        }
        else s = 0;
        if (!noise) s = pm ? (short)x.Psb[3 + idx] : Interpolate(x, idx);

        short cR = VolumeConvTable[(env << 8) | x.VolumeR];
        short cL = VolumeConvTable[(env << 8) | x.VolumeL];
        dspBuffer[esi] += Mul(s, cR);
        dspBuffer[esi + 1] += Mul(s, cL);
        lastCx = (ushort)cL;
        if (echo)
        {
            short eR = VolumeConvTable[(env << 8) | x.VolumeRe];
            short eL = VolumeConvTable[(env << 8) | x.VolumeLe];
            echoBuffer[esi] += Mul(s, eR);
            echoBuffer[esi + 1] += Mul(s, eL);
            lastCx = (ushort)eL;
        }

        if (pm)
        {
            // CalculatePMod: read one frame late (esi already advanced)
            byte m = (byte)(pmodBuffer[esi + 2] + 0x80);
            x.BrrPlace += (uint)(((ulong)m * x.Freq) >> 7);
        }
        else x.BrrPlace += x.Freq;
    }

    private void MixEcho()
    {
        uint esi = cEchoPtr;
        for (int edi = 0; edi < BufSizeB; )
        {
            for (int ch = 0; ch < 2; ch++)
            {
                int[] loop = ch == 0 ? filtLoop : filtLoopR;
                int ebx = echobuf[esi & 0x3FFF];
                uint edx = curFiltPtr;
                loop[edx] = ebx;
                int ecx = (ebx * fir[0]) >> 7;
                for (int t = 1; t < 8; t++)
                {
                    edx = (edx - 2) & 0x0F;
                    ecx += (fir[t] * loop[edx]) >> 7;
                }
                dspBuffer[edi] += ecx;
                if (ch == 1) curFiltPtr = (curFiltPtr + 1) & 0x0F;
                int fb = (echoFB * ecx) >> 7;
                echobuf[esi & 0x3FFF] = ((echoBuffer[edi] * (ch == 0 ? echoVL : echoVR)) >> 7) + fb;
                esi++;
                edi++;
            }
            if (esi >= maxEcho * 2) esi = 0;
        }
        cEchoPtr = esi;
    }

    private void MixEcho2()   // FIR = $7F,0,0,0,0,0,0,0: no filter at all
    {
        uint esi = cEchoPtr;
        for (int edi = 0; edi < BufSizeB; )
        {
            for (int ch = 0; ch < 2; ch++)
            {
                int ecx = echobuf[esi & 0x3FFF];
                dspBuffer[edi] += ecx;
                int fb = (echoFB * ecx) >> 7;
                echobuf[esi & 0x3FFF] = ((echoBuffer[edi] * (ch == 0 ? echoVL : echoVR)) >> 7) + fb;
                esi++;
                edi++;
            }
            if (esi >= maxEcho * 2) esi = 0;
        }
        cEchoPtr = esi;
    }
}
