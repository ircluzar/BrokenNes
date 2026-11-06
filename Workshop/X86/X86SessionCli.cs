using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Systems;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86session img [--frames N] [--shot png]</c>: the x86 console as the apps run it (<see cref="PcSession"/> through <see cref="ConsoleSessions.Create"/>): ROM detection of the image, the core menus, N frames,
/// the picture, a save state round trip (the same frames after a load give the same pictures), and the disk as the battery (a write shows in <c>ExportSave</c>). Exit code 0 = all passed.
/// </summary>
internal static class X86SessionCli
{
    public static int Run(string[] args)
    {
        string img = args.Length > 1 ? args[1] : throw new ArgumentException("--x86session img");
        int frames = 600;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--frames") frames = int.Parse(args[i + 1]);
        Consoles.X86Enabled = true;
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}"); if (!ok) fail++; }
        var data = File.ReadAllBytes(img);
        string name = Path.GetFileName(img);
        Check(RomDetect.Detect(data, name) == ConsoleKind.Pc, $"{name} is detected as the x86 PC");
        Check(Consoles.All.Contains(ConsoleKind.Pc) && Consoles.FromKey("x86") == ConsoleKind.Pc, "the x86 PC is offered, key x86");
        foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
        {
            var o = CoreCatalog.Options(ConsoleKind.Pc, slot);
            Check(o.Count == 1, $"{slot} menu: {string.Join(" | ", o.Select(x => $"{x.Id} = {x.Label} [{x.Section}]"))}");
        }
        using var s = ConsoleSessions.Create(ConsoleKind.Pc, data, CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Cpu), CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Ppu), CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Apu), null, name);
        Console.WriteLine($"  session: {s.Description}, {s.FramesPerSecond:F2} fps");
        var save0 = s.ExportSave();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buf = new short[8192]; long samples = 0;
        for (int f = 0; f < frames; f++) { s.RunFrame(); samples += s.ReadSamples(buf); }
        Console.WriteLine($"  {frames} frames in {sw.Elapsed.TotalSeconds:F1} s ({frames / sw.Elapsed.TotalSeconds:F1} fps, real time is {s.FramesPerSecond:F1}); picture {s.FrameWidth}x{s.FrameHeight} shown {s.DisplayWidth} wide");
        Check(s.Frame.Take(s.FrameWidth * s.FrameHeight).Distinct().Count() > 1, "the picture is not blank");
        int si = Array.IndexOf(args, "--shot");
        if (si > 0 && si + 1 < args.Length && s is PcSession ps) { X86Shots.SavePng(ps.Machine.Vga, args[si + 1]); Console.WriteLine($"  picture: {args[si + 1]}"); }
        Check(Math.Abs(samples / 2.0 - frames * 44100 / s.FramesPerSecond) < 2000, $"silence at the right rate ({samples / 2} stereo samples)");
        var st = s.SaveState();
        string Run100() { for (int f = 0; f < 100; f++) { s.SetPad(0, f % 20 < 10 ? PadButtons.Right : PadButtons.None); s.RunFrame(); } return Convert.ToHexString(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight)))); }
        string a = Run100();
        s.LoadState(st);
        string b = Run100();
        Check(a == b, $"save state round trip: the same 100 frames give the same picture ({a[..12]})");
        var save1 = s.ExportSave();
        Check(ReferenceEquals(save1, s.ExportSave()), "an unchanged disk exports the same array (no copy)");
        Console.WriteLine($"  disk changed since power-on: {!save0.AsSpan().SequenceEqual(save1)}");
        Console.WriteLine(fail == 0 ? "x86session: all passed" : $"x86session: {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }
}
