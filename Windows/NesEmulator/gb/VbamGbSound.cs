using System.IO;

namespace NesEmulator.Gb;

// =====================================================================================================================
//  VBA-M (VisualBoyAdvance-M) Game Boy sound as of commit 5e3902b4 (2007-12-03; VBA 1.8.0-beta), the last
//  revision of the hand-written engine before commit 872441ce replaced it with blargg's Gb_Apu. Ported to C#.
//
//  Source: https://github.com/visualboyadvance-m/visualboyadvance-m @ 5e3902b4: src/gb/gbSound.cpp (+ the shared
//  tables and defaults in src/Sound.cpp and the sound clocking / register writes in src/gb/GB.cpp, unchanged from
//  1.7.2 for what is used here). Made from our VBA 1.7.2 port (VbaGbSound.cs) plus the 1.7.2 -> 5e3902b4 diff.
//  Copyright (C) 1999-2003 Forgotten; Copyright (C) 2005-2006 Forgotten and the VBA development team.
//  License: GPL-2.0-or-later. This is a documented exception to the repository's license policy - see
//  THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port. The bugs and shortcuts of the original are kept on purpose; they are the sound. From 1.7.2 (see
//  VbaGbSound.cs): 48-M-cycle sample ticks, everything counted in output samples, SOUND_MAGIC pitch, the swapped
//  terminal volumes, the noise LFSR seeds / silent shifts 13-15, the sweep quirks, no DAC checks.
//  Changed in this revision (kept as written):
//   - read masks: NR10|80, NR11/NR21|3F, NR13/NR23/NR31/NR33 read FF, NR14/NR24/NR34/NR44|BF, NR30|7F, NR32|9F;
//     NR52 reads bit 7 | 0x70 | the channel bits (NR12/NR22/NR41-43/NR50/NR51 still raw; NR41 not masked)
//   - frequencies live in shadow registers (freq1low/high ...); sweep writes them, not NR13/NR14
//   - a retrigger reloads the length from the value last written to NRx1 (sound1ATLreload ...), and so do NR13/NR14
//     and NR23/NR24 writes; noise retrigger no longer re-reads NR43
//   - "digital sound" now multiplies instead of replacing: res *= level * 256 (the 1.7.2 DC is gone). The flag is
//     still only computed on register writes, so a channel triggered at envelope volume 0 that then fades in is
//     scaled by 256 instead of 60 (about 4x louder) until the next sound register write
//   - the wave channel outputs 0 (not its last sample) while off; noise length expiry clears NR52 bit 3 (fixed)
//   - power-off clears the NR52 channel bits; reset writes NR52 = F0 on SGB and F1 elsewhere (DMG/CGB here: F1),
//     which leaves channel 1's status bit set after reset with nothing playing
//  Left out: the echo / low-pass / reverse options (off by default) and the GBA direct-sound parts of Sound.cpp.
// =====================================================================================================================

