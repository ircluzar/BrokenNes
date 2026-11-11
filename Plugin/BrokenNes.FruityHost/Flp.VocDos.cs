// `FruityHost flp-vocdos <song.flp> <out folder> [dos|95]`: an FL project for the Vaults DOS / Vaults 95 music tests (BrokenDev/tools/music/flp/musictest/build-vaults-*-mt.ps1):
//   <name>.vocfl.c           the song for the music test's FL slot (Music/Flp/VocDosBackend.cs: the Game Gear driver's song bytes, played by the PC ports' unchanged driver on the OPL2)
//   <name>.vaults-<t>.fence.txt  every rule: ok / note / OVER (first line: "<file> for vaults-<t>: N over the limit, ...")
//   --as-gb: every BrokenNes2 instance is read as a Game Boy instance (FlpVocChecks.Retarget, as flp-test reads the user's NES songs for Vaults); the fence says so in a note line.
// Exit 0 when written, 2 when the fence has OVER lines (written all the same), 1 on an error.
using BrokenNes2.Music.Flp;

namespace BrokenNes.FruityHost;

internal static class FlpVocDos
{
    public static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: flp-vocdos <song.flp> <out folder> [dos|95] [--as-gb]"); return 1; }
        string flp = args[1], dir = args[2], t = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : "dos";
        bool asGb = args.Contains("--as-gb");
        if (!File.Exists(flp)) { Console.WriteLine("FAIL  no such .flp: " + flp); return 1; }
        var model = FlpSong.Read(File.ReadAllBytes(flp));
        int moved = asGb ? model.Instruments.Count(i => i.Bn2 is { Console: not 1 }) : 0;
        if (asGb) FlpVocChecks.Retarget(model, 1);
        string name = Path.GetFileNameWithoutExtension(flp);
        Directory.CreateDirectory(dir);
        var v = VocDosBackend.Build(model, name);
        File.WriteAllText(Path.Combine(dir, name + ".vocfl.c"), VocDosBackend.CSource(v, name));
        if (asGb) v.Fence.Add(new FenceLine("retarget", FenceLevel.Note, $"--as-gb: {moved} BrokenNes2 instances set to another console in the .flp were read as Game Boy instances (the song as composed for the NES, played by the Game Boy channels)"));
        var lines = FlpFence.Lines(v.Fence).ToList();
        string head = $"{Path.GetFileName(flp)} for vaults-{t}: {FlpFence.Summary(v.Fence)}";
        File.WriteAllLines(Path.Combine(dir, $"{name}.vaults-{t}.fence.txt"), [head, .. lines]);
        Console.WriteLine(head);
        foreach (var l in lines) Console.WriteLine("  " + l);
        return v.Fence.Any(f => f.Level == FenceLevel.Over) ? 2 : 0;
    }
}
