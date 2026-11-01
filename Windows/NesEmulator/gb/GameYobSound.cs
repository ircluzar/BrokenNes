// Ported from GameYob (Nintendo DS Game Boy emulator by Drenn / Stewmath), master @ 45599fbb2f08b2f8042e7f9f47578ef078f2e889
//   platform/ds/arm9/source/dssoundengine.cpp  (arm9: Game Boy registers -> DS channel parameters, FIFO commands)
//   platform/ds/arm7/source/sound.c            (arm7: the commands -> DS sound channel registers)
//   platform/ds/arm7/include/noise.h           (the 32 KB 15-bit-LFSR PCM8 table; regenerated here, same bytes)
//   https://github.com/Stewmath/GameYob
// Copyright (c) 2013 Drenn & contributors. License: MIT.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// GameYob does not synthesize Game Boy sound: it maps the GB channels onto the DS's sound hardware. This file does
// both halves: the arm9 SoundEngine (length / envelope / sweep timed on the GB clock, frequencies converted to DS
// rates) and an emulation of the DS channels it drives (PSG duty squares on DS channels 8/9, a 32-byte PCM8 loop for
// the wave on channel 0, PCM8 LFSR noise tables on channel 14, the "background hum" DC loop on channel 1, volume
// 0-127, pan 0-127, master volume, 32,768 Hz mixer). Default mode (no "hyperSound"): commands go over the FIFO as
// soon as arm9 computes them; here they are delivered in a bunch at each GB frame (70,224 T-cycles), reading the
// shared state as it stands then, like arm7 reading sharedData late.
// Quirks kept:
//  - square/wave rates are integer-divided before scaling (131072/(2048-f)*8, 65536/(2048-f)*32) and go through the
//    DS timer's SOUND_FREQ (-0x1000000/n, truncated to 16 bits: wraps for very low rates);
//  - squares use DS duties {12.5, 25, 50, 75}% and are never restarted (phase runs on; silent = volume 0);
//  - channel volume = GB volume*2 (max 30 of 127), doubled when panned centre; wave levels 15/7/3;
//  - NR50 only uses SO1 (right) volume, *18, for the DS master volume; NR51 picks pan 0/64/127, none = muted;
//  - background hum: a DC PCM loop of 0x7F at 0x20 volume per NR51-enabled channel (the clicks it causes are kept);
//  - envelope counters never reload on trigger/NR12 writes; sweep timer reloads with 128/time integer division and
//    adds its own (negative) remainder twice; chanFreq is unsigned (sweep-down underflow kills the channel);
//  - NR10 sweep and NR1x length/envelope run on the GB clock, but the DS hears changes once per frame (no
//    mid-frame sample-accurate effects: GameYob says only hyperSound gets "Pikachu" right);
//  - noise is a pre-baked LFSR PCM table (127+1 or 32768 bytes) played at (524288/r)>>(s+1) Hz; switching width
//    restarts it; the wave PCM is 4-bit -> pcmVals (*0x70) and is read live from wave RAM writes.
using System;
using System.IO;

namespace NesEmulator.Gb;

public sealed class GameYobSound
{
    public const int Rate = 32768;          // DS mixer output rate
    private const int ClockSpeed = 4194304;
    private const double DsTicksPerSample = 16756991.0 / Rate;   // DS sound timers run at 33.513982 MHz / 2
    private const int ChanOn1 = 1, ChanOn2 = 2, ChanOn3 = 4, ChanOn4 = 8;

    // ---- GB side (gameboy->ioRam) ----
    public readonly byte[] io = new byte[0x40];

    // ---- arm9 SoundEngine ----
    private double chan4FreqRatio = 0.5;
    private int chan1SweepTime, chan1SweepCounter, chan1SweepDir, chan1SweepAmount;
    private readonly int[] chanLen = new int[4], chanLenCounter = new int[4], chanUseLen = new int[4];
    private readonly uint[] chanFreq = new uint[4];
    private readonly int[] chanVol = new int[4], chanEnvDir = new int[4], chanEnvCounter = new int[4], chanEnvSweep = new int[4];
    private static readonly int[] PcmVals = MakePcm();

    // ---- sharedData ----
    private int chanOn, volControl, chanOutput;
    private bool lfsr7Bit;
    private readonly int[] chanDuty = new int[4], chanPan = new int[4], chanRealVol = new int[4], chanRealFreq = new int[4];
    private readonly byte[] sampleData = new byte[0x20];

