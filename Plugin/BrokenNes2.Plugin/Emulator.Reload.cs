// Round 8 `reload` (2026-10-18): a recording offered again from the files it left (its MIDI file, and its tracks' presets in the library), so BrokenNes2 channels can still find their tracks after
// FL was restarted without the project, or (the self-test) when the user's own FL session is replayed from its log against the recording that session used. Record/RecReload.cs reads the file.
using BrokenNes2.Presets;
using BrokenNes2.Record;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Offers the recording a MIDI file holds, with the presets of its tracks from <paramref name="presetFolder"/> (null: the library's Recordings folder named after the recording). The
    /// recording becomes this emulator's (the Record window lists it). The words for the window.</summary>
    internal (bool Ok, string Message) OfferFromFiles(string midiPath, string? presetFolder)
    {
        if (RecordUi.State is RecordPanel.Armed or RecordPanel.Recording or RecordPanel.Finishing) return (false, "a recording is being made here: wait for it, or use another emulator");
        byte[] midi;
        try { midi = File.ReadAllBytes(midiPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return (false, "the MIDI file could not be read: " + e.Message); }
        string? folder = presetFolder;
        var (probe, why) = RecReload.FromMidi(midi, _ => null);
        if (probe == null) return (false, why ?? "the MIDI file is not a recording's");
        if (folder == null)
        {
            // the library keeps a recording's presets in <library>\<game>\Recordings\<recording>
            string lib = PresetLibrary.FolderPath(), want = PresetStore.SafeFileName(probe.Session.Name);
            try { folder = Directory.Exists(lib) ? Directory.EnumerateDirectories(lib, want, SearchOption.AllDirectories).FirstOrDefault(d => string.Equals(Path.GetFileName(Path.GetDirectoryName(d)), "Recordings", StringComparison.OrdinalIgnoreCase)) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { folder = null; }
        }
        byte[]? PresetOf(string track)
        {
            if (folder == null) return null;
            string p = Path.Combine(folder, PresetStore.SafeFileName(track) + PresetFile.Extension);
            try { return File.Exists(p) ? File.ReadAllBytes(p) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        var (tr, why2) = RecReload.FromMidi(midi, PresetOf);
        if (tr == null) return (false, why2 ?? "the MIDI file is not a recording's");
        int found = tr.Tracks.Count(t => t.PresetFile != null);
        if (found == 0) return (false, folder == null ? $"no presets of \"{tr.Session.Name}\" in the library (Presets\\<game>\\Recordings\\{PresetStore.SafeFileName(tr.Session.Name)})" : $"no preset of its tracks in {folder}");
        string dir = Path.GetDirectoryName(Path.GetFullPath(midiPath)) ?? ".";
        var paths = tr.Tracks.Select(t => Path.Combine(dir, PresetStore.SafeFileName(t.Name) + ".mid")).Select(p => File.Exists(p) ? p : "").ToArray();
        tr.Facts.Add($"read back from {Path.GetFileName(midiPath)}: {tr.Tracks.Count} tracks, {found} with their preset{(folder != null ? $" (from {folder})" : "")}");
        foreach (var t in tr.Tracks.Where(t => t.PresetFile == null)) tr.Facts.Add($"{t.Name}: {t.PresetProblem}");
        string note = $"{found} of {tr.Tracks.Count} presets found";
        recSection = null; recCarried = null; recProblem = null;
        recording = new RecordHeld(tr.Session, tr, dir, Path.GetFullPath(midiPath), paths, new string[tr.Tracks.Count], note, Interlocked.Increment(ref recGeneration));
        RecordUi.State = RecordPanel.Done;
        RecordUi.Status = "";
        TrackOffers.Offer(Id, recording);
        string msg = $"\"{tr.Session.Name}\" read back: its {tr.Tracks.Count} tracks are on offer ({note}). Drag its MIDI file into FL and Replace each channel by BrokenNes2.";
        RecordUi.SetResult(msg, true);
        return (true, msg);
    }

    /// <summary>Tests (FruityHost alloc-gc-probe contend): holds the game's lock for <paramref name="ms"/> milliseconds, as a worker thread does between frames.</summary>
    internal void HoldRomLockForTests(int ms)
    {
        lock (romLock) Thread.Sleep(ms);
    }
}
