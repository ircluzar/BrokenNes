// Round 6 (imodel): checks of the corruptor: the PRNG against an independent reference, seed determinism, locks, history, the saved forms and their damage, BRR structure.
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

internal static partial class ImodelChecks
{
    /// <summary>One instrument of every kind that has something to corrupt, from the made-up snapshots.</summary>
    internal static List<(string Name, Instrument Inst)> CorruptionTargets()
    {
        var list = new List<(string, Instrument)>();
        void Add(string name, object snap, int index) => list.Add((name, Src(snap).Read(index)!));
        var snes = ImodelData.SnesWithSamples();
        Add("snes-slot0", snes, 0); Add("snes-slot1", snes, 1); Add("snes-slot2", snes, 2);
        var opn = ImodelData.OpnWithPatches();
        Add("opn-patch0", opn, 0); Add("opn-patch2", opn, 2); Add("opn-dac-loop", opn, 6); Add("opn-dac-oneshot", opn, 7); Add("opn-psg", opn, opn.PsgInstrument);
        Add("opll-user", ImodelData.OpllWithUser(), 0);
        var gb = ImodelData.GbWithSettings();
        Add("gb-pulse1", gb, 0); Add("gb-wave", gb, 2); Add("gb-noise", gb, 3);
        var nes = ImodelFixtures.NesSynthetic();
        Add("nes-pulse1", nes, 0); Add("nes-dmc", nes, 4);
        return list;
    }

    /// <summary>Every field inside its range, every blob inside its length limits, a DAC loop inside its sound.</summary>
    internal static string? Illegal(Instrument i)
    {
        foreach (var f in i.Fields) if (f.Value < f.Min || f.Value > f.Max) return $"{f.Id} = {f.Value} outside {f.Min}..{f.Max}";
        foreach (var b in i.Blobs) if (b.Data.Length < b.MinLength || b.Data.Length > b.MaxLength) return $"{b.Id} length {b.Data.Length} outside {b.MinLength}..{b.MaxLength}";
        if (i.Kind == InstrumentKind.OpnDac && i.Get("dac.loop") != 0 && !(i.Get("dac.loop.start") < i.Get("dac.loop.end") && i.Get("dac.loop.end") <= i.Blobs[0].Data.Length)) return "DAC loop outside its sound";
        return null;
    }

    internal static ulong Fnv(byte[] b) { ulong h = 14695981039346656037UL; foreach (byte x in b) h = (h ^ x) * 1099511628211UL; return h; }

    // ---- the PRNG against vectors computed by an independent implementation (a script written from the published algorithms: docs/projects/round6-imodel.md names it) ----

    public static void Prng_(ImodelReport r)
    {
        (ulong seed, ulong[] outs)[] vectors =
        [
            (0, [0x99EC5F36CB75F2B4UL, 0xBF6E1F784956452AUL, 0x1A5F849D4933E6E0UL, 0x6AA594F1262D2D2CUL]),
            (1, [0xB3F2AF6D0FC710C5UL, 0x853B559647364CEAUL, 0x92F89756082A4514UL, 0x642E1C7BC266A3A7UL]),
            (12345, [0xBE6A36374160D49BUL, 0x214AAA0637A688C6UL, 0xF69D16DE9954D388UL, 0x0C60048C4E96E033UL]),
            (0xDEADBEEFCAFEBABEUL, [0x2299B3421AA78B66UL, 0x9AF6550483E02887UL, 0xDA059CB5EEC5C979UL, 0xCF86BD29360FFF98UL]),
        ];
        foreach (var (seed, outs) in vectors)
        {
            var g = new Prng(seed);
            for (int i = 0; i < outs.Length; i++) { ulong v = g.NextU64(); r.Expect(v == outs[i], $"Prng({seed:X}) output {i}: {v:X16}, the reference says {outs[i]:X16}"); }
        }
        (ulong seed, uint n, uint[] outs)[] below =
        [
            (7, 13, [9, 3, 10, 12, 12, 11, 0, 1]), (99, 1000, [348, 564, 378, 855, 785, 205, 277, 58]), (2024, 3, [0, 2, 0, 0, 2, 0, 1, 0]),
            (5, 1u << 31, [619358396, 1292961965, 1394890982, 1764265744, 1109634680, 1684752358, 1082095117, 1736587552]),
        ];
        foreach (var (seed, n, outs) in below)
        {
            var g = new Prng(seed);
            for (int i = 0; i < outs.Length; i++) { uint v = g.Below(n); r.Expect(v == outs[i], $"Prng({seed}).Below({n}) draw {i}: {v}, the reference says {outs[i]}"); }
        }
        // uniformity: 60,000 draws of Below(6) land within 3% of even
        var u = new Prng(31337); var hist = new int[6];
        for (int i = 0; i < 60000; i++) hist[u.Below(6)]++;
        r.Expect(hist.All(c => Math.Abs(c - 10000) < 300), $"Below(6) is not uniform: {string.Join(",", hist)}");
        r.Note("PRNG: 16 xoshiro256** outputs and 32 bounded draws match the independent reference");
    }

