using System;
using System.IO;
using System.Text;
using System.Text.Json;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// Diagnostic mode built for the SMB3 "red clouds" boot-freeze investigation, generally useful
/// for any "game hangs/crashes early during boot" bug: runs N whole frames via RunFrame() (fast),
/// then switches to single-instruction stepping via NES.StepInstruction() and keeps a ring buffer
/// of (index, PC, SP, P, opcode) so the exact instruction stream leading into a crash/hang can be
/// inspected after the fact.
///
/// Caveat: each row's PC/opcode are read via a non-executing PeekCpu *before* that step runs - they
/// show what's at that address, not proof it was actually dispatched. If a hardware interrupt (or
/// BRK) preempts the fetch, the row still shows the byte sitting at PC; the *next* row's PC is what
/// reveals the redirect (e.g. jumping to the IRQ/BRK vector instead of the address shown). Don't
/// infer "this opcode executed" from one row alone - confirm via the following row's PC/SP delta.
///
/// Usage: --irqtrace --rom path.nes --cpu ID --ppu ID --apu ID [--frames-before N] [--ring N]
///        [--max-instr N] [--dump-addr XXXX --dump-len N] [--watch XXXX,YYYY,...]
///        (dump mode prints raw bytes at a CPU address instead of tracing - e.g. to disassemble an
///        IRQ handler at its vector; --watch adds extra columns showing the live byte at each given
///        CPU address on every traced row, e.g. to follow a specific RAM variable across a crash)
/// </summary>
internal static class IrqTraceCli
{
    public static int Run(string[] args)
    {
        string? romPath = null, cpu = null, ppu = null, apu = null, dumpAddrStr = null, watchStr = null;
        int framesBefore = 12;
        int ring = 400;
        int maxInstr = 4_000_000;
        int dumpLen = 96;
        long mapperAtInstr = -1;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom": romPath = args[++i]; break;
                case "--cpu": cpu = args[++i]; break;
                case "--ppu": ppu = args[++i]; break;
                case "--apu": apu = args[++i]; break;
                case "--frames-before": framesBefore = int.Parse(args[++i]); break;
                case "--ring": ring = int.Parse(args[++i]); break;
                case "--max-instr": maxInstr = int.Parse(args[++i]); break;
                case "--dump-addr": dumpAddrStr = args[++i]; break;
                case "--dump-len": dumpLen = int.Parse(args[++i]); break;
                case "--watch": watchStr = args[++i]; break;
                case "--mapper-at": mapperAtInstr = long.Parse(args[++i]); break;
            }
        }

        if (romPath == null)
        {
            Console.Error.WriteLine("Usage: --irqtrace --rom <path.nes> --cpu ID --ppu ID --apu ID [--frames-before N] [--ring N] [--max-instr N] [--dump-addr XXXX --dump-len N] [--watch XXXX,YYYY,...]");
            return 2;
        }

        ushort[] watch = watchStr == null
            ? Array.Empty<ushort>()
            : Array.ConvertAll(watchStr.Split(','), s => Convert.ToUInt16(s, 16));

        var nes = new NES { RomName = Path.GetFileName(romPath) };
        nes.LoadROM(File.ReadAllBytes(romPath));
        if (cpu != null) nes.SetCpuCore(cpu);
        if (ppu != null) nes.SetPpuCore(ppu);
        if (apu != null) nes.SetApuCore(apu);

        ushort nmiVec = (ushort)(nes.PeekCpu(0xFFFA) | (nes.PeekCpu(0xFFFB) << 8));
        ushort resetVec = (ushort)(nes.PeekCpu(0xFFFC) | (nes.PeekCpu(0xFFFD) << 8));
        ushort irqVec = (ushort)(nes.PeekCpu(0xFFFE) | (nes.PeekCpu(0xFFFF) << 8));
        Console.WriteLine($"NMI vector=${nmiVec:X4} RESET vector=${resetVec:X4} IRQ/BRK vector=${irqVec:X4}");

        if (dumpAddrStr != null)
        {
            ushort dumpAddr = Convert.ToUInt16(dumpAddrStr, 16);
            for (int i = 0; i < dumpLen; i++) Console.Write($"{nes.PeekCpu((ushort)(dumpAddr + i)):X2} ");
            Console.WriteLine();
            return 0;
        }

        for (int i = 0; i < framesBefore && !nes.IsCrashed(); i++) nes.RunFrame();

        var buf = new (long Idx, ushort Pc, byte Op, ushort Sp, byte P, byte A, byte X, byte Y, byte[] Watch)[ring];
        int head = 0; int count = 0;
        long instrIndex = 0;
        while (!nes.IsCrashed() && instrIndex < maxInstr)
        {
            var regsBefore = nes.GetCpuRegs();
            byte opcode = nes.PeekCpu(regsBefore.PC);
            byte[] watchVals = new byte[watch.Length];
            for (int w = 0; w < watch.Length; w++) watchVals[w] = nes.PeekCpu(watch[w]);
            buf[head] = (instrIndex, regsBefore.PC, opcode, regsBefore.SP, regsBefore.P, regsBefore.A, regsBefore.X, regsBefore.Y, watchVals);
            head = (head + 1) % ring; if (count < ring) count++;
            if (instrIndex == mapperAtInstr)
            {
                var mapperJsonOpts = new JsonSerializerOptions { IncludeFields = true };
                Console.WriteLine($"[mapper @ instr {instrIndex}] {JsonSerializer.Serialize(nes.GetMapperState(), nes.GetMapperState().GetType(), mapperJsonOpts)}");
            }
            nes.StepInstruction();
            instrIndex++;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Crashed={nes.IsCrashed()} CrashInfo={nes.GetCrashInfo()} InstrExecuted={instrIndex}");
        var header = new StringBuilder("idx      PC     op SP   P    A  X  Y");
        foreach (var w in watch) header.Append($"  ${w:X4}");
        sb.AppendLine(header.ToString());
        int start = count < ring ? 0 : head;
        for (int k = 0; k < count; k++)
        {
            var e = buf[(start + k) % ring];
            var line = new StringBuilder($"{e.Idx,-8} {e.Pc:X4}   {e.Op:X2} {e.Sp:X4} {e.P:X2}  {e.A:X2} {e.X:X2} {e.Y:X2}");
            foreach (var v in e.Watch) line.Append($"   {v:X2} ");
            sb.AppendLine(line.ToString());
        }
        Console.WriteLine(sb.ToString());
        return 0;
    }
}
