using System;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>The kind of controller in a Genesis port.</summary>
public enum MdPadKind { ThreeButton, SixButton }

/// <summary>
/// The Genesis I/O chip ($A10001-$A1001F) with its two control ports, and the 3-button and 6-button pads behind them. A pad has no registers of its own: the console writes the TH pin
/// (bit 6 of the data port) and the pad answers on the other six lines, which is how the buttons are multiplexed. The 6-button pad counts TH's falling edges and, on the right
/// ones, shows X, Y, Z and Mode; it forgets the count when TH stays still for about 8192 68000 cycles (Mortal Kombat 3 and Decap Attack depend on both facts).
/// </summary>
/// <remarks>
/// Reads, TH=1: bit 6 = TH, bit 5 = C, bit 4 = B, then right, left, down, up. TH=0: bit 5 = Start, bit 4 = A, bits 3 and 2 = 0, down, up. Buttons are active low. The 6-button pad goes through eight
/// phases, TH high then low four times from the timeout: normal, normal, normal, normal, normal, Start, A and four zero bits (the 6-button ID), C, B, Mode, X, Y, Z, then Start, A and four one bits;
/// counted in falling edges of TH (<c>Falls</c>, 0 at the timeout, 1 to 4 after): the ID is TH low with 3, the extra buttons TH high with 3, the ones TH low with 4, and the fifth edge starts the
/// sequence over. Pins the console has set as outputs (control bit 1) read back what was written; TH, undriven, is pulled up.
/// </remarks>
public sealed class MdPads
{
    /// <summary>The pad's lines go quiet this long (8192 cycles of the 68000, in master clocks) after the last TH edge, and the 6-button pad's count starts again.</summary>
    public const long TimeoutMasterClocks = 8192L * 7;

    private sealed class Port
    {
        public MdPadKind Kind;
        public PadButtons Buttons;
        public byte DataLatch = 0x00;       // what the console wrote to the data port
        public byte Control = 0x00;         // 1 = output
        public int Falls;                   // TH falling edges since the timeout
        public bool Th = true;
        public long LastEdge;
    }

    private readonly Port[] ports = { new() { Kind = MdPadKind.SixButton }, new() { Kind = MdPadKind.ThreeButton }, new() { Kind = MdPadKind.ThreeButton } };
    private readonly byte version;

    /// <param name="version">The value of the version register ($A10001): bit 7 overseas, bit 6 PAL, bit 5 no expansion unit, bits 3-0 the hardware version.</param>
    public MdPads(byte version) { this.version = version; }

    public byte Version => version;

    public void SetKind(int port, MdPadKind kind) => ports[port].Kind = kind;
    public MdPadKind GetKind(int port) => ports[port].Kind;
    public void SetButtons(int port, PadButtons b) { if (port is 0 or 1) ports[port].Buttons = b; }

    private static bool Down(PadButtons b, PadButtons which) => (b & which) != 0;

    /// <summary>The six lines a pad drives, given TH.</summary>
    private static byte Lines(Port p, bool th, long now)
    {
        PadButtons b = p.Buttons;
        int fall = p.Falls;
        if (th)
        {
            if (p.Kind == MdPadKind.SixButton && fall == 3)
                return (byte)(0x40 | (Down(b, PadButtons.C) ? 0 : 0x20) | (Down(b, PadButtons.B) ? 0 : 0x10) | (Down(b, PadButtons.Mode) ? 0 : 8) | (Down(b, PadButtons.X) ? 0 : 4)
                                   | (Down(b, PadButtons.Y) ? 0 : 2) | (Down(b, PadButtons.Z) ? 0 : 1));
            return (byte)(0x40 | (Down(b, PadButtons.C) ? 0 : 0x20) | (Down(b, PadButtons.B) ? 0 : 0x10) | (Down(b, PadButtons.Right) ? 0 : 8) | (Down(b, PadButtons.Left) ? 0 : 4)
                               | (Down(b, PadButtons.Down) ? 0 : 2) | (Down(b, PadButtons.Up) ? 0 : 1));
        }
        int low;
        if (p.Kind == MdPadKind.SixButton && fall == 3) low = 0x00;
        else if (p.Kind == MdPadKind.SixButton && fall == 4) low = 0x0F;
        else low = (Down(b, PadButtons.Down) ? 0 : 2) | (Down(b, PadButtons.Up) ? 0 : 1);
        return (byte)((Down(b, PadButtons.Start) ? 0 : 0x20) | (Down(b, PadButtons.A) ? 0 : 0x10) | low);
    }

