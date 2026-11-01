using System.IO;

namespace NesEmulator.Gb;

// =====================================================================================================================
//  VisualBoyAdvance 1.7.2 Game Boy sound, ported to C#.
//
//  Source: VisualBoyAdvance 1.7.2 (2004), src/gb/gbSound.cpp + gbSound.h, the shared tables and defaults in
//  src/Sound.cpp (soundWavePattern, soundFreqRatio, soundShiftClock, soundQuality = 2, soundVolume = 0,
//  soundEnableFlag = 0x3ff) and the sound clocking in src/gb/GB.cpp (soundTicks / SOUND_CLOCK_TICKS = 24 *
//  soundQuality, gbSoundTick; FF10-FF26 writes go to gbSoundEvent, sound register reads return gbMemory raw).
//  Mirror used: https://github.com/weimingtom/vba-raspi (commit 9fbd04f8, VERSION "VisualBoyAdvance 1.7.2").
//  Copyright (C) 1999-2003 Forgotten; Copyright (C) 2004 Forgotten and the VBA development team.
//  License: GPL-2.0-or-later. This is a documented exception to the repository's license policy - see
//  THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (the user's call: quick and dirty first, polish later). The bugs and shortcuts of the original are kept
//  on purpose; they are the sound:
//   - one sample per 48 M-cycles (soundQuality 2, nominal 22,050 Hz); everything (length, envelope, sweep) counts in
//     output samples, not in a frame sequencer; pitch is SOUND_MAGIC-based, ~0.94% sharp of the 22,050 Hz design
//   - left output = NR51 bits 4-7 times the NR50 bits 0-2 volume (SO1), right = bits 0-3 times bits 4-6: the
//     terminal volumes are swapped against the panning bits
//   - "digital sound": when every channel is silent (as of the last register write) the output is a DC level of
//     NR50 volume * 256, not zero
//   - the wave channel holds its last sample when stopped (DC); no DAC checks; NR13/NR23 writes reload length
//   - noise: the 7-bit mode starts its LFSR at 0x7FFF and the 15-bit mode at 0x7F; clock shifts 13-15 are silent;
//     noise length expiry clears NR52 bit 1 (channel 2's) instead of bit 3
//   - sweep: a shift that leaves the frequency unchanged sets it to 0; sweep never stops on a negative result
//   - NR52 reads back the last byte written plus the trigger bits (0xF0 after reset); power-off does not clear
//     registers; no read masks anywhere
//  Left out: the echo / low-pass / reverse options (off by default) and the GBA direct-sound parts of Sound.cpp.
// =====================================================================================================================

/// <summary>VisualBoyAdvance 1.7.2's Game Boy sound engine: feed it FF10-FF3F writes, call <see cref="Tick"/> every
/// 48 M-cycles, get one stereo sample pair.</summary>
public sealed class VbaGbSound
{
    public const int SoundQuality = 2;                 // Sound.cpp default (win32 regQueryDwordValue("soundQuality", 2))
    public const int Rate = 44100 / SoundQuality;      // 22,050 Hz
    public const int ClockTicks = 24 * SoundQuality;   // SOUND_CLOCK_TICKS, in GB.cpp's M-cycles

    private const int SOUND_MAGIC = 0x60000000, SOUND_MAGIC_2 = 0x30000000, NOISE_MAGIC = 5;
    private static readonly int SoundVolume = 0;       // Sound.cpp default: x1
    private const int SoundEnableFlag = 0x3ff;

    private static readonly sbyte[][] WavePattern = BuildPatterns();
    private static readonly int[] FreqRatio = { 1048576, 524288, 262144, 174763, 131072, 104858, 87381, 74898 };
    private static readonly int[] ShiftClock = { 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 1, 1 };

    private static sbyte[][] BuildPatterns()
    {
        var p = new sbyte[4][];
        int[] high = { 4, 8, 16, 24 };   // 0x01 for the first n steps, then 0xff (-1)
        for (int d = 0; d < 4; d++) { p[d] = new sbyte[32]; for (int i = 0; i < 32; i++) p[d][i] = (sbyte)(i < high[d] ? 1 : -1); }
        return p;
    }

