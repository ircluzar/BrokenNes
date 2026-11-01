// Round 6 (imodel): checks that need the samplers: BRR structure and what a broken sample does, which fields are audible (rendered PCM through the existing sampler classes, clean against edited), the golden hashes of
// untouched snapshots, and allocation. A claim "this field has no audible effect today" is tested as strictly as "it has one": the PCM must be bit-identical.
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

internal static partial class ImodelChecks
{
    /// <summary>Test switches for the mutant runs (never set otherwise): the sampler is fed a snapshot without its overlay; the classification of a field is overridden.</summary>
    internal static bool StripOverlay;
    internal static Func<Instrument, Field, Audible?>? ClassifyOverride;

    internal static float[] RenderInstrument(object snap, int index)
    {
        switch (snap)
        {
            case SpcSnapshot s:
                if (StripOverlay) s = s.WithOverlay(null);
                return ImodelData.RenderSnes(s, [index, index, index]);
            case SegaSnapshot g:
            {
                int first = g.SlotBase(index), count = g.Slots(index);
                var notes = new List<(int, int, double, double)> { (first, index, 220.0, 0.9) };
                if (count > 1) notes.Add((first + 1, index, 330.0, 0.8));
                return ImodelData.RenderSega(g, g.Kind == SegaSnapshot.KindOpll ? 24 : 12, notes.ToArray());
            }
            case GbSnapshot b: return ImodelData.RenderGbChannel(b, index);
            default: throw new ArgumentException("nothing plays a " + snap.GetType().Name);
        }
    }

    // ---- BRR structure ----

    private static bool LoopOk(Instrument i)
    {
        var d = i.Blobs[0].Data;
        int blocks = d.Length / 9;
        int rel = i.Get("loop.addr") - i.Origin;
        return rel >= 0 && rel % 9 == 0 && rel / 9 < blocks;
    }

