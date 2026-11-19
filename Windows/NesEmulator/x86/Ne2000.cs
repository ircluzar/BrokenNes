// The x86 PC's network card: a Realtek RTL8029 (a PCI NE2000, the DP8390 core with 32 KB of buffer memory), which Windows 98 installs
// with its in-box driver (NETRT.INF, PCI\VEN_10EC&DEV_8029). Written for BrokenNes from the DP8390D / RTL8029AS datasheets.
//
// The card only moves raw Ethernet frames: what is on the other end of the cable is an INetLink (the built-in NAT in PcNat.cs today, a
// virtual switch between emulator instances later). Frames from the link wait in a queue and enter the receive ring only when it has
// room, so the cable never drops a frame the guest could have taken. The card's registers and buffer memory are part of a save state;
// the link (and frames in flight on it) is host-side and is not.

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

/// <summary>The other end of the network cable: takes the frames the card sends, and hands back frames for the card.</summary>
public interface INetLink
{
    /// <summary>A frame the guest transmitted (destination MAC first, no FCS).</summary>
    void Send(byte[] frame);
    /// <summary>Called on the emulation thread every batch: the next frame for the guest, or null.</summary>
    byte[]? Receive();
}

public sealed class Rtl8029
{
    public const ushort Vendor = 0x10ec, Device = 0x8029;
    public const int IoSize = 32;
    private const int MemSize = 0xc000;                  // PROM image at 0..1F, 32 KB of buffer at 4000h..BFFFh
    private const int MemStart = 0x4000;

    /// <summary>The card's address: 52:54:00 (locally administered, the usual emulator OUI) + a fixed tail; another one per PC on a shared LAN (PcLan.cs).
    /// It is the PROM: a change takes effect at the next reset (a cold boot), as on a real card.</summary>
    public byte[] Mac = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
    public INetLink? Link;
    /// <summary>Raises / lowers the card's interrupt line (INTA# through the PIIX4's router).</summary>
    public Action<bool> SetIrq = _ => { };
    public long FramesSent, FramesReceived;

    private readonly byte[] _mem = new byte[MemSize];
    private byte _cmd, _start, _stop, _bnry, _tpsr, _isr, _imr, _rcr, _tcr, _dcr, _tsr, _rsr, _curpag;
    private ushort _tcnt, _rsar, _rcnt;
    private readonly byte[] _par = new byte[6], _mar = new byte[8];
    private bool _irqOut;
    private byte[]? _pending;                            // a frame from the link waiting for room in the ring

    public Rtl8029() { Reset(); }

    public void Reset()
    {
        Array.Clear(_mem);
        for (int i = 0; i < 6; i++) { _mem[i * 2] = Mac[i]; _mem[i * 2 + 1] = Mac[i]; }
        _mem[14] = _mem[15] = 0x57;                       // 'W': a 16-bit card (byte-mode readers look here...)
        _mem[28] = _mem[29] = _mem[30] = _mem[31] = 0x57; // (...word-doubled PROM readers here, as on QEMU / real cards)
        _cmd = 0x21; _isr = 0x80; _imr = 0; _start = _stop = _bnry = _tpsr = _curpag = 0; _rcr = _tcr = _dcr = _tsr = _rsr = 0;
        _tcnt = _rsar = _rcnt = 0;
        Array.Clear(_par); Array.Clear(_mar);
        _pending = null;
        UpdateIrq();
    }

    private void UpdateIrq()
    {
        bool on = (_isr & _imr & 0x7f) != 0;
        if (on != _irqOut) { _irqOut = on; SetIrq(on); }
    }

    // ================= I/O (offset from the BAR) =================

    /// <summary>Debug tools: every register access (off, isWrite, value, word).</summary>
    public Action<int, bool, int, bool>? Trace;

    public int In(int off, bool word)
    {
        off &= 0x1f;
        if (Trace != null) { int v = InCore(off, word); Trace(off, false, v, word); return v; }
        return InCore(off, word);
    }

    private int InCore(int off, bool word)
    {
        if (off < 0x10) return ReadReg(off);
        if (off < 0x18) return DmaRead(word);
        Reset(); return 0;                                // the reset port: a read resets the card
    }

    public void Out(int off, int v, bool word)
    {
        off &= 0x1f;
        Trace?.Invoke(off, true, v, word);
        if (off < 0x10) WriteReg(off, (byte)v);
        else if (off < 0x18) DmaWrite(v, word);
        // (a write to the reset port does nothing)
    }

