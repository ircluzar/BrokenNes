/*
 * MinigbApu - minigb_apu (Peanut-GB's SDL2 example sound, based on Alex Baines's MiniGBS) ported to C#, BEFORE its April 2022 rework.
 * Source: github.com/deltabeard/Peanut-GB commit 024175d (2022-03-31), examples/sdl2/minigb_apu/minigb_apu.c + minigb_apu.h.
 * URL: https://github.com/deltabeard/Peanut-GB/tree/024175d/examples/sdl2/minigb_apu
 * Authors: Mahyar Koshkouei (deltabeard) and contributors; MiniGBS by Alex Baines. License: MIT (minigb_apu's LICENSE file, as the file's
 * header says; the commit tree itself carries no LICENSE file at that path). Listed in THIRD_PARTY_NOTICES.md section 2a.
 * Sources as fetched (sha256): C:\BrokenNes-tools\apu-harvest\minigb_apu\MANIFEST.sha256.
 *
 * Characteristic flaws kept on purpose (art instrument; do not fix):
 *  - Everything is float and per-sample: no frame sequencer. Length, envelope and sweep are float counters stepped once per 48 kHz
 *    output sample; register writes are only heard at the next audio callback (one block of 803 stereo samples = one GB frame).
 *  - Each channel has its own high-pass "capacitor" (x - c; c = x - out*0.996), much stronger than real hardware: bass thins out.
 *  - Wave channel: the sample is offset by a per-volume diff (7.5, 3.75, 1.5) and scaled by 1/7.5, then high-passed; volume 0
 *    skips the channel entirely (the capacitor freezes). The wave position restarts at 0 on trigger and the first sample is read
 *    before the step (late by one); the old wave clipping bug stays.
 *  - Noise: disables itself when the clock shift is >= 14; the LFSR is 16-bit, shifted LEFT with the current output fed in, taps
 *    14^13 (wide) or 6^5 (narrow); it starts at 0xFFFF and the output starts at -1 (the "noise starts late" bug).
 *  - Sweep: the shadow frequency is captured once at trigger and never updated, so each step adds/subtracts the SAME delta (a
 *    linear glide instead of an exponential one); its counter starts just above 1.0, so the first sweep step lands on the first
 *    sample; the pitch after a sweep step uses an integer division (coarser than the float used elsewhere).
 *  - Envelope arithmetic is on a byte: a falling envelope that starts at volume 0 wraps to 255 and is clamped to 15 (a click up to
 *    full volume, then a fade). "Zombie mode" NRx2 writes: +1, +2 or 16-volume.
 *  - NR52 writes are just stored (no power-off, registers stay writable: the "register R/W while off" bug); the channel status
 *    bits are rewritten whenever a channel is enabled or disabled.
 *  - Pitches go through an integer Hz (set_note_freq takes a uint_fast16_t): high notes are detuned.
 *  - Duty patterns 0x10, 0x30, 0x3C, 0xCF (bit-mapped, stepping LSB first); square amplitude +-1 times volume/15, everything x0.25
 *    per channel times NR50 (0..7)/7 per side.
 */
using System;
using System.IO;

namespace NesEmulator.Gb;

public sealed class MinigbApu
{
    public const float AudioSampleRate = 48000.0f;
    public const int SamplesPerCallback = (int)(48000.0 / (4194304.0 / 70224.0));   // AUDIO_SAMPLES = 803
    private const int AudioNSamples = SamplesPerCallback * 2;
    private const int AddrComp = 0xFF10;

    public readonly byte[] Mem = new byte[0xFF3F - 0xFF10 + 1];

    public sealed class Chan
    {
        public bool Enabled, Powered, OnLeft, OnRight, Muted;
        public byte Volume, VolumeInit;
        public ushort Freq; public float FreqCounter, FreqInc;
        public sbyte Val;
        public byte LenLoad; public bool LenEnabled; public float LenCounter, LenInc;
        public byte EnvStep; public bool EnvUp; public float EnvCounter, EnvInc;
        public ushort SweepFreq; public byte SweepRate, SweepShift; public bool SweepUp; public float SweepCounter, SweepInc;
        public byte Duty, DutyCounter;
        public ushort LfsrReg; public byte LfsrWide, LfsrDiv;
        public byte Sample;
        public float Capacitor;

