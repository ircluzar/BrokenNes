// The x86 PC's IDE / ATA controller (docs/projects/x86-modular-pc.md, M3): two channels (primary 1F0h-1F7h + 3F6h, IRQ 14; secondary
// 170h-177h + 376h, IRQ 15), each with a master and a slave. Hard disks speak ATA (PIO, CHS and LBA28, IDENTIFY, READ/WRITE SECTORS and
// MULTIPLE, SET FEATURES, INITIALIZE DEVICE PARAMETERS...); a CD-ROM speaks ATAPI (PACKET + the SCSI/MMC commands a CD driver and
// Windows 98's ESDI_506/CDFS need, over an ISO image). Written for BrokenNes from the ATA-4 / ATAPI (SFF-8020i) specifications; no code
// from another emulator. Transfers are instant (data is ready the moment a command is accepted): deterministic and fast.

using System;
using System.IO;
using System.Text;

namespace NesEmulator.X86;

public sealed class AtaChannel
{
    private const byte BSY = 0x80, DRDY = 0x40, DSC = 0x10, DRQ = 0x08, ERR = 0x01;
    private const byte ABRT = 0x04;

    public readonly int Irq;
    private readonly Pic8259 _pic;
    /// <summary>The two devices: a hard disk (DiskImage) or a CD-ROM (CdImage), or nothing. Set by the board.</summary>
    public readonly DiskImage?[] Disk = new DiskImage?[2];
    public readonly CdImage?[] Cd = new CdImage?[2];
    public readonly bool[] CdPresent = new bool[2];
    private readonly bool[] _mediaChanged = new bool[2];
    /// <summary>A disc went in or out while the machine runs: the drive answers its next command with UNIT ATTENTION (not saved: a state reload keeps the disc it is given).</summary>
    public void MediaChanged(int d) => _mediaChanged[d] = true;   // an ATAPI drive is wired even without a disc in it

    // task file (one copy: the ATA registers are shared by both devices; per-device CHS translation below)
    private byte _feature, _count, _sector, _cylLo, _cylHi, _drvHead = 0xa0, _status, _error, _devCtl;
    private readonly int[] _lHeads = { 16, 16 }, _lSpt = { 63, 63 };
    private readonly int[] _multiple = { 16, 16 };
    private bool _irqOut;

    // the data buffer and what happens when it runs dry
    private readonly byte[] _buf = new byte[65536 + 2048];
    private int _pos, _len;
    private enum Next { None, ReadMore, WriteMore, Packet, AtapiData }
    private Next _next;
    private long _lba;          // next sector for multi-block transfers
    private int _left;          // sectors (ATA) or bytes (ATAPI) still to transfer after the current block
    private int _blockSectors;  // ATA: sectors per DRQ block
    private byte _cmd;
    // ATAPI sense
    private byte _senseKey, _asc;
    private readonly byte[] _atapiData = new byte[65536 * 2];
    private int _atapiPos, _atapiLen;

    public AtaChannel(int irq, Pic8259 pic) { Irq = irq; _pic = pic; }

    private int Dev => (_drvHead >> 4) & 1;
    private bool Present(int d) => Disk[d] != null || CdPresent[d];
    private bool IsCd(int d) => CdPresent[d];

    public void Reset()
    {
        _feature = 0; _drvHead = 0xa0; _devCtl = 0; _next = Next.None; _pos = _len = 0; _error = 1;
        for (int d = 0; d < 2; d++) { _multiple[d] = 16; if (Disk[d] is { } k) { _lHeads[d] = k.Heads; _lSpt[d] = k.Sectors; } }
        Signature();
        SetIrq(false);
        ClearAudio();
        ClearBm();
    }

    private void Signature()
    {
        _count = 1; _sector = 1;
        if (IsCd(Dev)) { _cylLo = 0x14; _cylHi = 0xeb; _status = 0; }
        else { _cylLo = 0; _cylHi = 0; _status = Present(Dev) ? (byte)(DRDY | DSC) : (byte)0; }
    }

    private void SetIrq(bool on)
    {
        bool level = on && (_devCtl & 2) == 0;
        if (level == _irqOut) return;
        _irqOut = level;
        _pic.SetIrq(Irq, level ? 1 : 0);
    }
    private void Interrupt() { _irqOut = false; SetIrq(true); }

    // ================= ports =================

    /// <summary>reg 0-7: the task file at base+reg; reg 8: the alternate status / device control.</summary>
    public int Read(int reg, bool wide)
    {
        if (reg == 0) return ReadData(wide);
        if (!Present(0) && !Present(1)) return 0xff;   // an empty channel floats high
        if (!Present(Dev) && reg == 7) return 0;
        switch (reg)
        {
            case 1: return _error;
            case 2: return _count;
            case 3: return _sector;
            case 4: return _cylLo;
            case 5: return _cylHi;
            case 6: return _drvHead | 0xa0;
            case 7: SetIrq(false); return _status;
            case 8: return Present(Dev) ? _status : 0;
        }
        return 0xff;
    }

    public void Write(int reg, int v, bool wide)
    {
        if (reg == 0) { WriteData(v, wide); return; }
        byte b = (byte)v;
        switch (reg)
        {
            case 1: _feature = b; break;
            case 2: _count = b; break;
            case 3: _sector = b; break;
            case 4: _cylLo = b; break;
            case 5: _cylHi = b; break;
            case 6: _drvHead = b; if (!IsCd(Dev) && (_status & BSY) == 0) _status = Present(Dev) ? (byte)(_status | DRDY | DSC) : (byte)0; break;
            case 7: if (Present(Dev)) Command(b); break;
            case 8:
                if ((b & 4) != 0 && (_devCtl & 4) == 0) { _next = Next.None; _pos = _len = 0; _status = BSY; }
                else if ((b & 4) == 0 && (_devCtl & 4) != 0) { _error = 1; _drvHead = 0xa0; Signature(); }   // soft reset done: device 0 selected, signatures in place
                _devCtl = b;
                if ((b & 2) != 0) SetIrq(false);
                break;
        }
    }

