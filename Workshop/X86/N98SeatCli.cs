using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>--n98seat PKG.n98 [--frames N] [--shot png] [--apu SB16_D072]: the desktop's nano98 path headless (PcSession on a .n98: apply + seat command +
/// resume snapshot), printing the seat's status lines and how it ended. Uses the same BROKENNES_N98_* settings as the app.</summary>
internal static class N98SeatCli
{
    public static int Run(string[] args)
    {
        string pkg = args[1]; int frames = 6000; string? shot = null, apu = "SB16_D072";
        for (int i = 2; i + 1 < args.Length; i++)
        {
            if (args[i] == "--frames") frames = int.Parse(args[i + 1]);
            if (args[i] == "--shot") shot = args[i + 1];
            if (args[i] == "--apu") apu = args[i + 1];
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new PcSession(File.ReadAllBytes(pkg), Path.GetFileName(pkg), apu, "I386", "VGA");
        Console.WriteLine($"n98seat: {s.Title} on {s.MachineKey}, prepared in {sw.Elapsed.TotalSeconds:F1} s, cache {s.Seat!.CacheDir}");
        string? ended = null;
        s.Machine.StatusLine += l => Console.WriteLine($"n98seat: status \"{l}\" at {sw.Elapsed.TotalSeconds:F1} s");
        s.SeatEnded += (k, d) => ended = $"{k} {d}";
        int f = 0;
        for (; f < frames && ended == null; f++) s.RunFrame();
        Console.WriteLine($"n98seat: {f} frames, {sw.Elapsed.TotalSeconds:F1} s, ended: {ended ?? "no"}");
        if (shot != null) X86Shots.SavePng(s.Machine.Vga, shot);
        return 0;
    }
}
