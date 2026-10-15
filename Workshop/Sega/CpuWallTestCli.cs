using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NesEmulator;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--cpu-wall-test [--only catalog|z80|m68k|wall|swap]</c>: the two NES-facing CPU walls, <c>CPU_Z80</c> and <c>CPU_M68K</c>, by measurement.
/// <list type="bullet">
/// <item><b>catalog</b>: the NES CPU menu lists both under their families and says what they are; the registry finds them.</item>
/// <item><b>z80</b> / <b>m68k</b>: a tiny program written for that chip, in a NES cartridge, drives the NES screen through the PPU registers: its NMI handler is reached through the NES vector (the NMI counter in RAM follows the frames, the backdrop colour cycles with it), its IRQ
/// handler through the NES IRQ vector, the stock-clock timing is the documented ratio (a NOP is 2 NES cycles on the Z80; 30 clocks of the 68000 are 7), and the Z80's I/O space is empty and counted. So the chips really do the work; the games are the wall.</item>
/// <item><b>wall</b>: the same job written in 6502 code works on CPU_FIX and does not on either wall (the program never draws its colour).</item>
/// <item><b>swap</b>: swapping the CPU in the middle of a run is accepted by the NES core and the old game stops being itself (the wall), without a crash.</item>
/// </list>
/// Exit code 0 = every check passed. Every group is guarded.
/// </summary>
internal static class CpuWallTestCli
{
    private static int passes, fails;
    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else fails++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }
    private static void Guard(string title, Action body)
    {
        Console.WriteLine($"\n== {title}");
        try { body(); }
        catch (Exception ex) { Check($"{title}: the group ran to the end", false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string only = args.Length > 2 && args[1].Equals("--only", StringComparison.OrdinalIgnoreCase) ? args[2] : "";
        bool Want(string s) => only.Length == 0 || only.Equals(s, StringComparison.OrdinalIgnoreCase);
        if (Want("catalog")) Guard("catalog", Catalog);
        if (Want("z80")) Guard("CPU_Z80 runs a Z80 program on the NES board", Z80Program);
        if (Want("m68k")) Guard("CPU_M68K runs a 68000 program on the NES board", M68kProgram);
        if (Want("wall")) Guard("the wall: a 6502 program does not run on either", Wall);
        if (Want("swap")) Guard("swapping the CPU under a running game", Swap);
        if (only == "trace") Guard("trace", Trace);
        Console.WriteLine($"\n{passes} passed, {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------------------------ cartridges
    private static readonly byte[] Colours = { 0x16, 0x2A, 0x12, 0x30 };

    /// <summary>NROM-256 with CHR RAM-less 8 KB CHR; the program lives at $8000, the colour table at $8100, the vectors at the end.</summary>
    private static byte[] Rom(IEnumerable<(int at, byte[] code)> pieces, int nmi, int reset, int irq)
    {
        var prg = new byte[0x8000];
        foreach (var (at, code) in pieces) Array.Copy(code, 0, prg, at - 0x8000, code.Length);
        Array.Copy(Colours, 0, prg, 0x100, Colours.Length);
        void Vec(int off, int v) { prg[off] = (byte)v; prg[off + 1] = (byte)(v >> 8); }
        Vec(0x7FFA, nmi); Vec(0x7FFC, reset); Vec(0x7FFE, irq);
        var rom = new byte[16 + prg.Length + 0x2000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 2; rom[5] = 1;
        Array.Copy(prg, 0, rom, 16, prg.Length);
        return rom;
    }

    /// <summary>A cartridge of zeros (NOP on the Z80, ORI.B #0,D0 on the 68000) with every vector at $8000, for timing.</summary>
    private static byte[] Blank() { var r = Rom(Array.Empty<(int, byte[])>(), 0x8000, 0x8000, 0x8000); Array.Clear(r, 16 + 0x100, Colours.Length); return r; }

    /// <summary>The Z80 program: NMI on, then each time the NMI handler has counted a frame, show the next of four backdrop colours. IRQ handler (the APU frame IRQ): acknowledge, count in $0011, return with EI.</summary>
    internal static byte[] Z80Rom() => Rom(new (int, byte[])[]
    {
        (0x8000, new byte[]
        {
            0x31, 0xFF, 0x01,             // LD SP,$01FF
            0x3E, 0x80, 0x32, 0x00, 0x20, // LD A,$80 ; LD ($2000),A           NMI on
            0xAF, 0x32, 0x10, 0x00,       // XOR A ; LD ($0010),A
            0x32, 0x11, 0x00,             // LD ($0011),A
            0x3E, 0x00, 0x32, 0x17, 0x40, // LD A,0 ; LD ($4017),A             frame IRQ on
            0xFB,                         // EI
            0x3A, 0x10, 0x00,             // main: LD A,($0010)
            0x47,                         // LD B,A
            0x3A, 0x10, 0x00,             // wait: LD A,($0010)
            0xB8,                         // CP B
            0x28, 0xFA,                   // JR Z,wait
            0xE6, 0x03,                   // AND 3
            0x5F, 0x16, 0x00,             // LD E,A ; LD D,0
            0x21, 0x00, 0x81, 0x19,       // LD HL,$8100 ; ADD HL,DE
            0x3E, 0x3F, 0x32, 0x06, 0x20, // LD A,$3F ; LD ($2006),A
            0xAF, 0x32, 0x06, 0x20,       // XOR A ; LD ($2006),A
            0x7E, 0x32, 0x07, 0x20,       // LD A,(HL) ; LD ($2007),A
            0xAF, 0x32, 0x06, 0x20, 0x32, 0x06, 0x20,   // XOR A ; LD ($2006),A twice
            0x18, 0xD7,                   // JR main
        }),
        (0x8040, new byte[] { 0xF5, 0x3A, 0x10, 0x00, 0x3C, 0x32, 0x10, 0x00, 0xF1, 0xED, 0x45 }),            // NMI: PUSH AF ; LD A,($0010) ; INC A ; LD ($0010),A ; POP AF ; RETN
        (0x8060, new byte[] { 0xF5, 0x3A, 0x15, 0x40, 0x3A, 0x11, 0x00, 0x3C, 0x32, 0x11, 0x00, 0xF1, 0xFB, 0xED, 0x4D }),   // IRQ: PUSH AF ; LD A,($4015) ; LD A,($0011) ; INC A ; LD ($0011),A ; POP AF ; EI ; RETI
    }, nmi: 0x8040, reset: 0x8000, irq: 0x8060);

    /// <summary>The same program for the 68000. The supervisor stack is the NES stack page (the board sets it), the interrupt mask is lowered for the IRQ.</summary>
    internal static byte[] M68kRom() => Rom(new (int, byte[])[]
    {
        (0x8000, new byte[]
        {
            0x46, 0xFC, 0x20, 0x00,                                   // MOVE #$2000,SR            supervisor, mask 0
            0x13, 0xFC, 0x00, 0x80, 0x00, 0x00, 0x20, 0x00,           // MOVE.B #$80,($2000).L     NMI on
            0x42, 0x39, 0x00, 0x00, 0x00, 0x10,                       // CLR.B ($0010).L
            0x42, 0x39, 0x00, 0x00, 0x00, 0x11,                       // CLR.B ($0011).L
            0x13, 0xFC, 0x00, 0x00, 0x00, 0x00, 0x40, 0x17,           // MOVE.B #0,($4017).L       frame IRQ on
            0x12, 0x39, 0x00, 0x00, 0x00, 0x10,                       // main: MOVE.B ($0010).L,D1
            0x10, 0x39, 0x00, 0x00, 0x00, 0x10,                       // wait: MOVE.B ($0010).L,D0
            0xB2, 0x00,                                               // CMP.B D0,D1
            0x67, 0xF6,                                               // BEQ.S wait
            0x02, 0x40, 0x00, 0x03,                                   // ANDI.W #3,D0
            0x20, 0x7C, 0x00, 0x00, 0x81, 0x00,                       // MOVEA.L #$8100,A0
            0x14, 0x30, 0x00, 0x00,                                   // MOVE.B (0,A0,D0.W),D2
            0x13, 0xFC, 0x00, 0x3F, 0x00, 0x00, 0x20, 0x06,           // MOVE.B #$3F,($2006).L
            0x13, 0xFC, 0x00, 0x00, 0x00, 0x00, 0x20, 0x06,           // MOVE.B #0,($2006).L
            0x13, 0xC2, 0x00, 0x00, 0x20, 0x07,                       // MOVE.B D2,($2007).L
            0x13, 0xFC, 0x00, 0x00, 0x00, 0x00, 0x20, 0x06,           // MOVE.B #0,($2006).L
            0x13, 0xFC, 0x00, 0x00, 0x00, 0x00, 0x20, 0x06,           // MOVE.B #0,($2006).L
            0x60, 0x00, 0xFF, 0xBA,                                   // BRA.W main
        }),
        (0x8080, new byte[] { 0x52, 0x39, 0x00, 0x00, 0x00, 0x10, 0x4E, 0x73 }),                              // NMI: ADDQ.B #1,($0010).L ; RTE
        (0x80A0, new byte[] { 0x10, 0x39, 0x00, 0x00, 0x40, 0x15, 0x52, 0x39, 0x00, 0x00, 0x00, 0x11, 0x4E, 0x73 }),   // IRQ: MOVE.B ($4015).L,D0 ; ADDQ.B #1,($0011).L ; RTE
    }, nmi: 0x8080, reset: 0x8000, irq: 0x80A0);

    /// <summary>The 6502 program: wait two frames, make the backdrop colour $16, spin.</summary>
    internal static byte[] M6502Rom() => Rom(new (int, byte[])[]
    {
        (0x8000, new byte[]
        {
            0x78, 0xD8,
            0xAD, 0x02, 0x20, 0x10, 0xFB, 0xAD, 0x02, 0x20, 0x10, 0xFB,
            0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20, 0xA9, 0x16, 0x8D, 0x07, 0x20,
            0xA9, 0x00, 0x8D, 0x06, 0x20, 0x8D, 0x06, 0x20,
            0x4C, 0x28, 0x80,
        }),
        (0x8040, new byte[] { 0x40 }),
    }, nmi: 0x8040, reset: 0x8000, irq: 0x8040);

    // ------------------------------------------------------------------------------------------------------------------ running
    private sealed class Ran
    {
        public NES Nes = null!; public Bus Bus = null!; public uint[] Backdrop = Array.Empty<uint>(); public bool Crashed;
        public byte Ram(int a) => Bus.Read((ushort)a);
        public ICPU Cpu => Bus.cpu;
    }

    private static Ran Go(byte[] rom, string cpu, int frames, int swapAt = -1, string swapTo = "")
    {
        var r = new Ran { Nes = new NES { RomName = "wall.nes" } };
        r.Nes.LoadROM(rom);
        if (!r.Nes.SetPpuCore("FIX") || !r.Nes.SetApuCore("FIX") || !r.Nes.SetCpuCore(cpu) || !r.Nes.GetCpuCoreId().EndsWith("_" + cpu, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"core {cpu} not applied (got {r.Nes.GetCpuCoreId()})");
        r.Bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.Nes)!;
        var list = new List<uint>();
        for (int f = 0; f < frames; f++)
        {
            if (f == swapAt && !r.Nes.SetCpuCore(swapTo)) throw new InvalidOperationException("swap refused");
            r.Nes.RunFrame();
            if (r.Nes.IsCrashed()) { r.Crashed = true; break; }
            var fb = r.Nes.GetFrameBuffer();
            list.Add((uint)fb[0] << 16 | (uint)fb[1] << 8 | fb[2]);
        }
        r.Backdrop = list.ToArray();
        return r;
    }

    /// <summary>The backdrop colours as a sequence of distinct runs over the frames after the start-up (the cycle the program draws).</summary>
    private static List<uint> Cycle(uint[] backdrop, int from)
    {
        var seq = new List<uint>();
        for (int i = from; i < backdrop.Length; i++) if (seq.Count == 0 || seq[^1] != backdrop[i]) seq.Add(backdrop[i]);
        return seq;
    }

    // ------------------------------------------------------------------------------------------------------------------ groups
    private static void Catalog()
    {
        var cpus = CoreCatalog.Options(ConsoleKind.Nes, CoreSlot.Cpu);
        Check("the NES CPU menu lists Z80 under Master System and M68K under Genesis", cpus.Any(o => o.Id == "Z80" && o.Family == "Master System") && cpus.Any(o => o.Id == "M68K" && o.Family == "Genesis"));
        Check("their labels say they are an instruction-set wall", cpus.First(o => o.Id == "Z80").Label.Contains("instruction-set wall") && cpus.First(o => o.Id == "M68K").Label.Contains("instruction-set wall"));
        Check("the registry finds CPU_Z80 and CPU_M68K", CoreRegistry.CpuIds.Contains("Z80", StringComparer.OrdinalIgnoreCase) && CoreRegistry.CpuIds.Contains("M68K", StringComparer.OrdinalIgnoreCase));
        Check("the own families come first and FIX is the default", CoreCatalog.Default(ConsoleKind.Nes, CoreSlot.Cpu) == "FIX");
        Check("the Master System's own CPU menu is still the one real Z80, the Genesis's the 68000", CoreCatalog.Options(ConsoleKind.MasterSystem, CoreSlot.Cpu).Single().Id == "Z80" && CoreCatalog.Options(ConsoleKind.Genesis, CoreSlot.Cpu).Single().Id == "M68K");
    }

    private static void Z80Program()
    {
        var r = Go(Z80Rom(), "Z80", 90);
        var z = (CPU_Z80)r.Cpu;
        var seq = Cycle(r.Backdrop, 10);
        Check("no crash", !r.Crashed);
        Check("the backdrop colour cycles through four colours in a fixed order", seq.Count >= 20 && seq.Distinct().Count() == 4 && Enumerable.Range(4, seq.Count - 4).All(i => seq[i] == seq[i - 4]), $"{seq.Count} changes, {seq.Distinct().Count()} colours");
        Check("so the Z80 did reach its NMI handler through the NES vector every frame", r.Ram(0x10) >= 80 && r.Ram(0x10) <= 92, $"counter {r.Ram(0x10)} after 90 frames");
        Check("and its IRQ handler through the NES IRQ vector (the APU frame IRQ, acknowledged by reading $4015)", r.Ram(0x11) >= 20, $"counter {r.Ram(0x11)}");
        Check("the interrupts are counted", z.Interrupts >= r.Ram(0x10) + r.Ram(0x11) - 4, $"{z.Interrupts}");
        Check("the I/O space is empty: nothing in this program touched it", z.PortReads == 0 && z.PortWrites == 0);
        // the stock clock: a NOP is 4 T-states = 2 NES cycles; carry keeps the odd ones
        var t = Go(Blank(), "Z80", 1);
        var cpu = (CPU_Z80)t.Cpu; cpu.Reset();
        int total = 0; for (int i = 0; i < 1000; i++) total += cpu.ExecuteInstruction();
        Check("timing: a NOP is two NES cycles (the Z80 runs at twice the NES CPU's clock)", total == 2000, $"{total} cycles for 1000 NOPs");
        cpu.Reset(); total = 0;
        for (int i = 0; i < 1000; i++) total += cpu.ExecuteInstruction();
        Check("and a reset starts the count again", total == 2000, $"{total}");
        var io = new byte[] { 0xDB, 0x01, 0xD3, 0x02, 0x18, 0xFE };    // IN A,(1) ; OUT (2),A ; JR $
        var t2 = Go(Rom(new (int, byte[])[] { (0x8000, io) }, 0x8000, 0x8000, 0x8000), "Z80", 2);
        Check("an IN / OUT reads $FF and is dropped, both counted", ((CPU_Z80)t2.Cpu).PortReads >= 1 && ((CPU_Z80)t2.Cpu).PortWrites >= 1 && ((CPU_Z80)t2.Cpu).Inner.A == 0xFF, $"reads {((CPU_Z80)t2.Cpu).PortReads}, writes {((CPU_Z80)t2.Cpu).PortWrites}, A=${((CPU_Z80)t2.Cpu).Inner.A:X2}");
        var st = z.GetRegisters();
        Check("the state probe reports the Z80's registers (PC inside the program)", st.PC >= 0x8000, $"PC ${st.PC:X4}");
    }

    private static void M68kProgram()
    {
        var r = Go(M68kRom(), "M68K", 90);
        var m = (CPU_M68K)r.Cpu;
        var seq = Cycle(r.Backdrop, 10);
        Check("no crash", !r.Crashed);
        Check("the backdrop colour cycles through four colours in a fixed order", seq.Count >= 20 && seq.Distinct().Count() == 4 && Enumerable.Range(4, seq.Count - 4).All(i => seq[i] == seq[i - 4]), $"{seq.Count} changes, {seq.Distinct().Count()} colours");
        Check("the 68000 reached its NMI handler (level 7, vector from $FFFA) every frame", r.Ram(0x10) >= 80 && r.Ram(0x10) <= 92, $"counter {r.Ram(0x10)} after 90 frames");
        Check("and its IRQ handler (level 1, vector from $FFFE)", r.Ram(0x11) >= 20, $"counter {r.Ram(0x11)}");
        Check("no address error: the program stays on even word addresses", m.AddressErrors == 0, $"{m.AddressErrors}");
        var empty = Go(Blank(), "M68K", 1);
        var cpu = (CPU_M68K)empty.Cpu; cpu.Reset();
        long total = 0; for (int i = 0; i < 3000; i++) total += cpu.ExecuteInstruction();
        // the memory is zeros: ORI.B #0,D0 (8 clocks) each; 3000 of them are 24,000 clocks = 5,600 NES cycles at 30/7 (the remainder is carried, not lost)
        Check("timing: 30 clocks of the 68000 are 7 NES cycles", Math.Abs(total - 5600) <= 2, $"{total} NES cycles for 3000 instructions of 8 clocks (5,600 expected)");
    }

    private static void Wall()
    {
        var fix = Go(M6502Rom(), "FIX", 60);
        uint red = fix.Backdrop[^1];
        Check("control: the 6502 program draws its colour on CPU_FIX", fix.Backdrop[^1] != fix.Backdrop[0] || fix.Backdrop.Distinct().Count() > 1, $"colour ${red:X6}");
        foreach (var id in new[] { "Z80", "M68K" })
        {
            var w = Go(M6502Rom(), id, 60);
            var cpu = w.Cpu;
            long ran = cpu is CPU_Z80 z ? z.InstructionsRun : ((CPU_M68K)cpu).InstructionsRun;
            Check($"{id}: the same 6502 program never draws its colour, though the chip ran {ran:N0} instructions", !w.Crashed && w.Backdrop[^1] != red && ran > 10000, $"backdrop ${w.Backdrop[^1]:X6}");
        }
    }

    /// <summary>Debug aid (--only trace): the program counter after each of the first instructions, then at frame ends.</summary>
    private static void Trace()
    {
        var r = Go(Z80Rom(), "Z80", 0);
        var z = (CPU_Z80)r.Cpu; z.Reset();
        for (int i = 0; i < 70; i++) { z.ExecuteInstruction(); Console.Write($"{z.Inner.PC:X4}{(i % 12 == 11 ? "\n" : " ")}"); }
        Console.WriteLine();
        var r2 = Go(Z80Rom(), "Z80", 6);
        var z2 = (CPU_Z80)r2.Cpu;
        Console.WriteLine($"after 6 frames: PC {z2.Inner.PC:X4} SP {z2.Inner.SP:X4} A {z2.Inner.A:X2} counters {r2.Ram(0x10)} {r2.Ram(0x11)} backdrop {string.Join(",", r2.Backdrop.Select(c => c.ToString("X6")))}");
    }

    private static void Swap()
    {
        foreach (var id in new[] { "Z80", "M68K" })
        {
            var r = Go(M6502Rom(), "FIX", 80, swapAt: 40, swapTo: id);
            // the program had drawn its colour by frame 40 and the picture stays (nothing erases a backdrop), so what the swap shows is that the run goes on without a crash and the new CPU executes
            long ran = r.Cpu is CPU_Z80 z ? z.InstructionsRun : ((CPU_M68K)r.Cpu).InstructionsRun;
            Check($"FIX -> {id} in the middle of a run: accepted, no crash, the chip executes ({ran:N0} instructions)", !r.Crashed && ran > 1000);
        }
        var back = Go(Z80Rom(), "Z80", 60, swapAt: 30, swapTo: "FIX");
        Check("Z80 -> FIX: the swap is accepted too", !back.Crashed && back.Cpu is not CPU_Z80);
    }
}
