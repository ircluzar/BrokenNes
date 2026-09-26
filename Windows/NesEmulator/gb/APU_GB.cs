using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>Read-only snapshot of one GB sound channel, for bridges that re-voice GB audio on other sound chips.</summary>
public readonly struct GbApuChannelView
{
    public readonly bool Enabled, DacOn, Left, Right;
    /// <summary>Current volume 0-15. For the wave channel: the output level mapped to 0/15/7/3 (mute/100%/50%/25%).</summary>
    public readonly int Volume;
    /// <summary>Wave channel only: the raw NR32 output level code 0-3.</summary>
    public readonly int WaveLevelCode;
    /// <summary>11-bit period register value (pulse/wave); 0 for noise.</summary>
    public readonly int Period;
    /// <summary>Pulse only: duty 0-3 (12.5/25/50/75%).</summary>
    public readonly int Duty;
    /// <summary>Noise only: NR43 clock shift 0-15, divisor code 0-7, and the 7-bit (short) LFSR mode.</summary>
    public readonly int NoiseShift, NoiseDivisorCode;
    public readonly bool NoiseShortMode;

    public GbApuChannelView(bool enabled, bool dacOn, bool left, bool right, int volume, int waveLevelCode, int period,
        int duty, int noiseShift, int noiseDivisorCode, bool noiseShortMode)
    {
        Enabled = enabled; DacOn = dacOn; Left = left; Right = right; Volume = volume; WaveLevelCode = waveLevelCode;
        Period = period; Duty = duty; NoiseShift = noiseShift; NoiseDivisorCode = noiseDivisorCode; NoiseShortMode = noiseShortMode;
    }
}

/// <summary>
/// Game Boy / Game Boy Color sound: two pulse channels (the first with sweep), the wave channel and noise,
/// clocked on the 4.194304 MHz base clock, with the 512 Hz frame sequencer driven by the board from DIV.
/// Output is box-filtered to 48 kHz stereo and high-passed like the real output capacitor.
/// Written from Pan Docs "Audio", "Audio Registers", "Audio details" and blargg's dmg_sound / cgb_sound notes.
/// </summary>
public sealed class APU_GB
{
    private const int BaseClock = 4194304;
    public int SampleRate => 48000;

    public readonly GbModel Model;
    public readonly byte[] WaveRam = new byte[16];

    // Read-back OR masks for FF10-FF2F.
    private static readonly byte[] ReadMask =
    {
        0x80, 0x3F, 0x00, 0xFF, 0xBF,  0xFF, 0x3F, 0x00, 0xFF, 0xBF,
        0x7F, 0xFF, 0x9F, 0xFF, 0xBF,  0xFF, 0xFF, 0x00, 0x00, 0xBF,
        0x00, 0x00, 0x70,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    };
    // Bit n set = duty step n outputs high.
    private static readonly byte[] DutyMask = { 0x80, 0x81, 0xE1, 0x7E };
    private static readonly int[] LengthMax = { 64, 64, 256, 64 };

    private readonly byte[] regs = new byte[0x20];   // FF10-FF2F as last written
    private bool power;
    private int fsStep;                                // step the next frame sequencer tick executes

    // Per channel (0 pulse1, 1 pulse2, 2 wave, 3 noise)
    private readonly bool[] enabled = new bool[4];
    private readonly bool[] lenEnabled = new bool[4];
    private readonly int[] length = new int[4];        // clocks left; 0 = expired
    private readonly int[] freq = new int[4];          // 11-bit period value (pulse/wave)
    private readonly int[] timer = new int[4];         // T-cycles to the next channel clock
    private readonly int[] volume = new int[4];        // envelope volume (pulse/noise)
    private readonly int[] envTimer = new int[4];
    private readonly int[] envPeriod = new int[4];     // latched at trigger
    private readonly bool[] envUp = new bool[4];
    private readonly int[] dutyPos = new int[2];

    // Sweep (channel 1)
    private int sweepShadow, sweepTimer;
    private bool sweepEnabled, sweepNegUsed;

    // Wave
    private int wavePos;              // index 0-31 of the last sample fetched
    private byte waveBuffer;          // last byte fetched
    private bool waveFetched;         // a fetch happened during the most recent Tick call (DMG access window)

    // Noise
    private int lfsr = 0x7FFF;

