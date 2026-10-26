using System;
using System.Collections.Generic;
using System.Linq;

namespace NesEmulator.Debugging.Checks;

/// <summary>
/// The coverage map (milestone D9 of docs/projects/brokendev-debugging-plan.md): which ROM bytes the main CPU ran, by the CPU's canonical
/// ROM key (so a bank-switched ROM is counted by offset, whatever bank maps it). Each instruction marks its opcode and operand bytes. An
/// observer of the D1 debugger: it asks for the main CPU's steps only, so with it removed the machine pays nothing.
/// </summary>
public sealed class CodeCoverage : DebugObserver
{
    private readonly DebugCpu cpu;
    private readonly int romSpace = -1;
    private readonly SpaceKey[] keys = new SpaceKey[8];

    /// <summary>One flag per ROM byte: true when it was part of an instruction the main CPU ran.</summary>
    public bool[] Ran { get; }
    public long Instructions { get; private set; }

    public CodeCoverage(Debugger debugger)
    {
        cpu = debugger.Machine.Cpus[0];
        var rom = cpu.Spaces.FirstOrDefault(s => s.Is("rom"));
        if (rom != null) romSpace = rom.Id;
        Ran = new bool[rom?.Size ?? 0];
    }

    /// <summary>Adds the coverage observer to a debugger (arming the main CPU's step hook) and returns it.</summary>
    public static CodeCoverage Attach(Debugger debugger)
    {
        var c = new CodeCoverage(debugger);
        debugger.AddObserver(c);
        return c;
    }

    public override bool WantsSteps(int cpuIndex) => cpuIndex == 0 && romSpace >= 0;

    public override void OnStep(DebugCpu c, long cycle, bool executes, string? interrupt)
    {
        if (!executes || c.Index != 0) return;
        uint pc = c.PC;
        int n = c.AccessKeys(pc, false, keys);
        for (int i = 0; i < n; i++)
        {
            if (keys[i].Space != romSpace) continue;
            uint off = keys[i].Offset;
            if (off >= (uint)Ran.Length) return;
            uint bytes = (uint)(c.Peek(pc) | c.Peek(c.NextAddress(pc, 1)) << 8);
            int len = Math.Max(1, c.Classify(bytes).Length);
            for (int k = 0; k < len && off + k < (uint)Ran.Length; k++) Ran[off + k] = true;
            Instructions++;
            return;
        }
    }

    /// <summary>The ROM offset of every symbol that binds into the main CPU's ROM space (code labels, and data labels kept in ROM).</summary>
    public Dictionary<string, uint> RomSymbols(Debugger debugger)
    {
        var d = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var s in debugger.Symbols.All)
            if (cpu.BindSymbol(s, out var k) && k.Space == romSpace && k.Offset < (uint)Ran.Length) d.TryAdd(s.Name, k.Offset);
        return d;
    }
}