    // ---- FIFO (commands, delivered at frame end) ----
    private const int CmdUpdate = 0, CmdStart = 1, CmdVolume = 2, CmdMaster = 3;
    private readonly int[] fifo = new int[256];
    private int fifoCount;

    // ---- arm7 + DS sound hardware ----
    private static readonly int[] Channels = { 8, 9, 0, 14 };
    private static readonly int[] DutyIndex = { 0, 1, 3, 5 };
    private readonly uint[] schannelCR = new uint[4];
    private bool currentLfsr;
    private int soundCnt = 0x7F;                 // REG_SOUNDCNT master volume
    private readonly Ds[] ds = new Ds[16];
    private double dcL, dcR, prevL, prevR;       // DS output coupling capacitor

    private enum Src { None, Wave, Hum, Lfsr7, Lfsr15 }
    private sealed class Ds
    {
        public uint Cr;                          // bits 0-6 vol, 16-22 pan, 24-26 duty, 29-30 format, 31 enable
        public ushort Timer;
        public Src Source;
        public int Length;                       // bytes
        public int Pos;
        public double Acc;
        public bool On => (Cr & 0x80000000u) != 0;
    }

    private static readonly byte[] Lfsr7 = MakeLfsr7();
    private static readonly byte[] Lfsr15 = MakeLfsr15();

    public GameYobSound() { for (int i = 0; i < 16; i++) ds[i] = new Ds(); Reset(); }

    private static int[] MakePcm()
    {
        double[] analog = { -1, -0.8667, -0.7334, -0.6, -0.4668, -0.3335, -0.2, -0.067, 0.0664, 0.2, 0.333, 0.4668, 0.6, 0.7334, 0.8667, 1 };
        var p = new int[16];
        for (int i = 0; i < 16; i++) p[i] = (int)(analog[i] * 0x70);
        return p;
    }

    private static byte[] MakeLfsr7()
    {
        const string bits = "11111011111001111010111000011011101001100010101100000101111000111011011001001010010000100111001011010001000110011010101000000010";
        var b = new byte[128];
        for (int i = 0; i < 128; i++) b[i] = bits[i] == '1' ? (byte)0xA0 : (byte)0x60;
        return b;
    }