    private static void Expire(Port p, long now)
    {
        if (now - p.LastEdge > TimeoutMasterClocks) { p.Falls = 0; }
    }

    /// <summary>A read of data port <paramref name="index"/> (0, 1 or 2 for the expansion port).</summary>
    public byte ReadData(int index, long now)
    {
        var p = ports[index];
        Expire(p, now);
        byte pins;
        if (index == 2) pins = 0x7F;      // nothing plugged into the expansion port
        else
        {
            bool th = (p.Control & 0x40) != 0 ? (p.DataLatch & 0x40) != 0 : true;
            pins = Lines(p, th, now);
            if ((p.Control & 0x40) != 0) pins = (byte)(pins & ~0x40 | p.DataLatch & 0x40);
        }
        // pins the console drives read back its latch, the others follow the pad
        return (byte)((p.DataLatch & p.Control | pins & ~p.Control) & 0xFF);
    }

    public void WriteData(int index, byte value, long now)
    {
        var p = ports[index];
        bool before = (p.Control & 0x40) != 0 ? (p.DataLatch & 0x40) != 0 : true;
        Expire(p, now);
        p.DataLatch = value;
        bool after = (p.Control & 0x40) != 0 ? (value & 0x40) != 0 : true;
        Edge(p, before, after, now);
    }

    public byte ReadControl(int index) => ports[index].Control;

    public void WriteControl(int index, byte value, long now)
    {
        var p = ports[index];
        bool before = (p.Control & 0x40) != 0 ? (p.DataLatch & 0x40) != 0 : true;
        p.Control = value;
        bool after = (p.Control & 0x40) != 0 ? (p.DataLatch & 0x40) != 0 : true;
        Edge(p, before, after, now);
    }

    private static void Edge(Port p, bool before, bool after, long now)
    {
        if (before == after) return;
        if (before && !after) p.Falls = p.Falls >= 4 ? 1 : p.Falls + 1;
        p.Th = after;
        p.LastEdge = now;
    }

    /// <summary>A byte read at <c>$A10000 + offset</c> (offset 0 to $1F): odd offsets are the registers, even ones answer with the same byte.</summary>
    public byte Read(int offset, long now)
    {
        int reg = offset >> 1 & 0xF;
        return reg switch
        {
            0 => version,
            1 => ReadData(0, now),
            2 => ReadData(1, now),
            3 => ReadData(2, now),
            4 => ReadControl(0),
            5 => ReadControl(1),
            6 => ReadControl(2),
            _ => 0xFF,        // serial registers: nothing connected, no data
        };
    }

    public void Write(int offset, byte value, long now)
    {
        int reg = offset >> 1 & 0xF;
        switch (reg)
        {
            case 1: WriteData(0, value, now); break;
            case 2: WriteData(1, value, now); break;
            case 3: WriteData(2, value, now); break;
            case 4: WriteControl(0, value, now); break;
            case 5: WriteControl(1, value, now); break;
            case 6: WriteControl(2, value, now); break;
        }
    }

    public string Describe() => $"pads: P1 {ports[0].Kind} (ctrl {ports[0].Control:X2} data {ports[0].DataLatch:X2} falls {ports[0].Falls}), P2 {ports[1].Kind} (ctrl {ports[1].Control:X2} data {ports[1].DataLatch:X2})";
}
