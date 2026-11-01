using System;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  Snes9x 1.16a (1998) sound engine (the "soundux" mixer and the S9xSetAPUDSP register side), ported to C#.
//
//  Source: Debian's snes9x 1.16a-1 sources (https://sources.debian.org/src/snes9x/1.16a-1/), files soundux.cpp,
//  soundux.h, apu.cpp (S9xSetAPUDSP, FixEnvelope, StartSample, S9xAPUSetEndOfSample/EndX, S9xResetAPU's DSP part),
//  apu.h, the FilterValues/NoiseFreq tables from globals.cpp and the default settings from unix/unix.cpp
//  (Linux /dev/dsp: Settings.SoundPlaybackRate 4 -> 22,050 Hz, stereo, 16-bit, 512-frame buffers, echo on,
//  sample caching on, master volume on).
//  Copyright (c) 1996, 1997, 1998 Gary Henderson and Jerremy Koot (and the other Snes9x 1.16 authors named in its
//  COPYRIGHT file: Ivar, zsKnight, _Demo_).
//  License: the Snes9x license - "Permission to use, copy, modify and distribute Snes9x in both binary and source form,
//  for non-commercial purposes, is hereby granted without fee, providing that this license information and copyright
//  notice appear with all copies and any derived work." (non-commercial, personal use only).
//  This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and
//  docs/projects/foreign-apu-project.md.
//
//  A quick port. The bugs and shortcuts of the original are kept on purpose; they are the sound:
//   - stereo 16-bit at 22,050 Hz, mixed in 512-frame blocks (register changes land on block boundaries)
//   - PITCH_MOD is the sixth guess, ((F + (int16) M) & 0x1ffff): the previous voice's (sample*envx/128) is *added*
//     to the frequency in "hertz*8" units, not multiplied (the five other guesses are commented out in the original)
//   - pitch is (P & 0x3fff) * 8 "hertz" (not *32000/4096), no interpolation at all (sample-and-hold)
//   - envelopes are millisecond tables with a fixed-point bug (FIXED_POINT2 = 0x8000: every rate runs at half speed),
//     linear decay/sustain/release, release is always "8 ms" (16 ms), GAIN bent-line/exp modes are straight lines,
//     key-off only when the voice was keyed by S9xSetAPUDSP, GAIN direct can re-raise a releasing voice
//   - BRR decoder with the 1.16 sample cache (ShadowRAM/CachedSamples: a block whose 9 bytes match the last decode at
//     that address reuses the cached PCM and its "previous" - the 'tick' the source comment warns about), filter
//     coefficients 240 / 488,-240 / 460,-208 >> 8, shifts 13-15 not special-cased (clamped), no end/loop flag mixing
//   - noise voices still walk their BRR sample (and end with it); noise LFSR (gen<<=1, ^0x40001) clocked at the
//     voice's sample rate, NoiseFreq[2] = 32 (a typo, should be 21)
//   - echo in a private buffer (ESA ignored, sound RAM never written), delay scaled to 22,050 Hz, FIR tap 0 always on,
//     feedback clamped to +-127, FLG mute stops the mixer (voices freeze)
//   - registers that repeat the old value are ignored (volumes, ADSR, GAIN, SRCN, NON, PMON, EON); SRCN changes on a
//     playing voice are ignored (S9xSetSoundSample is compiled out); KON is never stored as written
//  Left out: the SPC700 (BrokenNes's own SMP drives this engine through the DSP register writes), the mono and 8-bit
//  mix paths (not the default), ENVX/OUTX/ENDX reads (the stock S-DSP answers those), snapshot fix-ups.
// =====================================================================================================================

/// <summary>The Snes9x 1.16a sound engine: feed it the DSP register writes and the sound RAM, pull 22,050 Hz stereo blocks.</summary>
public sealed class S916Dsp
{
    public const int Rate = 22050;            // Linux Rates[4] (Settings.SoundPlaybackRate = 4)
    public const int BlockFrames = 512;       // BufferSizes[4]: one S9xMixSamples call per OSS fragment

    // ---- soundux.h
    private const int SOUND_SAMPLE = 0, SOUND_NOISE = 1;
    private const int SOUND_SILENT = 0, SOUND_ATTACK = 1, SOUND_DECAY = 2, SOUND_SUSTAIN = 3, SOUND_RELEASE = 4;
    private const int SOUND_MODE_NONE = 0, SOUND_MODE_ADSR = 1, SOUND_MODE_GAIN = 2, SOUND_MODE_INCREASE = 3, SOUND_MODE_DECREASE = 4;
    private const int MAX_ENVELOPE_HEIGHT = 127, ENVELOPE_SHIFT = 7, VOLUME_SHIFT = 7, NUM_CHANNELS = 8;
    private const uint FIXED_POINT = 0x10000, FIXED_POINT_REMAINDER = 0xffff;
    private const long FIXED_POINT2 = 0x8000;
    private const int FIXED_POINT_SHIFT = 16;
    private const uint NEG1 = 0xFFFFFFFF;      // ~0 as unsigned long