    // Mixer / resampler
    private float mixL, mixR;
    private double accL, accR;
    private int accN, sampleLeft = 87, sampleErr;
    private float capL, capR;
    private readonly float hpFactor = (float)Math.Pow(0.999958, BaseClock / 48000.0);
    private readonly short[] ring = new short[1 << 16];
    private int ringHead, ringCount;

    public APU_GB(GbModel model)
    {
        Model = model;
        if (model == GbModel.Cgb)
            for (int i = 0; i < 16; i++) WaveRam[i] = (byte)((i & 1) != 0 ? 0xFF : 0x00);
    }

    private bool Dmg => Model == GbModel.Dmg;

    public void ResetPostBoot()
    {
        WriteRegister(0x26, 0x00);
        WriteRegister(0x26, 0x80);
        WriteRegister(0x10, 0x80);
        WriteRegister(0x11, 0x80);
        WriteRegister(0x12, 0xF3);
        WriteRegister(0x13, 0xC1);
        WriteRegister(0x14, 0x87);
        volume[0] = 0;                // the boot chime has decayed; channel 1 stays on but silent
        WriteRegister(0x21, 0x00);    // noise envelope register (NR42)
        WriteRegister(0x24, 0x77);
        WriteRegister(0x25, 0xF3);
        Remix();
    }

    // ---- Channel views ----

    public GbApuChannelView Channel(int n)
    {
        bool left = (regs[0x15] & (0x10 << n)) != 0, right = (regs[0x15] & (1 << n)) != 0;
        switch (n)
        {
            case 0:
            case 1:
                return new GbApuChannelView(enabled[n], PulseNoiseDac(n), left, right, volume[n], 0, freq[n],
                    regs[n * 5 + 1] >> 6, 0, 0, false);
            case 2:
            {
                int code = (regs[0x0C] >> 5) & 3;
                return new GbApuChannelView(enabled[2], (regs[0x0A] & 0x80) != 0, left, right,
                    code switch { 1 => 15, 2 => 7, 3 => 3, _ => 0 }, code, freq[2], 0, 0, 0, false);
            }
            default:
            {
                byte nr43 = regs[0x12];
                return new GbApuChannelView(enabled[3], PulseNoiseDac(3), left, right, volume[3], 0, 0, 0,
                    nr43 >> 4, nr43 & 7, (nr43 & 8) != 0);
            }
        }
    }

    public int NR50 => regs[0x14];
    public int NR51 => regs[0x15];
    public bool PowerOn => power;

    // ---- Timing ----

    public void Tick(int tCycles)
    {
        waveFetched = false;
        int t = tCycles;
        while (t > 0)
        {
            int n = t < sampleLeft ? t : sampleLeft;
            if (power)
            {
                if (enabled[0] && timer[0] < n) n = timer[0];
                if (enabled[1] && timer[1] < n) n = timer[1];
                if (enabled[2] && timer[2] < n) n = timer[2];
                if (enabled[3] && timer[3] < n) n = timer[3];
            }
            accL += mixL * n; accR += mixR * n; accN += n;
            t -= n; sampleLeft -= n;

            if (power)
            {
                bool dirty = false;
                if (enabled[0] && (timer[0] -= n) == 0)
                {
                    timer[0] = (2048 - freq[0]) * 4;
                    dutyPos[0] = (dutyPos[0] + 1) & 7; dirty = true;
                }
                if (enabled[1] && (timer[1] -= n) == 0)
                {
                    timer[1] = (2048 - freq[1]) * 4;
                    dutyPos[1] = (dutyPos[1] + 1) & 7; dirty = true;
                }
                if (enabled[2] && (timer[2] -= n) == 0)
                {
                    timer[2] = (2048 - freq[2]) * 2;
                    wavePos = (wavePos + 1) & 31;
                    waveBuffer = WaveRam[wavePos >> 1];
                    waveFetched = true; dirty = true;
                }
                if (enabled[3] && (timer[3] -= n) == 0)
                {
                    timer[3] = NoisePeriod();
                    if ((regs[0x12] >> 4) < 14)
                    {
                        int x = (lfsr ^ (lfsr >> 1)) & 1;
                        lfsr = (lfsr >> 1) | (x << 14);
                        if ((regs[0x12] & 8) != 0) lfsr = (lfsr & ~0x40) | (x << 6);
                        dirty = true;
                    }
                }
                if (dirty) Remix();
            }

            if (sampleLeft == 0) EmitSample();
        }
    }

