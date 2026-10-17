using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BrokenNes2;
using NesEmulator.Corruption;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>The SNES half of the Instrument Runaway lab.</summary>
internal static class LabSnes
{
    public const int HostRate = 48000;

    // ------------------------------------------------------------------------------------------------- the SPC700's instruction lengths (to find which bytes of audio RAM are code)

    private static readonly string LengthRows =
        "1 1 2 3 2 3 1 2 2 3 3 2 3 1 3 1|2 1 2 3 2 3 3 2 3 1 2 2 1 1 3 3|1 1 2 3 2 3 1 2 2 3 3 2 3 1 3 2|2 1 2 3 2 3 3 2 3 1 2 2 1 1 2 3|" +
        "1 1 2 3 2 3 1 2 2 3 3 2 3 1 3 2|2 1 2 3 2 3 3 2 3 1 2 2 1 1 3 3|1 1 2 3 2 3 1 2 2 3 3 2 3 1 3 1|2 1 2 3 2 3 3 2 3 1 2 2 1 1 2 1|" +
        "1 1 2 3 2 3 1 2 2 3 3 2 3 2 1 3|2 1 2 3 2 3 3 2 3 1 2 2 1 1 1 1|1 1 2 3 2 3 1 2 2 3 3 2 3 2 1 1|2 1 2 3 2 3 3 2 3 1 2 2 1 1 1 1|" +
        "1 1 2 3 2 3 1 2 2 3 3 2 3 2 1 1|2 1 2 3 2 3 3 2 2 2 2 2 1 1 3 1|1 1 2 3 2 3 1 2 2 3 3 2 3 1 1 1|2 1 2 3 2 3 3 2 2 2 3 2 1 1 2 1";
    private static readonly byte[] OpLen = LengthRows.Split('|').SelectMany(r => r.Split(' ').Select(byte.Parse)).ToArray();

    // ------------------------------------------------------------------------------------------------- the games

    /// <summary>A game of the lab: its ROM, the pad script that takes it to where music plays, and how many frames that is.</summary>
    internal sealed record Game(string Name, byte[] Rom, string Input, int SetupFrames);

    /// <summary>The machine saved after the setup (the state every experiment starts from).</summary>
    internal static byte[] Setup(Game g)
    {
        InputScript.TryParse(g.Input, out var script, out _);
        var player = new InputScript.Player(script);
        using var run = new SnesRun(g.Rom);
        LabCommon.RunPcm(run.Session, g.SetupFrames, f => (PadButtons)player.Bits(f));
        return run.Session.SaveState();
    }

    internal static SnesRun Fresh(Game g, byte[] state)
    {
        var run = new SnesRun(g.Rom);
        run.Session.LoadState(state);
        LabCommon.Flush(run.Session);
        return run;
    }

    // ------------------------------------------------------------------------------------------------- what Instrument Runaway plays