    // ---- apu.h
    private const int APU_VOL_LEFT = 0x00, APU_VOL_RIGHT = 0x01, APU_P_LOW = 0x02, APU_P_HIGH = 0x03, APU_SRCN = 0x04,
        APU_ADSR1 = 0x05, APU_ADSR2 = 0x06, APU_GAIN = 0x07, APU_MVOL_LEFT = 0x0c, APU_MVOL_RIGHT = 0x1c, APU_EVOL_LEFT = 0x2c,
        APU_EVOL_RIGHT = 0x3c, APU_KON = 0x4c, APU_KOFF = 0x5c, APU_FLG = 0x6c, APU_ENDX = 0x7c, APU_EFB = 0x0d, APU_PMON = 0x2d,
        APU_NON = 0x3d, APU_EON = 0x4d, APU_DIR = 0x5d, APU_EDL = 0x7d;
    private const int APU_SOFT_RESET = 0x80, APU_MUTE = 0x40, APU_ECHO_DISABLED = 0x20, FREQUENCY_MASK = 0x3fff;

    // ---- globals.cpp
    private static readonly int[,] FilterValues = { { 0, 0 }, { 240, 0 }, { 488, -240 }, { 460, -208 } };
    private static readonly int[] NoiseFreq = {
        0, 16, 32, 25, 31, 42, 50, 63, 84, 100, 125, 167, 200, 250, 333,
        400, 500, 667, 800, 1000, 1300, 1600, 2000, 2700, 3200, 4000,
        5300, 6400, 8000, 10700, 16000, 32000 };

    // ---- FixEnvelope's statics (apu.cpp)
    private static readonly uint[] AttackRate = { 4100, 2600, 1500, 1000, 640, 380, 260, 160, 96, 64, 40, 24, 16, 10, 6, 0 };
    private static readonly uint[] DecayRate = { 1200, 740, 440, 290, 180, 110, 74, 37 };
    private static readonly uint[] SustainRate = {
        NEG1, 38000, 28000, 24000, 19000, 14000, 12000, 9400, 7100, 5900, 4700, 3500, 2900, 2400, 1800, 1500,
        1200, 880, 740, 590, 440, 370, 290, 220, 180, 150, 110, 92, 74, 55, 37, 18 };
    private static readonly uint[] IncreaseRate = {
        NEG1, 4100, 3100, 2600, 2000, 1500, 1300, 1000, 770, 640, 510, 380, 320, 260, 190, 160,
        130, 96, 80, 64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 4, 2 };
    private static readonly uint[] DecreaseRateExp = SustainRate;

    private sealed class Channel
    {
        public int state, type;
        public short volume_left, volume_right;
        public int frequency, count;
        public readonly short[] wave = new short[BlockFrames * 2];
        public bool loop;
        public int envx;
        public short left_vol_level, right_vol_level, envx_target;
        public uint env_error, erate;
        public int direction;
        public uint attack_rate, decay_rate, sustain_rate, release_rate, sustain_level;
        public short sample;
        public readonly short[] decoded = new short[16];
        public readonly short[] previous = new short[2];
        public short[] block = null!;   // ch->block: decoded, or a slice of CachedSamples
        public int blockOff;
        public ushort sample_number;
        public bool last_block, needs_decode;
        public uint block_pointer, sample_pointer;
        public bool echo;                // echo_buf_ptr == EchoBuffer (else DummyEchoBuffer: written, never read)
        public int mode;
    }

    private readonly byte[] ram;                           // IAPU.RAM (shared with the SMP)
    private readonly byte[] ShadowRAM = new byte[0x10000];
    private readonly short[] CachedSamples = new short[0x40000 / 2];
    public readonly byte[] Regs = new byte[0x80];          // APU.DSP
    private readonly Channel[] channels = new Channel[NUM_CHANNELS];
    private byte KeyedChannels;

    // so / SoundData / the globals
    private const int playback_rate = Rate;
    private int noise_gen, sound_switch = 255;
    private bool mute_sound = true;
    private short master_volume_left, master_volume_right, echo_volume_left, echo_volume_right;
    private int echo_enable, echo_feedback, echo_ptr, echo_buffer_size, echo_write_enabled, echo_channel_enable, pitch_mod;
    private readonly int[] Echo = new int[24000];
    private readonly int[] MixBuffer = new int[BlockFrames * 2];
    private readonly int[] EchoBuffer = new int[BlockFrames * 2];
    private readonly int[] FilterTaps = new int[8];
    private uint Z;
    private readonly int[] Loop = new int[16];

