using System;
using System.IO;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--alloc-probe --rom X [--warm 400] [--frames 300]</c>: bytes the managed heap grows by per frame of a console session in steady state (GC.GetAllocatedBytesForCurrentThread over the frames after the warm-up),
/// with a pad that taps Start / A now and then. The same file runs on any build, so the allocation of a change is read by running it on the build before and after.
/// </summary>
internal static class AllocProbeCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string path = Opt("rom", "");
        int warm = int.Parse(Opt("warm", "400")), frames = int.Parse(Opt("frames", "300"));
        byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(path), Path.GetFileName(path), out string inner);
        var console = RomDetect.Detect(rom, inner) ?? throw new InvalidDataException("unknown ROM");
        using var s = ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), CoreCatalog.Default(console, CoreSlot.Ppu), CoreCatalog.Default(console, CoreSlot.Apu), null, Path.GetFileName(inner));
        var buf = new short[65536];
        void Frame(int f)
        {
            s.SetPad(0, f % 90 is >= 20 and < 26 ? PadButtons.Start : f % 40 is >= 10 and < 14 ? PadButtons.A : PadButtons.Right);
            s.RunFrame();
            while (s.ReadSamples(buf) > 0) { }
        }
        for (int f = 0; f < warm; f++) Frame(f);
        GC.Collect(); GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = warm; f < warm + frames; f++) Frame(f);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"{Path.GetFileName(path)}: {bytes / frames} bytes a frame over {frames} frames after {warm} warm-up frames ({bytes} total)");
        return 0;
    }
}
