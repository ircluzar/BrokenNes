using System;

namespace NesEmulator.Snes;

/// <summary>
/// Sony S-DSP (the SNES sound generator) - SFC family.
///
/// Whole-sample model: <see cref="RunSample"/> produces one 32 kHz stereo sample, stepping all 8
/// voices and then the echo unit. The real chip spreads that work over 32 clocks with voices
/// pipelined against each other. The per-voice ORDER is kept (read BRR header/byte, interpolate,
/// envelope, then decode and advance the pitch counter), and so is the arithmetic: Gaussian
/// interpolation, BRR filters, the envelope rate counter, and the echo FIR's intermediate
/// truncations. The sub-sample timing of register reads is not. A game that rewrites DSP
/// registers mid-sample hears the change up to one sample later than on hardware.
/// </summary>
public sealed class DSP_SFC
{
    private readonly byte[] ram;          // the audio unit's 64KB ARAM (BRR samples, echo buffer)
    public readonly byte[] Regs = new byte[128];

    // Global register addresses
    private const int MVOLL = 0x0C, MVOLR = 0x1C, EVOLL = 0x2C, EVOLR = 0x3C, KON = 0x4C, KOFF = 0x5C,
        FLG = 0x6C, ENDX = 0x7C, EFB = 0x0D, PMON = 0x2D, NON = 0x3D, EON = 0x4D, DIR = 0x5D, ESA = 0x6D, EDL = 0x7D;

    private enum EnvMode { Release, Attack, Decay, Sustain }

    private sealed class Voice
    {
        public readonly int[] Buf = new int[24];   // 12 decoded samples, stored twice so reads never wrap
        public int BufPos, InterpPos, BrrAddr, BrrOffset = 1, KonDelay, Env, HiddenEnv;
        public EnvMode Mode;
    }

    private readonly Voice[] voices = new Voice[8];
    private byte newKon, kon, koff;
    private bool everyOther;
    private int counter;
    private int noise = 0x4000;
    private int echoOffset, echoLength, echoHistPos;
    private readonly int[,] echoHist = new int[16, 2];   // 8 taps, doubled so FIR reads never wrap
    private int echoOutL, echoOutR;

    /// <summary>Called once per produced sample (left, right).</summary>
    public Action<short, short>? Output;

    public DSP_SFC(byte[] aram)
    {
        ram = aram;
        for (int i = 0; i < 8; i++) voices[i] = new Voice();
        Reset();
    }

    public void Reset()
    {
        Array.Clear(Regs);
        Regs[FLG] = 0xE0;   // soft reset + mute + echo writes off
        foreach (var v in voices)
        {
            Array.Clear(v.Buf);
            v.BufPos = v.InterpPos = v.BrrAddr = v.KonDelay = v.Env = v.HiddenEnv = 0;
            v.BrrOffset = 1; v.Mode = EnvMode.Release;
        }
        newKon = kon = koff = 0;
        everyOther = false; counter = 0; noise = 0x4000;
        echoOffset = echoLength = echoHistPos = 0;
        Array.Clear(echoHist);
        echoOutL = echoOutR = 0;
    }

    public byte Read(int addr) => Regs[addr & 0x7F];

    public void Write(int addr, byte value)
    {
        addr &= 0x7F;
        Regs[addr] = value;
        if (addr == KON) newKon = value;
        else if (addr == ENDX) Regs[ENDX] = 0;   // any write clears all end flags
    }

    // =====================================================================================
    //  Tables (hardware)
    // =====================================================================================