    // ---- seed determinism ----

    private static byte[] Script(Instrument clean, ulong seed, bool keep = true)
    {
        var h = new InstrumentHistory(clean);
        h.Randomise(0.5, seed, keep);
        h.Nudge("", 1, seed + 1, 1, keep);
        h.FlipBits("", 3, seed + 2, keep);
        h.Randomise(1.0, seed + 3, false);
        h.Nudge("", -2, seed + 4, 3, keep);
        return h.FinalBytes();
    }

    /// <summary>Recorded once, from this code on this machine; the check compares what it computes now against them, so a change in what a seed does is noticed, and a different machine must agree.</summary>
    internal static readonly Dictionary<string, ulong> GoldenScripts = new()
    {
        ["snes-slot1"] = 0x3C42F9879994FDDE, ["opn-patch0"] = 0x61F48D43732B9A1F, ["opn-dac-loop"] = 0xDA37E8C0C4F2A452, ["gb-wave"] = 0xEE7CE967FDA1E45F, ["opll-user"] = 0x2F02F3F6B46FD9A2,
    };

    public static void Determinism(ImodelReport r)
    {
        foreach (var (name, inst) in CorruptionTargets())
        {
            var a = Script(inst, 1000);
            var b = Script(inst, 1000);
            r.Expect(a.AsSpan().SequenceEqual(b), $"{name}: the same seeds gave two different instruments");
            var distinct = new HashSet<ulong>();
            for (ulong s = 1; s <= 16; s++) distinct.Add(Fnv(Script(inst, s * 7919)));
            r.Expect(distinct.Count >= 15, $"{name}: 16 different seeds gave only {distinct.Count} different instruments");
            // each tool alone also depends on its seed
            foreach (var (tool, make) in new (string, Func<ulong, InstrumentOp>)[]
            {
                ("randomise", s => new InstrumentOp(OpKind.Randomise, "", 400, 0, s, InstrumentOp.KeepStructure, null)),
                ("nudge", s => new InstrumentOp(OpKind.Nudge, "", 1, 2, s, InstrumentOp.KeepStructure, null)),
                ("flip", s => new InstrumentOp(OpKind.FlipBits, "", 2, 0, s, InstrumentOp.KeepStructure, null)),
            })
            {
                var seen = new HashSet<ulong>();
                for (ulong s = 1; s <= 24; s++)
                {
                    var st = new EditState(inst.Clone());
                    InstrumentCorruptor.Apply(st, make(s * 104729));
                    seen.Add(Fnv(InstrumentCodec.Pack(st.Inst)));
                }
                r.Expect(seen.Count >= 4, $"{name}: {tool} with 24 different seeds gave only {seen.Count} results");
                var s1 = new EditState(inst.Clone()); var s2 = new EditState(inst.Clone());
                InstrumentCorruptor.Apply(s1, make(55)); InstrumentCorruptor.Apply(s2, make(55));
                r.Expect(InstrumentCodec.Pack(s1.Inst).AsSpan().SequenceEqual(InstrumentCodec.Pack(s2.Inst)), $"{name}: {tool} twice with the same seed differs");
                r.Expect(Illegal(s1.Inst) == null, $"{name}: {tool} left an illegal instrument: {Illegal(s1.Inst)}");
            }
        }
        // golden: fixed scripts on fixed instruments give recorded bytes
        var snes = ImodelData.SnesWithSamples();
        var targets = new (string, Instrument)[] { ("snes-slot1", Src(snes).Read(1)!), ("opn-patch0", Src(ImodelData.OpnWithPatches()).Read(0)!), ("opn-dac-loop", Src(ImodelData.OpnWithPatches()).Read(6)!), ("gb-wave", Src(ImodelData.GbWithSettings()).Read(2)!), ("opll-user", Src(ImodelData.OpllWithUser()).Read(0)!) };
        foreach (var (name, inst) in targets)
        {
            ulong now = Fnv(Script(inst, 20261017));
            if (GoldenScripts.TryGetValue(name, out ulong want)) r.Expect(now == want, $"golden {name}: the script now gives {now:X16}, recorded {want:X16}: the same seeds no longer give the same corruption");
            else r.Expect(false, $"golden {name} is not recorded: {now:X16}");
        }
    }