/// <summary>VBA-M 5e3902b4's (2007) Game Boy sound engine: feed it FF10-FF3F writes, call <see cref="Tick"/> every
/// 48 M-cycles, get one stereo sample pair.</summary>
public sealed class VbamGbSound
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
    private int s1ATLreload, freq1low, freq1high, s2ATLreload, freq2low, freq2high, s3ATLreload, freq3low, freq3high, s4ATLreload, freq4;

    // soundBuffer[0..3][soundIndex]: the channels' values this tick, stored through a u8 and read back as s8
    private readonly byte[] buf = new byte[4];

    public VbamGbSound() { Reset(); }

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
        s1ATLreload = freq1low = freq1high = s2ATLreload = freq2low = freq2high = s3ATLreload = freq3low = freq3high = s4ATLreload = freq4 = 0;

        Event(0x10, 0x80); Event(0x11, 0xbf); Event(0x12, 0xf3); Event(0x14, 0xbf);
        Event(0x16, 0x3f); Event(0x17, 0x00); Event(0x19, 0xbf);
        Event(0x1a, 0x7f); Event(0x1b, 0xff); Event(0x1c, 0xbf); Event(0x1e, 0xbf);
        Event(0x20, 0xff); Event(0x21, 0x00); Event(0x22, 0x00); Event(0x23, 0xbf);
        Event(0x24, 0x77); Event(0x25, 0xf3);
        Event(0x26, 0xf1);   // gbHardware & 4 (SGB) ? 0xf0 : 0xf1; no SGB model here
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
                Mem[address] = (byte)(data | 0x80);
                s1SweepATL = s1SweepATLReload = 344 * ((data >> 4) & 7);
                s1SweepSteps = data & 7; s1SweepUpDown = data & 0x08; s1SweepStep = 0;
                break;
            case 0x11:
                Mem[address] = (byte)(data | 0x3f);
                s1Duty = data >> 6; s1ATL = s1ATLreload = 172 * (64 - (data & 0x3f));
                break;
            case 0x12:
                s1EnvVol = data >> 4; s1EnvUpDown = data & 0x08; s1EnvATLReload = s1EnvATL = 689 * (data & 7);
                break;
            case 0x13:
                Mem[address] = 0xff;
                freq1low = data;
                freq = ((freq1high & 7) << 8) | freq1low;
                s1ATL = s1ATLreload;
                freq = 2048 - freq;
                s1Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                break;
            case 0x14:
                Mem[address] = (byte)(data | 0xbf);
                freq1high = data;
                freq = 2048 - (((freq1high & 7) << 8) | freq1low);
                s1ATL = s1ATLreload;
                s1Continue = data & 0x40;
                s1Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                if ((data & 0x80) != 0)
                {
                    Mem[0x26] |= 1;
                    s1EnvVol = Mem[0x12] >> 4; s1EnvUpDown = Mem[0x12] & 0x08;
                    s1EnvATLReload = s1EnvATL = 689 * (Mem[0x12] & 7);
                    s1SweepATL = s1SweepATLReload = 344 * ((Mem[0x10] >> 4) & 7);
                    s1SweepSteps = Mem[0x10] & 7; s1SweepUpDown = Mem[0x10] & 0x08; s1SweepStep = 0;
                    s1Index = 0; s1On = 1;
                }
                break;
            case 0x16:
                Mem[address] = (byte)(data | 0x3f);
                s2Duty = data >> 6; s2ATL = s2ATLreload = 172 * (64 - (data & 0x3f));
                break;
            case 0x17:
                s2EnvVol = data >> 4; s2EnvUpDown = data & 0x08; s2EnvATLReload = s2EnvATL = 689 * (data & 7);
                break;
            case 0x18:
                Mem[address] = 0xff;
                freq2low = data;
                freq = ((freq2high & 7) << 8) | freq2low;
                s2ATL = s2ATLreload;
                freq = 2048 - freq;
                s2Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                break;
            case 0x19:
                Mem[address] = (byte)(data | 0xbf);
                freq2high = data;
                freq = 2048 - (((freq2high & 7) << 8) | freq2low);
                s2ATL = s2ATLreload;
                s2Continue = data & 0x40;
                s2Skip = freq != 0 ? SOUND_MAGIC / freq : 0;
                if ((data & 0x80) != 0)
                {
                    Mem[0x26] |= 2;
                    s2EnvVol = Mem[0x17] >> 4; s2EnvUpDown = Mem[0x17] & 0x08;
                    s2ATL = s2ATLreload;
                    s2EnvATLReload = s2EnvATL = 689 * (Mem[0x17] & 7);
                    s2Index = 0; s2On = 1;
                }
                break;
            case 0x1a:
                Mem[address] = (byte)(data | 0x7f);
                if ((data & 0x80) == 0) { Mem[0x26] &= 0xfb; s3On = 0; }
                break;
            case 0x1b:
                Mem[address] = 0xff;
                s3ATL = s3ATLreload = 172 * (256 - data);
                break;
            case 0x1c:
                Mem[address] = (byte)(data | 0x9f);
                s3OutputLevel = (data >> 5) & 3;
                break;
            case 0x1d:
                Mem[address] = 0xff;
                freq3low = data;
                freq = 2048 - (((freq3high & 7) << 8) | freq3low);
                s3Skip = freq != 0 ? SOUND_MAGIC_2 / freq : 0;
                break;
            case 0x1e:
                Mem[address] = (byte)(data | 0xbf);
                freq3high = data;
                freq = 2048 - (((freq3high & 7) << 8) | freq3low);
                s3Skip = freq != 0 ? SOUND_MAGIC_2 / freq : 0;
                s3Continue = data & 0x40;
                if ((data & 0x80) != 0 && (Mem[0x1a] & 0x80) != 0)
                {
                    Mem[0x26] |= 4;
                    s3ATL = s3ATLreload;
                    s3Index = 0; s3On = 1;
                }
                break;
            case 0x20:
                s4ATL = s4ATLreload = 172 * (64 - (data & 0x3f));
                break;
            case 0x21:
                s4EnvVol = data >> 4; s4EnvUpDown = data & 0x08; s4EnvATLReload = s4EnvATL = 689 * (data & 7);
                break;
            case 0x22:
                freq = freq4 = FreqRatio[data & 7];
                s4NSteps = data & 0x08;
                s4Skip = (freq << 8) / NOISE_MAGIC;
                s4Clock = data >> 4;
                freq = freq / ShiftClock[s4Clock];
                s4ShiftSkip = (freq << 8) / NOISE_MAGIC;
                break;
            case 0x23:
                Mem[address] = (byte)(data | 0xbf);
                s4Continue = data & 0x40;
                if ((data & 0x80) != 0)
                {
                    Mem[0x26] |= 8;
                    s4EnvVol = Mem[0x21] >> 4; s4EnvUpDown = Mem[0x21] & 0x08;
                    s4ATL = s4ATLreload;
                    s4EnvATLReload = s4EnvATL = 689 * (Mem[0x21] & 7);
                    s4On = 1; s4Index = 0; s4ShiftIndex = 0;
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
                if ((data & 0x80) == 0) { s1On = s2On = s3On = s4On = 0; Mem[address] &= 0xf0; }
                Mem[address] = (byte)((data & 0x80) | 0x70 | (Mem[address] & 0xf));
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
                int freq = ((freq1high & 7) << 8) | freq1low;
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
                    freq1low = newfreq & 0xff;
                    freq1high = (freq1high & 0xf8) | ((newfreq >> 8) & 7);
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
        int value = 0;
        if (s3On != 0 && (s3ATL != 0 || s3Continue == 0))
        {
            value = s3Last;
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
            if (s4ATL <= 0 && s4Continue != 0) { Mem[0x26] &= 0xf7; s4On = 0; }
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
        if (digitalSound) res *= level * 256;
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
        s1ATLreload, freq1low, freq1high, s2ATLreload, freq2low, freq2high, s3ATLreload, freq3low, freq3high, s4ATLreload, freq4,
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
        s1ATLreload = f[i++]; freq1low = f[i++]; freq1high = f[i++]; s2ATLreload = f[i++]; freq2low = f[i++]; freq2high = f[i++];
        s3ATLreload = f[i++]; freq3low = f[i++]; freq3high = f[i++]; s4ATLreload = f[i++]; freq4 = f[i++];
        s1Index &= 0x1fffffff; s2Index &= 0x1fffffff;
    }
}
