using System;

namespace NesEmulator.Snes;

// =====================================================================================================================
//  Snes9x 1.51 sound engine (the last "soundux" mixer before 1.52 switched to blargg's SNES_SPC, plus the S9xSetAPUDSP
//  register side), ported to C#.
//
//  Source: Snes9x 1.51 (2007), via the snes9x-rr mirror
//  https://raw.githubusercontent.com/TheProjecter/snes9x-rr/master/snes9x-151/ (branch master), files soundux.cpp,
//  soundux.h, apu.cpp (S9xSetAPUDSP, S9xFixEnvelope, S9xSoftResetAPU's DSP part) and apu.h.
//  Copyright (c) 1996-2002 Gary Henderson, Jerremy Koot; (c) 2002-2004 Matthew Kendora; (c) 2002-2005 Peter Bortas;
//  (c) 2004-2005 Joel Yliluoma; (c) 2001-2006 John Weidman; (c) 2002-2006 funkyass, Kris Bleakley; (c) 2002-2007 Brad
//  Jorsch, Nach, zones; (c) 2006-2007 nitsuja. Sound DSP emulator code derived from SNEeSe and OpenSPC:
//  (c) 1998-2003 Brad Martin, (c) 1998-2006 Charles Bilyue' (the gaussian and env_counter tables).
//  License: the Snes9x license (permission to use, copy, modify and/or distribute for non-commercial purposes, the
//  license information and copyright notice must appear with all copies and derived works). This is a documented
//  exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and
//  docs/projects/foreign-apu-project.md.
//
//  A quick port. The original's bugs and shortcuts are kept on purpose; they are the sound. 1.51's defaults: stereo,
//  16-bit, 32,000 Hz, InterpolatedSound on, echo on, FakeMuteFix/ReverseStereo/FixFrequency off.
//   - mixed in blocks of 533 frames (one frame's worth; register changes land on block boundaries)
//   - envelopes on the SNEeSe/OpenSPC counter table (ENV_RANGE 0x800), but: attack rate 15 jumps +0x400 per sample
//     and keys on straight to full level, release is a fixed 1/256 step every sample, the exponential step is
//     x - ((x-1)>>8) - 1, and there is no envelope sample rate divider beyond the counter
//   - 4-point gaussian interpolation (sclip15 between the 3rd and 4th tap), BRR with sclip15(sclamp16()) per nybble,
//     invalid shifts (13-15) as out & ~0x7FF
//   - a non-looping sample that ends does not stop the voice: the envelope is zeroed and the voice keeps replaying the
//     last block (silent unless an attack/increase mode brings the envelope back) until a decreasing envelope hits 0
//   - noise: one LFSR run per block, replayed from its start by every noise voice
//   - pitch modulation reads the previous voice's output from the last block when that voice ended mid-block (stale)
//   - echo lives in a private buffer (ESA ignored, sound RAM never written); it is mixed even with EON=0; the FIR's tap
//     indices step by 2 (stereo interleave) over a 16-entry history; ECEN off zeroes the echo buffer as it plays
//   - FLG mute silences the output (and the engine starts muted until FLG is written); master volume resets to 127
//   - key-on/key-off as Snes9x did it (a key-on during key-off is deferred to the next register write)
//  Left out: the SPC700 (BrokenNes's own SMP drives this engine through the DSP register writes), MixMono and the 8-bit
//  paths, OUTX/ENVX reads (the stock S-DSP answers those), snapshot fix-ups.
// =====================================================================================================================

/// <summary>The Snes9x 1.51 sound engine: feed it the DSP register writes and the sound RAM, pull 32,000 Hz stereo blocks.</summary>
public sealed class S951Dsp
{
    public const int Rate = 32000;            // so.playback_rate
    public const int BlockFrames = 533;       // 32000 / 60: one S9xMixSamples call per frame

