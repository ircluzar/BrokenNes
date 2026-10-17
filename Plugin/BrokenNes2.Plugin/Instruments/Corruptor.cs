using System.Text;

namespace BrokenNes2.Instruments;

// =====================================================================================================================================================================
// The instrument corruptor: pure, deterministic operations on an Instrument, a seeded PRNG stored with each, field locks, and a history (undo, redo, replay from the clean instrument).
//
// Determinism: every decision is an integer comparison on numbers drawn from one Prng seeded by the operation's own seed; strength is an integer in permille; there is no floating point and no clock anywhere in here.
// So (clean instrument, operation list with seeds) gives the same final bytes on any machine, and the final bytes are stored too (InstrumentCodec), so a saved corruption does not depend on this code staying the same.
// =====================================================================================================================================================================

public enum OpKind : byte
{
    /// <summary>Re-roll fields and blob units; A = strength 0..1000 (permille); Target "" = everything unlocked, or one field / blob id.</summary>
    Randomise = 1,
    /// <summary>Move a value by a little; A = step (signed, in the field's units), B = how many units of a blob (default 1); Target "" = a random unlocked field or blob.</summary>
    Nudge = 2,
    /// <summary>Flip A random bits of a field (inside its width) or a blob; Target "" = a random unlocked field or blob.</summary>
    FlipBits = 3,
    /// <summary>A manual edit: field Target := A (clamped to its range). Ignores locks (a lock protects against the random tools only).</summary>
    Set = 4,
    /// <summary>A manual edit of a blob: Data written at offset A of blob Target. Ignores locks.</summary>
    SetBytes = 5,
    Lock = 6,
    Unlock = 7,
}

/// <summary>One recorded operation: everything needed to apply it again.</summary>
public readonly record struct InstrumentOp(OpKind Kind, string Target, int A, int B, ulong Seed, int Flags, byte[]? Data)
{
    /// <summary>Keep the structure valid (BRR: header ranges 0-12, the end flag only on the last block, the loop pointer on a block boundary inside the sample). Without it a high strength may break the structure on purpose.</summary>
    public const int KeepStructure = 1;
    /// <summary>On a BRR blob: work on the block headers only / the sample nibbles only (neither: both).</summary>
    public const int PartHeaders = 2, PartNibbles = 4;

    public bool Keep => (Flags & KeepStructure) != 0;

    public override string ToString() => Kind switch
    {
        OpKind.Randomise => $"randomise {(Target == "" ? "all" : Target)} strength {A / 10.0:0.#}% seed {Seed:X}{(Keep ? " keep-structure" : "")}",
        OpKind.Nudge => $"nudge {(Target == "" ? "random" : Target)} by {A} x{Math.Max(1, B)} seed {Seed:X}",
        OpKind.FlipBits => $"flip {A} bits of {(Target == "" ? "random" : Target)} seed {Seed:X}",
        OpKind.Set => $"set {Target} = {A}",
        OpKind.SetBytes => $"set {Target}[{A}..{A + (Data?.Length ?? 0)}]",
        OpKind.Lock => $"lock {Target}",
        _ => $"unlock {Target}",
    };
}

/// <summary>What an operation did, for the UI to show.</summary>
public readonly record struct OpResult(bool Changed, string Summary);

/// <summary>An instrument being edited, and the ids that are locked against the random tools.</summary>
public sealed class EditState
{
    public Instrument Inst;
    public readonly HashSet<string> Locks = new();
    public EditState(Instrument inst) { Inst = inst; }
    public EditState Clone() { var c = new EditState(Inst.Clone()); foreach (var l in Locks) c.Locks.Add(l); return c; }
}