    /// <summary>gbMemory[0xFF00 + i] for i in 0x10-0x3F (the sound registers and wave RAM, raw).</summary>
    public readonly byte[] Mem = new byte[0x40];

    private int soundLevel1, soundLevel2, soundBalance, soundMasterOn, soundVIN;
    private bool digitalSound;

    private int s1On, s1ATL, s1Skip, s1Index, s1Continue, s1EnvVol, s1EnvATL, s1EnvUpDown, s1EnvATLReload;
    private int s1SweepATL, s1SweepATLReload, s1SweepSteps, s1SweepUpDown, s1SweepStep, s1Duty;
    private int s2On, s2ATL, s2Skip, s2Index, s2Continue, s2EnvVol, s2EnvATL, s2EnvUpDown, s2EnvATLReload, s2Duty;
    private int s3On, s3ATL, s3Skip, s3Index, s3Continue, s3OutputLevel, s3Last;
    private int s4On, s4Clock, s4ATL, s4Skip, s4Index, s4ShiftRight, s4ShiftSkip, s4ShiftIndex, s4NSteps, s4Continue;
    private int s4EnvVol, s4EnvATL, s4EnvUpDown, s4EnvATLReload;

    // soundBuffer[0..3][soundIndex]: the channels' values this tick, stored through a u8 and read back as s8
    private readonly byte[] buf = new byte[4];

    public VbaGbSound() { Reset(); }

    // ---- gbSoundReset ----
    public void Reset()
    {
        soundMasterOn = 1; soundLevel1 = 7; soundLevel2 = 7; soundVIN = 0;
        s1On = s1ATL = s1Skip = s1Index = s1Continue = s1EnvVol = s1EnvATL = s1EnvUpDown = s1EnvATLReload = 0;
        s1SweepATL = s1SweepATLReload = s1SweepSteps = s1SweepUpDown = s1SweepStep = 0; s1Duty = 2;
        s2On = s2ATL = s2Skip = s2Index = s2Continue = s2EnvVol = s2EnvATL = s2EnvUpDown = s2EnvATLReload = 0; s2Duty = 2;
        s3On = s3ATL = s3Skip = s3Index = s3Continue = s3OutputLevel = 0;
        s4On = s4Clock = s4ATL = s4Skip = s4Index = 0; s4ShiftRight = 0x7f; s4NSteps = 0; s4Continue = 0;
        s4EnvVol = s4EnvATL = s4EnvUpDown = s4EnvATLReload = 0;

        Event(0x10, 0x80); Event(0x11, 0xbf); Event(0x12, 0xf3); Event(0x14, 0xbf);
        Event(0x16, 0x3f); Event(0x17, 0x00); Event(0x19, 0xbf);
        Event(0x1a, 0x7f); Event(0x1b, 0xff); Event(0x1c, 0xbf); Event(0x1e, 0xbf);
        Event(0x20, 0xff); Event(0x21, 0x00); Event(0x22, 0x00); Event(0x23, 0xbf);
        Event(0x24, 0x77); Event(0x25, 0xf3);
        Event(0x26, 0xf0);
        s1On = s2On = s3On = s4On = 0;
        for (int a = 0x30; a < 0x40; a += 2) { Mem[a] = 0x00; Mem[a + 1] = 0xff; }
    }

    // ---- GB.cpp register access ----
    public byte Read(int reg) => reg >= 0x10 && reg < 0x40 ? Mem[reg] : (byte)0xFF;

    public void Write(int reg, byte v)
    {
        if (reg < 0x10 || reg >= 0x40) return;
        if (reg <= 0x26) Event(reg, v); else Mem[reg] = v;
    }

