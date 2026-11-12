// `FruityHost mt-export vocgb|vocgg <song.flp> <out folder> [--nes-as-gb auto|on|off]`: the FL TEST slot of the Vaults music test players
// (BrokenDev/tools/music/flp/musictest/build-vaults-gb-mt.ps1 / build-vaults-gg-mt.ps1; the players live in VRUN_VOC_dev musictest/ and VRUN_VOC_GG_dev musictest/).
// Writes <name>.vocsong.json (the game's roster import, VocGbBackend.EventsJson: the player's build feeds it to the game's own converter) and
// <name>.<game>.fence.txt (first line: the summary with " over the limit"; then every fence line). The song is made by the existing back ends, unchanged:
// VocGbBackend.Build (vocgb) / VocGgBackend.Build (vocgg).
// --nes-as-gb: the user's songs are written with BrokenNes2 instances on the NES console; the Vaults back ends only read Game Boy (and, for GG, Master System)
// instances, so such a song has NOTHING for the game. auto (default): when the song has no instance for the target console, its NES Direct instances are read
// as Game Boy instances on the same channel (pulse 1/2, triangle -> wave, noise: the Game Boy back end's own NES-like mapping), and the fence says so
// in its first note line. on: always. (Since 2026-11-12 the back ends do auto themselves, FlpNesFallback: NES is the inter-console standard; off was removed.)
// Exit 0 when written (OVER lines are the composer's, not an error), 1 on an error.
using BrokenNes2.Music.Flp;

namespace BrokenNes.FruityHost;

internal static class MusicTestExport
{
    public static int Main(string[] args)
    {
        if (args.Length < 4 || args[1] is not ("vocgb" or "vocgg")) { Console.WriteLine("usage: mt-export vocgb|vocgg <song.flp> <out folder> [--nes-as-gb auto|on|off]"); return 1; }
        string game = args[1], flp = args[2], dir = args[3];
        string mode = "auto";
        for (int i = 4; i + 1 < args.Length; i++) if (args[i] == "--nes-as-gb") mode = args[i + 1];
        if (mode == "off") { Console.WriteLine("FAIL  --nes-as-gb off was removed (2026-11-12): a song with no instance for the console plays its NES instances (FlpNesFallback)"); return 1; }
        try
        {
            var model = FlpSong.Read(File.ReadAllBytes(flp));
            string name = Path.GetFileNameWithoutExtension(flp);
            Directory.CreateDirectory(dir);
            bool Native(FlpInstrument i) => i.Bn2 is { } b && i.Notes.Count > 0 && (b.Console == 1 || (game == "vocgg" && b.Console == 3));
            var nes = model.Instruments.Where(i => i.Bn2 is { } b && b.Console == 0 && b.Mode == "direct" && i.Notes.Count > 0 && b.Preset == null).ToList();
            bool retarget = mode == "on" || (mode == "auto" && !model.Instruments.Any(Native));
            if (!retarget) nes.Clear();
            foreach (var i in nes) { i.Bn2!.Console = 1; i.Bn2.ConsoleName = "GB"; }
            VocSong v = game == "vocgb" ? VocGbBackend.Build(model, name) : VocGgBackend.Build(model, name);
            var fence = v.Fence.ToList();
            if (nes.Count > 0)
                fence.Insert(0, new FenceLine("music test", FenceLevel.Note, $"{nes.Count} NES instances ({string.Join(", ", nes.Select(i => $"\"{i.Name}\""))}) read as Game Boy instances on the same channels (the song has none for {(game == "vocgb" ? "Vaults GB" : "Vaults GG")}): mt-export --nes-as-gb {mode}"));
            File.WriteAllText(Path.Combine(dir, name + ".vocsong.json"), VocGbBackend.EventsJson(v));
            var lines = FlpFence.Lines(fence).ToList();
            File.WriteAllLines(Path.Combine(dir, $"{name}.{game}.fence.txt"), [$"{Path.GetFileName(flp)} for {game} (music test): {FlpFence.Summary(fence)}", .. lines]);
            Console.WriteLine($"{Path.GetFileName(flp)} for {game} (music test): {FlpFence.Summary(fence)}");
            foreach (var l in lines) Console.WriteLine("  " + l);
            Console.WriteLine($"  {v.Seconds:0.00} s, notes {string.Join("/", v.Notes)}; wrote {name}.vocsong.json in {dir}");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL  " + e.Message); return 1; }
    }
}
