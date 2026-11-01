using System;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  SNEmulDS ARM7 S-DSP (the PocketSPC sound core by gladius), ported to C#.
//
//  Source: SNEmulDS (Nintendo DS SNES emulator), https://github.com/cotodevel/snemulds, commit 79762ad
//  ("fix several HiROM games crashing ..."), files arm7/source/dsp.c, arm7/source/dsp.h, arm7/source/dspMixer.S (the ARM
//  mixer and BRR decoder, translated instruction by instruction into C#), arm7/source/spcdefs.h (MIXRATE 32000,
//  MIXBUFSIZE 63), and how arm7 drives it (arm7/source/main.c SetupSoundSPC, interrupts/Interrupts_snes.c Timer2handlerUser,
//  common/ipcfifoTGDSUser.c defaults: apuCacheSamples 0, PocketSPCVersion 9).
//  Copyright holders: the files carry no per-file notice; the project credits Archeide (SNEmulDS), gladius (the PocketSPC
//  sound core this DSP comes from), Bubble2k (CPU core) and Coto / cotodevel (the TGDS port and its maintenance).
//  License: GNU General Public License, version 2 (the repository's LICENSE file).
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and
//  docs/projects/foreign-apu-project.md.
//
//  A quick port. The bugs and shortcuts of the original are kept on purpose; they are the sound:
//   - 32 kHz, mixed in 63-frame blocks by the Timer2 IRQ that fires every 62 output samples; the DS sound channel plays
//     LENGTH = 63>>1 words = 62 samples of each block, so the 63rd mixed frame is dropped (voices run 63/62 fast)
//   - pitch = ((P & 0x3fff) << 3 << 12) / 32000 & ~1: P is taken as "hertz/8", so every voice is 2.4 % sharp
//   - no interpolation: the "simple interpolation filter" computes into a scratch register and throws it away
//   - BRR: the doubled-sample fixed point of the ARM decoder (results are clamped to 15 bits then shifted left into a
//     halfword, so a clamped +32767 wraps to -2), shifts 13-15 give 0 / -2048, filter 0 not clamped
//   - envelopes on a 0x7800 counter (ENVCNT table), exponential decay/sustain as envx*255>>8, release -1/127 every
//     ENVCNT[0x1C], GAIN exponential decrease runs the sustain code, bent line flips at 0x5f
//   - noise voices are silent (key-on with NON set, or a NON write, disables the voice); no pitch modulation
//   - KON ignores voices whose KOF bit is still set; an SRCN change only takes effect at the next key-on/loop
//   - DspReset forces FLG = 0x20 ("Disable echo emulation"), FLG mute/reset bits are ignored by the mixer
//   - echo: every voice (not just EON ones) accumulates into the echo buffer until the first EON voice is mixed; the
//     self-modifying "streq" then patches the echo load into a no-op for good, so from then on the echo input is the
//     last voice's sample*volume plus a stale register (the sample four back on the left, a pointer on the right);
//     no FIR at all, feedback added before the >>15 (so ~1/256 strength), the right echo volume register is clobbered
//     by the FLG byte (right echo volume = FLG), the echo cursor only moves while echo writes are on
//   - 32-bit wrap-around in the voice accumulators, final clip at +-0x7f00, preamp 0x140
//  Left out: the BRR sample cache (off by default: apuCacheSamples is 0 except for Mega Man X2/X3 and Breath of Fire),
//  the unused Gaussian table, PocketSPC 1.0 key-on, ENVX/OUTX (the stock S-DSP answers reads). The echo area is a
//  private 64 KB buffer (the original reads and writes the SPC's own RAM).
// =====================================================================================================================

/// <summary>The SNEmulDS (PocketSPC) ARM7 DSP: feed it DSP register writes and the sound RAM, pull 63-frame 32 kHz blocks.</summary>
public sealed class SnemulDsDsp
{
    public const int Rate = 32000;         // MIXRATE
    public const int MixBufSize = 63;      // MIXBUFSIZE: frames mixed per Timer2 IRQ
    public const int PlayFrames = 62;      // frames the DS sound channel actually plays per IRQ (LENGTH = 63 >> 1 words)

