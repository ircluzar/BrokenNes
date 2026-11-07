namespace NesEmulator.Snes;

// =====================================================================================================================
//  PocketSPC 0.3 ARM7 S-DSP, ported to C#.
//
//  Source: PocketSPC (Nintendo DS SPC player) by gladius, version 0.3 ("v3"), GameBrew https://www.gamebrew.org/wiki/PocketSPC,
//  archive pocketspc.7z -> source/pocketspc-src-v3.zip. Files ported: arm7/source/dsp.cpp, arm7/source/dsp.h,
//  arm7/source/dspMixer.S (the ARM BRR decoder and mixer, translated instruction by instruction), spcme.h (MIXBUFSIZE 8,
//  MIXRATE 24000, ECHO_ENABLED commented out "Not fast enough with stereo mixing"), how arm7/source/main.cpp drives it (Timer1
//  IRQ every 8 samples, double-buffered DS channels, DspReset, dspPreamp from the ARM9) and arm9/source/main.cpp (the ARM9 sets
//  the preamp to 0x280 = "100%"). arm7/source/apu.cpp / apucore.S (the SPC700) are not used: BrokenNes's own SPC700 runs the
//  game and feeds this DSP the same register writes (apumem.S MemWriteDspData -> DspWriteByte(val, $F2)).
//  The archive's own text never names a version: the folder is "v3" and readme.txt is a to-do list ("Echo/FIR implementation",
//  "Noise channels"), so "0.3" is the GameBrew archive's name for it.
//  Authors: gladius (PocketSPC); the arm9 reference copy of snes9x (9xapu.cpp, 9xdsp.cpp, spc700.cpp) is (c) the Snes9x team
//  (Gary Henderson, Jerremy Koot and the others named in those files); nothing of it is ported here.
//  License: the PocketSPC files carry no licence notice; GameBrew lists the licence as "Mixed" (the snes9x parts are under
//  the Snes9x non-commercial licence stated in those files).
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2a and
//  docs/projects/apu-harvest-catalog.md.
//
//  Characteristic flaws kept on purpose (they are the sound):
//   - echo: there is no echo at all in v3 (ECHO_ENABLED is off). The "echo hack": a voice whose EON bit is set gets the echo
//     volume ADDED to its own volume (u8 wrap), and only when its volume is recomputed (VOL write or key-on). No delay, no FIR,
//     no feedback. (The catalog's "0.3 used an echo hack at 24 kHz" is right; real echo + FIR came later.)
//   - 24 kHz mixer (MIXRATE 24000), played by the DS at 16756991/699 = 23972.8 Hz; no interpolation (nearest sample)
//   - pitch = ((P & 0x3fff) << 3 << 12) / 24000 & ~1: P is taken as "hertz/8", every voice ~2.4% sharp (2.3% after the DS rate)
//   - volumes "made absolute" as v > 0x7f ? v - 0x7f : v, so -1 (0xff) is the LOUDEST (0x80) and 0x80 is almost silent
//   - BRR: table brrTab is the nibble << range with NO halving (twice the real scale), ranges 13-15 give 0 / -4096; the ARM
//     filters never clamp: inside a block the predictors run in 32-bit registers, only the stored halfwords and the two
//     prev samples handed back between blocks wrap to 16 bits
//   - loop bug: at the end block it sets blockPos = loop address but decodes from the OLD pointer (the 9 bytes after the end
//     block), then continues at loop + 9: the loop block itself is never played, the bytes after the end block are
//   - envelopes on a 0x7800 counter reset (not carried) on every step, ENVCNT table; attack +4, decay/sustain/exp-decrease
//     envx*255>>8, release -1 every ENVCNT[0x1C], bent line +1 above 0x5f; envx 0..0x7f only
//   - an ADSR/GAIN register change re-runs the envelope setup mid-note (ADSR: envx back to 0, or 0x7f for attack 15);
//     a SRCN change jumps the voice to the new sample's start at once
//   - KON ignores voices whose KOF bit is still set (the bit clears when the release ends); noise voices are silent (key-on with
//     NON set, or a NON write, disables them); no pitch modulation, no FLG mute/reset, no echo, no FIR
//   - main volume: left = MVOL_L * preamp >> 8, but right = left * preamp >> 8 (r9 used twice): MVOL_R is ignored and the
//     right channel is a re-scaled copy of the left level; final clip at +-32767
//  Not ported: the ACCURATE_MIXER / SLOW_MIXER C decoders (compiled out), the DS sound hardware's own 127/128 volume scaling,
//  ENVX/OUTX reads (kept in DSP_MEM as the original does, but the stock S-DSP answers the game's reads). PocketSPC's own
//  SPC700 timing (341 cycles in one go per IRQ) is not modelled: register writes apply at the next 8-frame block.
// =====================================================================================================================

