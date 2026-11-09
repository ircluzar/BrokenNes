// DOSBox 0.72's Tandy sound (the SN76496 of the Tandy 1000 / PCjr, from MAME's sn76496.c), ported line by line to C# for BrokenNes as an "art instrument".
//
// Source:   DOSBox 0.72, SVN tags/RELEASE_0_72, src/hardware/tandy_sound.cpp ("Based of sn76496.c of the M.A.M.E. project")
//           https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_72/src/hardware/tandy_sound.cpp
// Authors:  The DOSBox Team (Copyright (C) 2002-2007); sn76496.c by Nicola Salmoria and the MAME team; noise feedback by John Kortink, "JH 981127"
// Licence:  GPL-2.0-or-later - documented exception, see THIRD_PARTY_NOTICES.md section 2a.
//
// The card as DOSBox 0.72 builds it on a non-Tandy machine with tandy=true: the SN76496 at C0h-C1h and 1E0h-1E1h, the "DAC" ports C4h-C7h only logged
// (0.72 has no Tandy DAC), the second DMA controller CLOSED (its ports clash with C0h), clocked at 3579545 Hz, tandyrate 22050 into the 0.72 mixer
// (22050 Hz, DosboxMixer in PcCards.cs), held onto the console's 44.1 kHz. (The BIOS byte 40:D4h DOSBox writes is not set.)
//
// Faithful port: the flaws are KEPT, not fixed. Characteristic quirks:
//  - The periodic-noise feedback is the 15-bit FB_PNOISE 0x08000 (the "JH 981127 - fixes Do Run Run" value the file's own comment calls definitely
//    wrong): periodic noise is out of tune.
//  - The noise shifter is NOT reset on a noise-register write (the reset lines are commented out).
//  - The output is unipolar (0..32767, a DC offset): vol[] counts only the time each square is high, summed unsigned and clipped at 7FFFh.
//  - Volume table at gain 1: MAX_OUTPUT/3 per voice, the 2 dB steps; voices at volume 0 keep counting (the "count += length" trick).
//  - The channel switches itself OFF 5000 ms after the last write (and back on at the next): a held note stops after 5 s, the chip frozen meanwhile.
//  - Period 0 becomes 0x3fe; noise mode 3 follows tone 2 at 2x its period, updated only when tone 2's register is written.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class Dosbox072Tandy : IPcSoundCard, IPcSoundOutput
{
    public string Id => "TANDY_D072";
    private const int MAX_OUTPUT = 0x7fff, STEP = 0x10000, FB_WNOISE = 0x14002, FB_PNOISE = 0x08000, NG_PRESET = 0x0f35;
    private const int SampleRate = 22050;

    private readonly PcMachine _m;
    private readonly DosboxMixer _mix;
    private readonly DosboxMixer.Chan _chan;
    public long IrqsRaised => 0;

    // struct SN76496
    private uint UpdateStep;
    private readonly int[] VolTable = new int[16], Register = new int[8], Volume = new int[4], Period = new int[4], Count = new int[4], Output = new int[4];
    private int LastRegister;
    private uint RNG;
    private int NoiseFB;
    private bool enabled;
    private double last_write;   // PIC_Ticks (ms)
    private readonly short[] _buf = new short[4096];

    public Dosbox072Tandy(PcMachine m)
    {
        _m = m;
        _mix = new DosboxMixer(() => m.PitNow, 22050);
        _chan = _mix.AddChannel(SN76496Update, SampleRate);
        Reset();
    }

    public void Reset()
    {
        _mix.Restart(true);
        _chan.Enabled = false;
        enabled = false;
        last_write = 0;
        UpdateStep = (uint)(((double)STEP * SampleRate * 16) / 3579545);
        for (int i = 0; i < 4; i++) Volume[i] = 0;
        LastRegister = 0;
        for (int i = 0; i < 8; i += 2) { Register[i] = 0; Register[i + 1] = 0x0f; }
        for (int i = 0; i < 4; i++) { Output[i] = 0; Period[i] = Count[i] = (int)UpdateStep; }
        RNG = NG_PRESET;
        Output[3] = (int)(RNG & 1);
        NoiseFB = 0;
        SN76496_set_gain(0x1);
    }

    private double PicTicks => Math.Floor(_mix.Now * 1000.0 / Pit8254.Freq);

    private void SN76496Write(int data)
    {
        last_write = PicTicks;
        if (!enabled) { _chan.Enable(true); enabled = true; }
        if ((data & 0x80) != 0)
        {
            int r = (data & 0x70) >> 4;
            int c = r / 2;
            LastRegister = r;
            Register[r] = (Register[r] & 0x3f0) | (data & 0x0f);
            switch (r)
            {
                case 0: case 2: case 4:
                    Period[c] = (int)(UpdateStep * (uint)Register[r]);
                    if (Period[c] == 0) Period[c] = 0x3fe;
                    if (r == 4 && (Register[6] & 0x03) == 0x03) Period[3] = 2 * Period[2];
                    break;
                case 1: case 3: case 5: case 7:
                    Volume[c] = VolTable[data & 0x0f];
                    break;
                case 6:
                {
                    int n = Register[6];
                    NoiseFB = (n & 4) != 0 ? FB_WNOISE : FB_PNOISE;
                    n &= 3;
                    Period[3] = n == 3 ? 2 * Period[2] : (int)(UpdateStep << (5 + n));
                    break;
                }
            }
        }
        else
        {
            int r = LastRegister;
            int c = r / 2;
            switch (r)
            {
                case 0: case 2: case 4:
                    Register[r] = (Register[r] & 0x0f) | ((data & 0x3f) << 4);
                    Period[c] = (int)(UpdateStep * (uint)Register[r]);
                    if (Period[c] == 0) Period[c] = 0x3fe;
                    if (r == 4 && (Register[6] & 0x03) == 0x03) Period[3] = 2 * Period[2];
                    break;
            }
        }
    }

    private void SN76496Update(int length)
    {
        if (last_write + 5000 < PicTicks) { enabled = false; _chan.Enable(false); }
        length = Math.Min(length, _buf.Length);
        for (int i = 0; i < 4; i++)
            if (Volume[i] == 0 && Count[i] <= length * STEP) Count[i] += length * STEP;
        int count = length, o = 0;
        Span<int> vol = stackalloc int[4];
        while (count != 0)
        {
            vol[0] = vol[1] = vol[2] = vol[3] = 0;
            for (int i = 0; i < 3; i++)
            {
                if (Output[i] != 0) vol[i] += Count[i];
                Count[i] -= STEP;
                while (Count[i] <= 0)
                {
                    Count[i] += Period[i];
                    if (Count[i] > 0)
                    {
                        Output[i] ^= 1;
                        if (Output[i] != 0) vol[i] += Period[i];
                        break;
                    }
                    Count[i] += Period[i];
                    vol[i] += Period[i];
                }
                if (Output[i] != 0) vol[i] -= Count[i];
            }
            int left = STEP;
            do
            {
                int nextevent = Count[3] < left ? Count[3] : left;
                if (Output[3] != 0) vol[3] += Count[3];
                Count[3] -= nextevent;
                if (Count[3] <= 0)
                {
                    if ((RNG & 1) != 0) RNG ^= (uint)NoiseFB;
                    RNG >>= 1;
                    Output[3] = (int)(RNG & 1);
                    Count[3] += Period[3];
                    if (Output[3] != 0) vol[3] += Period[3];
                }
                if (Output[3] != 0) vol[3] -= Count[3];
                left -= nextevent;
            } while (left > 0);
            uint outv = (uint)(vol[0] * Volume[0] + vol[1] * Volume[1] + vol[2] * Volume[2] + vol[3] * Volume[3]);
            if (outv > (uint)MAX_OUTPUT * STEP) outv = (uint)MAX_OUTPUT * STEP;
            _buf[o++] = (short)(outv / STEP);
            count--;
        }
        _chan.AddMono(length, k => _buf[k]);
    }

    private void SN76496_set_gain(int gain)
    {
        gain &= 0xff;
        double outv = MAX_OUTPUT / 3;
        while (gain-- > 0) outv *= 1.023292992;
        for (int i = 0; i < 15; i++)
        {
            VolTable[i] = outv > MAX_OUTPUT / 3 ? MAX_OUTPUT / 3 : (int)outv;
            outv /= 1.258925412;
        }
        VolTable[15] = 0;
    }

    // ================= IPcSoundCard =================

    public bool In(int port, bool word, out int value) { value = 0xff; return false; }   // write-only ports

    public bool Out(int port, bool word, int value)
    {
        if (word) return false;
        if (port is 0xc0 or 0xc1 or 0x1e0 or 0x1e1) { Advance(); SN76496Write(value & 0xff); return true; }
        if (port is >= 0xc4 and <= 0xc7) return true;   // TandyDACWrite: logged only in 0.72
        return false;
    }

    public void Advance() => _mix.Advance();
    public int MixInto(short[] buffer, int count) => _mix.MixInto(buffer, count);
    public int Pending => _mix.Pending;
    public void Restart() => _mix.Restart();
    public void SaveOutput(BinaryWriter w) => _mix.SaveOutput(w);
    public void LoadOutput(BinaryReader r) => _mix.LoadOutput(r);

    public void Save(BinaryWriter w)
    {
        w.Write(1);
        _mix.Save(w);
        foreach (var a in new[] { Register, Volume, Period, Count, Output }) foreach (var v in a) w.Write(v);
        w.Write(LastRegister); w.Write(RNG); w.Write(NoiseFB); w.Write(enabled); w.Write(last_write);
    }

    public void Load(BinaryReader r)
    {
        if (r.ReadInt32() != 1) throw new InvalidDataException("Tandy state version");
        _mix.Load(r);
        foreach (var a in new[] { Register, Volume, Period, Count, Output }) for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt32();
        LastRegister = r.ReadInt32(); RNG = r.ReadUInt32(); NoiseFB = r.ReadInt32(); enabled = r.ReadBoolean(); last_write = r.ReadDouble();
    }
}
