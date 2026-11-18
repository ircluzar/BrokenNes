// The PC/AT keyboard controller (8042) with its keyboard. Written for BrokenNes (the behaviour follows the 8042 / AT keyboard
// documentation; tiny386 i8042.c was read for which commands DOS software sends). The PS/2 mouse (aux port, IRQ 12) only on a machine built
// with one (the Windows 98 board): the others keep the keyboard-only controller, byte for byte.

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

/// <summary>Ports 60h / 64h. The host queues set-1 scan codes (what the controller hands the CPU with translation on).</summary>
public sealed class Kbc8042
{
    private readonly Queue<byte> _q = new();
    private byte _out; private bool _obf;
    private byte _cmdByte = 0x45;       // translate, system flag, keyboard IRQ on
    private int _pendingCtrl = -1;      // a controller command waiting for its data byte (60h, D1h, D2h)
    private int _pendingKbd = -1;       // a keyboard command waiting for its data byte (EDh, F3h, F0h)
    private bool _kbdDisabled;
    private readonly Pic8259 _pic;
    public bool A20 = true;
    /// <summary>Set when software asks for a CPU reset (command FEh, or output port bit 0 low).</summary>
    public bool ResetRequested;

    public Kbc8042(Pic8259 pic, bool mouse = false) { _pic = pic; HasMouse = mouse; }

    // ---- the PS/2 mouse (a standard 3-byte mouse with 3 buttons: device ID 00h) ----
    public readonly bool HasMouse;
    private readonly Queue<byte> _aq = new();   // bytes from the mouse
    private bool _auxOut;                       // the output buffer holds a mouse byte (status bit 5)
    private bool _msStream, _msWrap, _msRemote; private byte _msRate = 100, _msRes = 2, _msScale1 = 1; private int _pendingAux = -1;
    private int _msButtons, _msDx, _msDy;       // motion not yet sent (remote mode, a full queue)

    /// <summary>The host's mouse: relative motion (x right, y DOWN, in mickeys) and the buttons (bit 0 left, 1 right, 2 middle).</summary>
    public void MouseInput(int dx, int dy, int buttons)
    {
        if (!HasMouse) return;
        _msDx += dx; _msDy += dy;
        bool btn = buttons != _msButtons; _msButtons = buttons;
        if (!_msStream || _msRemote || _msWrap) return;
        if (_aq.Count > 30) return;                                   // the guest is not reading: keep the motion for later
        if (!btn && _msDx == 0 && _msDy == 0) return;
        do QueuePacket(); while (_msDx != 0 || _msDy != 0);
        Pump();
    }
    private void QueuePacket()
    {
        int x = Math.Clamp(_msDx, -255, 255), y = Math.Clamp(-_msDy, -255, 255);
        _msDx -= x; _msDy += y;
        _aq.Enqueue((byte)(0x08 | (_msButtons & 7) | (x < 0 ? 0x10 : 0) | (y < 0 ? 0x20 : 0))); _aq.Enqueue((byte)x); _aq.Enqueue((byte)y);
    }
    private List<byte>? _direct;               // the BIOS's INT 15h C2xxh services: the mouse's replies come back here, not through the port
    private void AuxReply(params byte[] bs) { if (_direct != null) { _direct.AddRange(bs); return; } foreach (var b in bs) _aq.Enqueue(b); Pump(); }
    /// <summary>A mouse command from the BIOS (INT 15h C2xxh), answered straight back: its reply bytes (ACK first), nothing queued at the port.</summary>
    public List<byte> MouseDirect(byte cmd, int data = -1)
    {
        _direct = new List<byte>();
        try { AuxWrite(cmd); if (data >= 0) AuxWrite((byte)data); return _direct; }
        finally { _direct = null; }
    }
    /// <summary>The 8042's command byte bit 1 (mouse IRQ 12) and bit 5 (mouse clock off), as the BIOS's C200h enable / disable sets them.</summary>
    public void MousePort(bool on) { if (on) _cmdByte = (byte)((_cmdByte | 0x02) & ~0x20); else _cmdByte = (byte)(_cmdByte & ~0x02); }
    private void AuxWrite(byte v)
    {
        if (_pendingAux >= 0)
        {
            int c = _pendingAux; _pendingAux = -1;
            if (c == 0xf3) _msRate = v; else if (c == 0xe8) _msRes = v;
            AuxReply(0xfa); return;
        }
        if (_msWrap && v is not (0xec or 0xff)) { AuxReply(v); return; }
        switch (v)
        {
            case 0xff: _aq.Clear(); _msStream = _msWrap = _msRemote = false; _msRate = 100; _msRes = 2; _msScale1 = 1; _msDx = _msDy = 0; AuxReply(0xfa, 0xaa, 0x00); break;
            case 0xfe: AuxReply(0xfa); break;
            case 0xf6: _msStream = false; _msRate = 100; _msRes = 2; _msScale1 = 1; AuxReply(0xfa); break;
            case 0xf5: _msStream = false; AuxReply(0xfa); break;
            case 0xf4: _msStream = true; _msDx = _msDy = 0; AuxReply(0xfa); break;
            case 0xf3: case 0xe8: _pendingAux = v; AuxReply(0xfa); break;
            case 0xf2: AuxReply(0xfa, 0x00); break;
            case 0xf0: _msRemote = true; AuxReply(0xfa); break;
            case 0xea: _msRemote = false; AuxReply(0xfa); break;
            case 0xee: _msWrap = true; AuxReply(0xfa); break;
            case 0xec: _msWrap = false; AuxReply(0xfa); break;
            case 0xeb: AuxReply(0xfa); if (_direct == null) { QueuePacket(); Pump(); } break;
            case 0xe9: AuxReply(0xfa, (byte)((_msRemote ? 0x40 : 0) | (_msStream ? 0x20 : 0) | (_msScale1 == 2 ? 0x10 : 0) | (_msButtons & 1) << 2 | (_msButtons & 2) >> 1 | (_msButtons & 4) >> 1), _msRes, _msRate); break;
            case 0xe7: _msScale1 = 2; AuxReply(0xfa); break;
            case 0xe6: _msScale1 = 1; AuxReply(0xfa); break;
            default: AuxReply(0xfe); break;
        }
    }