    private readonly short[] outBuf = new short[BlockFrames * 2];

    public S916Dsp(byte[] aram)
    {
        ram = aram;
        for (int i = 0; i < NUM_CHANNELS; i++) { channels[i] = new Channel(); channels[i].block = channels[i].decoded; }
        Reset();
    }

    /// <summary>S9xResetAPU's DSP part (and the sample cache it clears).</summary>
    public void Reset()
    {
        Array.Clear(ShadowRAM); Array.Clear(CachedSamples);
        Array.Clear(Regs);
        Regs[APU_ENDX] = 0xff;
        Regs[APU_FLG] = APU_MUTE | APU_ECHO_DISABLED;
        KeyedChannels = 0;
        foreach (var c in channels)
        {
            c.sample = 0; c.needs_decode = false; c.last_block = false; c.block_pointer = 0; c.sample_pointer = 0;
            c.sample_number = 0; c.previous[0] = c.previous[1] = 0; Array.Clear(c.decoded); c.block = c.decoded; c.blockOff = 0;
        }
        Array.Clear(Echo); Array.Clear(Loop); Array.Clear(FilterTaps); Z = 0;
        master_volume_left = master_volume_right = echo_volume_left = echo_volume_right = 0;
        echo_write_enabled = 0; pitch_mod = 0;
        ResetSound(true);
        SetEchoEnable(0);
    }

    // =================================================================================================================
    //  soundux.cpp
    // =================================================================================================================

    private void SetEnvelopeRate(int channel, uint rate, int direction, int target)
    {
        var ch = channels[channel];
        ch.envx_target = (short)target;
        if (rate == NEG1) { ch.direction = 0; rate = 0; }
        else ch.direction = direction;

        const int steps = MAX_ENVELOPE_HEIGHT;
        if (rate == 0 || playback_rate == 0) ch.erate = 0;
        else ch.erate = (uint)(((FIXED_POINT2 * 1000 * 100000) / (long)((rate * 100000u) / (uint)steps)) / playback_rate);
    }

    private void SetSoundVolume(int channel, short volume_left, short volume_right)
    {
        var ch = channels[channel];
        ch.volume_left = volume_left; ch.volume_right = volume_right;   // so.stereo
        ch.left_vol_level = (short)((ch.envx * ch.volume_left * 16) >> 7);
        ch.right_vol_level = (short)((ch.envx * ch.volume_right * 16) >> 7);
    }

    private void SetMasterVolume(short l, short r) { master_volume_left = l; master_volume_right = r; }
    private void SetEchoVolume(short l, short r) { echo_volume_left = l; echo_volume_right = r; }

    private void SetEchoEnable(int b)
    {
        echo_channel_enable = b;
        if (echo_write_enabled == 0) b = 0;          // Settings.DisableSoundEcho = FALSE
        if (b != 0 && echo_enable == 0) Array.Clear(Echo);
        echo_enable = b;
        for (int i = 0; i < 8; i++) channels[i].echo = (b & (1 << i)) != 0;
    }

    private void SetEchoFeedback(int feedback)
    {
        if (feedback > 127) feedback = 127;
        else if (feedback < -127) feedback = -127;
        echo_feedback = feedback;
    }

    private void SetEchoDelay(int delay)
    {
        echo_buffer_size = (512 * delay * playback_rate) / 32000;
        echo_buffer_size <<= 1;                       // so.stereo
        if (echo_buffer_size != 0) echo_ptr %= echo_buffer_size;
        else echo_ptr = 0;
        SetEchoEnable(Regs[APU_EON]);
    }

    private void SetEchoWriteEnable(int b)
    {
        echo_write_enabled = b;
        SetEchoDelay(Regs[APU_EDL] & 15);
    }

    private void SetSoundKeyOff(int channel)
    {
        var ch = channels[channel];
        if (ch.state != SOUND_SILENT)
        {
            ch.state = SOUND_RELEASE;
            SetEnvelopeRate(channel, ch.release_rate, -1, 0);
            ch.mode = SOUND_MODE_NONE;
        }
    }

    private void SetSoundADSR(int channel, uint attack, uint decay, uint sustain, uint sustain_level, uint release)
    {
        var ch = channels[channel];
        ch.attack_rate = attack; ch.decay_rate = decay; ch.sustain_rate = sustain;
        ch.release_rate = release; ch.sustain_level = sustain_level;
    }

    private void SetEnvelopeHeight(int channel, int level)
    {
        SetEnvelopeRate(channel, NEG1, 0, 0);
        var ch = channels[channel];
        ch.envx = level >> (7 - ENVELOPE_SHIFT);
        ch.left_vol_level = (short)((ch.envx * ch.volume_left * 16) >> 7);
        ch.right_vol_level = (short)((ch.envx * ch.volume_right * 16) >> 7);
    }

