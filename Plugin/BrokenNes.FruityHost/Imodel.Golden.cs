// Round 6 (imodel): the golden hashes of what the existing samplers play for made-up snapshots. They were recorded BEFORE the sampler / snapshot change that lets an instrument edit be heard (the SNES overlay),
// from the unmodified SnesSampler, SpcSnapshot, GbSampler, PsgSampler, OpllSampler and OpnSampler, and the check compares the changed code against them: a clean snapshot must play bit for bit as it always did.
using BrokenNes2;

namespace BrokenNes.FruityHost;

internal static class ImodelGolden
{
    public static List<(string Name, string Hash, double Rms)> Compute()
    {
        var list = new List<(string, string, double)>();
        void Add(string name, float[] pcm) => list.Add((name, ImodelData.Hash(pcm), ImodelData.Rms(pcm)));
        Add("snes-testsample", ImodelData.RenderSnes(SpcSnapshot.TestSample(), [0, 0, 0]));
        Add("snes-samples-012", ImodelData.RenderSnes(ImodelData.SnesWithSamples(), [0, 1, 2]));
        Add("snes-samples-210", ImodelData.RenderSnes(ImodelData.SnesWithSamples(), [2, 1, 0]));
        Add("gb-dmg", ImodelData.RenderGb(ImodelData.GbWithSettings(false)));
        Add("gb-cgb", ImodelData.RenderGb(ImodelData.GbWithSettings(true)));
        Add("psg", ImodelData.RenderSega(ImodelData.PsgOnly(), (0, 0, 300, 0.8), (1, 0, 200, 0.5), (3, 1, 150, 0.6)));
        var opll = ImodelData.OpllWithUser();
        Add("opll", ImodelData.RenderSega(opll, (0, 0, 220, 0.9), (1, 3, 330, 0.7), (2, 8, 262, 0.8), (9, 16, 440, 0.5)));
        var opn = ImodelData.OpnWithPatches();
        int psg = opn.PsgInstrument;
        Add("opn", ImodelData.RenderSega(opn, (0, 0, 220, 0.9), (1, 1, 330, 0.8), (2, 2, 165, 0.7), (6, 6, 220, 0.8), (7, 7, 262, 0.9), (14, psg, 300, 0.6), (17, psg + 1, 150, 0.6)));
        return list;
    }

    /// <summary>The real games the checks use, and the frame each is captured at (long enough for the sound driver to have loaded its samples and started a tune).</summary>
    public static List<(string Name, Func<(object? Snapshot, string Info)> Capture)> RealGames() =>
    [
        ("smw", () => { var s = ImodelGames.Snes(ImodelGames.Smw, 1800, out var i); return (s, i); }),
        ("sonic1", () => { var s = ImodelGames.Genesis(ImodelGames.Sonic1, 900, out var i); return (s, i); }),
        ("zelda-gb", () => { var p = ImodelGames.Find(@"X:\EMULATION\GameBoy", "Link's Awakening", ".zip"); var s = p == null ? null : ImodelGames.Gb(p, 1800, out _); return (s, p ?? "ROM not found"); }),
        ("addams-gg", () => { var p = ImodelGames.Find(@"X:\EMULATION\Sega Game Gear", "Addams Family", ".gg"); var s = p == null ? null : ImodelGames.MasterSystem(p, 1500, false, out _); return (s, p ?? "ROM not found"); }),
    ];

    /// <summary>Lists what each real game's snapshot holds and the hash of a render of it (so the same render can be compared after a sampler change).</summary>
    public static IEnumerable<string> Real()
    {
        foreach (var (name, capture) in RealGames())
        {
            var (snap, info) = capture();
            if (snap == null) { yield return $"{name,-10} not available: {info}"; continue; }
            switch (snap)
            {
                case SpcSnapshot s:
                {
                    var list = s.Instruments();
                    var slots = list.Count == 0 ? [] : new[] { list[0].Slot, list[list.Count / 2].Slot, list[^1].Slot };
                    string h = slots.Length == 0 ? "-" : ImodelData.Hash(ImodelData.RenderSnes(s, slots));
                    yield return $"{name,-10} snes: {list.Count} samples (slots {string.Join(",", list.Select(i => i.Slot).Take(40))}), render of {string.Join(",", slots)}: {h}";
                    break;
                }
                case SegaSnapshot g:
                {
                    int fm = Enumerable.Range(0, 6).Count(g.OpnPatchUsed);
                    string h = ImodelData.Hash(ImodelData.RenderSega(g, g.Kind == SegaSnapshot.KindOpn ? new[] { (0, g.Resolve(0), 261.6, 0.8), (1, Math.Min(5, g.Resolve(0) + 1), 329.6, 0.8), (g.Dac.Length > 0 ? 6 : 2, g.Dac.Length > 0 ? 6 : 2, 261.6, 0.8) } : new[] { (0, g.Resolve(0), 261.6, 0.8) }));
                    yield return $"{name,-10} sega kind {g.Kind}: {g.InstrumentCount} instruments, {fm} FM patches set up, {g.Dac.Length} DAC sounds, render: {h}";
                    break;
                }
                case GbSnapshot b:
                    yield return $"{name,-10} gb: regs {Convert.ToHexString(b.Regs)} wave {Convert.ToHexString(b.Wave)} render: {ImodelData.Hash(ImodelData.RenderGb(b))}";
                    break;
            }
        }
    }
}