/// <summary>The PocketSPC 0.3 ARM7 DSP: feed it DSP register writes and the sound RAM, pull 8-frame 24 kHz blocks.</summary>
public sealed class PocketSpc03Dsp
{
    public const int MixRate = 24000;          // MIXRATE (pitch maths)
    public const int PlayRateX10 = 239728;     // the DS plays it at SOUND_FREQ(24000): 16756991 / 699 = 23972.8 Hz
    public const int MixBufSize = 8;           // MIXBUFSIZE

    private const int DSP_VOL_L = 0x00, DSP_VOL_R = 0x01, DSP_PITCH_L = 0x02, DSP_PITCH_H = 0x03, DSP_SRC = 0x04,
        DSP_ADSR1 = 0x05, DSP_ADSR2 = 0x06, DSP_GAIN = 0x07, DSP_ENVX = 0x08, DSP_OUTX = 0x09,
        DSP_MAINVOL_L = 0x0C, DSP_ECHOVOL_L = 0x2C, DSP_ECHOVOL_R = 0x3C,
        DSP_KON = 0x4C, DSP_KOF = 0x5C, DSP_ENDX = 0x7C, DSP_NOV = 0x3D, DSP_EON = 0x4D, DSP_DIR = 0x5D;
    private const int ENVSTATE_NONE = 0, ENVSTATE_ATTACK = 1, ENVSTATE_DECAY = 2, ENVSTATE_SUSTAIN = 3, ENVSTATE_RELEASE = 4,
        ENVSTATE_DIRECT = 5, ENVSTATE_INCREASE = 6, ENVSTATE_BENTLINE = 7, ENVSTATE_DECREASE = 8, ENVSTATE_DECEXP = 9;
    private const int ENVCNT_START = 0x7800;
    private static readonly ushort[] ENVCNT = {
        0x0000,0x000F,0x0014,0x0018,0x001E,0x0028,0x0030,0x003C,
        0x0050,0x0060,0x0078,0x00A0,0x00C0,0x00F0,0x0140,0x0180,
        0x01E0,0x0280,0x0300,0x03C0,0x0500,0x0600,0x0780,0x0A00,
        0x0C00,0x0F00,0x1400,0x1800,0x1E00,0x2800,0x3C00,0x7800 };

    private sealed class DspChannel
    {
        public int sampleSpeed, samplePos, envCount, envSpeed;
        public readonly short[] decoded = new short[16];
        public ushort decaySpeed, sustainSpeed;
        public short envx, prevSamp1, prevSamp2;
        public ushort blockLoop, blockPos;
        public byte envState, sustainLevel, leftCalcVolume, rightCalcVolume, leftVolume, rightVolume;
        public bool needsDecode, active;
        public byte brrHeader;
    }

    private readonly byte[] apu;                     // APU_MEM (read only)
    private readonly DspChannel[] channels = new DspChannel[8];
    private readonly byte[] DSP_MEM = new byte[0x80];
    private readonly int[] mixBuffer = new int[MixBufSize * 2];   // left [0..8), right [8..16)
    private readonly short[] brrTab = new short[16 * 16];
    private ushort dspPreamp;
    private int channelNum;
    private int r8;                                  // SAMPLE_VALUE register: survives across voices and calls (OUTX only)

    public PocketSpc03Dsp(byte[] aram)
    {
        apu = aram;
        for (int i = 0; i < 8; i++) channels[i] = new DspChannel();
        Reset();
    }

    /// <summary>Power-on: fresh channels and DSP_MEM, then DspReset() with the ARM9's 0x280 preamp.</summary>
    public void Reset()
    {
        for (int i = 0; i < 8; i++) channels[i] = new DspChannel();
        System.Array.Clear(DSP_MEM);
        System.Array.Clear(mixBuffer);
        r8 = 0; channelNum = 0;
        DspReset();
        dspPreamp = 0x280;   // arm9 main.cpp: *(vu16*)0x27ffff2 = 0x280, copied in by the ARM7 VBlank IRQ
    }

