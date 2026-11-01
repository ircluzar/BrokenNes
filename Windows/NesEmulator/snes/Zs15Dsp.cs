using System;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  ZSNES 0.15 S-DSP sound engine, ported to C#.
//
//  Source: zsnes-asm2c (https://gitlab.com/ReverendGumby/zsnes-asm2c, dsp.asm + the tables in mach.c), David Hunter's
//  reconstruction of the ZSNES 0.15 (1997) sound code from the 0.15 binary and the ZSNES 1.17b source; the engine
//  ZSnes9x / snes9x-cursed (https://gitlab.com/ReverendGumby/snes9x-cursed) transplants into Snes9x.
//  Copyright (C) 1997-2001 ZSNES Team (zsKnight, _Demo_); Copyright (C) 2023 David Hunter.
//  License: GPL-2.0-or-later (dsp.asm) / GPL-3.0-or-later (zsnes-asm2c). This is a documented exception to the
//  repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (the user's call: quick and dirty first, polish later). The bugs and shortcuts of the original are kept
//  on purpose; they are the sound:
//   - mono, mixed straight at 44,100 Hz in blocks of 320 samples (register changes land on block boundaries)
//   - a voice's volume is the larger of its left and right volumes through a lookup table; no master volume
//   - no interpolation (nearest sample), no echo, no noise, no pitch modulation, FLG ignored
//   - key-off cuts the voice dead (no release); the ADSR decay step is computed and then zeroed, so the envelope jumps
//   - a looping sample restarts one BRR block BEFORE its loop point; the envelope can touch 128 for one sample at the
//     end of the attack (a signed volume flip)
//   - a voice whose volume is 0 is frozen, not advanced
//  Left out: the decoded-block cache (spcRamcmp/spcPrevbf; it only saves work, the output is the same) and the
//  SPC700 (BrokenNes's own SMP drives this engine through the DSP register writes).
// =====================================================================================================================

/// <summary>The ZSNES 0.15 S-DSP: feed it the DSP register writes and the sound RAM, pull 44.1 kHz mono blocks.</summary>
public sealed class Zs15Dsp
{
    public const int Rate = 44100;          // SBToSPCSpeeds[SoundQuality = 3]
    public const int BlockSamples = 320;    // BufferSize (0xA0) * 2 samples per SBHandler16 call

    private readonly byte[] ram;            // spcRam (shared with the SMP)
    public readonly byte[] Regs = new byte[256];   // DSPMem

    // ---- tables (mach.c), the rate tables already through conv2speed (x 44100 / 11025 = x 4, from index 1 for the 32-entry ones)
    private static readonly int[] Filter = { 0, 0, 240, 0, 488, -240, 460, -208 };
    private static readonly byte[] VolumeTable = BuildVolumeTable();
    private static readonly uint[] AttackRate = Scale(new uint[] { 45202, 28665, 16537, 11025, 7056, 4189, 2866, 1764, 1058, 705, 441, 264, 176, 110, 66, 1 }, 0);
    private static readonly uint[] DecayRate = Scale(new uint[] { 13230, 8158, 4851, 3197, 1984, 1212, 815, 407 }, 0);
    private static readonly uint[] SustainRate = Scale(new uint[] {
        0xFFFFFFFF, 418950, 308700, 265600, 209475, 154350, 132300, 103635, 78277, 65047, 51817, 38587, 31972, 26460, 19845, 16537,
        13230, 9702, 8158, 6504, 4851, 4079, 3197, 2425, 1984, 1653, 1212, 1014, 815, 606, 407, 308 }, 1);
    private static readonly byte[] SustainValue = { 15, 31, 47, 63, 89, 95, 111, 127 };   // sic: 89, not 79
    private static readonly uint[] Increase = Scale(new uint[] {
        0xFFFFFFFF, 45202, 34177, 28665, 22050, 16537, 14332, 11025, 8489, 7056, 5622, 4189, 3528, 2866, 2094, 1764,
        1433, 1058, 882, 705, 529, 441, 352, 264, 220, 176, 132, 110, 88, 66, 44, 22 }, 1);
    private static readonly uint[] IncreaseBent = Scale(new uint[] {
        0xFFFFFFFF, 79380, 59535, 50715, 38587, 28665, 25357, 19845, 14332, 12127, 9922, 7386, 6394, 4961, 3748, 3087,
        2425, 1874, 1543, 1212, 926, 771, 617, 463, 385, 308, 231, 198, 154, 121, 77, 38 }, 1);
    private static readonly uint[] Decrease = Increase;   // the same numbers in the original
    private static readonly uint[] DecreaseRateExp = Scale(new uint[] {
        0xFFFFFFFF, 418950, 308700, 264600, 209470, 154350, 132300, 103635, 78277, 65047, 51817, 38587, 31972, 26460, 19845, 16537,
        13230, 9702, 8158, 6504, 4851, 4079, 3197, 2425, 1984, 1653, 1212, 1014, 815, 606, 407, 198 }, 1);
    private static readonly byte[] VolumeConvTable = BuildVolumeConvTable();