    private static readonly short[] Gauss =
    {
           0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
           1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   2,   2,   2,   2,   2,
           2,   2,   3,   3,   3,   3,   3,   4,   4,   4,   4,   4,   5,   5,   5,   5,
           6,   6,   6,   6,   7,   7,   7,   8,   8,   8,   9,   9,   9,  10,  10,  10,
          11,  11,  11,  12,  12,  13,  13,  14,  14,  15,  15,  15,  16,  16,  17,  17,
          18,  19,  19,  20,  20,  21,  21,  22,  23,  23,  24,  24,  25,  26,  27,  27,
          28,  29,  29,  30,  31,  32,  32,  33,  34,  35,  36,  36,  37,  38,  39,  40,
          41,  42,  43,  44,  45,  46,  47,  48,  49,  50,  51,  52,  53,  54,  55,  56,
          58,  59,  60,  61,  62,  64,  65,  66,  67,  69,  70,  71,  73,  74,  76,  77,
          78,  80,  81,  83,  84,  86,  87,  89,  90,  92,  94,  95,  97,  99, 100, 102,
         104, 106, 107, 109, 111, 113, 115, 117, 118, 120, 122, 124, 126, 128, 130, 132,
         134, 137, 139, 141, 143, 145, 147, 150, 152, 154, 156, 159, 161, 163, 166, 168,
         171, 173, 175, 178, 180, 183, 186, 188, 191, 193, 196, 199, 201, 204, 207, 210,
         212, 215, 218, 221, 224, 227, 230, 233, 236, 239, 242, 245, 248, 251, 254, 257,
         260, 263, 267, 270, 273, 276, 280, 283, 286, 290, 293, 297, 300, 304, 307, 311,
         314, 318, 321, 325, 328, 332, 336, 339, 343, 347, 351, 354, 358, 362, 366, 370,
         374, 378, 381, 385, 389, 393, 397, 401, 405, 410, 414, 418, 422, 426, 430, 434,
         439, 443, 447, 451, 456, 460, 464, 469, 473, 477, 482, 486, 491, 495, 499, 504,
         508, 513, 517, 522, 527, 531, 536, 540, 545, 550, 554, 559, 563, 568, 573, 577,
         582, 587, 592, 596, 601, 606, 611, 615, 620, 625, 630, 635, 640, 644, 649, 654,
         659, 664, 669, 674, 678, 683, 688, 693, 698, 703, 708, 713, 718, 723, 728, 732,
         737, 742, 747, 752, 757, 762, 767, 772, 777, 782, 787, 792, 797, 802, 806, 811,
         816, 821, 826, 831, 836, 841, 846, 851, 855, 860, 865, 870, 875, 880, 884, 889,
         894, 899, 904, 908, 913, 918, 923, 927, 932, 937, 941, 946, 951, 955, 960, 965,
         969, 974, 978, 983, 988, 992, 997,1001,1005,1010,1014,1019,1023,1027,1032,1036,
        1040,1045,1049,1053,1057,1061,1066,1070,1074,1078,1082,1086,1090,1094,1098,1102,
        1106,1109,1113,1117,1121,1125,1128,1132,1136,1139,1143,1146,1150,1153,1157,1160,
        1164,1167,1170,1174,1177,1180,1183,1186,1190,1193,1196,1199,1202,1205,1207,1210,
        1213,1216,1219,1221,1224,1227,1229,1232,1234,1237,1239,1241,1244,1246,1248,1251,
        1253,1255,1257,1259,1261,1263,1265,1267,1269,1270,1272,1274,1275,1277,1279,1280,
        1282,1283,1284,1286,1287,1288,1290,1291,1292,1293,1294,1295,1296,1297,1297,1298,
        1299,1300,1300,1301,1302,1302,1303,1303,1303,1304,1304,1304,1304,1304,1305,1305,
    };

    // Envelope/noise rate counter: rate r fires when (counter + offset[r]) % period[r] == 0.
    private const int CounterRange = 2048 * 5 * 3;
    private static readonly int[] RatePeriod =
    {
        CounterRange + 1, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80,
        64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1,
    };
    private static readonly int[] RateOffset =
    {
        1, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536,
        0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 0, 0,
    };

    private bool RateFires(int rate) => (counter + RateOffset[rate]) % RatePeriod[rate] == 0;