    private void SetSoundDecayMode(int channel)
    {
        var ch = channels[channel];
        if (ch.state != SOUND_SILENT)
        {
            SetEnvelopeRate(channel, ch.decay_rate, -1, 0);
            ch.state = SOUND_DECAY;
        }
    }

    private int SetSoundMode(int channel, int mode)
    {
        var ch = channels[channel];
        switch (mode)
        {
            case SOUND_MODE_ADSR:
                if (ch.mode == SOUND_MODE_NONE) ch.mode = SOUND_MODE_ADSR;
                break;
            case SOUND_MODE_GAIN:
                if (ch.mode == SOUND_MODE_NONE || ch.mode == SOUND_MODE_INCREASE || ch.mode == SOUND_MODE_DECREASE || ch.mode == SOUND_MODE_ADSR)
                    ch.mode = SOUND_MODE_GAIN;
                break;
            case SOUND_MODE_INCREASE:
                if (ch.mode == SOUND_MODE_NONE || ch.mode == SOUND_MODE_DECREASE) ch.mode = SOUND_MODE_INCREASE;
                break;
            case SOUND_MODE_DECREASE: ch.mode = SOUND_MODE_DECREASE; break;
            case SOUND_MODE_NONE: ch.mode = SOUND_MODE_NONE; break;
        }
        return ch.mode;
    }

    private void DecodeBlock(Channel ch)
    {
        if (ch.block_pointer > 0x10000 - 9)
        {
            ch.last_block = true; ch.loop = false;
            ch.block = ch.decoded; ch.blockOff = 0;
            return;
        }
        uint bp = ch.block_pointer;
        int filter = ram[bp];
        if ((filter & 1) != 0)
        {
            ch.last_block = true;
            if ((filter & 2) != 0) ch.loop = true;
        }

        // Sample caching (Settings.DisableSampleCaching = FALSE): "results in 'tick' sound on some samples"
        bool same = true;
        for (int k = 0; k < 9; k++) if (ram[bp + k] != ShadowRAM[bp + k]) { same = false; break; }
        if (same)
        {
            ch.block = CachedSamples; ch.blockOff = (int)(bp << 1);
            ch.previous[0] = ch.block[ch.blockOff + 15];
            ch.previous[1] = ch.block[ch.blockOff + 14];
        }
        else
        {
            Buffer.BlockCopy(ram, (int)bp, ShadowRAM, (int)bp, 9);
            short[] raw = ch.block = ch.decoded; ch.blockOff = 0;
            int shift = filter >> 4;
            filter = (filter >> 2) & 3;
            int prev0 = ch.previous[0], prev1 = ch.previous[1];
            int f0 = FilterValues[filter, 0], f1 = FilterValues[filter, 1];
            int r = 0, src = (int)bp + 1;
            for (int i = 8; i != 0; i--)
            {
                sbyte sample1 = (sbyte)ram[src++];
                sbyte sample2 = (sbyte)(sample1 << 4);
                sample2 >>= 4;
                sample1 >>= 4;
                int o = (sample1 << shift) + ((prev0 * f0 + prev1 * f1) >> 8);
                if (o < -32768) o = -32768;
                if (o > 32767) o = 32767;
                prev1 = prev0; prev0 = o;
                raw[r++] = (short)o;

                o = (sample2 << shift) + ((prev0 * f0 + prev1 * f1) >> 8);
                if (o < -32768) o = -32768;
                if (o > 32767) o = 32767;
                prev1 = prev0; prev0 = o;
                raw[r++] = (short)o;
            }
            ch.previous[0] = (short)prev0;
            ch.previous[1] = (short)prev1;
            Array.Copy(ch.decoded, 0, CachedSamples, (int)(bp << 1), 16);
        }

        if ((ch.block_pointer += 9) >= 0x10000 - 9)
        {
            ch.last_block = true; ch.loop = false;
            ch.block_pointer -= 0x10000 - 9;
        }
    }

    private static int PITCH_MOD(int F, int M) => (F + (short)M) & 0x1ffff;

    private int Kof(int freq) => (int)(uint)(((long)freq) * FIXED_POINT / playback_rate);

