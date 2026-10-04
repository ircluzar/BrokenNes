using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// Sleds for the question "at which instruction boundary is the frame interrupt taken?" when the instruction it falls in is longer than the NOP's 4 cycles. The programs enable the frame interrupt, then run one
/// kind of instruction over and over (the sled), so the interrupt arrives somewhere inside it; the handler (at $0038) acknowledges and returns. The cycle at which the handler starts, over a dozen frames (each
/// frame moves the interrupt against the sled's grid by the frame length modulo the instruction's), is compared between the oracle (<c>mesen-capture.ps1 -Watch 0038</c>) and this emulator (<c>--watch-pc 38</c>)
/// at the same start line. <c>--smstest --write-irq-probes dir</c> writes them.
/// </summary>
internal static class IrqProbes
{
    /// <summary>name, the sled's instruction bytes, its length in cycles (for the file name only), setup before the sled (run once per pass)</summary>
    public static readonly (string Name, byte[] Instr, byte[] Setup)[] Sleds =
    {
        ("nop", SmsProg.B(0x00), Array.Empty<byte>()),
        ("jr", SmsProg.B(0x18, 0x00), Array.Empty<byte>()),                    // JR +0: 12 cycles
        ("djnz", SmsProg.B(0x10, 0x00), SmsProg.B(0x06, 0xFF)),                // DJNZ +0 with B = 255: 13 cycles taken
        ("ldann", SmsProg.B(0x3A, 0x00, 0xC0), Array.Empty<byte>()),           // LD A,($C000): 13
        ("ldnna", SmsProg.B(0x32, 0x00, 0xC0), Array.Empty<byte>()),           // LD ($C000),A: 13
        ("jp", SmsProg.B(0xC3, 0x00, 0x00), Array.Empty<byte>()),              // JP nn to the next instruction (patched): 10
        ("pushpop", SmsProg.B(0xF5, 0xF1), Array.Empty<byte>()),               // PUSH AF; POP AF: 11 + 10
        ("ldan", SmsProg.B(0x3E, 0x00), Array.Empty<byte>()),                  // LD A,0: 7
        ("incrr", SmsProg.B(0x23), Array.Empty<byte>()),                       // INC HL: 6
        ("outn", SmsProg.B(0xD3, 0x3F), SmsProg.B(0x3E, 0xFF)),                // OUT ($3F),A: 11
        ("inn", SmsProg.B(0xDB, 0xDC), Array.Empty<byte>()),                   // IN A,($DC): 11
        ("ldhln", SmsProg.B(0x36, 0x00), SmsProg.B(0x21, 0x00, 0xC0)),         // LD (HL),0: 10 (the oracle 12)
        ("call", SmsProg.B(0xCD, 0x00, 0x00), Array.Empty<byte>()),            // CALL to the next instruction (patched): 17 (the stack grows, reset at the loop start)
    };

    public static byte[] Build(string name, int pad)
    {
        var (_, instr, setup) = Sleds.First(s => s.Name == name);
        var m = new List<byte>(SmsProg.Prolog);
        m.AddRange(SmsProg.B(0x3E, 0xE2, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF, 0xDB, 0xBF));       // R1 = $E2 (picture and frame interrupt on); read the status to clear it
        for (int i = 0; i < pad; i++) m.AddRange(SmsProg.B(0x3E, 0x00));                          // padding (7 cycles each) moves the sled against the interrupt
        m.Add(0xFB);                                                                              // EI
        int loopStart = 0x60 + m.Count;
        m.AddRange(SmsProg.B(0x31, 0xF0, 0xDF));                                                  // LD SP,$DFF0 (the call sled grows the stack)
        m.AddRange(setup);
        for (int i = 0; i < 60; i++)
        {
            int here = 0x60 + m.Count;
            m.AddRange(instr);
            if (name == "jp") { int t = here + 3; m[m.Count - 2] = (byte)t; m[m.Count - 1] = (byte)(t >> 8); }
            if (name == "call") { int t = here + 3; m[m.Count - 2] = (byte)t; m[m.Count - 1] = (byte)(t >> 8); }
        }
        m.AddRange(SmsProg.B(0xC3, loopStart & 0xFF, loopStart >> 8));                            // JP loop
        var handler = SmsProg.B(0xDB, 0xBF, 0xFB, 0xED, 0x4D);                                    // IN A,($BF); EI; RETI
        return SmsProg.Rom(m.ToArray(), handler, null, 0x60);
    }

    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var s in Sleds) File.WriteAllBytes(Path.Combine(dir, $"irq-{s.Name}.sms"), Build(s.Name, 0));
        Console.WriteLine($"wrote {Sleds.Length} interrupt sleds to {dir}");
        return 0;
    }
}
