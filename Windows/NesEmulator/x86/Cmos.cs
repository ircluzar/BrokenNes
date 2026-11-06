// The MC146818 CMOS RAM and real-time clock (ports 70h / 71h). Written for BrokenNes. The clock is deterministic: a fixed start
// date plus the machine's own elapsed time, so a run replays the same.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class Cmos
{
    public readonly byte[] Ram = new byte[128];
    private int _index;
    private readonly Func<long> _pitNow;
    /// <summary>The wall time at power-on (default 1994-06-01 12:00:00); the clock runs from there in machine time.</summary>
    public DateTime Start = new(1994, 6, 1, 12, 0, 0);

    public Cmos(Func<long> pitNow, int memKb)
    {
        _pitNow = pitNow;
        Ram[0x0a] = 0x26; Ram[0x0b] = 0x02; Ram[0x0d] = 0x80;
        Ram[0x10] = 0x00;                                 // no floppy drives
        Ram[0x12] = 0x00;
        Ram[0x14] = 0x06;                                 // VGA, keyboard, (no FPU)
        Ram[0x15] = 640 & 0xff; Ram[0x16] = 640 >> 8;
        int ext = Math.Min(memKb - 1024, 0xffff);
        Ram[0x17] = (byte)ext; Ram[0x18] = (byte)(ext >> 8);
        Ram[0x30] = (byte)ext; Ram[0x31] = (byte)(ext >> 8);
        Ram[0x32] = 0x19;
    }

    public DateTime Now => Start.AddTicks(_pitNow() * TimeSpan.TicksPerSecond / Pit8254.Freq);

    private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

    public byte In(int port)
    {
        if (port == 0x70) return (byte)_index;
        var t = Now;
        bool bin = (Ram[0x0b] & 4) != 0;
        byte F(int v) => bin ? (byte)v : Bcd(v);
        switch (_index)
        {
            case 0x00: return F(t.Second);
            case 0x02: return F(t.Minute);
            case 0x04: return F(t.Hour);
            case 0x06: return (byte)((int)t.DayOfWeek + 1);
            case 0x07: return F(t.Day);
            case 0x08: return F(t.Month);
            case 0x09: return F(t.Year % 100);
            case 0x32: return F(t.Year / 100);
            case 0x0a: return (byte)(Ram[0x0a] & 0x7f);   // update never in progress
            case 0x0c: { byte v = Ram[0x0c]; Ram[0x0c] = 0; return v; }
            default: return Ram[_index & 0x7f];
        }
    }

    public void Out(int port, byte v)
    {
        if (port == 0x70) { _index = v & 0x7f; return; }
        if (_index is 0x0c or 0x0d) return;
        Ram[_index] = v;
    }

    public void Save(BinaryWriter w) { w.Write(Ram); w.Write(_index); w.Write(Start.Ticks); }
    public void Load(BinaryReader r) { r.Read(Ram, 0, 128); _index = r.ReadInt32(); Start = new DateTime(r.ReadInt64()); }
}
