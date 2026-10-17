// Round 6 (presets): the preset library behind the Presets tab (Ui/Contracts.cs: IPresetLibrary), over the folder library (Presets/PresetStore.cs) and the preset the editor's instance holds. One library for the whole process, registered through the UiBoot seam in this file.
//
// Where the files are: %APPDATA%\BrokenNes\Presets (subfolders are categories; a new preset goes in the folder of the game it was ripped from). The environment variable BROKENNES_PRESETS_DIR names another folder (tests, a portable setup); it is read every time, so a test can point
// the library at a scratch folder and back. No preset ships with the product, and a preset ripped from a game holds the game's samples: it stays on the user's computer (git ignores the extension).
//
// "Load" and "Audition" act on the instance whose editor is handling the event (PresetHost.Current). The preset an instance holds that the library has no file for (the library file was moved or deleted, or the project came from another computer) is listed too, as "in this FL project".
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes2.Ui;

internal static partial class UiBoot
{
    static partial void RegisterPresetLibrary() => UiServices.Presets = PresetLibrary.Instance;
}

internal sealed class PresetLibrary : IPresetLibrary
{
    public static readonly PresetLibrary Instance = new();
    public const string ProjectPrefix = "project:";

    /// <summary>The library's folder: BROKENNES_PRESETS_DIR, else %APPDATA%\BrokenNes\Presets.</summary>
    public static string FolderPath()
    {
        string? env = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR");
        return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes", "Presets");
    }

    private PresetStore? store;
    private string? storeFolder;
    private long folderChanges;

    private PresetStore Store
    {
        get
        {
            string folder = FolderPath();
            if (store == null || !string.Equals(storeFolder, folder, StringComparison.OrdinalIgnoreCase)) { store = new PresetStore(folder); storeFolder = folder; folderChanges++; }
            return store;
        }
    }

    public string? Unavailable => null;

    public long Version
    {
        get
        {
            var s = Store;
            s.Refresh();
            var lp = PresetHost.Current?.Preset;
            return unchecked(s.Version * 1_000_003L + folderChanges * 7919L + (lp == null ? 0 : PresetFile.Checksum(lp.File) + 1));      // moves with the folder, and with the preset the editor's instance holds
        }
    }

    /// <summary>Looks at the folder again now; returns how many presets (files) it lists.</summary>
    public int Rescan() { Store.Refresh(force: true); return Store.Entries.Count; }

    private bool InLibrary(LoadedPreset lp)
    {
        uint crc = PresetFile.Checksum(lp.File);
        return Store.Entries.Any(e => e.Problem == null && e.Crc == crc && e.Size == lp.File.Length);
    }

    private static string ProjectId(LoadedPreset lp) => ProjectPrefix + PresetFile.Checksum(lp.File).ToString("X8");

    // ---- listing ----

