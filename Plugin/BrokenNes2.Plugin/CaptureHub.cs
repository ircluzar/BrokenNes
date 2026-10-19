// Round 9 (capture): "Capture from FL" for the whole FL process. The Test MIDI export window of any instance arms a take; every BrokenNes2 instance of the process (they are all registered here,
// in their constructors) gets a lane; FL's Play and Stop start and end it on each instance; the capture's worker builds the MIDI file (Capture/CaptureSong.cs), writes it to
// %TEMP%\BrokenNes2\Captures\ and hands it to Test MIDI export on the emulator of the instance that armed it: VRUN's converter, VRUN's sound-test player, played at once, the facts in the window.
// Save MIDI... copies the file where the user chooses. The live MIDI out of the notes (Bn2Plugin.CaptureLive.cs) is switched here too.
using BrokenNes2.Capture;

namespace BrokenNes2;

internal static class CaptureHub
{
    private static readonly object gate = new();
    private static readonly List<Bn2Plugin> instances = new();
    private static volatile int targetEmulator;
    private static volatile bool live = Prefs.GetInt("CaptureMidiOut", 0) == 1;
    private static volatile bool replacedDirect;
    private static int stamp;

    /// <summary>The MIDI file the last take wrote (null: none yet).</summary>
    public static volatile string? MidiPath;
    /// <summary>The window's last line about what a button did.</summary>
    public static volatile string LastWords = "";
    /// <summary>What the capture did to the emulator when it armed (switched back to Direct mode ...), for the window.</summary>
    public static volatile string? ArmNote;
    /// <summary>Changes whenever the take changes state or a result arrives (the editors repaint on it).</summary>
    public static int Stamp => Volatile.Read(ref stamp);
    public static bool LiveOn => live;
    public static int TargetEmulator => targetEmulator;

    public static void Register(Bn2Plugin p)
    {
        var s = CaptureSession.Current;
        lock (gate) instances.Add(p);
        if (s != null && s.Listening) p.AttachLane(s, s.NewLane(p.CaptureIdentity()));        // made while a take is armed or runs: it takes part (a lane of its own; it joins at the others' clock if FL never starts it)
    }

    public static void Unregister(Bn2Plugin p) { lock (gate) instances.Remove(p); }

    private static Bn2Plugin[] All() { lock (gate) return instances.ToArray(); }

