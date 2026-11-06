// Intel 8254 programmable interval timer. Ported from tiny386 i8254.c (commit 2c1205c), itself from QEMU, MIT licence,
// Copyright (c) 2003-2004 Fabrice Bellard. See x86/THIRD_PARTY_NOTICES.md.
// Changed: time is the machine's own deterministic clock counted in PIT input ticks (1.193182 MHz), not the host's microseconds.

using System;
using System.IO;

namespace NesEmulator.X86;

/// <summary>The PC's 8254 (ports 40h-43h). Channel 0 drives IRQ 0, channel 2 the speaker (gate and output through port 61h).</summary>
public sealed class Pit8254
{
    public const long Freq = 1193182;
    private const int LSB = 1, MSB = 2, WORD0 = 3, WORD1 = 4;

    private sealed class Ch
    {
        public uint Count; public ushort LatchedCount; public byte CountLatched, StatusLatched, Status, ReadState, WriteState, WriteLatch,
            RwMode, Mode, Bcd, Gate, ReloadPending;
        public ushort StoppedCount; public long LoadTime, LastIrqCount;
        public int Mode2 => Mode >= 6 ? Mode - 4 : Mode;
    }

    private readonly Ch[] _c = { new Ch(), new Ch(), new Ch() };
    private readonly Func<long> _now;
    private readonly Action _irq0;
    /// <summary>Called when channel 2's programming changes (the speaker follows it).</summary>
    public Action? Ch2Changed;

    public Pit8254(Func<long> now, Action irq0) { _now = now; _irq0 = irq0; Reset(); }

    public void Reset()
    {
        for (int i = 0; i < 3; i++) { var s = _c[i]; s.Mode = 3; s.Gate = (byte)(i != 2 ? 1 : 0); LoadCount(s, 0); }
    }

    private int GetCount(Ch s)
    {
        if (s.ReloadPending != 0) return s.StoppedCount;
        long d = _now() - s.LoadTime;
        return s.Mode2 switch
        {
            0 or 1 or 4 or 5 => (int)((s.Count - d) & 0xffff),
            3 => (int)(s.Count - ((2 * d) % s.Count)),
            _ => (int)(s.Count - (d % s.Count)),
        };
    }

    private int GetOut1(Ch s, long now)
    {
        if (s.ReloadPending != 0) return 0;
        long d = now - s.LoadTime;
        return s.Mode2 switch
        {
            1 => d < s.Count ? 1 : 0,
            2 => (d % s.Count) == 0 && d != 0 ? 1 : 0,
            3 => (d % s.Count) < ((s.Count + 1) >> 1) ? 1 : 0,
            4 or 5 => d == s.Count ? 1 : 0,
            _ => d >= s.Count ? 1 : 0,
        };
    }

    private void LoadCount(Ch s, int val)
    {
        if (val == 0) val = 0x10000;
        s.ReloadPending = 0; s.LoadTime = _now(); s.LastIrqCount = 0; s.Count = (uint)val;
        if (s == _c[2]) Ch2Changed?.Invoke();
    }

    private void Latch(Ch s)
    {
        if (s.CountLatched == 0) { s.LatchedCount = (ushort)GetCount(s); s.CountLatched = s.RwMode; }
    }

    public void Out(int addr, int val)
    {
        addr &= 3;
        if (addr == 3)
        {
            int channel = val >> 6;
            if (channel == 3)
            {
                for (channel = 0; channel < 3; channel++)
                {
                    var s = _c[channel];
                    if ((val & (2 << channel)) == 0) continue;
                    if ((val & 0x20) == 0) Latch(s);
                    if ((val & 0x10) == 0 && s.StatusLatched == 0)
                    {
                        s.Status = (byte)((GetOut1(s, _now()) << 7) | (s.RwMode << 4) | (s.Mode << 1) | s.Bcd);
                        s.StatusLatched = 1;
                    }
                }
            }
            else
            {
                var s = _c[channel];
                int access = (val >> 4) & 3;
                if (access == 0) Latch(s);
                else
                {
                    s.RwMode = (byte)access; s.ReadState = (byte)access; s.WriteState = (byte)access;
                    s.Mode = (byte)((val >> 1) & 7); s.ReloadPending = 0; s.Bcd = (byte)(val & 1);
                    if (channel == 2) Ch2Changed?.Invoke();
                }
            }
        }
        else
        {
            var s = _c[addr];
            switch (s.WriteState)
            {
                default: LoadCount(s, val); break;
                case MSB: LoadCount(s, val << 8); break;
                case WORD0:
                    if (s.Mode2 == 0) { s.StoppedCount = (ushort)GetCount(s); s.ReloadPending = 1; }
                    s.WriteLatch = (byte)val; s.WriteState = WORD1; break;
                case WORD1: LoadCount(s, s.WriteLatch | (val << 8)); s.WriteState = WORD0; break;
            }
        }
    }