    private const int DSP_VOL_L = 0x00, DSP_VOL_R = 0x01, DSP_PITCH_L = 0x02, DSP_PITCH_H = 0x03, DSP_SRC = 0x04,
        DSP_ADSR1 = 0x05, DSP_ADSR2 = 0x06, DSP_GAIN = 0x07, DSP_ENVX = 0x08, DSP_OUTX = 0x09,
        DSP_KON = 0x4C, DSP_KOF = 0x5C, DSP_FLAG = 0x6C, DSP_ENDX = 0x7C, DSP_EFB = 0x0D, DSP_NOV = 0x3D, DSP_EON = 0x4D,
        DSP_DIR = 0x5D, DSP_ESA = 0x6D, DSP_EDL = 0x7D;
    private const int ENVSTATE_NONE = 0, ENVSTATE_ATTACK = 1, ENVSTATE_DECAY = 2, ENVSTATE_SUSTAIN = 3, ENVSTATE_RELEASE = 4,
        ENVSTATE_DIRECT = 5, ENVSTATE_INCREASE = 6, ENVSTATE_BENTLINE = 7, ENVSTATE_DECREASE = 8, ENVSTATE_DECEXP = 9;
    private const int ENVCNT_START = 0x7800, ENVX_MAX = 0x7f00;
    private static readonly ushort[] ENVCNT = {
        0x0000,0x000F,0x0014,0x0018,0x001E,0x0028,0x0030,0x003C,
        0x0050,0x0060,0x0078,0x00A0,0x00C0,0x00F0,0x0140,0x0180,
        0x01E0,0x0280,0x0300,0x03C0,0x0500,0x0600,0x0780,0x0A00,
        0x0C00,0x0F00,0x1400,0x1800,0x1E00,0x2800,0x3C00,0x7800 };
    // a stand-in for the ARM7 address of the channels[] array (the stale pointer that leaks into the right echo input)
    private const int FakeChannelsAddr = 0x0380_8000;

    private sealed class DspChannel
    {
        public int sampleSpeed, samplePos, envCount, envSpeed;
        public readonly short[] prevDecode = new short[4];
        public readonly short[] decoded = new short[16];
        public ushort decaySpeed, sustainSpeed;
        public short envx, prevSamp1, prevSamp2;
        public ushort blockPos;
        public short leftCalcVolume, rightCalcVolume;
        public byte envState, sustainLevel;
        public sbyte leftVolume, rightVolume;
        public bool active;
        public byte brrHeader;
        public bool echoEnabled;
    }

    private readonly byte[] apu;                          // APU_MEM (the SPC's RAM, read only here)
    private readonly byte[] echoMem = new byte[0x10000];  // private stand-in for the echo area in APU_MEM
    private readonly DspChannel[] channels = new DspChannel[8];
    private readonly byte[] DSP_MEM = new byte[0x100];
    private readonly int[] mixBuffer = new int[64 * 2];
    private readonly int[] echoBuffer = new int[64 * 2];
    private readonly short[] brrTab = new short[16 * 16];
    private const int dspPreamp = 0x140;
    private int echoDelay, echoCursor, echoBase;
    private int channelNum;
    private bool echoPatched;   // branchLocation overwritten (never restored: the code is not reloaded on a DSP reset)

    public SnemulDsDsp(byte[] aram)
    {
        apu = aram;
        for (int i = 0; i < 8; i++) channels[i] = new DspChannel();
        Reset();
    }

    private ushort Rd16(int a) => (ushort)(apu[a & 0xFFFF] | apu[(a + 1) & 0xFFFF] << 8);

    // ---------------------------------------------------------------------------------------------------- dsp.c

