// An Intel 82077AA-compatible floppy disk controller (ports 3F0h-3F5h, 3F7h, IRQ 6, DMA channel 2) for the x86 console's PCI board, so
// Windows 98's protected-mode floppy driver (HSFLOP.PDR, *PNP0700) can run drive A: in 32-bit mode. Written for BrokenNes from the public
// 82077AA data sheet; no source copied. The disks are the board's DiskImage objects (Floppy[0], Floppy[1]): one image track = one
// cylinder/head, 512-byte sectors numbered from 1.
//
// Modelled: the AT-mode registers (DOR, MSR / DSR, FIFO, DIR / CCR), reset with the four post-reset SENSE INTERRUPT statuses, the
// command / execution / result phases, implied seek (CONFIGURE EIS), multi-track, the disk-change line, the data rate (a medium only
// reads at its own rate), DMA transfers that stop at terminal count. Not modelled: non-DMA (PIO) execution, the FIFO threshold, timing
// beyond a small fixed delay per command, deleted-data marks (every sector is normal data), READ TRACK / VERIFY / SCAN.
// No medium in a drive: SEEK / RECALIBRATE work, a read / write / READ ID / FORMAT never finds an index pulse and stays in the execution
// phase until the driver resets the controller (its timeout), as a real drive with no disk does.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class Fdc82077
{
    private readonly Func<int, DiskImage?> _disk;
    private readonly Dma8237.Channel _dma;
    private readonly Action<bool> _irq;
    private readonly Func<long> _us;          // machine time in microseconds

    // registers
    private byte _dor;                        // bits 0-1 drive, 2 /RESET, 3 DMA+IRQ gate, 4-7 motors
    private byte _rate;                       // data rate select (DSR / CCR bits 0-1): 0 = 500 kb/s, 1 = 300, 2 = 250, 3 = 1 M
    private byte _srtHut, _hltNd;             // SPECIFY
    private byte _config = 0x00, _pretrk;     // CONFIGURE byte 2 (EIS 40h, EFIFO 20h, POLL 10h, FIFOTHR), precomp start track
    private bool _lock;
    private byte _perp;
    private readonly byte[] _pcn = new byte[4];
    private readonly bool[] _changed = { true, true, true, true };
    private readonly DiskImage?[] _seen = new DiskImage?[2];

    // phases
    private enum Phase : byte { Command, Exec, Result }
    private Phase _phase;
    private readonly byte[] _cmd = new byte[9];
    private int _cmdLen, _cmdPos;
    private readonly byte[] _res = new byte[10];
    private int _resLen, _resPos;
    private byte _seekBusy;                   // MSR bits 0-3
    private readonly byte[] _pendSt0 = new byte[4];   // statuses for SENSE INTERRUPT (a seek / recalibrate / reset per drive)
    private byte _pendMask;
    private bool _irqLine;
    // the scheduled end of a command (machine microseconds; long.MaxValue = none, a stalled command when in the execution phase)
    private long _due = long.MaxValue;
    private byte _dueKind;                    // 1 = reset, 2 = seek end, 3 = command end (do the transfer),
    private byte _dueDrive;

    public Fdc82077(Func<int, DiskImage?> disk, Dma8237.Channel dma, Action<bool> irq, Func<long> microseconds)
    {
        _disk = disk; _dma = dma; _irq = irq; _us = microseconds;
        PowerOn();
    }

    /// <summary>The IRQ 6 line as the board sees it (gated by DOR bit 3).</summary>
    public bool Irq => _irqLine && (_dor & 0x08) != 0;

    public static bool IsPort(int port) => port is >= 0x3f0 and <= 0x3f5 or 0x3f7;

    public void PowerOn()
    {
        _dor = 0; _rate = 2; _srtHut = _hltNd = 0; _config = 0; _pretrk = 0; _lock = false; _perp = 0;
        Array.Clear(_pcn); for (int i = 0; i < 4; i++) _changed[i] = true; _seen[0] = _seen[1] = null;
        _phase = Phase.Command; _cmdLen = _cmdPos = _resLen = _resPos = 0; _seekBusy = 0; _pendMask = 0;
        _due = long.MaxValue; _dueKind = 0;
        SetIrq(false);
    }

    /// <summary>The chip's reset (DOR bit 2 low, or DSR bit 7): the command machine idles; LOCK keeps CONFIGURE's FIFO settings.</summary>
    private void ChipReset()
    {
        _phase = Phase.Command; _cmdLen = _cmdPos = _resLen = _resPos = 0; _seekBusy = 0; _pendMask = 0;
        _due = long.MaxValue; _dueKind = 0;
        if (!_lock) { _config = 0; _pretrk = 0; }
        _rate = 2;
        SetIrq(false);
    }

    private void EndReset()
    {
        // with drive polling on (CONFIGURE POLL = 0) the chip reports a "ready changed" status per drive: the four SENSE INTERRUPTs
        if ((_config & 0x10) == 0)
        {
            for (int d = 0; d < 4; d++) _pendSt0[d] = (byte)(0xc0 | d);
            _pendMask = 0x0f;
            SetIrq(true);
        }
    }

    private void SetIrq(bool on) { _irqLine = on; _irq(on && (_dor & 0x08) != 0); }

    private DiskImage? Media(int drive)
    {
        if (drive >= 2) return null;
        var d = _disk(drive);
        if (d != null && !d.IsFloppy) d = null;
        if (!ReferenceEquals(d, _seen[drive])) { _seen[drive] = d; _changed[drive] = true; }
        return d;
    }

    private static bool DrivePresent(int drive) => drive < 2;

    // ================= ports =================

    public byte In(int port)
    {
        Poll();
        switch (port)
        {
            case 0x3f0: case 0x3f1: return 0xff;                 // (PS/2 status registers: not in AT mode)
            case 0x3f2: return _dor;
            case 0x3f3: return 0x00;                             // tape drive register
            case 0x3f4: return Msr();
            case 0x3f5:
                if (_phase != Phase.Result) return 0xff;
                {
                    byte v = _res[_resPos++];
                    if (_resPos >= _resLen) { _phase = Phase.Command; _resLen = _resPos = 0; }
                    // reading the result of a read / write / READ ID / FORMAT clears the interrupt
                    SetIrq(false);
                    return v;
                }
            case 0x3f7:
            {
                int drive = _dor & 3;
                if (drive < 2) Media(drive);
                return (byte)(_changed[drive] && (_dor & (0x10 << drive)) != 0 ? 0x80 : 0x00);
            }
        }
        return 0xff;
    }

    public void Out(int port, byte v)
    {
        Poll();
        switch (port)
        {
            case 0x3f2:
            {
                bool wasReset = (_dor & 4) == 0;
                _dor = v;
                if ((v & 4) == 0) { if (!wasReset) ChipReset(); }
                else if (wasReset) Schedule(1, 0, 20);
                _irq(_irqLine && (_dor & 0x08) != 0);
                return;
            }
            case 0x3f4:                                          // DSR
                _rate = (byte)(v & 3);
                if ((v & 0x80) != 0) { ChipReset(); Schedule(1, 0, 20); }
                return;
            case 0x3f5: WriteFifo(v); return;
            case 0x3f7: _rate = (byte)(v & 3); return;          // CCR
        }
    }

    private byte Msr()
    {
        if ((_dor & 4) == 0) return 0x00;                        // held in reset
        byte busy = _seekBusy;
        switch (_phase)
        {
            case Phase.Command: return (byte)(0x80 | busy | (_cmdPos > 0 ? 0x10 : 0));
            case Phase.Exec: return (byte)(0x10 | busy);
            default: return (byte)(0xc0 | 0x10 | busy);
        }
    }

    private static int CommandLength(byte c) => (c & 0x1f) switch
    {
        0x02 or 0x05 or 0x06 or 0x09 or 0x0c => 9,
        0x03 => 3, 0x04 => 2, 0x07 => 2, 0x08 => 1, 0x0a => 2, 0x0d => 6, 0x0e => 1, 0x0f => 3,
        0x10 => 1, 0x12 => 2, 0x13 => 4, 0x14 => 1,
        _ => 1,
    };

    private void WriteFifo(byte v)
    {
        if ((_dor & 4) == 0 || _phase != Phase.Command) return;
        if (_cmdPos == 0) _cmdLen = CommandLength(v);
        _cmd[_cmdPos++] = v;
        if (_cmdPos < _cmdLen) return;
        _cmdPos = 0;
        Execute();
    }

    private void Result(params byte[] r)
    {
        Array.Copy(r, _res, r.Length); _resLen = r.Length; _resPos = 0;
        _phase = Phase.Result;
    }

    private void Schedule(byte kind, int drive, long delayUs)
    {
        _dueKind = kind; _dueDrive = (byte)drive; _due = _us() + delayUs;
    }

    /// <summary>Finishes a command whose time has come (called from the machine's run loop and before every port access).</summary>
    public void Poll()
    {
        if (_due == long.MaxValue || _us() < _due) return;
        _due = long.MaxValue;
        byte kind = _dueKind; _dueKind = 0;
        switch (kind)
        {
            case 1: EndReset(); break;
            case 2:                                              // seek / recalibrate end
                _seekBusy &= (byte)~(1 << _dueDrive);
                SetIrq(true);
                break;
            case 3: FinishTransfer(); break;
        }
    }

    // ================= commands =================

    private void Execute()
    {
        byte c = _cmd[0];
        switch (c & 0x1f)
        {
            case 0x03:                                           // SPECIFY
                _srtHut = _cmd[1]; _hltNd = _cmd[2];
                _phase = Phase.Command; return;
            case 0x04:                                           // SENSE DRIVE STATUS
            {
                int d = _cmd[1] & 3, h = (_cmd[1] >> 2) & 1;
                var m = Media(d);
                byte st3 = (byte)(d | (h << 2) | 0x08 /* two-sided */ | 0x20 /* ready */);
                if (_pcn[d] == 0) st3 |= 0x10;                   // track 0
                if (m != null && m.Data.Length == 0) st3 |= 0x40;
                Result(st3); return;
            }
            case 0x07:                                           // RECALIBRATE
                StartSeek(_cmd[1] & 3, 0, true); return;
            case 0x08:                                           // SENSE INTERRUPT STATUS
            {
                SetIrq(false);
                for (int d = 0; d < 4; d++)
                    if ((_pendMask & (1 << d)) != 0) { _pendMask &= (byte)~(1 << d); Result(_pendSt0[d], _pcn[d]); return; }
                Result(0x80); return;
            }
            case 0x0f:
                if ((c & 0x80) != 0)                             // RELATIVE SEEK (8Fh / CFh)
                {
                    int d = _cmd[1] & 3, n = _cmd[2];
                    int target = (c & 0x40) != 0 ? _pcn[d] + n : _pcn[d] - n;
                    StartSeek(d, Math.Clamp(target, 0, 255), false); return;
                }
                StartSeek(_cmd[1] & 3, _cmd[2], false); return;   // SEEK
            case 0x10 when c == 0x10: Result(0x90); return;     // VERSION: an 82077AA / enhanced controller
            case 0x0e when c == 0x0e:                            // DUMPREG
                Result(_pcn[0], _pcn[1], _pcn[2], _pcn[3], _srtHut, _hltNd, 0, (byte)((_lock ? 0x80 : 0) | (_perp & 0x7f)), _config, _pretrk);
                return;
            case 0x12 when c == 0x12: _perp = _cmd[1]; _phase = Phase.Command; return;   // PERPENDICULAR MODE
            case 0x13 when c == 0x13: _config = _cmd[2]; _pretrk = _cmd[3]; _phase = Phase.Command; return;   // CONFIGURE
            case 0x14 when (c & 0x7f) == 0x14: _lock = (c & 0x80) != 0; Result((byte)(_lock ? 0x10 : 0)); return;   // LOCK
            case 0x05: case 0x06: case 0x09: case 0x0c: case 0x0a: case 0x0d:
                StartTransfer(); return;
        }
        Result(0x80);                                            // invalid command
    }

    private void StartSeek(int d, int cyl, bool recal)
    {
        _phase = Phase.Command;
        if (!DrivePresent(d))
        {
            // no drive: no track-0 signal / no step, an equipment check after the step count runs out
            _pendSt0[d] = (byte)(0x70 | d); _pendMask |= (byte)(1 << d);
            _seekBusy |= (byte)(1 << d); Schedule(2, d, 2000);
            return;
        }
        var m = Media(d);
        int from = _pcn[d];
        if (recal) cyl = 0;
        _pcn[d] = (byte)cyl;
        // a step pulse with a disk in the drive clears the disk-change line
        if (m != null && (cyl != from || recal)) _changed[d] = false;
        _pendSt0[d] = (byte)(0x20 | d | (_cmd[1] & 4)); _pendMask |= (byte)(1 << d);
        _seekBusy |= (byte)(1 << d);
        Schedule(2, d, 200 + 30L * Math.Abs(cyl - from));
    }

    private bool RateFits(DiskImage m) => m.Sectors switch
    {
        9 => _rate is 1 or 2, 15 or 18 => _rate == 0, 36 => _rate == 3, _ => _rate == 0,
    };

    private void StartTransfer()
    {
        int d = _cmd[1] & 3;
        _phase = Phase.Exec;
        var m = Media(d);
        if (m == null || !RateFits(m))
        {
            // no medium (or the wrong data rate: no address mark found): with no disk there is no index pulse, the command never ends;
            // a disk at another rate fails after two revolutions
            if (m == null) { _due = long.MaxValue; _dueKind = 0; return; }
            Schedule(3, d, 400_000); return;
        }
        // implied seek (CONFIGURE EIS): the head goes to the command's cylinder first
        if ((_config & 0x40) != 0 && (_cmd[0] & 0x1f) != 0x0a && (_cmd[0] & 0x1f) != 0x0d && _cmd[2] != _pcn[d])
        {
            if (_cmd[2] != _pcn[d] && m != null) _changed[d] = false;
            _pcn[d] = _cmd[2];
        }
        Schedule(3, d, (_cmd[0] & 0x1f) == 0x0a ? 200 : 1000);
    }

    private void Done(byte st0, byte st1, byte st2, byte c, byte h, byte r, byte n)
    {
        Result(st0, st1, st2, c, h, r, n);
        SetIrq(true);
    }

    private void FinishTransfer()
    {
        byte op = (byte)(_cmd[0] & 0x1f);
        int d = _cmd[1] & 3, head = (_cmd[1] >> 2) & 1;
        byte st0Base = (byte)(d | (head << 2));
        var m = Media(d);
        if (m == null) return;                                   // (the disk went away: still waiting)
        if (!RateFits(m)) { Done((byte)(0x40 | st0Base), 0x01, 0x00, _pcn[d], (byte)head, 1, 2); return; }
        int cyl = _pcn[d];
        if (op == 0x0a)                                          // READ ID: the next sector header under the head
        {
            if (cyl >= m.Cylinders || head >= m.Heads) { Done((byte)(0x40 | st0Base), 0x01, 0x00, (byte)cyl, (byte)head, 1, 2); return; }
            Done(st0Base, 0, 0, (byte)cyl, (byte)head, 1, 2);
            return;
        }
        if (op == 0x0d) { Format(m, d, head, st0Base); return; }
        bool write = op is 0x05 or 0x09;
        bool mt = (_cmd[0] & 0x80) != 0;
        byte C = _cmd[2], H = _cmd[3], R = _cmd[4], N = _cmd[5], eot = _cmd[6];
        if (C != cyl || cyl >= m.Cylinders || head >= m.Heads || N != 2 || R == 0 || R > m.Sectors)
        {
            // the sector is not on this track: no data (and the wrong cylinder seen in the IDs)
            byte st2 = (byte)(C != cyl ? (C == 0xff ? 0x02 : 0x10) : 0);
            Done((byte)(0x40 | st0Base), 0x04, st2, C, H, R, N);
            return;
        }
        var buf = new byte[512];
        int sector = R, hd = head;
        bool tc = false, ok = true;
        byte st1 = 0;
        while (true)
        {
            long lba = ((long)cyl * m.Heads + hd) * m.Sectors + sector - 1;
            if (write)
            {
                int got = _dma.Read(512, buf, 0);
                if (got == 0) { ok = false; st1 = 0x10; break; }   // overrun: DMA not ready
                if (got < 512) Array.Clear(buf, got, 512 - got);
                m.Write(lba, 1, buf);
            }
            else
            {
                m.Read(lba, 1, buf);
                int got = _dma.Write(512, buf, 0);
                if (got == 0) { ok = false; st1 = 0x10; break; }
            }
            tc = _dma.Masked;
            // advance to the next sector (the result's C/H/R are the next sector after the last one moved)
            if (sector >= eot || sector >= m.Sectors)
            {
                sector = 1;
                if (mt && hd == 0 && m.Heads > 1) { hd = 1; H = (byte)(H | 1); if (tc) break; continue; }
                C++; if (mt) { hd = 0; H = (byte)(H & ~1); }
                if (!tc) { ok = false; st1 = 0x80; }             // end of cylinder before the terminal count
                break;
            }
            sector++;
            if (tc) break;
        }
        if (!ok)
        {
            Done((byte)(0x40 | (hd << 2) | d), st1, 0, C, H, (byte)sector, N);
            return;
        }
        Done((byte)((hd << 2) | d), 0, 0, C, H, (byte)sector, N);
    }

    private void Format(DiskImage m, int d, int head, byte st0Base)
    {
        int n = _cmd[2], sc = _cmd[3]; byte fill = _cmd[5];
        int cyl = _pcn[d];
        var id = new byte[4];
        var data = new byte[512];
        Array.Fill(data, fill);
        for (int i = 0; i < sc; i++)
        {
            if (_dma.Read(4, id, 0) < 4) { Done((byte)(0x40 | st0Base), 0x10, 0, (byte)cyl, (byte)head, 1, (byte)n); return; }
            int c = id[0], h = id[1], r = id[2];
            if (n == 2 && c < m.Cylinders && h < m.Heads && r >= 1 && r <= m.Sectors)
                m.Write(((long)c * m.Heads + h) * m.Sectors + r - 1, 1, data);
        }
        Done(st0Base, 0, 0, (byte)cyl, (byte)head, (byte)sc, (byte)n);
    }

    // ================= state =================

    public void Save(BinaryWriter w)
    {
        w.Write(1);   // block version
        w.Write(_dor); w.Write(_rate); w.Write(_srtHut); w.Write(_hltNd); w.Write(_config); w.Write(_pretrk); w.Write(_lock); w.Write(_perp);
        w.Write(_pcn); for (int i = 0; i < 4; i++) w.Write(_changed[i]);
        w.Write((byte)_phase); w.Write(_cmd); w.Write(_cmdLen); w.Write(_cmdPos); w.Write(_res); w.Write(_resLen); w.Write(_resPos);
        w.Write(_seekBusy); w.Write(_pendSt0); w.Write(_pendMask); w.Write(_irqLine);
        w.Write(_due); w.Write(_dueKind); w.Write(_dueDrive);
    }

    public void Load(BinaryReader r)
    {
        int ver = r.ReadInt32();
        if (ver != 1) throw new InvalidDataException($"floppy controller state version {ver} is not known");
        _dor = r.ReadByte(); _rate = r.ReadByte(); _srtHut = r.ReadByte(); _hltNd = r.ReadByte(); _config = r.ReadByte(); _pretrk = r.ReadByte(); _lock = r.ReadBoolean(); _perp = r.ReadByte();
        r.Read(_pcn, 0, 4); for (int i = 0; i < 4; i++) _changed[i] = r.ReadBoolean();
        _phase = (Phase)r.ReadByte(); r.Read(_cmd, 0, _cmd.Length); _cmdLen = r.ReadInt32(); _cmdPos = r.ReadInt32(); r.Read(_res, 0, _res.Length); _resLen = r.ReadInt32(); _resPos = r.ReadInt32();
        _seekBusy = r.ReadByte(); r.Read(_pendSt0, 0, 4); _pendMask = r.ReadByte(); _irqLine = r.ReadBoolean();
        _due = r.ReadInt64(); _dueKind = r.ReadByte(); _dueDrive = r.ReadByte();
        // the disks were restored with the state: what is in the drives now is what was there (no spurious change)
        _seen[0] = _disk(0) is { IsFloppy: true } a ? a : null; _seen[1] = _disk(1) is { IsFloppy: true } b ? b : null;
        _irq(_irqLine && (_dor & 0x08) != 0);
    }
}
