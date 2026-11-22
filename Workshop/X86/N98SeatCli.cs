using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>--n98seat PKG.n98 [--frames N] [--shot png] [--apu SB16_D072]: the desktop's nano98 path headless (PcSession on a .n98: apply + seat command +
/// resume snapshot), printing the seat's status lines and how it ended. Uses the same BROKENNES_N98_* settings as the app.</summary>
internal static class N98SeatCli
{
    private static int[] Shown(PcSession s) => System.Runtime.InteropServices.MemoryMarshal.Cast<uint, int>(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight)).ToArray();

    public static int Run(string[] args)
    {
        string pkg = args[1]; int frames = 6000; string? shot = null, apu = "SB16_D072", saveIn = null, saveOut = null, mp4Path = null, shotDir = null; int shotEvery = 0;
        for (int i = 2; i + 1 < args.Length; i++)
        {
            if (args[i] == "--frames") frames = int.Parse(args[i + 1]);
            if (args[i] == "--shot") shot = args[i + 1];
            if (args[i] == "--apu") apu = args[i + 1];
            if (args[i] == "--save-in") saveIn = args[i + 1];     // the saves layer to start from (as the app's N98Saves\<id>.n98s)
            if (args[i] == "--save-out") saveOut = args[i + 1];   // the saves layer at the end
            if (args[i] == "--mp4") mp4Path = args[i + 1];        // the whole boot as video (picture only, silent track)
            if (args[i] == "--shotevery") shotEvery = int.Parse(args[i + 1]);   // with --shotdir: a PNG every N frames (check every stage of the boot)
            if (args[i] == "--shotdir") shotDir = args[i + 1];
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new PcSession(File.ReadAllBytes(pkg), Path.GetFileName(pkg), apu, "I386", "VGA");
        Console.WriteLine($"n98seat: {s.Title} on {s.MachineKey}, prepared in {sw.Elapsed.TotalSeconds:F1} s, cache {s.Seat!.CacheDir}");
        if (s.Seat.V2) Console.WriteLine($"n98seat: schema 2, base {DiskImage.HashOf(s.Seat.Image)}, blank D {(Environment.GetEnvironmentVariable("N98SEAT_BLANKHASH") == "1" ? DiskImage.HashOf(s.Seat.BlankDrive!) : "-")}, game D {(s.Seat.GameDriveBig != null ? s.Seat.GameDriveBig.Hash + $" ({s.Seat.DriveMb} MB)" : DiskImage.HashOf(s.Seat.GameDrive!))}, warm {s.Seat.WarmDir}");
        if (saveIn != null) { s.ImportSave(File.ReadAllBytes(saveIn)); Console.WriteLine($"n98seat: saves layer {saveIn} {(s.SavesMismatch ? "NOT applied (other composition)" : "applied")}"); }
        string? ended = null;
        // --until-warm: stop once the shared warm-boot state exists (build_seat.py makes the base's warm state with an empty package)
        bool untilWarm = args.Contains("--until-warm"); string warmFile = Path.Combine(s.Seat.WarmDir, "warm.bin");
        if (untilWarm && File.Exists(warmFile)) { Console.WriteLine($"n98seat: warm state already there: {warmFile}"); return 0; }
        s.Machine.StatusLine += l => Console.WriteLine($"n98seat: status \"{l}\" at {sw.Elapsed.TotalSeconds:F1} s");
        s.SeatEnded += (k, d) => ended = $"{k} {d}";
        int f = 0;
        BrokenNes.Workshop.Movies.Mp4Writer? mp4 = mp4Path != null ? new(mp4Path, 640, 480, 25175000, 449 * 800, videoKbps: 4000) : null; long silence = 0;
        if (shotDir != null) Directory.CreateDirectory(shotDir);
        for (; f < frames && ended == null; f++)
        {
            if (untilWarm && f % 50 == 0 && File.Exists(warmFile)) { ended = "warm state saved: " + warmFile; break; }
            s.RunFrame();
            if (mp4 != null)
            {
                long want = (long)Math.Round((mp4.Frames + 1) * 44100 * Vga.FrameTicks / Pit8254.Freq); mp4.AddAudio(new byte[(int)(want - silence) * 4]); silence = want;
                mp4.AddFrame(Shown(s), s.FrameWidth, s.FrameHeight);   // what the app shows (the session's frame), not the raw VGA picture
            }
            if (shotDir != null && shotEvery > 0 && f % shotEvery == 0) X86Shots.SavePng(Shown(s), s.FrameWidth, s.FrameHeight, Path.Combine(shotDir, $"f{f:D6}.png"));
        }
        if (mp4 != null) { mp4.Finish(); Console.WriteLine($"n98seat: mp4 {mp4Path}: {mp4.Frames} frames, {mp4.Seconds:F1} s"); }
        Console.WriteLine($"n98seat: {f} frames, {sw.Elapsed.TotalSeconds:F1} s, ended: {ended ?? "no"}");
        if (shot != null) X86Shots.SavePng(Shown(s), s.FrameWidth, s.FrameHeight, shot);
        if (args.Contains("--state-roundtrip"))   // save the machine, run on, load it back, run the same frames again: the pictures and the state bytes must match
        {
            var st = s.SaveState(); for (int k = 0; k < 300; k++) s.RunFrame(); var p1 = Shown(s); var st1 = s.SaveState();
            s.LoadState(st); for (int k = 0; k < 300; k++) s.RunFrame(); var p2 = Shown(s); var st2 = s.SaveState();
            bool same = p1.AsSpan().SequenceEqual(p2) && st1.AsSpan().SequenceEqual(st2);
            Console.WriteLine($"n98seat: state round trip {(same ? "EXACT" : "DIFFERENT")} (state {st.Length >> 10} KB, D: {s.Machine.Hdd[1]?.Length >> 20} MB, D: changed {s.Machine.Hdd[1]?.OverlaySectors} sectors)");
        }
        if (saveOut != null) { var b = s.ExportSave(); File.WriteAllBytes(saveOut, b); Console.WriteLine($"n98seat: saves layer {saveOut}: {b.Length / 1024} KB, C: changed {s.Machine.Hdd[0]!.OverlaySectors} sectors"); }
        return 0;
    }
}
