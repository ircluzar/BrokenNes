using System;
using System.Collections.Generic;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--m68ktest --selftest</c>: hand-written checks of what the SingleStepTests vectors cannot reach: reset, interrupts (mask, level 7, the acknowledge, STOP waking), the privilege
/// violation, the exception vectors, address-error frames and the double fault, the Genesis TAS, the trace exception, the byte lanes, RESET, stack switching, that every clock the CPU
/// counts is reported to the bus, and that all 65,536 opcodes run without a host exception.
/// </summary>
internal static class M68kSelfTest
{
    private static int pass, fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  (" + detail + ")")}");
    }

    private static ushort Word(M68kFlatMachine m, int at) => (ushort)(m.Ram[at & 0xFFFF] << 8 | m.Ram[at + 1 & 0xFFFF]);
    private static uint LongAt(M68kFlatMachine m, int at) => (uint)Word(m, at) << 16 | Word(m, at + 2);
    private static ushort Word(M68kFlatMachine m, uint at) => Word(m, (int)at);
    private static uint LongAt(M68kFlatMachine m, uint at) => LongAt(m, (int)at);

    /// <summary>A CPU in supervisor mode at <paramref name="pc"/> with the stack at $8000 and the user stack at $7000, the vector table pointing every vector at its own handler ($3000 + 16 * vector).</summary>
    private static (M68k<M68kFlatBus> cpu, M68kFlatMachine m) Make(int pc = 0x1000, ushort sr = 0x2000)
    {
        var m = new M68kFlatMachine();
        for (int v = 2; v < 64; v++) m.Long(v * 4, (uint)(0x3000 + 16 * v));
        for (int v = 2; v < 64; v++) m.Load(0x3000 + 16 * v, 0x4E71, 0x4E71, 0x4E71);
        var cpu = new M68k<M68kFlatBus>(new M68kFlatBus { M = m });
        cpu.Power();
        cpu.WriteSr(sr);
        cpu.A[7] = 0x8000;
        if ((sr & 0x2000) != 0) cpu.SpOther = 0x7000; else { cpu.SpOther = 0x8000; cpu.A[7] = 0x7000; }
        Start(cpu, m, pc);
        return (cpu, m);
    }

    private static void Start(M68k<M68kFlatBus> cpu, M68kFlatMachine m, int pc)
    {
        cpu.Pc = (uint)pc + 4;
        cpu.Ir = Word(m, pc); cpu.Irc = Word(m, pc + 2);
    }

    private static uint Handler(M68k<M68kFlatBus> cpu) => cpu.Pc - 4;

    /// <summary>Put code at <paramref name="pc"/> and refill the prefetch queue from it.</summary>
    private static void Code(M68k<M68kFlatBus> cpu, M68kFlatMachine m, int pc, params ushort[] words)
    {
        m.Load(pc, words);
        Start(cpu, m, pc);
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("== 68000 self-test");

        // ---- reset
        {
            var m = new M68kFlatMachine();
            m.Long(0, 0x8000); m.Long(4, 0x1000); m.Load(0x1000, 0x4E71, 0x4E71);
            var cpu = new M68k<M68kFlatBus>(new M68kFlatBus { M = m });
            cpu.Power();
            cpu.Reset();
            Check("reset: SSP and PC come from vectors 0 and 1, the queue is filled, supervisor with the mask at 7",
                  cpu.A[7] == 0x8000 && Handler(cpu) == 0x1000 && cpu.Ir == 0x4E71 && cpu.Supervisor && cpu.IntMask == 7, $"A7={cpu.A[7]:X} pc={Handler(cpu):X}");
            Check("reset: two long reads and two prefetches (24 clocks, 6 bus cycles)", cpu.Cycles == 24 && m.Reads == 6, $"clocks={cpu.Cycles} reads={m.Reads}");
        }

        // ---- interrupts
        {
            var (cpu, m) = Make(0x1000, 0x2500);
            Code(cpu, m, 0x1000, 0x4E71, 0x4E71);
            m.IrqLevel = 6;
            int c = cpu.Step();
            Check("interrupt level 6 over mask 5: taken, autovector 30, mask becomes 6", Handler(cpu) == 0x3000 + 16 * 30 && cpu.IntMask == 6 && cpu.Supervisor && m.AckCount == 1, $"pc={Handler(cpu):X} mask={cpu.IntMask}");
            Check("interrupt frame: SR at SP, return address at SP+2, SP down 6", cpu.A[7] == 0x8000 - 6 && Word(m, 0x8000 - 6) == 0x2500 && LongAt(m, 0x8000 - 4) == 0x1000, $"A7={cpu.A[7]:X}");
            Check("interrupt costs 54 clocks (44 plus the Genesis autovector's synchronised acknowledge)", c == 54, $"{c}");
        }
        {
            var (cpu, m) = Make(0x1000, 0x2500);
            Code(cpu, m, 0x1000, 0x4E71, 0x4E71);
            m.IrqLevel = 4;
            int c = cpu.Step();
            Check("interrupt level 4 under mask 5: not taken, the NOP runs", m.AckCount == 0 && c == 4 && Handler(cpu) == 0x1002, $"c={c}");
        }
        {
            var (cpu, m) = Make(0x1000, 0x2700);
            Code(cpu, m, 0x1000, 0x4E71, 0x4E71);
            m.IrqLevel = 6;
            cpu.Step();
            Check("interrupt level 6 under mask 7: not taken", m.AckCount == 0);
            m.IrqLevel = 7;
            cpu.Step();
            Check("level 7 is taken whatever the mask", m.AckCount == 1 && Handler(cpu) == 0x3000 + 16 * 31);
        }
        {
            // the interrupt is looked at between instructions, once: raise it, and the instruction in progress finishes first
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x4E71, 0x4E71, 0x4E71);
            cpu.Step();
            m.IrqLevel = 2;
            cpu.Step();
            Check("an interrupt raised after an instruction is taken by the next step, not the one before", m.AckCount == 1);
        }

        // ---- STOP
        {
            var (cpu, m) = Make(0x1000, 0x2700);
            Code(cpu, m, 0x1000, 0x4E72, 0x2000, 0x4E71);
            int c = cpu.Step();
            Check("STOP #$2000: 4 clocks, SR loaded, stopped, nothing fetched", c == 4 && cpu.Stopped && cpu.ReadSr() == 0x2000 && m.Reads == 0, $"c={c} stopped={cpu.Stopped} sr={cpu.ReadSr():X}");
            long reads = m.Reads;
            for (int i = 0; i < 5; i++) cpu.Step();
            Check("a stopped CPU idles 4 clocks per step and does not touch the bus", m.Reads == reads && cpu.Stopped);
            m.IrqLevel = 2; m.Load(0x3000 + 16 * 26, 0x4E73);
            cpu.Step();
            Check("an interrupt ends the stop; the stacked return address is the instruction after STOP", !cpu.Stopped && LongAt(m, cpu.A[7] + 2) == 0x1004 && cpu.IntMask == 2, $"ret={LongAt(m, cpu.A[7] + 2):X}");
        }

        // ---- privilege violation and exceptions
        {
            var (cpu, m) = Make(0x1000, 0x0004);   // user mode, Z set
            Code(cpu, m, 0x1000, 0x027C, 0xF8FF);        // ANDI #$F8FF,SR
            cpu.Step();
            Check("ANDI to SR in user mode: privilege violation (vector 8), frame on the supervisor stack, SR as it was",
                  Handler(cpu) == 0x3000 + 16 * 8 && cpu.Supervisor && cpu.A[7] == 0x7000 - 6 + 0x1000 && Word(m, cpu.A[7]) == 0x0004 && LongAt(m, cpu.A[7] + 2) == 0x1000, $"A7={cpu.A[7]:X} pc={Handler(cpu):X}");
            Check("the user stack pointer was put aside", cpu.SpOther == 0x7000, $"{cpu.SpOther:X}");
        }
        void Exc(string name, ushort[] code, int vector, uint returnAddress, Action<M68k<M68kFlatBus>>? setup = null, ushort sr = 0x2000)
        {
            var (cpu, m) = Make(0x1000, sr);
            Code(cpu, m, 0x1000, code);
            setup?.Invoke(cpu);
            Start(cpu, m, 0x1000);
            cpu.Step();
            Check($"{name}: vector {vector}, stacked PC {returnAddress:X}", Handler(cpu) == 0x3000 + 16 * vector && LongAt(m, cpu.A[7] + 2) == returnAddress, $"pc={Handler(cpu):X} ret={LongAt(m, cpu.A[7] + 2):X}");
        }
        Exc("TRAP #1", new ushort[] { 0x4E41, 0x4E71 }, 33, 0x1002);
        Exc("TRAP #15", new ushort[] { 0x4E4F, 0x4E71 }, 47, 0x1002);
        Exc("TRAPV with V set", new ushort[] { 0x4E76, 0x4E71 }, 7, 0x1002, c => c.V = true);
        Exc("illegal opcode $4AFC", new ushort[] { 0x4AFC }, 4, 0x1000);
        Exc("line A $A000", new ushort[] { 0xA000 }, 10, 0x1000);
        Exc("line F $F000", new ushort[] { 0xF000 }, 11, 0x1000);
        Exc("DIVU #0,D0", new ushort[] { 0x80FC, 0x0000, 0x4E71 }, 5, 0x1004);
        Exc("CHK of a negative register", new ushort[] { 0x4180, 0x4E71 }, 6, 0x1002, c => { c.D[0] = 0xFFFF; c.D[1] = 100; });
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x4E71, 0x4E71, 0x4E71);
            m.Load(0x3000 + 16 * 4, 0x4E73);
            m.Load(0x3000 + 16 * 4, 0x4E71);
            cpu.Step(); cpu.Step();
            Check("untouched flags and mask after plain instructions", cpu.IntMask == 0 && cpu.Supervisor);
        }

        // ---- address errors
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x3010, 0x4E71);          // MOVE.W (A0),D0
            cpu.A[0] = 0x2001;
            int c = cpu.Step();
            uint sp = cpu.A[7];
            Check("odd word read: vector 3, 14-byte frame", Handler(cpu) == 0x3000 + 16 * 3 && sp == 0x8000 - 14 && cpu.AddressErrorCount == 1, $"A7={sp:X} pc={Handler(cpu):X}");
            Check("address-error frame: status (read, supervisor data), fault address, instruction word, SR",
                  Word(m, (int)sp) == (0x3010 & 0xFFE0 | 0x15) && LongAt(m, (int)sp + 2) == 0x2001 && Word(m, (int)sp + 6) == 0x3010 && Word(m, (int)sp + 8) == 0x2000,
                  $"status={Word(m, (int)sp):X4} addr={LongAt(m, (int)sp + 2):X} ir={Word(m, (int)sp + 6):X4} sr={Word(m, (int)sp + 8):X4}");
            Check("an address error costs 50 clocks (4 aborted + 28 frame + 8 vector + 10 handler fetch)", c == 50, $"{c}");
        }
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x20C1, 0x4E71);          // MOVE.L D1,(A0)+  (a write)
            cpu.A[0] = 0x2003; cpu.D[1] = 0x12345678;
            cpu.Step();
            uint sp = cpu.A[7];
            Check("odd long write: nothing was written, status says write", Handler(cpu) == 0x3000 + 16 * 3 && Word(m, 0x2002) == 0 && (Word(m, (int)sp) & 0x10) == 0, $"status={Word(m, (int)sp):X4}");
        }
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x4EF8, 0x2001);          // JMP $2001.w
            cpu.Step();
            uint sp = cpu.A[7];
            Check("jump to an odd address: the instruction fetch faults (program space), fault address is the target",
                  Handler(cpu) == 0x3000 + 16 * 3 && LongAt(m, (int)sp + 2) == 0x2001 && (Word(m, (int)sp) & 7) == 6, $"status={Word(m, (int)sp):X4}");
        }
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x4E41, 0x4E71);
            cpu.A[7] = 0x7FFF;                       // an odd supervisor stack
            cpu.Step();
            Check("an exception whose frame cannot be stacked (odd stack) halts the processor", cpu.Halted, "");
            long reads = m.Reads;
            int c = cpu.Step();
            Check("a halted CPU idles and reads nothing, even with an interrupt pending", m.Reads == reads && c == 4);
            m.Long(0, 0x8000); m.Long(4, 0x1000);
            cpu.Reset();
            Check("reset restarts a halted CPU", !cpu.Halted);
        }
        {
            var (cpu, m) = Make(0x1000, 0x0000);     // user mode
            Code(cpu, m, 0x1000, 0x3010, 0x4E71);
            cpu.A[0] = 0x2001;
            cpu.Step();
            uint sp = cpu.A[7];
            Check("an address error in user mode stacks on the supervisor stack with user-data function code", sp == 0x7000 - 14 + 0x1000 && (Word(m, (int)sp) & 7) == 1, $"A7={sp:X} status={Word(m, (int)sp):X4}");
        }

        // ---- TAS, the Genesis way and the other
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x4AD0, 0x4E71);          // TAS (A0)
            cpu.A[0] = 0x2000;
            cpu.Step();
            Check("TAS (A0) completes its write by default ($80 stored, Z set)", m.Ram[0x2000] == 0x80 && cpu.Z && !cpu.N);
        }
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            cpu.TasWritesBack = false;
            Code(cpu, m, 0x1000, 0x4AD0, 0x4E71, 0x4A00, 0x4E71);
            cpu.A[0] = 0x2000;
            cpu.Step();
            Check("TAS (A0) with the Genesis setting: the write never happens, flags still set", m.Ram[0x2000] == 0x00 && cpu.Z);
            cpu.D[0] = 0;
            m.Load(0x1004, 0x4AC0, 0x4E71);          // TAS D0
            Start(cpu, m, 0x1004);
            cpu.Step();
            Check("TAS D0 always writes the register ($80 in the low byte)", (cpu.D[0] & 0xFF) == 0x80);
        }

        // ---- trace
        {
            var (cpu, m) = Make(0x1000, 0xA000);     // T set, supervisor
            Code(cpu, m, 0x1000, 0x4E71, 0x4E71, 0x4E71);
            cpu.Step();
            Check("trace bit set: the instruction runs, the exception waits for the next step", Handler(cpu) == 0x1002 && cpu.Trace);
            cpu.Step();
            Check("trace exception (vector 9) follows the instruction; stacked PC is the next instruction, T clear in the handler", Handler(cpu) == 0x3000 + 16 * 9 && !cpu.Trace && LongAt(m, cpu.A[7] + 2) == 0x1002, $"pc={Handler(cpu):X}");
        }

        // ---- byte lanes
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            var log = new List<(uint a, bool u, bool l, ushort d)>();
            m.WriteHook = (a, u, l, d) => log.Add((a, u, l, d));
            Code(cpu, m, 0x1000, 0x1081, 0x1081, 0x4E71);  // MOVE.B D1,(A0) twice (A0 even, then odd)
            cpu.A[0] = 0x2000; cpu.D[1] = 0xAB;
            cpu.Step();
            cpu.A[0] = 0x2001;
            cpu.Step();
            Check("byte write to an even address: upper strobe only, the byte on both halves", log.Count == 2 && log[0].u && !log[0].l && log[0].d == 0xABAB && log[0].a == 0x2000, $"{(log.Count > 0 ? log[0].ToString() : "")}");
            Check("byte write to an odd address: lower strobe only", log[1].l && !log[1].u && log[1].a == 0x2000, $"{log[1]}");
            m.Ram[0x2000] = 0x11; m.Ram[0x2001] = 0x22;
            m.Load(0x1004, 0x1010, 0x1210, 0x4E71);  // MOVE.B (A0),D0 ; MOVE.B (A0),D1
            cpu.A[0] = 0x2001; Start(cpu, m, 0x1004);
            cpu.Step();
            cpu.A[0] = 0x2000;
            cpu.Step();
            Check("byte reads take the byte of the right lane", (cpu.D[0] & 0xFF) == 0x22 && (cpu.D[1] & 0xFF) == 0x11, $"{cpu.D[0] & 0xFF:X} {cpu.D[1] & 0xFF:X}");
        }

        // ---- RESET instruction, stack switching
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x4E70, 0x4E71);
            int c = cpu.Step();
            Check("RESET: 132 clocks, pulses the reset line", c == 132 && m.ResetPulsed, $"c={c}");
        }
        {
            var (cpu, m) = Make(0x1000, 0x2000);
            Code(cpu, m, 0x1000, 0x46FC, 0x0000, 0x4E71);  // MOVE #0,SR : to user mode
            cpu.Step();
            Check("MOVE to SR into user mode swaps the stack pointers", !cpu.Supervisor && cpu.A[7] == 0x7000 && cpu.SpOther == 0x8000, $"A7={cpu.A[7]:X}");
        }

        // ---- bus accounting: every clock the CPU counts reaches the bus
        {
            var m = new M68kFlatMachine();
            m.Load(0x100, 0x41F8, 0x2000, 0x43F8, 0x3000, 0x243C, 0, 0x0FFF, 0x32D8, 0x51CA, 0xFFFC, 0x7000, 0xD081, 0x5281, 0x4E75, 0x4E71, 0x4E71);
            var cpu = new M68k<M68kFlatBus>(new M68kFlatBus { M = m });
            cpu.Power();
            cpu.Supervisor = true; cpu.A[7] = 0x8000;
            Start(cpu, m, 0x100);
            long steps = 0;
            for (; steps < 20000; steps++) cpu.Step();
            Check("every clock the CPU counts is delivered to the bus (bus cycles + idle = counted clocks)", m.BusClocks + m.IdleClocks == cpu.Cycles, $"bus={m.BusClocks} idle={m.IdleClocks} cpu={cpu.Cycles}");
        }

        // ---- every opcode runs without a host exception
        {
            int crashed = 0; string first = "";
            for (int op = 0; op < 65536; op++)
            {
                var m = new M68kFlatMachine();
                m.Load(0x1000, (ushort)op, 0x0010, 0x0020, 0x4E71);
                for (int v = 2; v < 64; v++) m.Long(v * 4, 0x3000);
                m.Load(0x3000, 0x4E71, 0x4E71, 0x4E71);
                var cpu = new M68k<M68kFlatBus>(new M68kFlatBus { M = m });
                try
                {
                    cpu.Power();
                    cpu.Supervisor = true; cpu.A[7] = 0x8000; cpu.SpOther = 0x7000;
                    for (int i = 0; i < 8; i++) { cpu.D[i] = 0x00001230u + (uint)i * 2; cpu.A[i] = 0x2000u + (uint)i * 0x100; }
                    cpu.A[7] = 0x8000;
                    Start(cpu, m, 0x1000);
                    cpu.Step();
                }
                catch (Exception e) { crashed++; if (first == "") first = $"opcode {op:X4}: {e.GetType().Name}"; }
            }
            Check("all 65,536 opcodes execute without a host exception", crashed == 0, $"{crashed} crashed; first: {first}");
        }

        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