    private int ReadData(bool wide)
    {
        if ((_status & DRQ) == 0 || _next == Next.Packet || _next == Next.WriteMore) return wide ? 0xffff : 0xff;
        int v = _buf[_pos] | (wide ? _buf[_pos + 1] << 8 : 0);
        _pos += wide ? 2 : 1;
        if (_pos >= _len) BlockDone();
        return v;
    }

    private void WriteData(int v, bool wide)
    {
        if ((_status & DRQ) == 0 || (_next != Next.Packet && _next != Next.WriteMore)) return;
        _buf[_pos] = (byte)v; if (wide) _buf[_pos + 1] = (byte)(v >> 8);
        _pos += wide ? 2 : 1;
        if (_pos >= _len) BlockDone();
    }

    private void BlockDone()
    {
        _status &= unchecked((byte)~DRQ);
        _pos = _len = 0;
        switch (_next)
        {
            case Next.ReadMore:
                if (_left > 0) { LoadReadBlock(); Interrupt(); }
                else { _next = Next.None; _status = DRDY | DSC; }
                return;
            case Next.WriteMore:
            {
                var d = Disk[Dev]!;
                int n = _blockSectors;
                if (!d.Write(_lba, n, _buf.AsSpan(0, n * 512))) { Abort(0x10); Interrupt(); return; }
                _lba += n; _left -= n; StoreLba(_lba - 1);
                if (_left > 0) { _blockSectors = Math.Min(_left, BlockFor(_cmd)); _len = _blockSectors * 512; _status = DRDY | DSC | DRQ; }
                else { _next = Next.None; _status = DRDY | DSC; }
                Interrupt();
                return;
            }
            case Next.Packet: _next = Next.None; Packet(); return;
            case Next.AtapiData:
                if (_atapiPos < _atapiLen) AtapiSendChunk();
                else AtapiComplete();
                return;
        }
        _next = Next.None;
        _status = (byte)(IsCd(Dev) ? DRDY : DRDY | DSC);
    }

    // ================= ATA commands =================

    private int BlockFor(byte cmd) => cmd is 0xc4 or 0xc5 ? _multiple[Dev] : 1;

    private void Abort(byte err = ABRT) { _error = err; _status = DRDY | DSC | ERR; _next = Next.None; _pos = _len = 0; }

    private void Command(byte cmd)
    {
        _cmd = cmd;
        _error = 0;
        if (IsCd(Dev)) { AtapiCommand(cmd); return; }
        var d = Disk[Dev]!;
        switch (cmd)
        {
            case 0xec: Identify(d); Interrupt(); return;                         // IDENTIFY DEVICE
            case 0x20: case 0x21: case 0xc4:                                    // READ SECTORS (w/o retry), READ MULTIPLE
            case 0x30: case 0x31: case 0xc5:                                    // WRITE SECTORS, WRITE MULTIPLE
            {
                bool write = cmd is 0x30 or 0x31 or 0xc5;
                if (!TaskLba(d, out _lba)) { Abort(0x10); Interrupt(); return; }
                _left = _count == 0 ? 256 : _count;
                if (_lba + _left > d.TotalSectors) { Abort(0x10); Interrupt(); return; }
                if (write)
                {
                    _blockSectors = Math.Min(_left, BlockFor(cmd));
                    _next = Next.WriteMore; _pos = 0; _len = _blockSectors * 512; _status = DRDY | DSC | DRQ;   // no interrupt before the first block
                    return;
                }
                _next = Next.ReadMore; LoadReadBlock(); Interrupt(); return;
            }
            case 0xc8: case 0xc9: case 0xca: case 0xcb:                         // READ DMA / WRITE DMA (bus master)
            {
                if (DmaToMem == null) { Abort(); Interrupt(); return; }
                if (!TaskLba(d, out _lba)) { Abort(0x10); Interrupt(); return; }
                _left = _count == 0 ? 256 : _count;
                if (_lba + _left > d.TotalSectors) { Abort(0x10); Interrupt(); return; }
                DmaArm(cmd >= 0xca, false); return;
            }
            case 0x40: case 0x41:                                               // READ VERIFY SECTORS
            {
                if (!TaskLba(d, out long l)) { Abort(0x10); Interrupt(); return; }
                int n = _count == 0 ? 256 : _count;
                if (l + n > d.TotalSectors) { Abort(0x10); Interrupt(); return; }
                StoreLba(l + n - 1); _status = DRDY | DSC; Interrupt(); return;
            }
            case 0x91:                                                          // INITIALIZE DEVICE PARAMETERS
                _lHeads[Dev] = (_drvHead & 15) + 1; _lSpt[Dev] = Math.Max(1, (int)_count);
                _status = DRDY | DSC; Interrupt(); return;
            case 0xc6:                                                          // SET MULTIPLE MODE
                if (_count is 0 or > 16 || (_count & (_count - 1)) != 0) { Abort(); Interrupt(); return; }
                _multiple[Dev] = _count; _status = DRDY | DSC; Interrupt(); return;
            case 0x10: case >= 0x11 and <= 0x1f:                                // RECALIBRATE
            case 0x70:                                                          // SEEK
            case 0xef:                                                          // SET FEATURES (transfer modes, caches: accepted)
            case 0xe7: case 0xea:                                               // FLUSH CACHE
            case 0xe0: case 0xe1: case 0xe2: case 0xe3: case 0xe5: case 0xe6:   // power management
            case 0x94: case 0x95: case 0x96: case 0x97: case 0x98: case 0x99:
                _count = cmd is 0xe5 or 0x98 ? (byte)0xff : _count;
                _status = DRDY | DSC; Interrupt(); return;
            case 0x90:                                                          // EXECUTE DEVICE DIAGNOSTIC
                _error = 1; Signature(); Interrupt(); return;
            case 0x08: case 0xa1: case 0xa0: Abort(); Interrupt(); return;      // ATAPI commands on a disk
            default: Abort(); Interrupt(); return;
        }
    }