    public static void Structure(ImodelReport r)
    {
        var snes = ImodelData.SnesWithSamples();
        var src = Src(snes);
        var bases = new List<(string, Instrument)> { ("slot0", src.Read(0)!), ("slot1", src.Read(1)!), ("slot2", src.Read(2)!) };
        foreach (var fx in ImodelFixtures.Real()) if (fx.Snapshot is SpcSnapshot real) { var rs = Src(real); foreach (var i in rs.List().Take(5)) bases.Add(($"smw-{i.Index}", i)); }
        int trials = 0, violations = 0;
        foreach (var (name, inst) in bases)
        {
            r.Expect(InstrumentCorruptor.BrrWellFormed(inst.Blobs[0].Data), $"{name}: the starting sample is not well-formed, the test would prove nothing");
            for (int t = 0; t < 120; t++)
            {
                ulong seed = (ulong)(t * 7919 + 13);
                int strength = (t * 53) % 1000 + 1, part = ((t / 3) % 3) switch { 0 => 0, 1 => InstrumentOp.PartHeaders, _ => InstrumentOp.PartNibbles };
                var h = new InstrumentHistory(inst);
                switch (t % 4)
                {
                    case 0: h.Randomise(strength / 1000.0, seed, true, "sample", part); break;
                    case 1: h.Nudge("sample", (t % 5 + 1) * (t % 2 == 0 ? 1 : -1), seed, 1 + t % 6, true, part); break;
                    case 2: h.FlipBits("sample", 1 + t % 8, seed, true, part); break;
                    default: h.Randomise(strength / 1000.0, seed, true); break;                          // the whole instrument, loop pointer included
                }
                trials++;
                bool wf = InstrumentCorruptor.BrrWellFormed(h.Current.Blobs[0].Data);
                bool lastLoops = (h.Current.Blobs[0].Data[^9] & 2) != 0;
                bool loopTouched = h.Current.Get("loop.addr") != inst.Get("loop.addr");
                bool loopOk = !(lastLoops || loopTouched) || LoopOk(h.Current);
                if (!wf || !loopOk) { violations++; r.Expect(false, $"{name} trial {t} ({(OpKind)h.Operations[0].Kind} strength {strength}, part {part}): keeping the structure left it {(wf ? "" : "malformed ")}{(loopOk ? "" : "with a loop pointer outside the sample")}"); }
                else r.Assertions++;
            }
        }
        r.Note($"{trials} structure-keeping operations on {bases.Count} samples broke the structure {violations} times");

        // not keeping it: the strength decides how often the structure breaks
        double Rate(double strength)
        {
            int broken = 0, n = 100;
            for (int t = 0; t < n; t++)
            {
                var h = new InstrumentHistory(src.Read(1)!);
                h.Randomise(strength, (ulong)(t * 104729 + 7), false, "sample");
                if (!InstrumentCorruptor.BrrWellFormed(h.Current.Blobs[0].Data)) broken++;
            }
            return broken / (double)n;
        }
        double r15 = Rate(0.15), r60 = Rate(0.6), r100 = Rate(1.0);
        r.Expect(r15 < 0.35 && r60 > 0.8 && r100 > 0.9 && r15 < r60, $"the chance of breaking the structure must rise with the strength: 15% -> {r15:0.00}, 60% -> {r60:0.00}, 100% -> {r100:0.00}");
        r.Note($"structure broken without keep-structure: {r15:P0} at strength 15%, {r60:P0} at 60%, {r100:P0} at 100%");

        // a sample the user broke on purpose is still listed and played
        var e = src.Read(1)!.Clone();
        e.Blobs[0].Data[0] = (byte)(14 << 4 | e.Blobs[0].Data[0] & 0x0F);       // range 14: the DSP treats it as invalid
        e.Blobs[0].Data[^9] &= 0xFE;                                              // no end flag: the DSP runs on into whatever follows
        var w = src.Write(e);
        r.Expect(w.Ok, "a malformed sample must still be writable: " + w.Reason);
        if (w.Ok)
        {
            var broken = (SpcSnapshot)w.Snapshot!;
            r.Expect(broken.Instruments().Any(x => x.Slot == 1) && broken.HasSample(1) && broken.Instruments().Count == 3, $"a broken sample must stay in the sampler's list ({string.Join(",", broken.Instruments().Select(x => x.Slot))})");
            var clean = ImodelData.RenderSnes(snes, [1, 1, 1]);
            var edited = ImodelData.RenderSnes(broken, [1, 1, 1]);
            r.Expect(ImodelData.Rms(edited) > 1e-4 && ImodelData.DiffRms(clean, edited) > 1e-4, $"a broken sample must still play and sound different (rms {ImodelData.Rms(edited):0.0000}, difference {ImodelData.DiffRms(clean, edited):0.0000})");
            var unpinned = ((SpcSnapshot)w.Snapshot!).WithOverlay(null);
            r.Expect(!unpinned.HasSample(1) || unpinned.Instruments().Count != 3, "without the overlay the broken sample must fall out of the list (this is why edits are pinned)");
            // and the other samples are untouched, and the listing round-trips
            var again = Src(broken).Read(1)!;
            r.Expect(again.Blobs[0].Data.AsSpan().SequenceEqual(e.Blobs[0].Data), "the broken sample must read back as written");
        }
    }

    // ---- audibility ----