    private void MixStereo(int sample_count)
    {
        int prev_pitch_mod = 0;
        for (int J = 0; J < NUM_CHANNELS; J++)
        {
            var ch = channels[J];
            if ((pitch_mod & (1 << (J + 1))) != 0) Array.Clear(ch.wave);

            int VL, VR, freq, K;
            if (ch.state == SOUND_SILENT || (sound_switch & (1 << J)) == 0) continue;

            freq = ch.type == SOUND_NOISE ? NoiseFreq[Regs[APU_FLG] & 0x1f] : ch.frequency;
            if (freq != 0)
            {
                if (J > 0 && (pitch_mod & (1 << J)) != 0 && ch.type != SOUND_NOISE)
                {
                    prev_pitch_mod = channels[J - 1].wave[0];
                    freq = PITCH_MOD(ch.frequency, prev_pitch_mod);
                }
                K = Kof(freq);
            }
            else K = 0;

            if (ch.needs_decode)
            {
                DecodeBlock(ch);
                ch.needs_decode = false;
                ch.sample_pointer = 1;
                ch.sample = ch.block[ch.blockOff];
            }
            if (ch.type == SOUND_NOISE)
            {
                VL = (((noise_gen << 17) >> 17) * ch.left_vol_level) >> 7;
                VR = (((noise_gen << 17) >> 17) * ch.right_vol_level) >> 7;
            }
            else
            {
                VL = (ch.sample * ch.left_vol_level) >> 7;
                VR = (ch.sample * ch.right_vol_level) >> 7;
            }

            for (int I = 0; I < sample_count; I += 2)
            {
                ch.env_error += ch.erate;
                if (ch.env_error >= FIXED_POINT)
                {
                    uint step = (ch.env_error >> FIXED_POINT_SHIFT) - 1;
                    if (step != 0)
                    {
                        if (ch.direction < 0)
                        {
                            if (ch.envx - (int)step <= ch.envx_target) ch.envx = ch.envx_target;
                            else ch.envx -= (int)step;
                        }
                        else
                        {
                            if (ch.envx + (int)step >= ch.envx_target) ch.envx = ch.envx_target;
                            else ch.envx += (int)step;
                        }
                        ch.env_error &= FIXED_POINT_REMAINDER;
                    }
                    else ch.env_error -= FIXED_POINT;

                    if (ch.envx == ch.envx_target || ch.envx < 0)
                    {
                        if (ch.envx <= 0)
                        {
                            ch.state = SOUND_SILENT;
                            SetEndOfSample(J);
                            KeyedChannels &= (byte)~(1 << J);
                            goto stereo_exit;
                        }
                        switch (++ch.state)
                        {
                            case SOUND_DECAY:
                                if (ch.sustain_level != 7)
                                {
                                    SetEnvelopeRate(J, ch.decay_rate, -1, (int)((MAX_ENVELOPE_HEIGHT * (ch.sustain_level + 1)) >> 3));
                                    break;
                                }
                                ch.state++;
                                SetEnvelopeRate(J, ch.sustain_rate, -1, 0);   // fall through to SOUND_SUSTAIN
                                break;
                            case SOUND_SUSTAIN:
                                SetEnvelopeRate(J, ch.sustain_rate, -1, 0);
                                break;
                            case SOUND_RELEASE:
                                SetEnvelopeRate(J, ch.release_rate, -1, 0);
                                break;
                            default:
                                ch.state = SOUND_SILENT;
                                SetEndOfSample(J);
                                KeyedChannels &= (byte)~(1 << J);
                                goto stereo_exit;
                        }
                    }
                    else ch.envx += ch.direction;

                    ch.left_vol_level = (short)((ch.envx * ch.volume_left * 16) >> 7);
                    ch.right_vol_level = (short)((ch.envx * ch.volume_right * 16) >> 7);
                }

                ch.count += K;
                if ((uint)ch.count >= FIXED_POINT)
                {
                    ch.sample_pointer += (uint)(ch.count >> FIXED_POINT_SHIFT) - 1;
                    ch.count &= (int)FIXED_POINT_REMAINDER;
                    if (ch.sample_pointer >= 16)
                    {
                        ch.sample_pointer -= 16;
                        if (ch.last_block)
                        {
                            SetEndX(J);
                            if (!ch.loop)
                            {
                                ch.state = SOUND_SILENT;
                                KeyedChannels &= (byte)~(1 << J);
                                SetEndOfSample(J);
                                goto stereo_exit;
                            }
                            ch.last_block = false;
                            int dir = ((Regs[APU_DIR] << 8) + (ch.sample_number << 2)) & 0xffff;
                            ch.block_pointer = (uint)(ram[(dir + 2) & 0xffff] + (ram[(dir + 3) & 0xffff] << 8));
                        }
                        DecodeBlock(ch);
                    }
                    ch.sample = ch.block[ch.blockOff + (int)ch.sample_pointer++];
                    if (ch.type == SOUND_NOISE)
                    {
                        if (((noise_gen <<= 1) & unchecked((int)0x80000000)) != 0) noise_gen ^= 0x0040001;
                        VL = (((noise_gen << 17) >> 17) * ch.left_vol_level) >> 7;
                        VR = (((noise_gen << 17) >> 17) * ch.right_vol_level) >> 7;
                    }
                    else
                    {
                        VL = (ch.sample * ch.left_vol_level) >> 7;
                        VR = (ch.sample * ch.right_vol_level) >> 7;
                    }
                }
                MixBuffer[I] += VL;
                MixBuffer[I + 1] += VR;
                if (ch.echo) { EchoBuffer[I] += VL; EchoBuffer[I + 1] += VR; }

                if (pitch_mod != 0)
                {
                    if ((pitch_mod & (1 << (J + 1))) != 0)
                        ch.wave[I] = (short)((ch.sample * ch.envx) / 128);

                    if (J > 0 && (pitch_mod & (1 << J)) != 0 && ch.type != SOUND_NOISE)
                    {
                        int new_pitch_mod = channels[J - 1].wave[I];
                        if (new_pitch_mod != prev_pitch_mod)
                        {
                            prev_pitch_mod = new_pitch_mod;
                            freq = PITCH_MOD(ch.frequency, new_pitch_mod);
                            K = Kof(freq);
                        }
                    }
                }
            }
        stereo_exit:;
        }
    }