    // ---- soundux.h
    private const int SOUND_SAMPLE = 0, SOUND_NOISE = 1;
    private const int SOUND_SILENT = 0, SOUND_ATTACK = 1, SOUND_DECAY = 2, SOUND_SUSTAIN = 3, SOUND_RELEASE = 4, SOUND_GAIN = 5,
        SOUND_INCREASE_LINEAR = 6, SOUND_INCREASE_BENT_LINE = 7, SOUND_DECREASE_LINEAR = 8, SOUND_DECREASE_EXPONENTIAL = 9;
    private const int MODE_NONE = 0, MODE_ADSR = 1, MODE_RELEASE = 4, MODE_GAIN = 5, MODE_INCREASE_LINEAR = 6,
        MODE_INCREASE_BENT_LINE = 7, MODE_DECREASE_LINEAR = 8, MODE_DECREASE_EXPONENTIAL = 9;
    private const int SOUND_DECODE_LENGTH = 16, SOUND_BUFFER_SIZE = 1024 * 16;
    private const int ENV_RANGE = 0x800, ENV_MAX = 0x7FF, ENV_SHIFT = 4;
    private const int FIXED_POINT = 0x10000, FIXED_POINT_SHIFT = 16;

    // ---- apu.h
    private const int APU_VOL_LEFT = 0x00, APU_VOL_RIGHT = 0x01, APU_P_LOW = 0x02, APU_P_HIGH = 0x03, APU_SRCN = 0x04,
        APU_ADSR1 = 0x05, APU_ADSR2 = 0x06, APU_GAIN = 0x07, APU_MVOL_LEFT = 0x0c, APU_MVOL_RIGHT = 0x1c, APU_EVOL_LEFT = 0x2c,
        APU_EVOL_RIGHT = 0x3c, APU_KON = 0x4c, APU_KOFF = 0x5c, APU_FLG = 0x6c, APU_ENDX = 0x7c, APU_EFB = 0x0d, APU_PMON = 0x2d,
        APU_NON = 0x3d, APU_EON = 0x4d, APU_DIR = 0x5d, APU_EDL = 0x7d;
    private const int APU_SOFT_RESET = 0x80, APU_MUTE = 0x40, APU_ECHO_DISABLED = 0x20, FREQUENCY_MASK = 0x3fff;