public static class InstrumentCorruptor
{
    /// <summary>Applies one operation to the state. Never throws for a well-formed operation; an operation that cannot do anything (a locked target, an unknown id) says so in its result.</summary>
    public static OpResult Apply(EditState st, in InstrumentOp op)
    {
        var inst = st.Inst;
        var before = Fingerprint(inst);
        int loopBefore = inst.Kind == InstrumentKind.SnesSample ? inst.Get("loop.addr") : 0;
        OpResult r = op.Kind switch
        {
            OpKind.Randomise => Randomise(st, op),
            OpKind.Nudge => Nudge(st, op),
            OpKind.FlipBits => FlipBits(st, op),
            OpKind.Set => SetField(st, op),
            OpKind.SetBytes => SetBytes(st, op),
            OpKind.Lock => LockOp(st, op, true),
            OpKind.Unlock => LockOp(st, op, false),
            _ => new OpResult(false, "unknown operation"),
        };
        if (op.Kind is OpKind.Randomise or OpKind.Nudge or OpKind.FlipBits or OpKind.Set)
        {
            bool loopTouched = inst.Kind == InstrumentKind.SnesSample && inst.Get("loop.addr") != loopBefore;
            InstrumentRules.Legalise(st, op.Keep && op.Kind != OpKind.Set, loopTouched, LockedFn);
        }
        if (op.Kind is not (OpKind.Lock or OpKind.Unlock)) r = r with { Changed = Fingerprint(inst) != before };
        return r;
    }

    private static ulong Fingerprint(Instrument i)
    {
        ulong h = 1469598103934665603UL;
        foreach (var f in i.Fields) h = (h ^ (uint)f.Value) * 1099511628211UL;
        foreach (var b in i.Blobs) { foreach (byte x in b.Data) h = (h ^ x) * 1099511628211UL; h = (h ^ (uint)b.Data.Length) * 1099511628211UL; }
        return h;
    }

    private static bool Locked(EditState st, string id) => st.Locks.Contains(id) && !InstrumentMutants.On(InstrumentMutants.IgnoreLocks);
    private static readonly Func<EditState, string, bool> LockedFn = Locked;

    private static Prng Rng(in InstrumentOp op) => new(InstrumentMutants.On(InstrumentMutants.SeedIgnored) ? 0UL : op.Seed);

    // ---- locks ----

    private static OpResult LockOp(EditState st, in InstrumentOp op, bool on)
    {
        if (st.Inst.Field(op.Target) == null && st.Inst.Blob(op.Target) == null) return new OpResult(false, $"no field or blob called '{op.Target}'");
        bool changed = on ? st.Locks.Add(op.Target) : st.Locks.Remove(op.Target);
        return new OpResult(changed, (on ? "locked " : "unlocked ") + op.Target);
    }

    // ---- manual edits ----

    private static OpResult SetField(EditState st, in InstrumentOp op)
    {
        var f = st.Inst.Field(op.Target);
        if (f == null) return new OpResult(false, $"no field called '{op.Target}'");
        f.Value = f.Clamp(op.A);
        return new OpResult(true, $"{f.Name} = {f.Text}");
    }

    private static OpResult SetBytes(EditState st, in InstrumentOp op)
    {
        var b = st.Inst.Blob(op.Target);
        if (b == null) return new OpResult(false, $"no blob called '{op.Target}'");
        if (op.Data == null || op.A < 0 || op.A + op.Data.Length > b.Data.Length) return new OpResult(false, $"bytes {op.A}..{op.A + (op.Data?.Length ?? 0)} do not fit the {b.Data.Length}-byte {b.Name}");
        op.Data.CopyTo(b.Data, op.A);
        return new OpResult(true, $"{b.Name}: {op.Data.Length} bytes set");
    }

    // ---- randomise ----