    /// <summary>S9xMixSamples (16-bit stereo path): one 512-frame block, interleaved L/R.</summary>
    public short[] RenderBlock()
    {
        int sample_count = BlockFrames * 2;
        short[] buffer = outBuf;
        if (mute_sound)
        {
            Array.Clear(buffer);
            return buffer;
        }
        Array.Clear(MixBuffer, 0, sample_count);
        if (echo_enable != 0) Array.Clear(EchoBuffer, 0, sample_count);
        MixStereo(sample_count);

        int I;
        if (echo_enable != 0)
        {
            for (int J = 0; J < sample_count; J++)
            {
                int E = 0;
                if (echo_buffer_size != 0) E = Echo[echo_ptr];

                Loop[(Z - 0) & 15] = E;
                E = E * FilterTaps[0];
                E += Loop[(Z - 2) & 15] * FilterTaps[1];
                E += Loop[(Z - 4) & 15] * FilterTaps[2];
                E += Loop[(Z - 6) & 15] * FilterTaps[3];
                E += Loop[(Z - 8) & 15] * FilterTaps[4];
                E += Loop[(Z - 10) & 15] * FilterTaps[5];
                E += Loop[(Z - 12) & 15] * FilterTaps[6];
                E += Loop[(Z - 14) & 15] * FilterTaps[7];
                E >>= 7;
                Z++;
                if (E > 32767 * 16) E = 32767 * 16;
                else if (E < -32768 * 16) E = -32768 * 16;

                Echo[echo_ptr] = (E * echo_feedback) / 128 + (EchoBuffer[J] >> 1);

                if ((echo_ptr += 1) >= echo_buffer_size) echo_ptr = 0;

                if ((J & 1) == 0)
                {
                    I = ((MixBuffer[J] >> 1) * master_volume_left) >> (7 + 4);
                    I += (E * echo_volume_left) >> (7 + 4);
                }
                else
                {
                    I = ((MixBuffer[J] >> 1) * master_volume_right) >> (7 + 4);
                    I += (E * echo_volume_right) >> (7 + 4);
                }
                if (I > 32767) I = 32767;
                else if (I < -32768) I = -32768;
                buffer[J] = (short)I;
            }
        }
        else
        {
            for (int J = 0; J < sample_count; J++)
            {
                if ((J & 1) == 0) I = ((MixBuffer[J] >> 1) * master_volume_left) >> (7 + 4);
                else I = ((MixBuffer[J] >> 1) * master_volume_right) >> (7 + 4);
                if (I > 32767) I = 32767;
                else if (I < -32768) I = -32768;
                buffer[J] = (short)I;
            }
        }
        return buffer;
    }

    private void ResetSound(bool full)
    {
        foreach (var c in channels)
        {
            c.state = SOUND_SILENT; c.type = SOUND_SAMPLE; c.volume_left = 0; c.volume_right = 0; c.frequency = 0; c.count = 0;
            c.loop = false; c.envx_target = 0; c.env_error = 0; c.erate = 0; c.envx = 0; c.left_vol_level = 0; c.right_vol_level = 0;
            c.direction = 0; c.attack_rate = 0; c.decay_rate = 0; c.sustain_rate = 0; c.release_rate = 0; c.sustain_level = 0;
            c.mode = SOUND_MODE_NONE;
        }
        echo_ptr = 0; echo_feedback = 0; echo_buffer_size = 1;
        mute_sound = true; noise_gen = 1; sound_switch = 255;
    }

    private void PlaySample(int channel, int type, int frequency, ushort sample_number)
    {
        var ch = channels[channel];
        ch.state = SOUND_SILENT;
        ch.type = type;
        ch.frequency = frequency;
        ch.sample_number = sample_number;
        ch.loop = false;
        ch.needs_decode = true;
        ch.last_block = false;
        ch.previous[0] = ch.previous[1] = 0;
        int dir = ((Regs[APU_DIR] << 8) + (sample_number << 2)) & 0xffff;
        ch.block_pointer = (uint)(ram[dir] + (ram[(dir + 1) & 0xffff] << 8));
        ch.sample_pointer = 0;
        ch.env_error = 0;
        SoundStartEnvelope(channel);
    }