    private int NoisePeriod()
    {
        int code = regs[0x12] & 7;
        return (code == 0 ? 8 : code * 16) << (regs[0x12] >> 4);
    }

    public void FrameSequencerStep()
    {
        if (!power) return;
        int s = fsStep;
        fsStep = (fsStep + 1) & 7;
        if ((s & 1) == 0)
            for (int c = 0; c < 4; c++)
                if (lenEnabled[c] && length[c] > 0 && --length[c] == 0) enabled[c] = false;
        if (s == 2 || s == 6) ClockSweep();
        if (s == 7)
            for (int c = 0; c < 4; c++)
            {
                if (c == 2 || envPeriod[c] == 0) continue;
                if (--envTimer[c] > 0) continue;
                envTimer[c] = envPeriod[c];
                if (envUp[c]) { if (volume[c] < 15) volume[c]++; }
                else if (volume[c] > 0) volume[c]--;
            }
        Remix();
    }

    private void ClockSweep()
    {
        if (--sweepTimer > 0) return;
        int period = (regs[0x00] >> 4) & 7;
        sweepTimer = period == 0 ? 8 : period;
        if (!sweepEnabled || period == 0) return;
        int f = SweepCalc();
        if (f <= 2047 && (regs[0x00] & 7) != 0)
        {
            sweepShadow = f; freq[0] = f;
            SweepCalc();
        }
    }

    private int SweepCalc()
    {
        int d = sweepShadow >> (regs[0x00] & 7);
        int f;
        if ((regs[0x00] & 8) != 0) { f = sweepShadow - d; sweepNegUsed = true; }
        else f = sweepShadow + d;
        if (f > 2047) enabled[0] = false;
        return f;
    }

    // ---- Mixing ----

    private bool PulseNoiseDac(int c) => (regs[c * 5 + 2] & 0xF8) != 0;

    private void Remix()
    {
        float l = 0, r = 0;
        byte nr51 = regs[0x15];
        for (int c = 0; c < 4; c++)
        {
            bool dac = c == 2 ? (regs[0x0A] & 0x80) != 0 : PulseNoiseDac(c);
            if (!dac || !power) continue;
            int d = 0;
            if (enabled[c])
            {
                switch (c)
                {
                    case 0:
                    case 1:
                        d = ((DutyMask[regs[c * 5 + 1] >> 6] >> dutyPos[c]) & 1) * volume[c];
                        break;
                    case 2:
                    {
                        int s = (wavePos & 1) == 0 ? waveBuffer >> 4 : waveBuffer & 15;
                        int code = (regs[0x0C] >> 5) & 3;
                        d = code == 0 ? 0 : s >> (code - 1);
                        break;
                    }
                    default:
                        d = (~lfsr & 1) * volume[3];
                        break;
                }
            }
            float a = 1f - d / 7.5f;
            if ((nr51 & (0x10 << c)) != 0) l += a;
            if ((nr51 & (1 << c)) != 0) r += a;
        }
        byte nr50 = regs[0x14];
        mixL = l * (((nr50 >> 4) & 7) + 1) / 8f;
        mixR = r * ((nr50 & 7) + 1) / 8f;
    }

    private void EmitSample()
    {
        // 4194304 / 48000 = 87 + 18304/48000 T-cycles per sample
        sampleErr += 18304;
        if (sampleErr >= 48000) { sampleErr -= 48000; sampleLeft = 88; } else sampleLeft = 87;

        float inL = (float)(accL / accN), inR = (float)(accR / accN);
        accL = accR = 0; accN = 0;
        float outL = inL - capL, outR = inR - capR;
        capL = inL - outL * hpFactor;
        capR = inR - outR * hpFactor;

        Push(ToShort(outL)); Push(ToShort(outR));
    }

