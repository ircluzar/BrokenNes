using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Systems;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86session img [--frames N] [--shot png] [--cpu I386|I386_D074] [--ppu VGA|BOCHS20|DOSBOX072|PCEM_CGA] [--swapat F]</c>: the x86 console as the apps run it (<see cref="PcSession"/> through <see cref="ConsoleSessions.Create"/>): ROM detection of the image, the core menus, N frames,
/// the picture, a save state round trip (the same frames after a load give the same pictures), and the disk as the battery (a write shows in <c>ExportSave</c>). Exit code 0 = all passed.
/// </summary>
internal static class X86SessionCli
{
    private static string? Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i > 0 && i + 1 < a.Length ? a[i + 1] : null; }

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
            Check(slot == CoreSlot.Apu ? o.Count >= 1 : o.Count == (slot == CoreSlot.Cpu ? 2 : 1 + VgaPictures.Chips.Length), $"{slot} menu: {string.Join(" | ", o.Select(x => $"{x.Id} = {x.Label} [{x.Section}]"))}");
        }
        using var s = ConsoleSessions.Create(ConsoleKind.Pc, data, Array.IndexOf(args, "--cpu") is int ci && ci > 0 && ci + 1 < args.Length ? args[ci + 1] : CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Cpu), Arg(args, "--ppu") ?? CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Ppu), CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Apu), null, name);
        Console.WriteLine($"  session: {s.Description}, {s.FramesPerSecond:F2} fps");
        var save0 = s.ExportSave();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buf = new short[8192]; long samples = 0;
        long loud = 0, edges = 0, first = -1, lastLoud = 0, idx = 0; short last = 0;
        long rises = 0, firstRise = -1, lastRise = 0; bool low = true;   // sine pitch: rising crossings with hysteresis
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        int swapAt = Arg(args, "--swapat") is string sa ? int.Parse(sa) : -1;
        for (int f = 0; f < frames; f++)
        {
            if (f == swapAt) Check(s.TrySwapCore(CoreSlot.Ppu, Arg(args, "--swapto") ?? "VGA"), $"live picture swap at frame {f} to {Arg(args, "--swapto") ?? "VGA"}");
            s.RunFrame();
            int n = s.ReadSamples(buf); samples += n;
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, n)));
            for (int i = 0; i < n; i += 2)
            {
                long at = idx + i / 2;
                if (low && buf[i] > 300) { low = false; rises++; if (firstRise < 0) firstRise = at; lastRise = at; } else if (!low && buf[i] < -300) low = true;
            }
            for (int i = 0; i < n; i += 2, idx++) { if (buf[i] != 0) { loud++; if (first < 0) first = idx; lastLoud = idx; } if ((buf[i] > 2500) != (last > 2500)) edges++; last = buf[i]; }
        }
        double span = first < 0 ? 0 : (lastLoud - first + 1) / 44100.0;
        Console.WriteLine($"  sound: {loud} of {samples / 2} samples not silent, sounding for {span:F2} s, {edges / 2} square-wave periods ({(span > 0 ? edges / 2.0 / span : 0):F0} Hz)");
        if (s is PcSession pss) Console.WriteLine($"  adlib: {pss.Machine.Opl.DataWrites} data writes, {pss.Machine.Opl.StatusReads} status reads; {rises} rising crossings{(rises > 1 ? $" over {(lastRise - firstRise) / 44100.0:F2} s = {(rises - 1) * 44100.0 / (lastRise - firstRise):F1} Hz" : "")}");
        if (s is PcSession pst) Console.WriteLine($"  AdLib message on screen: {(pst.Machine.Teletype.ToString().Contains("AdLib not found") ? "AdLib not found" : pst.Machine.Teletype.ToString().Contains("AdLib found") ? "AdLib found" : "none")}");
        Console.WriteLine($"  sound hash: {Convert.ToHexString(hash.GetHashAndReset())[..16]}");
        Console.WriteLine($"  {frames} frames in {sw.Elapsed.TotalSeconds:F1} s ({frames / sw.Elapsed.TotalSeconds:F1} fps, real time is {s.FramesPerSecond:F1}); picture {s.FrameWidth}x{s.FrameHeight} shown {s.DisplayWidth} wide");
        Check(s.Frame.Take(s.FrameWidth * s.FrameHeight).Distinct().Count() > 1, "the picture is not blank");
        int si = Array.IndexOf(args, "--shot");
        if (si > 0 && si + 1 < args.Length && s is PcSession ps) { X86Shots.SavePng(ps.Machine.Vga, args[si + 1]); Console.WriteLine($"  picture: {args[si + 1]}"); }
        Check(Math.Abs(samples / 2.0 - frames * 44100 / s.FramesPerSecond) < 2000, $"silence at the right rate ({samples / 2} stereo samples)");
        var st = s.SaveState();
        string Run100() { using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); for (int f = 0; f < 100; f++) { s.SetPad(0, f % 20 < 10 ? PadButtons.Right : PadButtons.None); s.RunFrame(); h.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight))); } return Convert.ToHexString(h.GetHashAndReset()); }   // every frame's picture
        string a = Run100();
        var stA = ((PcSession)s).Machine.SaveState();   // payloads (the wrapped state carries a time stamp)
        s.LoadState(st);
        string b = Run100();
        Check(a == b, $"save state round trip: the same 100 frames give the same picture ({a[..12]})");
        s.LoadState(st);   // a third run, also from the load: it must end in the same machine state as the second, byte for byte (the first run did not start
        Run100();          // from a load, and a load restarts the AdLib's sample clock, so its state legitimately differs there)
        stA = ((PcSession)s).Machine.SaveState();
        s.LoadState(st);
        Run100();
        var stB = ((PcSession)s).Machine.SaveState();
        if (Environment.GetEnvironmentVariable("X86_DUMPST") is string dd) { File.WriteAllBytes(dd + ".a", stA); File.WriteAllBytes(dd + ".b", stB); }
        Check(stA.AsSpan().SequenceEqual(stB), "save state round trip: the state after those frames is byte for byte the same");
        var save1 = s.ExportSave();
        Check(ReferenceEquals(save1, s.ExportSave()), "an unchanged disk exports the same array (no copy)");
        Console.WriteLine($"  disk changed since power-on: {!save0.AsSpan().SequenceEqual(save1)}");
        Console.WriteLine(fail == 0 ? "x86session: all passed" : $"x86session: {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }
}