        public void Save(BinaryWriter w)
        {
            w.Write(Enabled); w.Write(Powered); w.Write(OnLeft); w.Write(OnRight); w.Write(Muted); w.Write(Volume); w.Write(VolumeInit);
            w.Write(Freq); w.Write(FreqCounter); w.Write(FreqInc); w.Write(Val);
            w.Write(LenLoad); w.Write(LenEnabled); w.Write(LenCounter); w.Write(LenInc);
            w.Write(EnvStep); w.Write(EnvUp); w.Write(EnvCounter); w.Write(EnvInc);
            w.Write(SweepFreq); w.Write(SweepRate); w.Write(SweepShift); w.Write(SweepUp); w.Write(SweepCounter); w.Write(SweepInc);
            w.Write(Duty); w.Write(DutyCounter); w.Write(LfsrReg); w.Write(LfsrWide); w.Write(LfsrDiv); w.Write(Sample); w.Write(Capacitor);
        }

        public void Load(BinaryReader r)
        {
            Enabled = r.ReadBoolean(); Powered = r.ReadBoolean(); OnLeft = r.ReadBoolean(); OnRight = r.ReadBoolean(); Muted = r.ReadBoolean(); Volume = r.ReadByte(); VolumeInit = r.ReadByte();
            Freq = r.ReadUInt16(); FreqCounter = r.ReadSingle(); FreqInc = r.ReadSingle(); Val = r.ReadSByte();
            LenLoad = r.ReadByte(); LenEnabled = r.ReadBoolean(); LenCounter = r.ReadSingle(); LenInc = r.ReadSingle();
            EnvStep = r.ReadByte(); EnvUp = r.ReadBoolean(); EnvCounter = r.ReadSingle(); EnvInc = r.ReadSingle();
            SweepFreq = r.ReadUInt16(); SweepRate = r.ReadByte(); SweepShift = r.ReadByte(); SweepUp = r.ReadBoolean(); SweepCounter = r.ReadSingle(); SweepInc = r.ReadSingle();
            Duty = r.ReadByte(); DutyCounter = r.ReadByte(); LfsrReg = r.ReadUInt16(); LfsrWide = r.ReadByte(); LfsrDiv = r.ReadByte(); Sample = r.ReadByte(); Capacitor = r.ReadSingle();
        }
    }

    public readonly Chan[] Chans = { new(), new(), new(), new() };
    private float volL, volR;
    private readonly float[] samples = new float[AudioNSamples];

    public MinigbApu() { Init(); }

    private static float Hipass(Chan c, float sample)
    {
        float output = sample - c.Capacitor;
        c.Capacitor = sample - output * 0.996f;
        return output;
    }

    private static void SetNoteFreq(Chan c, ulong freq) => c.FreqInc = (float)(freq / 48000.0);   // freq is a uint_fast16_t: pitches truncate to whole Hz

    private void ChanEnable(int i, bool enable)
    {
        Chans[i].Enabled = enable;
        Mem[0xFF26 - AddrComp] = (byte)((Mem[0xFF26 - AddrComp] & 0x80) | ((Chans[3].Enabled ? 1 : 0) << 3) | ((Chans[2].Enabled ? 1 : 0) << 2)
            | ((Chans[1].Enabled ? 1 : 0) << 1) | (Chans[0].Enabled ? 1 : 0));
    }

    private static void UpdateEnv(Chan c)
    {
        c.EnvCounter += c.EnvInc;
        while (c.EnvCounter > 1.0f)
        {
            if (c.EnvStep != 0)
            {
                c.Volume = (byte)(c.Volume + (c.EnvUp ? 1 : -1));
                if (c.Volume == 0 || c.Volume == 15) c.EnvInc = 0;
                c.Volume = Math.Max((byte)0, Math.Min((byte)15, c.Volume));
            }
            c.EnvCounter -= 1.0f;
        }
    }

    private void UpdateLen(Chan c, int index)
    {
        if (c.LenEnabled)
        {
            c.LenCounter += c.LenInc;
            if (c.LenCounter > 1.0f) { ChanEnable(index, false); c.LenCounter = 0.0f; }
        }
    }