    private bool TaskLba(DiskImage d, out long lba)
    {
        if ((_drvHead & 0x40) != 0) { lba = ((long)(_drvHead & 15) << 24) | ((long)_cylHi << 16) | ((long)_cylLo << 8) | _sector; return true; }
        int cyl = _cylLo | (_cylHi << 8), head = _drvHead & 15, sec = _sector;
        int h = _lHeads[Dev], s = _lSpt[Dev];
        lba = 0;
        if (sec == 0 || sec > s || head >= h) return false;
        lba = ((long)cyl * h + head) * s + sec - 1;
        return true;
    }

    private void StoreLba(long lba)
    {
        if ((_drvHead & 0x40) != 0) { _sector = (byte)lba; _cylLo = (byte)(lba >> 8); _cylHi = (byte)(lba >> 16); _drvHead = (byte)((_drvHead & 0xf0) | ((lba >> 24) & 15)); return; }
        int h = _lHeads[Dev], s = _lSpt[Dev];
        long cyl = lba / (h * s); int rem = (int)(lba % (h * s));
        _cylLo = (byte)cyl; _cylHi = (byte)(cyl >> 8); _drvHead = (byte)((_drvHead & 0xf0) | (rem / s)); _sector = (byte)(rem % s + 1);
    }

    private void LoadReadBlock()
    {
        var d = Disk[Dev]!;
        int n = Math.Min(_left, BlockFor(_cmd));
        d.Read(_lba, n, _buf.AsSpan(0, n * 512));
        _lba += n; _left -= n; StoreLba(_lba - 1);
        _count = (byte)_left;
        _pos = 0; _len = n * 512; _status = DRDY | DSC | DRQ;
    }

    private static void Word(byte[] b, int w, int v) { b[w * 2] = (byte)v; b[w * 2 + 1] = (byte)(v >> 8); }
    private static void Str(byte[] b, int w, int words, string s)
    {
        s = s.PadRight(words * 2);
        for (int i = 0; i < words; i++) { b[(w + i) * 2] = (byte)s[i * 2 + 1]; b[(w + i) * 2 + 1] = (byte)s[i * 2]; }   // ATA strings: byte-swapped words
    }

    private void Identify(DiskImage d)
    {
        var b = _buf; Array.Clear(b, 0, 512);
        long total = d.TotalSectors;
        int cyls = (int)Math.Min(16383, total / (16 * 63));
        Word(b, 0, 0x0040);
        Word(b, 1, Math.Max(1, cyls)); Word(b, 3, 16); Word(b, 6, 63);
        Str(b, 10, 10, $"BN{Dev}{total:X8}");
        Str(b, 23, 4, "1.0");
        Str(b, 27, 20, "BrokenNes ATA disk");
        Word(b, 47, 0x8010);                 // READ/WRITE MULTIPLE up to 16
        Word(b, 49, 0x0300);                 // LBA + DMA supported
        Word(b, 63, 0x0407);                 // multiword DMA 0-2, mode 2 selected
        Word(b, 51, 0x0200);                 // PIO mode 2 timing
        Word(b, 53, 0x0003);                 // words 54-58 and 64-70 valid
        int lc = Math.Min(65535, (int)Math.Min(int.MaxValue, total / (_lHeads[Dev] * _lSpt[Dev])));
        Word(b, 54, lc); Word(b, 55, _lHeads[Dev]); Word(b, 56, _lSpt[Dev]);
        long cur = (long)lc * _lHeads[Dev] * _lSpt[Dev];
        Word(b, 57, (int)cur); Word(b, 58, (int)(cur >> 16));
        Word(b, 59, 0x0100 | _multiple[Dev]);
        long lba28 = Math.Min(total, 0x0fffffff);
        Word(b, 60, (int)lba28); Word(b, 61, (int)(lba28 >> 16));
        Word(b, 64, 0x0003);                 // PIO modes 3 and 4
        Word(b, 65, 120); Word(b, 66, 120); Word(b, 67, 120); Word(b, 68, 120);
        Word(b, 80, 0x001e);                 // ATA-1..4
        Word(b, 82, 0x4000); Word(b, 83, 0x4000); Word(b, 84, 0x4000); Word(b, 85, 0x4000); Word(b, 86, 0x4000); Word(b, 87, 0x4000);
        _pos = 0; _len = 512; _next = Next.None; _status = DRDY | DSC | DRQ;
    }

    // ================= ATAPI =================