    private static (string Id, int Value)[] Prelude(Instrument inst, string id)
    {
        if (inst.Kind == InstrumentKind.SnesSample)
            return id switch
            {
                "env.ar" => [("env.adsr", 1)],
                "env.dr" => [("env.adsr", 1), ("env.sl", 1)],
                "env.sl" => [("env.adsr", 1), ("env.dr", 5)],
                "env.sr" => [("env.adsr", 1), ("env.dr", 7), ("env.sl", 1)],
                // GAIN mode needs a GAIN byte that is not zero: a direct level of 0 is silence
                "gain.mode" or "gain.curve" or "gain.rate" => [("env.adsr", 0), ("gain.curve", 3), ("gain.rate", 20)],
                _ => [],
            };
        if (inst.Kind == InstrumentKind.OpllUser)
            return id switch
            {
                "mod.eg" => [("mod.dr", 15), ("mod.sl", 8)],
                "mod.rr" => [("mod.eg", 0), ("mod.dr", 15), ("mod.sl", 8)],
                "car.rr" => [],
                _ => [],
            };
        if (inst.Kind == InstrumentKind.OpnPatch)
        {
            string op = id.StartsWith("op") ? id[..3] : "";
            string tail = id.StartsWith("op") ? id[4..] : id;
            return tail switch
            {
                "ams" => [("lfo.on", 1), ("lfo.freq", 6), ("op1.am", 1), ("op2.am", 1), ("op3.am", 1), ("op4.am", 1)],
                "fms" => [("lfo.on", 1), ("lfo.freq", 6)],
                "lfo.on" or "lfo.freq" => [("lfo.on", 1), ("lfo.freq", 6), ("fms", 7)],
                "am" => [("lfo.on", 1), ("lfo.freq", 6), ("ams", 3)],
                "ssg.mode" => [(op + ".ssg.on", 1)],
                "ssg.on" => [(op + ".ssg.mode", 3)],
                "d2r" => [(op + ".d1r", 31), (op + ".sl", 3)],
                "sl" => [(op + ".d1r", 31)],
                "d1r" => [(op + ".sl", 10)],
                "rs" => [(op + ".ar", 20), (op + ".d1r", 12)],
                "rr" => [],
                _ => [],
            };
        }
        // a Game Boy envelope has a direction only when it has a pace
        if (inst.Kind is InstrumentKind.GbPulse1 or InstrumentKind.GbPulse2 or InstrumentKind.GbNoise && id == "env.dir") return [("env.pace", 3)];
        return [];
    }

    private static bool SkipYes(Instrument inst, Field f)
    {
        // a loop pointer is used only by a sample that loops; a DAC loop's own fields only by a sound that loops
        if (inst.Kind == InstrumentKind.SnesSample && f.Id == "loop.addr") return (inst.Blobs[0].Data[^9] & 2) == 0;
        if (inst.Kind == InstrumentKind.OpnDac && f.Id is "dac.loop.start" or "dac.loop.end" or "dac.cycle") return inst.Get("dac.loop") == 0;
        return false;
    }

    /// <summary>Changes each field of one instrument in turn (in the context its prelude sets up), renders the sampler's PCM and checks the field's audibility claim: Yes means some value changes the sound, No means every value gives bit-identical PCM.</summary>
    public static void Probe(ImodelReport r, ImodelFixture fx, int index, string label, Func<Instrument, Field, bool>? only = null)
    {
        var baseInst = Src(fx.Snapshot).Read(index)!;
        int yes = 0, no = 0;
        foreach (var f in baseInst.Fields)
        {
            if (only != null && !only(baseInst, f)) continue;
            var h = new InstrumentHistory(baseInst);
            foreach (var (pid, pv) in Prelude(baseInst, f.Id)) if (h.Current.Field(pid) != null) h.Set(pid, pv);
            var wctx = Src(fx.Snapshot).Write(h.Current);
            if (!wctx.Ok) { r.Expect(false, $"{label} {f.Id}: the context was refused: {wctx.Reason}"); continue; }
            object ctx = wctx.Snapshot!;
            var ctxInst = Src(ctx).Read(index)!;
            var pcm0 = RenderInstrument(ctx, index);
            r.Expect(ImodelData.Rms(pcm0) > 1e-4, $"{label} {f.Id}: the context renders silence (rms {ImodelData.Rms(pcm0):0.00000}), the probe would prove nothing");
            string h0 = ImodelData.Hash(pcm0);
            bool anyDiff = false, allSame = true;
            double maxDiff = 0;
            foreach (int v in Candidates(ctxInst.Field(f.Id)!))
            {
                var h2 = new InstrumentHistory(ctxInst);
                h2.Set(f.Id, v);
                var w2 = Src(ctx).Write(h2.Current);
                if (!w2.Ok) continue;
                var pcm = RenderInstrument(w2.Snapshot!, index);
                double d = ImodelData.DiffRms(pcm0, pcm);
                maxDiff = Math.Max(maxDiff, d);
                if (d > 1e-5) anyDiff = true;
                if (ImodelData.Hash(pcm) != h0 || pcm.Length != pcm0.Length) allSame = false;
            }
            var claim = ClassifyOverride?.Invoke(baseInst, f) ?? f.Audible;
            if (claim == Audible.Yes)
            {
                if (SkipYes(baseInst, f)) continue;
                yes++;
                r.Expect(anyDiff, $"{label} {f.Id} is marked audible but no value changed the sound (largest difference {maxDiff:0.000000})");
            }
            else
            {
                no++;
                r.Expect(allSame, $"{label} {f.Id} is marked as having no audible effect ({f.AudibleNote}) but a value changed the sound (difference {maxDiff:0.000000})");
            }
        }
        foreach (var b in baseInst.Blobs)
        {
            var ctx = fx.Snapshot;
            var pcm0 = RenderInstrument(ctx, index);
            var h = new InstrumentHistory(baseInst);
            h.Randomise(0.6, 4242, true, b.Id);
            var w = Src(ctx).Write(h.Current);
            r.Expect(w.Ok, $"{label} {b.Id}: the corrupted blob was refused: {w.Reason}");
            if (!w.Ok) continue;
            double d = ImodelData.DiffRms(pcm0, RenderInstrument(w.Snapshot!, index));
            if (b.Audible == Audible.Yes) { yes++; r.Expect(d > 1e-5, $"{label} blob {b.Id} is marked audible but corrupting it did not change the sound ({d:0.000000})"); }
            else { no++; r.Expect(d == 0, $"{label} blob {b.Id} is marked as having no audible effect but corrupting it changed the sound ({d:0.000000})"); }
        }
        r.Note($"{label}: {yes} audible claims and {no} no-effect claims tested");
    }

