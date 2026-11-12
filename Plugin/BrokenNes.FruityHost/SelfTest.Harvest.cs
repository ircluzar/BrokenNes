// The harvested sound engines as plugin chips (2026-11-10, "ported cores in brokennes vst"): every engine the Foreign APU / APU harvest climbs ported (Bn2Consoles.Harvest.cs) is a chip under its console,
// appended after every earlier chip. Checks: the earlier 31 chips keep their index, the harvested ones follow in their fixed order, picking one moves to its console, a project saved on one comes back on it,
// and each one renders a note that is not silent and is the same sample for sample twice (deterministic). `chip-compat save|open` proves an older DLL's projects load and play the same.
using System.Security.Cryptography;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    /// <summary>The chips before the harvest, as they were (index = position). Never changes.</summary>
    internal static readonly string[] PinnedChips =
        ["FIX", "FIXS", "EIL", "FMC", "HI", "HI2", "HI2X", "LOW", "LQ", "LQ2", "QLOW", "QLQ", "QLQ2", "QN", "SPD", "SPD2", "ULQ", "DMG", "DMGS", "SNES", "SMS", "MD", "SMSFM",
         "PRESETS", "PRE-NES", "PRE-GB", "PRE-SNES", "PRE-SMS", "PRE-MD", "WMS", "NANOGB"];

    /// <summary>(id, console) in the order Bn2Consoles.Harvest.cs appends them.</summary>
    internal static readonly (string Id, int Console)[] HarvestChips =
    [
        ("DARC", 0), ("FCEU", 0), ("INES", 0), ("NSTC", 0), ("TUXN", 0), ("VNES", 0), ("PNES", 0), ("NSDS", 0), ("NOFR", 0), ("PCEG", 0), ("POKY", 0), ("AY124", 0), ("RSID16", 0), ("FSID", 0),
        ("DMG_GNUB", 1), ("DMG_VBA", 1), ("DMG_VBAM", 1), ("DMG_TGBD", 1), ("DMG_GYOB", 1), ("DMG_MGB", 1),
        ("SNES_ZS15", 2), ("SNES_ZS151", 2), ("SNES_S916", 2), ("SNES_S9X1", 2), ("SNES_S951", 2), ("SNES_OSPC", 2), ("SNES_SNDS", 2), ("SNES_PSPC3", 2),
        ("SMS_BLST", 3), ("SMS_M036", 3), ("SMS_GPGX", 3), ("SMS_GWEN", 3), ("SMS_GENS", 3), ("SMS_PICO", 3), ("SMS_DARC", 3), ("SMS_DEGA", 3), ("SMS_SMSA", 3), ("SMSFM_M036FM", 3), ("SMSFM_E055", 3),
        ("MD_GENS", 4), ("MD_PICO", 4), ("MD_M036", 4), ("MD_BLST", 4), ("MD_GPGX", 4), ("MD_GWEN", 4),
        ("NSTRJ", 0),
    ];

    private void Harvest(T t)
    {
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        for (int i = 0; i < PinnedChips.Length; i++)
            t.Expect(i < chips.Count && chips[i].Equals(PinnedChips[i], StringComparison.OrdinalIgnoreCase), $"chip {i} is {(i < chips.Count ? chips[i] : "missing")}, expected {PinnedChips[i]} (an earlier index moved)");
        // the harvested chips: after the pinned ones (another track may append its own chips in between), contiguous, in their order
        int first = chips.FindIndex(c => c.Equals(HarvestChips[0].Id, StringComparison.OrdinalIgnoreCase));
        t.Expect(first >= PinnedChips.Length, $"the first harvested chip {HarvestChips[0].Id} is index {first} (expected after the {PinnedChips.Length} pinned chips)");
        if (first < 0) return;
        for (int k = 0; k < HarvestChips.Length; k++)
            t.Expect(first + k < chips.Count && chips[first + k].Equals(HarvestChips[k].Id, StringComparison.OrdinalIgnoreCase), $"harvest chip {k} is {(first + k < chips.Count ? chips[first + k] : "missing")}, expected {HarvestChips[k].Id}");

        var rows = new List<string>(); int silent = 0, differ = 0;
        for (int k = 0; k < HarvestChips.Length; k++)
        {
            var (id, console) = HarvestChips[k];
            int index = first + k;
            // picking it puts the instance on its console; a saved project comes back on it
            byte[] blob;
            using (var a = lib.Create())
            {
                a.SetParam(PCore, index);
                t.Expect(a.GetParam(PConsole) == console, $"{id}: picking it left the console at {a.GetParam(PConsole)}, expected {console}");
                blob = a.SaveState();
            }
            using (var b = lib.Create())
            {
                b.LoadState(blob);
                t.Expect(b.GetParam(PCore) == index && b.GetName(Fpn.ParamValue, PCore, b.GetParam(PCore)) == id && b.GetParam(PConsole) == console, $"{id}: a project saved on it came back as chip {b.GetParam(PCore)} on console {b.GetParam(PConsole)}");
            }
            // a note: pulse 1 at A4 (the noise channel on the noise-only consoles is not needed: every chip carries pulse 1)
            double rms = 0; string? h1 = null, h2 = null;
            for (int run = 0; run < 2; run++)
            {
                var (audio, mixer, plugin) = Play(One(69, 0, 0.6), setup: p => p.SetParam(PCore, index));
                using (plugin)
                {
                    var mono = Mono(audio);
                    if (run == 0) rms = Audio.Rms(mono, mixer.Log[0].StartSample + 2000, mixer.Log[0].EndSample - 500);
                    string h = HashAudio(audio);
                    if (run == 0) h1 = h; else h2 = h;
                }
            }
            bool loud = rms > 1e-3 && !double.IsNaN(rms);
            if (!loud) silent++;
            if (h1 != h2) differ++;
            t.Expect(loud, $"{id} (chip {index}): pulse 1 at A4 is silent (rms {rms:0.00000})");
            t.Expect(h1 == h2, $"{id} (chip {index}): two renders of the same note differ ({h1} vs {h2})");
            rows.Add($"{index} {id} {rms:0.000}");
        }
        t.Note($"{HarvestChips.Length} harvested chips from index {first}: {silent} silent, {differ} not deterministic; rms at A4: {string.Join(", ", rows)}");
    }

    private static string HashAudio(float[] audio)
    {
        var bytes = new byte[audio.Length * 4];
        Buffer.BlockCopy(audio, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    /// <summary>
    /// `chip-compat save <dll> <dir>`: for every chip of the DLL, a project (instance state) saved on it with a few non-default parameters, and the hash of a note rendered by an instance that loaded it.
    /// `chip-compat open <dll> <dir>`: loads each saved project in another DLL, checks the chip name and parameters, renders the same note and compares the hash. Exit 0 when all equal.
    /// </summary>
    public static int ChipCompat(string verb, string dll, string dir)
    {
        var lib = new PluginLibrary(dll);
        Directory.CreateDirectory(dir);
        int bad = 0, n = 0;
        string Render(PluginInstance p)
        {
            var mixer = new Mixer { SampleRate = 44100, BlockSize = 256 };
            return HashAudio(mixer.Run(p, One(64, 0, 0.5)));
        }
        if (verb == "save")
        {
            int count; using (var probe = lib.Create()) count = Chips(probe).Count;
            for (int i = 0; i < count; i++)
            {
                byte[] blob; string name;
                using (var a = lib.Create()) { a.SetParam(PCore, i); a.SetParam(PDuty, 2); a.SetParam(PVolume, 700); name = a.GetName(Fpn.ParamValue, PCore, i); blob = a.SaveState(); }
                string hash; using (var b = lib.Create()) { b.LoadState(blob); hash = Render(b); }
                File.WriteAllBytes(Path.Combine(dir, $"{i:00}.state"), blob);
                File.WriteAllText(Path.Combine(dir, $"{i:00}.txt"), $"{name}\n{hash}\n");
                Console.WriteLine($"{i,3} {name,-10} {hash}");
                n++;
            }
            return 0;
        }
        foreach (var f in Directory.GetFiles(dir, "*.state").OrderBy(x => x))
        {
            var lines = File.ReadAllLines(Path.ChangeExtension(f, ".txt"));
            using var b = lib.Create();
            b.LoadState(File.ReadAllBytes(f));
            string name = b.GetName(Fpn.ParamValue, PCore, b.GetParam(PCore)), hash = Render(b);
            bool ok = name == lines[0] && hash == lines[1] && b.GetParam(PDuty) == 2 && b.GetParam(PVolume) == 700;
            if (!ok) bad++;
            n++;
            Console.WriteLine($"{(ok ? "SAME" : "DIFF")} {Path.GetFileNameWithoutExtension(f)} {lines[0],-10} -> {name,-10} {lines[1]} -> {hash}");
        }
        Console.WriteLine($"{n} projects, {bad} differ");
        return bad == 0 ? 0 : 1;
    }
}
