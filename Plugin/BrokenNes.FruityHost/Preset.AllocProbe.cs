// Diagnostic for the `play-alloc` check of the presets (round 6): `FruityHost alloc-probe [gb|snes|fm|dac] [blocks]` builds the same engine the check builds, plays the same note and prints
// what this thread allocated at every step (the engine's construction, the note, each warm-up block, each block of the window), so an allocation that happens "once in a while" can be
// located: in which block, how many bytes, and in a fresh process or a warm one.
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

internal static partial class PresetChecks
{
    public static int AllocProbe(string[] args)
    {
        string which = args.Length > 1 ? args[1] : "gb";
        int blocks = args.Length > 2 && int.TryParse(args[2], out int n) ? n : 104;
        var buf = new float[512];
        long last = GC.GetAllocatedBytesForCurrentThread();
        int gcs = GC.CollectionCount(0);
        // nothing is printed while measuring (a print allocates and would show up in the next step): steps are recorded into arrays made beforehand
        var names = new string[blocks + 8]; var deltas = new long[blocks + 8]; var gcAt = new int[blocks + 8];
        int steps = 0;
        void Mark(string what)
        {
            long now = GC.GetAllocatedBytesForCurrentThread();
            deltas[steps] = now - last; names[steps] = what; gcAt[steps] = GC.CollectionCount(0) - gcs;
            steps++;
            last = GC.GetAllocatedBytesForCurrentThread();
        }
        Action<int> block; Action note;
        switch (which)
        {
            case "snes":
            {
                var f = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
                var p = PresetPlayable.Build(PresetFixtures.Make(f, PresetFixtures.Instruments(f)[1], 2, 3), out _)!;
                Mark("setup");
                var s = new SnesSampler(p.Snes!, 48000); Mark("construct");
                block = _ => s.Render(buf, 256); note = () => s.NoteOn(0, p.InstrumentValue, SnesSampler.PitchFor(0), 100, 100);
                break;
            }
            case "fm":
            {
                var f = PresetFixtures.Synthetic().First(s => s.Name == "opn");
                var p = PresetPlayable.Build(PresetFixtures.Make(f, PresetFixtures.Instruments(f)[0], 2, 3), out _)!;
                Mark("setup");
                var s = new OpnSampler(p.Sega!, 48000); Mark("construct");
                block = _ => s.Render(buf, 256); note = () => s.NoteOn(0, p.InstrumentValue - 1, 220, 0.9);
                break;
            }
            case "dac":
            {
                var f = PresetFixtures.Synthetic().First(s => s.Name == "opn");
                var p = PresetPlayable.Build(PresetFixtures.Make(f, PresetFixtures.Instruments(f)[6], 0, 3), out _)!;
                Mark("setup");
                var s = new OpnSampler(p.Sega!, 48000); Mark("construct");
                block = _ => s.Render(buf, 256); note = () => s.NoteOn(OpnSampler.DacBase, p.InstrumentValue - 1, 220, 0.9);
                break;
            }
            default:
            {
                var f = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
                var p = PresetPlayable.Build(PresetFixtures.Make(f, PresetFixtures.Instruments(f)[0], 2, 3), out _)!;
                Mark("setup");
                var s = new GbSampler(p.Gb!, 48000); Mark("construct");
                block = _ => s.Render(buf, 256); note = () => s.NoteOn(0, 440, 12, -1, false);
                break;
            }
        }
        note(); Mark("note on");
        for (int i = 0; i < blocks; i++) { block(i); Mark(i < 4 ? "warm-up block" : "window block"); }
        Console.WriteLine($"alloc-probe {which}: this thread's allocation per step (only steps that allocate, or that saw a collection, are listed)");
        long total = 0, inWindow = 0; int bi = 0;
        for (int i = 0; i < steps; i++)
        {
            total += deltas[i];
            bool isBlock = names[i].EndsWith("block");
            int idx = isBlock ? bi++ : -1;
            if (isBlock && idx >= 4) inWindow += deltas[i];
            if (deltas[i] != 0 || gcAt[i] != 0)
                Console.WriteLine($"  {names[i]}{(isBlock ? " " + (idx < 4 ? idx : idx - 4) : "")}: +{deltas[i]} bytes{(gcAt[i] != 0 ? $" (a gen0 collection ran: {gcAt[i]})" : "")}");
        }
        Console.WriteLine($"  setup and construction are not part of the check's window; the window (the blocks after warm-up block 3) allocated {inWindow} bytes; all steps {total}");
        return inWindow == 0 ? 0 : 1;
    }
}

