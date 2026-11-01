using System;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  libOpenSPC S-DSP sound engine (Brad Martin, 2002/2003), ported to C#.
//
//  Source: libOpenSPC, https://github.com/bmartin427/libopenspc, commit 2f68400 ("Initial import of 1-9-2003 snapshot",
//  the oldest commit in the history: the release tarball of the 2002 code line, before the 2004-2005 reformatting and the
//  2005 noise rewrite). Files ported: libopenspc/dsp.c (DSP_Update, AdvanceEnvelope, DSP_Reset), libopenspc/dsp.h
//  (voice_state, DSP_WRITE_7C), libopenspc/gauss.h (the Gaussian table), and the DSP register write in
//  libopenspc/SNEeSe/SPCimpl.c (_SPC_WRITE_DSP).
//  Copyright (c) 2002-2003 Brad Martin. License: GNU LGPL 2.1 or later. The SPC700 core of libOpenSPC came from SNEeSe
//  (Charles Bilyue et al., the Clarified Artistic License); it is NOT ported - only the three-line DSP write hook in
//  SPCimpl.c (a SNEeSe-licensed file) is reproduced in Write(). BrokenNes's own SMP drives this engine.
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (the user's call: quick and dirty first, polish later). The original's guesses and bugs are kept, they
//  are the sound:
//   - envelopes are Brad's reading of the docs: 11-bit ENVX, one counter (0x7800) shared by every rate, attack +1/64,
//     decay/sustain/exp-gain x255/256, release a straight -1/256 per sample (no click guard); ENVX is written >>8 in
//     release but >>4 elsewhere
//   - KON takes effect 8 samples late, KON/KOF are only looked at once per sample, KOF wins over KON
//   - noise is rand() (glibc's TYPE_3 generator, seed 1) re-rolled at the noise rate, not the LFSR
//   - BRR invalid ranges give nybble & ~0x7FF; filters are the doc shift-sums, clamped then doubled into 16 bits
//   - the end-of-sample voice zeroes its buffer but still outputs this sample with the old envelope
//   - pitch modulation reads the previous voice's OUTX (voice 0 gets 0); Gaussian taps masked &~2047 one by one
//   - echo: 8-tap FIR read before write, delay (EDL&15)*2 KB (EDL 0 = one stereo sample), feedback added on the voice
//     sum, 32-bit wrapping math, FLG bit 7 resets the DSP every sample while it is set
//  One change to fit BrokenNes: the echo buffer lives in a private 64 KB copy (echoRam), because the stock S-DSP keeps
//  running alongside and writes its own echo into the shared sound RAM. Left out: the SPC700, the .spc/.zst loaders,
//  the debug prints.
// =====================================================================================================================

/// <summary>The libOpenSPC S-DSP: feed it the DSP register writes and the sound RAM, call <see cref="Update"/> once per 32 kHz
/// sample for a stereo pair.</summary>
public sealed class OspcDsp
{
    public const int Rate = 32000;

    private struct Voice
    {
        public int Smp1, Smp2;
        public int MemPtr, HeaderCnt, Range, Filter, End, Half, MixFrac, EnvCnt, EnvX, Pitch, SampPtr, OnCnt;
        public int EnvState;    // ATTACK=0, DECAY, SUSTAIN, RELEASE
        public short S0, S1, S2, S3;   // sampbuf[4]
        public short this[int i]
        {
            get => (i & 3) switch { 0 => S0, 1 => S1, 2 => S2, _ => S3 };
            set { switch (i & 3) { case 0: S0 = value; break; case 1: S1 = value; break; case 2: S2 = value; break; default: S3 = value; break; } }
        }
    }

    private const int ATTACK = 0, DECAY = 1, SUSTAIN = 2, RELEASE = 3;

    private readonly byte[] ram;                         // SPC_RAM (shared with the SMP)
    private readonly byte[] echoRam = new byte[0x10000]; // the echo region (private, see the header)
    public readonly byte[] Regs = new byte[256];         // DSPregs
    private readonly Voice[] vs = new Voice[8];
    private int keyedOn, keys;
    private readonly short[] firL = new short[8], firR = new short[8];
    private int firPtr, echoPtr;
    private int noiseCnt, noiseLev;
    private const int mask = 0xFF;

    private const int CNT_INIT = 0x7800;
    private static readonly int[] ENVCNT = {
        0x0000,0x000F,0x0014,0x0018,0x001E,0x0028,0x0030,0x003C,
        0x0050,0x0060,0x0078,0x00A0,0x00C0,0x00F0,0x0140,0x0180,
        0x01E0,0x0280,0x0300,0x03C0,0x0500,0x0600,0x0780,0x0A00,
        0x0C00,0x0F00,0x1400,0x1800,0x1E00,0x2800,0x3C00,0x7800 };

    private static readonly int[] gauss = {
        0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000,
        0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x002, 0x002, 0x002, 0x002, 0x002,
        0x002, 0x002, 0x003, 0x003, 0x003, 0x003, 0x003, 0x004, 0x004, 0x004, 0x004, 0x004, 0x005, 0x005, 0x005, 0x005,
        0x006, 0x006, 0x006, 0x006, 0x007, 0x007, 0x007, 0x008, 0x008, 0x008, 0x009, 0x009, 0x009, 0x00A, 0x00A, 0x00A,
        0x00B, 0x00B, 0x00B, 0x00C, 0x00C, 0x00D, 0x00D, 0x00E, 0x00E, 0x00F, 0x00F, 0x00F, 0x010, 0x010, 0x011, 0x011,
        0x012, 0x013, 0x013, 0x014, 0x014, 0x015, 0x015, 0x016, 0x017, 0x017, 0x018, 0x018, 0x019, 0x01A, 0x01B, 0x01B,
        0x01C, 0x01D, 0x01D, 0x01E, 0x01F, 0x020, 0x020, 0x021, 0x022, 0x023, 0x024, 0x024, 0x025, 0x026, 0x027, 0x028,
        0x029, 0x02A, 0x02B, 0x02C, 0x02D, 0x02E, 0x02F, 0x030, 0x031, 0x032, 0x033, 0x034, 0x035, 0x036, 0x037, 0x038,
        0x03A, 0x03B, 0x03C, 0x03D, 0x03E, 0x040, 0x041, 0x042, 0x043, 0x045, 0x046, 0x047, 0x049, 0x04A, 0x04C, 0x04D,
        0x04E, 0x050, 0x051, 0x053, 0x054, 0x056, 0x057, 0x059, 0x05A, 0x05C, 0x05E, 0x05F, 0x061, 0x063, 0x064, 0x066,
        0x068, 0x06A, 0x06B, 0x06D, 0x06F, 0x071, 0x073, 0x075, 0x076, 0x078, 0x07A, 0x07C, 0x07E, 0x080, 0x082, 0x084,
        0x086, 0x089, 0x08B, 0x08D, 0x08F, 0x091, 0x093, 0x096, 0x098, 0x09A, 0x09C, 0x09F, 0x0A1, 0x0A3, 0x0A6, 0x0A8,
        0x0AB, 0x0AD, 0x0AF, 0x0B2, 0x0B4, 0x0B7, 0x0BA, 0x0BC, 0x0BF, 0x0C1, 0x0C4, 0x0C7, 0x0C9, 0x0CC, 0x0CF, 0x0D2,
        0x0D4, 0x0D7, 0x0DA, 0x0DD, 0x0E0, 0x0E3, 0x0E6, 0x0E9, 0x0EC, 0x0EF, 0x0F2, 0x0F5, 0x0F8, 0x0FB, 0x0FE, 0x101,
        0x104, 0x107, 0x10B, 0x10E, 0x111, 0x114, 0x118, 0x11B, 0x11E, 0x122, 0x125, 0x129, 0x12C, 0x130, 0x133, 0x137,
        0x13A, 0x13E, 0x141, 0x145, 0x148, 0x14C, 0x150, 0x153, 0x157, 0x15B, 0x15F, 0x162, 0x166, 0x16A, 0x16E, 0x172,
        0x176, 0x17A, 0x17D, 0x181, 0x185, 0x189, 0x18D, 0x191, 0x195, 0x19A, 0x19E, 0x1A2, 0x1A6, 0x1AA, 0x1AE, 0x1B2,
        0x1B7, 0x1BB, 0x1BF, 0x1C3, 0x1C8, 0x1CC, 0x1D0, 0x1D5, 0x1D9, 0x1DD, 0x1E2, 0x1E6, 0x1EB, 0x1EF, 0x1F3, 0x1F8,
        0x1FC, 0x201, 0x205, 0x20A, 0x20F, 0x213, 0x218, 0x21C, 0x221, 0x226, 0x22A, 0x22F, 0x233, 0x238, 0x23D, 0x241,
        0x246, 0x24B, 0x250, 0x254, 0x259, 0x25E, 0x263, 0x267, 0x26C, 0x271, 0x276, 0x27B, 0x280, 0x284, 0x289, 0x28E,
        0x293, 0x298, 0x29D, 0x2A2, 0x2A6, 0x2AB, 0x2B0, 0x2B5, 0x2BA, 0x2BF, 0x2C4, 0x2C9, 0x2CE, 0x2D3, 0x2D8, 0x2DC,
        0x2E1, 0x2E6, 0x2EB, 0x2F0, 0x2F5, 0x2FA, 0x2FF, 0x304, 0x309, 0x30E, 0x313, 0x318, 0x31D, 0x322, 0x326, 0x32B,
        0x330, 0x335, 0x33A, 0x33F, 0x344, 0x349, 0x34E, 0x353, 0x357, 0x35C, 0x361, 0x366, 0x36B, 0x370, 0x374, 0x379,
        0x37E, 0x383, 0x388, 0x38C, 0x391, 0x396, 0x39B, 0x39F, 0x3A4, 0x3A9, 0x3AD, 0x3B2, 0x3B7, 0x3BB, 0x3C0, 0x3C5,
        0x3C9, 0x3CE, 0x3D2, 0x3D7, 0x3DC, 0x3E0, 0x3E5, 0x3E9, 0x3ED, 0x3F2, 0x3F6, 0x3FB, 0x3FF, 0x403, 0x408, 0x40C,
        0x410, 0x415, 0x419, 0x41D, 0x421, 0x425, 0x42A, 0x42E, 0x432, 0x436, 0x43A, 0x43E, 0x442, 0x446, 0x44A, 0x44E,
        0x452, 0x455, 0x459, 0x45D, 0x461, 0x465, 0x468, 0x46C, 0x470, 0x473, 0x477, 0x47A, 0x47E, 0x481, 0x485, 0x488,
        0x48C, 0x48F, 0x492, 0x496, 0x499, 0x49C, 0x49F, 0x4A2, 0x4A6, 0x4A9, 0x4AC, 0x4AF, 0x4B2, 0x4B5, 0x4B7, 0x4BA,
        0x4BD, 0x4C0, 0x4C3, 0x4C5, 0x4C8, 0x4CB, 0x4CD, 0x4D0, 0x4D2, 0x4D5, 0x4D7, 0x4D9, 0x4DC, 0x4DE, 0x4E0, 0x4E3,
        0x4E5, 0x4E7, 0x4E9, 0x4EB, 0x4ED, 0x4EF, 0x4F1, 0x4F3, 0x4F5, 0x4F6, 0x4F8, 0x4FA, 0x4FB, 0x4FD, 0x4FF, 0x500,
        0x502, 0x503, 0x504, 0x506, 0x507, 0x508, 0x50A, 0x50B, 0x50C, 0x50D, 0x50E, 0x50F, 0x510, 0x511, 0x511, 0x512,
        0x513, 0x514, 0x514, 0x515, 0x516, 0x516, 0x517, 0x517, 0x517, 0x518, 0x518, 0x518, 0x518, 0x518, 0x519, 0x519,
    };
    // G1=&gauss[256], G2=&gauss[512], G3=&gauss[255], G4=&gauss[-1]
    private static int G1(int i) => gauss[256 + i];
    private static int G2(int i) => gauss[512 + i];
    private static int G3(int i) => gauss[255 + i];
    private static int G4(int i) => gauss[-1 + i];

    // glibc rand() (TYPE_3 additive feedback, default seed 1) - the original called rand() for noise
    private readonly int[] rnd = new int[34];
    private int rndPos;
    private void SeedRand()
    {
        rnd[0] = 1;
        for (int i = 1; i < 31; i++) { long w = 16807L * rnd[i - 1] % 2147483647; if (w < 0) w += 2147483647; rnd[i] = (int)w; }
        for (int i = 31; i < 34; i++) rnd[i] = rnd[i - 31];
        rndPos = 0;
        for (int i = 0; i < 310; i++) NextRaw();
    }
    private int NextRaw()
    {
        // r[k] = r[k-31] + r[k-3] over a 34-entry ring
        int k = rndPos;
        int v = unchecked(rnd[(k + 34 - 31) % 34] + rnd[(k + 34 - 3) % 34]);
        rnd[k] = v;
        rndPos = (k + 1) % 34;
        return v;
    }
    private int Rand() => (int)((uint)NextRaw() >> 1);

    public OspcDsp(byte[] soundRam) { ram = soundRam; SeedRand(); Reset(); }

    /// <summary>OSPC_Init's part for the DSP: clear the voices, then DSP_Reset.</summary>
    public void Reset()
    {
        Array.Clear(vs); Array.Clear(Regs); Array.Clear(echoRam);
        noiseLev = 0;
        DspReset();
    }

    /// <summary>_SPC_WRITE_DSP (SNEeSe/SPCimpl.c) with DSP_WRITE_7C: a write to ENDX clears it, everything else is stored.</summary>
    public void Write(int addr, byte value)
    {
        addr &= 0xFF;
        if (addr == 0x7C) Regs[0x7C] = 0;
        else Regs[addr] = value;
    }

    private void DspReset()
    {
        for (int i = 0; i < 8; i++) { firL[i] = firR[i] = 0; vs[i].OnCnt = 0; }
        firPtr = echoPtr = 0;
        keys = keyedOn = noiseCnt = 0;
        Regs[0x6C] |= 0xE0;
        Regs[0x4C] = Regs[0x5C] = 0;
    }

    private int SL(int v) => Regs[(v << 4) + 6] >> 5;
    private int SR(int v) => Regs[(v << 4) + 6] & 0x1F;

    private int AdvanceEnvelope(int v)
    {
        ref Voice vp = ref vs[v];
        int envx = vp.EnvX, cnt, adsr1, t;
        if (vp.EnvState == RELEASE)
        {
            envx -= 0x8;
            if (envx <= 0)
            {
                envx = 0;
                keys &= ~(1 << v);
                return -1;
            }
            vp.EnvX = envx;
            Regs[(v << 4) + 8] = (byte)(envx >> 8);
            return envx;
        }
        cnt = vp.EnvCnt;
        adsr1 = Regs[(v << 4) + 5];
        if ((adsr1 & 0x80) != 0)
        {
            switch (vp.EnvState)
            {
                case ATTACK:
                    t = adsr1 & 0xF;
                    if (t == 0xF) envx += 0x400;
                    else
                    {
                        cnt -= ENVCNT[(t << 1) + 1];
                        if (cnt > 0) break;
                        envx += 0x20;
                        cnt = CNT_INIT;
                    }
                    if (envx > 0x7FF) { envx = 0x7FF; vp.EnvState = DECAY; }
                    vp.EnvX = envx;
                    break;
                case DECAY:
                    cnt -= ENVCNT[((adsr1 >> 3) & 0xE) + 0x10];
                    if (cnt <= 0)
                    {
                        cnt = CNT_INIT;
                        envx -= ((envx - 1) >> 8) + 1;
                        vp.EnvX = envx;
                    }
                    if (envx <= 0x100 * (SL(v) + 1)) vp.EnvState = SUSTAIN;
                    break;
                case SUSTAIN:
                    cnt -= ENVCNT[SR(v)];
                    if (cnt > 0) break;
                    cnt = CNT_INIT;
                    envx -= ((envx - 1) >> 8) + 1;
                    vp.EnvX = envx;
                    break;
                case RELEASE:
                    break;
            }
        }
        else
        {
            t = Regs[(v << 4) + 7];
            if (t < 0x80) envx = vp.EnvX = t << 4;
            else switch (t >> 5)
            {
                case 4:
                    cnt -= ENVCNT[t & 0x1F];
                    if (cnt > 0) break;
                    cnt = CNT_INIT;
                    envx -= 0x020;
                    if (envx < 0) envx = 0;
                    vp.EnvX = envx;
                    break;
                case 5:
                    cnt -= ENVCNT[t & 0x1F];
                    if (cnt > 0) break;
                    cnt = CNT_INIT;
                    envx -= ((envx - 1) >> 8) + 1;
                    vp.EnvX = envx;
                    break;
                case 6:
                    cnt -= ENVCNT[t & 0x1F];
                    if (cnt > 0) break;
                    cnt = CNT_INIT;
                    envx += 0x020;
                    if (envx > 0x7FF) envx = 0x7FF;
                    vp.EnvX = envx;
                    break;
                case 7:
                    cnt -= ENVCNT[t & 0x1F];
                    if (cnt > 0) break;
                    cnt = CNT_INIT;
                    if (envx < 0x600) envx += 0x020;
                    else envx += 0x008;
                    if (envx > 0x7FF) envx = 0x7FF;
                    vp.EnvX = envx;
                    break;
            }
        }
        vp.EnvCnt = cnt;
        Regs[(v << 4) + 8] = (byte)(envx >> 4);
        return envx;
    }

    private ushort SdVptr(int sdBase, int n) { int a = (sdBase + n * 4) & 0xFFFF; return (ushort)(ram[a] | ram[(a + 1) & 0xFFFF] << 8); }
    private ushort SdLptr(int sdBase, int n) { int a = (sdBase + n * 4 + 2) & 0xFFFF; return (ushort)(ram[a] | ram[(a + 1) & 0xFFFF] << 8); }

    /// <summary>DSP_Update: one 32 kHz sample period.</summary>
    public void Update(out short left, out short right)
    {
        int v, V, m, envx, outl, outr, echol, echor, vl, vr;
        int echoBase;
        int outx;
        int sd = Regs[0x5D] << 8;

        if ((Regs[0x6C] & 0x80) != 0) DspReset();
        Regs[0x7C] &= (byte)~Regs[0x4C];
        outx = 0;
        if (Regs[0x3D] != 0)
        {
            noiseCnt -= ENVCNT[Regs[0x6C] & 0x1F];
            if (noiseCnt <= 0)
            {
                noiseCnt = CNT_INIT;
                noiseLev = (Rand() & 0xFFFF) - 0x8000;
            }
        }
        outl = outr = echol = echor = 0;
        for (v = 0, m = 1, V = 0; v < 8; v++, V += 16, m <<= 1)
        {
            ref Voice vp = ref vs[v];
            if (vp.OnCnt != 0 && (--vp.OnCnt == 0))
            {
                keys |= m;
                keyedOn |= m;
                vl = Regs[(v << 4) + 4];
                vp.MemPtr = SdVptr(sd, vl);
                vp.HeaderCnt = vp.Half = vp.EnvX = vp.End = vp.SampPtr = 0;
                vp.MixFrac = 3 * 4096;
                vp.EnvCnt = CNT_INIT;
                vp.EnvState = ATTACK;
            }
            if ((Regs[0x4C] & m & ~Regs[0x5C]) != 0)
            {
                Regs[0x4C] &= (byte)~m;
                vp.OnCnt = 8;
            }
            if ((keys & Regs[0x5C] & m) != 0)
            {
                vp.EnvState = RELEASE;
                vp.OnCnt = 0;
            }
            if ((keys & m & mask) == 0 || (envx = AdvanceEnvelope(v)) < 0)
            {
                Regs[V + 8] = Regs[V + 9] = 0;
                outx = 0;
                continue;
            }
            vp.Pitch = (Regs[V + 2] | Regs[V + 3] << 8) & 0x3FFF;
            if ((Regs[0x2D] & m) != 0)
                vp.Pitch = (int)((vp.Pitch * ((long)outx + 32768)) >> 15);
            for (; vp.MixFrac >= 0; vp.MixFrac -= 4096)
            {
                if (vp.HeaderCnt == 0)
                {
                    if ((vp.End & 1) != 0)
                    {
                        Regs[0x7C] |= (byte)m;
                        if ((vp.End & 2) != 0)
                        {
                            vp.MemPtr = SdLptr(sd, Regs[V + 4]);
                        }
                        else
                        {
                            keys &= ~m;
                            Regs[V + 8] = 0; vp.EnvX = 0;
                            while (vp.MixFrac >= 0)
                            {
                                vp[vp.SampPtr] = 0; outx = 0;
                                vp.SampPtr = (vp.SampPtr + 1) & 3;
                                vp.MixFrac -= 4096;
                            }
                            break;
                        }
                    }
                    vp.HeaderCnt = 8;
                    vl = ram[vp.MemPtr++ & 0xFFFF];
                    vp.Range = vl >> 4;
                    vp.End = vl & 3;
                    vp.Filter = (vl & 12) >> 2;
                }
                if (vp.Half == 0)
                {
                    vp.Half = 1;
                    outx = ((sbyte)ram[vp.MemPtr & 0xFFFF]) >> 4;
                }
                else
                {
                    vp.Half = 0;
                    outx = (sbyte)(ram[vp.MemPtr++ & 0xFFFF] << 4);
                    outx >>= 4;
                    vp.HeaderCnt--;
                }
                if (vp.Range <= 0xC) outx = (outx << vp.Range) >> 1;
                else outx &= ~0x7FF;
                switch (vp.Filter)
                {
                    case 0: break;
                    case 1:
                        outx += vp.Smp1 >> 1;
                        outx += (-vp.Smp1) >> 5;
                        break;
                    case 2:
                        outx += vp.Smp1;
                        outx += (-(vp.Smp1 + (vp.Smp1 >> 1))) >> 5;
                        outx -= vp.Smp2 >> 1;
                        outx += vp.Smp2 >> 5;
                        break;
                    case 3:
                        outx += vp.Smp1;
                        outx += (-(vp.Smp1 + (vp.Smp1 << 2) + (vp.Smp1 << 3))) >> 7;
                        outx -= vp.Smp2 >> 1;
                        outx += (vp.Smp2 + (vp.Smp2 >> 1)) >> 4;
                        break;
                }
                if (outx < -32768) outx = -32768;
                else if (outx > 32767) outx = 32767;
                vp.Smp2 = (short)vp.Smp1;
                short s = (short)(outx << 1);
                vp[vp.SampPtr] = s;
                vp.Smp1 = s;
                vp.SampPtr = (vp.SampPtr + 1) & 3;
            }
            if ((Regs[0x3D] & m) != 0)
            {
                outx = noiseLev;
            }
            else
            {
                vl = vp.MixFrac >> 4;
                vr = (G4(-vl) * vp[vp.SampPtr]) & ~2047;
                vr += (G3(-vl) * vp[(vp.SampPtr + 1) & 3]) & ~2047;
                vr += (G2(vl) * vp[(vp.SampPtr + 2) & 3]) & ~2047;
                vr += (G1(vl) * vp[(vp.SampPtr + 3) & 3]) & ~2047;
                outx = (short)(vr >> 11) & ~1;
            }

            vp.MixFrac += vp.Pitch;

            outx = ((outx * envx) >> 11) & ~1;
            Regs[V + 9] = (byte)(outx >> 8);

            vl = (((sbyte)Regs[V]) * outx) >> 7;
            vr = (((sbyte)Regs[V + 1]) * outx) >> 7;
            outl += vl;
            outr += vr;
            if ((Regs[0x4D] & m) != 0)
            {
                echol += vl;
                echor += vr;
            }
        }
        outl = (outl * (sbyte)Regs[0x0C]) >> 7;
        outr = (outr * (sbyte)Regs[0x1C]) >> 7;

        echoBase = ((Regs[0x6D] << 8) + echoPtr) & 0xFFFF;
        firL[firPtr] = (short)(echoRam[echoBase] | echoRam[(echoBase + 1) & 0xFFFF] << 8);
        firR[firPtr] = (short)(echoRam[(echoBase + 2) & 0xFFFF] | echoRam[(echoBase + 3) & 0xFFFF] << 8);

        vl = firL[firPtr] * (sbyte)Regs[0x7F];
        vr = firR[firPtr] * (sbyte)Regs[0x7F];
        for (int tap = 0x6F; tap >= 0x0F; tap -= 0x10)
        {
            firPtr = (firPtr + 1) & 7;
            vl += firL[firPtr] * (sbyte)Regs[tap];
            vr += firR[firPtr] * (sbyte)Regs[tap];
        }
        outl += vl * (sbyte)Regs[0x2C] >> 14;
        outr += vr * (sbyte)Regs[0x3C] >> 14;

        if ((Regs[0x6C] & 0x20) == 0)
        {
            echol += vl * (sbyte)Regs[0x0D] >> 14;
            if (echol > 32767) echol = 32767; else if (echol < -32768) echol = -32768;
            echor += vr * (sbyte)Regs[0x0D] >> 14;
            if (echor > 32767) echor = 32767; else if (echor < -32768) echor = -32768;
            echoRam[echoBase] = (byte)echol; echoRam[(echoBase + 1) & 0xFFFF] = (byte)(echol >> 8);
            echoRam[(echoBase + 2) & 0xFFFF] = (byte)echor; echoRam[(echoBase + 3) & 0xFFFF] = (byte)(echor >> 8);
        }
        echoPtr += 4;
        if (echoPtr >= ((Regs[0x7D] & 0xF) << 11)) echoPtr = 0;

        if ((Regs[0x6C] & 0x40) != 0) { left = 0; right = 0; }
        else
        {
            left = (short)(outl > 32767 ? 32767 : outl < -32768 ? -32768 : outl);
            right = (short)(outr > 32767 ? 32767 : outr < -32768 ? -32768 : outr);
        }
    }
}