    private static uint[] Scale(uint[] t, int from)
    {
        var r = (uint[])t.Clone();
        for (int i = from; i < r.Length; i++) r[i] = (uint)((ulong)r[i] * Rate / 11025);
        return r;
    }

    private static byte[] BuildVolumeTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 64; i++) t[i] = (byte)(i * 2);
        for (int i = 64; i < 193; i++) t[i] = 126;
        for (int i = 193; i < 256; i++) t[i] = (byte)((255 - i) * 2);
        return t;
    }

    private static byte[] BuildVolumeConvTable()
    {
        // InitSPC: al = dh; imul dl; shr ax,7  (signed 8x8, then a logical shift of the 16-bit product)
        var t = new byte[32768];
        for (int e = 0; e < 32768; e++)
        {
            ushort ax = (ushort)(short)((sbyte)(e >> 8) * (sbyte)(e & 0xFF));
            t[e] = (byte)(ax >> 7);
        }
        return t;
    }

    // ---- per-voice state (Voice0xxx + %1)
    private struct Voice
    {
        public uint Ptr, LoopPtr, Freq, Time, IncNumber, EnvInc, GainTime, Attack, Decay, SustainR, DecreaseNumber, SLenNumber, SustainR2, SEndNumber, SEndLNumber, BrrPlace;
        public int Prev0, Prev1;
        public ushort Pitch;
        public byte Status, State, GainType, SustainL, SustainL2, End, Loop, Volume;
    }

    private readonly Voice[] v = new Voice[8];
    private readonly short[][] block = { new short[16], new short[16], new short[16], new short[16], new short[16], new short[16], new short[16], new short[16] };
    private readonly short[] mix = new short[BlockSamples];

    /// <summary>Voices to leave out (bit n = voice n), the original's VoiceNDisable.</summary>
    public byte DisableMask;

    public Zs15Dsp(byte[] soundRam) { ram = soundRam; Reset(); }

    public void Reset()
    {
        Array.Clear(Regs);
        for (int i = 0; i < 8; i++)
        {
            v[i] = default;
            v[i].Attack = 1; v[i].Decay = 1;           // mach_init_mem
            v[i].GainTime = 0xFFFFFFFF;                 // a key-on before any GAIN write would divide by zero in the original
            v[i].BrrPlace = 0x100000;
            Array.Clear(block[i]);
        }
    }

    private ushort Ram16(int a) => (ushort)(ram[a & 0xFFFF] | ram[(a + 1) & 0xFFFF] << 8);

    // =================================================================================================================
    //  Register writes (WDSPRegXX)
    // =================================================================================================================

    public byte Read(int reg) => Regs[reg & 0xFF];

    public void Write(int reg, byte al)
    {
        reg &= 0x7F;
        int vi = reg >> 4;
        switch (reg)
        {
            case 0x4C:   // key on
                for (int i = 0; i < 8; i++)
                {
                    if ((al & (1 << i)) == 0) continue;
                    v[i].Prev0 = 0; v[i].Prev1 = 0; v[i].End = 0;
                    if (i == 0) v[i].Loop = 0;          // ResetVoice leaves Loop alone for voices 1-7
                    v[i].BrrPlace = 0x100000;
                    VoiceStart(i);
                }
                Regs[0x4C] = al;
                return;
            case 0x5C:   // key off: the voice stops at once
                for (int i = 0; i < 8; i++)
                    if ((al & (1 << i)) != 0) { Regs[0x4C] &= (byte)~(1 << i); v[i].Status = 0; }
                Regs[0x5C] = al;
                return;
            case 0x7C:   // ENDX: any write clears it
                Regs[0x7C] = 0;
                return;
        }
        switch (reg & 0x0F)
        {
            case 0x05: case 0x06: Regs[reg] = al; VoiceAdsr(vi); return;
            case 0x07: VoiceGain(vi, al); return;
        }
        Regs[reg] = al;
    }

    private void VoiceAdsr(int i)
    {
        byte r5 = Regs[i * 16 + 5], r6 = Regs[i * 16 + 6];
        v[i].Attack = AttackRate[r5 & 0x0F];
        v[i].Decay = DecayRate[(r5 >> 4) & 7];
        v[i].SustainR = SustainRate[r6 & 0x1F];
        v[i].SustainL = SustainValue[(r6 >> 5) & 7];
    }

    private void VoiceGain(int i, byte al)
    {
        Regs[i * 16 + 7] = al;
        if ((al & 0x80) == 0)
        {
            v[i].GainTime = 0xFFFFFFFF; v[i].GainType = 0;      // direct
            Regs[i * 16 + 8] = (byte)(al & 0x3F);
            return;
        }
        int rate = al & 0x1F;
        if ((al & 0x40) != 0) { v[i].GainType = 1; v[i].GainTime = (al & 0x20) != 0 ? IncreaseBent[rate] : Increase[rate]; }
        else { v[i].GainType = 2; v[i].GainTime = (al & 0x20) != 0 ? DecreaseRateExp[rate] : Decrease[rate]; }
    }

    private void VoiceStart(int i)
    {
        ref Voice x = ref v[i];
        int dir = (Regs[0x5D] << 8) + Regs[i * 16 + 4] * 4;   // 16-bit address math
        x.Ptr = Ram16(dir & 0xFFFF);
        x.LoopPtr = Ram16((dir + 2) & 0xFFFF);
        ushort pitch = (ushort)(Regs[i * 16 + 2] | Regs[i * 16 + 3] << 8);
        if (x.Pitch != pitch) { x.Pitch = pitch; x.Freq = PitchToFreq(pitch); }
        x.Status = 1;
        x.Time = 0;

        if ((Regs[i * 16 + 5] & 0x80) == 0)
        {
            // GAIN
            x.State = 0;
            Regs[i * 16 + 8] &= 0x3F;
            byte al = (byte)(Regs[i * 16 + 8] << 1);
            x.EnvInc = (uint)al << 16;
            uint ebx = x.GainTime;
            if (ebx == 0xFFFFFFFF) { x.Time = 0xFFFFFFFF; x.IncNumber = 0; return; }
            if (ebx == 0) ebx = 1;                               // (the original would fault)
            x.Time = ebx;
            if (x.GainType == 2) x.IncNumber = (uint)-(int)(((uint)al << 16) / ebx);
            else x.IncNumber = ((uint)(byte)(0x7F - al) << 16) / ebx;
            return;
        }

        // ADSR
        x.State = 1;   // attack
        uint atk = x.Attack == 0 ? 1 : x.Attack;
        x.Time = atk;
        x.IncNumber = 0x800000u / atk;
        x.SLenNumber = x.Decay;
        x.DecreaseNumber = 0;                                     // computed, then zeroed by the original ("neg eax / xor eax,eax")
        x.EnvInc = 0;
        x.SustainR2 = x.SustainR;
        x.SustainL2 = x.SustainL;
        x.SEndNumber = (uint)(((uint)x.SustainL << 16) * 0x7Du) / Rate;
        x.SEndLNumber = Rate / 0x7Du;
    }

    private static uint PitchToFreq(ushort pitch) => ((uint)(pitch & 0x3FFF) << 18) / Rate << 1;

    // =================================================================================================================
    //  Mixing (SBHandler16 / ProcessVoiceHandler16 / NonEchoMono)
    // =================================================================================================================

    /// <summary>Mix one block of <see cref="BlockSamples"/> mono samples at <see cref="Rate"/>.</summary>
    public ReadOnlySpan<short> RenderBlock()
    {
        Array.Clear(mix);
        for (int i = 0; i < 8; i++)
            if ((DisableMask & (1 << i)) == 0) ProcessVoice(i);
        return mix;
    }

    private void ProcessVoice(int i)
    {
        ref Voice x = ref v[i];
        if (x.Status != 1) return;
        byte l = VolumeTable[Regs[i * 16 + 0]], r = VolumeTable[Regs[i * 16 + 1]];
        x.Volume = l > r ? l : r;
        if (x.Volume == 0) return;                                // frozen while silent

        short[] blk = block[i];
        int n = 0;
        while (true)
        {
            if (x.BrrPlace >= 0x100000)
            {
                if (!ProcessBrr(i, ref x)) return;               // the sample ended: the voice stops
                continue;
            }
            x.EnvInc += x.IncNumber;
            if (--x.Time == 0) NextEnvelope(ref x);

            // NonEchoMono
            byte cl = VolumeConvTable[(x.Volume << 8 | (byte)(x.EnvInc >> 16)) & 0x7FFF];
            int product = blk[(x.BrrPlace >> 16) & 15] * cl;     // imul cx -> dx:ax
            ushort ax = (ushort)(((ushort)product >> 7) | (((byte)((product >> 16) << 1)) << 8));
            int sum = mix[n] + (short)ax;
            mix[n] = (short)(sum > 32767 ? 32767 : sum < -32768 ? -32768 : sum);
            x.BrrPlace += x.Freq;

            if (++n == BlockSamples) break;
        }
        Regs[i * 16 + 8] = (byte)((byte)(x.EnvInc >> 16) >> 1);  // ENVX
        ushort pitch = (ushort)(Regs[i * 16 + 2] | Regs[i * 16 + 3] << 8);
        if (x.Pitch != pitch) { x.Pitch = pitch; x.Freq = PitchToFreq(pitch); }
    }

    private static void NextEnvelope(ref Voice x)
    {
        switch (x.State)
        {
            case 1:  // attack done -> decay
                x.EnvInc = 0x7F0000; x.Time = x.SLenNumber; x.IncNumber = x.DecreaseNumber; x.State = 2; break;
            case 2:  // decay done -> sustain level
                x.EnvInc = (uint)x.SustainL2 << 16; x.Time = x.SustainR2; x.IncNumber = 0; x.State = 3; break;
            case 3:  // sustain done -> "blank"
                x.DecreaseNumber = x.SEndNumber; x.Time = x.SEndLNumber; x.State = 4; break;
            case 4:  // end of sample
                x.Status = 0; break;
            default: // gain: hold
                x.Time = 0xFFFFFFFF; x.IncNumber = 0; break;
        }
    }

    /// <returns>false when the voice ended.</returns>
    private bool ProcessBrr(int i, ref Voice x)
    {
        if (x.End == 1)
        {
            Regs[0x7C] |= (byte)(1 << i);
            Regs[0x5C] &= (byte)~(1 << i);
            Regs[0x4C] &= (byte)~(1 << i);
            if (x.Loop != 1) { x.Status = 0; return false; }
            x.Ptr = (x.LoopPtr - 9) & 0xFFFF;                     // one block early
            x.Prev1 = x.Prev0;
        }
        x.BrrPlace -= 0x100000;
        BrrDecode((int)x.Ptr, block[i], ref x.Prev0, ref x.Prev1, out byte last, out byte loop);
        x.Loop = loop; x.End = last;
        x.Ptr = (x.Ptr + 9) & 0xFFFF;
        return true;
    }

    private void BrrDecode(int p, short[] dst, ref int prev0, ref int prev1, out byte lastbl, out byte loopbl)
    {
        byte h = ram[p & 0xFFFF];
        lastbl = (byte)(h & 1);
        loopbl = (byte)(lastbl == 1 && (h & 2) != 0 ? 1 : 0);
        int k = (h >> 2) & 3;
        int f0 = Filter[k * 2], f1 = Filter[k * 2 + 1];
        int shift = h >> 4;
        for (int o = 0; o < 16; o++)
        {
            byte d = ram[(p + 1 + (o >> 1)) & 0xFFFF];
            int nib = (o & 1) == 0 ? d >> 4 : d & 0x0F;          // ProcessA (high nibble), then ProcessB
            int edx = (prev0 * f0 + prev1 * f1) >> 8;
            int eax = (nib & 8) != 0 ? nib | unchecked((int)0xFFFFFFF0) : nib;
            edx += eax << shift;
            if (edx < -32768) edx = -32768;
            if (edx > 32767) edx = 32767;
            dst[o] = (short)edx;
            prev1 = prev0;
            prev0 = edx;
        }
    }
}
