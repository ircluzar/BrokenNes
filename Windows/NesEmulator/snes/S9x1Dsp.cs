using System;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  Snes9x 1.43 sound engine (the pre-accuracy "soundux" mixer and the S9xSetAPUDSP register side), ported to C#.
//
//  Source: snes9x2005 (https://github.com/libretro/snes9x2005, commit a79dfe9047e7fec58808aefe48ad2bf499c7af11), a C port
//  of Snes9x 1.43; files source/soundux.c, source/soundux.h, source/apu.c (S9xSetAPUDSP, S9xFixEnvelope), source/apu.h
//  and the NoiseFreq table from source/globals.c.
//  Copyright (c) 1996-2002 Gary Henderson, Jerremy Koot; (c) 2002-2006 Matthew Kendora, Peter Bortas, Joel Yliluoma,
//  John Weidman, funkyass, Kris Bleakley and the other Snes9x authors listed in the snes9x2005 "copyright" file;
//  ndssfc (c) 2010 dking, BassAceGold, ShadauxCat, Nebuleon; notaz (the precalculated rate tables); the RetroArch team.
//  License: the Snes9x license (non-commercial use only, copyright notice must stay with derived works); the ndssfc
//  layer is GPL-2.0-or-later. This is a documented exception to the repository's license policy - see
//  THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (the user's call: quick and dirty first, polish later). The bugs and shortcuts of the original are kept
//  on purpose; they are the sound:
//   - stereo, mixed at 32,040 Hz (the snes9x2005 default) in blocks of 534 frames (one frame's worth; register
//     changes land on block boundaries)
//   - envelopes are approximate: millisecond rate tables, rates looked up by the voice's *state* (the notaz step table),
//     exponential decay as x*255/256 per step, release a fixed ~16 ms ramp, attack hack (rate 15 means "no attack")
//   - "interpolation" is a per-sample linear step that only applies to pitches below 1.0, and is off for pitch-modulated
//     voices; noise voices use the snes9x2005 LFSR (blargg's) clocked at a table frequency
//   - BRR decode with no clamping (wraps through int16), no shift>12 special case, sloppy filter rounding
//   - echo lives in a private buffer (ESA ignored, sound RAM never written), delay scaled to the playback rate,
//     the FIR's tap 0 always applies, master volume resets to 127 and register writes that repeat the old value are ignored
//   - key-on/key-off as Snes9x did it (a key-on during key-off is deferred to the next register write), FLG mute ignored
//  Left out: the SPC700 (BrokenNes's own SMP drives this engine through the DSP register writes), the low-pass option
//  (off by default), OUTX/ENVX reads (the stock S-DSP answers those), snapshot fix-ups.
// =====================================================================================================================

/// <summary>The Snes9x 1.43 sound engine: feed it the DSP register writes and the sound RAM, pull 32,040 Hz stereo blocks.</summary>
public sealed class S9x1Dsp
{
    public const int Rate = 32040;            // AUDIO_SAMPLE_RATE (Settings.SoundPlaybackRate)
    public const int BlockFrames = 534;       // 32040 / 60: one S9xMixSamples call per frame

    // ---- soundux.h
    private const int SOUND_SAMPLE = 0, SOUND_NOISE = 1;
    private const int SOUND_SILENT = 0, SOUND_ATTACK = 1, SOUND_DECAY = 2, SOUND_SUSTAIN = 3, SOUND_RELEASE = 4, SOUND_GAIN = 5,
        SOUND_INCREASE_LINEAR = 6, SOUND_INCREASE_BENT_LINE = 7, SOUND_DECREASE_LINEAR = 8, SOUND_DECREASE_EXPONENTIAL = 9;
    private const int MODE_NONE = 0, MODE_ADSR = 1, MODE_RELEASE = 4, MODE_GAIN = 5, MODE_INCREASE_LINEAR = 6,
        MODE_INCREASE_BENT_LINE = 7, MODE_DECREASE_LINEAR = 8, MODE_DECREASE_EXPONENTIAL = 9;
    private const int MAX_ENVELOPE_HEIGHT = 127, ENVX_SHIFT = 24, SOUND_DECODE_LENGTH = 16;
    private const uint FIXED_POINT = 0x10000, FIXED_POINT_REMAINDER = 0xffff;
    private const int FIXED_POINT_SHIFT = 16, VOL_DIV16 = 0x80;
    private const uint LAST_SAMPLE = 0xffffff;
    private const int SOUND_BUFFER_SIZE = 2 * 44100 / 50;
    private const bool InterpolatedSound = true;   // Settings.InterpolatedSound (libretro.c default)

    // ---- apu.h
    private const int APU_VOL_LEFT = 0x00, APU_VOL_RIGHT = 0x01, APU_P_LOW = 0x02, APU_P_HIGH = 0x03, APU_SRCN = 0x04,
        APU_ADSR1 = 0x05, APU_ADSR2 = 0x06, APU_GAIN = 0x07, APU_MVOL_LEFT = 0x0c, APU_MVOL_RIGHT = 0x1c, APU_EVOL_LEFT = 0x2c,
        APU_EVOL_RIGHT = 0x3c, APU_KON = 0x4c, APU_KOFF = 0x5c, APU_FLG = 0x6c, APU_ENDX = 0x7c, APU_EFB = 0x0d, APU_PMON = 0x2d,
        APU_NON = 0x3d, APU_EON = 0x4d, APU_DIR = 0x5d, APU_EDL = 0x7d;
    private const int APU_SOFT_RESET = 0x80, APU_MUTE = 0x40, APU_ECHO_DISABLED = 0x20, FREQUENCY_MASK = 0x3fff;

