using System;
using System.IO;
using NesEmulator.Debugging.Capture;
using NesEmulator.Systems;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86bugring img [--machine M] [--frames N] [--every N]</c>: the desktop's bug-capture ring (F9's last 30 s) over a PcSession,
/// as MainForm feeds it, printing the process's private bytes and the managed heap every <c>--every</c> frames. The 2026-11-20 leak: under Win98 +
/// Age of Empires a state a second (RAM + disk diff, ~250 MB) x 32 slots held 10+ GB; <c>--noring</c> runs without it.
/// The env hooks of <see cref="PcSession"/> (BROKENNES_X86_MACHINESTATE, _HDD2, _CD) start it where a desktop window would.
/// </summary>
internal static class X86BugRingCli
{
    private static string? Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i > 0 && i + 1 < a.Length ? a[i + 1] : null; }

    public static int Run(string[] args)
    {
        string img = args.Length > 1 ? args[1] : throw new ArgumentException("--x86bugring img");
        int frames = int.Parse(Arg(args, "--frames") ?? "3600"), every = int.Parse(Arg(args, "--every") ?? "300");
        Consoles.X86Enabled = true;
        using var s = ConsoleSessions.Create(ConsoleKind.Pc, File.ReadAllBytes(img), CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Cpu), CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Ppu),
            Arg(args, "--apu") ?? CoreCatalog.Default(ConsoleKind.Pc, CoreSlot.Apu), null, Path.GetFileName(img), pcMachine: Arg(args, "--machine") ?? "WIN98-P166");
        var ring = new BugCaptureRing(60);
        if (Array.IndexOf(args, "--noring") < 0) ring.Attach(BugCaptureSource.For(s), 44100, s.FramesPerSecond, false);
        var buf = new short[8192]; var mono = new float[4096];
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        long peak = 0;
        Console.WriteLine("frame,privMB,heapMB,stateMB");
        for (int f = 0; f < frames; f++)
        {
            ring.Frame(PadButtons.None, PadButtons.None);
            s.RunFrame();
            int n = s.ReadSamples(buf);
            int m = Math.Min(n / 2, mono.Length); for (int i = 0; i < m; i++) mono[i] = buf[i * 2] / 32768f;
            ring.Audio(mono, m);
            if ((f + 1) % every == 0)
            {
                proc.Refresh(); long priv = proc.PrivateMemorySize64; peak = Math.Max(peak, priv);
                Console.WriteLine($"{f + 1},{priv >> 20},{GC.GetTotalMemory(false) >> 20},{ring.LastStateBytes >> 20}");
            }
        }
        if (!ring.Attached) { Console.WriteLine($"no ring; peak private {peak >> 20} MB"); return 0; }
        var d = ring.Capture();
        Console.WriteLine($"capture: {d.Frames} frames from a {(d.StartState?.Length ?? 0) >> 20} MB state ({d.Start}); peak private {peak >> 20} MB{(ring.StateError.Length > 0 ? "; STATE ERROR " + ring.StateError : "")}");
        return 0;
    }
}