    // ---- gbSoundEvent ----
    public void Event(int address, int data)
    {
        int freq;
        Mem[address] = (byte)data;
        switch (address)
        {
            case 0x10:
                s1SweepATL = s1SweepATLReload = 344 * ((data >> 4) & 7);
                s1SweepSteps = data & 7; s1SweepUpDown = data & 0x08; s1SweepStep = 0;
                break;
            case 0x11:
                s1Duty = data >> 6; s1ATL = 172 * (64 - (data & 0x3f));
                break;
            case 0x12:
                s1EnvVol = data >> 4; s1EnvUpDown = data & 0x08; s1EnvATLReload = s1EnvATL = 689 * (data & 7);
                break;
            case 0x13:
                freq = ((Mem[0x14] & 7) << 8) | data;
                s1ATL = 172 * (64 - (Mem[0x11] & 0x3f));
                freq = 2048 - freq;
                s1Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                break;
            case 0x14:
                freq = 2048 - (((data & 7) << 8) | Mem[0x13]);
                s1ATL = 172 * (64 - (Mem[0x11] & 0x3f));
                s1Continue = data & 0x40;
                s1Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                if ((data & 0x80) != 0)
                {
                    Mem[0x26] |= 1;
                    s1EnvVol = Mem[0x12] >> 4; s1EnvUpDown = Mem[0x12] & 0x08;
                    s1ATL = 172 * (64 - (Mem[0x11] & 0x3f));
                    s1EnvATLReload = s1EnvATL = 689 * (Mem[0x12] & 7);
                    s1SweepATL = s1SweepATLReload = 344 * ((Mem[0x10] >> 4) & 7);
                    s1SweepSteps = Mem[0x10] & 7; s1SweepUpDown = Mem[0x10] & 0x08; s1SweepStep = 0;
                    s1Index = 0; s1On = 1;
                }
                break;
            case 0x16:
                s2Duty = data >> 6; s2ATL = 172 * (64 - (data & 0x3f));
                break;
            case 0x17:
                s2EnvVol = data >> 4; s2EnvUpDown = data & 0x08; s2EnvATLReload = s2EnvATL = 689 * (data & 7);
                break;
            case 0x18:
                freq = ((Mem[0x19] & 7) << 8) | data;
                s2ATL = 172 * (64 - (Mem[0x16] & 0x3f));
                freq = 2048 - freq;
                s2Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                break;
            case 0x19:
                freq = 2048 - (((data & 7) << 8) | Mem[0x18]);
                s2ATL = 172 * (64 - (Mem[0x16] & 0x3f));
                s2Continue = data & 0x40;
                s2Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                if ((data & 0x80) != 0)
                {
                    Mem[0x26] |= 2;
                    s2EnvVol = Mem[0x17] >> 4; s2EnvUpDown = Mem[0x17] & 0x08;
                    s2ATL = 172 * (64 - (Mem[0x16] & 0x3f));
                    s2EnvATLReload = s2EnvATL = 689 * (Mem[0x17] & 7);
                    s2Index = 0; s2On = 1;
                }
                break;
            case 0x1a:
                if ((data & 0x80) == 0) { Mem[0x26] &= 0xfb; s3On = 0; }
                break;
            case 0x1b:
                s3ATL = 172 * (256 - data);
                break;
            case 0x1c:
                s3OutputLevel = (data >> 5) & 3;
                break;
            case 0x1d:
                freq = 2048 - (((Mem[0x1e] & 7) << 8) | data);
                s3Skip = freq != 0 ? SOUND_MAGIC_2 / freq : 0;
                break;
            case 0x1e:
                freq = 2048 - (((data & 7) << 8) | Mem[0x1d]);
                s3Skip = freq != 0 ? SOUND_MAGIC_2 / freq : 0;
                s3Continue = data & 0x40;
                if ((data & 0x80) != 0 && (Mem[0x1a] & 0x80) != 0)
                {
                    Mem[0x26] |= 4;
                    s3ATL = 172 * (256 - Mem[0x1b]);
                    s3Index = 0; s3On = 1;
                }
                break;
            case 0x20:
                s4ATL = 172 * (64 - (data & 0x3f));
                break;
            case 0x21:
                s4EnvVol = data >> 4; s4EnvUpDown = data & 0x08; s4EnvATLReload = s4EnvATL = 689 * (data & 7);
                break;
            case 0x22:
                freq = FreqRatio[data & 7];
                s4NSteps = data & 0x08;
                s4Skip = (freq << 8) / NOISE_MAGIC;
                s4Clock = data >> 4;
                freq = freq / ShiftClock[s4Clock];
                s4ShiftSkip = (freq << 8) / NOISE_MAGIC;
                break;
            case 0x23:
                s4Continue = data & 0x40;
                if ((data & 0x80) != 0)
                {
                    Mem[0x26] |= 8;
                    s4EnvVol = Mem[0x21] >> 4; s4EnvUpDown = Mem[0x21] & 0x08;
                    s4ATL = 172 * (64 - (Mem[0x20] & 0x3f));
                    s4EnvATLReload = s4EnvATL = 689 * (Mem[0x21] & 7);
                    s4On = 1; s4Index = 0; s4ShiftIndex = 0;
                    freq = FreqRatio[Mem[0x22] & 7];
                    s4Skip = (freq << 8) / NOISE_MAGIC;
                    s4NSteps = Mem[0x22] & 0x08;
                    freq = freq / ShiftClock[Mem[0x22] >> 4];
                    s4ShiftSkip = (freq << 8) / NOISE_MAGIC;
                    s4ShiftRight = s4NSteps != 0 ? 0x7fff : 0x7f;
                }
                break;
            case 0x24:
                soundVIN = data & 0x88; soundLevel1 = data & 7; soundLevel2 = (data >> 4) & 7;
                break;
            case 0x25:
                soundBalance = data & SoundEnableFlag;
                break;
            case 0x26:
                soundMasterOn = data & 0x80;
                if ((data & 0x80) == 0) s1On = s2On = s3On = s4On = 0;
                break;
        }

        digitalSound = true;
        if (s1On != 0 && s1EnvVol != 0) digitalSound = false;
        if (s2On != 0 && s2EnvVol != 0) digitalSound = false;
        if (s3On != 0 && s3OutputLevel != 0) digitalSound = false;
        if (s4On != 0 && s4EnvVol != 0) digitalSound = false;
    }

