// Round 6 (imodel): checks of the model itself: schemas, reading, writing, round trips, boundaries. Every check takes a report and records what it asserts; ImodelChecks.Mutants runs each against a deliberately broken library.
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

internal static partial class ImodelChecks
{
    internal static IInstrumentSource Src(object snap) => InstrumentSources.For(snap) ?? throw new ArgumentException("no reader for " + snap.GetType().Name);

    /// <summary>The values worth trying on a field other than its current one: both ends, the middle, and the neighbours of the current value.</summary>
    internal static IEnumerable<int> Candidates(Field f)
    {
        var set = new SortedSet<int> { f.Min, f.Max, (f.Min + f.Max) / 2, f.Value - 1, f.Value + 1 };
        foreach (int v in set) if (v >= f.Min && v <= f.Max && v != f.Value) yield return v;
    }

    // ---- 1. schemas ----

    public static void Schema(ImodelReport r)
    {
        var kinds = new HashSet<InstrumentKind>();
        var counts = new SortedDictionary<string, int>();
        foreach (var fx in ImodelFixtures.Synthetic())
            foreach (var inst in Src(fx.Snapshot).List())
            {
                kinds.Add(inst.Kind);
                string where = $"{fx.Name} {inst.Name}";
                var ids = new HashSet<string>();
                foreach (var f in inst.Fields)
                {
                    r.Expect(ids.Add(f.Id), $"{where}: field id '{f.Id}' appears twice");
                    r.Expect(f.Min <= f.Max && f.Value >= f.Min && f.Value <= f.Max, $"{where}: {f.Id} = {f.Value} is outside {f.Min}..{f.Max}");
                    r.Expect(f.Name.Length > 0 && f.Group.Length > 0, $"{where}: {f.Id} has no name or group");
                    r.Expect(f.Explanation.Length >= 12 && f.Explanation.Length <= 400 && !f.Explanation.Contains('\n'), $"{where}: {f.Id} explanation is not one line of 12 to 400 characters");
                    r.Expect(f.Audible == Audible.Yes || !string.IsNullOrWhiteSpace(f.AudibleNote), $"{where}: {f.Id} is marked as not audible without saying why");
                    r.Expect(f.Width >= 1 && f.Width <= 24, $"{where}: {f.Id} width {f.Width}");
                    switch (f.Kind)
                    {
                        case FieldKind.Enum: r.Expect(f.Names != null && f.Names.Length == f.Max - f.Min + 1, $"{where}: {f.Id} enum names do not cover {f.Min}..{f.Max}"); break;
                        case FieldKind.Bits: r.Expect(f.Min == 0 && f.Max == (1 << f.Width) - 1, $"{where}: {f.Id} bit field range {f.Min}..{f.Max} for width {f.Width}"); break;
                        case FieldKind.Signed: r.Expect(f.Min < 0 && f.Max > 0 && -f.Min <= 1 << (f.Width - 1) && f.Max < 1 << (f.Width - 1), $"{where}: {f.Id} signed range {f.Min}..{f.Max} does not fit {f.Width} bits"); break;
                        case FieldKind.Range: r.Expect(f.Max < (1 << f.Width), $"{where}: {f.Id} max {f.Max} needs more than {f.Width} bits"); break;
                    }
                }
                foreach (var b in inst.Blobs) r.Expect(b.MinLength <= b.Data.Length && b.Data.Length <= b.MaxLength && b.Explanation.Length > 12, $"{where}: blob {b.Id} length {b.Data.Length} outside {b.MinLength}..{b.MaxLength} or unexplained");
                r.Expect(inst.Description.Length > 10 && inst.Name.Length > 0, $"{where}: no name or description");
                counts[inst.Kind.ToString()] = inst.Fields.Length;
            }
        foreach (var k in Enum.GetValues<InstrumentKind>()) r.Expect(kinds.Contains(k), $"no synthetic fixture has a {k} instrument");
        r.Note("fields per kind: " + string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")));
    }

    // ---- 2. reading, against register values written by hand ----

    private static void Is(ImodelReport r, Instrument inst, string what, params (string Id, int Value)[] expect)
    {
        foreach (var (id, v) in expect)
        {
            var f = inst.Field(id);
            r.Expect(f != null && f.Value == v, $"{what}: {id} is {(f == null ? "missing" : f.Value.ToString())}, expected {v}");
        }
    }

    /// <summary>Reads made-up snapshots whose registers were encoded by independent code (the test data builders and hand-written hex), and checks every field against the register layouts of the chips.</summary>
    public static void Registers(ImodelReport r)
    {
        // ---- SNES: BRR blocks, the directory, ADSR / GAIN from the game's voices and the sampler's defaults ----
        var snes = ImodelData.SnesWithSamples();
        var list = new SnesInstruments(snes).List();
        r.Expect(list.Count == 3 && list.Select(i => i.Index).SequenceEqual([0, 1, 2]), $"SNES sample list: {string.Join(",", list.Select(i => i.Index))}, expected 0,1,2");
        if (list.Count == 3)
        {
            Is(r, list[0], "snes slot 0 (voice 0: ADSR $8F $E8)", ("env.adsr", 1), ("env.ar", 15), ("env.dr", 0), ("env.sl", 7), ("env.sr", 8), ("gain.mode", 0), ("gain.curve", 0), ("gain.rate", 0), ("loop.addr", 0x200), ("tune.cents", 0), ("vol.l", 127), ("vol.r", 127), ("mix.echo", 1), ("mix.noise", 0), ("mix.pmod", 0));
            Is(r, list[1], "snes slot 1 (voice 2: GAIN 100)", ("env.adsr", 0), ("gain.mode", 0), ("gain.curve", 3), ("gain.rate", 4), ("loop.addr", 0x240));
            Is(r, list[2], "snes slot 2 (no voice: organ default $FF $E0 $7F)", ("env.adsr", 1), ("env.ar", 15), ("env.dr", 7), ("env.sl", 7), ("env.sr", 0), ("gain.mode", 0), ("gain.curve", 3), ("gain.rate", 31), ("loop.addr", 0x289));
            r.Expect(list[0].Blobs[0].Data.SequenceEqual(ImodelData.Brr(9, 0, true, true, 7, 7, 7, 7, 7, 7, 7, 7, 9, 9, 9, 9, 9, 9, 9, 9)), "snes slot 0 blob is not the BRR block the test wrote");
            r.Expect(list[1].Blobs[0].Data.Length == 36 && list[2].Blobs[0].Data.Length == 27, $"snes sample lengths {list[1].Blobs[0].Data.Length}, {list[2].Blobs[0].Data.Length}");
            r.Expect(list[0].Origin == 0x200 && list[1].Origin == 0x240 && list[2].Origin == 0x280, "snes origins");
        }

        // ---- Genesis: the YM2612 patch layout (registers $30-$90 per operator, $B0, $B4, $22) ----
        var opn = ImodelData.OpnWithPatches();
        var gen = new SegaInstruments(opn);
        var p0 = gen.Read(0)!;
        Is(r, p0, "opn patch 0", ("alg", 4), ("fb", 3), ("pan.l", 1), ("pan.r", 1), ("ams", 0), ("fms", 0), ("lfo.on", 1), ("lfo.freq", 3),
            ("op1.mul", 1), ("op1.tl", 30), ("op1.ar", 31), ("op1.d1r", 4), ("op1.sl", 3), ("op1.d2r", 2), ("op1.rr", 6), ("op1.dt", 0), ("op1.rs", 0),
            ("op2.mul", 1), ("op2.tl", 0), ("op2.d1r", 6), ("op2.sl", 2), ("op2.rr", 8),
            ("op3.mul", 14), ("op3.tl", 40), ("op3.d1r", 8), ("op3.sl", 4), ("op3.d2r", 3), ("op3.rr", 7),
            ("op4.mul", 1), ("op4.tl", 0), ("op4.d1r", 3), ("op4.sl", 2), ("op4.d2r", 1), ("op4.rr", 6));
        var p2 = gen.Read(2)!;
        Is(r, p2, "opn patch 2", ("alg", 0), ("fb", 6), ("pan.l", 1), ("pan.r", 0), ("ams", 1), ("fms", 3), ("op1.dt", 1), ("op1.rs", 1), ("op1.ar", 28), ("op3.dt", 5), ("op3.ar", 30));
        // the raw bytes the test builder wrote must be where the reader looks: operator 2 is register offset +8, operator 3 is +4
        r.Expect(opn.OpnPatch[0][7 + 1] == 0 && opn.OpnPatch[0][14 + 1] == 40, "opn patch bytes: the builder's layout is not the one the reader assumes");
        var dac = gen.Read(6)!;
        Is(r, dac, "opn dac 0 (looping)", ("dac.rate", 11025), ("dac.loop", 1), ("dac.loop.start", 500), ("dac.loop.end", 600), ("dac.cycle", 11025));
        r.Expect(dac.Blobs[0].Data.Length == 600, "dac blob length");
        Is(r, gen.Read(7)!, "opn dac 1 (one-shot)", ("dac.rate", 11025), ("dac.loop", 0), ("dac.loop.start", 0), ("dac.loop.end", 0));

        // ---- PSG ----
        var psg = new SegaInstruments(ImodelData.PsgOnly());
        Is(r, psg.Read(0)!, "psg tones", ("tone1.period", 0x1FC), ("tone2.period", 0x150), ("tone3.period", 0xA0), ("tone1.att", 2), ("tone2.att", 5), ("tone3.att", 15), ("stereo", 0xFF));
        Is(r, psg.Read(1)!, "psg white noise", ("noise.mode", 1), ("noise.rate", 1), ("noise.att", 4));

        // ---- YM2413: the user instrument bytes $21 $21 $1A $03 $F4 $F3 $84 $34, and ROM instrument 1 (violin: $71 $61 $1E $17 $D0 $78 $00 $17) ----
        var opll = new SegaInstruments(ImodelData.OpllWithUser());
        r.Expect(opll.List().Count == 19, $"opll instrument count {opll.List().Count}");
        Is(r, opll.Read(0)!, "opll user", ("mod.am", 0), ("mod.vib", 0), ("mod.eg", 1), ("mod.ksr", 0), ("mod.mul", 1), ("car.eg", 1), ("car.mul", 1), ("mod.ksl", 0), ("mod.tl", 0x1A), ("car.ksl", 0), ("car.wave", 0), ("mod.wave", 0), ("mod.fb", 3),
            ("mod.ar", 15), ("mod.dr", 4), ("car.ar", 15), ("car.dr", 3), ("mod.sl", 8), ("mod.rr", 4), ("car.sl", 3), ("car.rr", 4));
        var violin = opll.Read(1)!;
        Is(r, violin, "opll rom 1 (violin)", ("mod.vib", 1), ("mod.eg", 1), ("mod.ksr", 1), ("mod.mul", 1), ("car.vib", 1), ("car.ksr", 0), ("mod.tl", 30), ("car.wave", 1), ("mod.wave", 0), ("mod.fb", 7), ("mod.ar", 13), ("mod.dr", 0), ("car.ar", 7), ("car.dr", 8), ("car.sl", 1), ("car.rr", 7));
        r.Expect(!violin.Editable && !string.IsNullOrEmpty(violin.ReadOnlyReason), "a ROM instrument must be read-only with a reason");

        // ---- Game Boy ----
        var gb = new GbInstruments(ImodelData.GbWithSettings());
        Is(r, gb.Read(0)!, "gb pulse 1", ("sweep.period", 0), ("duty", 2), ("env.vol", 15), ("env.dir", 0), ("env.pace", 3));
        Is(r, gb.Read(1)!, "gb pulse 2 (NR22 $A5: volume 10, bit 3 clear = fades out, pace 5)", ("duty", 1), ("env.vol", 10), ("env.dir", 0), ("env.pace", 5));
        Is(r, gb.Read(2)!, "gb wave", ("dac", 1), ("level", 1));
        Is(r, gb.Read(3)!, "gb noise", ("env.vol", 15), ("env.dir", 0), ("env.pace", 1), ("clock.shift", 3), ("width", 0), ("divisor", 5));
        r.Expect(gb.Read(2)!.Blobs[0].Data.SequenceEqual(ImodelData.GbWithSettings().Wave), "gb wave blob");

        // ---- NES ----
        var nes = new NesInstruments(ImodelFixtures.NesSynthetic());
        Is(r, nes.Read(0)!, "nes pulse 1", ("duty", 2), ("halt", 1), ("const", 1), ("vol", 15), ("sweep.on", 1), ("sweep.period", 0), ("sweep.neg", 1), ("sweep.shift", 0), ("length", 31));
        Is(r, nes.Read(1)!, "nes pulse 2", ("duty", 1), ("const", 1), ("vol", 10), ("sweep.on", 0), ("length", 2));
        Is(r, nes.Read(2)!, "nes triangle", ("control", 1), ("linear", 65), ("length", 1));
        Is(r, nes.Read(3)!, "nes noise", ("halt", 1), ("const", 1), ("vol", 10), ("mode", 1), ("period", 5), ("length", 3));
        Is(r, nes.Read(4)!, "nes dmc", ("irq", 0), ("loop", 1), ("rate", 14), ("load", 64), ("addr", 0), ("len", 1));
        r.Expect(nes.Read(4)!.Blobs[0].Data.Length == 17, "nes dmc blob length");
        // FromLatch: the DMC bytes come from the CPU memory at $C000 + 64 x $4012, 16 x $4013 + 1 long, only when $4010-$4013 were written
        var latch = new byte[NesSnapshot.RegCount]; latch[0x12] = 2; latch[0x13] = 3;
        var made = NesSnapshot.FromLatch(latch, 0xF0000, a => (byte)(a >> 4));
        r.Expect(made.Dmc.Length == 49 && made.Dmc[0] == (byte)(0xC080 >> 4 & 0xFF) && made.Dmc[48] == (byte)((0xC080 + 48) >> 4 & 0xFF), $"NesSnapshot.FromLatch read {made.Dmc.Length} bytes from the wrong place");
        r.Expect(NesSnapshot.FromLatch(latch, 0x0FFFF, a => 1).Dmc.Length == 0, "a DMC never written must have an empty sample");
    }

    // ---- 3. round trips: read, write unchanged, identical bytes ----

    public static void RoundTrip(ImodelReport r, IEnumerable<ImodelFixture> fixtures)
    {
        int instruments = 0;
        foreach (var fx in fixtures)
        {
            var src = Src(fx.Snapshot);
            var before = ImodelFixtures.Bytes(fx.Snapshot);
            var list = src.List();
            r.Expect(list.Count > 0, $"{fx.Name}: the snapshot lists no instrument");
            foreach (var inst in list)
            {
                instruments++;
                var res = src.Write(inst.Clone());
                if (!inst.Editable) { r.Expect(!res.Ok && !string.IsNullOrEmpty(res.Reason), $"{fx.Name} {inst.Name}: a read-only instrument must be refused with a reason"); continue; }
                r.Expect(res.Ok, $"{fx.Name} {inst.Name}: an unchanged write was refused: {res.Reason}");
                if (!res.Ok) continue;
                r.Expect(ImodelFixtures.Bytes(res.Snapshot!).AsSpan().SequenceEqual(before), $"{fx.Name} {inst.Name}: writing the instrument back unchanged changed the snapshot's bytes");
                r.Expect(res.Changed.Count == 0, $"{fx.Name} {inst.Name}: an unchanged write reports a change ({string.Join(", ", res.Changed)})");
                r.Expect(!ReferenceEquals(res.Snapshot, fx.Snapshot), $"{fx.Name} {inst.Name}: the write returned the original snapshot, not a copy");
            }
            r.Expect(ImodelFixtures.Bytes(fx.Snapshot).AsSpan().SequenceEqual(before), $"{fx.Name}: writing modified the original snapshot");
            var again = src.List();
            r.Expect(again.Count == list.Count && list.Zip(again).All(p => p.First.SameValues(p.Second)), $"{fx.Name}: reading twice gives different instruments");
        }
        r.Note($"{instruments} instruments written back unchanged");
    }

    // ---- 4. edits read back; nothing else moves ----

    private static Instrument Normalised(Instrument i)
    {
        var c = i.Clone();
        if (c.Kind == InstrumentKind.OpnDac && c.Get("dac.loop") == 0) { c.Field("dac.loop.start")!.Value = 0; c.Field("dac.loop.end")!.Value = 0; }
        return c;
    }

    private static string? FirstDifference(Instrument a, Instrument b)
    {
        for (int i = 0; i < a.Fields.Length; i++) if (a.Fields[i].Value != b.Fields[i].Value) return $"{a.Fields[i].Id} {a.Fields[i].Value} vs {b.Fields[i].Value}";
        for (int i = 0; i < a.Blobs.Length; i++)
        {
            if (a.Blobs[i].Data.Length != b.Blobs[i].Data.Length) return $"{a.Blobs[i].Id} length {a.Blobs[i].Data.Length} vs {b.Blobs[i].Data.Length}";
            for (int k = 0; k < a.Blobs[i].Data.Length; k++) if (a.Blobs[i].Data[k] != b.Blobs[i].Data[k]) return $"{a.Blobs[i].Id}[{k}] {a.Blobs[i].Data[k]} vs {b.Blobs[i].Data[k]}";
        }
        return null;
    }

    public static void EditReadback(ImodelReport r, IEnumerable<ImodelFixture> fixtures, bool collateral)
    {
        int writes = 0;
        foreach (var fx in fixtures)
        {
            var src = Src(fx.Snapshot);
            var all = src.List();
            foreach (var inst in all)
            {
                if (!inst.Editable) continue;
                string where = $"{fx.Name} {inst.Name}";
                int failed = 0;
                void Check(Instrument edited, string what)
                {
                    writes++;
                    var res = src.Write(edited);
                    if (!res.Ok) { if (failed++ < 3) r.Expect(false, $"{where}: {what}: the write was refused: {res.Reason}"); else r.Assertions++; return; }
                    var next = Src(res.Snapshot!);
                    var back = next.Read(inst.Index);
                    if (back == null) { if (failed++ < 3) r.Expect(false, $"{where}: {what}: the instrument is gone after the write"); return; }
                    string? diff = FirstDifference(Normalised(edited), back);
                    r.Expect(diff == null || failed++ >= 3, $"{where}: {what}: read back differently: {diff}");
                    if (collateral)
                        foreach (var other in all)
                        {
                            if (other.Index == inst.Index) continue;
                            var o2 = next.Read(other.Index);
                            var a = other.Clone(); var b = o2?.Clone();
                            if (b == null) { r.Expect(false, $"{where}: {what}: instrument {other.Index} disappeared"); continue; }
                            // shared by design: the YM2612's LFO is one register for the whole chip, and the PSG's white and periodic noise instruments are one noise register (and one attenuator)
                            foreach (var f in a.Fields) if (f.Id.StartsWith("lfo.") || f.Id.StartsWith("noise.")) b.Field(f.Id)!.Value = f.Value;
                            string? d2 = FirstDifference(a, b);
                            r.Expect(d2 == null || failed++ >= 3, $"{where}: {what}: instrument {other.Index} ({other.Name}) changed too: {d2}");
                        }
                }
                foreach (var f in inst.Fields)
                    foreach (int v in Candidates(f))
                    {
                        var h = new InstrumentHistory(inst);
                        h.Set(f.Id, v);
                        Check(h.Current, $"{f.Id} := {v}");
                    }
                foreach (var b in inst.Blobs)
                {
                    var h = new InstrumentHistory(inst);
                    var inverted = b.Data.Select(x => (byte)~x).ToArray();
                    h.SetBytes(b.Id, 0, inverted);
                    Check(h.Current, $"{b.Id} inverted");
                    var h2 = new InstrumentHistory(inst);
                    if (b.Data.Length > 1) { h2.SetBytes(b.Id, b.Data.Length - 1, [(byte)(b.Data[^1] ^ 0x5A)]); Check(h2.Current, $"{b.Id} last byte"); }
                }
            }
        }
        r.Note($"{writes} single edits written and read back");
    }

    // ---- 5. boundaries ----

    public static void Boundaries(ImodelReport r)
    {
        // a sound memory with no samples: no instruments, reads and writes say so
        var empty = new SpcSnapshot(new byte[SpcSnapshot.AramSize], new byte[SpcSnapshot.DspSize]);
        var es = new SnesInstruments(empty);
        r.Expect(es.List().Count == 0 && es.Read(0) == null, "an empty sound memory must list no instrument");
        var snes = new SnesInstruments(ImodelData.SnesWithSamples());
        var s1 = snes.Read(1)!;
        var ghost = SnesInstruments.Template(7, 9); ghost.Blobs[0].Data = new byte[9];
        var res = es.Write(ghost);
        r.Expect(!res.Ok && res.Reason!.Contains("no sample in slot 7"), $"writing to an empty slot: {res.Ok} {res.Reason}");

        // a SNES sample keeps its length: shorter, longer and empty blobs are refused with a reason
        foreach (int len in new[] { 0, 9, 27, 45, 72 })
        {
            var e = s1.Clone();
            e.Blobs[0].Data = new byte[len];
            var w = snes.Write(e);
            r.Expect(!w.Ok && !string.IsNullOrEmpty(w.Reason), $"a {len}-byte blob over a 36-byte sample must be refused with a reason");
        }
        // out-of-range field values are refused, not clamped silently
        var bad = s1.Clone(); bad.Field("env.ar")!.Value = 16;
        var wr = snes.Write(bad);
        r.Expect(!wr.Ok && wr.Reason!.Contains("env.ar"), $"env.ar = 16: {wr.Ok} {wr.Reason}");
        // an instrument of another console or kind
        var gbInst = new GbInstruments(ImodelData.GbWithSettings()).Read(0)!;
        var cross = snes.Write(gbInst);
        r.Expect(!cross.Ok && !string.IsNullOrEmpty(cross.Reason), "a Game Boy instrument written into a SNES snapshot must be refused");
        var wrongKind = new SegaInstruments(ImodelData.OpnWithPatches());
        r.Expect(!wrongKind.Write(new SegaInstruments(ImodelData.OpllWithUser()).Read(0)!).Ok, "a YM2413 instrument written into a Genesis snapshot must be refused");

        // the Genesis DAC: empty and oversized sounds, loops that do not fit
        var opn = new SegaInstruments(ImodelData.OpnWithPatches());
        var d0 = opn.Read(6)!;
        var emptyDac = d0.Clone(); emptyDac.Blobs[0].Data = [];
        r.Expect(!opn.Write(emptyDac).Ok, "an empty DAC sound must be refused");
        var bigDac = d0.Clone(); bigDac.Blobs[0].Data = new byte[DacSample.MaxLength + 1];
        r.Expect(!opn.Write(bigDac).Ok, "a DAC sound over 65,536 samples must be refused");
        var maxDac = d0.Clone(); maxDac.Blobs[0].Data = new byte[DacSample.MaxLength]; Array.Fill(maxDac.Blobs[0].Data, (byte)128);
        maxDac.Field("dac.loop.end")!.Value = 600;
        var wm = opn.Write(maxDac);
        r.Expect(wm.Ok, $"a DAC sound of exactly 65,536 samples must be accepted: {wm.Reason}");
        var oneDac = d0.Clone(); oneDac.Blobs[0].Data = [200]; oneDac.Field("dac.loop")!.Value = 0; oneDac.Field("dac.loop.start")!.Value = 0; oneDac.Field("dac.loop.end")!.Value = 0;
        r.Expect(opn.Write(oneDac).Ok, "a one-sample DAC sound must be accepted");
        var badLoop = d0.Clone(); badLoop.Field("dac.loop.start")!.Value = 590; badLoop.Field("dac.loop.end")!.Value = 700;
        var wl = opn.Write(badLoop);
        r.Expect(!wl.Ok && wl.Reason!.Contains("loop"), $"a loop that ends past the sound must be refused: {wl.Ok} {wl.Reason}");
        var reversed = d0.Clone(); reversed.Field("dac.loop.start")!.Value = 300; reversed.Field("dac.loop.end")!.Value = 200;
        r.Expect(!opn.Write(reversed).Ok, "a loop that ends before it starts must be refused");
        // a ROM instrument cannot be written, but can be copied into the user instrument
        var opll = new SegaInstruments(ImodelData.OpllWithUser());
        var romWrite = opll.Write(opll.Read(3)!.Clone());
        r.Expect(!romWrite.Ok && romWrite.Reason!.Contains("ROM"), $"a ROM instrument write: {romWrite.Ok} {romWrite.Reason}");
        var copied = opll.CopyRomToUser(1)!;
        var u = new SegaInstruments(copied).Read(0)!; var v = new SegaInstruments(copied).Read(1)!;
        bool same = u.Fields.Zip(v.Fields).All(p => p.First.Value == p.Second.Value);
        r.Expect(same, "CopyRomToUser: the user instrument must equal ROM instrument 1 field for field");
        r.Expect(opll.CopyRomToUser(0) == null && opll.CopyRomToUser(16) == null && new SegaInstruments(ImodelData.PsgOnly()).CopyRomToUser(1) == null, "CopyRomToUser must refuse 0, 16 and a snapshot with no YM2413");

        // the Game Boy wave RAM is exactly 16 bytes
        var gbs = new GbInstruments(ImodelData.GbWithSettings());
        var wave = gbs.Read(2)!.Clone();
        foreach (int len in new[] { 0, 15, 17 }) { wave.Blobs[0].Data = new byte[len]; r.Expect(!gbs.Write(wave).Ok, $"a {len}-byte wave RAM must be refused"); }
        // the NES DMC sample: empty is fine, 4081 is the most
        var nes = new NesInstruments(ImodelFixtures.NesSynthetic());
        var dmc = nes.Read(4)!.Clone();
        dmc.Blobs[0].Data = []; r.Expect(nes.Write(dmc).Ok, "an empty DMC sample is allowed");
        dmc.Blobs[0].Data = new byte[NesSnapshot.MaxDmc]; r.Expect(nes.Write(dmc).Ok, "a 4081-byte DMC sample is allowed");
        dmc.Blobs[0].Data = new byte[NesSnapshot.MaxDmc + 1]; r.Expect(!nes.Write(dmc).Ok, "a 4082-byte DMC sample must be refused");

        // a codec never reads past its input
        r.Expect(InstrumentCodec.Unpack([], out string? pr) == null && pr != null, "Unpack of nothing");
        r.Expect(InstrumentHistory.Deserialize([], out string? ph) == null && ph != null, "Deserialize of nothing");
        r.Expect(SnesOverlay.FromBytes([], out string? po) == null && po != null, "SnesOverlay.FromBytes of nothing");

        // operations at their limits
        var h = new InstrumentHistory(s1);
        var none = h.Randomise(0, 5);
        r.Expect(!none.Changed && h.Cursor == 1, $"strength 0 must change nothing but is still recorded: changed {none.Changed}");
        var over = h.Randomise(7.5, 5);          // clamps to 1
        r.Expect(h.Operations[1].A == 1000, $"strength 7.5 must be stored as 1000 permille, was {h.Operations[1].A}");
        var unknown = h.Randomise(0.5, 1, target: "nope");
        r.Expect(!unknown.Changed && unknown.Summary.Contains("nope"), "an unknown target must say so");
        r.Expect(!h.SetBytes("sample", 30, new byte[10]).Changed, "bytes past the end of a blob must be refused");
        r.Expect(!h.SetBytes("sample", -1, new byte[1]).Changed, "a negative offset must be refused");
        h.Set("env.ar", 99); r.Expect(h.Current.Get("env.ar") == 15, "Set clamps to the field's range");
        h.Set("env.ar", -5); r.Expect(h.Current.Get("env.ar") == 0, "Set clamps to the field's range (low)");
        var tiny = new InstrumentHistory(ghost);          // a one-block sample, all zero
        var tr = tiny.Randomise(1.0, 3);
        r.Expect(tr.Changed && tiny.Current.Blobs[0].Data.Length == 9, "a one-block sample can be randomised and keeps its length");
        var full = new InstrumentHistory(s1);
        for (int i = 0; i < InstrumentHistory.MaxOperations; i++) full.Set("env.ar", i & 15);
        var overflow = full.Set("env.ar", 3);
        r.Expect(!overflow.Changed && overflow.Summary.Contains("full") && full.Cursor == InstrumentHistory.MaxOperations, "a full history must refuse more operations and say so");
        r.Expect(InstrumentHistory.Deserialize(full.Serialize(), out _) != null, "a full history must serialise and read back");
    }
}
