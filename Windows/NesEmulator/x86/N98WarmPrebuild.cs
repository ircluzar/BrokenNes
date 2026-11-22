using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace NesEmulator.X86;

/// <summary>
/// nano98 item 2 (2026-11-22): warm-boot states made in the background, so a package still opens instantly after an app update (the warm state
/// is keyed by the emulator build) or a new system layer (a new variant = a new base hash). The app remembers the packages it opened
/// (LOCALAPPDATA\BrokenNes\nano98\warm_prebuild.txt); <see cref="Kick"/> (app start, a new layer) walks them on one low-priority thread and, for each
/// whose warm state is missing, boots it headless until the state is saved (the same path a first open takes), then drops the session.
/// It waits while <see cref="Busy"/> says the user is playing a PC game (one Win98 boot at a time on the box). Off with BROKENNES_N98_NOPREBUILD=1.
/// </summary>
public static class N98WarmPrebuild
{
    static string ListFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "nano98", "warm_prebuild.txt");
    static readonly object _lock = new();
    static Thread? _worker;
    static volatile bool _again;

    /// <summary>True while the prebuild should wait (the app sets it: a PC session is running).</summary>
    public static Func<bool>? Busy;
    /// <summary>Last thing done (for logs / status).</summary>
    public static string Status { get; private set; } = "idle";
    /// <summary>Frames a warm build may take before it is given up (a cold boot to the warm point is ~3-6k frames).</summary>
    public static int MaxFrames = 30000;

    /// <summary>Remembers an opened package (most recent first, 20 kept).</summary>
    public static void Remember(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            lock (_lock)
            {
                var l = Load().Where(p => !p.Equals(full, StringComparison.OrdinalIgnoreCase)).Prepend(full).Take(20).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile)!);
                File.WriteAllLines(ListFile, l);
            }
        }
        catch (Exception e) { Console.WriteLine($"[nano98] prebuild list: {e.Message}"); }
    }

    static List<string> Load() { try { return File.Exists(ListFile) ? File.ReadAllLines(ListFile).Where(l => l.Length > 0).ToList() : new(); } catch (IOException) { return new(); } }

    /// <summary>Starts (or re-runs) the background pass. Returns at once.</summary>
    public static void Kick()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_NOPREBUILD") == "1") return;
        lock (_lock)
        {
            if (_worker is { IsAlive: true }) { _again = true; return; }
            _worker = new Thread(Pass) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "nano98 warm prebuild" };
            _worker.Start();
        }
    }

    static void Pass()
    {
        do
        {
            _again = false;
            List<string> l; lock (_lock) l = Load();
            foreach (var p in l.Where(File.Exists))
            {
                try { BuildOne(p); }
                catch (Exception e) { Status = $"{Path.GetFileName(p)}: {e.Message}"; Console.WriteLine($"[nano98] prebuild {p}: {e.Message}"); }
            }
        } while (_again);
        Status = "idle";
    }

    /// <summary>Makes the warm state of one package if it is missing. True when one was made.</summary>
    public static bool BuildOne(string pkg)
    {
        WaitIdle();
        using var s = new PcSession(File.ReadAllBytes(pkg), Path.GetFileName(pkg), "SB16_D072", "I386", "VGA");
        if (s.Seat is not { V2: true }) return false;
        string warm = Path.Combine(s.Seat.WarmDir, "warm.bin");
        if (File.Exists(warm)) return false;
        Status = "building the warm state of " + Path.GetFileNameWithoutExtension(pkg);
        Console.WriteLine($"[nano98] prebuild: {Status} -> {s.Seat.WarmDir}");
        for (int f = 0; f < MaxFrames; f++)
        {
            if (f % 50 == 0) { if (File.Exists(warm)) { Console.WriteLine($"[nano98] prebuild: warm state saved after {f} frames"); return true; } WaitIdle(); }
            s.RunFrame();
        }
        Console.WriteLine($"[nano98] prebuild: no warm state after {MaxFrames} frames: {pkg}");
        return false;
    }

    static void WaitIdle() { while (Busy?.Invoke() == true) Thread.Sleep(2000); }
}