    // ---- locks ----

    private static void Rain(InstrumentHistory h, int n, ulong salt)
    {
        for (int i = 1; i <= n; i++)
        {
            ulong seed = salt * 1000 + (ulong)i;
            switch (i % 4)
            {
                case 0: h.Randomise(((i * 37) % 1000 + 1) / 1000.0, seed, i % 8 != 0); break;
                case 1: h.Nudge("", (i % 3 + 1) * (i % 2 == 0 ? 1 : -1), seed, 1 + i % 4, i % 8 != 1); break;
                case 2: h.FlipBits("", 1 + i % 4, seed, i % 8 != 2); break;
                default: h.Randomise(1.0, seed, i % 8 != 3); break;
            }
        }
    }

    public static void Locks(ImodelReport r)
    {
        int ops = 0;
        foreach (var (name, inst) in CorruptionTargets())
        {
            // A: half the fields and every blob locked; the rest may change, the locked never
            var h = new InstrumentHistory(inst);
            var locked = new List<string>();
            for (int i = 0; i < inst.Fields.Length; i += 2) locked.Add(inst.Fields[i].Id);
            foreach (var b in inst.Blobs) locked.Add(b.Id);
            foreach (var id in locked) h.Lock(id);
            Rain(h, 60, 1); ops += 60;
            foreach (var id in locked)
            {
                var f = inst.Field(id);
                if (f != null) r.Expect(h.Current.Field(id)!.Value == f.Value, $"{name}: locked field {id} changed from {f.Value} to {h.Current.Field(id)!.Value}");
                var b = inst.Blob(id);
                if (b != null) r.Expect(h.Current.Blob(id)!.Data.AsSpan().SequenceEqual(b.Data), $"{name}: locked blob {id} changed");
            }
            bool anyFree = inst.Fields.Length > locked.Count - inst.Blobs.Length;
            if (anyFree) r.Expect(!h.Current.SameValues(inst), $"{name}: 60 random operations changed nothing although some fields were free");
            r.Expect(Illegal(h.Current) == null, $"{name}: illegal after locked operations: {Illegal(h.Current)}");
            r.Expect(h.Locks.Count == locked.Count, $"{name}: {h.Locks.Count} locks recorded, {locked.Count} set");

            // B: everything locked: nothing changes, and every tool says so
            var all = new InstrumentHistory(inst);
            foreach (var f in inst.Fields) all.Lock(f.Id);
            foreach (var b in inst.Blobs) all.Lock(b.Id);
            Rain(all, 30, 2); ops += 30;
            r.Expect(all.Current.SameValues(inst), $"{name}: an instrument with everything locked changed");
            var says = all.Randomise(0.7, 9);
            r.Expect(!says.Changed && says.Summary.Contains("locked"), $"{name}: a fully locked randomise must say so, said '{says.Summary}'");

            // C: the tools aimed straight at a locked id do nothing
            foreach (var id in inst.Fields.Select(f => f.Id).Concat(inst.Blobs.Select(b => b.Id)).Take(4))
            {
                r.Expect(!all.Randomise(1.0, 3, target: id).Changed, $"{name}: randomise aimed at locked {id} changed it");
                r.Expect(!all.Nudge(id, 1, 4).Changed, $"{name}: nudge aimed at locked {id} changed it");
                r.Expect(!all.FlipBits(id, 2, 5).Changed, $"{name}: flip aimed at locked {id} changed it");
            }
            // D: unlocking gives the tools their power back; the manual edit ignores locks
            foreach (var f in inst.Fields) all.Unlock(f.Id);
            foreach (var b in inst.Blobs) all.Unlock(b.Id);
            Rain(all, 30, 3); ops += 30;
            r.Expect(!all.Current.SameValues(inst), $"{name}: after unlocking everything 30 operations changed nothing");
            var lockedSet = new InstrumentHistory(inst);
            lockedSet.Lock(inst.Fields[0].Id);
            int target = inst.Fields[0].Value == inst.Fields[0].Max ? inst.Fields[0].Min : inst.Fields[0].Max;
            lockedSet.Set(inst.Fields[0].Id, target);
            r.Expect(lockedSet.Current.Fields[0].Value == target || inst.Kind == InstrumentKind.OpnDac, $"{name}: a manual Set must work on a locked field (locks protect against the random tools only)");
        }
        r.Note($"{ops} random operations across {CorruptionTargets().Count} instruments honoured their locks");
    }