    private static short ToShort(float v)
    {
        int s = (int)(v * 7000f);
        return (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
    }

    private void Push(short s)
    {
        if (ringCount == ring.Length) { ringHead = (ringHead + 1) & (ring.Length - 1); ringCount--; }  // drop oldest
        ring[(ringHead + ringCount) & (ring.Length - 1)] = s;
        ringCount++;
    }

    public int ReadSamples(short[] buffer)
    {
        int n = Math.Min(ringCount, buffer.Length) & ~1;
        for (int i = 0; i < n; i++) buffer[i] = ring[(ringHead + i) & (ring.Length - 1)];
        ringHead = (ringHead + n) & (ring.Length - 1);
        ringCount -= n;
        return n;
    }

    // ---- Registers ----

    public byte ReadRegister(int reg)
    {
        if (reg >= 0x30 && reg <= 0x3F) return ReadWave(reg - 0x30);
        if (reg < 0x10 || reg > 0x2F) return 0xFF;
        if (reg == 0x26)
        {
            int v = 0x70 | (power ? 0x80 : 0);
            for (int c = 0; c < 4; c++) if (enabled[c]) v |= 1 << c;
            return (byte)v;
        }
        return (byte)(regs[reg - 0x10] | ReadMask[reg - 0x10]);
    }

    private byte ReadWave(int i)
    {
        if (!enabled[2]) return WaveRam[i];
        // While playing, the CPU sees the byte the channel is on; the DMG only manages it on the fetch cycle.
        if (Dmg && !waveFetched) return 0xFF;
        return WaveRam[wavePos >> 1];
    }

    private void WriteWave(int i, byte v)
    {
        if (!enabled[2]) { WaveRam[i] = v; return; }
        if (Dmg && !waveFetched) return;
        WaveRam[wavePos >> 1] = v;
    }

    public void WriteRegister(int reg, byte v)
    {
        if (reg >= 0x30 && reg <= 0x3F) { WriteWave(reg - 0x30, v); return; }
        if (reg < 0x10 || reg > 0x2F) return;
        if (reg == 0x26) { WritePower(v); return; }
        if (!power)
        {
            // DMG length counters keep working through power-off; nothing else is writable.
            if (Dmg)
            {
                if (reg == 0x11) length[0] = 64 - (v & 63);
                else if (reg == 0x16) length[1] = 64 - (v & 63);
                else if (reg == 0x1B) length[2] = 256 - v;
                else if (reg == 0x20) length[3] = 64 - (v & 63);
            }
            return;
        }

        int idx = reg - 0x10;
        regs[idx] = v;
        switch (reg)
        {
            case 0x10:
                // Leaving negate mode after a negated calculation kills the channel.
                if (sweepNegUsed && (v & 8) == 0) enabled[0] = false;
                break;
            case 0x11: length[0] = 64 - (v & 63); break;
            case 0x16: length[1] = 64 - (v & 63); break;
            case 0x1B: length[2] = 256 - v; break;
            case 0x20: length[3] = 64 - (v & 63); break;
            case 0x12: if ((v & 0xF8) == 0) enabled[0] = false; break;
            case 0x17: if ((v & 0xF8) == 0) enabled[1] = false; break;
            case 0x21: if ((v & 0xF8) == 0) enabled[3] = false; break;
            case 0x1A: if ((v & 0x80) == 0) enabled[2] = false; break;
            case 0x13: freq[0] = (freq[0] & 0x700) | v; break;
            case 0x18: freq[1] = (freq[1] & 0x700) | v; break;
            case 0x1D: freq[2] = (freq[2] & 0x700) | v; break;
            case 0x14: WriteControl(0, v); break;
            case 0x19: WriteControl(1, v); break;
            case 0x1E: WriteControl(2, v); break;
            case 0x23: WriteControl(3, v); break;
        }
        Remix();
    }

    private void WriteControl(int c, byte v)
    {
        if (c != 3) freq[c] = (freq[c] & 0xFF) | ((v & 7) << 8);
        bool wasLen = lenEnabled[c];
        lenEnabled[c] = (v & 0x40) != 0;
        bool trigger = (v & 0x80) != 0;
        // The next frame sequencer step won't clock length: enabling length now clocks it once extra.
        bool extra = (fsStep & 1) != 0;
        if (extra && !wasLen && lenEnabled[c] && length[c] > 0 && --length[c] == 0 && !trigger) enabled[c] = false;
        if (!trigger) return;

        if (length[c] == 0)
        {
            length[c] = LengthMax[c];
            if (extra && lenEnabled[c]) length[c]--;
        }

        switch (c)
        {
            case 0:
            case 1:
                enabled[c] = PulseNoiseDac(c);
                timer[c] = (2048 - freq[c]) * 4;
                TriggerEnvelope(c);
                if (c == 0)
                {
                    int period = (regs[0x00] >> 4) & 7, shift = regs[0x00] & 7;
                    sweepShadow = freq[0];
                    sweepTimer = period == 0 ? 8 : period;
                    sweepEnabled = period != 0 || shift != 0;
                    sweepNegUsed = false;
                    if (shift != 0) SweepCalc();
                }
                break;
            case 2:
                // DMG: retriggering just as the channel fetches a byte corrupts the start of wave RAM.
                if (Dmg && enabled[2] && timer[2] <= 2)
                {
                    int b = ((wavePos + 1) & 31) >> 1;
                    if (b < 4) WaveRam[0] = WaveRam[b];
                    else Array.Copy(WaveRam, b & ~3, WaveRam, 0, 4);
                }
                enabled[2] = (regs[0x0A] & 0x80) != 0;
                wavePos = 0;
                timer[2] = (2048 - freq[2]) * 2 + 6;   // first fetch is delayed slightly after trigger
                break;
            case 3:
                enabled[3] = PulseNoiseDac(3);
                timer[3] = NoisePeriod();
                lfsr = 0x7FFF;
                TriggerEnvelope(3);
                break;
        }
    }

    private void TriggerEnvelope(int c)
    {
        byte nrx2 = regs[c * 5 + 2];
        volume[c] = nrx2 >> 4;
        envUp[c] = (nrx2 & 8) != 0;
        envPeriod[c] = nrx2 & 7;
        envTimer[c] = envPeriod[c] == 0 ? 8 : envPeriod[c];
    }

    private void WritePower(byte v)
    {
        bool on = (v & 0x80) != 0;
        if (on == power) return;
        if (!on)
        {
            Array.Clear(regs, 0, 0x16);   // FF10-FF25
            for (int c = 0; c < 4; c++)
            {
                enabled[c] = false; lenEnabled[c] = false; freq[c] = 0; volume[c] = 0;
                envTimer[c] = 0; envPeriod[c] = 0; envUp[c] = false;
                if (!Dmg) length[c] = 0;
            }
            dutyPos[0] = dutyPos[1] = 0;
            sweepShadow = sweepTimer = 0; sweepEnabled = sweepNegUsed = false;
            wavePos = 0; waveBuffer = 0;
            power = false;
        }
        else
        {
            power = true;
            fsStep = 0;
        }
        Remix();
    }

    // ---- State ----

    public void SaveState(BinaryWriter w)
    {
        w.Write(regs); w.Write(WaveRam);
        w.Write(power); w.Write(fsStep);
        for (int c = 0; c < 4; c++)
        {
            w.Write(enabled[c]); w.Write(lenEnabled[c]); w.Write(length[c]); w.Write(freq[c]); w.Write(timer[c]);
            w.Write(volume[c]); w.Write(envTimer[c]); w.Write(envPeriod[c]); w.Write(envUp[c]);
        }
        w.Write(dutyPos[0]); w.Write(dutyPos[1]);
        w.Write(sweepShadow); w.Write(sweepTimer); w.Write(sweepEnabled); w.Write(sweepNegUsed);
        w.Write(wavePos); w.Write(waveBuffer); w.Write(lfsr);
        w.Write(sampleLeft); w.Write(sampleErr); w.Write(capL); w.Write(capR);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(regs, 0, regs.Length); r.Read(WaveRam, 0, WaveRam.Length);
        power = r.ReadBoolean(); fsStep = r.ReadInt32();
        for (int c = 0; c < 4; c++)
        {
            enabled[c] = r.ReadBoolean(); lenEnabled[c] = r.ReadBoolean(); length[c] = r.ReadInt32(); freq[c] = r.ReadInt32();
            timer[c] = r.ReadInt32(); volume[c] = r.ReadInt32(); envTimer[c] = r.ReadInt32(); envPeriod[c] = r.ReadInt32();
            envUp[c] = r.ReadBoolean();
        }
        dutyPos[0] = r.ReadInt32(); dutyPos[1] = r.ReadInt32();
        sweepShadow = r.ReadInt32(); sweepTimer = r.ReadInt32(); sweepEnabled = r.ReadBoolean(); sweepNegUsed = r.ReadBoolean();
        wavePos = r.ReadInt32(); waveBuffer = r.ReadByte(); lfsr = r.ReadInt32();
        sampleLeft = r.ReadInt32(); sampleErr = r.ReadInt32(); capL = r.ReadSingle(); capR = r.ReadSingle();
        accL = accR = 0; accN = 0; waveFetched = false;
        if (sampleLeft <= 0) sampleLeft = 87;
        Remix();
    }
}
