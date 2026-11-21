using System;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Debugging.Capture;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;
using NesEmulator.X86;

namespace BrokenNes.Workshop.Bugs;

/// <summary>
/// <c>--x86bugrt FLOPPY [--frames N] [--keep DIR]</c>: the x86 bug capture's round trip, as the battery runs it (no game data needed): a PC
/// session on a FreeDOS floppy with the capture ring attached as the desktop attaches it (keys through HostKey, so they are recorded), keys typed
/// while it runs, a capture written as a bundle (the floppy by reference), then <c>--bug play</c> of it (must be in sync) and of a copy without its
/// host-input lines (must go off course: the check can fail). Exit 0 when both hold.
/// </summary>
internal static class PcBugRoundTrip
{
    public static int Run(string[] args)
    {
        string? Opt(string k) { int i = Array.FindIndex(args, a => a.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string fd = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : throw new ArgumentException("usage: --x86bugrt FLOPPY [--frames N] [--keep DIR]");
        int frames = int.Parse(Opt("--frames") ?? "900");
        string dir = Opt("--keep") ?? Path.Combine(Path.GetTempPath(), "x86bugrt_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);

        // the JIT compiles on the CPU's thread (X86_JITSYNC, read once when the CPU type first loads: before any PC here): background
        // compiles finishing at different moments made 1 run in 5 of this check go off course at frame 840 (2026-11-21), which is the JIT's
        // repeatability, not the capture's: this check tests the capture; the background JIT's is a separate finding
        if (Environment.GetEnvironmentVariable("X86_JITSYNC") == null && Environment.GetEnvironmentVariable("X86_JITDELAY") == null) Environment.SetEnvironmentVariable("X86_JITSYNC", "1");   // (X86_JITDELAY: the battery's x86bugrt-jitdelay keeps the background JIT, randomised)
        var s = new PcSession(File.ReadAllBytes(fd), Path.GetFileName(fd));
        var ring = new BugCaptureRing();
        ring.Attach(BugCaptureSource.For(s), 44100, s.FramesPerSecond, true);
        s.InputTap = ring.Input;
        // "ver", Enter, "dir", Enter typed from frame 300 (FreeDOS is at its prompt by then), one key every 4 frames, down then up
        byte[] keys = { 0x2F, 0x12, 0x13, 0x1C, 0x20, 0x17, 0x13, 0x1C };
        for (int f = 0; f < frames; f++)
        {
            ring.Frame(PadButtons.None, PadButtons.None);
            int k = (f - 300) / 4, ph = (f - 300) % 4;
            if (f >= 300 && k < keys.Length && ph is 0 or 2) s.HostKey(keys[k], false, ph == 0);
            if (f >= 300 && k >= keys.Length && k < keys.Length + 30 && ph == 0) s.HostMouse(0, 0, 0);   // (no PS/2 mouse on this machine: recorded, harmless)
            s.RunFrame();
        }
        var d = ring.Capture();
        var meta = new BugMeta { RomName = Path.GetFileName(fd), Rom = File.ReadAllBytes(fd), RomPath = fd, App = "BrokenNes Workshop --x86bugrt", Cores = "I386/VGA/ADLIB", CapturedUtc = DateTime.UtcNow, CapturedLocal = DateTime.Now };
        meta.DiskRefs.Add(("C", Path.GetFullPath(fd)));
        meta.Settings.Add(("machine", s.MachineKey));
        string path = BugBundle.WriteFolder(dir, BugBundle.Files(d, meta), meta.CapturedLocal);
        int xs = File.ReadAllLines(Path.Combine(path, BugBundle.MovieFile)).Count(l => l.StartsWith("x "));
        Console.WriteLine($"x86bugrt: captured {path}: {d.Frames} frames from {d.Start}, {xs} host inputs");

        var (c1, j1) = BugCli.Play(BugBundleReader.Open(path), null, new(), false, null);
        Console.WriteLine($"x86bugrt: replay: {(string)j1["summary"]!}");

        string neg = path + "_noinput";
        CopyDir(path, neg);
        string mf = Path.Combine(neg, BugBundle.MovieFile);
        File.WriteAllLines(mf, File.ReadAllLines(mf).Where(l => !l.StartsWith("x ")), new UTF8Encoding(false));
        var (c2, j2) = BugCli.Play(BugBundleReader.Open(neg), null, new(), false, null);
        Console.WriteLine($"x86bugrt: without its inputs: {(string)j2["summary"]!}");

        bool ok = c1 == 0 && c2 == 1 && xs > 0;
        if (Opt("--keep") == null) try { Directory.Delete(dir, true); } catch (IOException) { }
        Console.WriteLine(ok ? "x86bugrt: ok (in sync with its inputs, off course without them)" : $"x86bugrt: FAILED (replay {c1}, without inputs {c2}, {xs} inputs)");
        return ok ? 0 : 1;
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
        }
    }
}