    private void AtapiCommand(byte cmd)
    {
        switch (cmd)
        {
            case 0xa1:                                                          // IDENTIFY PACKET DEVICE
            {
                var b = _buf; Array.Clear(b, 0, 512);
                Word(b, 0, 0x85c0);              // ATAPI, CD-ROM, removable, 12-byte packets, DRQ within 50 us
                Str(b, 10, 10, $"BNCD{Dev}");
                Str(b, 23, 4, "1.0");
                Str(b, 27, 20, "BrokenNes ATAPI CD-ROM");
                Word(b, 49, 0x0300); Word(b, 53, 0x0003); Word(b, 63, 0x0407); Word(b, 64, 0x0003);
                Word(b, 65, 120); Word(b, 66, 120); Word(b, 67, 120); Word(b, 68, 120);
                Word(b, 80, 0x001e);
                _pos = 0; _len = 512; _next = Next.None; _status = DRDY | DSC | DRQ; Interrupt(); return;
            }
            case 0xa0:                                                          // PACKET
                _count = 1;                      // CoD = 1, IO = 0: send the command packet
                _pos = 0; _len = 12; _next = Next.Packet; _status = DRDY | DRQ;   // no interrupt: the host writes the packet when DRQ shows
                return;
            case 0x08:                                                          // DEVICE RESET
                _error = 1; Signature(); _status = 0; return;
            case 0x90: _error = 1; Signature(); Interrupt(); return;
            case 0xec:                                                          // IDENTIFY DEVICE: abort with the ATAPI signature
                _error = ABRT; Signature(); _status = DRDY | ERR; Interrupt(); return;
            case 0xef: case 0xe0: case 0xe1: case 0xe5: case 0xe7: _status = DRDY; Interrupt(); return;
            default: _error = ABRT; _status = DRDY | ERR; Interrupt(); return;
        }
    }

    private void Sense(byte key, byte asc) { _senseKey = key; _asc = asc; }

    private void AtapiFail(byte key, byte asc)
    {
        Sense(key, asc);
        _error = (byte)(key << 4 | ABRT);
        _count = 3;                          // CoD = 1, IO = 1: status
        _status = DRDY | ERR;
        Interrupt();
    }

    private void AtapiComplete()
    {
        _next = Next.None;
        _count = 3; _error = 0; _status = DRDY;
        Sense(0, 0);
        Interrupt();
    }

    private void AtapiReply(int length, int alloc)
    {
        _atapiLen = Math.Min(length, alloc); _atapiPos = 0;
        if (_atapiLen == 0) { AtapiComplete(); return; }
        if ((_feature & 1) != 0 && DmaToMem != null) { DmaArm(false, true); return; }   // the packet asked for DMA
        AtapiSendChunk();
    }

    private void AtapiSendChunk()
    {
        int limit = _cylLo | (_cylHi << 8);
        if (limit == 0 || limit == 0xffff) limit = 0xfffe;
        limit &= ~1;
        int n = Math.Min(limit, _atapiLen - _atapiPos);
        Array.Copy(_atapiData, _atapiPos, _buf, 0, n);
        _atapiPos += n;
        _cylLo = (byte)n; _cylHi = (byte)(n >> 8);
        _count = 2;                          // CoD = 0, IO = 1: data to the host
        _pos = 0; _len = n; _next = Next.AtapiData; _status = DRDY | DRQ;
        Interrupt();
    }


    // ================= PIIX4 bus-master DMA (X86-02): the PRD table is walked in guest memory, the whole transfer happens when both sides are ready =================

    /// <summary>Guest physical memory for bus-master DMA (set by the board): copy n bytes to / from phys.</summary>
    public Action<uint, byte[], int, int>? DmaToMem, DmaFromMem;
    private byte _bmCmd, _bmStatus; private uint _bmPrd;
    private bool _dmaPending, _dmaToDisk, _dmaAtapi;
    public long DmaTransfers, DmaBytes;   // diagnostics (not saved)

    /// <summary>Bus-master register <paramref name="reg"/> (0 command, 2 status, 4-7 PRD table address).</summary>
    public byte BmRead(int reg) => reg switch
    {
        0 => _bmCmd, 2 => _bmStatus, 4 => (byte)_bmPrd, 5 => (byte)(_bmPrd >> 8), 6 => (byte)(_bmPrd >> 16), 7 => (byte)(_bmPrd >> 24), _ => 0,
    };

    public void BmWrite(int reg, byte v)
    {
        switch (reg)
        {
            case 0:
            {
                bool start = (v & 1) != 0, was = (_bmCmd & 1) != 0;
                _bmCmd = (byte)(v & 0x09);
                if (start && !was) { _bmStatus |= 1; if (_dmaPending) RunDma(); }
                else if (!start) _bmStatus &= unchecked((byte)~1);
                break;
            }
            case 2: _bmStatus = (byte)((_bmStatus & 0x01) | (_bmStatus & 0x06 & ~v) | (v & 0x60)); break;   // bits 1-2 write-1-to-clear, 5-6 drive DMA capable
            case 4: _bmPrd = (_bmPrd & 0xffffff00) | (uint)(v & 0xfc); break;
            case 5: _bmPrd = (_bmPrd & 0xffff00ff) | (uint)v << 8; break;
            case 6: _bmPrd = (_bmPrd & 0xff00ffff) | (uint)v << 16; break;
            case 7: _bmPrd = (_bmPrd & 0x00ffffff) | (uint)v << 24; break;
        }
    }

    private void DmaArm(bool toDisk, bool atapi)
    {
        _dmaPending = true; _dmaToDisk = toDisk; _dmaAtapi = atapi; _next = Next.None; _pos = _len = 0;
        _status = (byte)(atapi ? DRDY | DRQ : DRDY | DSC | DRQ);
        if (atapi) _count = 2;
        if ((_bmCmd & 1) != 0) RunDma();
    }