    public static void Audible_(ImodelReport r)
    {
        var syn = ImodelFixtures.Synthetic();
        ImodelFixture F(string n) => syn.First(x => x.Name == n);
        // SNES: the two looping samples (the one-shot is too short to hear most fields)
        Probe(r, F("snes-samples"), 0, "snes slot 0");
        Probe(r, F("snes-samples"), 2, "snes slot 2");
        // Genesis: two patches whose operators all reach the output, the looping DAC sound, the one-shot, the PSG
        Probe(r, F("opn"), 0, "opn patch 0");
        Probe(r, F("opn"), 2, "opn patch 2");
        Probe(r, F("opn"), 6, "opn dac loop");
        Probe(r, F("opn"), 7, "opn dac one-shot");
        Probe(r, F("opn"), ((SegaSnapshot)F("opn").Snapshot).PsgInstrument, "opn psg tones");
        Probe(r, F("opn"), ((SegaSnapshot)F("opn").Snapshot).PsgInstrument + 1, "opn psg noise");
        Probe(r, F("opll"), 0, "opll user");
        // Game Boy: each of the four channels
        for (int ch = 0; ch < 4; ch++) Probe(r, F("gb-dmg"), ch, $"gb channel {ch}");
        // NES: nothing plays the registers, so there is no PCM to compare; the claim is that every field says so
        foreach (var inst in Src(F("nes").Snapshot).List())
        {
            r.Expect(inst.Fields.All(f => f.Audible == Audible.No) && inst.Blobs.All(b => b.Audible == Audible.No), $"nes {inst.Name}: every field must be marked as having no audible effect (there is no NES sampler)");
        }
        // the real games: the fields that must be silent are silent, and the salient ones are heard
        foreach (var fx in ImodelFixtures.Real())
        {
            switch (fx.Snapshot)
            {
                case SpcSnapshot s:
                {
                    var list = Src(s).List();
                    // (a loop pointer is heard only once the sample has played through to its end: short looping samples)
                    var loopers = list.Where(i => (i.Blobs[0].Data[^9] & 2) != 0 && i.Blobs[0].Data.Length <= 9 * 100 && ImodelData.Rms(RenderInstrument(s, i.Index)) > 1e-3).Take(2).ToList();
                    r.Expect(loopers.Count > 0, $"{fx.Name}: no looping sample plays in the real game's sound memory");
                    foreach (var i in loopers) Probe(r, fx, i.Index, $"{fx.Name} slot {i.Index}", (inst, f) => f.Id is "tune.cents" or "vol.l" or "vol.r" or "mix.noise" or "env.adsr" or "gain.mode" or "loop.addr" or "mix.pmod");
                    break;
                }
                case GbSnapshot:
                    for (int ch = 0; ch < 4; ch++) Probe(r, fx, ch, $"{fx.Name} channel {ch}");
                    break;
                case SegaSnapshot g when g.Kind == SegaSnapshot.KindOpn:
                {
                    int shown = 0;
                    for (int c = 0; c < 6 && shown < 2; c++)
                        if (g.OpnPatchUsed(c) && ImodelData.Rms(RenderInstrument(g, c)) > 1e-3) { Probe(r, fx, c, $"{fx.Name} patch {c}", (inst, f) => f.Id is "pan.l" or "pan.r" or "alg" or "fb" || f.Id.EndsWith(".tl")); shown++; }
                    if (g.Dac.Length > 0) Probe(r, fx, 6, $"{fx.Name} dac 0", (inst, f) => f.Id is "dac.rate" or "dac.loop");
                    Probe(r, fx, g.PsgInstrument, $"{fx.Name} psg");
                    break;
                }
                case SegaSnapshot g2: Probe(r, fx, g2.PsgInstrument, $"{fx.Name} psg"); break;
            }
        }
    }