    private static OpResult Randomise(EditState st, in InstrumentOp op)
    {
        var inst = st.Inst;
        int strength = Math.Clamp(op.A, 0, 1000);
        var rng = Rng(op);
        var fields = new List<Field>();
        var blobs = new List<Blob>();
        string note = "";
        if (op.Target == "")
        {
            foreach (var f in inst.Fields) if (!Locked(st, f.Id)) fields.Add(f);
            foreach (var b in inst.Blobs) if (!Locked(st, b.Id)) blobs.Add(b);
        }
        else
        {
            var f = inst.Field(op.Target); var b = inst.Blob(op.Target);
            if (f == null && b == null) return new OpResult(false, $"no field or blob called '{op.Target}'");
            if (Locked(st, op.Target)) return new OpResult(false, $"{op.Target} is locked");
            if (f != null) fields.Add(f); else blobs.Add(b!);
        }
        if (fields.Count == 0 && blobs.Count == 0) return new OpResult(false, "everything is locked");
        if (strength == 0) return new OpResult(false, "strength 0 changes nothing");

        int touched = 0;
        if (fields.Count > 0)
        {
            int k = Math.Max(1, (int)(((long)strength * fields.Count + 500) / 1000));
            k = Math.Min(k, fields.Count);
            // k distinct fields: a partial Fisher-Yates shuffle
            for (int i = 0; i < k; i++)
            {
                int j = i + (int)rng.Below((uint)(fields.Count - i));
                (fields[i], fields[j]) = (fields[j], fields[i]);
                RandomiseField(fields[i], strength, ref rng, op.Keep);
                touched++;
            }
        }
        int units = 0;
        for (int i = 0; i < blobs.Count; i++)
        {
            var sub = new Prng(Prng.Mix(op.Seed, (ulong)(i + 1)));
            units += RandomiseBlob(blobs[i], strength, ref sub, op.Keep, op.Flags);
        }
        if (touched > 0) note += $"{touched} fields";
        if (units > 0) note += (note.Length > 0 ? ", " : "") + $"{units} sample units";
        return new OpResult(true, "randomised " + note);
    }

    private static void RandomiseField(Field f, int strength, ref Prng rng, bool keep)
    {
        int old = f.Value;
        if (strength >= 1000) { f.Value = rng.Between(f.Min, f.Max); return; }
        int span = f.Max - f.Min;
        int dm = Math.Max(1, (int)((long)span * strength / 1000));
        int delta = rng.Between(1, dm);
        if (rng.Below(2) == 0) delta = -delta;
        int v = f.Clamp(old + delta);
        if (v == old) v = f.Clamp(old - delta);       // at an edge: bounce the other way
        f.Value = v;
    }

    /// <summary>Randomises a blob; returns how many units (nibbles / samples / bytes) were touched.</summary>
    private static int RandomiseBlob(Blob b, int strength, ref Prng rng, bool keep, int flags)
    {
        var d = b.Data;
        if (d.Length == 0) return 0;
        switch (b.Kind)
        {
            case BlobKind.Brr: return RandomiseBrr(d, strength, ref rng, keep, flags);
            case BlobKind.Nibbles:
            {
                int units = d.Length * 2;
                if (strength >= 1000) { for (int i = 0; i < units; i++) SetNibble(d, i, (int)rng.Below(16)); return units; }
                int m = Math.Max(1, (int)((long)units * strength / 2000));
                for (int k = 0; k < m; k++)
                {
                    int i = (int)rng.Below((uint)units), old = GetNibble(d, i);
                    int nv = rng.Chance(strength) ? (int)rng.Below(16) : Math.Clamp(old + Signed(ref rng, 1 + (int)rng.Below((uint)(1 + 7 * strength / 1000))), 0, 15);
                    if (nv == old) nv = (old + 1) & 15;
                    SetNibble(d, i, nv);
                }
                return m;
            }
            default:  // Pcm8, Bytes
            {
                if (strength >= 1000) { for (int i = 0; i < d.Length; i++) d[i] = (byte)rng.Below(256); return d.Length; }
                int m = Math.Max(1, (int)((long)d.Length * strength / 2000));
                for (int k = 0; k < m; k++)
                {
                    int i = (int)rng.Below((uint)d.Length), old = d[i];
                    int nv = rng.Chance(strength) ? (int)rng.Below(256) : Math.Clamp(old + Signed(ref rng, 1 + (int)rng.Below((uint)(1 + 127 * strength / 1000))), 0, 255);
                    if (nv == old) nv = (old + 1) & 255;
                    d[i] = (byte)nv;
                }
                return m;
            }
        }
    }