    private int DecodeSampleBlock(DspChannel channel)
    {
        int cur = channel.blockPos;
        if (channel.blockPos > 0x10000 - 9) { DspSetEndOfSample(channelNum); return 1; }

        if ((channel.brrHeader & 1) != 0)
        {
            DSP_MEM[DSP_ENDX] |= (byte)(1 << channelNum);
            if ((channel.brrHeader & 2) != 0)
            {
                int sampleBase = DSP_MEM[DSP_DIR] << 8;
                channel.blockPos = Rd16(sampleBase + (DSP_MEM[(channelNum << 4) + DSP_SRC] << 2) + 2);
                cur = channel.blockPos;
            }
            else { DspSetEndOfSample(channelNum); return 1; }
        }
        channel.brrHeader = apu[cur];
        DecodeSampleBlockAsmUncached(cur, channel);
        channel.blockPos += 9;
        return 0;
    }

    public void Reset()
    {
        echoDelay = 4; echoCursor = 0; echoBase = 0;
        Array.Clear(echoMem);
        // Disable echo emulation
        DSP_MEM[DSP_FLAG] = 0x20;
        for (int i = 0; i < 8; i++)
        {
            channels[i].samplePos = 0; channels[i].envCount = 0;
            channels[i].active = false; channels[i].echoEnabled = false;
        }
        for (int i = 0; i < 13; i++)
            for (int c = 0; c < 16; c++)
                brrTab[(i << 4) + c] = (short)((((c ^ 8) - 8) << i) >> 1);
        for (int i = 13; i < 16; i++)
        {
            for (int c = 0; c < 8; c++) brrTab[(i << 4) + c] = 0;
            for (int c = 8; c < 16; c++) brrTab[(i << 4) + c] = unchecked((short)0xF800);
        }
        channelNum = 8;
    }

    private void DspSetChannelVolume(int channel)
    {
        var c = channels[channel];
        c.leftVolume = (sbyte)DSP_MEM[(channel << 4) + DSP_VOL_L];
        c.rightVolume = (sbyte)DSP_MEM[(channel << 4) + DSP_VOL_R];
        c.leftCalcVolume = (short)((c.leftVolume * c.envx) >> 7);
        c.rightCalcVolume = (short)((c.rightVolume * c.envx) >> 7);
    }

    private void DspSetChannelPitch(int channel)
    {
        int rawFreq = ((DSP_MEM[(channel << 4) + DSP_PITCH_H] << 8) + DSP_MEM[(channel << 4) + DSP_PITCH_L]) & 0x3fff;
        channels[channel].sampleSpeed = (((rawFreq << 3) << 12) / Rate) & ~1;
    }

    private void DspSetChannelSource(int channel)
    {
        int sampleBase = DSP_MEM[DSP_DIR] << 8;
        channels[channel].blockPos = Rd16(sampleBase + (DSP_MEM[(channel << 4) + DSP_SRC] << 2));
    }

    private void DspSetEndOfSample(int channel)
    {
        if (channel >= 8) return;   // channelNum is 8 outside the mixer (an out-of-bounds write in the original)
        channels[channel].active = false;
        channels[channel].envState = ENVSTATE_NONE;
        channels[channel].envx = 0;
        DSP_MEM[(channel << 4) | DSP_ENVX] = 0;
        DSP_MEM[(channel << 4) | DSP_OUTX] = 0;
    }

    private void DspSetChannelEnvelopeHeight(int channel, int height)
    {
        var c = channels[channel];
        c.envx = (short)(height << 8);
        c.leftCalcVolume = (short)((c.leftVolume * c.envx) >> 7);
        c.rightCalcVolume = (short)((c.rightVolume * c.envx) >> 7);
    }

    private static byte GainState(int gain) => ((gain >> 5) & 3) switch
    {
        0 => ENVSTATE_DECREASE, 1 => ENVSTATE_DECEXP, 2 => ENVSTATE_INCREASE, _ => ENVSTATE_BENTLINE
    };