    private static bool UpdateFreq(Chan c, ref float pos)
    {
        float inc = c.FreqInc - pos;
        c.FreqCounter += inc;
        if (c.FreqCounter > 1.0f) { pos = c.FreqInc - (c.FreqCounter - 1.0f); c.FreqCounter = 0.0f; return true; }
        pos = c.FreqInc;
        return false;
    }

    private static void UpdateSweep(Chan c)
    {
        c.SweepCounter += c.SweepInc;
        while (c.SweepCounter > 1.0f)
        {
            if (c.SweepShift != 0)
            {
                ushort inc = (ushort)(c.SweepFreq >> c.SweepShift);
                if (!c.SweepUp) inc = (ushort)(inc * -1);
                c.Freq = (ushort)(c.Freq + inc);
                if (c.Freq > 2047) c.Enabled = false;
                else { SetNoteFreq(c, (ulong)(4194304 / ((2048 - c.Freq) << 5))); c.FreqInc *= 8.0f; }
            }
            else if (c.SweepRate != 0) c.Enabled = false;
            c.SweepCounter -= 1.0f;
        }
    }

    private void UpdateSquare(bool ch2)
    {
        var c = Chans[ch2 ? 1 : 0];
        if (!c.Powered) return;
        SetNoteFreq(c, (ulong)(4194304.0f / ((2048 - c.Freq) << 5)));
        c.FreqInc *= 8.0f;
        for (int i = 0; i < AudioNSamples; i += 2)
        {
            UpdateLen(c, ch2 ? 1 : 0);
            if (!c.Enabled) continue;
            UpdateEnv(c);
            if (!ch2) UpdateSweep(c);
            float pos = 0.0f, prevPos = 0.0f, sample = 0.0f;
            while (UpdateFreq(c, ref pos))
            {
                c.DutyCounter = (byte)((c.DutyCounter + 1) & 7);
                sample += ((pos - prevPos) / c.FreqInc) * c.Val;
                c.Val = (sbyte)((c.Duty & (1 << c.DutyCounter)) != 0 ? 1 : -1);
                prevPos = pos;
            }
            sample += ((pos - prevPos) / c.FreqInc) * c.Val;
            sample = Hipass(c, sample * (c.Volume / 15.0f));
            if (!c.Muted)
            {
                samples[i + 0] += sample * 0.25f * (c.OnLeft ? 1 : 0) * volL;
                samples[i + 1] += sample * 0.25f * (c.OnRight ? 1 : 0) * volR;
            }
        }
    }

    private byte WaveSample(int pos, int volume)
    {
        byte sample = Mem[(0xFF30 + pos / 2) - AddrComp];
        if ((pos & 1) != 0) sample &= 0xF; else sample >>= 4;
        return volume != 0 ? (byte)(sample >> (volume - 1)) : (byte)0;
    }

    private static readonly float[] WaveDiff = { 7.5f, 3.75f, 1.5f };

    private void UpdateWave()
    {
        var c = Chans[2];
        if (!c.Powered) return;
        uint freq = (uint)(4194304.0f / ((2048 - c.Freq) << 5));
        SetNoteFreq(c, freq);
        c.FreqInc *= 16.0f;
        for (int i = 0; i < AudioNSamples; i += 2)
        {
            UpdateLen(c, 2);
            if (!c.Enabled) continue;
            float pos = 0.0f, prevPos = 0.0f, sample = 0.0f;
            c.Sample = WaveSample(c.Val, c.Volume);
            while (UpdateFreq(c, ref pos))
            {
                c.Val = (sbyte)((c.Val + 1) & 31);
                sample += ((pos - prevPos) / c.FreqInc) * c.Sample;
                c.Sample = WaveSample(c.Val, c.Volume);
                prevPos = pos;
            }
            sample += ((pos - prevPos) / c.FreqInc) * c.Sample;
            if (c.Volume > 0)
            {
                float diff = WaveDiff[c.Volume - 1];
                sample = Hipass(c, (sample - diff) / 7.5f);
                if (!c.Muted)
                {
                    samples[i + 0] += sample * 0.25f * (c.OnLeft ? 1 : 0) * volL;
                    samples[i + 1] += sample * 0.25f * (c.OnRight ? 1 : 0) * volR;
                }
            }
        }
    }