    private int ReadReg(int r)
    {
        if (r == 0) return _cmd;
        int page = _cmd >> 6;
        if (page == 1)
        {
            if (r <= 6) return _par[r - 1];
            if (r == 7) return _curpag;
            return _mar[r - 8];
        }
        if (page == 2)
        {
            return r switch { 1 => _start, 2 => _stop, 3 => 0, 4 => _tpsr, 0x0c => _rcr, 0x0d => _tcr, 0x0e => _dcr, 0x0f => _imr, _ => 0 };
        }
        if (page == 3) return 0;
        switch (r)
        {
            case 0x03: return _bnry;
            case 0x04: return _tsr;
            case 0x07: return _isr;
            case 0x08: return (byte)_rsar;
            case 0x09: return (byte)(_rsar >> 8);
            case 0x0a: return 0x50;                       // RTL8029 ID: 'P'
            case 0x0b: return 0x43;                       //             'C'
            case 0x0c: return _rsr;
            default: return 0;                            // CLDA, NCR, FIFO, tally counters
        }
    }

    private void WriteReg(int r, byte v)
    {
        if (r == 0)
        {
            _cmd = (byte)(v & ~0x04);                     // TXP never sticks: the frame goes out at once
            if ((v & 0x01) == 0)
            {
                _isr &= 0x7f;                             // started: the reset flag clears
                if ((v & 0x18) != 0 && _rcnt == 0) _isr |= 0x40;   // a remote DMA of nothing completes at once
                if ((v & 0x04) != 0) Transmit();
            }
            UpdateIrq();
            return;
        }
        int page = _cmd >> 6;
        if (page == 1)
        {
            if (r <= 6) _par[r - 1] = v;
            else if (r == 7) _curpag = v;
            else _mar[r - 8] = v;
            return;
        }
        if (page != 0) return;
        switch (r)
        {
            case 0x01: _start = v; break;
            case 0x02: _stop = v; break;
            case 0x03: _bnry = v; break;
            case 0x04: _tpsr = v; break;
            case 0x05: _tcnt = (ushort)((_tcnt & 0xff00) | v); break;
            case 0x06: _tcnt = (ushort)((_tcnt & 0x00ff) | (v << 8)); break;
            case 0x07: _isr &= (byte)~(v & 0x7f); UpdateIrq(); break;
            case 0x08: _rsar = (ushort)((_rsar & 0xff00) | v); break;
            case 0x09: _rsar = (ushort)((_rsar & 0x00ff) | (v << 8)); break;
            case 0x0a: _rcnt = (ushort)((_rcnt & 0xff00) | v); break;
            case 0x0b: _rcnt = (ushort)((_rcnt & 0x00ff) | (v << 8)); break;
            case 0x0c: _rcr = v; break;
            case 0x0d: _tcr = v; break;
            case 0x0e: _dcr = v; break;
            case 0x0f: _imr = v; UpdateIrq(); break;
        }
    }

    // ================= remote DMA (the data port) =================

    private byte MemRead(int a) => a < 32 ? _mem[a & 0x1f] : a >= MemStart && a < MemSize ? _mem[a] : (byte)0xff;
    private void MemWrite(int a, byte v) { if (a >= MemStart && a < MemSize) _mem[a] = v; }

    private void DmaStep(int n)
    {
        _rsar = (ushort)(_rsar + n);
        if (_rsar == _stop << 8) _rsar = (ushort)(_start << 8);   // (DP8390: the remote DMA wraps inside the ring too)
        _rcnt = (ushort)(_rcnt <= n ? 0 : _rcnt - n);
        if (_rcnt == 0) { _isr |= 0x40; UpdateIrq(); }
    }

    private int DmaRead(bool word)
    {
        if (word && (_dcr & 1) != 0)
        {
            int v = MemRead(_rsar) | MemRead(_rsar + 1) << 8;
            DmaStep(2); return v;
        }
        int b = MemRead(_rsar); DmaStep(1); return b;
    }

    private void DmaWrite(int v, bool word)
    {
        if (word && (_dcr & 1) != 0) { MemWrite(_rsar, (byte)v); MemWrite(_rsar + 1, (byte)(v >> 8)); DmaStep(2); }
        else { MemWrite(_rsar, (byte)v); DmaStep(1); }
    }

    // ================= the wire =================