    // ---- tables (soundux.c, globals.c)
    private static readonly uint[] AttackRate = { 4100, 2600, 1500, 1000, 640, 380, 260, 160, 96, 64, 40, 24, 16, 10, 6, 1 };
    private static readonly uint[] DecayRate = { 1200, 740, 440, 290, 180, 110, 74, 37 };
    private static readonly uint[] DecreaseRateExp = {
        0xFFFFFFFF, 38000, 28000, 24000, 19000, 14000, 12000, 9400, 7100, 5900, 4700, 3500, 2900, 2400, 1800, 1500,
        1200, 880, 740, 590, 440, 370, 290, 220, 180, 150, 110, 92, 74, 55, 37, 18 };
    private static readonly uint[] IncreaseRate = {
        0xFFFFFFFF, 4100, 3100, 2600, 2000, 1500, 1300, 1000, 770, 640, 510, 380, 320, 260, 190, 160,
        130, 96, 80, 64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 4, 2 };
    private static readonly uint[] SustainRate = DecreaseRateExp;
    private static readonly int[] NoiseFreq = {
        0, 16, 21, 25, 31, 42, 50, 63, 84, 100, 125, 167, 200, 250, 333, 400, 500, 667, 800, 1000, 1300, 1600, 2000, 2700,
        3200, 4000, 5300, 6400, 8000, 10700, 16000, 32000 };

    private readonly uint[,] AttackERate = new uint[16, 10], DecayERate = new uint[8, 10], SustainERate = new uint[32, 10],
        IncreaseERate = new uint[32, 10], DecreaseERateExp = new uint[32, 10];
    private readonly uint[] KeyOffERate = new uint[10];

    private sealed class Channel
    {
        public int state, type;
        public short volume_left, volume_right;
        public uint hertz, frequency, count;
        public bool loop;
        public int envx;
        public short left_vol_level, right_vol_level, envx_target;
        public uint env_error, erate;
        public int direction;
        public uint attack_rate, decay_rate, sustain_rate, release_rate, sustain_level;
        public short sample;
        public readonly short[] decoded = new short[17];   // [16] stands in for the struct memory past the array (read at pitch 16.0)
        public ushort sample_number;
        public bool last_block, needs_decode;
        public uint block_pointer, sample_pointer;
        public bool echo;                                   // echo_buf_ptr != NULL
        public int mode, envxx;
        public short next_sample;
        public int interpolate;
        public readonly int[] previous = new int[2];
        public byte env_ind_attack, env_ind_decay, env_ind_sustain;
    }

    private readonly byte[] ram;                           // IAPU.RAM (shared with the SMP)
    public readonly byte[] Regs = new byte[0x80];          // APU.DSP
    private readonly Channel[] channels = new Channel[8];

    // SoundStatus so / SSoundData SoundData / the globals
    private int playback_rate, noise_gen;
    private uint freqbase;
    private int echo_enable, echo_feedback, echo_ptr, echo_buffer_size, echo_write_enabled, pitch_mod;
    private readonly short[] master_volume = new short[2], echo_volume = new short[2];
    private int noise_hertz;
    private readonly int[] Echo = new int[24000];
    private readonly int[] MixBuffer = new int[SOUND_BUFFER_SIZE];
    private readonly int[] EchoBuffer = new int[SOUND_BUFFER_SIZE];
    private readonly int[] FilterTaps = new int[8];
    private byte FilterTapDefinitionBitfield;
    private uint Z;
    private readonly int[] Loop = new int[16];
    private readonly int[] wave = new int[SOUND_BUFFER_SIZE];   // MixStereo's static
    private byte KeyedChannels, KeyOn, KeyOnPrev;               // APU.KeyedChannels and S9xSetAPUDSP's statics

    private readonly short[] outBuf = new short[BlockFrames * 2];

    public S9x1Dsp(byte[] aram)
    {
        ram = aram;
        for (int i = 0; i < 8; i++) channels[i] = new Channel();
        Reset();
    }

    /// <summary>S9xResetAPU's DSP part, then S9xSetPlaybackRate (as libretro does at load).</summary>
    public void Reset()
    {
        Array.Clear(Regs);
        Regs[APU_FLG] = APU_SOFT_RESET | APU_MUTE;
        KeyedChannels = 0; KeyOn = 0; KeyOnPrev = 0;
        for (int i = 0; i < 8; i++)
        {
            var c = channels[i];
            c.frequency = 0; c.sample = 0; c.next_sample = 0; c.interpolate = 0; c.needs_decode = false; c.last_block = false;
            c.block_pointer = 0; c.sample_pointer = 0; c.sample_number = 0; c.previous[0] = c.previous[1] = 0;
            Array.Clear(c.decoded);
        }
        Array.Clear(Echo); Array.Clear(Loop); Array.Clear(wave); Z = 0;
        ResetSound(true);
        SetEchoEnable(0);
        SetPlaybackRate(Rate);
    }

    // =================================================================================================================
    //  soundux.c
    // =================================================================================================================

    private void SetEndOfSample(int i, Channel ch)
    {
        ch.state = SOUND_SILENT;
        ch.mode = MODE_NONE;
        Regs[APU_ENDX] |= (byte)(1 << i);
        Regs[APU_KON] &= (byte)~(1 << i);
        Regs[APU_KOFF] &= (byte)~(1 << i);
        KeyedChannels &= (byte)~(1 << i);
    }

    private void SetEndX(int ch) => Regs[APU_ENDX] |= (byte)(1 << ch);