    private static readonly byte[] NoiseDiv = { 8, 16, 32, 48, 64, 80, 96, 112 };

    private void UpdateNoise()
    {
        var c = Chans[3];
        if (!c.Powered) return;
        uint freq = (uint)(4194304 / (NoiseDiv[c.LfsrDiv] << c.Freq));
        SetNoteFreq(c, freq);
        if (c.Freq >= 14) c.Enabled = false;
        for (int i = 0; i < AudioNSamples; i += 2)
        {
            UpdateLen(c, 3);
            if (!c.Enabled) continue;
            UpdateEnv(c);
            float pos = 0.0f, prevPos = 0.0f, sample = 0.0f;
            while (UpdateFreq(c, ref pos))
            {
                c.LfsrReg = (ushort)((c.LfsrReg << 1) | (c.Val == 1 ? 1 : 0));
                if (c.LfsrWide != 0) c.Val = (sbyte)((((c.LfsrReg >> 14) & 1) ^ ((c.LfsrReg >> 13) & 1)) == 0 ? 1 : -1);
                else c.Val = (sbyte)((((c.LfsrReg >> 6) & 1) ^ ((c.LfsrReg >> 5) & 1)) == 0 ? 1 : -1);
                sample += ((pos - prevPos) / c.FreqInc) * c.Val;
                prevPos = pos;
            }
            sample += ((pos - prevPos) / c.FreqInc) * c.Val;
            sample = Hipass(c, sample * (c.Volume / 15.0f));
            if (!c.Muted)
            {
                samples[i + 0] += sample * 0.25f * (c.OnLeft ? 1 : 0) * volL;
                samples[i + 1] += sample * 0.25f * (c.OnRight ? 1 : 0) * volR;
            }
        }
    }

    /// <summary>audio_callback: one block of <see cref="SamplesPerCallback"/> stereo samples, interleaved L/R floats.</summary>
    public float[] Callback()
    {
        Array.Clear(samples);
        UpdateSquare(false);
        UpdateSquare(true);
        UpdateWave();
        UpdateNoise();
        return samples;
    }

    private void ChanTrigger(int i)
    {
        var c = Chans[i];
        ChanEnable(i, true);
        c.Volume = c.VolumeInit;
        {
            byte val = Mem[(0xFF12 + (i * 5)) - AddrComp];
            c.EnvStep = (byte)(val & 0x07);
            c.EnvUp = (val & 0x08) != 0;
            c.EnvInc = c.EnvStep != 0 ? (64.0f / c.EnvStep) / AudioSampleRate : 8.0f / AudioSampleRate;
            c.EnvCounter = 0.0f;
        }
        if (i == 0)
        {
            byte val = Mem[0xFF10 - AddrComp];
            c.SweepFreq = c.Freq;
            c.SweepRate = (byte)((val >> 4) & 0x07);
            c.SweepUp = (val & 0x08) == 0;
            c.SweepShift = (byte)(val & 0x07);
            c.SweepInc = c.SweepRate != 0 ? (128.0f / c.SweepRate) / AudioSampleRate : 0;
            c.SweepCounter = MathF.BitIncrement(1.0f);   // nexttowardf(1.0f, 1.1f)
        }
        int lenMax = 64;
        if (i == 2) { lenMax = 256; c.Val = 0; }
        else if (i == 3) { c.LfsrReg = 0xFFFF; c.Val = -1; }
        c.LenInc = (256.0f / (lenMax - c.LenLoad)) / AudioSampleRate;
        c.LenCounter = 0.0f;
    }

    private static readonly byte[] OrTab = { 0x80, 0x3f, 0x00, 0xff, 0xbf, 0xff, 0x3f, 0x00, 0xff, 0xbf, 0x7f, 0xff, 0x9f, 0xff, 0xbf, 0xff, 0xff, 0x00, 0x00, 0xbf, 0x00, 0x00, 0x70 };

    public byte Read(ushort addr)
    {
        if (addr > 0xFF26) return Mem[addr - AddrComp];
        return (byte)(Mem[addr - AddrComp] | OrTab[addr - 0xFF10]);
    }

    private static readonly byte[] DutyLookup = { 0x10, 0x30, 0x3C, 0xCF };