    private static int Signed(ref Prng rng, int magnitude) => rng.Below(2) == 0 ? -magnitude : magnitude;

    private static int GetNibble(byte[] d, int i) => (i & 1) == 0 ? d[i >> 1] >> 4 : d[i >> 1] & 15;
    private static void SetNibble(byte[] d, int i, int v) => d[i >> 1] = (i & 1) == 0 ? (byte)(d[i >> 1] & 0x0F | v << 4) : (byte)(d[i >> 1] & 0xF0 | v);

    // ---- BRR ----

    /// <summary>The nibble n (0-15) of block b as a 0..15 value.</summary>
    private static int BrrNibble(byte[] d, int b, int n) => (n & 1) == 0 ? d[b * 9 + 1 + (n >> 1)] >> 4 : d[b * 9 + 1 + (n >> 1)] & 15;
    private static void SetBrrNibble(byte[] d, int b, int n, int v)
    {
        int at = b * 9 + 1 + (n >> 1);
        d[at] = (n & 1) == 0 ? (byte)(d[at] & 0x0F | v << 4) : (byte)(d[at] & 0xF0 | v);
    }

    private static int RandomiseBrr(byte[] d, int strength, ref Prng rng, bool keep, int flags)
    {
        int blocks = d.Length / 9;
        if (blocks == 0) { for (int i = 0; i < d.Length; i++) d[i] = (byte)rng.Below(256); return d.Length; }
        bool headers = (flags & InstrumentOp.PartHeaders) != 0 || (flags & InstrumentOp.PartNibbles) == 0, nibbles = (flags & InstrumentOp.PartNibbles) != 0 || (flags & InstrumentOp.PartHeaders) == 0;
        int touched = 0;
        if (strength >= 1000)
        {
            for (int b = 0; b < blocks; b++)
            {
                if (nibbles) { for (int j = 1; j < 9; j++) d[b * 9 + j] = (byte)rng.Below(256); touched += 16; }
                if (headers)
                {
                    if (keep && !InstrumentMutants.On(InstrumentMutants.BrrHeaderBroken)) d[b * 9] = (byte)((int)rng.Below(13) << 4 | (int)rng.Below(4) << 2 | d[b * 9] & 3);
                    else d[b * 9] = (byte)rng.Below(256);
                    touched++;
                }
            }
            return touched;
        }
        int m = Math.Max(1, (int)((long)blocks * 16 * strength / 2000));
        int pBreak = keep && !InstrumentMutants.On(InstrumentMutants.BrrHeaderBroken) ? 0 : (int)((long)strength * strength / 1000);
        for (int k = 0; k < m; k++)
        {
            int b = (int)rng.Below((uint)blocks);
            if (headers && pBreak > 0 && rng.Chance(pBreak)) { BreakBrr(d, b, blocks, ref rng); touched++; continue; }
            int cls = (int)rng.Below(100);
            if (nibbles && (cls < 76 || !headers))
            {
                int n = (int)rng.Below(16), old = BrrNibble(d, b, n);
                int nv = rng.Chance(strength) ? (int)rng.Below(16) : (old + Signed(ref rng, 1 + (int)rng.Below((uint)(1 + 7 * strength / 1000))) + 16) & 15;
                if (nv == old) nv = (old + 1) & 15;
                SetBrrNibble(d, b, n, nv);
            }
            else if (headers)
            {
                int h = d[b * 9];
                if (cls < 88)
                {
                    int range = h >> 4, nr = Math.Clamp(range + Signed(ref rng, 1 + (int)rng.Below(3)), 0, 12);
                    if (nr == range) nr = range < 12 ? range + 1 : range - 1;
                    d[b * 9] = (byte)(nr << 4 | h & 0x0F);
                }
                else if (cls < 96)
                {
                    int f = h >> 2 & 3, nf = (f + 1 + (int)rng.Below(3)) & 3;
                    d[b * 9] = (byte)(h & 0xF3 | nf << 2);
                }
                else d[blocks * 9 - 9] ^= 2;                      // the last block's loop flag: loops or plays once, both valid
            }
            touched++;
        }
        return touched;
    }

