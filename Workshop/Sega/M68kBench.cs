using System;
using System.Collections.Generic;
using System.Diagnostics;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>A flat 64 KB machine for the benchmark and the self-test: word-wide RAM, optional interrupt level and acknowledge, a count of the bus calls.</summary>
internal sealed class M68kFlatMachine
{
    public readonly byte[] Ram = new byte[0x10000];
    public int IrqLevel;
    public long Reads, Writes, IdleClocks, BusClocks;
    public int AckCount;
    public bool ResetPulsed;
    public Action<uint, bool, bool, ushort>? WriteHook;

    public void Load(int at, params ushort[] words)
    {
        foreach (ushort w in words) { Ram[at++ & 0xFFFF] = (byte)(w >> 8); Ram[at++ & 0xFFFF] = (byte)w; }
    }

    public void Long(int at, uint v) { Load(at, (ushort)(v >> 16), (ushort)v); }
}

internal struct M68kFlatBus : IM68kBus
{
    public M68kFlatMachine M;

    public ushort Read(uint address, bool upper, bool lower, int cycles)
    {
        M.Reads++; M.BusClocks += cycles;
        int a = (int)(address & 0xFFFE);
        return (ushort)(M.Ram[a] << 8 | M.Ram[a + 1 & 0xFFFF]);
    }

    public void Write(uint address, bool upper, bool lower, ushort data, int cycles)
    {
        M.Writes++; M.BusClocks += cycles;
        int a = (int)(address & 0xFFFE);
        if (upper) M.Ram[a] = (byte)(data >> 8);
        if (lower) M.Ram[a + 1 & 0xFFFF] = (byte)data;
        M.WriteHook?.Invoke(address, upper, lower, data);
    }

    public void Idle(int cycles) { M.IdleClocks += cycles; }
    public int InterruptLevel => M.IrqLevel;
    public int Acknowledge(int level, int cycles) { M.AckCount++; M.BusClocks += cycles; return 24 + level; }
    public void ResetLine(int cycles) { M.ResetPulsed = true; M.IdleClocks += cycles; }
}

/// <summary><c>--m68ktest --bench</c>: how many 68000 clocks per second the core executes on hand-assembled kernels (flat 64 KB RAM, no timing work in the bus), against the Genesis's 7.67 million.</summary>
internal static class M68kBench
{
    private const double GenesisHz = 53_693_175.0 / 7.0;

    // machine code, one kernel at a time, started at $0100
    private static readonly (string name, ushort[] code)[] Kernels =
    {
        // copy 4096 words with (A0)+,(A1)+ and DBRA: memory-bound, short instructions
        ("copy loop (MOVE.W (A0)+,(A1)+ ; DBRA)", new ushort[]
        {
            0x41F8, 0x2000,             // LEA $2000.w,A0
            0x43F8, 0x3000,             // LEA $3000.w,A1
            0x243C, 0x0000, 0x0FFF,     // MOVE.L #$0FFF,D2
            0x32D8,                     // loop: MOVE.W (A0)+,(A1)+
            0x51CA, 0xFFFC,             // DBRA D2,loop
            0x60EA,                     // BRA.S start ($0100)
        }),
        // register arithmetic and a branch: ALU and flag work
        ("ALU loop (ADD.L, ADDQ.L, CMP.L, BNE)", new ushort[]
        {
            0x7000,                     // MOVEQ #0,D0
            0x7201,                     // MOVEQ #1,D1
            0x263C, 0x0000, 0x03E8,     // MOVE.L #1000,D3
            0xD081,                     // loop: ADD.L D1,D0
            0x5281,                     // ADDQ.L #1,D1
            0xB283,                     // CMP.L D3,D1
            0x66F8,                     // BNE.S loop
            0x60EC,                     // BRA.S start
        }),
        // calls, stack traffic, shifts and a multiply: what a game's inner code mostly is
        ("call loop (BSR, MOVEM, LSL, MULU, RTS)", new ushort[]
        {
            0x4FF8, 0x2000,             // LEA $2000.w,A7
            0x7C0A,                     // MOVEQ #10,D6
            0x6100, 0x0008,             // call: BSR.W sub
            0x51CE, 0xFFFA,             // DBRA D6,call
            0x60F0,                     // BRA.S start
            // sub:
            0x48E7, 0xC0C0,             // MOVEM.L D0-D1/A0-A1,-(A7)
            0xE348,                     // LSL.W #1,D0
            0xC0C1,                     // MULU D1,D0
            0xD040,                     // ADD.W D0,D0
            0x4CDF, 0x0303,             // MOVEM.L (A7)+,D0-D1/A0-A1
            0x4E75,                     // RTS
        }),
    };

    public static int Run(string[] args)
    {
        double seconds = double.TryParse(M68kTestCli.Arg(args, "--seconds"), out double s) ? s : 3.0;
        Console.WriteLine($"== 68000 throughput ({seconds:F1} s per kernel; the Genesis runs {GenesisHz / 1e6:F2} M clocks per second)");
        double total = 0; int n = 0;
        foreach (var (name, code) in Kernels)
        {
            var m = new M68kFlatMachine();
            m.Load(0x100, code);
            var cpu = new M68k<M68kFlatBus>(new M68kFlatBus { M = m });
            cpu.Power();
            cpu.A[7] = 0x8000;
            cpu.Supervisor = true;
            cpu.Pc = 0x104; cpu.Ir = 0; cpu.Irc = 0;
            cpu.Ir = (ushort)(m.Ram[0x100] << 8 | m.Ram[0x101]); cpu.Irc = (ushort)(m.Ram[0x102] << 8 | m.Ram[0x103]);
            // warm up (JIT), then measure
            for (int i = 0; i < 200_000; i++) cpu.Step();
            long c0 = cpu.Cycles, i0 = cpu.Instructions;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds) for (int i = 0; i < 20_000; i++) cpu.Step();
            sw.Stop();
            double clocks = (cpu.Cycles - c0) / sw.Elapsed.TotalSeconds, instr = (cpu.Instructions - i0) / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  {name,-44} {clocks / 1e6,7:F1} M clocks/s  {instr / 1e6,6:F2} M instr/s  = {clocks / GenesisHz,5:F1}x real time   (halted={cpu.Halted}, address errors={cpu.AddressErrorCount})");
            total += clocks / GenesisHz; n++;
        }
        Console.WriteLine($"  mean {total / n:F1}x the Genesis's 68000 speed (the core alone: no VDP, no Z80, no timing arbiter)");
        return 0;
    }
}