    // ---- gbSoundChannel1..4 ----
    private void Channel1()
    {
        int vol = s1EnvVol, value = 0;
        if (s1On != 0 && (s1ATL != 0 || s1Continue == 0))
        {
            s1Index += SoundQuality * s1Skip;
            s1Index &= 0x1fffffff;
            value = WavePattern[s1Duty][s1Index >> 24] * vol;
        }
        buf[0] = (byte)value;
        if (s1On == 0) return;
        if (s1ATL != 0)
        {
            s1ATL -= SoundQuality;
            if (s1ATL <= 0 && s1Continue != 0) { Mem[0x26] &= 0xfe; s1On = 0; }
        }
        if (s1EnvATL != 0)
        {
            s1EnvATL -= SoundQuality;
            if (s1EnvATL <= 0)
            {
                if (s1EnvUpDown != 0) { if (s1EnvVol < 15) s1EnvVol++; }
                else if (s1EnvVol != 0) s1EnvVol--;
                s1EnvATL += s1EnvATLReload;
            }
        }
        if (s1SweepATL != 0)
        {
            s1SweepATL -= SoundQuality;
            if (s1SweepATL <= 0)
            {
                int freq = ((Mem[0x14] & 7) << 8) | Mem[0x13];
                int updown = s1SweepUpDown != 0 ? -1 : 1;
                int newfreq;
                if (s1SweepSteps != 0)
                {
                    newfreq = freq + updown * freq / (1 << s1SweepSteps);
                    if (newfreq == freq) newfreq = 0;
                }
                else newfreq = freq;

                if (newfreq < 0) s1SweepATL += s1SweepATLReload;
                else if (newfreq > 2047) { s1SweepATL = 0; s1On = 0; Mem[0x26] &= 0xfe; }
                else
                {
                    s1SweepATL += s1SweepATLReload;
                    s1Skip = SOUND_MAGIC / (2048 - newfreq);
                    Mem[0x13] = (byte)(newfreq & 0xff);
                    Mem[0x14] = (byte)((Mem[0x14] & 0xf8) | ((newfreq >> 8) & 7));
                }
            }
        }
    }