    public void Write(ushort addr, byte val)
    {
        int i = (addr - 0xFF10) / 5;
        Mem[addr - AddrComp] = val;
        switch (addr)
        {
            case 0xFF12: case 0xFF17: case 0xFF21:
            {
                var c = Chans[i];
                c.VolumeInit = (byte)(val >> 4);
                c.Powered = (val >> 3) != 0;
                if (c.Powered && c.Enabled)
                {
                    if (c.EnvStep == 0 && c.EnvInc != 0) { if ((val & 0x08) != 0) c.Volume++; else c.Volume += 2; }
                    else c.Volume = (byte)(16 - c.Volume);
                    c.Volume &= 0x0F;
                    c.EnvStep = (byte)(val & 0x07);
                }
                break;
            }
            case 0xFF1C: Chans[i].Volume = Chans[i].VolumeInit = (byte)((val >> 5) & 0x03); break;
            case 0xFF11: case 0xFF16: case 0xFF20:
                Chans[i].LenLoad = (byte)(val & 0x3f);
                Chans[i].Duty = DutyLookup[val >> 6];
                break;
            case 0xFF1B: Chans[i].LenLoad = val; break;
            case 0xFF13: case 0xFF18: case 0xFF1D:
                Chans[i].Freq &= 0xFF00; Chans[i].Freq |= val;
                break;
            case 0xFF1A:
                Chans[i].Powered = (val & 0x80) != 0;
                ChanEnable(i, (val & 0x80) != 0);
                break;
            case 0xFF14: case 0xFF19: case 0xFF1E:
                Chans[i].Freq &= 0x00FF; Chans[i].Freq |= (ushort)((val & 0x07) << 8);
                goto case 0xFF23;
            case 0xFF23:
                Chans[i].LenEnabled = (val & 0x40) != 0;
                if ((val & 0x80) != 0) ChanTrigger(i);
                break;
            case 0xFF22:
                Chans[3].Freq = (ushort)(val >> 4);
                Chans[3].LfsrWide = (byte)((val & 0x08) == 0 ? 1 : 0);
                Chans[3].LfsrDiv = (byte)(val & 0x07);
                break;
            case 0xFF24:
                volL = ((val >> 4) & 0x07) / 7.0f;
                volR = (val & 0x07) / 7.0f;
                break;
            case 0xFF25:
                for (int k = 0; k < 4; ++k) { Chans[k].OnLeft = ((val >> (4 + k)) & 1) != 0; Chans[k].OnRight = ((val >> k) & 1) != 0; }
                break;
        }
    }

    public void Init()
    {
        for (int k = 0; k < 4; k++) Chans[k] = new Chan();
        Chans[0].Val = Chans[1].Val = -1;
        Array.Clear(Mem);
        volL = volR = 0;
        byte[] regsInit = { 0x80, 0xBF, 0xF3, 0xFF, 0x3F, 0xFF, 0x3F, 0x00, 0xFF, 0x3F, 0x7F, 0xFF, 0x9F, 0xFF, 0x3F, 0xFF, 0xFF, 0x00, 0x00, 0x3F, 0x77, 0xF3, 0xF1 };
        for (int k = 0; k < regsInit.Length; ++k) Write((ushort)(0xFF10 + k), regsInit[k]);
        byte[] waveInit = { 0xac, 0xdd, 0xda, 0x48, 0x36, 0x02, 0xcf, 0x16, 0x2c, 0x04, 0xe5, 0x2c, 0xac, 0xdd, 0xda, 0x48 };
        for (int k = 0; k < waveInit.Length; ++k) Write((ushort)(0xFF30 + k), waveInit[k]);
    }

    public void Save(BinaryWriter w)
    {
        w.Write(Mem); w.Write(volL); w.Write(volR);
        foreach (var c in Chans) c.Save(w);
    }

    public void Load(BinaryReader r)
    {
        r.Read(Mem, 0, Mem.Length); volL = r.ReadSingle(); volR = r.ReadSingle();
        foreach (var c in Chans) c.Load(r);
    }

    /// <summary>NR50 as stored by the original (for rebuilding from a foreign state).</summary>
    public void RestoreVolumes() { Write(0xFF24, Mem[0xFF24 - AddrComp]); Write(0xFF25, Mem[0xFF25 - AddrComp]); }
}