    /// <summary>One structure-breaking change: a range the DSP treats as invalid, a missing end flag at the end, an end flag in the middle, a loop flag where it does not belong, or a whole header of noise.</summary>
    private static void BreakBrr(byte[] d, int b, int blocks, ref Prng rng)
    {
        switch (rng.Below(5))
        {
            case 0: d[b * 9] = (byte)((13 + (int)rng.Below(3)) << 4 | d[b * 9] & 0x0F); break;
            case 1: d[blocks * 9 - 9] &= 0xFE; break;
            case 2: d[b * 9] |= 1; break;
            case 3: d[b * 9] ^= 2; break;
            default: d[b * 9] = (byte)rng.Below(256); break;
        }
    }

    /// <summary>Whether a BRR blob is well-formed in the sense the sampler's sample list uses: whole blocks, every range 12 or less, the end flag on the last block and on no other.</summary>
    public static bool BrrWellFormed(byte[] d)
    {
        if (d.Length == 0 || d.Length % 9 != 0) return false;
        int blocks = d.Length / 9;
        for (int b = 0; b < blocks; b++)
        {
            if ((d[b * 9] >> 4) > 12) return false;
            bool end = (d[b * 9] & 1) != 0;
            if (end != (b == blocks - 1)) return false;
        }
        return true;
    }

    // ---- nudge ----

    private static OpResult Nudge(EditState st, in InstrumentOp op)
    {
        var inst = st.Inst;
        var rng = Rng(op);
        Field? f = null; Blob? b = null;
        if (op.Target == "")
        {
            var fs = inst.Fields.Where(x => !Locked(st, x.Id)).ToList();
            var bs = inst.Blobs.Where(x => !Locked(st, x.Id) && x.Data.Length > 0).ToList();
            int total = fs.Count + bs.Count;
            if (total == 0) return new OpResult(false, "everything is locked");
            int pick = (int)rng.Below((uint)total);
            if (pick < fs.Count) f = fs[pick]; else b = bs[pick - fs.Count];
        }
        else
        {
            f = inst.Field(op.Target); b = inst.Blob(op.Target);
            if (f == null && b == null) return new OpResult(false, $"no field or blob called '{op.Target}'");
            if (Locked(st, op.Target)) return new OpResult(false, $"{op.Target} is locked");
        }
        int step = op.A;
        if (f != null)
        {
            int old = f.Value;
            int nv = f.Clamp(old + (inst.Kind == InstrumentKind.SnesSample && f.Id == "loop.addr" && op.Keep ? step * 9 : step));
            f.Value = nv;
            return new OpResult(f.Value != old, $"{f.Name} {old} -> {f.Value}");
        }
        int count = Math.Max(1, op.B);
        var d = b!.Data;
        int moved = 0;
        for (int k = 0; k < count; k++)
        {
            switch (b.Kind)
            {
                case BlobKind.Brr:
                {
                    int blocks = d.Length / 9;
                    if (blocks == 0) break;
                    int blk = (int)rng.Below((uint)blocks);
                    bool header = (op.Flags & InstrumentOp.PartHeaders) != 0 && (op.Flags & InstrumentOp.PartNibbles) == 0;
                    if (header)
                    {
                        int h = d[blk * 9], range = h >> 4, nr = Math.Clamp(range + step, 0, op.Keep ? 12 : 15);
                        d[blk * 9] = (byte)(nr << 4 | h & 0x0F);
                        if (nr != range) moved++;
                    }
                    else
                    {
                        int n = (int)rng.Below(16), old = BrrNibble(d, blk, n);
                        int sv = (old ^ 8) - 8;                     // the nibble as a signed 4-bit value
                        int nvs = Math.Clamp(sv + step, -8, 7);
                        SetBrrNibble(d, blk, n, nvs & 15);
                        if (nvs != sv) moved++;
                    }
                    break;
                }
                case BlobKind.Nibbles:
                {
                    int i = (int)rng.Below((uint)(d.Length * 2)), old = GetNibble(d, i), nv = Math.Clamp(old + step, 0, 15);
                    SetNibble(d, i, nv);
                    if (nv != old) moved++;
                    break;
                }
                default:
                {
                    int i = (int)rng.Below((uint)d.Length), old = d[i], nv = Math.Clamp(old + step, 0, 255);
                    d[i] = (byte)nv;
                    if (nv != old) moved++;
                    break;
                }
            }
        }
        return new OpResult(moved > 0, $"{b.Name}: {moved} of {count} units moved by {step}");
    }