    private void Channel2()
    {
        int vol = s2EnvVol, value = 0;
        if (s2On != 0 && (s2ATL != 0 || s2Continue == 0))
        {
            s2Index += SoundQuality * s2Skip;
            s2Index &= 0x1fffffff;
            value = WavePattern[s2Duty][s2Index >> 24] * vol;
        }
        buf[1] = (byte)value;
        if (s2On == 0) return;
        if (s2ATL != 0)
        {
            s2ATL -= SoundQuality;
            if (s2ATL <= 0 && s2Continue != 0) { Mem[0x26] &= 0xfd; s2On = 0; }
        }
        if (s2EnvATL != 0)
        {
            s2EnvATL -= SoundQuality;
            if (s2EnvATL <= 0)
            {
                if (s2EnvUpDown != 0) { if (s2EnvVol < 15) s2EnvVol++; }
                else if (s2EnvVol != 0) s2EnvVol--;
                s2EnvATL += s2EnvATLReload;
            }
        }
    }

    private void Channel3()
    {
        int value = s3Last;
        if (s3On != 0 && (s3ATL != 0 || s3Continue == 0))
        {
            s3Index += SoundQuality * s3Skip;
            s3Index &= 0x1fffffff;
            value = Mem[0x30 + (s3Index >> 25)];
            if ((s3Index & 0x01000000) != 0) value &= 0x0f; else value >>= 4;
            value -= 8;
            switch (s3OutputLevel)
            {
                case 0: value = 0; break;
                case 1: break;
                case 2: value >>= 1; break;
                case 3: value >>= 2; break;
            }
            s3Last = value;
        }
        buf[2] = (byte)value;
        if (s3On != 0 && s3ATL != 0)
        {
            s3ATL -= SoundQuality;
            if (s3ATL <= 0 && s3Continue != 0) { Mem[0x26] &= 0xfb; s3On = 0; }
        }
    }

    private void Channel4()
    {
        int vol = s4EnvVol, value = 0;
        if (s4Clock <= 0x0c)
        {
            if (s4On != 0 && (s4ATL != 0 || s4Continue == 0))
            {
                s4Index += SoundQuality * s4Skip;
                s4ShiftIndex += SoundQuality * s4ShiftSkip;
                if (s4NSteps != 0)
                {
                    while (s4ShiftIndex > 0x1fffff)
                    {
                        s4ShiftRight = (((s4ShiftRight << 6) ^ (s4ShiftRight << 5)) & 0x40) | (s4ShiftRight >> 1);
                        s4ShiftIndex -= 0x200000;
                    }
                }
                else
                {
                    while (s4ShiftIndex > 0x1fffff)
                    {
                        s4ShiftRight = (((s4ShiftRight << 14) ^ (s4ShiftRight << 13)) & 0x4000) | (s4ShiftRight >> 1);
                        s4ShiftIndex -= 0x200000;
                    }
                }
                s4Index &= 0x1fffff;
                s4ShiftIndex &= 0x1fffff;
                value = ((s4ShiftRight & 1) * 2 - 1) * vol;
            }
            else value = 0;
        }
        buf[3] = (byte)value;
        if (s4On == 0) return;
        if (s4ATL != 0)
        {
            s4ATL -= SoundQuality;
            if (s4ATL <= 0 && s4Continue != 0) { Mem[0x26] &= 0xfd; s4On = 0; }   // sic: channel 2's bit
        }
        if (s4EnvATL != 0)
        {
            s4EnvATL -= SoundQuality;
            if (s4EnvATL <= 0)
            {
                if (s4EnvUpDown != 0) { if (s4EnvVol < 15) s4EnvVol++; }
                else if (s4EnvVol != 0) s4EnvVol--;
                s4EnvATL += s4EnvATLReload;
            }
        }
    }

    // ---- gbSoundMix (soundEcho / soundLowPass / soundReverse off: the defaults) ----
    private short MixSide(int balanceShift, int level)
    {
        int res = 0;
        for (int c = 0; c < 4; c++)
            if ((soundBalance & (1 << (balanceShift + c))) != 0) res += (sbyte)buf[c];
        if (digitalSound) res = level * 256;
        else res *= level * 60;
        switch (SoundVolume)
        {
            case 0: case 1: case 2: case 3: res *= SoundVolume + 1; break;
            case 4: res >>= 2; break;
            case 5: res >>= 1; break;
        }
        if (res > 32767) res = 32767;
        if (res < -32768) res = -32768;
        return (short)res;
    }

