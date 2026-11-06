// The PC/AT keyboard controller (8042) with its keyboard. Written for BrokenNes (the behaviour follows the 8042 / AT keyboard
// documentation; tiny386 i8042.c was read for which commands DOS software sends). Keyboard only: no PS/2 mouse port.

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

    public Kbc8042(Pic8259 pic) { _pic = pic; }

    /// <summary>A key event from the host: its set-1 bytes (E0 prefix included), in order.</summary>
    public void KeyBytes(params byte[] codes) { foreach (var c in codes) _q.Enqueue(c); Pump(); }

    public bool HasPendingKeys => _q.Count > 0 || _obf;

    private void Deliver(byte b, bool irq = true)
    {
        _out = b; _obf = true;
        if (irq && (_cmdByte & 1) != 0) _pic.Pulse(1);
    }

    /// <summary>Moves the next queued byte into the output buffer when the buffer is free (called after reads and by the run loop).</summary>
    public void Pump()
    {
        if (_obf || _q.Count == 0 || _kbdDisabled || (_cmdByte & 0x10) != 0) return;
        Deliver(_q.Dequeue());
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
        return (byte)((_obf ? 1 : 0) | (_cmdByte & 4) | (_pendingCtrl >= 0 ? 8 : 0));
    }

    public void Out(int port, byte v)
    {
        if (port == 0x64)
        {
            switch (v)
            {
                case 0x20: Reply(_cmdByte); break;
                case 0x60: case 0xd1: case 0xd2: _pendingCtrl = v; break;
                case 0xa7: case 0xa8: break;                       // mouse port off / on: no mouse
                case 0xa9: Reply(0x01); break;                     // mouse port test: none
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
}