    // ---- flip bits ----

    private static OpResult FlipBits(EditState st, in InstrumentOp op)
    {
        var inst = st.Inst;
        var rng = Rng(op);
        Field? f = null; Blob? b = null;
        if (op.Target == "")
        {
            var fs = inst.Fields.Where(x => !Locked(st, x.Id)).ToList();
            var bs = inst.Blobs.Where(x => !Locked(st, x.Id) && x.Data.Length > 0).ToList();
            int total = fs.Count + bs.Count;
            if (total == 0) return new OpResult(false, "everything is locked");
            int pick = (int)rng.Below((uint)total);
            if (pick < fs.Count) f = fs[pick]; else b = bs[pick - fs.Count];
        }
        else
        {
            f = inst.Field(op.Target); b = inst.Blob(op.Target);
            if (f == null && b == null) return new OpResult(false, $"no field or blob called '{op.Target}'");
            if (Locked(st, op.Target)) return new OpResult(false, $"{op.Target} is locked");
        }
        int count = Math.Max(1, op.A);
        if (f != null)
        {
            uint raw = f.Raw;
            int width = f.Width;
            // distinct bit positions inside the field's width (all of them when more are asked for than exist)
            var pos = new List<int>();
            for (int i = 0; i < width; i++) pos.Add(i);
            int n = Math.Min(count, width);
            for (int i = 0; i < n; i++)
            {
                int j = i + (int)rng.Below((uint)(width - i));
                (pos[i], pos[j]) = (pos[j], pos[i]);
                raw ^= 1u << (InstrumentMutants.On(InstrumentMutants.FlipOutOfWidth) ? pos[i] + 1 : pos[i]);
            }
            int old = f.Value;
            if (InstrumentMutants.On(InstrumentMutants.FlipOutOfWidth)) f.Value = (int)raw; else f.SetRaw(raw);
            return new OpResult(f.Value != old, $"{f.Name} {old} -> {f.Value}");
        }
        var d = b!.Data;
        int flipped = 0;
        for (int k = 0; k < count; k++)
        {
            switch (b.Kind)
            {
                case BlobKind.Brr:
                {
                    int blocks = d.Length / 9;
                    if (blocks == 0) { d[(int)rng.Below((uint)d.Length)] ^= (byte)(1 << (int)rng.Below(8)); flipped++; break; }
                    bool onlyHeaders = (op.Flags & InstrumentOp.PartHeaders) != 0 && (op.Flags & InstrumentOp.PartNibbles) == 0, onlyNibbles = (op.Flags & InstrumentOp.PartNibbles) != 0 && (op.Flags & InstrumentOp.PartHeaders) == 0;
                    for (int attempt = 0; attempt < 12; attempt++)
                    {
                        int blk = (int)rng.Below((uint)blocks);
                        int j = onlyHeaders ? 0 : onlyNibbles ? 1 + (int)rng.Below(8) : (int)rng.Below(9);
                        int bit = (int)rng.Below(8);
                        int at = blk * 9 + j;
                        byte was = d[at];
                        d[at] ^= (byte)(1 << bit);
                        // keeping the structure valid: a flip that breaks it is taken back and another bit tried
                        if (j == 0 && op.Keep && !InstrumentMutants.On(InstrumentMutants.BrrHeaderBroken) && !HeaderOk(d, blk, blocks)) { d[at] = was; continue; }
                        flipped++;
                        break;
                    }
                    break;
                }
                case BlobKind.Nibbles:
                {
                    int i = (int)rng.Below((uint)(d.Length * 2)), bit = (int)rng.Below(4);
                    SetNibble(d, i, GetNibble(d, i) ^ (1 << bit));
                    flipped++;
                    break;
                }
                default:
                {
                    int i = (int)rng.Below((uint)d.Length);
                    d[i] ^= (byte)(1 << (int)rng.Below(8));
                    flipped++;
                    break;
                }
            }
        }
        return new OpResult(flipped > 0, $"{b.Name}: {flipped} bits flipped");
    }

