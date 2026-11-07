// Two cascaded Intel 8259A interrupt controllers. Ported from tiny386 i8259.c (commit 2c1205c), itself from QEMU,
// MIT licence, Copyright (c) 2003-2004 Fabrice Bellard. See x86/THIRD_PARTY_NOTICES.md.

using System;

namespace NesEmulator.X86;

/// <summary>The PC's master (ports 20h/21h) and slave (A0h/A1h) 8259A. <see cref="Raise"/> is called when an unmasked IRQ wants the CPU.</summary>
public sealed class Pic8259
{
    private sealed class Chip
    {
        public byte LastIrr, Irr, Imr, Isr, PriorityAdd, IrqBase, ReadRegSelect, Poll, SpecialMask, InitState, AutoEoi,
            RotateOnAutoEoi, SpecialFullyNested, Init4, SingleMode;
        public void Reset()
        {
            LastIrr = Irr = Imr = Isr = PriorityAdd = IrqBase = ReadRegSelect = Poll = SpecialMask = InitState = AutoEoi = 0;
            RotateOnAutoEoi = SpecialFullyNested = Init4 = SingleMode = 0;
        }
    }

    private readonly Chip[] _p = { new Chip(), new Chip() };
    public Action Raise = () => { };

    public byte MasterImr => _p[0].Imr;
    public byte SlaveImr => _p[1].Imr;
    /// <summary>The master's request register (tests: which IRQs a device raised).</summary>
    public byte MasterIrr => _p[0].Irr;

    private static void SetIrq1(Chip s, int irq, int level)
    {
        int mask = 1 << irq;
        if (level != 0)
        {
            if ((s.LastIrr & mask) == 0) s.Irr |= (byte)mask;
            s.LastIrr |= (byte)mask;
        }
        else s.LastIrr &= (byte)~mask;
    }

    private static int GetPriority(Chip s, int mask)
    {
        if (mask == 0) return 8;
        int p = 0;
        while ((mask & (1 << ((p + s.PriorityAdd) & 7))) == 0) p++;
        return p;
    }

    private int GetIrq(Chip s)
    {
        int mask = s.Irr & ~s.Imr;
        int priority = GetPriority(s, mask);
        if (priority == 8) return -1;
        mask = s.Isr;
        if (s.SpecialMask != 0) mask &= ~s.Imr;
        if (s.SpecialFullyNested != 0 && s == _p[0]) mask &= ~(1 << 2);
        int cur = GetPriority(s, mask);
        return priority < cur ? (priority + s.PriorityAdd) & 7 : -1;
    }

    private void UpdateIrq()
    {
        if (GetIrq(_p[1]) >= 0) { SetIrq1(_p[0], 2, 1); SetIrq1(_p[0], 2, 0); }
        if (GetIrq(_p[0]) >= 0) Raise();
    }

    /// <summary>True when an unmasked request is waiting (for the run loop: a halted CPU wakes on it).</summary>
    public bool Pending => GetIrq(_p[0]) >= 0;

    public void SetIrq(int irq, int level) { SetIrq1(_p[irq >> 3], irq & 7, level); UpdateIrq(); }
    /// <summary>An edge on an IRQ line (the PIT and keyboard pulse their lines).</summary>
    public void Pulse(int irq) { SetIrq1(_p[irq >> 3], irq & 7, 1); SetIrq1(_p[irq >> 3], irq & 7, 0); UpdateIrq(); }

    private static void IntAck(Chip s, int irq)
    {
        if (s.AutoEoi != 0) { if (s.RotateOnAutoEoi != 0) s.PriorityAdd = (byte)((irq + 1) & 7); }
        else s.Isr |= (byte)(1 << irq);
        s.Irr &= (byte)~(1 << irq);
    }

    /// <summary>tiny386 i8259_read_irq: the vector of the interrupt the CPU takes now.</summary>
    public int ReadIrq()
    {
        int intno;
        int irq = GetIrq(_p[0]);
        if (irq >= 0)
        {
            IntAck(_p[0], irq);
            if (irq == 2)
            {
                int irq2 = GetIrq(_p[1]);
                if (irq2 >= 0) IntAck(_p[1], irq2); else irq2 = 7;
                intno = _p[1].IrqBase + irq2;
            }
            else intno = _p[0].IrqBase + irq;
        }
        else intno = _p[0].IrqBase + 7;
        UpdateIrq();
        return intno;
    }

