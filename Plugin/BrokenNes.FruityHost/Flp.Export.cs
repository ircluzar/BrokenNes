// `FruityHost flp-export vocgb|vocgg|ctwsms <song.flp> <out folder>`: an FL project for one of the VRUN games on the Game Boy / Game Gear / Master System, the way the user ships it:
//   <name>.<game>.<ext>    the test ROM (the game's own sound driver with the song written in), as the plugin's "This FL project" makes it
//   <name>.<game>.fence.txt  every rule of the game's engine: ok / CHANGED / OUT and by how much
//   vocgb / vocgg: <name>.vocsong.json   the game's import (VRUN_VOC_dev tools/music/roster.json: {"events": "<file>"}; the GG port takes the Game Boy songs)
//   ctwsms:        <name>.vrun.mid       the game's import (CTW SMS translates VRUN NES's music: the file VRUN's own converter reads, as VRUN NES's roster takes it; convert it there WITHOUT bends)
// Exit 0 when written, 2 when the fence has OUT lines (written all the same), 1 on an error.
using BrokenNes2.Music.Flp;

namespace BrokenNes.FruityHost;

internal static class FlpExport
{
    public static int Main(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("usage: flp-export vocgb|vocgg|ctwsms <song.flp> <out folder>"); return 1; }
        string game = args[1], flp = args[2], dir = args[3];
        var model = FlpSong.Read(File.ReadAllBytes(flp));
        string name = Path.GetFileNameWithoutExtension(flp);
        Directory.CreateDirectory(dir);
        List<FenceLine> fence; byte[]? rom; string? why; string ext;
        switch (game)
        {
            case "vocgb":
                {
                    var v = VocGbBackend.Build(model, name); var (t, m) = FlpVocChecks.Template();
                    rom = VocGbBackend.Patch(t, m, v, out why); fence = v.Fence; ext = ".gb";
                    File.WriteAllText(Path.Combine(dir, name + ".vocsong.json"), VocGbBackend.EventsJson(v));
                    break;
                }
            case "vocgg":
                {
                    var v = VocGgBackend.Build(model, name); string rp = FlpVocChecks.RepoFile(@"Plugin\BrokenNes2.Plugin\Resources\flpvocgg.gg");
                    rom = VocGgBackend.Patch(File.ReadAllBytes(rp), File.ReadAllText(Path.ChangeExtension(rp, ".txt")), v, out why); fence = v.Fence; ext = ".gg";
                    File.WriteAllText(Path.Combine(dir, name + ".vocsong.json"), VocGbBackend.EventsJson(v));
                    break;
                }
            case "ctwsms":
                {
                    var v = CtwSmsBackend.Build(model, name); var (t, m) = FlpSmsChecks.Template();
                    rom = CtwSmsBackend.Patch(t, m, v, out why); fence = v.Fence; ext = ".sms";
                    File.WriteAllBytes(Path.Combine(dir, name + ".vrun.mid"), v.Nes.Midi);
                    break;
                }
            default: Console.WriteLine($"unknown game {game}: vocgb, vocgg or ctwsms"); return 1;
        }
        var lines = FlpFence.Lines(fence).ToList();
        File.WriteAllLines(Path.Combine(dir, $"{name}.{game}.fence.txt"), [$"{Path.GetFileName(flp)} for {game}: {FlpFence.Summary(fence)}", .. lines]);
        Console.WriteLine($"{Path.GetFileName(flp)} for {game}: {FlpFence.Summary(fence)}");
        foreach (var l in lines) Console.WriteLine("  " + l);
        if (rom == null) { Console.WriteLine("no test ROM: " + why); return 1; }
        File.WriteAllBytes(Path.Combine(dir, $"{name}.{game}{ext}"), rom);
        Console.WriteLine($"wrote {Path.Combine(dir, $"{name}.{game}{ext}")}");
        return fence.Any(f => f.Fit == Fit.Out) ? 2 : 0;
    }
}