    // ---- history: undo, redo, replay ----

    private static bool SameOp(InstrumentOp a, InstrumentOp b) => a.Kind == b.Kind && a.Target == b.Target && a.A == b.A && a.B == b.B && a.Seed == b.Seed && a.Flags == b.Flags && (a.Data ?? []).AsSpan().SequenceEqual(b.Data ?? []);

    public static void History(ImodelReport r)
    {
        int steps = 0;
        foreach (var (name, inst) in CorruptionTargets())
        {
            var h = new InstrumentHistory(inst);
            var rng = new Prng(Fnv(System.Text.Encoding.UTF8.GetBytes(name)));
            var ids = inst.Fields.Select(f => f.Id).Concat(inst.Blobs.Select(b => b.Id)).ToArray();
            for (int step = 0; step < 70; step++)
            {
                int pick = (int)rng.Below(100);
                int count0 = h.Operations.Count, cur0 = h.Cursor;
                if (pick < 55)
                {
                    ulong seed = rng.NextU64();
                    switch (rng.Below(5))
                    {
                        case 0: h.Randomise(rng.Below(1001) / 1000.0, seed, rng.Below(2) == 0); break;
                        case 1: h.Nudge(rng.Below(2) == 0 ? "" : ids[rng.Below((uint)ids.Length)], rng.Between(-3, 3), seed, rng.Between(1, 4), rng.Below(2) == 0); break;
                        case 2: h.FlipBits(rng.Below(2) == 0 ? "" : ids[rng.Below((uint)ids.Length)], rng.Between(1, 5), seed, rng.Below(2) == 0); break;
                        case 3: h.Set(inst.Fields[rng.Below((uint)inst.Fields.Length)].Id, rng.Between(-5, 300)); break;
                        default: if (inst.Blobs.Length > 0) h.SetBytes(inst.Blobs[0].Id, 0, [(byte)rng.Below(256)]); else h.Lock(ids[rng.Below((uint)ids.Length)]); break;
                    }
                    r.Expect(h.Cursor == cur0 + 1 && h.Operations.Count == cur0 + 1, $"{name}: a new operation must drop what could have been redone (cursor {cur0} -> {h.Cursor}, {h.Operations.Count} operations)");
                }
                else if (pick < 70) h.Undo();
                else if (pick < 80) h.Redo();
                else if (pick < 85) h.GoTo((int)rng.Below((uint)h.Operations.Count + 1));
                else { string id = ids[rng.Below((uint)ids.Length)]; if (rng.Below(2) == 0) h.Lock(id); else h.Unlock(id); }
                steps++;
                // the invariant: the instrument and the locks equal a replay from the clean one
                var replay = h.Replay(h.Cursor);
                r.Expect(InstrumentCodec.Pack(replay).AsSpan().SequenceEqual(h.FinalBytes()), $"{name} step {step}: the instrument at cursor {h.Cursor} differs from a replay from the clean instrument");
                r.Expect(h.ReplayLocks(h.Cursor).SetEquals(h.Locks), $"{name} step {step}: the locks differ from a replay");
                r.Expect(h.CanUndo == h.Cursor > 0 && h.CanRedo == h.Cursor < h.Operations.Count, $"{name} step {step}: CanUndo / CanRedo wrong");
                r.Expect(Illegal(h.Current) == null, $"{name} step {step}: illegal instrument {Illegal(h.Current)}");
                r.Expect(h.Clean.SameValues(inst), $"{name} step {step}: the clean instrument moved");
                if (step % 17 == 0)
                {
                    var back = InstrumentHistory.Deserialize(h.Serialize(), out string? problem);
                    r.Expect(back != null, $"{name} step {step}: a history did not read back: {problem}");
                    if (back != null)
                    {
                        r.Expect(back.ReplayMatchesSaved && back.Cursor == h.Cursor && back.Operations.Count == h.Operations.Count && back.Operations.Zip(h.Operations).All(p => SameOp(p.First, p.Second)), $"{name} step {step}: the saved operations, cursor or replay differ");
                        r.Expect(back.FinalBytes().AsSpan().SequenceEqual(h.FinalBytes()), $"{name} step {step}: the saved final bytes differ");
                        r.Expect(back.Locks.SetEquals(h.Locks), $"{name} step {step}: the saved locks differ");
                    }
                }
            }
            // all the way back is the clean instrument; all the way forward is where it was
            string finalHex = Convert.ToHexString(h.FinalBytes());
            int end = h.Cursor;
            h.GoTo(h.Operations.Count);
            string fullHex = Convert.ToHexString(h.FinalBytes());
            while (h.Undo()) { }
            r.Expect(h.Cursor == 0 && h.Current.SameValues(inst) && h.Locks.Count == 0, $"{name}: undoing everything must give back the clean instrument and no locks");
            while (h.Redo()) { }
            r.Expect(Convert.ToHexString(h.FinalBytes()) == fullHex, $"{name}: redoing everything must give back the instrument as it was at the end");
            h.GoTo(end);
            r.Expect(Convert.ToHexString(h.FinalBytes()) == finalHex, $"{name}: going back to cursor {end} must give back the instrument as it was there");
        }
        r.Note($"{steps} history steps; at each the instrument and locks equal a replay from the clean instrument");
    }