    // ---- golden: untouched snapshots play as they always did ----

    internal static readonly Dictionary<string, string> Baseline = new()
    {
        ["snes-testsample"] = "68C1E70FEE49D1B4", ["snes-samples-012"] = "FB43C1502050B57C", ["snes-samples-210"] = "987DF6D6A5FA851D",
        ["gb-dmg"] = "3C8182EC1214095B", ["gb-cgb"] = "3C8182EC1214095B", ["psg"] = "072F4F9B130B72B5", ["opll"] = "C1BB4AF5907E2CBF", ["opn"] = "33DB00A31FAB5379",
    };

    internal static readonly Dictionary<string, string> RealBaseline = new()
    {
        ["smw"] = "6D3BC4987EEF8698", ["sonic1"] = "84F10318A98B238C", ["zelda-gb"] = "26F0D4F8E46D3A37", ["addams-gg"] = "6F4FEEB3B47C0886",
    };

    public static void Neutral(ImodelReport r)
    {
        foreach (var (name, hash, _) in ImodelGolden.Compute())
            r.Expect(Baseline.TryGetValue(name, out var want) && want == hash, $"{name}: the sampler now plays {hash}, it played {(Baseline.TryGetValue(name, out var w) ? w : "?")} before the instrument-edit change");
        int real = 0;
        foreach (var line in ImodelGolden.Real())
        {
            string name = line.Split(' ')[0];
            if (line.Contains("not available")) continue;
            real++;
            r.Expect(RealBaseline.TryGetValue(name, out var want) && line.EndsWith(want), $"{name}: the real game now plays differently from before the change: {line}");
        }
        r.Note($"{Baseline.Count} made-up snapshots and {real} real games play bit for bit as before the SNES overlay was added");

        // an overlay that only pins a well-formed sample, or that names a sample nobody plays, does not change the sound
        var snes = ImodelData.SnesWithSamples();
        var pinOnly = new SnesOverlay([new SnesOverlay.Entry { Srcn = 2, Flags = SnesOverlay.Pinned, Blocks = 3 }, new SnesOverlay.Entry { Srcn = 40, Flags = SnesOverlay.HasEnv | SnesOverlay.HasTune, Adsr1 = 0x80, Tune = 700 }]);
        r.Expect(ImodelData.Hash(ImodelData.RenderSnes(snes.WithOverlay(pinOnly), [0, 1, 2])) == ImodelData.Hash(ImodelData.RenderSnes(snes, [0, 1, 2])), "an overlay that pins a sample or names an unused one must not change the sound");
    }

    // ---- allocation ----