    /// <summary>Arms a take from <paramref name="from"/>'s window (the UI thread). Null when armed, else why not.</summary>
    public static string? Arm(Bn2Plugin from)
    {
        var emu = from.Emulator;
        if (emu == null) return "this instance has no emulator to play the captured song in";
        var all = All();
        // the emulator plays the last captured song in ROM mode where the user had Direct-mode channels: back to Direct, so FL's notes are heard while capturing
        ArmNote = null;
        if (replacedDirect && targetEmulator == emu.Id && emu.SongActive && emu.Mode == 1)
        {
            emu.SetMode(0);
            ArmNote = $"Emulator #{emu.Id} is back in Direct mode: its channels play FL's notes while you capture.";
        }
        var s = CaptureSession.Arm($"FL capture {DateTime.Now:HH.mm.ss}", () => FlClock.Now(EmulatorHub.HostRate));
        s.OnFinished = Finished;
        s.RefreshIdentity = Refresh;
        targetEmulator = emu.Id;
        foreach (var p in all) p.AttachLane(s, s.NewLane(p.CaptureIdentity()));
        HostLog.SetCapture(true);
        HostLog.Text(from.InstanceNo, $"capture armed from emulator #{emu.Id}: {all.Length} BrokenNes2 instance{(all.Length == 1 ? "" : "s")} listen ({string.Join(", ", all.Select(p => $"#{p.InstanceNo} {p.CaptureIdentity().Name}"))}); FL so far: {FlClock.Counts()}");
        Interlocked.Increment(ref stamp);
        return null;
    }

    public static void Cancel(string why)
    {
        var s = CaptureSession.Current;
        if (s == null || !s.Listening && s.State != CaptureSession.Finishing) return;
        s.Cancel();
        foreach (var p in All()) p.DetachLane();
        HostLog.Text(0, "capture cancelled: " + why);
        HostLog.SetCapture(false);
        Interlocked.Increment(ref stamp);
    }

    /// <summary>Tests: no take, nothing remembered.</summary>
    public static void Reset() { foreach (var p in All()) p.DetachLane(); MidiPath = null; LastWords = ""; ArmNote = null; replacedDirect = false; Interlocked.Increment(ref stamp); }

    /// <summary>The lane's identity again when the take is built (the instance may have moved since it was armed).</summary>
    private static LaneIdentity Refresh(LaneData d)
    {
        foreach (var p in All()) if (p.InstanceNo == d.Identity.InstanceNo) return p.CaptureIdentity();
        return d.Identity;
    }

    /// <summary>The worker: the take is built. The file is written and played by Test MIDI export on the emulator that armed the take; the details go to the host log.</summary>
    private static void Finished(CaptureSession s)
    {
        foreach (var p in All()) p.DetachLane();
        var r = s.Result!;
        HostLog.Text(0, $"capture \"{s.Name}\" finished: {(r.Problem ?? $"{r.Notes} notes, {r.Tracks.Count} tracks, {r.BendEvents} bends")}; FL: {FlClock.Counts()}");
        foreach (var line in r.Report) HostLog.Text(0, "capture: " + line);
        foreach (var line in r.Facts) HostLog.Text(0, "capture fact: " + line);
        try
        {
            if (r.Problem != null) return;
            string dir = Path.Combine(Path.GetTempPath(), "BrokenNes2", "Captures");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, s.Name + ".mid");
            File.WriteAllBytes(path, r.Midi);
            MidiPath = path;
            if (CaptureMutants.On(CaptureMutants.NotFed)) return;                     // (the deliberate bug: built, never played)
            var emu = EmulatorHub.Get(targetEmulator);
            if (emu == null) { LastWords = $"the capture was saved as {path}, but emulator #{targetEmulator} is gone: open it with Open MIDI..."; return; }
            replacedDirect = emu.Mode == 0;
            if (emu.OpenSong(path) is { } refused) LastWords = $"the capture was saved as {path}; Test MIDI export did not take it: {refused}";
            else LastWords = $"Captured and sent to Test MIDI export: {Path.GetFileName(path)} plays in emulator #{emu.Id}";
        }
        catch (Exception e) { LastWords = "the capture could not be written: " + e.Message; }
        finally
        {
            HostLog.SetCapture(false);
            Interlocked.Increment(ref stamp);
        }
    }

    /// <summary>Save MIDI...: the last take's file where the user chose. Null when written, else why not.</summary>
    public static string? SaveMidi(string path)
    {
        var r = CaptureSession.Current?.Result;
        if (r == null || r.Problem != null || r.Midi.Length == 0) return "there is no captured song yet: Capture from FL first";
        try { File.WriteAllBytes(path, r.Midi); return null; }
        catch (Exception e) { return $"{Path.GetFileName(path)} could not be written ({e.Message.TrimEnd('.')})"; }
    }

    /// <summary>The live MIDI out of the notes, for every instance (the UI thread); remembered.</summary>
    public static void SetLive(bool on)
    {
        live = on;
        Prefs.SetInt("CaptureMidiOut", on ? 1 : 0);
        foreach (var p in All()) p.LiveSwitched();
        Interlocked.Increment(ref stamp);
    }

    /// <summary>The window's status line.</summary>
    public static string StatusText()
    {
        var s = CaptureSession.Current;
        if (s == null || s.State == CaptureSession.Cancelled) return "Capture from FL: press it, then Play in FL from the start of your song and Stop at its end. Every BrokenNes2 channel records what FL plays on it (notes, slides, pitch bends); the song is converted and played here at once.";
        return s.State switch
        {
            CaptureSession.Armed => $"ARMED: press Play in FL from the start of the song, Stop at its end. {s.LaneCount} BrokenNes2 channel{(s.LaneCount == 1 ? "" : "s")} listen." + (ArmNote is { } an ? "  " + an : ""),
            CaptureSession.Recording => $"CAPTURING {Dur(s.Seconds)}: {All().Sum(p => p.CapturedNotes)} notes so far. Press Stop in FL at the end of the song.",
            CaptureSession.Finishing => "FL stopped: building the MIDI file...",
            _ => s.Result?.Problem is { } why ? "Nothing captured: " + why + "." : $"Captured {s.Name}: {s.Result?.Notes} notes on {s.Result?.Tracks.Count} channels. Save MIDI... keeps the file (with the pitch bends).",
        };
    }

    private static string Dur(double sec) => $"{(int)(sec / 60)}:{sec % 60:00.0}";
}
