using System;
using System.Diagnostics;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86bench [--n millions]</c>: host speed of the i386 core alone (no board), in million instructions per second, on a small flat
/// 32-bit protected-mode loop of everyday instructions (MOV r/m both ways, ADD, SHL, AND, a byte store, DEC, JNZ). For before / after
/// numbers when optimising the core; the game's own number comes from <c>--x86boot ... --ips</c>.
/// </summary>
internal static class X86BenchCli
{
    public static int Run(string[] args)
    {
        int n = 30;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--n") n = int.Parse(args[i + 1]);
        bool tight = Array.IndexOf(args, "--tight") >= 0;   // just DEC ECX / JNZ: the per-instruction overhead
        var cpu = new I386(4, 4 * 1024 * 1024, new X86TestCli.TestBus());
        byte[] code =
        {
            0xB9, 0, 0, 0, 0,          // mov ecx, iterations
            0x8B, 0x06,                // l: mov eax, [esi]
            0x01, 0xD8,                // add eax, ebx
            0x89, 0x46, 0x04,          // mov [esi+4], eax
            0xC1, 0xE0, 0x03,          // shl eax, 3
            0x25, 0xFF, 0, 0, 0,       // and eax, 0xff
            0x88, 0x1C, 0x07,          // mov [edi+eax], bl
            0x49,                      // dec ecx
            0x75, 0xEB,                // jnz l
            0xF4,                      // hlt
        };
        if (tight) code = new byte[] { 0xB9, 0, 0, 0, 0, 0x49, 0x75, 0xFD, 0xF4 };
        uint iters = (uint)(n * 1_000_000L / (tight ? 2 : 8));
        BitConverter.GetBytes(iters).CopyTo(code, 1);
        double best = 0;
        for (int round = 0; round < 3; round++)
        {
            cpu.ResetPm(0x1000);
            code.CopyTo(cpu.Mem, 0x1000);
            cpu.Cycle = 0; cpu.R[6] = 0x2000; cpu.R[7] = 0x10000; cpu.R[3] = 0x1234567;
            var sw = Stopwatch.StartNew();
            while (!cpu.Halt) cpu.Step(256);
            double mips = cpu.Cycle / sw.Elapsed.TotalSeconds / 1e6;
            best = Math.Max(best, mips);
            Console.WriteLine($"x86bench: round {round + 1}: {cpu.Cycle:N0} instructions in {sw.Elapsed.TotalSeconds:F2} s = {mips:F1} M instr/s");
        }
        Console.WriteLine($"x86bench: best {best:F1} M instr/s");
        return 0;
    }
}