    private void DspReset()
    {
        dspPreamp = 0x300;
        for (int i = 0; i < 8; i++)
        {
            channels[i].samplePos = 0; channels[i].envCount = 0;
            channels[i].needsDecode = true; channels[i].active = false;
        }
        for (int i = 0; i < 13; i++)
            for (int c = 0; c < 16; c++)
                brrTab[(i << 4) + c] = (short)(((((c ^ 8) - 8) << i) >> 1) << 1);
        for (int i = 13; i < 16; i++)
        {
            for (int c = 0; c < 8; c++) brrTab[(i << 4) + c] = 0;
            for (int c = 8; c < 16; c++) brrTab[(i << 4) + c] = -4096;
        }
    }

    // ---------------------------------------------------------------------------------------------------- dsp.cpp

    private int DecodeSampleBlock(DspChannel channel)
    {
        int cur = channel.blockPos;                  // s8 *cur = &APU_MEM[blockPos], taken BEFORE the loop jump
        if (channel.blockPos > 0x10000 - 9) { DspSetEndOfSample(channelNum); return 1; }

        if ((channel.brrHeader & 1) != 0)
        {
            DSP_MEM[DSP_ENDX] |= (byte)(1 << channelNum);
            if ((channel.brrHeader & 2) != 0) channel.blockPos = channel.blockLoop;   // cur not updated (the loop bug)
            else { DspSetEndOfSample(channelNum); return 1; }
        }
        channel.brrHeader = apu[cur & 0xFFFF];

        uint prevSamples = DecodeSampleBlockAsm(cur, channel.decoded, channel.prevSamp1, channel.prevSamp2);
        channel.prevSamp1 = (short)(prevSamples & 0xffff);
        channel.prevSamp2 = (short)(prevSamples >> 16);

        channel.blockPos += 9;
        return 0;
    }

    // dspMixer.S DecodeSampleBlockAsm: r2 = PREV0, r3 = PREV1 (32-bit registers, never clamped)
    private uint DecodeSampleBlockAsm(int src, short[] outp, int prev0, int prev1)
    {
        int hdr = apu[src++ & 0xFFFF];
        int tab = (hdr >> 4) << 4;
        int o = 0;
        switch ((hdr >> 2) & 3)
        {
            case 0:
                for (int it = 0; it < 8; it++)
                {
                    int b = apu[src++ & 0xFFFF];
                    int s2 = brrTab[tab + (b & 0xF)], s1 = brrTab[tab + (b >> 4)];
                    outp[o++] = (short)s1; outp[o++] = (short)s2;
                }
                return (ushort)outp[15] | (uint)(ushort)outp[14] << 16;
            case 1:
                for (int it = 0; it < 8; it++)
                {
                    int b = apu[src++ & 0xFFFF];
                    int s2 = brrTab[tab + (b & 0xF)], s1 = brrTab[tab + (b >> 4)];
                    prev1 = s1 + prev0 - (prev0 >> 4);
                    prev0 = s2 + prev1 - (prev1 >> 4);
                    outp[o++] = (short)prev1; outp[o++] = (short)prev0;
                }
                break;
            case 2:
                for (int it = 0; it < 8; it++)
                {
                    int b = apu[src++ & 0xFFFF];
                    int s2 = brrTab[tab + (b & 0xF)], s1 = brrTab[tab + (b >> 4)];
                    for (int k = 0; k < 2; k++)
                    {
                        int s = k == 0 ? s1 : s2;
                        int r6 = (prev1 >> 4) - prev1;
                        prev1 = prev0;
                        int r7 = -(prev0 + (prev0 << 1));
                        r6 += r7 >> 5;
                        r7 = s + (prev0 << 1);
                        prev0 = r6 + r7;
                        outp[o++] = (short)prev0;
                    }
                }
                break;
            default:
                for (int it = 0; it < 8; it++)
                {
                    int b = apu[src++ & 0xFFFF];
                    int s2 = brrTab[tab + (b & 0xF)], s1 = brrTab[tab + (b >> 4)];
                    for (int k = 0; k < 2; k++)
                    {
                        int s = k == 0 ? s1 : s2;
                        int r6 = ((prev1 + (prev1 << 1)) >> 4) - prev1;
                        prev1 = prev0;
                        int r7 = -(prev0 + (prev0 << 2) + (prev0 << 3));
                        r6 += r7 >> 6;
                        r6 += prev0 << 1;
                        prev0 = s + r6;
                        outp[o++] = (short)prev0;
                    }
                }
                break;
        }
        return (uint)(prev0 & 0xffff) | (uint)prev1 << 16;
    }

