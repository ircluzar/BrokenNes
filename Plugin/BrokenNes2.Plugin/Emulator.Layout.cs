// Layout B (2026-11-13): what the editor's new pieces need from the emulator: the names given to Runaway tree rows (shared by every instance on this emulator, saved by each one in
// the project as RNM1, RunawayTree.cs), the Game view's "play N s, then freeze", and the song bar's target picker.
using BrokenNes2.Music.Flp;

namespace BrokenNes2;

public sealed partial class Emulator
{
    private readonly Dictionary<string, string> runawayNames = new();
    private int runawayNamesVersion;

    /// <summary>Changes whenever a name is set or merged (the editor repaints, the tree is rebuilt).</summary>
    internal int RunawayNamesVersion => Volatile.Read(ref runawayNamesVersion);

    internal string? RunawayName(string key) { lock (runawayNames) return runawayNames.TryGetValue(key, out var n) ? n : null; }

    /// <summary>Gives a tree row a name (empty or null: back to the default name).</summary>
    internal void SetRunawayName(string key, string? name)
    {
        name = name?.Trim();
        if (name is { Length: > RunawayNames.MaxName }) name = name[..RunawayNames.MaxName];
        lock (runawayNames) { if (string.IsNullOrEmpty(name)) runawayNames.Remove(key); else runawayNames[key] = name; }
        Interlocked.Increment(ref runawayNamesVersion);
    }

    /// <summary>The names of a reopened project (each instance of it carries them; the first fills the emulator, the others agree).</summary>
    internal void MergeRunawayNames(Dictionary<string, string> names)
    {
        lock (runawayNames) foreach (var (k, v) in names) runawayNames[k] = v;
        Interlocked.Increment(ref runawayNamesVersion);
    }

    internal byte[]? RunawayNamesSection()
    {
        if (Ui.UiMutate.Has(Ui.UiMutate.RenameNotSaved)) return null;        // (the `runaway-tree` check's deliberate bug)
        lock (runawayNames) return RunawayNames.Build(new Dictionary<string, string>(runawayNames));
    }

    /// <summary>The Game view's "Play N s, then freeze": the game runs on, then Instrument Runaway switches on by itself, with the game voices its sound driver played in that time
    /// (the same as the Corrupt view's tick, for a game that was not corrupted). The progress goes to the corruption note over the picture.</summary>
    internal string FreezeLater(int seconds)
    {
        if (!CanRunaway) return "Load a game in ROM mode first.";
        if (Runaway) return "Instrument Runaway is on already: switch it off first.";
        if (seconds <= 0) { SetRunaway(true); return "Instrument Runaway is on."; }
        var g = Volatile.Read(ref game);
        double fps = g?.FramesPerSecond is > 1 and var f ? f : 60.0988;
        long target = FramesRun + (long)Math.Round(seconds * fps);
        Corruption.Listening = $"the game plays for {seconds} s, then Instrument Runaway switches on by itself";
        Task.Run(() =>
        {
            try
            {
                long last = FramesRun, lastProgress = Environment.TickCount64;
                while (FramesRun < target)
                {
                    if (!ReferenceEquals(Volatile.Read(ref game), g)) { Corruption.SetResult("Another game took the emulator's place: Instrument Runaway was not switched on", false); return; }
                    if (FramesRun != last) { last = FramesRun; lastProgress = Environment.TickCount64; }
                    else if (Environment.TickCount64 - lastProgress > 10_000) { Corruption.SetResult("The game is not running (FL is stopped and the plugin window is closed): press Instrument Runaway when you are ready", false); return; }
                    Corruption.Listening = $"the game is playing: {Math.Max(0, (target - FramesRun) / fps):0} s left, then Instrument Runaway switches on";
                    Thread.Sleep(50);
                }
                SetRunaway(true);
                Corruption.SetResult($"Instrument Runaway is on after {seconds} s of play" + (ListenNote.Length > 0 ? ". " + ListenNote : ""), true);
            }
            catch (Exception e) { BrokenNes.Fruity.Diag.Log("freeze later: " + e); Corruption.SetResult("could not switch Instrument Runaway on: " + e.Message, false); }
            finally { Corruption.Listening = ""; }
        });
        return $"the game plays for {seconds} s, then Instrument Runaway switches on";
    }

    /// <summary>The song bar's "in &lt;game&gt;" picker: which game "This FL project" builds for (remembered in the preferences, as the Song view's button does).</summary>
    internal void SetFlpTarget(GameTarget t) { FlpTarget = t; Prefs.Set("FlpTarget", t.Id); }
}
