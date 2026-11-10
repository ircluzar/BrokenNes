// "This FL project" / Open for the consoles other than the NES (docs/projects/flp-music-pipeline.md): the .flp's BrokenNes2 instances say which console the song is for (the console with
// the most notes in Direct mode); a Game Boy song becomes a Vaults GB test ROM, a Master System song a CTW SMS one (or a Vaults GG one when the project's name or title says GG / Game Gear),
// each built from that game's own sound driver with the song written in (Music/Flp: VocGbBackend, VocGgBackend, CtwSmsBackend). The ROM is written to %TEMP%\BrokenNes2\FlpGames and
// loaded as this emulator's game (ROM mode, that console): the window says what was built and the fence (every limit of the game's engine the song is outside of) is the note.
// A NES song (or a project with no other console's notes) goes on to VRUN NES as before.
using BrokenNes2.Music.Flp;

namespace BrokenNes2;

public sealed partial class Emulator
{
    /// <summary>Which game a project's song is for: 0 = VRUN NES (the old path), 1 = Vaults GB, 2 = Vaults GG, 3 = CTW SMS.</summary>
    internal static int FlpTarget(FlpSongModel s, string path)
    {
        var notes = new int[Cx.Count];
        foreach (var i in s.Instruments) if (i.Bn2 is { Mode: "direct" } b && (uint)b.Console < (uint)notes.Length) notes[b.Console] += i.Notes.Count;
        int best = Array.IndexOf(notes, notes.Max());
        if (notes.Max() == 0 || best == Cx.Nes) return 0;
        if (best == Cx.GameBoy) return 1;
        if (best != Cx.MasterSystem) return 0;
        string words = (Path.GetFileNameWithoutExtension(path) + " " + s.Title).ToUpperInvariant();
        return words.Contains("GAME GEAR") || words.Contains("GAMEGEAR") || System.Text.RegularExpressions.Regex.IsMatch(words, @"\bGG\b") ? 2 : 3;
    }

    private static readonly string[] FlpGameNames = ["VRUN NES", "Vaults GB", "Vaults GG", "CTW SMS"];

    private static (byte[] Rom, string Manifest) FlpTemplate(string rom)
    {
        var asm = typeof(Emulator).Assembly;
        byte[] Read(string n) { using var st = asm.GetManifestResourceStream(n) ?? throw new InvalidOperationException($"no resource {n}"); using var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray(); }
        return (Read(rom), System.Text.Encoding.ASCII.GetString(Read(Path.ChangeExtension(rom, ".txt"))));
    }

    /// <summary>Worker thread: a project for another console becomes that game's test ROM and is loaded. False: it is a NES song (the caller goes on).</summary>
    private bool FlpOtherConsole(string path, byte[] bytes)
    {
        string file = Path.GetFileName(path), name = Music.SongConvert.NameOf(path);
        var model = FlpSong.Read(bytes);
        int target = FlpTarget(model, path);
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
        string? err = LoadRomFile(romPath);
        if (err != null) { SongUi.SetResult($"{file}: the {game} test ROM was built but not loaded: {err}", false); return true; }
        SongUi.SetResult($"Loaded {file} into {game}'s own sound driver ({FlpFence.Summary(fence)}): it plays from its start", true);
        SongUi.SetNote($"{game}: " + string.Join("\n", lines.Where(l => !l.StartsWith("ok ")).DefaultIfEmpty("every rule of the game's engine is met").Take(10)) + $"\n(the whole fence: {Path.ChangeExtension(romPath, ".fence.txt")})");
        return true;
    }
}