    private static byte[] MakeLfsr15()
    {
        // noise.h's table: the 15-bit LFSR (seed 1, out bit set -> 0x60), first byte 0xA0.
        var b = new byte[32768];
        int l = 1;
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (l & 1) != 0 ? (byte)0x60 : (byte)0xA0;
            int x = (l ^ (l >> 1)) & 1;
            l = (l >> 1) | (x << 14);
        }
        b[0] = 0xA0;
        return b;
    }

    public void Reset()
    {
        Array.Clear(io);
        chan4FreqRatio = 0.5;
        chan1SweepTime = chan1SweepCounter = chan1SweepDir = chan1SweepAmount = 0;
        Array.Clear(chanLen); Array.Clear(chanLenCounter); Array.Clear(chanUseLen); Array.Clear(chanFreq);
        Array.Clear(chanVol); Array.Clear(chanEnvDir); Array.Clear(chanEnvCounter); Array.Clear(chanEnvSweep);
        chanOn = volControl = chanOutput = 0; lfsr7Bit = false;
        Array.Clear(chanDuty); Array.Clear(chanPan); Array.Clear(chanRealVol); Array.Clear(chanRealFreq); Array.Clear(sampleData);
        fifoCount = 0; Array.Clear(schannelCR); currentLfsr = false; soundCnt = 0x7F;
        foreach (var c in ds) { c.Cr = 0; c.Timer = 0; c.Source = Src.None; c.Length = 0; c.Pos = 0; c.Acc = 0; }
        dcL = dcR = prevL = prevR = 0;

        // installGameboySoundFIFO: the background "hum" channel
        var h = ds[1];
        h.Source = Src.Hum; h.Length = 16; h.Timer = 0;
        h.Cr = 0x80000000u | (64u << 16) | (1u << 27);

        // Post-boot I/O as the Game Boy leaves it, then SoundEngine::init() + refresh()
        byte[] post = { 0x80, 0xBF, 0xF3, 0xFF, 0xBF, 0xFF, 0x3F, 0x00, 0xFF, 0xBF, 0x7F, 0xFF, 0x9F, 0xFF, 0xBF, 0xFF, 0xFF, 0x00, 0x00, 0xBF, 0x77, 0xF3, 0xF1 };
        for (int i = 0; i < post.Length; i++) io[0x10 + i] = post[i];
        for (int i = 0; i < 16; i++) io[0x30 + i] = (byte)((i & 1) != 0 ? 0xFF : 0x00);
        Refresh();
        Deliver();
    }

    /// <summary>SoundEngine::refresh(): replay every register, retrigger the channels NR52 says are on.</summary>
    public void Refresh()
    {
        chanOn = 0;
        Handle(0x26, io[0x26]);
        for (int i = 0x10; i <= 0x3F; i++)
            Handle(i, i is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(io[i] & ~0x80) : io[i]);
        if ((io[0x26] & 1) != 0) Handle(0x14, (byte)(io[0x14] | 0x80));
        if ((io[0x26] & 2) != 0) Handle(0x19, (byte)(io[0x19] | 0x80));
        if ((io[0x26] & 4) != 0) Handle(0x1E, (byte)(io[0x1E] | 0x80));
        if ((io[0x26] & 8) != 0) Handle(0x23, (byte)(io[0x23] | 0x80));
    }

    public byte Read(int reg) => io[reg];

    /// <summary>The CPU writes a sound register (ioRam store + handleSoundRegister).</summary>
    public void Write(int reg, byte v)
    {
        if (reg < 0x26 && (io[0x26] & 0x80) == 0) return;
        if (reg == 0x26) { io[0x26] = (byte)((v & 0x80) | (io[0x26] & 0x0F) | 0x70); if ((v & 0x80) == 0) io[0x26] &= 0xF0; }
        else io[reg] = v;
        if ((reg is 0x14 or 0x19 or 0x23 && (v & 0x80) != 0) || (reg == 0x1E && (v & 0x80) != 0 && (io[0x1A] & 0x80) != 0))
            io[0x26] |= (byte)(1 << ((reg - 0x14) / 5));
        Handle(reg, v);
    }

    private void ClearSoundChannel(int bit) => io[0x26] &= (byte)~bit;

    private void Send(int cmd, int data) { if (fifoCount < fifo.Length) fifo[fifoCount++] = cmd << 8 | data; }

    // ======================== arm9: dssoundengine.cpp ========================

    public void UpdateSound(int cycles)
    {
        bool changed = false;
        if ((chanOn & ChanOn1) != 0 && chan1SweepTime != 0)
        {
            chan1SweepCounter -= cycles;
            while (chan1SweepCounter <= 0)
            {
                chan1SweepCounter += (ClockSpeed / (128 / chan1SweepTime)) + chan1SweepCounter;
                chanFreq[0] = (uint)(chanFreq[0] + (chanFreq[0] >> chan1SweepAmount) * chan1SweepDir);
                if (chanFreq[0] > 0x7FF) { chanOn &= ~ChanOn1; ClearSoundChannel(ChanOn1); }
                else RefreshSoundFreq(0);
                changed = true;
            }
        }
        for (int i = 0; i < 4; i++)
        {
            if (i != 2 && (chanOn & (1 << i)) != 0 && chanEnvSweep[i] != 0)
            {
                chanEnvCounter[i] -= cycles;
                while (chanEnvCounter[i] <= 0)
                {
                    chanEnvCounter[i] += chanEnvSweep[i] * ClockSpeed / 64;
                    chanVol[i] += chanEnvDir[i];
                    if (chanVol[i] < 0) chanVol[i] = 0;
                    if (chanVol[i] > 0xF) chanVol[i] = 0xF;
                    changed = true;
                    RefreshSoundVolume(i, false);
                }
            }
        }
        for (int i = 0; i < 4; i++)
        {
            if ((chanOn & (1 << i)) != 0 && chanUseLen[i] != 0)
            {
                chanLenCounter[i] -= cycles;
                if (chanLenCounter[i] <= 0) { chanOn &= ~(1 << i); changed = true; ClearSoundChannel(1 << i); }
            }
        }
        if (changed) Send(CmdUpdate, 4);   // "Force immediate update, even though hyperSound isn't on."
    }

    private void Handle(int ioReg, byte val)
    {
        switch (ioReg)
        {
            case 0x10:
                chan1SweepTime = (val >> 4) & 7;
                if (chan1SweepTime != 0) chan1SweepCounter = ClockSpeed / (128 / chan1SweepTime);
                chan1SweepDir = (val & 8) != 0 ? -1 : 1;
                chan1SweepAmount = val & 7;
                break;
            case 0x11:
                chanLen[0] = val & 0x3F;
                chanLenCounter[0] = (64 - chanLen[0]) * ClockSpeed / 256;
                chanDuty[0] = val >> 6;
                Send(CmdUpdate, 0);
                break;
            case 0x12: Envelope(0, val); break;
            case 0x13:
                chanFreq[0] &= 0x700; chanFreq[0] |= val;
                RefreshSoundFreq(0); Send(CmdUpdate, 0);
                break;
            case 0x14:
                chanFreq[0] &= 0xFF; chanFreq[0] |= (uint)(val & 7) << 8;
                RefreshSoundFreq(0);
                chanUseLen[0] = (val & 0x40) != 0 ? 1 : 0;
                if ((val & 0x80) != 0)
                {
                    chanLenCounter[0] = (64 - chanLen[0]) * ClockSpeed / 256;
                    chanOn |= ChanOn1;
                    chanVol[0] = io[0x12] >> 4;
                    if (chan1SweepTime != 0) chan1SweepCounter = ClockSpeed / (128 / chan1SweepTime);
                    RefreshSoundVolume(0, false);
                    Send(CmdStart, 0);
                }
                else Send(CmdUpdate, 0);
                break;
            case 0x16:
                chanLen[1] = val & 0x3F;
                chanLenCounter[1] = (64 - chanLen[1]) * ClockSpeed / 256;
                chanDuty[1] = val >> 6;
                Send(CmdUpdate, 1);
                break;
            case 0x17: Envelope(1, val); break;
            case 0x18:
                chanFreq[1] &= 0x700; chanFreq[1] |= val;
                RefreshSoundFreq(1); Send(CmdUpdate, 1);
                break;
            case 0x19:
                chanFreq[1] &= 0xFF; chanFreq[1] |= (uint)(val & 7) << 8;
                RefreshSoundFreq(1);
                chanUseLen[1] = (val & 0x40) != 0 ? 1 : 0;
                if ((val & 0x80) != 0)
                {
                    chanLenCounter[1] = (64 - chanLen[1]) * ClockSpeed / 256;
                    chanOn |= ChanOn2;
                    chanVol[1] = io[0x17] >> 4;
                    RefreshSoundVolume(1, false);
                    Send(CmdStart, 1);
                }
                else Send(CmdUpdate, 1);
                break;
            case 0x1A:
                if ((val & 0x80) == 0) { chanOn &= ~ChanOn3; Send(CmdUpdate, 2); }
                break;
            case 0x1B:
                chanLen[2] = val;
                chanLenCounter[2] = (256 - chanLen[2]) * ClockSpeed / 256;
                break;
            case 0x1C:
                chanVol[2] = ((val >> 5) & 3) switch { 0 => 0, 1 => 15, 2 => 15 >> 1, _ => 15 >> 2 };
                RefreshSoundVolume(2, true);
                break;
            case 0x1D:
                chanFreq[2] &= 0xFF00; chanFreq[2] |= val;
                RefreshSoundFreq(2); Send(CmdUpdate, 2);
                break;
            case 0x1E:
                chanFreq[2] &= 0xFF; chanFreq[2] |= (uint)(val & 7) << 8;
                RefreshSoundFreq(2);
                chanUseLen[2] = (val & 0x40) != 0 ? 1 : 0;
                if ((val & 0x80) != 0 && (io[0x1A] & 0x80) != 0)
                {
                    chanOn |= ChanOn3;
                    chanLenCounter[2] = (256 - chanLen[2]) * ClockSpeed / 256;
                    RefreshSoundVolume(2, false);
                    Send(CmdStart, 2);
                }
                else Send(CmdUpdate, 2);
                break;
            case 0x20:
                chanLen[3] = val & 0x3F;
                chanLenCounter[3] = (64 - chanLen[3]) * ClockSpeed / 256;
                break;
            case 0x21: Envelope(3, val); break;
            case 0x22:
                chanFreq[3] = (uint)(val >> 4);
                chan4FreqRatio = val & 7;
                if (chan4FreqRatio == 0) chan4FreqRatio = 0.5;
                lfsr7Bit = (val & 8) != 0;
                RefreshSoundFreq(3); Send(CmdUpdate, 3);
                break;
            case 0x23:
                chanUseLen[3] = (val & 0x40) != 0 ? 1 : 0;
                if ((val & 0x80) != 0)
                {
                    chanLenCounter[3] = (64 - chanLen[3]) * ClockSpeed / 256;
                    chanOn |= ChanOn4;
                    chanVol[3] = io[0x21] >> 4;
                    RefreshSoundVolume(3, false);
                    Send(CmdStart, 3);
                }
                break;
            case 0x24:
                if ((volControl & 7) != (val & 7)) { volControl = val; Send(CmdMaster, 0); }
                else volControl = val;
                break;
            case 0x25:
                chanOutput = val;
                for (int i = 0; i < 4; i++) RefreshSoundPan(i);
                Send(CmdUpdate, 4); Send(CmdMaster, 0);
                break;
            case 0x26:
                if ((val & 0x80) == 0) { chanOn = 0; Send(CmdUpdate, 4); }
                break;
            default:
                if (ioReg >= 0x30 && ioReg <= 0x3F)
                {
                    int b = ioReg - 0x30;
                    byte s = io[ioReg];
                    sampleData[b * 2] = (byte)PcmVals[s >> 4];
                    sampleData[b * 2 + 1] = (byte)PcmVals[s & 0xF];
                }
                break;
        }
    }

    private void Envelope(int i, byte val)
    {
        chanVol[i] = val >> 4;
        chanEnvDir[i] = (val & 8) != 0 ? 1 : -1;
        chanEnvSweep[i] = val & 7;
        RefreshSoundVolume(i, true);
    }

    private void RefreshSoundPan(int i)
    {
        bool r = (chanOutput & (1 << i)) != 0, l = (chanOutput & (1 << (i + 4))) != 0;
        chanPan[i] = r && l ? 64 : r ? 127 : l ? 0 : 128;   // 128 = "special signal" (silent)
    }

    private void RefreshSoundVolume(int i, bool send)
    {
        if ((chanOn & (1 << i)) == 0) return;
        int volume = chanVol[i];
        if (send && chanRealVol[i] != volume) { chanRealVol[i] = volume; Send(CmdVolume, i); }
        else chanRealVol[i] = volume;
    }

    private void RefreshSoundFreq(int i)
    {
        int freq = 0;
        if (i <= 1) freq = (int)(131072 / (2048 - chanFreq[i]) * 8);
        else if (i == 2) freq = (int)(65536 / (2048 - chanFreq[i]) * 32);
        else freq = (int)(524288 / chan4FreqRatio) >> (int)(chanFreq[i] + 1);
        chanRealFreq[i] = freq;
    }

    // ======================== arm7: sound.c ========================

    private void Deliver()
    {
        for (int k = 0; k < fifoCount; k++) DoCommand(fifo[k] >> 8, fifo[k] & 0xFF);
        fifoCount = 0;
    }

    private void DoCommand(int cmd, int data)
    {
        switch (cmd)
        {
            case CmdUpdate:
                if (data == 4) for (int i = 0; i < 4; i++) UpdateChannel(i, true);
                else UpdateChannel(data, true);
                break;
            case CmdStart: StartChannel(data); break;
            case CmdVolume: SetChannelVolume(data, true); break;
            case CmdMaster: UpdateMasterVolume(); break;
        }
    }

    private static ushort SoundFreq(int n) => n == 0 ? (ushort)0 : (ushort)(-(0x1000000 / n));   // libnds SOUND_FREQ

    private void SetCr(int channel, uint v)
    {
        var c = ds[channel];
        bool was = c.On;
        c.Cr = v;
        if (!was && c.On) { c.Pos = 0; c.Acc = 0; }   // the DS restarts a channel on a 0->1 enable edge only
    }

    private void SetChannelVolume(int c, bool write)
    {
        int channel = Channels[c];
        int volume = chanRealVol[c] * 2;
        if (chanPan[c] >= 128) volume = 0;
        else if (chanPan[c] == 64) volume *= 2;   // "DS divides sound by 2 for each speaker; gameboy doesn't"
        schannelCR[c] = (schannelCR[c] & ~0x7Fu) | (uint)volume;
        if (write) SetCr(channel, (ds[channel].Cr & ~0x7Fu) | (uint)(volume & 0x7F));
    }

    private const uint PanMask = 127u << 16;

    private void UpdateChannel(int c, bool write)
    {
        int channel = Channels[c];
        if ((chanOn & (1 << c)) == 0)
        {
            schannelCR[c] &= ~0x7Fu;
            SetCr(channel, ds[channel].Cr & ~0x7Fu);
            return;
        }
        ds[channel].Timer = SoundFreq(chanRealFreq[c]);
        uint pan = (uint)(chanPan[c] & 127) << 16;
        if (c < 2)
            schannelCR[c] = (schannelCR[c] & ~(PanMask | 7u << 24)) | pan | ((uint)DutyIndex[chanDuty[c] & 3] << 24);
        else if (c == 2)
            schannelCR[c] = (schannelCR[c] & ~PanMask) | pan;
        else
        {
            if (currentLfsr != lfsr7Bit) StartChannel(c);
            schannelCR[c] = (schannelCR[c] & ~PanMask) | pan;
        }
        if (write)
            SetCr(channel, (ds[channel].Cr & ~(PanMask | 7u << 24)) | (schannelCR[c] & (PanMask | 7u << 24)));
        SetChannelVolume(c, write);
    }

    private void StartChannel(int c)
    {
        int channel = Channels[c];
        var d = ds[channel];
        if (c == 2)
        {
            d.Source = Src.Wave; d.Length = 0x20;
            schannelCR[c] = 1u << 27;                 // PCM8, repeat
        }
        else if (c == 3)
        {
            SetCr(channel, 0);
            currentLfsr = lfsr7Bit;
            if (lfsr7Bit) { d.Source = Src.Lfsr7; d.Length = 128; }
            else { d.Source = Src.Lfsr15; d.Length = 32768; }
            schannelCR[c] = 1u << 27;
        }
        else schannelCR[c] = 3u << 29;                // PSG
        UpdateChannel(c, false);
        SetCr(channel, schannelCR[c] | 0x80000000u);
    }

    private void UpdateMasterVolume()
    {
        int so1 = volControl & 7;
        if (so1 * 18 != (soundCnt & 0x7F)) soundCnt = so1 * 18;
        int vol = 0;
        for (int i = 0; i < 4; i++) if ((chanOutput & ((1 << i) | (1 << (i + 4)))) != 0) vol += 0x20;
        if (vol == 0x80) vol = 0x7F;
        uint cr = ds[1].Cr & ~0x7Fu;
        if (chanOutput != 0) cr |= (uint)vol;
        SetCr(1, cr);
    }

    // ======================== DS sound hardware ========================

    private int Sample(Ds c)
    {
        int fmt = (int)(c.Cr >> 29) & 3;
        if (fmt == 3)
        {
            int duty = (int)(c.Cr >> 24) & 7;
            return (c.Pos & 7) > 6 - duty ? 0x7FFF : -0x7FFF;   // duty d: (d+1)/8 high
        }
        sbyte b = c.Source switch
        {
            Src.Wave => (sbyte)sampleData[c.Pos],
            Src.Hum => 0x7F,
            Src.Lfsr7 => (sbyte)Lfsr7[c.Pos],
            Src.Lfsr15 => (sbyte)Lfsr15[c.Pos],
            _ => 0
        };
        return b << 8;
    }

    /// <summary>Delivers the frame's FIFO commands, then mixes n stereo samples at 32,768 Hz.</summary>
    public void Render(short[] dst, int n)
    {
        Deliver();
        for (int s = 0; s < n; s++)
        {
            long l = 0, r = 0;
            for (int ch = 0; ch < 16; ch++)
            {
                var c = ds[ch];
                if (!c.On) continue;
                int fmt = (int)(c.Cr >> 29) & 3;
                int v = Sample(c) * (int)(c.Cr & 0x7F) >> 7;
                int pan = (int)(c.Cr >> 16) & 0x7F;
                l += (long)v * (128 - pan) >> 7;
                r += (long)v * pan >> 7;
                int period = 0x10000 - c.Timer;
                c.Acc += DsTicksPerSample;
                while (c.Acc >= period)
                {
                    c.Acc -= period;
                    c.Pos++;
                    if (fmt == 3) c.Pos &= 7;
                    else if (c.Pos >= c.Length) c.Pos = 0;
                }
            }
            l = l * soundCnt >> 7; r = r * soundCnt >> 7;
            l = Math.Clamp(l, -32768, 32767); r = Math.Clamp(r, -32768, 32767);
            // the DS's output capacitor (AC coupling)
            dcL = 0.995 * (dcL + l - prevL); prevL = l;
            dcR = 0.995 * (dcR + r - prevR); prevR = r;
            dst[s * 2] = (short)Math.Clamp((int)dcL, -32768, 32767);
            dst[s * 2 + 1] = (short)Math.Clamp((int)dcR, -32768, 32767);
        }
    }

    // ======================== save state ========================

    public void Save(BinaryWriter w)
    {
        w.Write(io);
        w.Write(chan4FreqRatio);
        w.Write(chan1SweepTime); w.Write(chan1SweepCounter); w.Write(chan1SweepDir); w.Write(chan1SweepAmount);
        for (int i = 0; i < 4; i++)
        {
            w.Write(chanLen[i]); w.Write(chanLenCounter[i]); w.Write(chanUseLen[i]); w.Write(chanFreq[i]);
            w.Write(chanVol[i]); w.Write(chanEnvDir[i]); w.Write(chanEnvCounter[i]); w.Write(chanEnvSweep[i]);
            w.Write(chanDuty[i]); w.Write(chanPan[i]); w.Write(chanRealVol[i]); w.Write(chanRealFreq[i]); w.Write(schannelCR[i]);
        }
        w.Write(chanOn); w.Write(volControl); w.Write(chanOutput); w.Write(lfsr7Bit); w.Write(sampleData);
        w.Write(fifoCount); for (int i = 0; i < fifoCount; i++) w.Write(fifo[i]);
        w.Write(currentLfsr); w.Write(soundCnt);
        foreach (var c in ds) { w.Write(c.Cr); w.Write(c.Timer); w.Write((int)c.Source); w.Write(c.Length); w.Write(c.Pos); w.Write(c.Acc); }
        w.Write(dcL); w.Write(dcR); w.Write(prevL); w.Write(prevR);
    }

    public void Load(BinaryReader r)
    {
        r.Read(io, 0, io.Length);
        chan4FreqRatio = r.ReadDouble();
        chan1SweepTime = r.ReadInt32(); chan1SweepCounter = r.ReadInt32(); chan1SweepDir = r.ReadInt32(); chan1SweepAmount = r.ReadInt32();
        for (int i = 0; i < 4; i++)
        {
            chanLen[i] = r.ReadInt32(); chanLenCounter[i] = r.ReadInt32(); chanUseLen[i] = r.ReadInt32(); chanFreq[i] = r.ReadUInt32();
            chanVol[i] = r.ReadInt32(); chanEnvDir[i] = r.ReadInt32(); chanEnvCounter[i] = r.ReadInt32(); chanEnvSweep[i] = r.ReadInt32();
            chanDuty[i] = r.ReadInt32(); chanPan[i] = r.ReadInt32(); chanRealVol[i] = r.ReadInt32(); chanRealFreq[i] = r.ReadInt32(); schannelCR[i] = r.ReadUInt32();
        }
        chanOn = r.ReadInt32(); volControl = r.ReadInt32(); chanOutput = r.ReadInt32(); lfsr7Bit = r.ReadBoolean(); r.Read(sampleData, 0, sampleData.Length);
        fifoCount = Math.Min(r.ReadInt32(), fifo.Length); for (int i = 0; i < fifoCount; i++) fifo[i] = r.ReadInt32();
        currentLfsr = r.ReadBoolean(); soundCnt = r.ReadInt32();
        foreach (var c in ds) { c.Cr = r.ReadUInt32(); c.Timer = r.ReadUInt16(); c.Source = (Src)r.ReadInt32(); c.Length = r.ReadInt32(); c.Pos = r.ReadInt32(); c.Acc = r.ReadDouble(); }
        dcL = r.ReadDouble(); dcR = r.ReadDouble(); prevL = r.ReadDouble(); prevR = r.ReadDouble();
    }
}