    private void SetEnvRate(Channel ch, uint rate, int direction, int target, uint mode)
    {
        ch.envx_target = (short)target;
        if (rate == ~0u) { ch.direction = 0; rate = 0; }
        else ch.direction = direction;

        if (rate == 0 || playback_rate == 0) ch.erate = 0;
        else
        {
            switch (mode >> 28)
            {
                case 0: ch.erate = AttackERate[ch.env_ind_attack, ch.state]; break;
                case 1: ch.erate = DecayERate[ch.env_ind_decay, ch.state]; break;
                case 2: ch.erate = SustainERate[ch.env_ind_sustain, ch.state]; break;
                case 3: ch.erate = IncreaseERate[mode & 0x1f, ch.state]; break;
                case 4: ch.erate = DecreaseERateExp[mode & 0x1f, ch.state]; break;
                case 5: ch.erate = KeyOffERate[ch.state]; break;
            }
        }
    }

    private void SetSoundVolume(int channel, short left, short right)
    {
        var ch = channels[channel];
        ch.volume_left = left; ch.volume_right = right;
        ch.left_vol_level = (short)(ch.envx * left / 128);
        ch.right_vol_level = (short)(ch.envx * right / 128);
    }

    private void SetMasterVolume(short l, short r) { master_volume[0] = l; master_volume[1] = r; }   // DisableMasterVolume = false
    private void SetEchoVolume(short l, short r) { echo_volume[0] = l; echo_volume[1] = r; }

    private void SetEchoEnable(int b)
    {
        if (echo_write_enabled == 0) b = 0;   // DisableSoundEcho = false
        if (b != 0 && echo_enable == 0) { Array.Clear(Echo); Array.Clear(Loop); }
        echo_enable = b;
        for (int i = 0; i < 8; i++) channels[i].echo = (b & (1 << i)) != 0;
    }

    private void SetEchoFeedback(int feedback)
    {
        if (feedback <= -128) feedback = -128; else if (feedback >= 127) feedback = 127;
        echo_feedback = feedback;
    }

    private void SetEchoDelay(int delay)
    {
        echo_buffer_size = 512 * delay * playback_rate / 32040;
        echo_buffer_size <<= 1;
        if (echo_buffer_size != 0) echo_ptr %= echo_buffer_size;
        else echo_ptr = 0;
        SetEchoEnable(Regs[APU_EON]);
    }

    private void SetEchoWriteEnable(int b)
    {
        echo_write_enabled = b;
        SetEchoDelay(Regs[APU_EDL] & 15);
    }

    private void SetFrequencyModulationEnable(byte b) => pitch_mod = b & 0xFE;

    private void SetSoundKeyOff(int channel)
    {
        var ch = channels[channel];
        if (ch.state != SOUND_SILENT)
        {
            ch.state = SOUND_RELEASE;
            ch.mode = MODE_RELEASE;
            SetEnvRate(ch, 8, -1, 0, 5u << 28);
        }
    }

    private void SetFilterCoefficient(int tap, int value)
    {
        FilterTaps[tap & 7] = value;
        if (value == 0 || (tap == 0 && value == 127)) FilterTapDefinitionBitfield &= (byte)~(1 << (tap & 7));
        else FilterTapDefinitionBitfield |= (byte)(1 << (tap & 7));
    }

    private void SetSoundADSR(int channel, int attack_ind, int decay_ind, int sustain_ind, int sustain_level, int release_rate)
    {
        uint attack_rate = AttackRate[attack_ind], decay_rate = DecayRate[decay_ind], sustain_rate = SustainRate[sustain_ind];
        // Hack for ROMs that use a very short attack rate, key on a channel, then switch to decay mode. e.g. Final Fantasy II.
        if (attack_rate == 1) attack_rate = 0;

        var ch = channels[channel];
        ch.env_ind_attack = (byte)attack_ind; ch.env_ind_decay = (byte)decay_ind; ch.env_ind_sustain = (byte)sustain_ind;
        ch.attack_rate = attack_rate; ch.decay_rate = decay_rate; ch.sustain_rate = sustain_rate;
        ch.release_rate = (uint)release_rate;
        ch.sustain_level = (uint)(sustain_level + 1);

        switch (ch.state)
        {
            case SOUND_ATTACK: SetEnvRate(ch, attack_rate, 1, 127, 0); break;
            case SOUND_DECAY: SetEnvRate(ch, decay_rate, -1, (MAX_ENVELOPE_HEIGHT * (sustain_level + 1)) >> 3, 1u << 28); break;
            case SOUND_SUSTAIN: SetEnvRate(ch, sustain_rate, -1, 0, 2u << 28); break;
        }
    }

    private void SetEnvelopeHeight(int channel, int level)
    {
        var ch = channels[channel];
        ch.envx = level;
        ch.envxx = level << ENVX_SHIFT;
        ch.left_vol_level = (short)(level * ch.volume_left / 128);
        ch.right_vol_level = (short)(level * ch.volume_right / 128);
        if (ch.envx == 0 && ch.state != SOUND_SILENT && ch.state != SOUND_GAIN) SetEndOfSample(channel, ch);
    }

    private void SetSoundFrequency(int channel, uint hertz)
    {
        if (channels[channel].type == SOUND_NOISE) hertz = (uint)NoiseFreq[Regs[APU_FLG] & 0x1f];
        channels[channel].frequency = unchecked(hertz * freqbase) >> 11;
    }

    private void SetSoundHertz(int channel, uint hertz)
    {
        channels[channel].hertz = hertz;
        SetSoundFrequency(channel, hertz);
    }