    public byte In(int addr)
    {
        var s = _c[addr & 3];
        int ret, count;
        if (s.StatusLatched != 0) { s.StatusLatched = 0; ret = s.Status; }
        else if (s.CountLatched != 0)
        {
            switch (s.CountLatched)
            {
                default: ret = s.LatchedCount & 0xff; s.CountLatched = 0; break;
                case MSB: ret = s.LatchedCount >> 8; s.CountLatched = 0; break;
                case WORD0: ret = s.LatchedCount & 0xff; s.CountLatched = MSB; break;
            }
        }
        else
        {
            switch (s.ReadState)
            {
                default: count = GetCount(s); ret = count & 0xff; break;
                case MSB: count = GetCount(s); ret = (count >> 8) & 0xff; break;
                case WORD0: count = GetCount(s); ret = count & 0xff; s.ReadState = WORD1; break;
                case WORD1: count = GetCount(s); ret = (count >> 8) & 0xff; s.ReadState = WORD0; break;
            }
        }
        return (byte)ret;
    }

    /// <summary>tiny386 i8254_update_irq: pulse IRQ 0 when channel 0 has run out since the last call.</summary>
    public void UpdateIrq()
    {
        var s = _c[0];
        long d = _now() - s.LoadTime;
        switch (s.Mode2)
        {
            case 2: case 3:
                if (s.LastIrqCount + s.Count <= d) { _irq0(); s.LastIrqCount += s.Count; }
                break;
            case 0: case 4:
                if (s.ReloadPending != 0) break;
                if (s.LastIrqCount == 0 && d >= s.Count) { _irq0(); s.LastIrqCount = s.Count; }
                break;
        }
    }

    /// <summary>The PIT tick at which channel 0 next raises IRQ 0, or long.MaxValue (a halted CPU sleeps until then).</summary>
    public long NextIrq0()
    {
        var s = _c[0];
        return s.Mode2 switch
        {
            2 or 3 => s.LoadTime + s.LastIrqCount + s.Count,
            0 or 4 when s.ReloadPending == 0 && s.LastIrqCount == 0 => s.LoadTime + s.Count,
            _ => long.MaxValue,
        };
    }

    public int GetOut(int ch) => GetOut1(_c[ch], _now());
    public int GetGate(int ch) => _c[ch].Gate;
    public int Ch2Count => (int)_c[2].Count;
    public int Ch2Mode => _c[2].Mode2;
    public void SetGate(int ch, int val)
    {
        var s = _c[ch];
        if (s.Mode2 is 1 or 5 or 2 or 3 && s.Gate < val) s.LoadTime = _now();
        s.Gate = (byte)val;
        if (ch == 2) Ch2Changed?.Invoke();
    }

    public void Save(BinaryWriter w)
    {
        foreach (var s in _c)
        {
            w.Write(s.Count); w.Write(s.LatchedCount);
            w.Write(new[] { s.CountLatched, s.StatusLatched, s.Status, s.ReadState, s.WriteState, s.WriteLatch, s.RwMode, s.Mode, s.Bcd, s.Gate, s.ReloadPending });
            w.Write(s.StoppedCount); w.Write(s.LoadTime); w.Write(s.LastIrqCount);
        }
    }
    public void Load(BinaryReader r)
    {
        foreach (var s in _c)
        {
            s.Count = r.ReadUInt32(); s.LatchedCount = r.ReadUInt16();
            var b = r.ReadBytes(11);
            s.CountLatched = b[0]; s.StatusLatched = b[1]; s.Status = b[2]; s.ReadState = b[3]; s.WriteState = b[4]; s.WriteLatch = b[5];
            s.RwMode = b[6]; s.Mode = b[7]; s.Bcd = b[8]; s.Gate = b[9]; s.ReloadPending = b[10];
            s.StoppedCount = r.ReadUInt16(); s.LoadTime = r.ReadInt64(); s.LastIrqCount = r.ReadInt64();
        }
    }
}