    /// <summary>A block header that keeps the structure: range 12 or less, and the end flag on the last block only.</summary>
    private static bool HeaderOk(byte[] d, int blk, int blocks) => (d[blk * 9] >> 4) <= 12 && ((d[blk * 9] & 1) != 0) == (blk == blocks - 1);
}

/// <summary>Rules that tie fields to each other or to a blob, applied after every operation so the instrument stays writable.</summary>
internal static class InstrumentRules
{
    /// <summary>
    /// Brings cross-field constraints back in range after an operation, never moving a locked field.
    /// <list type="bullet">
    /// <item>A Genesis DAC loop must lie inside its sound (start below end, end no further than the length): the instrument cannot be written otherwise. If a locked field stands in the way the loop is switched off instead.</item>
    /// <item>With <paramref name="keep"/>, a SNES loop pointer sits on a block boundary inside the sample: moved there when the operation changed it, and also whenever the last block's loop flag is set (the pointer then matters).</item>
    /// </list>
    /// </summary>
    public static void Legalise(EditState st, bool keep, bool loopTouched, Func<EditState, string, bool> locked)
    {
        var inst = st.Inst;
        switch (inst.Kind)
        {
            case InstrumentKind.OpnDac:
            {
                if (inst.Get("dac.loop") == 0) break;
                int len = inst.Blobs[0].Data.Length;
                var s = inst.Field("dac.loop.start")!; var e = inst.Field("dac.loop.end")!;
                int ne = Math.Clamp(e.Value, 1, Math.Max(1, len));
                int ns = Math.Clamp(s.Value, 0, ne - 1);
                bool needsEnd = ne != e.Value, needsStart = ns != s.Value;
                if ((needsEnd && locked(st, e.Id)) || (needsStart && locked(st, s.Id)))
                {
                    var on = inst.Field("dac.loop")!;
                    if (!locked(st, on.Id)) on.Value = 0;
                    break;
                }
                e.Value = ne; s.Value = ns;
                break;
            }
            case InstrumentKind.SnesSample:
            {
                if (!keep || InstrumentMutants.On(InstrumentMutants.LoopOutsideSample)) break;
                var d = inst.Blobs[0].Data;
                int blocks = d.Length / 9;
                var loop = inst.Field("loop.addr")!;
                if (blocks == 0 || locked(st, loop.Id)) break;
                bool lastLoops = (d[blocks * 9 - 9] & 2) != 0;
                if (!loopTouched && !lastLoops) break;
                int rel = loop.Value - inst.Origin;
                int idx = rel < 0 ? 0 : Math.Min(blocks - 1, (rel + 4) / 9);
                int snapped = inst.Origin + idx * 9;
                if (loop.Value != snapped) loop.Value = loop.Clamp(snapped);
                break;
            }
        }
    }
}
