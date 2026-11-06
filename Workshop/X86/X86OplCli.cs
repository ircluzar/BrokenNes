using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86opl script.txt out.raw samples</c>: plays a scripted register-write sequence on the x86 console's YM3812 (<see cref="Opl2"/>) and dumps its raw chip samples (int16, ~49716 Hz, before
/// resampling). The script's lines are "sample reg value" (decimal sample, hex register and value): each write lands just before that sample is made, the way the ymfm reference harness
/// (C:\BrokenNes-work\x86_build\opl_ref\harness.cpp, built from ymfm itself) plays the same file, so the two dumps compare sample for sample. With a fourth argument (the reference's dump)
/// it compares them and reports the first difference. Exit code 0 = identical (or no reference given).
/// </summary>
internal static class X86OplCli
{
    public static int Run(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("--x86opl script.txt out.raw samples [reference.raw]"); return 2; }
        var writes = File.ReadAllLines(args[1]).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 3)
            .Select(p => (s: long.Parse(p[0]), r: Convert.ToByte(p[1], 16), v: Convert.ToByte(p[2], 16))).ToList();
        long n = long.Parse(args[3]);
        var chip = new Opl2(() => 0) { Enabled = true, RawTap = new List<short>() };
        foreach (var w in writes) chip.WriteAt(w.s * 72 - 1, w.r, w.v);
        chip.RunTo((n - 1) * 72);
        var raw = chip.RawTap.Take((int)n).ToArray();
        File.WriteAllBytes(args[2], System.Runtime.InteropServices.MemoryMarshal.AsBytes(raw.AsSpan()).ToArray());
        long loud = raw.Count(x => x != 0);
        Console.WriteLine($"x86opl: {writes.Count} writes, {raw.Length} samples, {loud} not silent, peak {raw.Select(x => Math.Abs((int)x)).DefaultIfEmpty(0).Max()}");
        if (args.Length < 5) return 0;
        var refb = File.ReadAllBytes(args[4]);
        var refs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(refb).ToArray();
        long diffs = 0, first = -1;
        for (int i = 0; i < Math.Min(refs.Length, raw.Length); i++) if (refs[i] != raw[i]) { diffs++; if (first < 0) first = i; }
        if (refs.Length != raw.Length) Console.WriteLine($"  length differs: reference {refs.Length}, ours {raw.Length}");
        Console.WriteLine(diffs == 0 && refs.Length == raw.Length ? $"  IDENTICAL to the reference, {raw.Length} samples" : $"  {diffs} samples differ, the first at {first} (reference {refs[first]}, ours {raw[first]})");
        return diffs == 0 && refs.Length == raw.Length ? 0 : 1;
    }
}
