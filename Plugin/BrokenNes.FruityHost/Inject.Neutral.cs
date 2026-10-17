// Round 6 (inject): `FruityHost inject-hash` prints the hash of the game's own audio (and the frame count) for a real game of each console, run from power-on with a scripted pad and NO push. It uses nothing of the push library, so the same
// file builds on a tree that has none: run it on a build of `main` from before the hooks and on this branch and compare the lines; identical lines mean the hooks (all null) changed nothing in the SNES S-DSP, the YM2612, the YM2413 or the Game Boy APU
// on those games. Games that are not there are skipped.
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static class InjectNeutral
{
    public static int Main(string[] args)
    {
        int frames = int.TryParse(args.Length > 1 ? args[1] : null, out int f) ? f : 2400;
        string? Find(string folder, string frag, params string[] ext) => ImodelGames.Find(folder, frag, ext);
        var games = new (string Label, ConsoleKind Kind, string? Path, string Apu, string Script)[]
        {
            ("Super Mario World (SNES)", ConsoleKind.Snes, File.Exists(ImodelGames.Smw) ? ImodelGames.Smw : null, "", "500:Start,508:,700:A,708:,900:A,908:,1100:A,1108:,1300:A,1308:,1600:B,1610:,1650:A,1660:,1700:B,1710:,1750:A,1760:,1800:B,1810:,1850:A,1860:,1900:B,1910:,1950:A,1960:,2000:B,2010:,2050:A,2060:,2100:B,2110:,2150:A,2160:,2200:B,2210:,2250:A,2260:,2300:B,2310:"),
            ("Sonic the Hedgehog (Genesis)", ConsoleKind.Genesis, File.Exists(ImodelGames.Sonic1) ? ImodelGames.Sonic1 : null, "MD", "400:Start,420:,900:Right,1700:Right+A,1730:Right"),
            ("Link's Awakening (Game Boy)", ConsoleKind.GameBoy, Find(@"X:\EMULATION\GameBoy", "Link's Awakening", ".zip"), "", "300:Start,310:,600:Start,610:,900:A,910:"),
            ("Galactic Protector (Master System, FM unit)", ConsoleKind.MasterSystem, Find(@"X:\EMULATION\Sega Master System", "Galactic Protector", ".7z"), "SMS-FM", "200:Start,210:,500:Start,510:,800:A,810:"),
            ("Dr. Mario DX (Game Boy Color)", ConsoleKind.GameBoyColor, Find(@"X:\EMULATION\GameBoy Color", "Dr. Mario DX", ".gbc"), "", "300:Start,310:,600:Start,610:,900:A,910:"),
        };
        foreach (var g in games)
        {
            if (g.Path == null) { Console.WriteLine($"{g.Label}: not found"); continue; }
            var rom = ImodelGames.LoadRom(g.Path, out string name);
            if (rom == null) { Console.WriteLine($"{g.Label}: could not read"); continue; }
            var s = InjectRun.Create(g.Kind, rom, name, g.Apu.Length > 0 ? g.Apu : null);
            var pcm = new List<short>();
            InjectRun.Frames(s, 0, frames, InjectRun.Pad(g.Script), pcm);
            Console.WriteLine($"{g.Label}: {frames} frames, {pcm.Count / 2} audio frames, hash {InjectRun.Hash(pcm):X16}");
        }
        return 0;
    }
}