    private void DecodeBlock(Channel ch)
    {
        if (ch.block_pointer > 0x10000 - 9)
        {
            ch.last_block = true;
            ch.loop = false;
            return;   // ch->block = ch->decoded: the old samples play again
        }

        int p = (int)ch.block_pointer;
        byte filter = ram[p];
        if (ch.last_block = (filter & 1) != 0) ch.loop = (filter & 2) != 0;

        short[] raw = ch.decoded;
        int r = 0;
        p++;
        int prev0 = ch.previous[0], prev1 = ch.previous[1];
        int shift = filter >> 4;
        int out_;

        switch ((filter >> 2) & 3)
        {
            case 0:
                for (int i = 8; i != 0; i--)
                {
                    sbyte s1 = (sbyte)ram[p++]; sbyte s2 = (sbyte)(s1 << 4); s2 >>= 4; s1 >>= 4;
                    raw[r++] = (short)(s1 << shift);
                    raw[r++] = (short)(s2 << shift);
                }
                prev1 = raw[r - 2];
                prev0 = raw[r - 1];
                break;
            case 1:
                for (int i = 8; i != 0; i--)
                {
                    sbyte s1 = (sbyte)ram[p++]; sbyte s2 = (sbyte)(s1 << 4); s2 >>= 4; s1 >>= 4;
                    prev0 = (short)prev0;
                    raw[r++] = (short)(prev1 = (s1 << shift) + prev0 - (prev0 >> 4));
                    prev1 = (short)prev1;
                    raw[r++] = (short)(prev0 = (s2 << shift) + prev1 - (prev1 >> 4));
                }
                break;
            case 2:
                for (int i = 8; i != 0; i--)
                {
                    sbyte s1 = (sbyte)ram[p++]; sbyte s2 = (sbyte)(s1 << 4); s2 >>= 4; s1 >>= 4;
                    out_ = (s1 << shift) - prev1 + (prev1 >> 4);
                    prev1 = (short)prev0;
                    prev0 &= ~3;
                    raw[r++] = (short)(prev0 = out_ + (prev0 << 1) - (prev0 >> 5) - (prev0 >> 4));
                    out_ = (s2 << shift) - prev1 + (prev1 >> 4);
                    prev1 = (short)prev0;
                    prev0 &= ~3;
                    raw[r++] = (short)(prev0 = out_ + (prev0 << 1) - (prev0 >> 5) - (prev0 >> 4));
                }
                break;
            case 3:
                for (int i = 8; i != 0; i--)
                {
                    sbyte s1 = (sbyte)ram[p++]; sbyte s2 = (sbyte)(s1 << 4); s2 >>= 4; s1 >>= 4;
                    out_ = s1 << shift;
                    out_ = out_ - prev1 + (prev1 >> 3) + (prev1 >> 4);
                    prev1 = (short)prev0;
                    prev0 &= ~3;
                    raw[r++] = (short)(prev0 = out_ + (prev0 << 1) - (prev0 >> 3) - (prev0 >> 4) - (prev1 >> 6));
                    out_ = s2 << shift;
                    out_ = out_ - prev1 + (prev1 >> 3) + (prev1 >> 4);
                    prev1 = (short)prev0;
                    prev0 &= ~3;
                    raw[r++] = (short)(prev0 = out_ + (prev0 << 1) - (prev0 >> 3) - (prev0 >> 4) - (prev1 >> 6));
                }
                break;
        }
        ch.previous[0] = prev0;
        ch.previous[1] = prev1;
        ch.block_pointer += 9;
    }

    private int SampleAddress(int sample_number) => ((Regs[APU_DIR] << 8) + (sample_number << 2)) & 0xffff;
    private int ReadWord(int a) => ram[a & 0xffff] | (ram[(a + 1) & 0xffff] << 8);

    private static uint PitchMod(uint f, int m) => unchecked(f * ((uint)(m + 0x800000) >> 16)) >> 7;