    private void Write(Chip s, int addr, int val)
    {
        if ((addr & 1) == 0)
        {
            if ((val & 0x10) != 0)
            {
                s.Reset();
                s.InitState = 1; s.Init4 = (byte)(val & 1); s.SingleMode = (byte)(val & 2);
                // level-triggered mode (tiny386: abort) is taken as edge-triggered
            }
            else if ((val & 0x08) != 0)
            {
                if ((val & 0x04) != 0) s.Poll = 1;
                if ((val & 0x02) != 0) s.ReadRegSelect = (byte)(val & 1);
                if ((val & 0x40) != 0) s.SpecialMask = (byte)((val >> 5) & 1);
            }
            else
            {
                int cmd = val >> 5, irq;
                switch (cmd)
                {
                    case 0: case 4: s.RotateOnAutoEoi = (byte)(cmd >> 2); break;
                    case 1: case 5:
                        int pr = GetPriority(s, s.Isr);
                        if (pr != 8)
                        {
                            irq = (pr + s.PriorityAdd) & 7;
                            s.Isr &= (byte)~(1 << irq);
                            if (cmd == 5) s.PriorityAdd = (byte)((irq + 1) & 7);
                            UpdateIrq();
                        }
                        break;
                    case 3: irq = val & 7; s.Isr &= (byte)~(1 << irq); UpdateIrq(); break;
                    case 6: s.PriorityAdd = (byte)((val + 1) & 7); UpdateIrq(); break;
                    case 7: irq = val & 7; s.Isr &= (byte)~(1 << irq); s.PriorityAdd = (byte)((irq + 1) & 7); UpdateIrq(); break;
                }
            }
        }
        else
        {
            switch (s.InitState)
            {
                case 0: s.Imr = (byte)val; UpdateIrq(); break;
                case 1: s.IrqBase = (byte)(val & 0xf8); s.InitState = (byte)(s.SingleMode != 0 ? (s.Init4 != 0 ? 3 : 0) : 2); break;
                case 2: s.InitState = (byte)(s.Init4 != 0 ? 3 : 0); break;
                case 3: s.SpecialFullyNested = (byte)((val >> 4) & 1); s.AutoEoi = (byte)((val >> 1) & 1); s.InitState = 0; break;
            }
        }
    }

    private int PollRead(Chip s, int addr1)
    {
        int ret = GetIrq(s);
        if (ret >= 0)
        {
            if ((addr1 >> 7) != 0) { _p[0].Isr &= unchecked((byte)~(1 << 2)); _p[0].Irr &= unchecked((byte)~(1 << 2)); }
            s.Irr &= (byte)~(1 << ret); s.Isr &= (byte)~(1 << ret);
            if ((addr1 >> 7) != 0 || ret != 2) UpdateIrq();
        }
        else { ret = 7; UpdateIrq(); }
        return ret;
    }

    private int Read(Chip s, int addr1)
    {
        if (s.Poll != 0) { int r = PollRead(s, addr1); s.Poll = 0; return r; }
        if ((addr1 & 1) == 0) return s.ReadRegSelect != 0 ? s.Isr : s.Irr;
        return s.Imr;
    }

    public byte In(int port) => (byte)Read(port is 0x20 or 0x21 ? _p[0] : _p[1], port);
    public void Out(int port, int v) => Write(port is 0x20 or 0x21 ? _p[0] : _p[1], port, v);

    public void Reset() { _p[0].Reset(); _p[1].Reset(); }

    // ---- save state ----
    public void Save(System.IO.BinaryWriter w)
    {
        foreach (var s in _p)
            w.Write(new[] { s.LastIrr, s.Irr, s.Imr, s.Isr, s.PriorityAdd, s.IrqBase, s.ReadRegSelect, s.Poll, s.SpecialMask, s.InitState,
                s.AutoEoi, s.RotateOnAutoEoi, s.SpecialFullyNested, s.Init4, s.SingleMode });
    }
    public void Load(System.IO.BinaryReader r)
    {
        foreach (var s in _p)
        {
            var b = r.ReadBytes(15);
            s.LastIrr = b[0]; s.Irr = b[1]; s.Imr = b[2]; s.Isr = b[3]; s.PriorityAdd = b[4]; s.IrqBase = b[5]; s.ReadRegSelect = b[6];
            s.Poll = b[7]; s.SpecialMask = b[8]; s.InitState = b[9]; s.AutoEoi = b[10]; s.RotateOnAutoEoi = b[11]; s.SpecialFullyNested = b[12];
            s.Init4 = b[13]; s.SingleMode = b[14];
        }
    }
}
