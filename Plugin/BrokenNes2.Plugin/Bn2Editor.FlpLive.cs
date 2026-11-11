// Live re-ingest (2026-11-10): once "This FL project" has played the picked .flp (ProjectFlp.cs), saving the project in FL again rebuilds and reloads it with no button press:
// the editor's UI poll (Signature -> SongSignature) checks the file's save time; when it changed and has stayed put for a second (FL writes the file in steps), the same path as
// the button runs (UseProjectFlp: VRUN NES in the plugin, the other games and the music test players through their build scripts). Never while a build is running.
namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    private string? liveFlp;            // the file the last "This FL project" ingested (null: not armed)
    private DateTime liveFlpStamp;      // its save time then
    private DateTime liveFlpSeen;       // a newer save time seen, waiting to settle
    private long liveFlpSeenAt;

    private void ArmLiveFlp(string path)
    {
        liveFlp = path;
        try { liveFlpStamp = File.GetLastWriteTimeUtc(path); } catch { liveFlpStamp = default; }
        liveFlpSeen = liveFlpStamp;
    }

    private void LiveFlpTick()
    {
        var emu = plugin.Emulator;
        if (liveFlp == null || emu == null || emu.SongUi.Busy || liveFlp != plugin.ProjectFlpPath) return;
        DateTime t;
        try { if (!File.Exists(liveFlp)) return; t = File.GetLastWriteTimeUtc(liveFlp); } catch { return; }
        if (t == liveFlpStamp) return;
        if (t != liveFlpSeen) { liveFlpSeen = t; liveFlpSeenAt = Environment.TickCount64; return; }
        if (Environment.TickCount64 - liveFlpSeenAt < 1000) return;
        liveFlpStamp = t;
        BrokenNes.Fruity.Diag.Log($"This FL project: {liveFlp} saved again ({t.ToLocalTime():HH:mm:ss}): rebuilding for {emu.FlpTarget.Label}");
        emu.SongUi.SetNote($"{Path.GetFileName(liveFlp)} was saved again: rebuilt and reloaded automatically");
        UseProjectFlp(change: false);
    }
}