    /// <summary>What the plugin plays for one instrument slot of a snapshot: a note at the root key, level 100 of 127, held 1.2 s and released for 0.3 s, at 48 kHz mono (left channel). Silence when the slot has no sample (the plugin plays nothing then).</summary>
    internal static float[] RenderSlot(SpcSnapshot snap, int slot)
    {
        int frames = (int)(HostRate * 1.5);
        var outp = new float[frames];
        int inst = snap.Resolve(slot);
        if (!snap.HasSample(inst)) return outp;
        var s = new SnesSampler(snap, HostRate);
        s.NoteOn(0, inst, SnesSampler.PitchFor(0), 100, 100);
        var block = new float[256 * 2];
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(256, frames - done);
            if (done >= (int)(HostRate * 1.2) && done - n < (int)(HostRate * 1.2)) s.NoteOff(0);
            s.Render(block, n);
            for (int i = 0; i < n; i++) outp[done + i] = block[i * 2];
            done += n;
        }
        return outp;
    }

    /// <summary>Instrument Runaway's renders of every instrument the clean snapshot lists, from a clean and a corrupted snapshot: how many differ at all (by hash), how many by more than the threshold, the mean distance.</summary>
    internal sealed record RunawayDiff(int Instruments, int Differ, int DifferBeyondThreshold, double MeanDistance, double MaxDistance)
    {
        public bool Carried => DifferBeyondThreshold > 0;
        public override string ToString() => $"{DifferBeyondThreshold}/{Instruments} differ (mean {LabCommon.F(MeanDistance)}, max {LabCommon.F(MaxDistance)}; {Differ} not bit-identical)";
    }

    public const double Threshold = 0.5;

    internal static RunawayDiff CompareRunaway(SpcSnapshot clean, SpcSnapshot corrupt)
    {
        var slots = clean.Instruments().Select(i => i.Slot).Take(24).ToList();
        int differ = 0, beyond = 0; double sum = 0, max = 0;
        foreach (int slot in slots)
        {
            var a = RenderSlot(clean, slot); var b = RenderSlot(corrupt, slot);
            if (LabCommon.Hash(a) != LabCommon.Hash(b)) differ++;
            double d = LabCommon.SpectralDistance(a, b, HostRate);
            if (d >= Threshold) beyond++;
            sum += d; max = Math.Max(max, d);
        }
        return new RunawayDiff(slots.Count, differ, beyond, slots.Count == 0 ? 0 : sum / slots.Count, max);
    }

    // ------------------------------------------------------------------------------------------------- classes of audio RAM

    internal sealed record Cls(string Name, string Domain, int[] Addresses, string Note);

    /// <summary>The classes the lab corrupts, from what the machine at the start of the experiment holds and what the SPC700 does over the observation window.</summary>
    internal static List<Cls> Classes(Game g, byte[] state, int window, out string description)
    {
        // what the machine holds
        using var at0 = Fresh(g, state);
        var snap = new SpcSnapshot(at0.Spc.Aram, at0.Spc.Dsp.Regs);
        var aram = (byte[])at0.Spc.Aram.Clone();
        var regs = (byte[])at0.Spc.Dsp.Regs.Clone();

        var sample = new HashSet<int>(); var dir = new HashSet<int>();
        foreach (var inst in snap.Instruments())
        {
            for (int k = 0; k < 4; k++) dir.Add((snap.DirectoryBase + inst.Slot * 4 + k) & 0xFFFF);
            for (int a = inst.Start; a < inst.Start + inst.Blocks * 9 && a < 0x10000; a++) sample.Add(a);
        }
        int esa = regs[0x6D] * 256, edl = regs[0x7D] & 15, echoLen = edl == 0 ? 4 : edl * 2048;
        var echo = new HashSet<int>();
        for (int a = esa; a < esa + echoLen && a < 0x10000; a++) echo.Add(a);

        // what the SPC700 does over the window: the bytes it executes, the instrument settings it keys on with
        using var obs = Fresh(g, state);
        var code = new HashSet<int>();
        var spc = obs.Spc;
        spc.SmpHook = pc =>
        {
            if (pc >= 0xFFC0) return;
            int len = OpLen[spc.Aram[pc]];
            for (int k = 0; k < len; k++) code.Add((pc + k) & 0xFFFF);
        };
        var tuples = new Dictionary<(byte, byte, byte, byte), int>();
        spc.DspWriteTap = (a, v, t) =>
        {
            if (a != 0x4C) return;
            for (int i = 0; i < 8; i++)
                if ((v >> i & 1) != 0) tuples[(spc.Dsp.Regs[i * 16 + 4], spc.Dsp.Regs[i * 16 + 5], spc.Dsp.Regs[i * 16 + 6], spc.Dsp.Regs[i * 16 + 7])] = 1;
        };
        LabCommon.RunPcm(obs.Session, window);

        // the instrument table: where the (SRCN, ADSR1, ADSR2, GAIN) the driver keys on with sit side by side in audio RAM
        var hits = new List<int>();
        for (int p = 0; p + 3 < 0x10000; p++)
            if (tuples.ContainsKey((aram[p], aram[p + 1], aram[p + 2], aram[p + 3])) && !sample.Contains(p) && !echo.Contains(p) && !code.Contains(p)) hits.Add(p);
        // keep the hits that sit in a run: at least three, evenly spaced by 4 to 8 bytes
        var itab = new HashSet<int>(); string itabNote = "none found";
        if (hits.Count >= 3)
        {
            for (int stride = 4; stride <= 8; stride++)
            {
                var runs = hits.Where(h => hits.Contains(h + stride) || hits.Contains(h - stride)).ToList();
                if (runs.Count >= 3)
                {
                    int lo = runs.Min(), hi = runs.Max() + stride;
                    for (int a = lo; a < hi; a++) itab.Add(a);
                    itabNote = $"${lo:X4}-${hi - 1:X4}, stride {stride}, {runs.Count} entries matched";
                    break;
                }
            }
        }

        var vars = Enumerable.Range(0, 0x200).Where(a => aram[a] != 0 && !sample.Contains(a) && !code.Contains(a)).ToArray();
        var taken = new HashSet<int>(sample); taken.UnionWith(dir); taken.UnionWith(echo); taken.UnionWith(code); taken.UnionWith(itab); taken.UnionWith(Enumerable.Range(0, 0x200));
        var other = Enumerable.Range(0, 0xFFC0).Where(a => !taken.Contains(a) && aram[a] != 0).ToArray();

        var list = new List<Cls>
        {
            new("ARAM sample data (BRR)", "ARAM", sample.OrderBy(a => a).ToArray(), "static"),
            new("ARAM sample directory", "ARAM", dir.OrderBy(a => a).ToArray(), "static"),
            new("ARAM driver code", "ARAM", code.Where(a => a < 0xFFC0 && !sample.Contains(a)).OrderBy(a => a).ToArray(), "driver"),
            new("ARAM instrument table", "ARAM", itab.OrderBy(a => a).ToArray(), "driver"),
            new("ARAM driver variables ($0000-$01FF)", "ARAM", vars, "driver"),
            new("ARAM other data (sequences, tables)", "ARAM", other, "driver"),
            new("ARAM echo buffer", "ARAM", echo.OrderBy(a => a).ToArray(), "static"),
            new("DSP voice registers", "DSP", Enumerable.Range(0, 8).SelectMany(v => Enumerable.Range(0, 8).Select(r => v * 16 + r)).ToArray(), "dsp"),
            new("DSP global registers", "DSP", new[] { 0x0C, 0x1C, 0x2C, 0x3C, 0x0D, 0x2D, 0x3D, 0x4D, 0x5D, 0x6D, 0x7D, 0x6C, 0x0F, 0x1F, 0x2F, 0x3F, 0x4F, 0x5F, 0x6F, 0x7F }, "dsp"),
            new("WRAM (main CPU RAM, feeds the driver)", "WRAM", Enumerable.Range(0, 0x20000).ToArray(), "game"),
        };
        description = $"samples {sample.Count} B in {snap.Instruments().Count} slots, directory {dir.Count} B at ${snap.DirectoryBase:X4}, echo {echo.Count} B at ${esa:X4}, code {code.Count} B, instrument table [{itabNote}], variables {vars.Length} B, other live data {other.Length} B";
        return list;
    }

    // ------------------------------------------------------------------------------------------------- the matrix

    private static List<CorruptionWrite> MakeWrites(Cls c, int count, long seed)
    {
        var rnd = new SeededRandom(seed);
        var pool = c.Addresses.ToArray();
        // a partial shuffle: the first `count` distinct addresses
        count = Math.Min(count, pool.Length);
        for (int i = 0; i < count; i++) { int j = i + rnd.Next(pool.Length - i); (pool[i], pool[j]) = (pool[j], pool[i]); }
        var w = new List<CorruptionWrite>();
        for (int i = 0; i < count; i++) w.Add(new CorruptionWrite(c.Domain, pool[i], (byte)rnd.Next(256)));
        return w;
    }

    private static int CountFor(Cls c) => c.Domain switch
    {
        "WRAM" => 600,
        "DSP" => c.Addresses.Length <= 20 ? 6 : 12,
        _ => Math.Clamp(c.Addresses.Length / 50, 8, 400),       // about 2 percent of the class (at least 8, at most 400)
    };

    internal sealed record Row(string Class, string Kind, int Writes, double GameDistance, bool GameIdentical, double GameRmsRatio, RunawayDiff Once, RunawayDiff Late);

    /// <summary>One second of a recipe played at its own pitch and volume (32 kHz, left channel): the sound it stands for.</summary>
    internal static float[] RecipeSound(SpcSnapshot snap, SpcRecipe r) => RunawayChecks.RenderRecipe(snap, r, 32000);

    /// <summary>How far the recipes of a corrupted run are from what the clean game had: how many have no bit-identical twin among the clean ones, and the mean over the corrupted recipes of the distance to the nearest clean recipe's sound.</summary>
    internal static string RecipeNovelty(SpcSnapshot clean, List<float[]> cleanSounds, SpcSnapshot bad)
    {
        if (bad.Recipes.Length == 0) return $"0 recipes (the game keyed nothing; clean had {clean.Recipes.Length})";
        var hashes = new HashSet<ulong>(clean.Recipes.Select(r => r.Hash()));
        int novel = 0; double sum = 0;
        foreach (var r in bad.Recipes)
        {
            if (!hashes.Contains(r.Hash())) novel++;
            var snd = RecipeSound(bad, r);
            double best = cleanSounds.Count == 0 ? 20 : cleanSounds.Min(c => LabCommon.SpectralDistance(c, snd, 32000));
            sum += best;
        }
        return $"{bad.Recipes.Length} recipes ({clean.Recipes.Length} clean), {novel} unlike any clean one, mean distance to nearest clean {LabCommon.F(sum / bad.Recipes.Length)}";
    }

    /// <summary>--runaway-lab snes-matrix --rom X [--name N] [--input script] [--setup N] [--window N] [--seeds N] [--md file]: the corruption classes against the game's own sound and Instrument Runaway, frozen at once and after the window.</summary>
    public static int Matrix(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        var rom = LabCommon.ReadRom(romPath, out _, out _);
        var game = new Game(Opt("name", Path.GetFileNameWithoutExtension(romPath)), rom, Opt("input", ""), int.Parse(Opt("setup", "1200")));
        int window = int.Parse(Opt("window", "600")), seeds = int.Parse(Opt("seeds", "3"));
        Console.WriteLine($"== SNES: {game.Name}  setup {game.SetupFrames} frames, window {window} frames ({window / 60.0:0.#} s), {seeds} seed(s), threshold {Threshold}");

        var state = Setup(game);
        var classes = Classes(game, state, window, out string desc);
        Console.WriteLine("machine: " + desc);

        // the clean game over the window, and what Instrument Runaway captures from it at the start and at the end
        using var clean = Fresh(game, state);
        var cleanOnce = new SpcSnapshot(clean.Spc.Aram, clean.Spc.Dsp.Regs);
        var cleanLog = new SpcWriteLog(); cleanLog.Attach(clean.Spc);
        var cleanPcm = LabCommon.RunPcm(clean.Session, window);
        var cleanLate = new SpcSnapshot(clean.Spc.Aram, clean.Spc.Dsp.Regs);
        cleanLate.Recipes = SpcRecipes.Derive(cleanLog, clean.Spc.SamplesProduced, window * 534L, clean.Spc.Dsp.Regs, out var cleanStats);       // a frame is 533.7 DSP samples
        var cleanRecipeSounds = cleanLate.Recipes.Select(r => RecipeSound(cleanLate, r)).ToList();
        Console.WriteLine($"clean recipes: {cleanStats}");
        var cleanMono = LabCommon.ToMono(cleanPcm);
        Console.WriteLine($"clean game over the window: peak {LabCommon.Peak(cleanMono):0.000}, rms {LabCommon.Rms(cleanMono):0.0000}, {cleanOnce.Instruments().Count} instruments at the start, {cleanLate.Instruments().Count} at the end");

        // the scale of the distance: two different clean instruments against each other
        var inst = cleanOnce.Instruments().Select(i => i.Slot).Take(8).ToList();
        double scale = 0; int pairs = 0;
        for (int i = 0; i < inst.Count; i++) for (int j = i + 1; j < inst.Count; j++) { scale += LabCommon.SpectralDistance(RenderSlot(cleanOnce, inst[i]), RenderSlot(cleanOnce, inst[j]), HostRate); pairs++; }
        Console.WriteLine($"scale: two different clean instruments of this game are {LabCommon.F(pairs == 0 ? 0 : scale / pairs)} apart on average; a note against silence is about 10 to 20");

        var rows = new List<Row>();
        foreach (var c in classes)
        {
            if (c.Addresses.Length == 0) { Console.WriteLine($"-- {c.Name}: empty class on this game, skipped"); continue; }
            double gd = 0, gr = 0; int ident = 0, n = 0, once = 0, late = 0;
            var recipeNotes = new List<string>();
            var onceD = new List<RunawayDiff>(); var lateD = new List<RunawayDiff>();
            int count = CountFor(c);
            for (int seed = 1; seed <= seeds; seed++)
            {
                var writes = MakeWrites(c, count, seed * 7919L + c.Name.Length);
                using var bad = Fresh(game, state);
                var rep = CorruptionApplier.Apply(MachineDomains.For(bad.Session), writes);
                var badOnce = new SpcSnapshot(bad.Spc.Aram, bad.Spc.Dsp.Regs);
                var badLog = new SpcWriteLog(); badLog.Attach(bad.Spc);
                var badPcm = LabCommon.RunPcm(bad.Session, window);
                var badLate = new SpcSnapshot(bad.Spc.Aram, bad.Spc.Dsp.Regs);
                badLate.Recipes = SpcRecipes.Derive(badLog, bad.Spc.SamplesProduced, window * 534L, bad.Spc.Dsp.Regs, out _);
                recipeNotes.Add(RecipeNovelty(cleanLate, cleanRecipeSounds, badLate));
                var badMono = LabCommon.ToMono(badPcm);
                double d = LabCommon.SpectralDistance(cleanMono, badMono, 32000);
                gd += d; gr += LabCommon.Rms(badMono) / Math.Max(1e-9, LabCommon.Rms(cleanMono));
                if (LabCommon.Hash(cleanPcm) == LabCommon.Hash(badPcm)) ident++;
                n++;
                onceD.Add(CompareRunaway(cleanOnce, badOnce));
                lateD.Add(CompareRunaway(cleanLate, badLate));
            }
            RunawayDiff Mean(List<RunawayDiff> l) => new((int)l.Average(x => x.Instruments), (int)Math.Round(l.Average(x => x.Differ)), (int)Math.Round(l.Average(x => x.DifferBeyondThreshold)), l.Average(x => x.MeanDistance), l.Max(x => x.MaxDistance));
            var row = new Row(c.Name, c.Note, count, gd / n, ident == n, gr / n, Mean(onceD), Mean(lateD));
            rows.Add(row);
            Console.WriteLine($"{c.Name,-42} writes {count,4}  game: dist {LabCommon.F(row.GameDistance),6}{(row.GameIdentical ? " (identical)" : "")} level x{LabCommon.F(row.GameRmsRatio)}   Runaway at once: {row.Once}   after {window / 60} s: {row.Late}");
            Console.WriteLine($"{"",-42} new capture (recipes, {window / 60} s): {string.Join("  |  ", recipeNotes)}");
        }

        // the heavily corrupted state: static writes alone, driver writes alone, both
        Console.WriteLine();
        Console.WriteLine("-- heavily corrupted state: how much of the change in the game's sound is static instrument data and how much is the driver");
        var heavy = new List<CorruptionWrite>();
        var staticSet = new HashSet<int>(classes.First(c => c.Name.StartsWith("ARAM sample data")).Addresses.Concat(classes.First(c => c.Name.StartsWith("ARAM sample directory")).Addresses));
        var rnd = new SeededRandom(424242);
        for (int i = 0; i < 4000; i++) heavy.Add(new CorruptionWrite("ARAM", rnd.Next(0xFFC0), (byte)rnd.Next(256)));
        for (int i = 0; i < 600; i++) heavy.Add(new CorruptionWrite("WRAM", rnd.Next(0x20000), (byte)rnd.Next(256)));
        for (int i = 0; i < 24; i++) heavy.Add(new CorruptionWrite("DSP", rnd.Next(128), (byte)rnd.Next(256)));
        var staticOnly = heavy.Where(w => w.Domain == "ARAM" && staticSet.Contains(w.Address)).ToList();
        var driverOnly = heavy.Where(w => !(w.Domain == "ARAM" && staticSet.Contains(w.Address))).ToList();
        foreach (var (label, writes) in new[] { ("everything", heavy), ("static instrument data only (BRR + directory)", staticOnly), ("driver / registers / game RAM only", driverOnly) })
        {
            using var bad = Fresh(game, state);
            CorruptionApplier.Apply(MachineDomains.For(bad.Session), writes);
            var heavyLog = new SpcWriteLog(); heavyLog.Attach(bad.Spc);
            var badPcm = LabCommon.RunPcm(bad.Session, window);
            var badMono = LabCommon.ToMono(badPcm);
            var badLate = new SpcSnapshot(bad.Spc.Aram, bad.Spc.Dsp.Regs);
            badLate.Recipes = SpcRecipes.Derive(heavyLog, bad.Spc.SamplesProduced, window * 534L, bad.Spc.Dsp.Regs, out _);
            Console.WriteLine($"{label,-48} {writes.Count,5} writes: game dist {LabCommon.F(LabCommon.SpectralDistance(cleanMono, badMono, 32000)),6}, level x{LabCommon.F(LabCommon.Rms(badMono) / Math.Max(1e-9, LabCommon.Rms(cleanMono)))}, Runaway after {window / 60} s: {CompareRunaway(cleanLate, badLate)}");
            Console.WriteLine($"{"",-48} new capture: {RecipeNovelty(cleanLate, cleanRecipeSounds, badLate)}");
        }
        return 0;
    }

    // ------------------------------------------------------------------------------------------------- what a macro can carry

    internal enum Resynth { Macro, Exemplar, Plain }

    /// <summary>
    /// The game's own notes played again on a private S-DSP at their original times and voices, to see how much of what the driver did a recipe model holds (an upper bound on what FL could play, since FL supplies its own notes and the game's sequencer is not run).
    /// Macro: every note with its own recorded macro. Exemplar: every note with the settings it was keyed on with but the macro of the recipe it belongs to (what grouping notes into recipes keeps). Plain: the instrument alone, the way Instrument Runaway played
    /// before (the sample at the note's pitch with the envelope the game had on a playing voice, no macro).
    /// </summary>
    internal static float[] Resynthesize(SpcSnapshot snap, List<SpcRecipes.SpcNote> notes, SpcRecipe[] recipes, long from, long to, Resynth mode)
    {
        int n = (int)(to - from);
        var outp = new float[n];
        var s = new SnesSampler(snap, 32000);
        var one = new float[2];
        var order = notes.OrderBy(x => x.Start).ToList();
        int next = 0;
        var endAt = new SortedDictionary<long, List<int>>();
        for (int i = 0; i < n; i++)
        {
            long t = from + i;
            while (next < order.Count && order[next].Start <= t)
            {
                var note = order[next++];
                var r = note.Recipe;
                switch (mode)
                {
                    case Resynth.Macro:
                        s.NoteOnRecipe(note.Voice, r, r.Pitch, r.PeakVolume);
                        break;
                    case Resynth.Exemplar:
                    {
                        var ex = recipes.FirstOrDefault(x => x.Srcn == r.Srcn && x.Adsr1 == r.Adsr1 && x.Adsr2 == r.Adsr2 && x.Gain == r.Gain && x.Non == r.Non && x.Eon == r.Eon && x.Pmon == r.Pmon) ?? r;
                        var mixed = new SpcRecipe { Srcn = r.Srcn, Adsr1 = r.Adsr1, Adsr2 = r.Adsr2, Gain = r.Gain, VolL = ex.VolL, VolR = ex.VolR, Pitch = r.Pitch, Non = r.Non, Eon = r.Eon, Pmon = r.Pmon, NoiseRate = r.NoiseRate, Events = ex.Events, Uses = 1 };
                        s.NoteOnRecipe(note.Voice, mixed, r.Pitch, r.PeakVolume);          // the volumes are the exemplar's scaled to this note's loudest
                        break;
                    }
                    default:
                    {
                        int level = Math.Min(127, r.PeakVolume);
                        s.NoteOn(note.Voice, r.Srcn, r.Pitch, level, level);
                        break;
                    }
                }
                if (note.KeyedOff) { if (!endAt.TryGetValue(note.End, out var l)) endAt[note.End] = l = new(); l.Add(note.Voice); }
            }
            while (endAt.Count > 0 && endAt.Keys.First() <= t) { var k = endAt.Keys.First(); foreach (int v in endAt[k]) s.NoteOff(v); endAt.Remove(k); }
            s.Render(one, 1);
            outp[i] = (one[0] + one[1]) * 0.5f;
        }
        return outp;
    }

    /// <summary>--runaway-lab snes-resynth --rom X [--name N] [--setup N] [--window N] [--seeds N] [--input script]: for a clean game and some heavily corrupted ones, the game's own sound against its notes played again from the recorded macros.</summary>
    public static int ResynthLab(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        var rom = LabCommon.ReadRom(romPath, out _, out _);
        var game = new Game(Opt("name", Path.GetFileNameWithoutExtension(romPath)), rom, Opt("input", ""), int.Parse(Opt("setup", "1200")));
        int window = int.Parse(Opt("window", "600")), seeds = int.Parse(Opt("seeds", "6")), nw = int.Parse(Opt("writes", "300"));
        var state = Setup(game);
        Console.WriteLine($"== SNES resynthesis: {game.Name}, window {window} frames, corruptions of {nw} random audio-RAM writes and {nw / 50} DSP-register writes");
        Console.WriteLine("distance of the game's own sound to: [macro] each note with its own recorded macro, [exemplar] each note with its recipe's macro, [plain] the instrument alone (before recipes), [silence]");
        for (int seed = 0; seed <= seeds; seed++)
        {
            using var run = Fresh(game, state);
            string label = "clean game";
            if (seed > 0)
            {
                var rnd = new SeededRandom(1000 + seed);
                var writes = new List<CorruptionWrite>();
                for (int i = 0; i < nw; i++) writes.Add(new CorruptionWrite("ARAM", rnd.Next(0xFFC0), (byte)rnd.Next(256)));
                for (int i = 0; i < nw / 50; i++) writes.Add(new CorruptionWrite("DSP", rnd.Next(128), (byte)rnd.Next(256)));
                CorruptionApplier.Apply(MachineDomains.For(run.Session), writes);
                label = $"corruption, seed {seed}";
            }
            var log = new SpcWriteLog(); log.Attach(run.Spc);
            long t0 = run.Spc.SamplesProduced;
            var pcm = LabCommon.RunPcm(run.Session, window);
            var game32 = LabCommon.ToMono(pcm);
            long t1 = run.Spc.SamplesProduced;
            var snap = new SpcSnapshot(run.Spc.Aram, run.Spc.Dsp.Regs);
            var notes = SpcRecipes.Notes(log, t1, t1 - t0, run.Spc.Dsp.Regs, out var stats, out _);
            var recipes = SpcRecipes.Derive(log, t1, t1 - t0, run.Spc.Dsp.Regs, out _);
            snap.Recipes = recipes;
            double hf = HighFraction(game32);
            var sil = new float[game32.Length];
            string line = $"{label,-28} game rms {LabCommon.Rms(game32):0.0000} (HF {hf:0.00}), {stats.KeyOns} key-ons, {recipes.Length} recipes:";
            if (notes.Count == 0)
            {
                var rg = run.Spc.Dsp.Regs;
                string envx = string.Join(" ", Enumerable.Range(0, 8).Select(v => $"{rg[v * 16 + 8]:X2}"));
                Console.WriteLine(line + $" no notes at all. At the end: ENVX {envx}, KON {rg[0x4C]:X2} KOFF {rg[0x5C]:X2} EON {rg[0x4D]:X2} NON {rg[0x3D]:X2} PMON {rg[0x2D]:X2} FLG {rg[0x6C]:X2} EFB {rg[0x0D]:X2} EVOL {rg[0x2C]:X2}/{rg[0x3C]:X2} MVOL {rg[0x0C]:X2}/{rg[0x1C]:X2} EDL {rg[0x7D]:X2} ESA {rg[0x6D]:X2}; SPC halted {run.Spc.Smp.Halted}, PC ${run.Spc.Smp.PC:X4}");
                continue;
            }
            int skip = game32.Length / 5;            // the notes that were already sounding when the window began are not in it: judge the last four fifths
            line += $" ({notes.Count(n => n.Recipe.Held)} held)";
            foreach (var mode in new[] { Resynth.Macro, Resynth.Exemplar, Resynth.Plain })
            {
                var re = Resynthesize(snap, notes, recipes, t0, t1, mode);
                line += $"  [{mode.ToString().ToLowerInvariant()}] {LabCommon.F(LabCommon.SpectralDistance(game32[skip..], re[skip..], 32000))}";
            }
            foreach (int rule in new[] { 1, 2, 3 })
            {
                var alt = GroupBy(notes, rule);
                var re = Resynthesize(snap, notes, alt, t0, t1, Resynth.Exemplar);
                line += $"  [exemplar rule {rule}] {LabCommon.F(LabCommon.SpectralDistance(game32[skip..], re[skip..], 32000))}";
            }
            line += $"  [silence] {LabCommon.F(LabCommon.SpectralDistance(game32[skip..], sil[skip..], 32000))}";
            Console.WriteLine(line);
        }
        return 0;
    }

    /// <summary>Recipes by another rule for the exemplar than the one Derive uses: 1 the note with the median number of events, 2 the note of median length, 3 the shortest note that has any event (else the shortest).</summary>
    internal static SpcRecipe[] GroupBy(List<SpcRecipes.SpcNote> notes, int rule)
    {
        var list = new List<SpcRecipe>();
        foreach (var g in notes.GroupBy(n => (n.Recipe.Srcn, n.Recipe.Adsr1, n.Recipe.Adsr2, n.Recipe.Gain, n.Recipe.Non, n.Recipe.Eon, n.Recipe.Pmon, n.Recipe.Held)))
        {
            var ordered = (rule == 2 ? g.OrderBy(n => n.Recipe.LengthSamples) : g.OrderBy(n => n.Recipe.Events.Length).ThenBy(n => n.Recipe.LengthSamples)).ToList();
            var pick = rule switch
            {
                3 => ordered.FirstOrDefault(n => n.Recipe.Events.Length > 0) ?? ordered[0],
                _ => ordered[ordered.Count / 2],
            };
            pick.Recipe.Uses = ordered.Count;
            list.Add(pick.Recipe);
        }
        return list.ToArray();
    }

    /// <summary>The share of the sound's energy above 4 kHz (a rough measure of how screechy or hissy it is).</summary>
    internal static double HighFraction(float[] mono32)
    {
        var bands = LabCommon.AverageBands(mono32, 32000);
        double total = bands.Sum();
        if (total <= 0) return 0;
        // bands are log-spaced from 60 Hz to 16 kHz in 20 steps: the band that starts at 4 kHz is index 14 (60 * (16000/60)^(14/20) = 3.1 kHz) .. use 15 and up (about 3.9 kHz and above)
        return bands.Skip(15).Sum() / total;
    }

    // ------------------------------------------------------------------------------------------------- the probe

    /// <summary>--runaway-lab snes-probe --rom X [--frames N] [--input script]: what a SNES game's sound driver does (per second: peak, key-ons, DSP writes) and which samples it plays.</summary>
    public static int Probe(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        int frames = int.Parse(Opt("frames", "1800"));
        InputScript.TryParse(Opt("input", ""), out var script, out _);
        var rom = LabCommon.ReadRom(romPath, out var kind, out _);
        using var run = new SnesRun(rom);
        var spc = run.Spc;
        long writes = 0, kons = 0;
        var srcnUse = new Dictionary<int, int>();
        spc.DspWriteTap = (a, v, t) =>
        {
            writes++;
            if (a == 0x4C && v != 0)
                for (int i = 0; i < 8; i++) if ((v >> i & 1) != 0) { kons++; int sr = spc.Dsp.Regs[i * 16 + 4]; srcnUse[sr] = srcnUse.GetValueOrDefault(sr) + 1; }
        };
        var player = new InputScript.Player(script);
        Console.WriteLine($"game {Path.GetFileName(romPath)}  {frames} frames");
        for (int sec = 0; sec * 60 < frames; sec++)
        {
            var pcm = LabCommon.RunPcm(run.Session, Math.Min(60, frames - sec * 60), f => (PadButtons)player.Bits(f), sec * 60);
            var mono = LabCommon.ToMono(pcm);
            Console.WriteLine($"  s{sec,3}: peak {LabCommon.Peak(mono):0.000} rms {LabCommon.Rms(mono):0.0000}  key-ons so far {kons,5}  dsp writes so far {writes,7}  voices keyed now {Voices(spc)}");
        }
        var snap = new SpcSnapshot(spc.Aram, spc.Dsp.Regs);
        Console.WriteLine($"instruments in the directory: {snap.Instruments().Count}   SRCN used by key-ons: {string.Join(", ", srcnUse.OrderByDescending(k => k.Value).Take(16).Select(k => $"#{k.Key}x{k.Value}"))}");
        Console.WriteLine($"DIR ${snap.DirectoryBase:X4}  ESA ${spc.Dsp.Regs[0x6D] * 256:X4}  EDL {spc.Dsp.Regs[0x7D] & 15}  FLG ${spc.Dsp.Regs[0x6C]:X2}  EON ${spc.Dsp.Regs[0x4D]:X2} PMON ${spc.Dsp.Regs[0x2D]:X2} NON ${spc.Dsp.Regs[0x3D]:X2}");
        return 0;
    }

    private static string Voices(APU_SFC spc)
    {
        var s = "";
        for (int v = 0; v < 8; v++) s += spc.Dsp.Regs[v * 16 + 8] != 0 ? "X" : ".";
        return s;
    }
}