    private void DspSetChannelVolume(int ch)
    {
        var c = channels[ch];
        c.leftVolume = DSP_MEM[(ch << 4) + DSP_VOL_L];
        c.rightVolume = DSP_MEM[(ch << 4) + DSP_VOL_R];
        if (c.leftVolume > 0x7f) c.leftVolume = (byte)(c.leftVolume - 0x7f);
        if (c.rightVolume > 0x7f) c.rightVolume = (byte)(c.rightVolume - 0x7f);

        if ((DSP_MEM[DSP_EON] & (1 << ch)) != 0)
        {
            // "Hack to give it the echo volume as well"
            byte leftEcho = DSP_MEM[DSP_ECHOVOL_L], rightEcho = DSP_MEM[DSP_ECHOVOL_R];
            if (leftEcho > 0x7f) leftEcho -= 0x7f;
            if (rightEcho > 0x7f) rightEcho -= 0x7f;
            c.leftVolume += leftEcho;
            c.rightVolume += rightEcho;
        }
        c.leftCalcVolume = (byte)((c.leftVolume * c.envx) >> 7);
        c.rightCalcVolume = (byte)((c.rightVolume * c.envx) >> 7);
    }

    private void DspSetChannelPitch(int ch)
    {
        int rawFreq = ((DSP_MEM[(ch << 4) + DSP_PITCH_H] << 8) + DSP_MEM[(ch << 4) + DSP_PITCH_L]) & 0x3fff;
        channels[ch].sampleSpeed = (((rawFreq << 3) << 12) / MixRate) & ~1;
    }

    private void DspSetChannelSource(int ch)
    {
        int lookup = (DSP_MEM[DSP_DIR] << 8) + (DSP_MEM[(ch << 4) + DSP_SRC] << 2);
        channels[ch].blockPos = Rd16(lookup);
        channels[ch].blockLoop = Rd16(lookup + 2);
    }

    private ushort Rd16(int a) => (ushort)(apu[a & 0xFFFF] | apu[(a + 1) & 0xFFFF] << 8);

    private void DspSetEndOfSample(int ch)
    {
        channels[ch].active = false;
        channels[ch].envState = ENVSTATE_NONE;
        DSP_MEM[DSP_KON] &= (byte)~(1 << ch);
        DSP_MEM[DSP_KOF] &= (byte)~(1 << ch);
        DSP_MEM[DSP_ENDX] |= (byte)(1 << ch);
        DSP_MEM[(ch << 4) | DSP_ENVX] = 0;
        DSP_MEM[(ch << 4) | DSP_OUTX] = 0;
    }

    private void DspSetChannelEnvelopeHeight(int ch, byte height)
    {
        var c = channels[ch];
        c.envx = height;
        c.leftCalcVolume = (byte)((c.leftVolume * height) >> 7);
        c.rightCalcVolume = (byte)((c.rightVolume * height) >> 7);
    }

    private void DspSetChannelEnvelope(int ch)
    {
        var c = channels[ch];
        byte adsr1 = DSP_MEM[(ch << 4) + DSP_ADSR1], adsr2 = DSP_MEM[(ch << 4) + DSP_ADSR2], gain = DSP_MEM[(ch << 4) + DSP_GAIN];
        if (c.envState == ENVSTATE_RELEASE) return;
        if ((adsr1 & 0x80) != 0)
        {
            int attack = adsr1 & 0xf, decay = (adsr1 >> 4) & 0x7, sustainLevel = adsr2 >> 5, sustainRate = adsr2 & 0x1f;
            c.decaySpeed = ENVCNT[(decay << 1) + 0x10];
            c.sustainLevel = (byte)(0x10 * (sustainLevel + 1));
            c.sustainSpeed = ENVCNT[sustainRate];
            if (attack == 0xf)
            {
                DspSetChannelEnvelopeHeight(ch, 0x7f);
                c.envSpeed = c.decaySpeed;
                c.envState = ENVSTATE_DECAY;
            }
            else
            {
                DspSetChannelEnvelopeHeight(ch, 0);
                c.envSpeed = ENVCNT[(attack << 1) + 1];
                c.envState = ENVSTATE_ATTACK;
            }
        }
        else if ((gain & 0x80) == 0)
        {
            c.envState = ENVSTATE_DIRECT;
            DspSetChannelEnvelopeHeight(ch, (byte)(gain & 0x7f));
        }
        else
        {
            c.envSpeed = ENVCNT[gain & 0x1f];
            c.envState = ((gain >> 5) & 3) switch
            {
                0 => ENVSTATE_DECREASE, 1 => ENVSTATE_DECEXP, 2 => ENVSTATE_INCREASE, _ => ENVSTATE_BENTLINE,
            };
        }
    }