    private void SoundStartEnvelope(int channel)
    {
        var ch = channels[channel];
        if (ch.attack_rate == 0)
        {
            if (ch.decay_rate == 0 || ch.sustain_level == 7)
            {
                if (ch.mode != SOUND_MODE_GAIN)
                    ch.envx = (int)((MAX_ENVELOPE_HEIGHT * (ch.sustain_level + 1)) >> 3);
                ch.left_vol_level = (short)((ch.envx * ch.volume_left * 16) >> VOLUME_SHIFT);
                ch.right_vol_level = (short)((ch.envx * ch.volume_right * 16) >> VOLUME_SHIFT);
                SetEnvelopeRate(channel, ch.sustain_rate, -1, 0);
                ch.state = SOUND_SUSTAIN;
            }
            else
            {
                ch.envx = MAX_ENVELOPE_HEIGHT;
                ch.left_vol_level = (short)((ch.envx * ch.volume_left * 16) >> VOLUME_SHIFT);
                ch.right_vol_level = (short)((ch.envx * ch.volume_right * 16) >> VOLUME_SHIFT);
                SetEnvelopeRate(channel, ch.decay_rate, -1, (int)((MAX_ENVELOPE_HEIGHT * (ch.sustain_level + 1)) >> 3));
                ch.state = SOUND_DECAY;
            }
        }
        else
        {
            ch.envx = 0;
            ch.left_vol_level = 0;
            ch.right_vol_level = 0;
            SetEnvelopeRate(channel, ch.attack_rate, 1, MAX_ENVELOPE_HEIGHT);
            ch.state = SOUND_ATTACK;
        }
    }

    // =================================================================================================================
    //  apu.cpp
    // =================================================================================================================

    private void SetEndOfSample(int c)
    {
        Regs[APU_ENDX] |= (byte)(1 << c);
        Regs[APU_KON] &= (byte)~(1 << c);
        Regs[APU_KOFF] &= (byte)~(1 << c);
    }

    private void SetEndX(int c) => Regs[APU_ENDX] |= (byte)(1 << c);

    /// <summary>S9xSetAPUDSP.</summary>
    public void Write(int reg, byte b)
    {
        reg &= 0x7f;
        switch (reg)
        {
            case APU_FLG:
                if ((b & APU_SOFT_RESET) != 0)
                {
                    Regs[reg] = (byte)(APU_MUTE | APU_ECHO_DISABLED | (b & 0x1f));
                    Regs[APU_ENDX] = 0xff; Regs[APU_KOFF] = 0; Regs[APU_KON] = 0;
                    SetEchoWriteEnable(0);
                    ResetSound(false);
                }
                else
                {
                    SetEchoWriteEnable((b & APU_ECHO_DISABLED) == 0 ? 1 : 0);
                    mute_sound = (b & APU_MUTE) != 0;
                }
                break;
            case APU_NON:
                if (b != Regs[APU_NON])
                    for (int c = 0; c < 8; c++) channels[c].type = (b & (1 << c)) != 0 ? SOUND_NOISE : SOUND_SAMPLE;
                break;
            case APU_MVOL_LEFT:
                if (b != Regs[reg]) SetMasterVolume((sbyte)b, (sbyte)Regs[APU_MVOL_RIGHT]);
                break;
            case APU_MVOL_RIGHT:
                if (b != Regs[reg]) SetMasterVolume((sbyte)Regs[APU_MVOL_LEFT], (sbyte)b);
                break;
            case APU_EVOL_LEFT:
                if (b != Regs[reg]) SetEchoVolume((sbyte)b, (sbyte)Regs[APU_EVOL_RIGHT]);
                break;
            case APU_EVOL_RIGHT:
                if (b != Regs[reg]) SetEchoVolume((sbyte)Regs[APU_EVOL_LEFT], (sbyte)b);
                break;
            case APU_ENDX:
                b = 0;
                break;
            case APU_KOFF:
                if (b != 0)
                    for (int c = 0; c < 8; c++)
                    {
                        byte mask = (byte)(1 << c);
                        if ((b & mask) != 0 && (KeyedChannels & mask) != 0)
                        {
                            KeyedChannels &= (byte)~mask;
                            Regs[APU_KON] &= (byte)~mask;
                            Regs[APU_KOFF] |= mask;
                            SetSoundKeyOff(c);
                        }
                    }
                Regs[APU_KOFF] = b;
                return;
            case APU_KON:
                if (b != 0)
                    for (int c = 0; c < 8; c++)
                    {
                        byte mask = (byte)(1 << c);
                        if ((b & mask) != 0)
                        {
                            KeyedChannels |= mask;
                            Regs[APU_KON] |= mask;
                            StartSample(c);
                        }
                    }
                return;
            case APU_PMON:
                if (b != Regs[reg]) pitch_mod = b;
                break;
            case APU_EON:
                if (b != Regs[reg]) SetEchoEnable(b);
                break;
            case APU_EFB:
                SetEchoFeedback((sbyte)b);
                break;
            case APU_EDL:
                SetEchoDelay(b & 0xf);
                break;
            default:
                if ((reg & 0x0f) == 0x0f) { FilterTaps[(reg >> 4) & 7] = (sbyte)b; break; }   // APU_C0..C7
                if ((reg & 0x0f) < 0x0a) VoiceWrite(reg, b);
                break;
        }
        Regs[reg] = b;
    }