    private void DspStartChannelEnvelope(int channel)
    {
        var c = channels[channel];
        int adsr1 = DSP_MEM[(channel << 4) + DSP_ADSR1];
        int adsr2 = DSP_MEM[(channel << 4) + DSP_ADSR2];
        int decay = (adsr1 >> 4) & 0x7;
        int sustainLevel = adsr2 >> 5;
        int sustainRate = adsr2 & 0x1f;
        c.decaySpeed = ENVCNT[(decay << 1) + 0x10];
        c.sustainLevel = (byte)(0x10 * (sustainLevel + 1));
        c.sustainSpeed = ENVCNT[sustainRate];

        if ((adsr1 & 0x80) != 0)
        {
            int attack = adsr1 & 0xf;
            if (attack == 0xf)
            {
                DspSetChannelEnvelopeHeight(channel, 0x7f);
                c.envSpeed = c.decaySpeed;
                c.envState = ENVSTATE_DECAY;
            }
            else
            {
                DspSetChannelEnvelopeHeight(channel, 0);
                c.envSpeed = ENVCNT[(attack << 1) + 1];
                c.envState = ENVSTATE_ATTACK;
            }
        }
        else
        {
            int gain = DSP_MEM[(channel << 4) + DSP_GAIN];
            if ((gain & 0x80) == 0)
            {
                DspSetChannelEnvelopeHeight(channel, gain & 0x7f);
                c.envState = ENVSTATE_DIRECT;
            }
            else
            {
                DspSetChannelEnvelopeHeight(channel, 0);
                c.envSpeed = ENVCNT[gain & 0x1f];
                c.envState = GainState(gain);
            }
        }
    }

    private void DspChangeChannelEnvelopeGain(int channel)
    {
        var c = channels[channel];
        if (!c.active) return;
        if (c.envState == ENVSTATE_RELEASE) return;
        if ((DSP_MEM[(channel << 4) + DSP_ADSR1] & 0x80) != 0) return;
        int gain = DSP_MEM[(channel << 4) + DSP_GAIN];
        if ((gain & 0x80) == 0)
        {
            DspSetChannelEnvelopeHeight(channel, gain & 0x7f);
            c.envState = ENVSTATE_DIRECT;
            c.envSpeed = 0;
        }
        else
        {
            c.envSpeed = ENVCNT[gain & 0x1f];
            c.envState = GainState(gain);
        }
    }

    private void DspChangeChannelEnvelopeAdsr1(int channel, int orig)
    {
        var c = channels[channel];
        if (!c.active) return;
        if (c.envState == ENVSTATE_RELEASE) return;
        int adsr1 = DSP_MEM[(channel << 4) + DSP_ADSR1];
        int decay = (adsr1 >> 4) & 0x7;
        c.decaySpeed = ENVCNT[(decay << 1) + 0x10];
        if (c.envState == ENVSTATE_ATTACK) c.envSpeed = ENVCNT[((adsr1 & 0xf) << 1) + 1];
        else if (c.envState == ENVSTATE_DECAY) c.envSpeed = c.decaySpeed;

        if ((adsr1 & 0x80) != 0)
        {
            if ((orig & 0x80) == 0)
            {
                c.envState = ENVSTATE_ATTACK;
                c.envSpeed = ENVCNT[((adsr1 & 0xf) << 1) + 1];
            }
        }
        else DspChangeChannelEnvelopeGain(channel);
    }

    private void DspChangeChannelEnvelopeAdsr2(int channel)
    {
        var c = channels[channel];
        if (!c.active) return;
        if (c.envState == ENVSTATE_RELEASE) return;
        c.sustainSpeed = ENVCNT[DSP_MEM[(channel << 4) + DSP_ADSR2] & 0x1f];
        if (c.envState == ENVSTATE_SUSTAIN) c.envSpeed = c.sustainSpeed;
    }