internal static partial class PresetChecks
{
    /// <summary>`FruityHost alloc-probe-loop [iterations] [garbageMB]`: the `play-alloc` check's four engines built and measured over and over in ONE process (so the runtime's tiering state ages and the heap fills),
    /// each 100-block window recorded block by block into arrays made beforehand; every window that allocates is listed with its block and byte count. Between iterations some garbage is made so collections
    /// land at different places.</summary>
    public static int AllocProbeLoop(string[] args)
    {
        int iterations = args.Length > 1 && int.TryParse(args[1], out int it) ? it : 300;
        int garbageMb = args.Length > 2 && int.TryParse(args[2], out int gm) ? gm : 2;
        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var sp = PresetPlayable.Build(PresetFixtures.Make(snes, PresetFixtures.Instruments(snes)[1], 2, 3), out _)!;
        var op = PresetPlayable.Build(PresetFixtures.Make(md, PresetFixtures.Instruments(md)[0], 2, 3), out _)!;
        var dp = PresetPlayable.Build(PresetFixtures.Make(md, PresetFixtures.Instruments(md)[6], 0, 3), out _)!;
        var gp = PresetPlayable.Build(PresetFixtures.Make(gb, PresetFixtures.Instruments(gb)[0], 2, 3), out _)!;
        var buf = new float[512];
        var perBlock = new long[100];
        var hits = new List<string>();
        long[] garbage = new long[64];
        string[] engines = ["SNES", "Genesis FM", "Genesis DAC", "Game Boy"];
        var windowDeltas = new long[iterations * 4];
        var firstBlock = new int[iterations * 4];
        for (int n = 0; n < iterations; n++)
        {
            for (int e = 0; e < 4; e++)
            {
                Action<int> block; Action note;
                switch (e)
                {
                    case 0: { var s = new SnesSampler(sp.Snes!, 48000); block = _ => s.Render(buf, 256); note = () => s.NoteOn(0, sp.InstrumentValue, SnesSampler.PitchFor(0), 100, 100); break; }
                    case 1: { var s = new OpnSampler(op.Sega!, 48000); block = _ => s.Render(buf, 256); note = () => s.NoteOn(0, op.InstrumentValue - 1, 220, 0.9); break; }
                    case 2: { var s = new OpnSampler(dp.Sega!, 48000); block = _ => s.Render(buf, 256); note = () => s.NoteOn(OpnSampler.DacBase, dp.InstrumentValue - 1, 220, 0.9); break; }
                    default: { var s = new GbSampler(gp.Gb!, 48000); block = _ => s.Render(buf, 256); note = () => s.NoteOn(0, 440, 12, -1, false); break; }
                }
                note(); for (int i = 0; i < 4; i++) block(i);
                long last = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) { block(i); long now = GC.GetAllocatedBytesForCurrentThread(); perBlock[i] = now - last; last = GC.GetAllocatedBytesForCurrentThread(); }
                long sum = 0; int first = -1;
                for (int i = 0; i < 100; i++) { sum += perBlock[i]; if (first < 0 && perBlock[i] != 0) first = i; }
                windowDeltas[n * 4 + e] = sum; firstBlock[n * 4 + e] = first;
            }
            // some garbage so collections land at different places, and a moment of real time so the runtime's timers can fire
            for (int g = 0; g < garbageMb * 16; g++) garbage = new long[8192];
            if (n % 25 == 0) Thread.Sleep(120);
        }
        int bad = 0;
        for (int k = 0; k < windowDeltas.Length; k++)
            if (windowDeltas[k] != 0) { bad++; if (hits.Count < 40) hits.Add($"iteration {k / 4} {engines[k % 4]}: {windowDeltas[k]} bytes, first in window block {firstBlock[k]}"); }
        Console.WriteLine($"alloc-probe-loop: {iterations} iterations x 4 engines = {windowDeltas.Length} windows of 100 blocks; {bad} allocated");
        foreach (var h in hits) Console.WriteLine("  " + h);
        GC.KeepAlive(garbage);
        return bad == 0 ? 0 : 1;
    }
}