    private void VoiceWrite(int reg, byte b)
    {
        int c = reg >> 4;
        switch (reg & 0x0f)
        {
            case APU_VOL_LEFT:
                if (b != Regs[reg]) SetSoundVolume(c, (sbyte)b, (sbyte)Regs[reg + 1]);
                break;
            case APU_VOL_RIGHT:
                if (b != Regs[reg]) SetSoundVolume(c, (sbyte)Regs[reg - 1], (sbyte)b);
                break;
            case APU_P_LOW:
                channels[c].frequency = ((b + (Regs[reg + 1] << 8)) & FREQUENCY_MASK) * 8;
                break;
            case APU_P_HIGH:
                channels[c].frequency = (((b << 8) + Regs[reg - 1]) & FREQUENCY_MASK) * 8;
                break;
            case APU_SRCN:
                break;   // S9xSetSoundSample is "#if 1 ... {}" in 1.16a
            case APU_ADSR1:
                if (b != Regs[reg]) FixEnvelope(c, Regs[reg + 2], b, Regs[reg + 1]);
                break;
            case APU_ADSR2:
                if (b != Regs[reg]) FixEnvelope(c, Regs[reg + 1], Regs[reg - 1], b);
                break;
            case APU_GAIN:
                if (b != Regs[reg]) FixEnvelope(c, b, Regs[reg - 2], Regs[reg - 1]);
                break;
        }
    }

    private void FixEnvelope(int channel, byte gain, byte adsr1, byte adsr2)
    {
        if ((adsr1 & 0x80) != 0)
        {
            if (SetSoundMode(channel, SOUND_MODE_ADSR) == SOUND_MODE_ADSR)
                SetSoundADSR(channel, AttackRate[adsr1 & 0xf], DecayRate[(adsr1 >> 4) & 7], SustainRate[adsr2 & 0x1f], (uint)((adsr2 >> 5) & 7), 8);
        }
        else if ((gain & 0x80) == 0)
        {
            if (SetSoundMode(channel, SOUND_MODE_GAIN) == SOUND_MODE_GAIN)
            {
                SetSoundADSR(channel, 0, 0, NEG1, 7, 8);
                SetEnvelopeHeight(channel, gain & 0x7f);
            }
        }
        else if ((gain & 0x40) != 0)
        {
            // Increase mode. "XXX: Bent curve or straight line"
            if (SetSoundMode(channel, SOUND_MODE_INCREASE) == SOUND_MODE_INCREASE)
                SetSoundADSR(channel, IncreaseRate[gain & 0x1f], NEG1, NEG1, 7, 8);
        }
        else
        {
            if (SetSoundMode(channel, SOUND_MODE_DECREASE) == SOUND_MODE_DECREASE)
            {
                SetSoundADSR(channel, 0, (gain & 0x20) != 0 ? DecreaseRateExp[gain & 0x1f] : IncreaseRate[gain & 0x1f], NEG1, 7, 8);
                SetSoundDecayMode(channel);
            }
        }
    }

    private void StartSample(int c)
    {
        ushort sample_number = Regs[APU_SRCN + c * 0x10];
        int freq, type;
        if ((Regs[APU_NON] & (1 << c)) != 0)
        {
            type = SOUND_NOISE;
            freq = NoiseFreq[Regs[APU_FLG] & 0x1f];
        }
        else
        {
            type = SOUND_SAMPLE;
            freq = ((Regs[APU_P_LOW + c * 0x10] + (Regs[APU_P_HIGH + c * 0x10] << 8)) & FREQUENCY_MASK) * 8;
        }
        SetSoundMode(c, SOUND_MODE_NONE);
        FixEnvelope(c, Regs[APU_GAIN + (c << 4)], Regs[APU_ADSR1 + (c << 4)], Regs[APU_ADSR2 + (c << 4)]);
        PlaySample(c, type, freq, sample_number);
    }
}