    private static int Clamp16(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;

    // =====================================================================================
    //  One sample
    // =====================================================================================

    public void RunSample()
    {
        // KON/KOFF are only sampled every other sample (the chip polls them at 16 kHz).
        everyOther = !everyOther;
        if (everyOther)
        {
            newKon &= (byte)~kon;
            kon = newKon;
            koff = Regs[KOFF];
        }
        if (--counter < 0) counter = CounterRange - 1;
        if (RateFires(Regs[FLG] & 0x1F))
        {
            int feedback = (noise << 13) ^ (noise << 14);
            noise = (feedback & 0x4000) ^ (noise >> 1);
        }

        byte pmon = (byte)(Regs[PMON] & 0xFE), non = Regs[NON], eon = Regs[EON];
        int mainL = 0, mainR = 0, echoL = 0, echoR = 0;
        int prevOutput = 0;
        byte endx = Regs[ENDX];

        for (int i = 0; i < 8; i++)
        {
            var v = voices[i];
            int bit = 1 << i, r = i << 4;

            // Sample pointer: start address while keying on, loop address afterwards.
            int dirEntry = (Regs[DIR] * 0x100 + Regs[r + 4] * 4) & 0xFFFF;
            if (v.KonDelay == 0) dirEntry += 2;
            int nextAddr = ram[dirEntry & 0xFFFF] | ram[(dirEntry + 1) & 0xFFFF] << 8;

            int pitch = Regs[r + 2] | (Regs[r + 3] & 0x3F) << 8;
            int brrByte = ram[(v.BrrAddr + v.BrrOffset) & 0xFFFF];
            int header = ram[v.BrrAddr & 0xFFFF];

            if ((pmon & bit) != 0) pitch += ((prevOutput >> 5) * pitch) >> 10;

            if (v.KonDelay != 0)
            {
                if (v.KonDelay == 5)
                {
                    v.BrrAddr = nextAddr; v.BrrOffset = 1; v.BufPos = 0;
                    header = 0;   // header is ignored on this sample
                }
                v.Env = 0; v.HiddenEnv = 0;
                v.InterpPos = (--v.KonDelay & 3) != 0 ? 0x4000 : 0;
                pitch = 0;
            }

            int output = (non & bit) != 0 ? (short)(noise * 2) : Interpolate(v);
            output = ((output * v.Env) >> 11) & ~1;
            Regs[r + 8] = (byte)(v.Env >> 4);   // ENVX

            if ((Regs[FLG] & 0x80) != 0 || (header & 3) == 1) { v.Mode = EnvMode.Release; v.Env = 0; }

            if (everyOther)
            {
                if ((koff & bit) != 0) v.Mode = EnvMode.Release;
                if ((kon & bit) != 0) { v.KonDelay = 5; v.Mode = EnvMode.Attack; }
            }
            if (v.KonDelay == 0) RunEnvelope(v, r);

            // Decode the next 4 BRR samples once interpolation has moved past them.
            bool looped = false;
            if (v.InterpPos >= 0x4000)
            {
                DecodeBrr(v, header, brrByte);
                if ((v.BrrOffset += 2) >= 9)
                {
                    v.BrrAddr = (v.BrrAddr + 9) & 0xFFFF;
                    if ((header & 1) != 0) { v.BrrAddr = nextAddr; looped = true; }
                    v.BrrOffset = 1;
                }
            }
            v.InterpPos = Math.Min((v.InterpPos & 0x3FFF) + pitch, 0x7FFF);

            int ampL = (output * (sbyte)Regs[r]) >> 7, ampR = (output * (sbyte)Regs[r + 1]) >> 7;
            mainL = Clamp16(mainL + ampL); mainR = Clamp16(mainR + ampR);
            if ((eon & bit) != 0) { echoL = Clamp16(echoL + ampL); echoR = Clamp16(echoR + ampR); }

            if (looped) endx |= (byte)bit;
            if (v.KonDelay == 5) endx &= (byte)~bit;
            Regs[r + 9] = (byte)(output >> 8);   // OUTX
            prevOutput = output;
        }
        Regs[ENDX] = endx;

        RunEcho(mainL, mainR, echoL, echoR);
    }

    private int Interpolate(Voice v)
    {
        int p = (v.InterpPos >> 12) + v.BufPos;
        int offset = (v.InterpPos >> 4) & 0xFF;
        int fwd = 255 - offset, rev = offset;
        int out0 = (Gauss[fwd] * v.Buf[p]) >> 11;
        out0 += (Gauss[fwd + 256] * v.Buf[p + 1]) >> 11;
        out0 += (Gauss[rev + 256] * v.Buf[p + 2]) >> 11;
        out0 = (short)out0;
        out0 += (Gauss[rev] * v.Buf[p + 3]) >> 11;
        return Clamp16(out0) & ~1;
    }

    private void DecodeBrr(Voice v, int header, int firstByte)
    {
        int nybbles = firstByte << 8 | ram[(v.BrrAddr + v.BrrOffset + 1) & 0xFFFF];
        int shift = header >> 4, filter = header & 0x0C;
        int pos = v.BufPos;
        v.BufPos = v.BufPos + 4 >= 12 ? 0 : v.BufPos + 4;

        for (int i = 0; i < 4; i++, pos++, nybbles <<= 4)
        {
            int s = (short)nybbles >> 12;                 // sign-extended nybble
            s = (s << shift) >> 1;
            if (shift >= 0x0D) s = (s >> 25) << 11;       // invalid shifts clamp to 0 / -2048

            int p1 = v.Buf[pos + 11], p2 = v.Buf[pos + 10] >> 1;   // previous two samples (wrapped copy)
            if (filter >= 8)
            {
                s += p1 - p2;
                if (filter == 8) { s += p2 >> 4; s += (p1 * -3) >> 6; }
                else { s += (p1 * -13) >> 7; s += (p2 * 3) >> 4; }
            }
            else if (filter != 0) { s += p1 >> 1; s += (-p1) >> 5; }

            s = (short)(Clamp16(s) * 2);
            v.Buf[pos] = v.Buf[pos + 12] = s;
        }
    }

    private void RunEnvelope(Voice v, int r)
    {
        int env = v.Env;
        if (v.Mode == EnvMode.Release)
        {
            v.Env = Math.Max(env - 8, 0);
            return;
        }

        int rate, envData = Regs[r + 6];   // ADSR2
        byte adsr1 = Regs[r + 5];
        if ((adsr1 & 0x80) != 0)
        {
            if (v.Mode >= EnvMode.Decay)
            {
                env--;
                env -= env >> 8;
                rate = envData & 0x1F;
                if (v.Mode == EnvMode.Decay) rate = ((adsr1 >> 3) & 0x0E) + 0x10;
            }
            else
            {
                rate = (adsr1 & 0x0F) * 2 + 1;
                env += rate < 31 ? 0x20 : 0x400;
            }
        }
        else
        {
            envData = Regs[r + 7];   // GAIN
            int mode = envData >> 5;
            if (mode < 4) { env = envData * 0x10; rate = 31; }
            else
            {
                rate = envData & 0x1F;
                if (mode == 4) env -= 0x20;
                else if (mode < 6) { env--; env -= env >> 8; }
                else
                {
                    env += 0x20;
                    if (mode > 6 && (uint)v.HiddenEnv >= 0x600) env += 0x8 - 0x20;   // bent line
                }
            }
        }

        if ((env >> 8) == (envData >> 5) && v.Mode == EnvMode.Decay) v.Mode = EnvMode.Sustain;
        v.HiddenEnv = env;
        if ((uint)env > 0x7FF)
        {
            env = env < 0 ? 0 : 0x7FF;
            if (v.Mode == EnvMode.Attack) v.Mode = EnvMode.Decay;
        }
        if (RateFires(rate)) v.Env = env;
    }

    // =====================================================================================
    //  Echo
    // =====================================================================================

    private void RunEcho(int mainL, int mainR, int echoL, int echoR)
    {
        int ptr = (Regs[ESA] * 0x100 + echoOffset) & 0xFFFF;

        // Read the oldest echo sample into the history ring (doubled so the FIR never wraps).
        echoHistPos = (echoHistPos + 1) & 7;
        int inL = (short)(ram[ptr] | ram[(ptr + 1) & 0xFFFF] << 8) >> 1;
        int inR = (short)(ram[(ptr + 2) & 0xFFFF] | ram[(ptr + 3) & 0xFFFF] << 8) >> 1;
        echoHist[echoHistPos, 0] = echoHist[echoHistPos + 8, 0] = inL;
        echoHist[echoHistPos, 1] = echoHist[echoHistPos + 8, 1] = inR;

        int firL = Fir(0), firR = Fir(1);

        int outL = Clamp16((short)((mainL * (sbyte)Regs[MVOLL]) >> 7) + (short)((firL * (sbyte)Regs[EVOLL]) >> 7));
        int outR = Clamp16((short)((mainR * (sbyte)Regs[MVOLR]) >> 7) + (short)((firR * (sbyte)Regs[EVOLR]) >> 7));

        echoOutL = Clamp16(echoL + (short)((firL * (sbyte)Regs[EFB]) >> 7)) & ~1;
        echoOutR = Clamp16(echoR + (short)((firR * (sbyte)Regs[EFB]) >> 7)) & ~1;

        if ((Regs[FLG] & 0x20) == 0)
        {
            ram[ptr] = (byte)echoOutL; ram[(ptr + 1) & 0xFFFF] = (byte)(echoOutL >> 8);
            ram[(ptr + 2) & 0xFFFF] = (byte)echoOutR; ram[(ptr + 3) & 0xFFFF] = (byte)(echoOutR >> 8);
        }

        if (echoOffset == 0) echoLength = (Regs[EDL] & 0x0F) * 0x800;
        echoOffset += 4;
        if (echoOffset >= echoLength) echoOffset = 0;

        if ((Regs[FLG] & 0x40) != 0) { outL = 0; outR = 0; }
        Output?.Invoke((short)outL, (short)outR);
    }

    /// <summary>8-tap FIR over the echo history, oldest tap first, with the chip's 16-bit wrap before tap 7.</summary>
    private int Fir(int ch)
    {
        int sum = 0;
        for (int i = 0; i < 7; i++) sum += (echoHist[echoHistPos + i + 1, ch] * (sbyte)Regs[i * 0x10 + 0x0F]) >> 6;
        sum = (short)sum;
        sum += (short)((echoHist[echoHistPos + 8, ch] * (sbyte)Regs[0x7F]) >> 6);
        return Clamp16(sum) & ~1;
    }
}