    /// <summary>gbSoundTick: one output sample pair (first = soundFinalWave's even slot, the left).</summary>
    public void Tick(out short left, out short right)
    {
        if (soundMasterOn != 0)
        {
            Channel1(); Channel2(); Channel3(); Channel4();
            left = MixSide(4, soundLevel1);
            right = MixSide(0, soundLevel2);
        }
        else { left = 0; right = 0; }
    }

    // ---- state (gbSoundSaveStruct, more or less) ----
    public void Save(BinaryWriter w)
    {
        w.Write(Mem);
        foreach (int v in Fields()) w.Write(v);
        w.Write(digitalSound);
    }

    public void Load(BinaryReader r)
    {
        r.Read(Mem, 0, Mem.Length);
        var f = new int[Fields().Length];
        for (int i = 0; i < f.Length; i++) f[i] = r.ReadInt32();
        SetFields(f);
        digitalSound = r.ReadBoolean();
    }

    private int[] Fields() => new[]
    {
        soundLevel1, soundLevel2, soundBalance, soundMasterOn, soundVIN,
        s1On, s1ATL, s1Skip, s1Index, s1Continue, s1EnvVol, s1EnvATL, s1EnvUpDown, s1EnvATLReload,
        s1SweepATL, s1SweepATLReload, s1SweepSteps, s1SweepUpDown, s1SweepStep, s1Duty,
        s2On, s2ATL, s2Skip, s2Index, s2Continue, s2EnvVol, s2EnvATL, s2EnvUpDown, s2EnvATLReload, s2Duty,
        s3On, s3ATL, s3Skip, s3Index, s3Continue, s3OutputLevel, s3Last,
        s4On, s4Clock, s4ATL, s4Skip, s4Index, s4ShiftRight, s4ShiftSkip, s4ShiftIndex, s4NSteps, s4Continue,
        s4EnvVol, s4EnvATL, s4EnvUpDown, s4EnvATLReload,
    };

    private void SetFields(int[] f)
    {
        int i = 0;
        soundLevel1 = f[i++]; soundLevel2 = f[i++]; soundBalance = f[i++]; soundMasterOn = f[i++]; soundVIN = f[i++];
        s1On = f[i++]; s1ATL = f[i++]; s1Skip = f[i++]; s1Index = f[i++]; s1Continue = f[i++]; s1EnvVol = f[i++]; s1EnvATL = f[i++]; s1EnvUpDown = f[i++]; s1EnvATLReload = f[i++];
        s1SweepATL = f[i++]; s1SweepATLReload = f[i++]; s1SweepSteps = f[i++]; s1SweepUpDown = f[i++]; s1SweepStep = f[i++]; s1Duty = f[i++] & 3;
        s2On = f[i++]; s2ATL = f[i++]; s2Skip = f[i++]; s2Index = f[i++]; s2Continue = f[i++]; s2EnvVol = f[i++]; s2EnvATL = f[i++]; s2EnvUpDown = f[i++]; s2EnvATLReload = f[i++]; s2Duty = f[i++] & 3;
        s3On = f[i++]; s3ATL = f[i++]; s3Skip = f[i++]; s3Index = f[i++] & 0x1fffffff; s3Continue = f[i++]; s3OutputLevel = f[i++] & 3; s3Last = f[i++];
        s4On = f[i++]; s4Clock = f[i++] & 15; s4ATL = f[i++]; s4Skip = f[i++]; s4Index = f[i++]; s4ShiftRight = f[i++]; s4ShiftSkip = f[i++]; s4ShiftIndex = f[i++]; s4NSteps = f[i++]; s4Continue = f[i++];
        s4EnvVol = f[i++]; s4EnvATL = f[i++]; s4EnvUpDown = f[i++]; s4EnvATLReload = f[i++];
        s1Index &= 0x1fffffff; s2Index &= 0x1fffffff;
    }
}