    public static void Alloc(ImodelReport r)
    {
        foreach (var (label, snap) in new[] { ("without an overlay", ImodelData.SnesWithSamples()), ("with an overlay", WithEdits(ImodelData.SnesWithSamples())) })
        {
            var s = new SnesSampler(snap, 48000);
            var buf = new float[256 * 2];
            // this host runs the sampler through the JIT (the plugin DLL is Native AOT): warm it until a whole round allocates nothing, then measure
            long Round(int blocks, int salt)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < blocks; i++)
                {
                    int v = i % 8;
                    s.NoteOn(v, (i + salt) % 3, SnesSampler.PitchFor((i + salt) * 7 % 2400 - 1200), 40 + i % 80, 100);
                    s.Update((v + 3) % 8, SnesSampler.PitchFor(i % 700), 50, 60);
                    s.Render(buf, 256);
                    if (i % 5 == 0) s.NoteOff((v + 1) % 8);
                }
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            int rounds = 0;
            while (rounds < 30 && Round(60, rounds) != 0) rounds++;
            long used = Round(400, 99);
            r.Expect(rounds < 30, $"the SNES sampler {label} never reached a round of 60 blocks with no allocation (30 rounds)");
            r.Expect(used == 0, $"the SNES sampler {label} allocated {used} bytes in 400 blocks of notes");
            r.Note($"SNES sampler {label}: warm after {rounds} rounds of 60 blocks, then {used} bytes allocated over 400 blocks of notes");
        }
    }

    /// <summary>A snapshot whose three samples all carry edits (envelope, tuning, volume weights, flags).</summary>
    internal static SpcSnapshot WithEdits(SpcSnapshot snap)
    {
        var src = new SnesInstruments(snap);
        object cur = snap;
        foreach (var inst in src.List())
        {
            var h = new InstrumentHistory(inst);
            h.Set("env.adsr", 1); h.Set("env.ar", 10); h.Set("env.dr", 3); h.Set("env.sl", 4); h.Set("env.sr", 12); h.Set("tune.cents", 100 * (inst.Index + 1)); h.Set("vol.l", 90); h.Set("vol.r", 64); h.Set("mix.echo", 0); h.Set("mix.pmod", 1); h.Set("loop.addr", inst.Origin);
            cur = Src(cur).Write(h.Current).Snapshot!;
        }
        return (SpcSnapshot)cur;
    }

    // ---- real games: the tools on real data ----

    public static void RealCorrupt(ImodelReport r)
    {
        int tried = 0, heard = 0;
        foreach (var fx in ImodelFixtures.Real())
        {
            if (fx.Snapshot is NesSnapshot) continue;
            var src = Src(fx.Snapshot);
            var list = src.List().Where(i => i.Editable).ToList();
            foreach (var inst in list.Take(fx.Snapshot is SegaSnapshot { Kind: SegaSnapshot.KindOpn } ? 12 : 8))
            {
                var h = new InstrumentHistory(inst);
                var a = h.Randomise(0.4, 99);
                h.Nudge("", 2, 100); h.FlipBits("", 2, 101);
                r.Expect(Illegal(h.Current) == null, $"{fx.Name} {inst.Name}: illegal after the tools: {Illegal(h.Current)}");
                var w = src.Write(h.Current);
                r.Expect(w.Ok, $"{fx.Name} {inst.Name}: the corrupted instrument was refused: {w.Reason}");
                if (!w.Ok) continue;
                tried++;
                var back = Src(w.Snapshot!).Read(inst.Index);
                r.Expect(back != null && FirstDifference(Normalised(h.Current), back) == null, $"{fx.Name} {inst.Name}: the corrupted instrument reads back differently: {(back == null ? "gone" : FirstDifference(Normalised(h.Current), back))}");
                float[] clean, edited;
                try { clean = RenderInstrument(fx.Snapshot, inst.Index); edited = RenderInstrument(w.Snapshot!, inst.Index); }
                catch (Exception e) { r.Expect(false, $"{fx.Name} {inst.Name}: rendering the corrupted instrument threw {e.GetType().Name}: {e.Message}"); continue; }
                r.Expect(edited.All(float.IsFinite), $"{fx.Name} {inst.Name}: the corrupted instrument renders non-finite samples");
                if (ImodelData.DiffRms(clean, edited) > 1e-4) heard++;
            }
        }
        r.Expect(tried > 0, "no real game was available for the corruption check");
        r.Expect(tried == 0 || heard >= tried / 2, $"only {heard} of {tried} corrupted real instruments sound different from the clean ones");
        r.Note($"{tried} real instruments corrupted with all three tools, written, read back and rendered; {heard} sound different");
    }
}