    public IReadOnlyList<PresetSummary> Search(string text, string? console)
    {
        var rows = new List<PresetSummary>();
        var lp = PresetHost.Current?.Preset;
        if (lp != null && !InLibrary(lp))
        {
            string c = PresetFile.ConsoleName(lp.Data.Console);
            var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool hit = (console == null || console == c) && words.All(w => lp.Data.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || lp.Data.SourceGame.Contains(w, StringComparison.OrdinalIgnoreCase) || lp.Data.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase)) || c.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hit) rows.Add(new PresetSummary(ProjectId(lp), lp.Data.Name, c, "This project", "kept in this FL project only; the library has no file for it"));
        }
        foreach (var e in Store.Search(text ?? "", console)) rows.Add(new PresetSummary(e.Id, e.Name, e.ConsoleName, e.Category, e.Detail));
        return rows;
    }

    public PresetDetails? Details(string id)
    {
        if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal))
        {
            var lp = PresetHost.Current?.Preset;
            if (lp == null || ProjectId(lp) != id) return null;
            var d = lp.Data;
            return new PresetDetails(id, d.Name, PresetFile.ConsoleName(d.Console), "This project", d.SourceGame, d.SourceSha1, d.Tags, d.Notes, d.FieldCount, d.BlobCount, d.HasNoteBehaviour, "this project");
        }
        var e = Store.Entries.FirstOrDefault(x => x.Id == id);
        if (e == null) return null;
        if (e.Problem != null) return new PresetDetails(id, e.Name, "?", e.Category, "", "", [], "This file cannot be opened: " + e.Problem + ". It is listed so that it is not hidden; delete it, or replace it with a good copy.", 0, 0, false, "library");
        return new PresetDetails(id, e.Name, e.ConsoleName, e.Category, e.Game, e.Sha1, e.Tags, e.Notes, e.Fields, e.Blobs, e.HasNoteBehaviour, "library");
    }

    // ---- actions ----

    public ActionResult Add(PresetDraft draft)
    {
        if (draft.Payload is not InstrumentPayload pl) return ActionResult.Refused("This instrument was not packaged by the instrument model; it cannot be kept as a preset.");
        var host = PresetHost.Current;
        string sha = host?.Emulator?.SourceGameSha1() ?? "";
        string instrumentName = draft.Name.EndsWith(" (corrupted)", StringComparison.Ordinal) ? draft.Name[..^12] : draft.Name;
        var d = PresetMaker.Make(draft.Name.Trim(), instrumentName, draft.SourceGame, sha, draft.Notes, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), pl.Packed, pl.CleanPacked, pl.HistoryBytes, pl.Context, out string? problem);
        if (d == null) return ActionResult.Refused("This instrument cannot be kept as a preset: " + problem);
        if (host?.Emulator?.Snapshot is { } frozen) PresetNote.Attach(d, frozen.Recipes, d.Index);       // (the game voices of this sample: what the driver was heard doing with it, Presets/PresetNote.cs)
        var r = Store.Add(d);
        Store.Refresh(force: true);
        return r.Ok ? ActionResult.Done(r.Message + ". Open the Presets tab to rename it or play it with no ROM.") : ActionResult.Refused(r.Message);
    }

    public ActionResult Load(string id)
    {
        var host = PresetHost.Current;
        if (host == null) return ActionResult.Refused("No FL instance is attached to this window.");
        if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal))
            return host.Preset != null && ProjectId(host.Preset) == id ? host.LoadPresetFile(host.Preset.File, host.Preset.LibraryId) : ActionResult.Refused("That preset is not in this project any more.");
        var r = Store.Load(id, out var bytes);
        if (r.Data == null || bytes == null) return ActionResult.Refused("Cannot load: " + r.Problem);
        return host.LoadPresetFile(PresetMutants.On(PresetMutants.LoadsWrongBytes) ? bytes.AsSpan(0, bytes.Length - 1).ToArray() : bytes, id);
    }

    public ActionResult Rename(string id, string newName)
    {
        if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal)) return ActionResult.Refused("This copy lives in the FL project only. Export it, import the file, and rename that one.");
        var host = PresetHost.Current;
        bool held = host?.Preset?.LibraryId == id;               // the instance plays this very file: it follows the rename, so that the library and the project do not end up with two spellings of one preset
        var r = Store.Rename(id, newName);
        if (!r.Ok) return ActionResult.Refused(r.Message);
        if (held && r.Id != null)
        {
            var again = Store.Load(r.Id, out var bytes);
            if (again.Data != null && bytes != null && host!.AdoptPreset(bytes, r.Id, out _) != null)
                return ActionResult.Done(r.Message + ". (This instance keeps the preset under its old name: the renamed file could not be loaded into it.)");
        }
        return ActionResult.Done(r.Message);
    }

    public ActionResult Delete(string id)
    {
        if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal))
        {
            var host = PresetHost.Current;
            if (host?.Preset == null || ProjectId(host.Preset) != id) return ActionResult.Refused("That preset is not in this project any more.");
            string name = host.Preset.Data.Name;
            host.ClearPreset();
            return ActionResult.Done($"\"{name}\" is no longer kept in this project; this instance plays nothing until a preset is loaded.");
        }
        var r = Store.Delete(id);
        return r.Ok ? ActionResult.Done(r.Message) : ActionResult.Refused(r.Message);
    }

    public ActionResult Export(string id, string path)
    {
        if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal))
        {
            var lp = PresetHost.Current?.Preset;
            if (lp == null || ProjectId(lp) != id) return ActionResult.Refused("That preset is not in this project any more.");
            var pr = PresetStore.ExportBytes(lp.File, path, lp.Data.Name);
            return pr.Ok ? ActionResult.Done(pr.Message) : ActionResult.Refused(pr.Message);
        }
        var r = Store.Export(id, path);
        return r.Ok ? ActionResult.Done(r.Message) : ActionResult.Refused(r.Message);
    }

    public ActionResult Import(string path)
    {
        var r = Store.Import(path);
        Store.Refresh(force: true);
        return r.Ok ? ActionResult.Done(r.Message) : ActionResult.Refused(r.Message);
    }

    public ActionResult Audition(string id)
    {
        var host = PresetHost.Current;
        if (host == null) return ActionResult.Refused("No FL instance is attached to this window: there is nothing to play through.");
        PresetData? data;
        if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal))
        {
            if (host.Preset == null || ProjectId(host.Preset) != id) return ActionResult.Refused("That preset is not in this project any more.");
            data = host.Preset.Data;
        }
        else
        {
            var r = Store.Load(id, out _);
            if (r.Data == null) return ActionResult.Refused("Cannot play: " + r.Problem);
            data = r.Data;
        }
        var play = PresetPlayable.Build(data, out string? why);
        if (play == null) return ActionResult.Refused("Cannot play: " + why);
        return host.StartAudition(play);
    }
}