    private void RunDma()
    {
        _dmaPending = false;
        byte[] data; int total;
        if (_dmaAtapi) { data = _atapiData; total = _atapiLen; }
        else { total = _left * 512; data = new byte[total]; if (!_dmaToDisk) Disk[Dev]!.Read(_lba, _left, data); }
        int done = 0; uint prd = _bmPrd; var e = new byte[8]; bool err = false;
        for (int guard = 0; guard < 8192 && done < total; guard++)
        {
            DmaFromMem!(prd, e, 0, 8);
            uint addr = BitConverter.ToUInt32(e, 0) & 0xfffffffe; int cnt = BitConverter.ToUInt16(e, 4) & 0xfffe; if (cnt == 0) cnt = 65536;
            bool eot = (e[7] & 0x80) != 0;
            int n = Math.Min(cnt, total - done);
            if (_dmaToDisk) DmaFromMem(addr, data, done, n); else DmaToMem!(addr, data, done, n);
            done += n; prd += 8;
            if (eot) break;
        }
        if (done < total) err = true;                               // the PRD table was shorter than the transfer
        DmaTransfers++; DmaBytes += done;
        if (_dmaAtapi) { _atapiPos = _atapiLen; AtapiComplete(); }
        else
        {
            if (_dmaToDisk && !Disk[Dev]!.Write(_lba, _left, data)) { Abort(0x10); }
            else { _lba += _left; _left = 0; StoreLba(_lba - 1); _count = 0; _status = DRDY | DSC; }
            Interrupt();
        }
        _bmStatus = (byte)((_bmStatus & ~1) | 4 | (err ? 2 : 0));
    }

    /// <summary>The bus-master block (the board saves it only once a driver has touched the engine, so older states stay byte for byte).</summary>
    public bool BmActive => _bmCmd != 0 || _bmStatus != 0 || _bmPrd != 0 || _dmaPending;
    public void SaveBm(BinaryWriter w) { w.Write(_bmCmd); w.Write(_bmStatus); w.Write(_bmPrd); w.Write(_dmaPending); w.Write(_dmaToDisk); w.Write(_dmaAtapi); }
    public void LoadBm(BinaryReader r) { _bmCmd = r.ReadByte(); _bmStatus = r.ReadByte(); _bmPrd = r.ReadUInt32(); _dmaPending = r.ReadBoolean(); _dmaToDisk = r.ReadBoolean(); _dmaAtapi = r.ReadBoolean(); }
    public void ClearBm() { _bmCmd = _bmStatus = 0; _bmPrd = 0; _dmaPending = false; }

    // ================= CD audio (PLAY AUDIO): the play position follows the machine clock, 588 stereo samples per sector =================

    /// <summary>The machine clock in PIT ticks (set by the board); CD audio positions are derived from it, so they are deterministic.</summary>
    public Func<long>? Clock;
    private byte _aState;              // 0 none, 1 playing, 2 paused, 3 completed
    private long _aLba, _aEnd, _aT0;   // playing: _aLba is the sector at sample _aT0; paused: the sector it stopped at
    private long _mixIdx = long.MinValue;
    private readonly byte[] _aSector = new byte[2352]; private long _aSectorLba = -1; private CdImage? _aSectorCd;
    public const int AudioRate = 44100;

    private long SampleNow() => Clock == null ? 0 : (long)((Int128)Clock() * AudioRate / Pit8254.Freq);

    private long AudioLba() => _aState != 1 ? _aLba : Math.Min(_aEnd, _aLba + (SampleNow() - _aT0) / 588);

    private void AudioUpdate() { if (_aState == 1 && AudioLba() >= _aEnd) { _aLba = _aEnd; _aState = 3; } }
    private void AudioStop(byte state) { if (_aState is 1 or 2) _aLba = AudioLba(); _aState = state; }

    /// <summary>True while CD audio plays (diagnostics).</summary>
    public bool AudioPlaying { get { AudioUpdate(); return _aState == 1; } }

    /// <summary>Adds the CD audio of the time up to now onto <paramref name="buffer"/> (44.1 kHz stereo interleaved, <paramref name="count"/> shorts, saturating).
    /// It reads the play position the guest sees; nothing here is machine state.</summary>
    public void MixCdAudio(short[] buffer, int count)
    {
        int frames = count / 2;
        long now = SampleNow();
        if (_mixIdx == long.MinValue || _mixIdx > now || _mixIdx < now - frames - AudioRate / 2) _mixIdx = now - frames;
        var cd = Cd[0] ?? Cd[1];
        if (cd != null && _aState is 1 or 3)
        {
            for (int i = 0; i < frames; i++)
            {
                long rel = _mixIdx + i - _aT0; if (rel < 0) continue;
                long lba = _aLba + rel / 588; if (lba >= _aEnd) break;
                if (lba != _aSectorLba || cd != _aSectorCd) { cd.ReadRaw(lba, _aSector, 0); _aSectorLba = lba; _aSectorCd = cd; }
                int at = (int)(rel % 588) * 4;
                short l = (short)(_aSector[at] | _aSector[at + 1] << 8), r = (short)(_aSector[at + 2] | _aSector[at + 3] << 8);
                buffer[2 * i] = (short)Math.Clamp(buffer[2 * i] + l, -32768, 32767);
                buffer[2 * i + 1] = (short)Math.Clamp(buffer[2 * i + 1] + r, -32768, 32767);
            }
        }
        _mixIdx += frames;
    }

    /// <summary>The audio player's state (the board writes it as its own block only while it is not idle, so other states stay byte for byte).</summary>
    public bool AudioActive => _aState != 0;
    public void SaveAudio(BinaryWriter w) { w.Write(_aState); w.Write(_aLba); w.Write(_aEnd); w.Write(_aT0); }
    public void LoadAudio(BinaryReader r) { _aState = r.ReadByte(); _aLba = r.ReadInt64(); _aEnd = r.ReadInt64(); _aT0 = r.ReadInt64(); _mixIdx = long.MinValue; }
    public void ClearAudio() { _aState = 0; _aLba = _aEnd = _aT0 = 0; _mixIdx = long.MinValue; }