    private void Transmit()
    {
        int a = _tpsr << 8, n = Math.Min((int)_tcnt, 1518);
        var f = new byte[n];
        for (int i = 0; i < n; i++) f[i] = MemRead(a + i);
        _tsr = 0x01; _isr |= 0x02;                        // PTX
        if ((_tcr & 0x06) != 0) Receive(f);               // internal loopback (driver self-tests)
        else { FramesSent++; Link?.Send(f); }
        UpdateIrq();
    }

    /// <summary>Called every batch: moves frames from the link into the receive ring while there is room.</summary>
    public void Poll()
    {
        if (Link == null) return;
        while (true)
        {
            _pending ??= Link.Receive();
            if (_pending == null) return;
            if ((_cmd & 0x01) != 0 || _start == 0 || _stop <= _start) { _pending = null; continue; }   // stopped: the frame is lost, like on a wire
            if (BufferFull()) return;                    // wait for the driver to free the ring
            var f = _pending; _pending = null;
            Receive(f);
        }
    }

    private bool BufferFull()
    {
        int index = _curpag << 8, boundary = _bnry << 8, avail;
        if (index < boundary) avail = boundary - index;
        else avail = ((_stop - _start) << 8) - (index - boundary);
        return avail < 1518 + 4 + 256;
    }

    private bool Accept(byte[] f)
    {
        if ((_rcr & 0x10) != 0) return true;             // promiscuous
        if (f[0] == 0xff && f[1] == 0xff && f[2] == 0xff && f[3] == 0xff && f[4] == 0xff && f[5] == 0xff) return (_rcr & 0x04) != 0;
        if ((f[0] & 1) != 0)
        {
            if ((_rcr & 0x08) == 0) return false;
            uint crc = 0xffffffff;                       // the multicast hash: top 6 bits of the Ethernet CRC of the address
            for (int i = 0; i < 6; i++) { byte b = f[i]; for (int k = 0; k < 8; k++) { bool c = ((crc >> 31) ^ (uint)(b & 1)) != 0; crc <<= 1; b >>= 1; if (c) crc ^= 0x04c11db7; } }
            int idx = (int)(crc >> 26);
            return (_mar[idx >> 3] & (1 << (idx & 7))) != 0;
        }
        for (int i = 0; i < 6; i++) if (f[i] != _par[i]) return false;
        return true;
    }

    private void Receive(byte[] f)
    {
        if (f.Length < 14 || !Accept(f)) return;
        if (f.Length < 60) Array.Resize(ref f, 60);
        int start = _start << 8, stop = _stop << 8;
        int index = _curpag << 8;
        if (index < start || index >= stop) index = start;
        int total = f.Length + 4;
        int next = index + ((total + 255) & ~0xff);
        if (next >= stop) next -= stop - start;
        _rsr = (byte)(0x01 | ((f[0] & 1) != 0 ? 0x20 : 0));
        var hdr = new byte[] { _rsr, (byte)(next >> 8), (byte)total, (byte)(total >> 8) };
        int p = index;
        void Put(byte b) { MemWrite(p, b); if (++p >= stop) p = start; }
        foreach (byte b in hdr) Put(b);
        foreach (byte b in f) Put(b);
        _curpag = (byte)(next >> 8);
        _isr |= 0x01;                                     // PRX
        FramesReceived++;
        UpdateIrq();
    }

    // ================= save state =================

    public void Save(BinaryWriter w)
    {
        w.Write(1);
        w.Write(_mem);
        w.Write(new[] { _cmd, _start, _stop, _bnry, _tpsr, _isr, _imr, _rcr, _tcr, _dcr, _tsr, _rsr, _curpag });
        w.Write(_tcnt); w.Write(_rsar); w.Write(_rcnt);
        w.Write(_par); w.Write(_mar);
    }

    public void Load(BinaryReader r)
    {
        if (r.ReadInt32() != 1) throw new InvalidDataException("unknown RTL8029 state version");
        r.Read(_mem, 0, MemSize);
        var b = r.ReadBytes(13);
        _cmd = b[0]; _start = b[1]; _stop = b[2]; _bnry = b[3]; _tpsr = b[4]; _isr = b[5]; _imr = b[6]; _rcr = b[7]; _tcr = b[8]; _dcr = b[9]; _tsr = b[10]; _rsr = b[11]; _curpag = b[12];
        _tcnt = r.ReadUInt16(); _rsar = r.ReadUInt16(); _rcnt = r.ReadUInt16();
        r.Read(_par, 0, 6); r.Read(_mar, 0, 8);
        _pending = null;
        _irqOut = !((_isr & _imr & 0x7f) != 0);           // force the line to the loaded level
        UpdateIrq();
    }
}