    private void DspKeyOnChannel(int i)
    {
        var c = channels[i];
        c.envState = ENVSTATE_NONE;
        DspSetChannelEnvelopeHeight(i, 0);
        DSP_MEM[(i << 4) | DSP_ENVX] = 0;
        DSP_MEM[(i << 4) | DSP_OUTX] = 0;
        DspSetChannelVolume(i);
        DspSetChannelPitch(i);
        DspSetChannelSource(i);
        DspStartChannelEnvelope(i);
        c.samplePos = 0;                 // PocketSPCVersion 9 (the default)
        c.brrHeader = 0;
        c.prevSamp1 = 0; c.prevSamp2 = 0;
        DecodeSampleBlock(c);            // PocketSPCVersion 9: decode the first block now (channelNum is stale here)
        c.envCount = ENVCNT_START;
        c.active = true;
        if (((DSP_MEM[DSP_NOV] >> i) & 1) != 0) c.active = false;   // noise sample: not implemented
        DSP_MEM[DSP_ENDX] &= (byte)~(1 << i);
    }

    /// <summary>DspWriteByte(val, address).</summary>
    public void Write(int address, byte val)
    {
        address &= 0xFF;
        int orig = DSP_MEM[address];
        DSP_MEM[address] = val;
        if (address > 0x7f) return;

        switch (address & 0xf)
        {
            case DSP_VOL_L: case DSP_VOL_R: DspSetChannelVolume(address >> 4); break;
            case DSP_PITCH_L: case DSP_PITCH_H: DspSetChannelPitch(address >> 4); break;
            case DSP_ADSR1: DspChangeChannelEnvelopeAdsr1(address >> 4, orig); break;
            case DSP_ADSR2: DspChangeChannelEnvelopeAdsr2(address >> 4); break;
            case DSP_GAIN: DspChangeChannelEnvelopeGain(address >> 4); break;
            case 0xC:
                switch (address >> 4)
                {
                    case DSP_KON >> 4:
                        if (val != 0)
                        {
                            DSP_MEM[DSP_KON] = (byte)(val & DSP_MEM[DSP_KOF]);
                            int v = val & ~DSP_MEM[DSP_KOF];
                            for (int i = 0; i < 8; i++) if (((v >> i) & 1) != 0) DspKeyOnChannel(i);
                        }
                        break;
                    case DSP_KOF >> 4:
                        for (int i = 0; i < 8; i++)
                            if (((val >> i) & 1) != 0 && channels[i].active && channels[i].envState != ENVSTATE_RELEASE)
                            {
                                channels[i].envState = ENVSTATE_RELEASE;
                                channels[i].envCount = ENVCNT_START;
                                channels[i].envSpeed = ENVCNT[0x1C];
                            }
                        break;
                    case DSP_ENDX >> 4: DSP_MEM[DSP_ENDX] = 0; break;
                }
                break;
            case 0xD:
                switch (address >> 4)
                {
                    case DSP_EDL >> 4:
                        {
                            int e = val & 0xf;
                            echoDelay = e == 0 ? 4 : (ushort)((((e << 4) * (Rate / 1000))) << 2);
                        }
                        break;
                    case DSP_NOV >> 4:
                        for (int i = 0; i < 8; i++) if (((val >> i) & 1) != 0) channels[i].active = false;
                        break;
                    case DSP_ESA >> 4: echoBase = DSP_MEM[DSP_ESA] << 8; break;
                    case DSP_EON >> 4:
                        for (int i = 0; i < 8; i++) channels[i].echoEnabled = ((val >> i) & 1) != 0;
                        break;
                }
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------------- dspMixer.S

    private static int Clamp15(int v) => v > 0x7fff ? 0x7fff : v < -0x8000 ? -0x8000 : v;

    private void DecodeSampleBlockAsmUncached(int cur, DspChannel ch)
    {
        ch.prevDecode[3] = ch.decoded[15];
        int p0 = ch.prevSamp1, p1 = ch.prevSamp2;
        int hdr = apu[cur & 0xFFFF]; cur++;
        int tab = (hdr >> 4) << 4;
        int filter = (hdr >> 2) & 3;
        var d = ch.decoded;
        int o = 0;
        for (int k = 0; k < 8; k++)
        {
            int b = apu[cur & 0xFFFF]; cur++;
            int s2 = brrTab[tab + (b & 0xf)];
            int s1 = brrTab[tab + (b >> 4)];
            switch (filter)
            {
                case 0:
                    d[o++] = (short)(s1 << 1);
                    d[o++] = (short)(s2 << 1);
                    break;
                case 1:
                    p1 = s1 + (p0 >> 1) - (p0 >> 5);
                    p1 = (short)(Clamp15(p1) << 1); d[o++] = (short)p1;
                    p0 = s2 + (p1 >> 1) - (p1 >> 5);
                    p0 = (short)(Clamp15(p0) << 1); d[o++] = (short)p0;
                    break;
                case 2:
                    for (int h = 0; h < 2; h++)
                    {
                        int s = h == 0 ? s1 : s2;
                        int r6 = (p1 >> 5) - (p1 >> 1);
                        p1 = p0;
                        int r7 = -(p0 + (p0 >> 1));
                        r6 += r7 >> 5;
                        p0 = r6 + s + p0;
                        p0 = (short)(Clamp15(p0) << 1); d[o++] = (short)p0;
                    }
                    break;
                default:
                    for (int h = 0; h < 2; h++)
                    {
                        int s = h == 0 ? s1 : s2;
                        int r6 = ((p1 + (p1 >> 1)) >> 4) - (p1 >> 1);
                        p1 = p0;
                        int r7 = -(p0 * 13);
                        r6 += r7 >> 7;
                        r6 += p0;
                        p0 = s + r6;
                        p0 = (short)(Clamp15(p0) << 1); d[o++] = (short)p0;
                    }
                    break;
            }
        }
        if (filter == 0) { p0 = d[15]; p1 = d[14]; }
        ch.prevSamp1 = (short)p0; ch.prevSamp2 = (short)p1;
    }

    /// <summary>DspMixSamplesStereo(numSamples, out): fills <paramref name="output"/> with interleaved L/R frames.</summary>
    public void MixSamplesStereo(int numSamples, short[] output)
    {
        unchecked
        {
            Array.Clear(mixBuffer); Array.Clear(echoBuffer);
            int n8 = numSamples & 0xFF;   // ldrb r3, numSamples

            for (channelNum = 0; channelNum < 8; channelNum++)
            {
                var c = channels[channelNum];
                if (!c.active) continue;
                if (c.echoEnabled) echoPatched = true;

                int speed = c.sampleSpeed, pos = c.samplePos, cnt = c.envCount, espd = c.envSpeed;
                int lcv = c.leftCalcVolume, rcv = c.rightCalcVolume;
                for (int i = 0; i < n8; i++)
                {
                    cnt -= espd;
                    if (cnt < 0)
                    {
                        cnt = 0x7800;
                        int r9 = c.envx;
                        bool end = false, store = true;
                        switch (c.envState)
                        {
                            case ENVSTATE_ATTACK:
                                r9 += 4 << 8;
                                if (r9 > ENVX_MAX) { r9 = ENVX_MAX; c.envState = ENVSTATE_DECAY; espd = c.decaySpeed; }
                                break;
                            case ENVSTATE_DECAY:
                                r9 = (r9 * 255) >> 8;
                                if (r9 < (c.sustainLevel << 8))
                                {
                                    c.envState = ENVSTATE_SUSTAIN; espd = c.sustainSpeed;
                                    if (r9 < 0) end = true;
                                }
                                break;
                            case ENVSTATE_SUSTAIN: case ENVSTATE_DECEXP:
                                r9 = (r9 * 255) >> 8;
                                if (r9 < 0) end = true;
                                break;
                            case ENVSTATE_RELEASE:
                                r9 -= 1 << 8;
                                if (r9 < 0) end = true;
                                break;
                            case ENVSTATE_INCREASE:
                                r9 += 4 << 8;
                                if (r9 > ENVX_MAX) { r9 = ENVX_MAX; c.envState = ENVSTATE_DIRECT; espd = 0; }
                                break;
                            case ENVSTATE_BENTLINE:
                                r9 += r9 > (0x5f << 8) ? 1 << 8 : 4 << 8;
                                if (r9 >= ENVX_MAX) { r9 = ENVX_MAX; c.envState = ENVSTATE_DIRECT; espd = 0; }
                                break;
                            case ENVSTATE_DECREASE:
                                r9 -= 4 << 8;
                                if (r9 < 0) end = true;
                                break;
                            default: store = false; break;   // none / direct
                        }
                        if (end) { DspSetEndOfSample(channelNum); break; }
                        if (store)
                        {
                            c.envx = (short)r9;
                            lcv = (c.leftVolume * r9) >> 7;
                            rcv = (c.rightVolume * r9) >> 7;
                        }
                    }

                    pos += speed;
                    if ((uint)pos >= 16 << 12)
                    {
                        pos -= 16 << 12;
                        if (DecodeSampleBlock(c) == 1) break;
                    }

                    int p = (int)((uint)pos >> 12);
                    int r8 = c.decoded[p];
                    int stale = p >= 4 ? c.decoded[p - 4] : c.prevDecode[p];   // r9 left over from the dead interpolation

                    int eL, eR;
                    if (!echoPatched) { eL = echoBuffer[2 * i]; eR = echoBuffer[2 * i + 1]; }
                    else { eL = stale; eR = FakeChannelsAddr + channelNum * 80 + p * 2; }   // r12 = &channel->decoded[p] - 24
                    echoBuffer[2 * i] = eL + r8 * lcv;
                    echoBuffer[2 * i + 1] = eR + r8 * rcv;

                    mixBuffer[2 * i] += r8 * lcv;
                    mixBuffer[2 * i + 1] += r8 * rcv;
                }
                c.leftCalcVolume = (short)lcv; c.rightCalcVolume = (short)rcv;
                c.sampleSpeed = speed; c.samplePos = pos; c.envCount = cnt; c.envSpeed = espd;
            }

            // ---- echo (no FIR; r5 = right echo volume is overwritten by the FLG byte)
            int evolL = (sbyte)DSP_MEM[0x2C] << 7;
            int efb = (sbyte)DSP_MEM[DSP_EFB];
            int flg = DSP_MEM[DSP_FLAG];
            int start = echoBase, endA = echoBase + echoDelay, cur = echoBase + echoCursor;
            for (int i = 0; i < numSamples; i++)
            {
                int r0 = (short)(echoMem[cur & 0xFFFF] | echoMem[(cur + 1) & 0xFFFF] << 8);
                int r1 = (short)(echoMem[(cur + 2) & 0xFFFF] | echoMem[(cur + 3) & 0xFFFF] << 8);
                int r2 = (echoBuffer[2 * i] + efb * r0) >> 15;
                int r3 = (echoBuffer[2 * i + 1] + efb * r1) >> 15;
                if ((flg & 0x20) == 0)
                {
                    echoMem[cur & 0xFFFF] = (byte)r2; echoMem[(cur + 1) & 0xFFFF] = (byte)(r2 >> 8);
                    echoMem[(cur + 2) & 0xFFFF] = (byte)r3; echoMem[(cur + 3) & 0xFFFF] = (byte)(r3 >> 8);
                    cur += 4;
                }
                if (cur >= endA) cur = start;
                echoBuffer[2 * i] = evolL * r0;
                echoBuffer[2 * i + 1] = flg * r1;
            }
            echoCursor = (ushort)(cur - start);

            // ---- volume, echo mix and clip
            int lv = ((sbyte)DSP_MEM[0x0C] * dspPreamp) >> 7;
            int rv = ((sbyte)DSP_MEM[0x1C] * dspPreamp) >> 7;
            for (int i = 0; i < numSamples; i++)
            {
                output[2 * i] = Clip((((mixBuffer[2 * i] >> 15) * lv) + (echoBuffer[2 * i] >> 7)) >> 7);
                output[2 * i + 1] = Clip((((mixBuffer[2 * i + 1] >> 15) * rv) + (echoBuffer[2 * i + 1] >> 7)) >> 7);
            }
        }
    }

    private static short Clip(int v) => (short)(v > 0x7f00 ? 0x7f00 : v < -0x7f00 ? -0x7f00 : v);
}