    // ---- the snapshot depends on the instrument's state, not on how it got there: undo is bit-exact in the sound memory ----

    public static void Restore(ImodelReport r, IEnumerable<ImodelFixture> fixtures, int perFixture)
    {
        int walks = 0, steps = 0;
        foreach (var fx in fixtures)
        {
            var src = Src(fx.Snapshot);
            var original = ImodelFixtures.Bytes(fx.Snapshot);
            foreach (var inst in src.List().Where(i => i.Editable).Take(perFixture))
            {
                var h = new InstrumentHistory(inst);
                var rng = new Prng(Fnv(System.Text.Encoding.UTF8.GetBytes(fx.Name)) ^ (ulong)inst.Index);
                object cur = fx.Snapshot;
                string where = $"{fx.Name} {inst.Name}";
                for (int k = 0; k < 10; k++)
                {
                    ulong seed = rng.NextU64();
                    switch (rng.Below(6))
                    {
                        case 0: h.Randomise(rng.Below(1001) / 1000.0, seed, rng.Below(2) == 0); break;
                        case 1: h.Nudge("", rng.Between(-3, 3), seed, rng.Between(1, 3), rng.Below(2) == 0); break;
                        case 2: h.FlipBits("", rng.Between(1, 4), seed, rng.Below(2) == 0); break;
                        case 3: h.Set(inst.Fields[rng.Below((uint)inst.Fields.Length)].Id, rng.Between(-3, 200)); break;
                        case 4: h.Lock(inst.Fields[rng.Below((uint)inst.Fields.Length)].Id); break;
                        default: h.Randomise(0.3, seed, true); break;
                    }
                    var w = Src(cur).Write(h.Current);
                    if (!w.Ok) { r.Expect(false, $"{where} step {k}: the write was refused: {w.Reason}"); break; }
                    cur = w.Snapshot!;
                    var direct = src.Write(h.Current);
                    r.Expect(direct.Ok && ImodelFixtures.Bytes(direct.Snapshot!).AsSpan().SequenceEqual(ImodelFixtures.Bytes(cur)), $"{where} step {k}: the sound memory depends on the path the instrument took (writing it from the clean memory gives other bytes)");
                    steps++;
                }
                h.GoTo(0);
                var back = Src(cur).Write(h.Current);
                r.Expect(back.Ok && ImodelFixtures.Bytes(back.Snapshot!).AsSpan().SequenceEqual(original), $"{where}: after ten operations and an undo of all of them the sound memory is not byte for byte the original");
                walks++;
            }
        }
        r.Note($"{walks} instruments walked through ten operations each and back to the clean state: {steps} writes equal the direct write, {walks} undone writes equal the original memory");
    }