    private static void Be32(byte[] b, int o, long v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static void Msf(byte[] b, int o, long lba) { lba += 150; b[o] = 0; b[o + 1] = (byte)(lba / 4500); b[o + 2] = (byte)(lba / 75 % 60); b[o + 3] = (byte)(lba % 75); }

    private void Packet()
    {
        var p = new byte[12]; Array.Copy(_buf, p, 12);
        var cd = Cd[Dev];
        var o = _atapiData;
        int alloc16 = p[7] << 8 | p[8];
        bool needsDisc = p[0] is 0x00 or 0x25 or 0x28 or 0xa8 or 0x43 or 0xbe or 0xb9 or 0x2b or 0x45 or 0x47 or 0xa5;
        if (_mediaChanged[Dev] && p[0] is not (0x03 or 0x12 or 0x4a or 0xbd)) { _mediaChanged[Dev] = false; AtapiFail(6, 0x28); return; }   // UNIT ATTENTION: medium may have changed
        if (needsDisc && cd == null) { AtapiFail(2, 0x3a); return; }   // NOT READY, medium not present
        switch (p[0])
        {
            case 0x00: AtapiComplete(); return;                                 // TEST UNIT READY
            case 0x03:                                                          // REQUEST SENSE
            {
                Array.Clear(o, 0, 18);
                o[0] = 0x70; o[2] = _senseKey; o[7] = 10; o[12] = _asc;
                AtapiReply(18, p[4]); return;   // the sense clears when the reply completes
            }
            case 0x12:                                                          // INQUIRY
            {
                Array.Clear(o, 0, 36);
                o[0] = 0x05; o[1] = 0x80; o[2] = 0x00; o[3] = 0x21; o[4] = 31;
                Encoding.ASCII.GetBytes("BROKENNES".PadRight(8)[..8]).CopyTo(o, 8);
                Encoding.ASCII.GetBytes("ATAPI CD-ROM".PadRight(16)).CopyTo(o, 16);
                Encoding.ASCII.GetBytes("1.0 ").CopyTo(o, 32);
                AtapiReply(36, p[4]); return;
            }
            case 0x1b:                                                          // START/STOP UNIT (a stop or an eject ends audio play)
                if ((p[4] & 1) == 0) AudioStop(0);
                AtapiComplete(); return;
            case 0x1e: case 0x2b: case 0x55: case 0xbb: AtapiComplete(); return; // PREVENT, SEEK, MODE SELECT, SET SPEED
            case 0x4e: AudioStop(0); AtapiComplete(); return;                   // STOP PLAY / SCAN
            case 0x25:                                                          // READ CAPACITY
                Be32(o, 0, cd!.Sectors - 1); Be32(o, 4, 2048); AtapiReply(8, 8); return;
            case 0x28: case 0xa8:                                               // READ (10) / READ (12)
            {
                long lba = (uint)(p[2] << 24 | p[3] << 16 | p[4] << 8 | p[5]);
                int n = p[0] == 0x28 ? p[7] << 8 | p[8] : p[6] << 24 | p[7] << 16 | p[8] << 8 | p[9];
                if (lba + n > cd!.Sectors) { AtapiFail(5, 0x21); return; }      // ILLEGAL REQUEST, LBA out of range
                if (n == 0) { AtapiComplete(); return; }
                if (n * 2048 > o.Length) { AtapiFail(5, 0x24); return; }
                if (!cd.IsData(lba, n)) { AtapiFail(5, 0x64); return; }         // ILLEGAL MODE FOR THIS TRACK (an audio sector)
                cd.ReadData(lba, n, o, 0);
                AtapiReply(n * 2048, n * 2048); return;
            }
            case 0xbe: case 0xb9:                                               // READ CD / READ CD MSF
            {
                long lba; int n;
                if (p[0] == 0xbe) { lba = (uint)(p[2] << 24 | p[3] << 16 | p[4] << 8 | p[5]); n = p[6] << 16 | p[7] << 8 | p[8]; }
                else { lba = (p[3] * 60 + p[4]) * 75 + p[5] - 150; n = (int)((p[6] * 60 + p[7]) * 75 + p[8] - 150 - lba); }
                int expect = (p[1] >> 2) & 7, flags = p[9];
                if (lba < 0 || n < 0 || lba + n > cd!.Sectors) { AtapiFail(5, 0x21); return; }
                if (n == 0 || (flags & 0xf8) == 0) { AtapiComplete(); return; }
                bool audio = cd.TrackAt(lba)!.Audio;
                if (expect == 1 && !audio || expect >= 2 && audio) { AtapiFail(5, 0x64); return; }
                bool raw = audio || (flags & 0xf8) == 0xf8;                    // audio, or sync + header + user data + EDC/ECC
                int size = raw ? 2352 : 2048;
                if ((long)n * size > o.Length) { AtapiFail(5, 0x24); return; }
                if (!raw && !cd.IsData(lba, n)) { AtapiFail(5, 0x64); return; }
                if (raw) for (int i = 0; i < n; i++) cd.ReadRaw(lba + i, o, i * 2352); else cd.ReadData(lba, n, o, 0);
                AtapiReply(n * size, n * size); return;
            }
            case 0x45: case 0xa5: case 0x47:                                    // PLAY AUDIO (10) / (12) / MSF
            {
                long start, end;
                if (p[0] == 0x47)
                {
                    start = p[3] == 0xff && p[4] == 0xff && p[5] == 0xff ? AudioLba() : (p[3] * 60 + p[4]) * 75 + p[5] - 150;
                    end = (p[6] * 60 + p[7]) * 75 + p[8] - 150;
                }
                else
                {
                    uint l = (uint)(p[2] << 24 | p[3] << 16 | p[4] << 8 | p[5]);
                    start = l == 0xffffffff ? AudioLba() : l;
                    end = start + (p[0] == 0x45 ? p[7] << 8 | p[8] : (uint)(p[6] << 24 | p[7] << 16 | p[8] << 8 | p[9]));
                }
                if (end == start) { AtapiComplete(); return; }                  // a zero length: no play, no error
                if (start < 0 || end > cd!.Sectors || end < start) { AtapiFail(5, 0x21); return; }
                if (!cd.TrackAt(start)!.Audio) { AtapiFail(5, 0x64); return; }
                _aState = 1; _aLba = start; _aEnd = end; _aT0 = SampleNow();
                AtapiComplete(); return;
            }
            case 0x4b:                                                          // PAUSE / RESUME
            {
                AudioUpdate();
                if (_aState is not (1 or 2)) { AtapiFail(5, 0x2c); return; }    // COMMAND SEQUENCE ERROR: nothing playing
                if ((p[8] & 1) != 0) { if (_aState == 2) { _aState = 1; _aT0 = SampleNow(); } }
                else if (_aState == 1) { _aLba = AudioLba(); _aState = 2; }
                AtapiComplete(); return;
            }
            case 0x43:                                                          // READ TOC: format 0 (tracks), 1 (session), 2 (full TOC)
            {
                bool msf = (p[1] & 2) != 0;
                int format = p[9] >> 6 | (p[2] & 0x0f);
                int first = cd!.Tracks[0].Number, last = cd.Tracks[^1].Number;
                Array.Clear(o, 0, 1024);
                void Addr(int at, long lba) { if (msf) Msf(o, at, lba); else Be32(o, at, lba); }
                int len;
                if (format == 1)                                                // session info: first track of the last session
                {
                    o[2] = 1; o[3] = 1; o[5] = cd.Tracks[0].Control; o[6] = (byte)first; Addr(8, cd.Tracks[0].Start); len = 12;
                }
                else if (format == 2)                                           // raw TOC: A0, A1, A2 then the tracks, MSF always
                {
                    o[2] = 1; o[3] = 1; int k = 4;
                    void Ent(byte ctl, byte point, int pm, int ps, int pf) { o[k] = 1; o[k + 1] = ctl; o[k + 3] = point; o[k + 8] = (byte)pm; o[k + 9] = (byte)ps; o[k + 10] = (byte)pf; k += 11; }
                    long lo = cd.LeadOut + 150;
                    Ent(cd.Tracks[0].Control, 0xa0, first, 0, 0);
                    Ent(cd.Tracks[^1].Control, 0xa1, last, 0, 0);
                    Ent(cd.Tracks[^1].Control, 0xa2, (int)(lo / 4500), (int)(lo / 75 % 60), (int)(lo % 75));
                    foreach (var t in cd.Tracks) { long a = t.Start + 150; Ent(t.Control, (byte)t.Number, (int)(a / 4500), (int)(a / 75 % 60), (int)(a % 75)); }
                    len = k;
                }
                else
                {
                    int from = p[6]; if (from == 0) from = 1;
                    if (from > last && from != 0xaa) { AtapiFail(5, 0x24); return; }
                    o[2] = (byte)first; o[3] = (byte)last; int k = 4;
                    foreach (var t in cd.Tracks)
                    {
                        if (t.Number < from || from == 0xaa) continue;
                        o[k + 1] = t.Control; o[k + 2] = (byte)t.Number; Addr(k + 4, t.Start); k += 8;
                    }
                    o[k + 1] = cd.Tracks[^1].Control; o[k + 2] = 0xaa; Addr(k + 4, cd.LeadOut); k += 8;
                    len = k;
                }
                o[0] = (byte)((len - 2) >> 8); o[1] = (byte)(len - 2);
                AtapiReply(len, alloc16); return;
            }
            case 0x5a: case 0x1a:                                               // MODE SENSE (10 / 6)
            {
                int page = p[2] & 0x3f;
                Array.Clear(o, 0, 64);
                bool ten = p[0] == 0x5a; int h = ten ? 8 : 4;
                int len;
                if (page == 0x2a || page == 0x3f)                               // CD capabilities page
                {
                    o[h] = 0x2a; o[h + 1] = 0x12; o[h + 2] = 0x01; o[h + 4] = 0x71; o[h + 5] = 0x03; o[h + 6] = 0x29; o[h + 7] = 0x03; o[h + 8] = 0x10; o[h + 9] = 0x80;   // + audio play, CD-DA read
                    o[h + 10] = 0x01; o[h + 12] = 0x02; o[h + 14] = 0x10; o[h + 15] = 0x80;
                    len = h + 20;
                }
                else if (page == 0x0e)                                          // CD audio control: immediate, both channels at full volume
                {
                    o[h] = 0x0e; o[h + 1] = 14; o[h + 2] = 0x04; o[h + 8] = 1; o[h + 9] = 0xff; o[h + 10] = 2; o[h + 11] = 0xff;
                    len = h + 16;
                }
                else if (page == 0x01 || page == 0x0d) { o[h] = (byte)page; o[h + 1] = 6; len = h + 8; }
                else { AtapiFail(5, 0x24); return; }
                if (ten) { o[0] = (byte)((len - 2) >> 8); o[1] = (byte)(len - 2); } else o[0] = (byte)(len - 1);
                bool hasAudio = cd != null && cd.Tracks.Exists(t => t.Audio), hasData = cd == null || cd.Tracks.Exists(t => !t.Audio);
                o[ten ? 2 : 1] = (byte)(hasAudio && hasData ? 0x03 : hasAudio ? 0x02 : 0x01);   // medium type: 120 mm data / audio / mixed CD
                AtapiReply(len, ten ? alloc16 : p[4]); return;
            }
            case 0x4a:                                                          // GET EVENT STATUS NOTIFICATION
                Array.Clear(o, 0, 8); o[1] = 6; o[2] = 0x04; o[3] = 0x10; o[5] = (byte)(cd != null ? 2 : 0);
                AtapiReply(8, alloc16); return;
            case 0xbd:                                                          // MECHANISM STATUS
                Array.Clear(o, 0, 8); o[7] = 0; AtapiReply(8, alloc16); return;
            case 0x51:                                                          // READ DISC INFORMATION
                Array.Clear(o, 0, 34); o[1] = 32; o[2] = 0x0e; o[3] = 1; o[4] = 1; o[5] = 1; o[6] = (byte)(cd?.Tracks[^1].Number ?? 1); AtapiReply(34, alloc16); return;
            case 0x42:                                                          // READ SUB-CHANNEL
            {
                AudioUpdate();
                Array.Clear(o, 0, 24);
                o[1] = _aState switch { 1 => 0x11, 2 => 0x12, 3 => 0x13, _ => 0x15 };
                if (_aState == 3) _aState = 0;                                  // "completed" is reported once
                bool msf = (p[1] & 2) != 0;
                int fmt = p[3];
                if ((p[2] & 0x40) == 0) { AtapiReply(4, alloc16); return; }     // header only
                o[3] = (byte)(fmt == 1 ? 12 : 20); o[4] = (byte)fmt;
                if (fmt == 1 && cd != null)
                {
                    long lba = AudioLba(); var t = cd.TrackAt(lba) ?? cd.Tracks[^1];
                    o[5] = t.Control; o[6] = (byte)t.Number; o[7] = (byte)(lba < t.Start ? 0 : 1);
                    if (msf) { Msf(o, 8, lba); long rel = Math.Abs(lba - t.Start); o[12] = 0; o[13] = (byte)(rel / 4500); o[14] = (byte)(rel / 75 % 60); o[15] = (byte)(rel % 75); }
                    else { Be32(o, 8, lba); Be32(o, 12, lba - t.Start); }
                    AtapiReply(16, alloc16); return;
                }
                AtapiReply(24, alloc16); return;                                // MCN / ISRC: not valid
            }
            default: AtapiFail(5, 0x20); return;                                // ILLEGAL REQUEST, invalid command
        }
    }

    // ================= save state =================

    public void Save(BinaryWriter w)
    {
        w.Write(_feature); w.Write(_count); w.Write(_sector); w.Write(_cylLo); w.Write(_cylHi); w.Write(_drvHead); w.Write(_status); w.Write(_error); w.Write(_devCtl);
        for (int d = 0; d < 2; d++) { w.Write(_lHeads[d]); w.Write(_lSpt[d]); w.Write(_multiple[d]); }
        w.Write(_irqOut); w.Write(_pos); w.Write(_len); w.Write((int)_next); w.Write(_lba); w.Write(_left); w.Write(_blockSectors); w.Write(_cmd);
        w.Write(_senseKey); w.Write(_asc); w.Write(_atapiPos); w.Write(_atapiLen);
        w.Write(_buf, 0, Math.Max(_len, 12));
        w.Write(_atapiData, 0, _atapiLen);
    }

    public void Load(BinaryReader r)
    {
        _feature = r.ReadByte(); _count = r.ReadByte(); _sector = r.ReadByte(); _cylLo = r.ReadByte(); _cylHi = r.ReadByte(); _drvHead = r.ReadByte(); _status = r.ReadByte(); _error = r.ReadByte(); _devCtl = r.ReadByte();
        for (int d = 0; d < 2; d++) { _lHeads[d] = r.ReadInt32(); _lSpt[d] = r.ReadInt32(); _multiple[d] = r.ReadInt32(); }
        _irqOut = r.ReadBoolean(); _pos = r.ReadInt32(); _len = r.ReadInt32(); _next = (Next)r.ReadInt32(); _lba = r.ReadInt64(); _left = r.ReadInt32(); _blockSectors = r.ReadInt32(); _cmd = r.ReadByte();
        _senseKey = r.ReadByte(); _asc = r.ReadByte(); _atapiPos = r.ReadInt32(); _atapiLen = r.ReadInt32();
        r.Read(_buf, 0, Math.Max(_len, 12));
        r.Read(_atapiData, 0, _atapiLen);
    }
}

/// <summary>The two IDE channels the board wires: primary (1F0h, 3F6h, IRQ 14) and secondary (170h, 376h, IRQ 15).</summary>
public sealed class AtaController
{
    public readonly AtaChannel Primary, Secondary;
    public AtaController(Pic8259 pic) { Primary = new AtaChannel(14, pic); Secondary = new AtaChannel(15, pic); }

    public void Reset() { Primary.Reset(); Secondary.Reset(); }

    private AtaChannel? Decode(int port, out int reg)
    {
        reg = 0;
        if (port is >= 0x1f0 and <= 0x1f7) { reg = port - 0x1f0; return Primary; }
        if (port is >= 0x170 and <= 0x177) { reg = port - 0x170; return Secondary; }
        if (port == 0x3f6) { reg = 8; return Primary; }
        if (port == 0x376) { reg = 8; return Secondary; }
        return null;
    }

    public bool In(int port, bool wide, out int v)
    {
        v = 0;
        var ch = Decode(port, out int reg);
        if (ch == null || (wide && reg != 0)) return false;
        v = ch.Read(reg, wide);
        return true;
    }

    public bool Out(int port, bool wide, int v)
    {
        var ch = Decode(port, out int reg);
        if (ch == null || (wide && reg != 0)) return false;
        ch.Write(reg, v, wide);
        return true;
    }

    public void Save(BinaryWriter w) { Primary.Save(w); Secondary.Save(w); }
    public void Load(BinaryReader r) { Primary.Load(r); Secondary.Load(r); }
}