    private void MixStereo(int sample_count)
    {
        int pm = pitch_mod & ~Regs[APU_NON];

        for (int J = 0; J < 8; J++)
        {
            var ch = channels[J];
            if (ch.state == SOUND_SILENT) continue;

            uint freq0 = ch.frequency;
            bool mod = (pm & (1 << J)) != 0;
            int VL, VR;

            if (ch.needs_decode)
            {
                DecodeBlock(ch);
                ch.needs_decode = false;
                ch.sample = ch.decoded[0];
                ch.sample_pointer = freq0 >> FIXED_POINT_SHIFT;
                if (ch.sample_pointer == 0) ch.sample_pointer = 1;
                if (ch.sample_pointer > SOUND_DECODE_LENGTH) ch.sample_pointer = SOUND_DECODE_LENGTH - 1;
                ch.next_sample = ch.decoded[ch.sample_pointer];
                ch.interpolate = 0;
                if (InterpolatedSound && freq0 < FIXED_POINT && !mod)
                    ch.interpolate = (ch.next_sample - ch.sample) * (int)freq0 / (int)FIXED_POINT;
            }
            VL = ch.sample * ch.left_vol_level / 128;
            VR = ch.sample * ch.right_vol_level / 128;

            for (int I = 0; I < sample_count; I += 2)
            {
                uint freq = freq0;
                if (mod) freq = PitchMod(freq, wave[I / 2]);

                ch.env_error += ch.erate;
                if (ch.env_error >= FIXED_POINT)
                {
                    uint step = ch.env_error >> FIXED_POINT_SHIFT;
                    switch (ch.state)
                    {
                        case SOUND_ATTACK:
                            ch.env_error &= FIXED_POINT_REMAINDER;
                            ch.envx += (int)(step << 1);
                            ch.envxx = ch.envx << ENVX_SHIFT;
                            if (ch.envx >= 126)
                            {
                                ch.envx = 127;
                                ch.envxx = 127 << ENVX_SHIFT;
                                ch.state = SOUND_DECAY;
                                if (ch.sustain_level != 8)
                                {
                                    SetEnvRate(ch, ch.decay_rate, -1, (int)((MAX_ENVELOPE_HEIGHT * ch.sustain_level) >> 3), 1u << 28);
                                    break;
                                }
                                ch.state = SOUND_SUSTAIN;
                                SetEnvRate(ch, ch.sustain_rate, -1, 0, 2u << 28);
                            }
                            break;
                        case SOUND_DECAY:
                            while (ch.env_error >= FIXED_POINT) { ch.envxx = (ch.envxx >> 8) * 255; ch.env_error -= FIXED_POINT; }
                            ch.envx = ch.envxx >> ENVX_SHIFT;
                            if (ch.envx <= ch.envx_target)
                            {
                                if (ch.envx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                                ch.state = SOUND_SUSTAIN;
                                SetEnvRate(ch, ch.sustain_rate, -1, 0, 2u << 28);
                            }
                            break;
                        case SOUND_SUSTAIN:
                        case SOUND_DECREASE_EXPONENTIAL:
                            while (ch.env_error >= FIXED_POINT) { ch.envxx = (ch.envxx >> 8) * 255; ch.env_error -= FIXED_POINT; }
                            ch.envx = ch.envxx >> ENVX_SHIFT;
                            if (ch.envx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                            break;
                        case SOUND_RELEASE:
                            while (ch.env_error >= FIXED_POINT) { ch.envxx -= (MAX_ENVELOPE_HEIGHT << ENVX_SHIFT) / 256; ch.env_error -= FIXED_POINT; }
                            ch.envx = ch.envxx >> ENVX_SHIFT;
                            if (ch.envx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                            break;
                        case SOUND_INCREASE_LINEAR:
                            ch.env_error &= FIXED_POINT_REMAINDER;
                            ch.envx += (int)(step << 1);
                            ch.envxx = ch.envx << ENVX_SHIFT;
                            if (ch.envx >= 126)
                            {
                                ch.envx = 127; ch.envxx = 127 << ENVX_SHIFT;
                                ch.state = SOUND_GAIN; ch.mode = MODE_GAIN;
                                SetEnvRate(ch, 0, -1, 0, 0);
                            }
                            break;
                        case SOUND_INCREASE_BENT_LINE:
                            if (ch.envx >= (MAX_ENVELOPE_HEIGHT * 3) / 4)
                            {
                                while (ch.env_error >= FIXED_POINT) { ch.envxx += (MAX_ENVELOPE_HEIGHT << ENVX_SHIFT) / 256; ch.env_error -= FIXED_POINT; }
                                ch.envx = ch.envxx >> ENVX_SHIFT;
                            }
                            else
                            {
                                ch.env_error &= FIXED_POINT_REMAINDER;
                                ch.envx += (int)(step << 1);
                                ch.envxx = ch.envx << ENVX_SHIFT;
                            }
                            if (ch.envx >= 126)
                            {
                                ch.envx = 127; ch.envxx = 127 << ENVX_SHIFT;
                                ch.state = SOUND_GAIN; ch.mode = MODE_GAIN;
                                SetEnvRate(ch, 0, -1, 0, 0);
                            }
                            break;
                        case SOUND_DECREASE_LINEAR:
                            ch.env_error &= FIXED_POINT_REMAINDER;
                            ch.envx -= (int)(step << 1);
                            ch.envxx = ch.envx << ENVX_SHIFT;
                            if (ch.envx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                            break;
                        case SOUND_GAIN:
                            SetEnvRate(ch, 0, -1, 0, 0);
                            break;
                    }
                    ch.left_vol_level = (short)(ch.envx * ch.volume_left / 128);
                    ch.right_vol_level = (short)(ch.envx * ch.volume_right / 128);
                    VL = ch.sample * ch.left_vol_level / 128;
                    VR = ch.sample * ch.right_vol_level / 128;
                }

                ch.count += freq;
                if (ch.count >= FIXED_POINT)
                {
                    VL = (int)(ch.count >> FIXED_POINT_SHIFT);
                    ch.sample_pointer += (uint)VL;
                    ch.count &= FIXED_POINT_REMAINDER;

                    ch.sample = ch.next_sample;
                    if (ch.sample_pointer >= SOUND_DECODE_LENGTH)
                    {
                        if (ch.sample_pointer >= LAST_SAMPLE) { SetEndOfSample(J, ch); goto stereo_exit; }
                        do
                        {
                            ch.sample_pointer -= SOUND_DECODE_LENGTH;
                            if (ch.last_block)
                            {
                                if (!ch.loop)
                                {
                                    ch.sample_pointer = LAST_SAMPLE;
                                    ch.next_sample = ch.sample;
                                    break;
                                }
                                SetEndX(J);
                                ch.last_block = false;
                                ch.block_pointer = (uint)ReadWord(SampleAddress(ch.sample_number) + 2);
                            }
                            DecodeBlock(ch);
                        }
                        while (ch.sample_pointer >= SOUND_DECODE_LENGTH);
                        if (ch.sample_pointer < LAST_SAMPLE) ch.next_sample = ch.decoded[ch.sample_pointer];
                    }
                    else ch.next_sample = ch.decoded[ch.sample_pointer];

                    if (ch.type == SOUND_SAMPLE)
                    {
                        if (InterpolatedSound && freq < FIXED_POINT && !mod)
                        {
                            ch.interpolate = (ch.next_sample - ch.sample) * (int)freq / (int)FIXED_POINT;
                            ch.sample = (short)(ch.sample + (ch.next_sample - ch.sample) * (int)ch.count / (int)FIXED_POINT);
                        }
                        else ch.interpolate = 0;
                    }
                    else
                    {
                        // Snes9x 1.53's SPC_DSP.cpp, by blargg (as snes9x2005 has it)
                        int feedback = (noise_gen << 13) ^ (noise_gen << 14);
                        noise_gen = (feedback & 0x4000) ^ (noise_gen >> 1);
                        ch.sample = (short)((noise_gen << 17) >> 17);
                        ch.interpolate = 0;
                    }

                    VL = ch.sample * ch.left_vol_level / 128;
                    VR = ch.sample * ch.right_vol_level / 128;
                }
                else if (ch.interpolate != 0)
                {
                    int s = ch.sample + ch.interpolate;
                    if (s <= -32768) s = -32768; else if (s >= 32767) s = 32767;
                    ch.sample = (short)s;
                    VL = ch.sample * ch.left_vol_level / 128;
                    VR = ch.sample * ch.right_vol_level / 128;
                }

                if ((pm & (1 << (J + 1))) != 0) wave[I / 2] = ch.sample * ch.envx;

                MixBuffer[I] += VL;
                MixBuffer[I + 1] += VR;
                if (!ch.echo) continue;
                EchoBuffer[I] += VL;
                EchoBuffer[I + 1] += VR;
            }
        stereo_exit:;
        }
    }

    /// <summary>S9xMixSamples: one block of interleaved stereo samples.</summary>
    public short[] RenderBlock()
    {
        int sample_count = BlockFrames * 2;
        short[] buffer = outBuf;
        if (echo_enable != 0) Array.Clear(EchoBuffer, 0, sample_count);
        Array.Clear(MixBuffer, 0, sample_count);
        MixStereo(sample_count);

        if (echo_enable != 0 && echo_buffer_size != 0)
        {
            if (FilterTapDefinitionBitfield == 0)
            {
                for (int J = 0; J < sample_count; J++)
                {
                    int E = Echo[echo_ptr];
                    Echo[echo_ptr++] = E * echo_feedback / 128 + EchoBuffer[J];
                    if (echo_ptr >= echo_buffer_size) echo_ptr = 0;
                    buffer[J] = Clip16((MixBuffer[J] * master_volume[J & 1] + E * echo_volume[J & 1]) / VOL_DIV16);
                }
            }
            else
            {
                for (int J = 0; J < sample_count; J++)
                {
                    Loop[Z & 15] = Echo[echo_ptr];
                    int E = Loop[Z & 15] * FilterTaps[0];
                    if ((FilterTapDefinitionBitfield & 0x02) != 0) E += Loop[(Z - 2) & 15] * FilterTaps[1];
                    if ((FilterTapDefinitionBitfield & 0x04) != 0) E += Loop[(Z - 4) & 15] * FilterTaps[2];
                    if ((FilterTapDefinitionBitfield & 0x08) != 0) E += Loop[(Z - 6) & 15] * FilterTaps[3];
                    if ((FilterTapDefinitionBitfield & 0x10) != 0) E += Loop[(Z - 8) & 15] * FilterTaps[4];
                    if ((FilterTapDefinitionBitfield & 0x20) != 0) E += Loop[(Z - 10) & 15] * FilterTaps[5];
                    if ((FilterTapDefinitionBitfield & 0x40) != 0) E += Loop[(Z - 12) & 15] * FilterTaps[6];
                    if ((FilterTapDefinitionBitfield & 0x80) != 0) E += Loop[(Z - 14) & 15] * FilterTaps[7];
                    E /= 128;
                    Z++;
                    Echo[echo_ptr++] = E * echo_feedback / 128 + EchoBuffer[J];
                    if (echo_ptr >= echo_buffer_size) echo_ptr = 0;
                    buffer[J] = Clip16((MixBuffer[J] * master_volume[J & 1] + E * echo_volume[J & 1]) / VOL_DIV16);
                }
            }
        }
        else
        {
            for (int J = 0; J < sample_count; J++) buffer[J] = Clip16(MixBuffer[J] * master_volume[J & 1] / VOL_DIV16);
        }
        return buffer;
    }

    private static short Clip16(int v) => (short)(v <= -32768 ? -32768 : v >= 32767 ? 32767 : v);

    private void ResetSound(bool full)
    {
        for (int i = 0; i < 8; i++)
        {
            var c = channels[i];
            c.state = SOUND_SILENT; c.mode = MODE_NONE; c.type = SOUND_SAMPLE;
            c.volume_left = 0; c.volume_right = 0; c.hertz = 0; c.count = 0; c.loop = false;
            c.envx_target = 0; c.env_error = 0; c.erate = 0; c.envx = 0; c.envxx = 0;
            c.left_vol_level = 0; c.right_vol_level = 0; c.direction = 0;
            c.attack_rate = 0; c.decay_rate = 0; c.sustain_rate = 0; c.release_rate = 0; c.sustain_level = 0;
            c.env_ind_attack = 0; c.env_ind_decay = 0; c.env_ind_sustain = 0;
            echo_ptr = 0; echo_feedback = 0; echo_buffer_size = 1;
        }
        FilterTaps[0] = 127;
        for (int i = 1; i < 8; i++) FilterTaps[i] = 0;
        FilterTapDefinitionBitfield = 0;
        noise_gen = 1;
        if (full)
        {
            echo_enable = 0; echo_write_enabled = 0; pitch_mod = 0;
            echo_volume[0] = echo_volume[1] = 0;
            noise_hertz = 0;
        }
        master_volume[0] = master_volume[1] = 127;
    }

    private void SetPlaybackRate(int rate)
    {
        playback_rate = rate;
        if (rate != 0)
        {
            int[] steps = { 0, 64, 619, 619, 128, 1, 64, 55, 64, 619 };
            freqbase = (uint)((FIXED_POINT << 11) / (uint)(rate * 33 / 32));
            uint r = (uint)rate;
            for (int u = 0; u < 10; u++)
            {
                long fp1000su = (long)FIXED_POINT * 1000 * steps[u];
                // the denominators are uint32 products in C (they wrap for the 0xFFFFFFFF entries)
                for (int i = 0; i < 16; i++) AttackERate[i, u] = (uint)(fp1000su / unchecked(AttackRate[i] * r));
                for (int i = 0; i < 8; i++) DecayERate[i, u] = (uint)(fp1000su / unchecked(DecayRate[i] * r));
                for (int i = 0; i < 32; i++)
                {
                    SustainERate[i, u] = (uint)(fp1000su / unchecked(SustainRate[i] * r));
                    IncreaseERate[i, u] = (uint)(fp1000su / unchecked(IncreaseRate[i] * r));
                    DecreaseERateExp[i, u] = (uint)(fp1000su / unchecked(DecreaseRateExp[i] / 2 * r));
                }
                KeyOffERate[u] = (uint)(fp1000su / (8 * r));
            }
        }
        SetEchoDelay(Regs[APU_EDL] & 0xf);
        for (int i = 0; i < 8; i++) SetSoundFrequency(i, channels[i].hertz);
    }

    private bool SetSoundMode(int channel, int mode)
    {
        var ch = channels[channel];
        switch (mode)
        {
            case MODE_RELEASE:
                if (ch.mode != MODE_NONE) { ch.mode = MODE_RELEASE; return true; }
                break;
            case MODE_DECREASE_LINEAR:
            case MODE_DECREASE_EXPONENTIAL:
            case MODE_GAIN:
            case MODE_INCREASE_LINEAR:
            case MODE_INCREASE_BENT_LINE:
                if (ch.mode != MODE_RELEASE)
                {
                    ch.mode = mode;
                    if (ch.state != SOUND_SILENT) ch.state = mode;
                    return true;
                }
                break;
            case MODE_ADSR:
                if (ch.mode == MODE_NONE || ch.mode == MODE_ADSR) { ch.mode = mode; return true; }
                break;
        }
        return false;
    }

    private void PlaySample(int channel)
    {
        var ch = channels[channel];
        ch.state = SOUND_SILENT; ch.mode = MODE_NONE; ch.envx = 0; ch.envxx = 0;

        int b = channel << 4;
        FixEnvelope(channel, Regs[APU_GAIN + b], Regs[APU_ADSR1 + b], Regs[APU_ADSR2 + b]);

        ch.sample_number = Regs[APU_SRCN + b];
        ch.type = (Regs[APU_NON] & (1 << channel)) != 0 ? SOUND_NOISE : SOUND_SAMPLE;

        SetSoundFrequency(channel, ch.hertz);
        ch.loop = false;
        ch.needs_decode = true;
        ch.last_block = false;
        ch.previous[0] = ch.previous[1] = 0;
        ch.block_pointer = (uint)ReadWord(SampleAddress(ch.sample_number));
        ch.sample_pointer = 0;
        ch.env_error = 0;
        ch.next_sample = 0;
        ch.interpolate = 0;
        switch (ch.mode)
        {
            case MODE_ADSR:
                if (ch.attack_rate == 0)
                {
                    if (ch.decay_rate == 0 || ch.sustain_level == 8)
                    {
                        ch.state = SOUND_SUSTAIN;
                        ch.envx = (int)((MAX_ENVELOPE_HEIGHT * ch.sustain_level) >> 3);
                        SetEnvRate(ch, ch.sustain_rate, -1, 0, 2u << 28);
                    }
                    else
                    {
                        ch.state = SOUND_DECAY;
                        ch.envx = MAX_ENVELOPE_HEIGHT;
                        SetEnvRate(ch, ch.decay_rate, -1, (int)((MAX_ENVELOPE_HEIGHT * ch.sustain_level) >> 3), 1u << 28);
                    }
                    ch.left_vol_level = (short)(ch.envx * ch.volume_left / 128);
                    ch.right_vol_level = (short)(ch.envx * ch.volume_right / 128);
                }
                else
                {
                    ch.state = SOUND_ATTACK;
                    ch.envx = 0;
                    ch.left_vol_level = 0; ch.right_vol_level = 0;
                    SetEnvRate(ch, ch.attack_rate, 1, MAX_ENVELOPE_HEIGHT, 0);
                }
                ch.envxx = ch.envx << ENVX_SHIFT;
                break;
            case MODE_GAIN: ch.state = SOUND_GAIN; break;
            case MODE_INCREASE_LINEAR: ch.state = SOUND_INCREASE_LINEAR; break;
            case MODE_INCREASE_BENT_LINE: ch.state = SOUND_INCREASE_BENT_LINE; break;
            case MODE_DECREASE_LINEAR: ch.state = SOUND_DECREASE_LINEAR; break;
            case MODE_DECREASE_EXPONENTIAL: ch.state = SOUND_DECREASE_EXPONENTIAL; break;
        }

        FixEnvelope(channel, Regs[APU_GAIN + b], Regs[APU_ADSR1 + b], Regs[APU_ADSR2 + b]);
    }

    // =================================================================================================================
    //  apu.c
    // =================================================================================================================

    private void FixEnvelope(int channel, byte gain, byte adsr1, byte adsr2)
    {
        if ((adsr1 & 0x80) != 0)
        {
            if (SetSoundMode(channel, MODE_ADSR))
                SetSoundADSR(channel, adsr1 & 0xf, (adsr1 >> 4) & 7, adsr2 & 0x1f, (adsr2 >> 5) & 7, 8);
        }
        else if ((gain & 0x80) == 0)
        {
            if (SetSoundMode(channel, MODE_GAIN))
            {
                SetEnvRate(channels[channel], 0, 0, gain & 0x7f, 0);
                SetEnvelopeHeight(channel, gain & 0x7f);
            }
        }
        else if ((gain & 0x40) != 0)
        {
            if (SetSoundMode(channel, (gain & 0x20) != 0 ? MODE_INCREASE_BENT_LINE : MODE_INCREASE_LINEAR))
                SetEnvRate(channels[channel], gain, 1, 127, (3u << 28) | gain);
        }
        else if ((gain & 0x20) != 0)
        {
            if (SetSoundMode(channel, MODE_DECREASE_EXPONENTIAL))
                SetEnvRate(channels[channel], gain, -1, 0, (4u << 28) | gain);
        }
        else
        {
            if (SetSoundMode(channel, MODE_DECREASE_LINEAR))
                SetEnvRate(channels[channel], gain, -1, 0, (3u << 28) | gain);
        }
    }

    /// <summary>S9xSetAPUDSP: a write to DSP register <paramref name="reg"/> ($00-$7F).</summary>
    public void Write(int reg, byte b)
    {
        reg &= 0x7f;
        switch (reg)
        {
            case APU_FLG:
                if ((b & APU_SOFT_RESET) != 0)
                {
                    Regs[reg] = (byte)(APU_MUTE | APU_ECHO_DISABLED | (b & 0x1f));
                    Regs[APU_ENDX] = 0; Regs[APU_KOFF] = 0; Regs[APU_KON] = 0;
                    SetEchoWriteEnable(0);
                    ResetSound(false);   // kill sound
                }
                else
                {
                    SetEchoWriteEnable((b & APU_ECHO_DISABLED) == 0 ? 1 : 0);
                    // so.mute_sound = byte & APU_MUTE: nothing reads it
                    noise_hertz = NoiseFreq[b & 0x1f];
                    for (int i = 0; i < 8; i++)
                        if (channels[i].type == SOUND_NOISE) SetSoundFrequency(i, (uint)noise_hertz);
                }
                break;
            case APU_NON:
                if (b != Regs[APU_NON])
                    for (int c = 0; c < 8; c++) channels[c].type = (b & (1 << c)) != 0 ? SOUND_NOISE : SOUND_SAMPLE;
                break;
            case APU_MVOL_LEFT:
                if (b != Regs[APU_MVOL_LEFT]) SetMasterVolume((sbyte)b, (sbyte)Regs[APU_MVOL_RIGHT]);
                break;
            case APU_MVOL_RIGHT:
                if (b != Regs[APU_MVOL_RIGHT]) SetMasterVolume((sbyte)Regs[APU_MVOL_LEFT], (sbyte)b);
                break;
            case APU_EVOL_LEFT:
                if (b != Regs[APU_EVOL_LEFT]) SetEchoVolume((sbyte)b, (sbyte)Regs[APU_EVOL_RIGHT]);
                break;
            case APU_EVOL_RIGHT:
                if (b != Regs[APU_EVOL_RIGHT]) SetEchoVolume((sbyte)Regs[APU_EVOL_LEFT], (sbyte)b);
                break;
            case APU_ENDX:
                b = 0;
                break;
            case APU_KOFF:
                for (int c = 0; c < 8; c++)
                {
                    byte mask = (byte)(1 << c);
                    if ((b & mask) != 0)
                    {
                        if ((KeyedChannels & mask) != 0)
                        {
                            KeyOnPrev &= (byte)~mask;
                            KeyedChannels &= (byte)~mask;
                            Regs[APU_KON] &= (byte)~mask;
                            SetSoundKeyOff(c);
                        }
                    }
                    else if ((KeyOnPrev & mask) != 0)
                    {
                        KeyOnPrev &= (byte)~mask;
                        KeyedChannels |= mask;
                        Regs[APU_KOFF] &= (byte)~mask;
                        Regs[APU_ENDX] &= (byte)~mask;
                        PlaySample(c);
                    }
                }
                Regs[APU_KOFF] = b;
                return;
            case APU_KON:
                if (b != 0)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        byte mask = (byte)(1 << c);
                        if ((b & mask) == 0) continue;
                        // Pac-In-Time requires that channels can be key-on regardless of their current state.
                        if ((Regs[APU_KOFF] & mask) == 0)
                        {
                            KeyOnPrev &= (byte)~mask;
                            KeyedChannels |= mask;
                            Regs[APU_ENDX] &= (byte)~mask;
                            PlaySample(c);
                        }
                        else KeyOn |= mask;
                    }
                }
                return;
            case APU_EFB: SetEchoFeedback((sbyte)b); break;
            case APU_PMON: if (b != Regs[APU_PMON]) SetFrequencyModulationEnable(b); break;
            case APU_EON: if (b != Regs[APU_EON]) SetEchoEnable(b); break;
            case APU_EDL: SetEchoDelay(b & 0xf); break;
            default:
                switch (reg & 0x0f)
                {
                    case APU_VOL_LEFT: SetSoundVolume(reg >> 4, (sbyte)b, (sbyte)Regs[reg + 1]); break;
                    case APU_VOL_RIGHT: SetSoundVolume(reg >> 4, (sbyte)Regs[reg - 1], (sbyte)b); break;
                    case APU_P_LOW: SetSoundHertz(reg >> 4, (uint)(((b + (Regs[reg + 1] << 8)) & FREQUENCY_MASK) * 8)); break;
                    case APU_P_HIGH: SetSoundHertz(reg >> 4, (uint)((((b << 8) + Regs[reg - 1]) & FREQUENCY_MASK) * 8)); break;
                    case APU_ADSR1: if (b != Regs[reg]) FixEnvelope(reg >> 4, Regs[reg + 2], b, Regs[reg + 1]); break;
                    case APU_ADSR2: if (b != Regs[reg]) FixEnvelope(reg >> 4, Regs[reg + 1], Regs[reg - 1], b); break;
                    case APU_GAIN: if (b != Regs[reg]) FixEnvelope(reg >> 4, b, Regs[reg - 2], Regs[reg - 1]); break;
                    case 0x0f: SetFilterCoefficient(reg >> 4, (sbyte)b); break;   // APU_C0..C7
                }
                break;
        }

        KeyOnPrev |= KeyOn;
        KeyOn = 0;
        Regs[reg] = b;
    }
}