    // ---- the saved forms ----

    private static byte[] WithCrc(byte[] body)
    {
        var all = new byte[body.Length + 4];
        body.CopyTo(all, 0);
        uint crc = Crc(body);
        all[^4] = (byte)crc; all[^3] = (byte)(crc >> 8); all[^2] = (byte)(crc >> 16); all[^1] = (byte)(crc >> 24);
        return all;
    }

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
        return ~crc;
    }

    public static void Codec(ImodelReport r)
    {
        var targets = CorruptionTargets();
        var every = ImodelFixtures.Synthetic().SelectMany(fx => Src(fx.Snapshot).List().Select(i => (fx.Name, Inst: i))).ToList();
        foreach (var (name, inst) in every)
        {
            var packed = InstrumentCodec.Pack(inst);
            var back = InstrumentCodec.Unpack(packed, out string? problem);
            r.Expect(back != null && back.SameValues(inst) && back.Origin == inst.Origin && back.Console == inst.Console && back.Kind == inst.Kind && back.Index == inst.Index, $"{name} {inst.Name}: pack / unpack did not give the instrument back ({problem})");
            r.Expect(packed.AsSpan().SequenceEqual(InstrumentCodec.Pack(inst)), $"{name} {inst.Name}: pack is not deterministic");
        }
        r.Note($"{every.Count} instruments pack and unpack to themselves");

        // damage: every truncation, and every single flipped bit of a small instrument (a sample of bit positions of a big one), is refused with a reason and never throws
        int refused = 0, tried = 0;
        foreach (var (name, inst) in targets.Where(t => t.Name is "opn-patch0" or "gb-wave" or "snes-slot2" or "nes-dmc" or "opll-user" or "opn-psg"))
        {
            var packed = InstrumentCodec.Pack(inst);
            for (int len = 0; len < packed.Length; len++)
            {
                tried++;
                var cut = InstrumentCodec.Unpack(packed[..len], out string? why);
                if (cut == null && !string.IsNullOrEmpty(why)) refused++; else r.Expect(false, $"{name}: truncated to {len} of {packed.Length} bytes was accepted ({why})");
            }
            for (int bit = 0; bit < packed.Length * 8; bit += packed.Length > 400 ? 7 : 1)
            {
                var d = (byte[])packed.Clone();
                d[bit / 8] ^= (byte)(1 << bit % 8);
                tried++;
                var flipped = InstrumentCodec.Unpack(d, out string? why);
                if (flipped == null && !string.IsNullOrEmpty(why)) refused++; else r.Expect(false, $"{name}: flipping bit {bit} of {packed.Length * 8} was accepted");
            }
        }
        r.Note($"{refused} of {tried} damaged instruments refused with a reason");
        // damage that keeps the checksum valid is refused by the schema
        var sample = InstrumentCodec.Pack(targets.First(t => t.Name == "opn-patch0").Inst);
        var newer = (byte[])sample.Clone(); newer[3] = 2;
        var rn = InstrumentCodec.Unpack(WithCrc(newer[..^4]), out string? pn);
        r.Expect(rn == null && pn!.Contains("newer"), $"a newer version must be refused as such: {pn}");
        var unknown = (byte[])sample.Clone(); unknown[5] = 200;
        r.Expect(InstrumentCodec.Unpack(WithCrc(unknown[..^4]), out _) == null, "an unknown kind must be refused");
        var wrongConsole = (byte[])sample.Clone(); wrongConsole[4] = (byte)ConsoleId.GameBoy;
        r.Expect(InstrumentCodec.Unpack(WithCrc(wrongConsole[..^4]), out _) == null, "a kind on the wrong console must be refused");
        var outOfRangeInst = targets.First(t => t.Name == "opn-patch0").Inst.Clone();
        outOfRangeInst.Field("alg")!.Value = 200;                                                     // Pack does not judge; the reader does
        var ro = InstrumentCodec.Unpack(InstrumentCodec.Pack(outOfRangeInst), out string? po);
        r.Expect(ro == null && po!.Contains("outside"), $"a value outside its range (with a valid checksum) must be refused: {po}");
        var longBlob = targets.First(t => t.Name == "gb-wave").Inst.Clone();
        longBlob.Blobs[0].Data = new byte[17];
        r.Expect(InstrumentCodec.Unpack(InstrumentCodec.Pack(longBlob), out string? pb) == null && pb!.Contains("bytes"), $"a blob of the wrong length (with a valid checksum) must be refused: {pb}");
        var endsEarly = new List<byte>(sample[..16]);                                                 // the header and one field, then the end
        r.Expect(InstrumentCodec.Unpack(WithCrc(endsEarly.ToArray()), out _) == null, "a stream that ends inside the fields must be refused");

        // histories
        var h = new InstrumentHistory(targets.First(t => t.Name == "snes-slot1").Inst);
        Rain(h, 25, 9);
        var bytes = h.Serialize();
        r.Expect(InstrumentHistory.Deserialize(bytes, out _) != null, "a history reads back");
        int hRefused = 0, hTried = 0;
        for (int len = 0; len < bytes.Length; len += 3) { hTried++; if (InstrumentHistory.Deserialize(bytes[..len], out string? why) == null && why != null) hRefused++; else r.Expect(false, $"history truncated to {len} bytes was accepted"); }
        for (int bit = 0; bit < bytes.Length * 8; bit += 13) { var d = (byte[])bytes.Clone(); d[bit / 8] ^= (byte)(1 << bit % 8); hTried++; if (InstrumentHistory.Deserialize(d, out string? why) == null && why != null) hRefused++; else r.Expect(false, $"history with bit {bit} flipped was accepted"); }
        r.Note($"{hRefused} of {hTried} damaged histories refused");
        var hNewer = (byte[])bytes.Clone(); hNewer[4] = 9;
        var hn = InstrumentHistory.Deserialize(WithCrc(hNewer[..^4]), out string? hnp);
        r.Expect(hn == null && hnp!.Contains("newer"), $"a newer history must be refused as such: {hnp}");
        // the corruptor changed since it was saved: the saved final bytes are what counts, and the history says the replay no longer matches
        var single = new InstrumentHistory(targets.First(t => t.Name == "opn-patch0").Inst);
        single.Randomise(0.9, 31337);                                   // one operation, so changing its seed must change the replay
        var sbytes = single.Serialize();
        var tampered = (byte[])sbytes.Clone();
        tampered[FindFirstOpSeed(sbytes)] ^= 0x55;                      // another seed, and the checksum made valid again
        var td = InstrumentHistory.Deserialize(WithCrc(tampered[..^4]), out string? tp);
        r.Expect(td != null && !td.ReplayMatchesSaved && td.FinalBytes().AsSpan().SequenceEqual(single.FinalBytes()), $"a history whose replay no longer matches must keep its saved final bytes and say so (reason: {tp}, matches saved: {td?.ReplayMatchesSaved})");

        // the SNES overlay
        var ov = new SnesOverlay([new SnesOverlay.Entry { Srcn = 3, Flags = SnesOverlay.HasEnv | SnesOverlay.HasTune | SnesOverlay.Pinned, Adsr1 = 0x8F, Adsr2 = 0xE0, Gain = 0x7F, Tune = -250, Blocks = 12 },
            new SnesOverlay.Entry { Srcn = 200, Flags = SnesOverlay.HasVol | SnesOverlay.HasMix, WeightL = -64, WeightR = 127, Mix = 5 }]);
        var ob = ov.ToBytes();
        var oback = SnesOverlay.FromBytes(ob, out string? op);
        r.Expect(oback != null && oback.Count == 2 && oback.ToBytes().AsSpan().SequenceEqual(ob), $"an overlay reads back: {op}");
        int oRefused = 0, oTried = 0;
        for (int len = 0; len < ob.Length; len++) { oTried++; if (SnesOverlay.FromBytes(ob[..len], out string? why) == null && why != null) oRefused++; else r.Expect(false, $"overlay truncated to {len} was accepted"); }
        for (int bit = 0; bit < ob.Length * 8; bit++) { var d = (byte[])ob.Clone(); d[bit / 8] ^= (byte)(1 << bit % 8); oTried++; if (SnesOverlay.FromBytes(d, out string? why) == null && why != null) oRefused++; else r.Expect(false, $"overlay with bit {bit} flipped was accepted"); }
        r.Note($"{oRefused} of {oTried} damaged overlays refused");
        var oRange = (byte[])ob.Clone(); oRange[3 + 6] = 0xFF; oRange[3 + 7] = 0x7F;               // a tuning of 32767 cents, checksum fixed
        r.Expect(SnesOverlay.FromBytes(WithCrc(oRange[..^4]), out _) == null, "an overlay tuning out of range must be refused");
    }

    /// <summary>The offset of the first operation's seed in a serialised history (header, two packed instruments, counts, then kind, flags, A, B, seed).</summary>
    private static int FindFirstOpSeed(byte[] b)
    {
        int pos = 4 + 1 + 1;
        int cl = BitConverter.ToInt32(b, pos); pos += 4 + cl;
        int fl = BitConverter.ToInt32(b, pos); pos += 4 + fl;
        pos += 4;                              // count, cursor
        return pos + 1 + 1 + 4 + 4;            // kind, flags, A, B
    }
}