    /// <summary>A key event from the host: its set-1 bytes (E0 prefix included), in order.</summary>
    public void KeyBytes(params byte[] codes) { foreach (var c in codes) _q.Enqueue(c); Pump(); }

    public bool HasPendingKeys => _q.Count > 0 || _obf;

    private void Deliver(byte b, bool irq = true)
    {
        _out = b; _obf = true; _auxOut = false;
        if (irq && (_cmdByte & 1) != 0) _pic.Pulse(1);
    }

    /// <summary>Moves the next queued byte into the output buffer when the buffer is free (called after reads and by the run loop).</summary>
    public void Pump()
    {
        if (_obf) return;
        if (_q.Count > 0 && !_kbdDisabled && (_cmdByte & 0x10) == 0) { Deliver(_q.Dequeue()); return; }
        if (HasMouse && _aq.Count > 0 && (_cmdByte & 0x20) == 0)
        {
            _out = _aq.Dequeue(); _obf = true; _auxOut = true;
            if ((_cmdByte & 2) != 0) _pic.Pulse(12);
        }
    }

    private void Reply(params byte[] bs)
    {
        // controller / keyboard replies go ahead of queued keys
        var rest = _q.ToArray(); _q.Clear();
        foreach (var b in bs) _q.Enqueue(b);
        foreach (var b in rest) _q.Enqueue(b);
        _obf = false; Pump();
    }

    public byte In(int port)
    {
        if (port == 0x60)
        {
            byte v = _out; _obf = false;
            return v;
        }
        // status: OBF, system flag (bit 2), command/data (bit 3) as last written; input buffer always empty
        return (byte)((_obf ? 1 : 0) | (_cmdByte & 4) | (_pendingCtrl >= 0 ? 8 : 0) | (_obf && _auxOut ? 0x20 : 0));
    }

