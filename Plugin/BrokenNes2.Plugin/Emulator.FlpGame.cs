// "This FL project" / Open for the consoles other than the NES (docs/projects/flp-music-pipeline.md): the .flp's BrokenNes2 instances say which console the song is for (the console with
// the most notes in Direct mode); a Game Boy song becomes a Vaults GB test ROM, a Master System song a CTW SMS one (Vaults GG, or any of them on purpose: the "In game:" picker, GameTargets),
// each built from that game's own sound driver with the song written in (Music/Flp: VocGbBackend, VocGgBackend, CtwSmsBackend). The ROM is written to %TEMP%\BrokenNes2\FlpGames and
// loaded as this emulator's game (ROM mode, that console): the window says what was built and the fence (every limit of the game's engine the song is outside of) is the note.
// A NES song (or a project with no other console's notes) goes on to VRUN NES as before.
using BrokenNes2.Music.Flp;

namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Which game a project's song goes to when the picker says VRUN NES: 0 = VRUN NES, 1 = Vaults GB (most notes on the Game Boy console), 3 = CTW SMS (on the Master System
    /// console). Vaults GG (and any of these on purpose) is chosen in the "In game:" picker (GameTargets "vaults-gg").</summary>
    internal static int FlpAutoTarget(FlpSongModel s)
    {
        var notes = new int[Cx.Count];
        foreach (var i in s.Instruments) if (i.Bn2 is { Mode: "direct" } b && (uint)b.Console < (uint)notes.Length) notes[b.Console] += i.Notes.Count;
        int best = Array.IndexOf(notes, notes.Max());
        if (notes.Max() == 0) return 0;
        return best == Cx.GameBoy ? 1 : best == Cx.MasterSystem ? 3 : 0;
    }

    /// <summary>"In game:" Vaults GB / GG or CTW SMS (GameTarget.Inside): built here on a worker. Null when started.</summary>
    internal string? BuildInside(string flp, GameTarget t)
    {
        int target = t.Id switch { "vaults-gb" => 1, "vaults-gg" => 2, _ => 3 };
        SongUi.Busy = true;
        SongUi.Status = $"building {Path.GetFileName(flp)} for {t.Label} with the game's own driver ...";
        Task.Run(() =>
        {
            try { FlpOtherConsole(flp, File.ReadAllBytes(flp), target); }
            catch (Exception e) { SongUi.SetResult($"{t.Label}: {e.Message.TrimEnd('.')}: nothing changed", false); }
            finally { SongUi.Status = ""; SongUi.Busy = false; }
        });
        return null;
    }

    private static readonly string[] FlpGameNames = ["VRUN NES", "Vaults GB", "Vaults GG", "CTW SMS"];

    private static (byte[] Rom, string Manifest) FlpTemplate(string rom)
    {
        var asm = typeof(Emulator).Assembly;
        byte[] Read(string n) { using var st = asm.GetManifestResourceStream(n) ?? throw new InvalidOperationException($"no resource {n}"); using var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray(); }
        return (Read(rom), System.Text.Encoding.ASCII.GetString(Read(Path.ChangeExtension(rom, ".txt"))));
    }

    /// <summary>Worker thread: a project for another console becomes that game's test ROM and is loaded. False: it is a NES song (the caller goes on).</summary>
    private bool FlpOtherConsole(string path, byte[] bytes, int forced = 0)
    {
        string file = Path.GetFileName(path), name = Music.SongConvert.NameOf(path);
        var model = FlpSong.Read(bytes);
        int target = forced != 0 ? forced : FlpAutoTarget(model);
        if (target == 0) return false;
        byte[]? rom; string? why; List<FenceLine> fence; string ext;
        switch (target)
        {
            case 1: { var v = VocGbBackend.Build(model, name); var t = FlpTemplate("flpvocgb.gbc"); rom = VocGbBackend.Patch(t.Rom, t.Manifest, v, out why); fence = v.Fence; ext = ".gb"; break; }
            case 2: { var v = VocGgBackend.Build(model, name); var t = FlpTemplate("flpvocgg.gg"); rom = VocGgBackend.Patch(t.Rom, t.Manifest, v, out why); fence = v.Fence; ext = ".gg"; break; }
            default: { var v = CtwSmsBackend.Build(model, name); var t = FlpTemplate("flpctwsms.sms"); rom = CtwSmsBackend.Patch(t.Rom, t.Manifest, v, out why); fence = v.Fence; ext = ".sms"; break; }
        }
        string game = FlpGameNames[target];
        var lines = FlpFence.Lines(fence).ToList();
        BrokenNes.Fruity.Diag.Log($"song from {path} for {game}: {FlpFence.Summary(fence)}: " + string.Join(" | ", lines));
        if (rom == null) { SongUi.SetResult($"{file} was not loaded: it does not fit {game}'s test ROM ({why}). Nothing changed", false); SongUi.SetNote(string.Join("\n", lines.Take(8))); return true; }
        string dir = Path.Combine(Path.GetTempPath(), "BrokenNes2", "FlpGames");
        Directory.CreateDirectory(dir);
        string romPath = Path.Combine(dir, name + "." + game.Replace(' ', '-').ToLowerInvariant() + ext);
        File.WriteAllBytes(romPath, rom);
        File.WriteAllLines(Path.ChangeExtension(romPath, ".fence.txt"), [$"{file} for {game}: {FlpFence.Summary(fence)}", .. lines]);
        MarkFlpRom(romPath);
        string? err = LoadRomFile(romPath);
        if (err != null) { SongUi.SetResult($"{file}: the {game} test ROM was built but not loaded: {err}", false); return true; }
        SongUi.SetResult($"Loaded {file} into {game}'s own sound driver ({FlpFence.Summary(fence)}): it plays from its start", true);
        SongUi.SetNote($"{game}: " + string.Join("\n", lines.Where(l => !l.StartsWith("ok")).DefaultIfEmpty("every rule of the game's engine is met").Take(10)) + $"\n(the whole fence: {Path.ChangeExtension(romPath, ".fence.txt")})");
        return true;
    }
}