    private void DspKeyOnChannel(int i)
    {
        var c = channels[i];
        c.envState = ENVSTATE_NONE;
        DspSetChannelVolume(i);
        DspSetChannelPitch(i);
        DspSetChannelSource(i);
        DspSetChannelEnvelope(i);
        c.samplePos = 0;
        c.brrHeader = 0;
        c.prevSamp1 = 0; c.prevSamp2 = 0;
        c.needsDecode = true;
        c.envCount = ENVCNT_START;
        c.active = true;
        if (((DSP_MEM[DSP_NOV] >> i) & 1) != 0) c.active = false;   // noise sample: silent
        else DSP_MEM[DSP_KON] |= (byte)(1 << i);
        DSP_MEM[DSP_ENDX] &= (byte)~(1 << i);
    }

    /// <summary>DspWriteByte(val, address): a write to the DSP data port ($F3) with $F2 = address.</summary>
    public void Write(int address, byte val)
    {
        if (address > 0x7f || address < 0) return;
        byte orig = DSP_MEM[address];
        bool sameWritten = orig == val;
        DSP_MEM[address] = val;
        int ch = address >> 4;
        switch (address & 0xf)
        {
            case DSP_VOL_L: case DSP_VOL_R: DspSetChannelVolume(ch); break;
            case DSP_PITCH_L: case DSP_PITCH_H: DspSetChannelPitch(ch); break;
            case DSP_SRC: if (!sameWritten) DspSetChannelSource(ch); break;
            case DSP_ADSR1: case DSP_ADSR2: case DSP_GAIN: if (!sameWritten) DspSetChannelEnvelope(ch); break;
            case DSP_ENVX: case DSP_OUTX: DSP_MEM[address] = orig; break;
            case 0xC:
                switch (ch)
                {
                    case DSP_KON >> 4:
                        val &= (byte)~DSP_MEM[DSP_KOF];
                        DSP_MEM[DSP_KON] = orig;
                        for (int i = 0; i < 8; i++) if (((val >> i) & 1) != 0) DspKeyOnChannel(i);
                        break;
                    case DSP_KOF >> 4:
                        DSP_MEM[DSP_KOF] = 0;
                        for (int i = 0; i < 8; i++)
                            if (((val >> i) & 1) != 0 && channels[i].active)
                            {
                                channels[i].envState = ENVSTATE_RELEASE;
                                channels[i].envCount = ENVCNT_START;
                                channels[i].envSpeed = ENVCNT[0x1C];
                                DSP_MEM[DSP_KOF] |= (byte)(1 << i);
                            }
                        break;
                    case DSP_ENDX >> 4: DSP_MEM[DSP_ENDX] = 0; break;
                }
                break;
            case 0xD:
                if (ch == DSP_NOV >> 4)
                    for (int i = 0; i < 8; i++) if (((val >> i) & 1) != 0) channels[i].active = false;
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------------- dspMixer.S

    /// <summary>DspMixSamplesStereo(MIXBUFSIZE, ...): writes <paramref name="output"/> as interleaved L,R.</summary>
    public void MixSamplesStereo(short[] output)
    {
        const int numSamples = MixBufSize;
        System.Array.Clear(mixBuffer);
        for (channelNum = 0; channelNum < 8; channelNum++)
        {
            var c = channels[channelNum];
            if (!c.active) continue;
            if (c.needsDecode)
            {
                c.needsDecode = false;
                if (DecodeSampleBlock(c) == 1) continue;    // nextChannelNothingDone
            }
            int sampleSpeed = c.sampleSpeed, samplePos = c.samplePos, envCount = c.envCount, envSpeed = c.envSpeed;
            int lcv = c.leftCalcVolume, rcv = c.rightCalcVolume;
            for (int i = 0; i < numSamples; i++)
            {
                envCount -= envSpeed;
                if (envCount < 0)
                {
                    envCount = ENVCNT_START;
                    int r9 = c.envx;
                    bool store = true, end = false;
                    switch (c.envState)
                    {
                        case ENVSTATE_ATTACK:
                            r9 += 4;
                            if (r9 >= 0x7f) { r9 = 0x7f; c.envState = ENVSTATE_DECAY; envSpeed = c.decaySpeed; }
                            break;
                        case ENVSTATE_DECAY:
                            r9 = (r9 * 255) >> 8;
                            if (r9 <= c.sustainLevel)
                            {
                                c.envState = ENVSTATE_SUSTAIN; envSpeed = c.sustainSpeed;
                                if (r9 <= 0) end = true;
                            }
                            break;
                        case ENVSTATE_SUSTAIN: case ENVSTATE_DECEXP:
                            r9 = (r9 * 255) >> 8; if (r9 <= 0) end = true; break;
                        case ENVSTATE_RELEASE:
                            r9 -= 1; if (r9 <= 0) end = true; break;
                        case ENVSTATE_INCREASE:
                            r9 += 4;
                            if (r9 >= 0x7f) { r9 = 0x7f; c.envState = ENVSTATE_DIRECT; envSpeed = 0; }
                            break;
                        case ENVSTATE_BENTLINE:
                            r9 += r9 > 0x5f ? 1 : 4;
                            if (r9 >= 0x7f) { r9 = 0x7f; c.envState = ENVSTATE_DIRECT; envSpeed = 0; }
                            break;
                        case ENVSTATE_DECREASE:
                            r9 -= 4; if (r9 <= 0) end = true; break;
                        default: store = false; break;   // none / direct
                    }
                    if (end) { DspSetEndOfSample(channelNum); break; }
                    if (store)
                    {
                        c.envx = (short)r9;
                        lcv = (byte)((uint)(c.leftVolume * r9) >> 7); c.leftCalcVolume = (byte)lcv;
                        rcv = (byte)((uint)(c.rightVolume * r9) >> 7); c.rightCalcVolume = (byte)rcv;
                    }
                }
                samplePos += sampleSpeed;
                if ((uint)samplePos >= 16 << 12)
                {
                    samplePos -= 16 << 12;
                    // DecodeSampleBlock reads the channel struct, whose samplePos is still the stale stored one (unused by it)
                    if (DecodeSampleBlock(c) == 1) break;
                }
                r8 = c.decoded[(samplePos >> 12) & 15];
                mixBuffer[i] += r8 * lcv;
                mixBuffer[MixBufSize + i] += r8 * rcv;
            }
            // nextChannel: ENVX / OUTX, then the four registers go back to the struct
            int envxU = (ushort)c.envx;
            DSP_MEM[(channelNum << 4) + DSP_ENVX] = (byte)envxU;
            DSP_MEM[(channelNum << 4) + DSP_OUTX] = (byte)((r8 * envxU) >> 15);
            c.sampleSpeed = sampleSpeed; c.samplePos = samplePos; c.envCount = envCount; c.envSpeed = envSpeed;
        }
        channelNum = 8;

        int r10 = DSP_MEM[0x1C]; if (r10 > 0x7f) r10 -= 0x7f;
        int lv = DSP_MEM[DSP_MAINVOL_L]; if (lv > 0x7f) lv -= 0x7f;
        lv = (int)((uint)(dspPreamp * lv) >> 8);
        r10 = (int)((uint)(dspPreamp * lv) >> 8);   // r10 = preamp * r9: the right main volume is overwritten by the left
        for (int i = 0; i < numSamples; i++)
        {
            int l = ((mixBuffer[i] >> 8) * lv) >> 8;
            if (l < -32768) l = -32768; if (l > 32767) l = 32767;
            int r = ((mixBuffer[MixBufSize + i] >> 8) * r10) >> 8;
            if (r < -32768) r = -32768; if (r > 32767) r = 32767;
            output[i * 2] = (short)l; output[i * 2 + 1] = (short)r;
        }
    }
}
