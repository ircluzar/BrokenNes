// DOSBox 0.74's Gravis Ultrasound (the GF1: 32 voices, volume ramps, DRAM by poke or DMA, the timers), ported line by line to C# for BrokenNes as an "art instrument".
//
// Source:   DOSBox 0.74, SVN tags/RELEASE_0_74, src/hardware/gus.cpp ($Id: gus.cpp,v 1.37 2009-09-03 c2woody)
//           https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_74/src/hardware/gus.cpp
// Authors:  The DOSBox Team (Copyright (C) 2002-2010)
// Licence:  GPL-2.0-or-later - documented exception, see THIRD_PARTY_NOTICES.md section 2a.
//
// The card as DOSBox 0.74 builds it with its defaults: gusbase 240h (portbase 40h: ports 240h, 246h, 248h, 249h, 24Ah, 24Bh, 342h-347h), gusirq 5,
// gusdma 3, gusrate 44100 into the 0.74 mixer at its default 44100 Hz (DosboxMixer in PcCards.cs; equal rates, so the card's samples pass through),
// 1 MB of DRAM. No firmware is needed for the card itself, but DOS programs need SET ULTRASND=240,3,3,5,5 (and most need ULTRADIR with the GUS
// patches, which are NOT shipped) to find and use it.
// Not ported: the MIDI and joystick parts (left to other devices in DOSBox too), the AdLib-compatible timer's command latch beyond what gus.cpp has.
//
// Faithful port: the flaws are KEPT, not fixed. Characteristic quirks of 0.74:
//  - MONO: the card's voices are panned and mixed in stereo, then summed to one channel here (the user's pick for this card: "DOSBox 0.74 GUS, mono,
//    its known bug"); left + right are averaged into both outputs.
//  - AutoAmp: the output gain starts at 512/512 and drops by one step on every clipped sample, never to come back up (a loud song gets quieter for good).
//  - Interpolation is skipped whenever the voice steps >= 1 sample per output sample (Delta test), so high notes alias.
//  - Voice and ramp IRQ flags are checked once per mixer callback (1 ms), not per sample; the active voice count clamps to 14..32 and sets the
//    rate the voices step at (1 MHz / (1.619695497 * voices)), as the real GF1 does.
//  - Register 0Eh also overwrites the register select ("JAZZ Jackrabbit seems to assume this?").
//  - DMA uploads happen all at once when the channel is unmasked (no DMA timing), the 16-bit-DMA address translation of a real GUS is not done.
//  - 16-bit sample addresses use the GF1 bank formula; the GUS timers are PIC events of 80 us / 320 us steps and only raise IRQ status if enabled.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class Dosbox074Gus : IPcSoundCard
{
    public string Id => "GUS_D074";

    private const int WAVE_BITS = 2, WAVE_FRACT = 9 + WAVE_BITS, WAVE_FRACT_MASK = (1 << WAVE_FRACT) - 1;
    private const uint WAVE_MSWMASK = (1u << (16 + WAVE_BITS)) - 1, WAVE_LSWMASK = 0xffffffff ^ WAVE_MSWMASK;
    private const int RAMP_FRACT = 10;
    private const int EV_TIMER = 1;

    private readonly PcMachine _m;
    private readonly DosboxMixer _mix;
    private readonly DosboxMixer.Chan _gusChan;
    public long IrqsRaised { get; private set; }

    private static readonly byte[] irqtable = { 0, 2, 5, 3, 7, 11, 12, 15 };
    private static readonly byte[] dmatable = { 0, 1, 3, 5, 6, 7, 0, 0 };
    private readonly byte[] GUSRam = new byte[1024 * 1024 + 4];
    private int AutoAmp = 512;
    private static readonly ushort[] vol16bit = new ushort[4096];
    private static readonly uint[] pantable = new uint[16];
    private byte adlib_commandreg;

    // struct GFGus
    private byte gRegSelect;
    private ushort gRegData;
    private uint gDramAddr;
    private ushort gCurChannel;
    private byte DMAControl;
    private ushort dmaAddr;
    private byte TimerControl, SampControl, mixControl, ActiveChannels;
    private uint basefreq;
    private struct GusTimer { public byte value; public bool reached, raiseirq, masked, running; public float delay; }
    private readonly GusTimer[] timers = new GusTimer[2];
    private const uint rate = 44100;
    private const int portbase = 0x40;
    private byte dma1, dma2, irq1, irq2;
    private bool irqenabled, ChangeIRQDMA;
    private byte IRQStatus;
    private uint ActiveMask;
    private byte IRQChan;
    private uint RampIRQ, WaveIRQ;
    private bool _dmaCbRegistered;

    static Dosbox074Gus() => MakeTables();

    private sealed class Voice
    {
        public uint WaveStart, WaveEnd, WaveAddr, WaveAdd;
        public byte WaveCtrl;
        public ushort WaveFreq;
        public uint RampStart, RampEnd, RampVol, RampAdd;
        public byte RampRate, RampCtrl, PanPot, channum;
        public uint irqmask, PanLeft, PanRight;
        public int VolLeft, VolRight;
        public Dosbox074Gus G = null!;
        public Voice(byte num) { channum = num; irqmask = 1u << num; WaveCtrl = 3; RampCtrl = 3; PanPot = 0x7; }

        public void WriteWaveFreq(ushort val)
        {
            WaveFreq = val;
            double frameadd = (double)(val >> 1) / 512.0;
            double realadd = (frameadd * G.basefreq / rate) * (1 << WAVE_FRACT);
            WaveAdd = (uint)realadd;
        }
        public void WriteWaveCtrl(byte val)
        {
            uint oldirq = G.WaveIRQ;
            WaveCtrl = (byte)(val & 0x7f);
            if ((val & 0xa0) == 0xa0) G.WaveIRQ |= irqmask; else G.WaveIRQ &= ~irqmask;
            if (oldirq != G.WaveIRQ) G.CheckVoiceIrq();
        }
        public byte ReadWaveCtrl() { byte ret = WaveCtrl; if ((G.WaveIRQ & irqmask) != 0) ret |= 0x80; return ret; }
        public void UpdateWaveRamp() { WriteWaveFreq(WaveFreq); WriteRampRate(RampRate); }
        public void WritePanPot(byte val)
        {
            PanPot = val;
            PanLeft = pantable[0x0f - (val & 0xf)];
            PanRight = pantable[val & 0xf];
            UpdateVolumes();
        }
        public void WriteRampCtrl(byte val)
        {
            uint old = G.RampIRQ;
            RampCtrl = (byte)(val & 0x7f);
            if ((val & 0xa0) == 0xa0) G.RampIRQ |= irqmask; else G.RampIRQ &= ~irqmask;
            if (old != G.RampIRQ) G.CheckVoiceIrq();
        }
        public byte ReadRampCtrl() { byte ret = RampCtrl; if ((G.RampIRQ & irqmask) != 0) ret |= 0x80; return ret; }
        public void WriteRampRate(byte val)
        {
            RampRate = val;
            double frameadd = (double)(RampRate & 63) / (double)(1 << (3 * (val >> 6)));
            double realadd = (frameadd * G.basefreq / rate) * (1 << RAMP_FRACT);
            RampAdd = (uint)realadd;
        }
        public void WaveUpdate()
        {
            if ((WaveCtrl & 0x3) != 0) return;
            int WaveLeft;
            if ((WaveCtrl & 0x40) != 0) { WaveAddr -= WaveAdd; WaveLeft = (int)(WaveStart - WaveAddr); }
            else { WaveAddr += WaveAdd; WaveLeft = (int)(WaveAddr - WaveEnd); }
            if (WaveLeft < 0) return;
            if ((WaveCtrl & 0x20) != 0) G.WaveIRQ |= irqmask;
            if ((RampCtrl & 0x04) != 0) return;
            if ((WaveCtrl & 0x08) != 0)
            {
                if ((WaveCtrl & 0x10) != 0) WaveCtrl ^= 0x40;
                WaveAddr = (WaveCtrl & 0x40) != 0 ? (uint)(WaveEnd - WaveLeft) : (uint)(WaveStart + WaveLeft);
            }
            else
            {
                WaveCtrl |= 1;
                WaveAddr = (WaveCtrl & 0x40) != 0 ? WaveStart : WaveEnd;
            }
        }
        public void UpdateVolumes()
        {
            int templeft = (int)(RampVol - PanLeft);
            templeft &= ~(templeft >> 31);
            int tempright = (int)(RampVol - PanRight);
            tempright &= ~(tempright >> 31);
            VolLeft = vol16bit[(templeft >> RAMP_FRACT) & 4095];
            VolRight = vol16bit[(tempright >> RAMP_FRACT) & 4095];
        }
        public void RampUpdate()
        {
            if ((RampCtrl & 0x3) != 0) return;
            int RampLeft;
            if ((RampCtrl & 0x40) != 0) { RampVol -= RampAdd; RampLeft = (int)(RampStart - RampVol); }
            else { RampVol += RampAdd; RampLeft = (int)(RampVol - RampEnd); }
            if (RampLeft < 0) { UpdateVolumes(); return; }
            if ((RampCtrl & 0x20) != 0) G.RampIRQ |= irqmask;
            if ((RampCtrl & 0x08) != 0)
            {
                if ((RampCtrl & 0x10) != 0) RampCtrl ^= 0x40;
                RampVol = (RampCtrl & 0x40) != 0 ? (uint)(RampEnd - RampLeft) : (uint)(RampStart + RampLeft);
            }
            else
            {
                RampCtrl |= 1;
                RampVol = (RampCtrl & 0x40) != 0 ? RampStart : RampEnd;
            }
            UpdateVolumes();
        }
        public void generateSamples(int[] stream, int len)
        {
            if ((RampCtrl & WaveCtrl & 3) != 0) return;
            bool eightbit = (WaveCtrl & 0x4) == 0;
            for (int i = 0; i < len; i++)
            {
                int tmpsamp = G.GetSample(WaveAdd, WaveAddr, eightbit);
                stream[i << 1] += tmpsamp * VolLeft;
                stream[(i << 1) + 1] += tmpsamp * VolRight;
                WaveUpdate();
                RampUpdate();
            }
        }
        public void Save(BinaryWriter w)
        {
            w.Write(WaveStart); w.Write(WaveEnd); w.Write(WaveAddr); w.Write(WaveAdd); w.Write(WaveCtrl); w.Write(WaveFreq);
            w.Write(RampStart); w.Write(RampEnd); w.Write(RampVol); w.Write(RampAdd); w.Write(RampRate); w.Write(RampCtrl); w.Write(PanPot);
            w.Write(PanLeft); w.Write(PanRight); w.Write(VolLeft); w.Write(VolRight);
        }
        public void Load(BinaryReader r)
        {
            WaveStart = r.ReadUInt32(); WaveEnd = r.ReadUInt32(); WaveAddr = r.ReadUInt32(); WaveAdd = r.ReadUInt32(); WaveCtrl = r.ReadByte(); WaveFreq = r.ReadUInt16();
            RampStart = r.ReadUInt32(); RampEnd = r.ReadUInt32(); RampVol = r.ReadUInt32(); RampAdd = r.ReadUInt32(); RampRate = r.ReadByte(); RampCtrl = r.ReadByte(); PanPot = r.ReadByte();
            PanLeft = r.ReadUInt32(); PanRight = r.ReadUInt32(); VolLeft = r.ReadInt32(); VolRight = r.ReadInt32();
        }
    }

    private readonly Voice[] guschan = new Voice[32];
    private Voice? curchan;

    public Dosbox074Gus(PcMachine m)
    {
        _m = m;
        _mix = new DosboxMixer(() => m.PitNow, 44100, stereoOut: false);
        _mix.OnEvent = (code, val) => { if (code == EV_TIMER) GUS_TimerEvent(val); };
        _gusChan = _mix.AddChannel(GUS_CallBack, (int)rate);
        for (byte i = 0; i < 32; i++) guschan[i] = new Voice(i) { G = this };
        Reset();
    }

    public void Reset()
    {
        UnregisterDma();
        _mix.Restart(true);
        _gusChan.Enabled = false;
        Array.Clear(GUSRam);
        gRegSelect = 0; gRegData = 0; gDramAddr = 0; gCurChannel = 0; DMAControl = 0; dmaAddr = 0; TimerControl = SampControl = mixControl = ActiveChannels = 0;
        basefreq = 0; timers[0] = timers[1] = default; irqenabled = ChangeIRQDMA = false; IRQStatus = 0; ActiveMask = 0; IRQChan = 0; RampIRQ = WaveIRQ = 0;
        AutoAmp = 512; adlib_commandreg = 0;
        dma1 = dma2 = 3; irq1 = irq2 = 5;
        for (byte i = 0; i < 32; i++) guschan[i] = new Voice(i) { G = this };
        curchan = null;
        gRegData = 0x1; GUSReset(); gRegData = 0x0;
    }

    private int GetSample(uint Delta, uint CurAddr, bool eightbit)
    {
        uint useAddr = CurAddr >> WAVE_FRACT;
        if (eightbit)
        {
            useAddr &= 0xfffff;
            if (Delta >= (1 << WAVE_FRACT)) return (sbyte)GUSRam[useAddr] << 8;
            int w1 = (sbyte)GUSRam[useAddr] << 8;
            int w2 = (sbyte)GUSRam[useAddr + 1] << 8;
            int diff = w2 - w1;
            return w1 + ((diff * (int)(CurAddr & WAVE_FRACT_MASK)) >> WAVE_FRACT);
        }
        else
        {
            uint holdAddr = useAddr & 0xc0000;
            useAddr &= 0x1ffff;
            useAddr <<= 1;
            useAddr = (holdAddr | useAddr) & 0xfffff;
            if (Delta >= (1 << WAVE_FRACT)) return GUSRam[useAddr] | ((sbyte)GUSRam[useAddr + 1] << 8);
            int w1 = GUSRam[useAddr] | ((sbyte)GUSRam[useAddr + 1] << 8);
            int w2 = GUSRam[useAddr + 2] | ((sbyte)GUSRam[useAddr + 3] << 8);
            int diff = w2 - w1;
            return w1 + ((diff * (int)(CurAddr & WAVE_FRACT_MASK)) >> WAVE_FRACT);
        }
    }

    private void GUSReset()
    {
        if ((gRegData & 0x1) == 0x1)
        {
            adlib_commandreg = 85;
            IRQStatus = 0;
            timers[0].raiseirq = timers[1].raiseirq = false;
            timers[0].reached = timers[1].reached = false;
            timers[0].running = timers[1].running = false;
            timers[0].value = timers[1].value = 0xff;
            timers[0].delay = 0.080f; timers[1].delay = 0.320f;
            ChangeIRQDMA = false;
            mixControl = 0x0b;
            for (int i = 0; i < 32; i++)
            {
                guschan[i].RampVol = 0;
                guschan[i].WriteWaveCtrl(0x1);
                guschan[i].WriteRampCtrl(0x1);
                guschan[i].WritePanPot(0x7);
            }
            IRQChan = 0;
        }
        irqenabled = (gRegData & 0x4) != 0;
    }

    private void GUS_CheckIRQ()
    {
        if (IRQStatus != 0 && (mixControl & 0x08) != 0) { IrqsRaised++; _m.Pic.Pulse(irq1 & 15); }
    }

    private void CheckVoiceIrq()
    {
        IRQStatus &= 0x9f;
        uint totalmask = (RampIRQ | WaveIRQ) & ActiveMask;
        if (totalmask == 0) return;
        if (RampIRQ != 0) IRQStatus |= 0x40;
        if (WaveIRQ != 0) IRQStatus |= 0x20;
        GUS_CheckIRQ();
        for (;;)
        {
            uint check = 1u << IRQChan;
            if ((totalmask & check) != 0) return;
            IRQChan++;
            if (IRQChan >= ActiveChannels) IRQChan = 0;
        }
    }

    private ushort ExecuteReadRegister()
    {
        byte tmpreg;
        switch (gRegSelect)
        {
            case 0x41:
                tmpreg = (byte)(DMAControl & 0xbf);
                tmpreg |= (byte)((IRQStatus & 0x80) >> 1);
                IRQStatus &= 0x7f;
                return (ushort)(tmpreg << 8);
            case 0x42: return dmaAddr;
            case 0x45: return (ushort)(TimerControl << 8);
            case 0x49:
                tmpreg = (byte)(DMAControl & 0xbf);
                tmpreg |= (byte)((IRQStatus & 0x80) >> 1);
                return (ushort)(tmpreg << 8);
            case 0x80: return curchan != null ? (ushort)(curchan.ReadWaveCtrl() << 8) : (ushort)0x0300;
            case 0x82: return curchan != null ? (ushort)(curchan.WaveStart >> (WAVE_BITS + 16)) : (ushort)0;
            case 0x83: return curchan != null ? (ushort)(curchan.WaveStart >> WAVE_BITS) : (ushort)0;
            case 0x89: return curchan != null ? (ushort)((curchan.RampVol >> RAMP_FRACT) << 4) : (ushort)0;
            case 0x8a: return curchan != null ? (ushort)(curchan.WaveAddr >> (WAVE_BITS + 16)) : (ushort)0;
            case 0x8b: return curchan != null ? (ushort)(curchan.WaveAddr >> WAVE_BITS) : (ushort)0;
            case 0x8d: return curchan != null ? (ushort)(curchan.ReadRampCtrl() << 8) : (ushort)0x0300;
            case 0x8f:
            {
                tmpreg = (byte)(IRQChan | 0x20);
                uint mask = 1u << IRQChan;
                if ((RampIRQ & mask) == 0) tmpreg |= 0x40;
                if ((WaveIRQ & mask) == 0) tmpreg |= 0x80;
                RampIRQ &= ~mask;
                WaveIRQ &= ~mask;
                CheckVoiceIrq();
                return (ushort)(tmpreg << 8);
            }
            default: return gRegData;
        }
    }

    private void GUS_TimerEvent(int val)
    {
        if (!timers[val].masked) timers[val].reached = true;
        if (timers[val].raiseirq) { IRQStatus |= (byte)(0x4 << val); GUS_CheckIRQ(); }
        if (timers[val].running) _mix.AddEvent(EV_TIMER, timers[val].delay, val);
    }

    private void RegisterDma(bool on)
    {
        UnregisterDma();
        _dmaCbRegistered = on;
        _m.Dma[dma1].RegisterCallback(on ? DmaCb : null);
    }
    private void UnregisterDma() { foreach (var c in _m.Dma.Channels) if (c.Callback == DmaCb) c.Callback = null; _dmaCbRegistered = false; }
    private void DmaCb(Dma8237.Channel c, DmaEvent e) => GUS_DMA_Callback(c, e);

    private void ExecuteGlobRegister()
    {
        switch (gRegSelect)
        {
            case 0x0: curchan?.WriteWaveCtrl((byte)(gRegData >> 8)); break;
            case 0x1: curchan?.WriteWaveFreq(gRegData); break;
            case 0x2:
                if (curchan != null) { uint t = (uint)(gRegData & 0x1fff) << (16 + WAVE_BITS); curchan.WaveStart = (curchan.WaveStart & WAVE_MSWMASK) | t; }
                break;
            case 0x3:
                if (curchan != null) { uint t = (uint)gRegData << WAVE_BITS; curchan.WaveStart = (curchan.WaveStart & WAVE_LSWMASK) | t; }
                break;
            case 0x4:
                if (curchan != null) { uint t = (uint)(gRegData & 0x1fff) << (16 + WAVE_BITS); curchan.WaveEnd = (curchan.WaveEnd & WAVE_MSWMASK) | t; }
                break;
            case 0x5:
                if (curchan != null) { uint t = (uint)gRegData << WAVE_BITS; curchan.WaveEnd = (curchan.WaveEnd & WAVE_LSWMASK) | t; }
                break;
            case 0x6: curchan?.WriteRampRate((byte)(gRegData >> 8)); break;
            case 0x7: if (curchan != null) curchan.RampStart = (uint)((gRegData >> 8) & 0xff) << (4 + RAMP_FRACT); break;
            case 0x8: if (curchan != null) curchan.RampEnd = (uint)((gRegData >> 8) & 0xff) << (4 + RAMP_FRACT); break;
            case 0x9:
                if (curchan != null) { uint t = (uint)(gRegData >> 4); curchan.RampVol = t << RAMP_FRACT; curchan.UpdateVolumes(); }
                break;
            case 0xA:
                if (curchan != null) { uint t = (uint)(gRegData & 0x1fff) << (16 + WAVE_BITS); curchan.WaveAddr = (curchan.WaveAddr & WAVE_MSWMASK) | t; }
                break;
            case 0xB:
                if (curchan != null) { uint t = (uint)gRegData << WAVE_BITS; curchan.WaveAddr = (curchan.WaveAddr & WAVE_LSWMASK) | t; }
                break;
            case 0xC: curchan?.WritePanPot((byte)(gRegData >> 8)); break;
            case 0xD: curchan?.WriteRampCtrl((byte)(gRegData >> 8)); break;
            case 0xE:
                gRegSelect = (byte)(gRegData >> 8);
                ActiveChannels = (byte)(1 + ((gRegData >> 8) & 63));
                if (ActiveChannels < 14) ActiveChannels = 14;
                if (ActiveChannels > 32) ActiveChannels = 32;
                ActiveMask = 0xffffffffu >> (32 - ActiveChannels);
                _gusChan.Enable(true);
                basefreq = (uint)((float)1000000 / (1.619695497 * (float)ActiveChannels));
                for (int i = 0; i < ActiveChannels; i++) guschan[i].UpdateWaveRamp();
                break;
            case 0x10: break;
            case 0x41:
                DMAControl = (byte)(gRegData >> 8);
                RegisterDma((DMAControl & 0x1) != 0);
                break;
            case 0x42: dmaAddr = gRegData; break;
            case 0x43: gDramAddr = (0xff0000 & gDramAddr) | gRegData; break;
            case 0x44: gDramAddr = (0xffff & gDramAddr) | ((uint)(gRegData >> 8) << 16); break;
            case 0x45:
                TimerControl = (byte)(gRegData >> 8);
                timers[0].raiseirq = (TimerControl & 0x04) > 0;
                if (!timers[0].raiseirq) IRQStatus &= unchecked((byte)~0x04);
                timers[1].raiseirq = (TimerControl & 0x08) > 0;
                if (!timers[1].raiseirq) IRQStatus &= unchecked((byte)~0x08);
                break;
            case 0x46:
                timers[0].value = (byte)(gRegData >> 8);
                timers[0].delay = (0x100 - timers[0].value) * 0.080f;
                break;
            case 0x47:
                timers[1].value = (byte)(gRegData >> 8);
                timers[1].delay = (0x100 - timers[1].value) * 0.320f;
                break;
            case 0x49:
                SampControl = (byte)(gRegData >> 8);
                RegisterDma((SampControl & 0x1) != 0);
                break;
            case 0x4c: GUSReset(); break;
        }
    }

    private int read_gus(int port, bool word)
    {
        switch (port - portbase)
        {
            case 0x206: return IRQStatus;
            case 0x208:
            {
                byte t = 0;
                if (timers[0].reached) t |= 1 << 6;
                if (timers[1].reached) t |= 1 << 5;
                if ((t & 0x60) != 0) t |= 1 << 7;
                if ((IRQStatus & 0x04) != 0) t |= 1 << 2;
                if ((IRQStatus & 0x08) != 0) t |= 1 << 1;
                return t;
            }
            case 0x20a: return adlib_commandreg;
            case 0x302: return (byte)gCurChannel;
            case 0x303: return gRegSelect;
            case 0x304: return word ? ExecuteReadRegister() & 0xffff : ExecuteReadRegister() & 0xff;
            case 0x305: return ExecuteReadRegister() >> 8;
            case 0x307: return gDramAddr < 1024 * 1024 ? GUSRam[gDramAddr] : 0;
        }
        return 0xff;
    }

    private void write_gus(int port, int val, bool word)
    {
        switch (port - portbase)
        {
            case 0x200: mixControl = (byte)val; ChangeIRQDMA = true; return;
            case 0x208: adlib_commandreg = (byte)val; break;
            case 0x209:
                if ((val & 0x80) != 0) { timers[0].reached = false; timers[1].reached = false; return; }
                timers[0].masked = (val & 0x40) > 0;
                timers[1].masked = (val & 0x20) > 0;
                if ((val & 0x1) != 0) { if (!timers[0].running) { _mix.AddEvent(EV_TIMER, timers[0].delay, 0); timers[0].running = true; } }
                else timers[0].running = false;
                if ((val & 0x2) != 0) { if (!timers[1].running) { _mix.AddEvent(EV_TIMER, timers[1].delay, 1); timers[1].running = true; } }
                else timers[1].running = false;
                break;
            case 0x20b:
                if (!ChangeIRQDMA) break;
                ChangeIRQDMA = false;
                if ((mixControl & 0x40) != 0) { if (irqtable[val & 0x7] != 0) irq1 = irqtable[val & 0x7]; }
                else { if (dmatable[val & 0x7] != 0) dma1 = dmatable[val & 0x7]; }
                break;
            case 0x302: gCurChannel = (ushort)(val & 31); curchan = guschan[gCurChannel]; break;
            case 0x303: gRegSelect = (byte)val; gRegData = 0; break;
            case 0x304:
                if (word) { gRegData = (ushort)val; ExecuteGlobRegister(); }
                else gRegData = (ushort)val;
                break;
            case 0x305:
                gRegData = (ushort)((0x00ff & gRegData) | (val << 8));
                ExecuteGlobRegister();
                break;
            case 0x307: if (gDramAddr < 1024 * 1024) GUSRam[gDramAddr] = (byte)val; break;
        }
    }

    private readonly byte[] _dmaTmp = new byte[2 * 65536 + 4];

    private void GUS_DMA_Callback(Dma8237.Channel chan, DmaEvent ev)
    {
        if (ev != DmaEvent.Unmasked) return;
        int dmaaddr = dmaAddr << 4;
        if ((DMAControl & 0x2) == 0)
        {
            int want = chan.CurrCnt + 1;
            int read = chan.Read(want, _dmaTmp, 0);
            read *= chan.Dma16 ? 2 : 1;
            for (int i = 0; i < read; i++) if (dmaaddr + i < 1024 * 1024) GUSRam[dmaaddr + i] = _dmaTmp[i];
            if ((DMAControl & 0x80) != 0)
            {
                if ((DMAControl & 0x40) == 0) { for (int i = dmaaddr; i < dmaaddr + read && i < 1024 * 1024; i++) GUSRam[i] ^= 0x80; }
                else { for (int i = dmaaddr + 1; i < dmaaddr + read && i < 1024 * 1024; i += 2) GUSRam[i] ^= 0x80; }
            }
        }
        else
        {
            int want = chan.CurrCnt + 1, bytes = want * (chan.Dma16 ? 2 : 1);
            for (int i = 0; i < bytes; i++) _dmaTmp[i] = dmaaddr + i < 1024 * 1024 ? GUSRam[dmaaddr + i] : (byte)0;
            chan.Write(want, _dmaTmp, 0);
        }
        if ((DMAControl & 0x20) != 0) { IRQStatus |= 0x80; GUS_CheckIRQ(); }
        chan.Callback = null; _dmaCbRegistered = false;   // chan->Register_Callback(0) (its mask report goes to no one)
    }

    private int[] _buf32 = new int[8192];

    private void GUS_CallBack(int len)
    {
        if (_buf32.Length < len * 2) _buf32 = new int[len * 2];
        Array.Clear(_buf32, 0, len * 2);
        for (int i = 0; i < ActiveChannels; i++) guschan[i].generateSamples(_buf32, len);
        for (int i = 0; i < len * 2; i++)
        {
            int sample = ((_buf32[i] >> 13) * AutoAmp) >> 9;
            if (sample > 32767) { sample = 32767; AutoAmp--; }
            else if (sample < -32768) { sample = -32768; AutoAmp--; }
            _buf32[i] = sample;
        }
        // MONO (see the header): left and right summed into one channel
        _gusChan.AddMono(len, k => (_buf32[k * 2] + _buf32[k * 2 + 1]) >> 1);
        CheckVoiceIrq();
    }

    private static void MakeTables()
    {
        double outv = 1 << 13;
        for (int i = 4095; i >= 0; i--) { vol16bit[i] = (ushort)(short)outv; outv /= 1.002709201; }
        pantable[0] = 0;
        for (int i = 1; i < 16; i++) pantable[i] = (uint)(-128.0 * (Math.Log((double)i / 15.0) / Math.Log(2.0)) * (1 << RAMP_FRACT));
    }

    // ================= IPcSoundCard =================

    private static bool IsPort(int o) => o is 0x200 or 0x206 or 0x208 or 0x209 or 0x20a or 0x20b or >= 0x302 and <= 0x307;

    public bool In(int port, bool word, out int value)
    {
        value = 0xff;
        int o = port - portbase;
        if (word) { if (o != 0x304) return false; Advance(); value = read_gus(port, true); return true; }
        if (!IsPort(o) || o is 0x200 or 0x209 or 0x20b) { if (IsPort(o)) return true; return false; }   // write-only ones read FFh
        Advance(); value = read_gus(port, false); return true;
    }

    public bool Out(int port, bool word, int value)
    {
        int o = port - portbase;
        if (word) { if (o != 0x304) return false; Advance(); write_gus(port, value & 0xffff, true); return true; }
        if (!IsPort(o) || o is 0x206 or 0x20a) { if (IsPort(o)) return true; return false; }
        Advance(); write_gus(port, value & 0xff, false); return true;
    }

    public void Advance() => _mix.Advance();
    public int MixInto(short[] buffer, int count) => _mix.MixInto(buffer, count);
    public int Pending => _mix.Pending;
    public void Restart() => _mix.Restart();

    public void Save(BinaryWriter w)
    {
        w.Write(1);
        _mix.Save(w);
        w.Write(GUSRam, 0, 1024 * 1024); w.Write(AutoAmp); w.Write(adlib_commandreg);
        w.Write(gRegSelect); w.Write(gRegData); w.Write(gDramAddr); w.Write(gCurChannel); w.Write(DMAControl); w.Write(dmaAddr);
        w.Write(TimerControl); w.Write(SampControl); w.Write(mixControl); w.Write(ActiveChannels); w.Write(basefreq);
        foreach (var t in timers) { w.Write(t.value); w.Write(t.reached); w.Write(t.raiseirq); w.Write(t.masked); w.Write(t.running); w.Write(t.delay); }
        w.Write(dma1); w.Write(dma2); w.Write(irq1); w.Write(irq2); w.Write(irqenabled); w.Write(ChangeIRQDMA); w.Write(IRQStatus); w.Write(ActiveMask);
        w.Write(IRQChan); w.Write(RampIRQ); w.Write(WaveIRQ); w.Write(_dmaCbRegistered); w.Write(curchan != null);
        foreach (var v in guschan) v.Save(w);
    }

    public void Load(BinaryReader r)
    {
        if (r.ReadInt32() != 1) throw new InvalidDataException("GUS state version");
        _mix.Load(r);
        r.Read(GUSRam, 0, 1024 * 1024); AutoAmp = r.ReadInt32(); adlib_commandreg = r.ReadByte();
        gRegSelect = r.ReadByte(); gRegData = r.ReadUInt16(); gDramAddr = r.ReadUInt32(); gCurChannel = r.ReadUInt16(); DMAControl = r.ReadByte(); dmaAddr = r.ReadUInt16();
        TimerControl = r.ReadByte(); SampControl = r.ReadByte(); mixControl = r.ReadByte(); ActiveChannels = r.ReadByte(); basefreq = r.ReadUInt32();
        for (int i = 0; i < 2; i++) timers[i] = new GusTimer { value = r.ReadByte(), reached = r.ReadBoolean(), raiseirq = r.ReadBoolean(), masked = r.ReadBoolean(), running = r.ReadBoolean(), delay = r.ReadSingle() };
        dma1 = r.ReadByte(); dma2 = r.ReadByte(); irq1 = r.ReadByte(); irq2 = r.ReadByte(); irqenabled = r.ReadBoolean(); ChangeIRQDMA = r.ReadBoolean(); IRQStatus = r.ReadByte(); ActiveMask = r.ReadUInt32();
        IRQChan = r.ReadByte(); RampIRQ = r.ReadUInt32(); WaveIRQ = r.ReadUInt32(); bool cb = r.ReadBoolean(); bool cur = r.ReadBoolean();
        foreach (var v in guschan) v.Load(r);
        curchan = cur ? guschan[gCurChannel] : null;
        UnregisterDma();
        if (cb) { _dmaCbRegistered = true; _m.Dma[dma1].SetCallbackSilently(DmaCb); }
    }
}