    public void Out(int port, byte v)
    {
        if (port == 0x64)
        {
            switch (v)
            {
                case 0x20: Reply(_cmdByte); break;
                case 0x60: case 0xd1: case 0xd2: _pendingCtrl = v; break;
                case 0xa7: if (HasMouse) _cmdByte |= 0x20; break;  // mouse port off / on (no mouse: ignored)
                case 0xa8: if (HasMouse) { _cmdByte &= 0xdf; Pump(); } break;
                case 0xa9: Reply(HasMouse ? (byte)0x00 : (byte)0x01); break;   // mouse port test: 00 = ok, 01 = none
                case 0xd3: case 0xd4: if (HasMouse) _pendingCtrl = v; break;
                case 0xaa: Reply(0x55); break;
                case 0xab: Reply(0x00); break;
                case 0xad: _kbdDisabled = true; break;
                case 0xae: _kbdDisabled = false; Pump(); break;
                case 0xc0: Reply(0x80); break;
                case 0xd0: Reply((byte)(0x01 | (A20 ? 2 : 0) | (_obf ? 0x10 : 0))); break;
                case 0xdd: A20 = false; break;
                case 0xdf: A20 = true; break;
                case 0xfe: ResetRequested = true; break;
                default: if (v >= 0xf0 && (v & 1) == 0) ResetRequested = true; break;   // pulse output port bit 0
            }
            return;
        }
        // port 60h
        if (_pendingCtrl >= 0)
        {
            int c = _pendingCtrl; _pendingCtrl = -1;
            if (c == 0x60) { _cmdByte = v; Pump(); }
            else if (c == 0xd1) { A20 = (v & 2) != 0; if ((v & 1) == 0) ResetRequested = true; }
            else if (c == 0xd2) Reply(v);
            else if (c == 0xd3) AuxReply(v);
            else if (c == 0xd4) AuxWrite(v);
            return;
        }
        if (_pendingKbd >= 0)
        {
            int c = _pendingKbd; _pendingKbd = -1;
            Reply(c == 0xf0 && v == 0 ? new byte[] { 0xfa, 0x02 } : new byte[] { 0xfa });
            return;
        }
        switch (v)
        {
            case 0xed: case 0xf3: case 0xf0: _pendingKbd = v; Reply(0xfa); break;
            case 0xf2: Reply(0xfa, 0xab, 0x41); break;
            case 0xee: Reply(0xee); break;
            case 0xff: _q.Clear(); Reply(0xfa, 0xaa); break;
            default: Reply(0xfa); break;                           // F4 enable, F5 disable, F6 defaults, ...
        }
    }

    public void Save(BinaryWriter w)
    {
        var q = _q.ToArray(); w.Write(q.Length); w.Write(q);
        w.Write(_out); w.Write(_obf); w.Write(_cmdByte); w.Write(_pendingCtrl); w.Write(_pendingKbd); w.Write(_kbdDisabled); w.Write(A20);
    }
    public void Load(BinaryReader r)
    {
        _q.Clear(); foreach (var b in r.ReadBytes(r.ReadInt32())) _q.Enqueue(b);
        _out = r.ReadByte(); _obf = r.ReadBoolean(); _cmdByte = r.ReadByte(); _pendingCtrl = r.ReadInt32(); _pendingKbd = r.ReadInt32();
        _kbdDisabled = r.ReadBoolean(); A20 = r.ReadBoolean(); ResetRequested = false;
    }

    /// <summary>The mouse's own block (the machine writes it last, tagged, so a state from before the mouse still loads: its mouse starts reset).</summary>
    public void SaveMouse(BinaryWriter w)
    {
        var aq = _aq.ToArray(); w.Write(aq.Length); w.Write(aq);
        w.Write(_auxOut); w.Write(_msStream); w.Write(_msWrap); w.Write(_msRemote); w.Write(_msRate); w.Write(_msRes); w.Write(_msScale1); w.Write(_pendingAux);
        w.Write(_msButtons); w.Write(_msDx); w.Write(_msDy);
    }
    public void ResetMouse() { _aq.Clear(); _auxOut = false; _msStream = _msWrap = _msRemote = false; _msRate = 100; _msRes = 2; _msScale1 = 1; _pendingAux = -1; _msButtons = _msDx = _msDy = 0; }
    public void LoadMouse(BinaryReader r)
    {
        ResetMouse();
        foreach (var b in r.ReadBytes(r.ReadInt32())) _aq.Enqueue(b);
        _auxOut = r.ReadBoolean(); _msStream = r.ReadBoolean(); _msWrap = r.ReadBoolean(); _msRemote = r.ReadBoolean(); _msRate = r.ReadByte(); _msRes = r.ReadByte(); _msScale1 = r.ReadByte();
        _pendingAux = r.ReadInt32(); _msButtons = r.ReadInt32(); _msDx = r.ReadInt32(); _msDy = r.ReadInt32();
    }
}