    // ---- soundux.cpp tables: gaussian table by libopenspc and SNEeSe
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
        0x513, 0x514, 0x514, 0x515, 0x516, 0x516, 0x517, 0x517, 0x517, 0x518, 0x518, 0x518, 0x518, 0x518, 0x519, 0x519 };
    private static int G1(int n) => gauss[256 + n];
    private static int G2(int n) => gauss[512 + n];
    private static int G3(int n) => gauss[255 + n];
    private static int G4(int n) => gauss[-1 + n];

    // envelope/noise table by libopenspc and SNEeSe
    private static readonly int[] env_counter_table = {
        0x0000, 0x000F, 0x0014, 0x0018, 0x001E, 0x0028, 0x0030, 0x003C, 0x0050, 0x0060, 0x0078, 0x00A0, 0x00C0, 0x00F0, 0x0140, 0x0180,
        0x01E0, 0x0280, 0x0300, 0x03C0, 0x0500, 0x0600, 0x0780, 0x0A00, 0x0C00, 0x0F00, 0x1400, 0x1800, 0x1E00, 0x2800, 0x3C00, 0x7800 };
    private const int env_counter_max_master = 0x7800;
    private int env_counter_max;

    private sealed class Channel
    {
        public int state, type;
        public short volume_left, volume_right;
        public int hertz;
        public uint frequency;
        public bool loop;
        public short sample;
        public readonly short[] decoded = new short[16];
        public ushort sample_number;
        public bool last_block, needs_decode;
        public uint block_pointer, sample_pointer;
        public bool echo;                                   // echo_buf_ptr == EchoBuffer
        public int mode;
        public readonly int[] previous = new int[2];
        public int nb_index;
        public readonly short[] nb_sample = new short[4];
        public short out_sample;
        public int xenvx, xenvx_target, xenv_count, xenv_rate, xsmp_count, xattack_rate, xdecay_rate, xsustain_rate, xsustain_level;
    }

    private readonly byte[] ram;                           // IAPU.RAM (shared with the SMP)
    public readonly byte[] Regs = new byte[0x80];          // APU.DSP
    private readonly Channel[] channels = new Channel[8];

    // SoundStatus so / SSoundData SoundData / the globals
    private bool mute_sound;
    private int echo_enable, echo_feedback, echo_ptr, echo_buffer_size, echo_write_enabled, pitch_mod;
    private readonly int[] master_volume = new int[2], echo_volume = new int[2];
    private bool no_filter;
    private int noise_count, noise_rate;
    private ushort noise_seed;
    private readonly int[] Loop = new int[16];
    private readonly int[] Echo = new int[24000];
    private readonly int[] FilterTaps = new int[8];
    private readonly int[] MixBuffer = new int[SOUND_BUFFER_SIZE];
    private readonly int[] EchoBuffer = new int[SOUND_BUFFER_SIZE];
    private uint FIRIndex;
    private readonly int[] noise_cache = new int[SOUND_BUFFER_SIZE];
    private readonly int[] wave = new int[SOUND_BUFFER_SIZE];
    private byte KeyedChannels, KeyOn, KeyOnPrev;          // APU.KeyedChannels and the S9xSetAPUDSP globals

    private readonly short[] outBuf = new short[BlockFrames * 2];

    public S951Dsp(byte[] aram)
    {
        ram = aram;
        for (int i = 0; i < 8; i++) channels[i] = new Channel();
        Reset();
    }

    /// <summary>S9xInitSound + S9xSetPlaybackRate, then S9xSoftResetAPU's DSP part.</summary>
    public void Reset()
    {
        Array.Clear(Regs);
        Array.Clear(MixBuffer); Array.Clear(EchoBuffer); Array.Clear(wave); Array.Clear(noise_cache); FIRIndex = 0;
        KeyOn = 0; KeyOnPrev = 0;
        SetPlaybackRate(Rate);
        Regs[APU_FLG] = APU_MUTE | APU_ECHO_DISABLED;
        KeyedChannels = 0;
        ResetSound(true);
        SetEchoEnable(0);
    }

    // =================================================================================================================
    //  soundux.cpp
    // =================================================================================================================

    private static short sclip15(int x) => (short)((x & 16384) != 0 ? (x | ~16383) : (x & 16383));
    private static short sclamp15(int x) => (short)(x > 16383 ? 16383 : x < -16384 ? -16384 : x);
    private static short sclamp16(int x) => (short)(x > 32767 ? 32767 : x < -32768 ? -32768 : x);

    private int SampleAddress(int sample_number) => ((Regs[APU_DIR] << 8) + (sample_number << 2)) & 0xffff;
    private int ReadWord(int a) => ram[a & 0xffff] | (ram[(a + 1) & 0xffff] << 8);

    private void SetEndOfSample(int i, Channel ch)
    {
        ch.state = SOUND_SILENT;
        ch.mode = MODE_NONE;
        ch.out_sample = 0;
        ch.xenvx = 0;
        Regs[APU_ENDX] |= (byte)(1 << i);
        Regs[APU_KON] &= (byte)~(1 << i);
        Regs[APU_KOFF] &= (byte)~(1 << i);
        KeyedChannels &= (byte)~(1 << i);
    }

    private void SetEndX(int i) => Regs[APU_ENDX] |= (byte)(1 << i);

    private static void SetEnvRate(Channel ch, int rate_count, int xtarget)
    {
        ch.xenvx_target = xtarget;
        ch.xenv_rate = rate_count;
    }

    private void SetSoundVolume(int channel, short l, short r)
    {
        // so.stereo_switch = ~0: every voice on both sides; stereo
        channels[channel].volume_left = l;
        channels[channel].volume_right = r;
    }

    private void SetMasterVolume(short l, short r) { master_volume[0] = l; master_volume[1] = r; }   // DisableMasterVolume = false
    private void SetEchoVolume(short l, short r) { echo_volume[0] = l; echo_volume[1] = r; }

    private void SetEchoEnable(int b)
    {
        echo_enable = b;
        for (int i = 0; i < 8; i++) channels[i].echo = (b & (1 << i)) != 0;
    }

    private void SetEchoFeedback(int feedback) => echo_feedback = feedback;

    private void SetEchoDelay(int delay)
    {
        echo_buffer_size = (delay << 10) * Rate / 32000;
        if (echo_buffer_size != 0) echo_ptr %= echo_buffer_size;
        else echo_ptr = 0;
    }

    private void SetEchoWriteEnable(int b) => echo_write_enabled = b;

    private void SetFrequencyModulationEnable(byte b) => pitch_mod = b & ~1;

    private void SetSoundKeyOff(int channel)
    {
        var ch = channels[channel];
        if (ch.state != SOUND_SILENT)
        {
            ch.state = SOUND_RELEASE;
            ch.mode = MODE_RELEASE;
            SetEnvRate(ch, env_counter_max, 0);
        }
    }

    private void SetFilterCoefficient(int tap, int value)
    {
        FilterTaps[tap & 7] = value;
        no_filter = FilterTaps[0] == 127 && FilterTaps[1] == 0 && FilterTaps[2] == 0 && FilterTaps[3] == 0 &&
                    FilterTaps[4] == 0 && FilterTaps[5] == 0 && FilterTaps[6] == 0 && FilterTaps[7] == 0;
    }

    private void SetSoundADSR(int channel, int ar, int dr, int sr, int sl)
    {
        var ch = channels[channel];
        ch.xattack_rate = env_counter_table[(ar << 1) + 1];
        ch.xdecay_rate = env_counter_table[(dr << 1) + 0x10];
        ch.xsustain_rate = env_counter_table[sr];
        ch.xsustain_level = (ENV_RANGE >> 3) * (sl + 1);

        switch (ch.state)
        {
            case SOUND_ATTACK: SetEnvRate(ch, ch.xattack_rate, ENV_MAX); break;
            case SOUND_DECAY: SetEnvRate(ch, ch.xdecay_rate, ch.xsustain_level); break;
            case SOUND_SUSTAIN: SetEnvRate(ch, ch.xsustain_rate, 0); break;
        }
    }

    private void SetEnvelopeHeight(int channel, int xlevel)
    {
        var ch = channels[channel];
        ch.xenvx = ch.xenvx_target = xlevel;
        ch.xenv_rate = 0;
        if (xlevel == 0 && ch.state != SOUND_SILENT && ch.state != SOUND_GAIN) SetEndOfSample(channel, ch);
    }

    private void SetSoundFrequency(int channel, int hertz)
    {
        channels[channel].frequency = (uint)((long)(hertz << (FIXED_POINT_SHIFT - 15)) * 32000 / Rate);
    }

    private void SetSoundHertz(int channel, int hertz)
    {
        channels[channel].hertz = hertz;
        SetSoundFrequency(channel, hertz);
    }

    private void DecodeBlock(Channel ch)
    {
        short[] raw = ch.decoded;
        if (ch.block_pointer > 0x10000 - 9)
        {
            Array.Clear(raw);
            ch.previous[0] = 0; ch.previous[1] = 0;
            ch.last_block = true;
            ch.loop = false;
            return;
        }

        int p = (int)ch.block_pointer;
        byte filter = ram[p++];
        ch.last_block = (filter & 1) != 0;
        ch.loop = (filter & 2) != 0;

        if (ch.state == SOUND_SILENT)
        {
            Array.Clear(raw);
            ch.previous[0] = 0; ch.previous[1] = 0;
        }
        else
        {
            int shift = filter >> 4;
            bool valid_header = shift < 0xD;
            filter &= 0x0C;
            int S1 = ch.previous[0], S2 = ch.previous[1];
            int r = 0;
            for (int i = 0; i < 8; i++)
            {
                sbyte sample1 = (sbyte)ram[p++];
                sbyte sample2 = (sbyte)(sample1 << 4);
                sample1 >>= 4;
                sample2 >>= 4;
                for (int nybble = 0; nybble < 2; nybble++)
                {
                    int o = nybble != 0 ? sample2 : sample1;
                    o = valid_header ? ((o << shift) >> 1) : (o & ~0x7FF);
                    switch (filter)
                    {
                        case 0x00: break;
                        case 0x04: o += S1 + ((-S1) >> 4); break;
                        case 0x08: o += (S1 << 1) + ((-((S1 << 1) + S1)) >> 5) - S2 + (S2 >> 4); break;
                        case 0x0C: o += (S1 << 1) + ((-(S1 + (S1 << 2) + (S1 << 3))) >> 6) - S2 + (((S2 << 1) + S2) >> 4); break;
                    }
                    o = sclip15(sclamp16(o));
                    S2 = S1;
                    S1 = o;
                    raw[r++] = (short)o;
                }
            }
            ch.previous[0] = S1;
            ch.previous[1] = S2;
        }
        ch.block_pointer += 9;
    }

    private void MixStereo(int sample_count)
    {
        int byte_count = sample_count << 1;   // so.sixteen_bit
        int pm = pitch_mod & ~Regs[APU_NON] & ~1;

        int noise_index = 0;
        int ncount = noise_count;
        int noise_count_next = noise_count;

        if (Regs[APU_NON] != 0)
        {
            noise_cache[noise_index] = noise_seed;
            for (int I = 0; I < sample_count; I += 2)
            {
                ncount -= noise_rate;
                while (ncount <= 0)
                {
                    noise_seed = (ushort)((noise_seed >> 1) | (((noise_seed << 14) ^ (noise_seed << 13)) & 0x4000));
                    noise_index = (noise_index + 1) % SOUND_BUFFER_SIZE;
                    noise_cache[noise_index] = noise_seed;
                    ncount += env_counter_max;
                }
            }
            noise_count_next = ncount;
        }

        for (int J = 0; J < 8; J++)
        {
            var ch = channels[J];
            uint freq = ch.frequency;
            bool last_block = false;

            if (ch.type == SOUND_NOISE)
            {
                noise_index = 0;
                ncount = noise_count;
            }

            bool mod1 = (pm & (1 << J)) != 0;
            bool mod2 = (pm & (1 << (J + 1))) != 0;

            if (ch.state == SOUND_SILENT || last_block)
            {
                ch.out_sample = 0;
                if (mod2) Array.Clear(wave, 0, byte_count);
                continue;
            }

            if (ch.needs_decode)
            {
                DecodeBlock(ch);
                ch.needs_decode = false;
                ch.sample = ch.decoded[0];
                ch.sample_pointer = 0;
            }

            for (int I = 0; I < sample_count; I += 2)
            {
                switch (ch.state)
                {
                    case SOUND_ATTACK:
                        if (ch.xenv_rate == env_counter_max_master) ch.xenvx += ENV_RANGE >> 1;   // FIXME
                        else
                        {
                            ch.xenv_count -= ch.xenv_rate;
                            while (ch.xenv_count <= 0) { ch.xenvx += ENV_RANGE >> 6; ch.xenv_count += env_counter_max; }
                        }
                        if (ch.xenvx > ENV_MAX)
                        {
                            ch.xenvx = ENV_MAX;
                            if (ch.xsustain_level != ENV_RANGE) { ch.state = SOUND_DECAY; SetEnvRate(ch, ch.xdecay_rate, ch.xsustain_level); }
                            else { ch.state = SOUND_SUSTAIN; SetEnvRate(ch, ch.xsustain_rate, 0); }
                        }
                        break;
                    case SOUND_DECAY:
                        ch.xenv_count -= ch.xenv_rate;
                        while (ch.xenv_count <= 0) { ch.xenvx -= ((ch.xenvx - 1) >> 8) + 1; ch.xenv_count += env_counter_max; }
                        if (ch.xenvx <= ch.xenvx_target)
                        {
                            if (ch.xenvx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                            ch.state = SOUND_SUSTAIN;
                            SetEnvRate(ch, ch.xsustain_rate, 0);
                        }
                        break;
                    case SOUND_SUSTAIN:
                        ch.xenv_count -= ch.xenv_rate;
                        while (ch.xenv_count <= 0) { ch.xenvx -= ((ch.xenvx - 1) >> 8) + 1; ch.xenv_count += env_counter_max; }
                        if (ch.xenvx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                        break;
                    case SOUND_RELEASE:
                        ch.xenv_count -= env_counter_max;
                        while (ch.xenv_count <= 0) { ch.xenvx -= ENV_RANGE >> 8; ch.xenv_count += env_counter_max; }
                        if (ch.xenvx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                        break;
                    case SOUND_INCREASE_LINEAR:
                        ch.xenv_count -= ch.xenv_rate;
                        while (ch.xenv_count <= 0) { ch.xenvx += ENV_RANGE >> 6; ch.xenv_count += env_counter_max; }
                        if (ch.xenvx > ENV_MAX) { ch.xenvx = ENV_MAX; ch.state = SOUND_GAIN; ch.mode = MODE_GAIN; SetEnvRate(ch, 0, 0); }
                        break;
                    case SOUND_INCREASE_BENT_LINE:
                        ch.xenv_count -= ch.xenv_rate;
                        while (ch.xenv_count <= 0)
                        {
                            if (ch.xenvx >= ((ENV_RANGE * 3) >> 2)) ch.xenvx += ENV_RANGE >> 8;
                            else ch.xenvx += ENV_RANGE >> 6;
                            ch.xenv_count += env_counter_max;
                        }
                        if (ch.xenvx > ENV_MAX) { ch.xenvx = ENV_MAX; ch.state = SOUND_GAIN; ch.mode = MODE_GAIN; SetEnvRate(ch, 0, 0); }
                        break;
                    case SOUND_DECREASE_LINEAR:
                        ch.xenv_count -= ch.xenv_rate;
                        while (ch.xenv_count <= 0) { ch.xenvx -= ENV_RANGE >> 6; ch.xenv_count += env_counter_max; }
                        if (ch.xenvx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                        break;
                    case SOUND_DECREASE_EXPONENTIAL:
                        ch.xenv_count -= ch.xenv_rate;
                        while (ch.xenv_count <= 0) { ch.xenvx -= ((ch.xenvx - 1) >> 8) + 1; ch.xenv_count += env_counter_max; }
                        if (ch.xenvx <= 0) { SetEndOfSample(J, ch); goto stereo_exit; }
                        break;
                    case SOUND_GAIN:
                        SetEnvRate(ch, 0, 0);
                        break;
                }

                ch.xsmp_count = unchecked(ch.xsmp_count + (mod1 ? (int)(((long)freq * (32768 + wave[I >> 1])) >> 15) : (int)freq));

                while (ch.xsmp_count >= 0)
                {
                    ch.xsmp_count -= FIXED_POINT;
                    ch.nb_sample[ch.nb_index] = ch.sample;
                    ch.nb_index = (ch.nb_index + 1) & 3;

                    ch.sample_pointer++;
                    if (ch.sample_pointer == SOUND_DECODE_LENGTH)
                    {
                        ch.sample_pointer = 0;
                        if (ch.last_block)
                        {
                            SetEndX(J);
                            if (!ch.loop)
                            {
                                ch.xenvx = 0;
                                last_block = true;
                                while (ch.xsmp_count >= 0)
                                {
                                    ch.xsmp_count -= FIXED_POINT;
                                    ch.nb_sample[ch.nb_index] = 0;
                                    ch.nb_index = (ch.nb_index + 1) & 3;
                                }
                                break;
                            }
                            ch.last_block = false;
                            ch.sample_number = Regs[APU_SRCN + J * 0x10];   // Mystical Ninja pipe sound fix
                            ch.block_pointer = (uint)ReadWord(SampleAddress(ch.sample_number) + 2);
                        }
                        DecodeBlock(ch);
                    }
                    ch.sample = ch.decoded[ch.sample_pointer];
                }

                int outx;
                if (ch.type == SOUND_SAMPLE)
                {
                    // 4-point gaussian interpolation (Settings.InterpolatedSound)
                    int d = ch.xsmp_count >> (FIXED_POINT_SHIFT - 8);
                    outx = (G4(-d) * ch.nb_sample[ch.nb_index]) >> 11;
                    outx += (G3(-d) * ch.nb_sample[(ch.nb_index + 1) & 3]) >> 11;
                    outx += (G2(d) * ch.nb_sample[(ch.nb_index + 2) & 3]) >> 11;
                    outx = sclip15(outx);
                    outx += (G1(d) * ch.nb_sample[(ch.nb_index + 3) & 3]) >> 11;
                    outx = sclamp15(outx);
                }
                else
                {
                    ncount -= noise_rate;
                    while (ncount <= 0)
                    {
                        ncount += env_counter_max;
                        noise_index = (noise_index + 1) % SOUND_BUFFER_SIZE;
                    }
                    outx = sclip15(noise_cache[noise_index]);
                }

                outx = (outx * ch.xenvx) >> 11;
                ch.out_sample = (short)(outx << 1);
                if (mod2) wave[I >> 1] = outx << 1;

                int VL = ((outx * ch.volume_left) >> 7) << 1;    // so.sound_switch = 255
                int VR = ((outx * ch.volume_right) >> 7) << 1;
                MixBuffer[I] += VL;
                MixBuffer[I + 1] += VR;
                if (ch.echo) { EchoBuffer[I] += VL; EchoBuffer[I + 1] += VR; }   // else DummyEchoBuffer (never read)
            }
        stereo_exit:;
        }

        if (Regs[APU_NON] != 0) noise_count = noise_count_next;
    }

    /// <summary>S9xMixSamples (16-bit stereo, echo on): one block of interleaved stereo samples.</summary>
    public short[] RenderBlock()
    {
        int sample_count = BlockFrames * 2;
        short[] buffer = outBuf;
        Array.Clear(MixBuffer, 0, sample_count);
        Array.Clear(EchoBuffer, 0, sample_count);
        MixStereo(sample_count);

        for (int J = 0; J < sample_count; J++)
        {
            int E = Echo[echo_ptr];
            Loop[FIRIndex & 15] = E;
            if (no_filter) E = (E * 127) >> 7;
            else
            {
                E = E * FilterTaps[0];
                E += Loop[(FIRIndex - 2) & 15] * FilterTaps[1];
                E += Loop[(FIRIndex - 4) & 15] * FilterTaps[2];
                E += Loop[(FIRIndex - 6) & 15] * FilterTaps[3];
                E += Loop[(FIRIndex - 8) & 15] * FilterTaps[4];
                E += Loop[(FIRIndex - 10) & 15] * FilterTaps[5];
                E += Loop[(FIRIndex - 12) & 15] * FilterTaps[6];
                E += Loop[(FIRIndex - 14) & 15] * FilterTaps[7];
                E >>= 7;
            }
            FIRIndex++;

            if (echo_write_enabled != 0) Echo[echo_ptr] = sclamp16(EchoBuffer[J] + ((E * echo_feedback) >> 7));
            else Echo[echo_ptr] = 0;   // FIXME: Snes9x's echo buffer is not in APU_RAM

            if (++echo_ptr >= echo_buffer_size) echo_ptr = 0;

            buffer[J] = sclamp16((MixBuffer[J] * master_volume[J & 1] + E * echo_volume[J & 1]) >> 7);
        }

        if (mute_sound) Array.Clear(buffer);
        return buffer;
    }

    private void ResetSound(bool full)
    {
        for (int i = 0; i < 8; i++)
        {
            var c = channels[i];
            c.state = SOUND_SILENT; c.mode = MODE_NONE; c.type = SOUND_SAMPLE;
            c.volume_left = 0; c.volume_right = 0; c.hertz = 0; c.loop = false;
            c.xsmp_count = 0; c.xenvx = 0; c.xenvx_target = 0; c.xenv_count = 0; c.xenv_rate = 0;
            c.xattack_rate = 0; c.xdecay_rate = 0; c.xsustain_rate = 0; c.xsustain_level = 0;
            if (full)
            {
                c.out_sample = 0; c.block_pointer = 0; c.sample_pointer = 0; c.sample = 0; c.sample_number = 0; c.last_block = false;
                c.previous[0] = c.previous[1] = 0;
                Array.Clear(c.decoded); Array.Clear(c.nb_sample);
                c.nb_index = 0;
            }
        }
        FilterTaps[0] = 127;
        for (int i = 1; i < 8; i++) FilterTaps[i] = 0;

        mute_sound = true;
        noise_count = env_counter_max;
        noise_seed = 0x4000;

        if (full)
        {
            echo_enable = 0; echo_write_enabled = 0; pitch_mod = 0;
            echo_volume[0] = echo_volume[1] = 0;
            Array.Clear(Loop); Array.Clear(Echo);
        }
        // At least Super Bomberman 2 requires the default master volume is not zero.
        master_volume[0] = master_volume[1] = 127;
        no_filter = true;
        echo_ptr = 0;
        echo_feedback = 0;
        echo_buffer_size = 1;
    }

    private void SetPlaybackRate(int rate)
    {
        Array.Clear(Loop); Array.Clear(Echo);
        SetEchoDelay(Regs[APU_EDL] & 0xF);
        for (int i = 0; i < 8; i++) SetSoundFrequency(i, channels[i].hertz);
        env_counter_max = env_counter_max_master * rate / 32000;
        noise_count = env_counter_max;
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
        ch.state = SOUND_SILENT; ch.mode = MODE_NONE; ch.xenvx = 0;

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
        ch.xenv_count = env_counter_max;
        ch.xsmp_count = 3 * FIXED_POINT;   // since gaussian interpolation uses 4 points
        Array.Clear(ch.nb_sample);
        ch.nb_index = 0;

        switch (ch.mode)
        {
            case MODE_ADSR:   // FIXME: rapid attack
                if (ch.xattack_rate == env_counter_max_master)
                {
                    ch.xenvx = ENV_MAX;
                    if (ch.xsustain_level == ENV_RANGE) { ch.state = SOUND_SUSTAIN; SetEnvRate(ch, ch.xsustain_rate, 0); }
                    else { ch.state = SOUND_DECAY; SetEnvRate(ch, ch.xdecay_rate, ch.xsustain_level); }
                }
                else
                {
                    ch.state = SOUND_ATTACK;
                    ch.xenvx = 0;
                    SetEnvRate(ch, ch.xattack_rate, ENV_MAX);
                }
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
    //  apu.cpp
    // =================================================================================================================

    private void FixEnvelope(int channel, byte gain, byte adsr1, byte adsr2)
    {
        if ((adsr1 & 0x80) != 0)
        {
            if (SetSoundMode(channel, MODE_ADSR))
                SetSoundADSR(channel, adsr1 & 0xf, (adsr1 >> 4) & 7, adsr2 & 0x1f, (adsr2 >> 5) & 7);
        }
        else if ((gain & 0x80) == 0)
        {
            if (SetSoundMode(channel, MODE_GAIN)) SetEnvelopeHeight(channel, (gain & 0x7f) << ENV_SHIFT);
        }
        else if ((gain & 0x40) != 0)
        {
            if (SetSoundMode(channel, (gain & 0x20) != 0 ? MODE_INCREASE_BENT_LINE : MODE_INCREASE_LINEAR))
                SetEnvRate(channels[channel], env_counter_table[gain & 0x1f], ENV_MAX);
        }
        else
        {
            if (SetSoundMode(channel, (gain & 0x20) != 0 ? MODE_DECREASE_EXPONENTIAL : MODE_DECREASE_LINEAR))
                SetEnvRate(channels[channel], env_counter_table[gain & 0x1f], 0);
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
                    mute_sound = (b & APU_MUTE) != 0;
                    noise_rate = env_counter_table[b & 0x1f];
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
                    // At Shin Megami Tensei suggestion 6/11/00: volume writes are not filtered on "same value"
                    case APU_VOL_LEFT: SetSoundVolume(reg >> 4, (sbyte)b, (sbyte)Regs[reg + 1]); break;
                    case APU_VOL_RIGHT: SetSoundVolume(reg >> 4, (sbyte)Regs[reg - 1], (sbyte)b); break;
                    case APU_P_LOW: SetSoundHertz(reg >> 4, ((b + (Regs[reg + 1] << 8)) & FREQUENCY_MASK) * 8); break;
                    case APU_P_HIGH: SetSoundHertz(reg >> 4, (((b << 8) + Regs[reg - 1]) & FREQUENCY_MASK) * 8); break;
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
