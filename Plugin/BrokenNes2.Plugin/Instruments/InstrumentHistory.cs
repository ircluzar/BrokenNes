namespace BrokenNes2.Instruments;

/// <summary>
/// A corrupted instrument is the clean instrument plus the ordered list of operations (operation, target, seed, parameters) that were applied to it, a cursor into that list (undo and redo move it), and the locks those operations honoured
/// (lock and unlock are operations too, so a replay sees the same locks at every step). <see cref="Current"/> is always equal to <see cref="Replay"/> of the first <see cref="Cursor"/> operations from the clean instrument. The final
/// bytes (<see cref="FinalBytes"/>) are stored beside the operations when saved, so a saved corruption does not depend on the corruptor staying the same.
/// <para>UI thread only; not thread-safe.</para>
/// </summary>
public sealed class InstrumentHistory
{
    public const int MaxOperations = 4096;
    private readonly List<InstrumentOp> ops = new();
    private int cursor;
    private EditState current;
    /// <summary>Set by <see cref="Deserialize"/>: whether replaying the saved operations from the saved clean instrument gave the saved final bytes (false: the corruptor changed since, and the saved final bytes are what <see cref="Current"/> is).</summary>
    public bool ReplayMatchesSaved { get; private set; } = true;

    public InstrumentHistory(Instrument clean)
    {
        Clean = clean.Clone();
        current = new EditState(Clean.Clone());
    }

    /// <summary>The instrument as read from the snapshot, before any operation. Do not modify.</summary>
    public Instrument Clean { get; }
    /// <summary>The instrument after the first <see cref="Cursor"/> operations. Do not modify; copy it with <see cref="Instrument.Clone"/>.</summary>
    public Instrument Current => current.Inst;
    /// <summary>The ids locked against the random tools at the cursor.</summary>
    public IReadOnlySet<string> Locks => current.Locks;
    /// <summary>Every operation, applied or waiting to be redone.</summary>
    public IReadOnlyList<InstrumentOp> Operations => ops;
    /// <summary>How many operations are applied.</summary>
    public int Cursor => cursor;
    public bool CanUndo => cursor > 0;
    public bool CanRedo => cursor < ops.Count;
    /// <summary>Whether the instrument differs from the clean one.</summary>
    public bool Modified => !Current.SameValues(Clean);

    /// <summary>Applies an operation after the cursor (what could have been redone is dropped) and records it. Returns what it did.</summary>
    public OpResult Do(in InstrumentOp op)
    {
        if (cursor >= MaxOperations) return new OpResult(false, $"the history is full ({MaxOperations} operations): keep this one as a preset and start a new history");
        if (cursor < ops.Count) ops.RemoveRange(cursor, ops.Count - cursor);
        var stored = InstrumentMutants.On(InstrumentMutants.SeedNotStored) ? op with { Seed = 0 } : op;
        var r = InstrumentCorruptor.Apply(current, op);
        ops.Add(stored);
        cursor++;
        return r;
    }

    public bool Undo()
    {
        if (cursor == 0) return false;
        cursor--;
        var stale = current;
        current = Build(cursor);
        if (InstrumentMutants.On(InstrumentMutants.UndoLeavesBit) && stale.Inst.Blobs.Length > 0 && stale.Inst.Blobs[0].Data.Length > 0) current.Inst.Blobs[0].Data[0] = stale.Inst.Blobs[0].Data[0];
        return true;
    }

    public bool Redo()
    {
        if (cursor >= ops.Count) return false;
        InstrumentCorruptor.Apply(current, ops[cursor]);
        cursor++;
        return true;
    }

    /// <summary>Goes to any point of the history (0 = clean).</summary>
    public void GoTo(int position)
    {
        position = Math.Clamp(position, 0, ops.Count);
        if (position == cursor) return;
        cursor = position;
        current = Build(cursor);
    }

    private EditState Build(int upTo)
    {
        var st = new EditState(Clean.Clone());
        int n = InstrumentMutants.On(InstrumentMutants.ReplayDropsLast) && upTo > 0 ? upTo - 1 : upTo;
        for (int i = 0; i < n; i++) InstrumentCorruptor.Apply(st, ops[i]);
        return st;
    }

    /// <summary>The instrument after the first <paramref name="upTo"/> operations, rebuilt from the clean one (a fresh object).</summary>
    public Instrument Replay(int upTo) => Build(Math.Clamp(upTo, 0, ops.Count)).Inst;

    /// <summary>The corrupted instrument as bytes (<see cref="InstrumentCodec.Pack"/>): the form a preset or a project stores, independent of the corruptor.</summary>
    public byte[] FinalBytes() => InstrumentCodec.Pack(Current);

    // ---- convenience: the three tools and the manual edits ----

    /// <summary>Re-rolls the unlocked fields and blobs. <paramref name="strength"/> is 0 to 1 (stored as an integer in permille); 1 is a full shuffle.</summary>
    public OpResult Randomise(double strength, ulong seed, bool keepStructure = true, string target = "", int part = 0) =>
        Do(new InstrumentOp(OpKind.Randomise, target, (int)Math.Round(Math.Clamp(strength, 0, 1) * 1000), 0, seed, (keepStructure ? InstrumentOp.KeepStructure : 0) | part, null));

    /// <summary>A little nudge: <paramref name="step"/> units of one field (or of <paramref name="count"/> places of a blob). An empty target picks a random unlocked field or blob.</summary>
    public OpResult Nudge(string target, int step, ulong seed, int count = 1, bool keepStructure = true, int part = 0) =>
        Do(new InstrumentOp(OpKind.Nudge, target, step, count, seed, (keepStructure ? InstrumentOp.KeepStructure : 0) | part, null));

    public OpResult FlipBits(string target, int count, ulong seed, bool keepStructure = true, int part = 0) =>
        Do(new InstrumentOp(OpKind.FlipBits, target, count, 0, seed, (keepStructure ? InstrumentOp.KeepStructure : 0) | part, null));

    public OpResult Set(string field, int value) => Do(new InstrumentOp(OpKind.Set, field, value, 0, 0, 0, null));
    public OpResult SetBytes(string blob, int offset, byte[] data) => Do(new InstrumentOp(OpKind.SetBytes, blob, offset, 0, 0, 0, data));
    public OpResult Lock(string id) => Do(new InstrumentOp(OpKind.Lock, id, 0, 0, 0, 0, null));
    public OpResult Unlock(string id) => Do(new InstrumentOp(OpKind.Unlock, id, 0, 0, 0, 0, null));

    // ---- saved form ----
    // "BNIH" u8 version | u8 flags | clean (u32 length, Pack) | final (u32 length, Pack) | u16 operation count | u16 cursor | operations | u32 CRC-32 of everything before
    // operation: u8 kind | u8 flags | i32 A | i32 B | u64 seed | u8 target length + UTF-8 | u16 data length + data

    private const uint Magic = 0x48494E42;     // "BNIH"
    public const int Version = 1;

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic); w.Write((byte)Version); w.Write((byte)0);
        var clean = InstrumentCodec.Pack(Clean);
        w.Write(clean.Length); w.Write(clean);
        var fin = FinalBytes();
        w.Write(fin.Length); w.Write(fin);
        w.Write((ushort)ops.Count); w.Write((ushort)cursor);
        foreach (var op in ops)
        {
            w.Write((byte)op.Kind); w.Write((byte)op.Flags); w.Write(op.A); w.Write(op.B); w.Write(op.Seed);
            var t = System.Text.Encoding.UTF8.GetBytes(op.Target);
            w.Write((byte)t.Length); w.Write(t);
            int dl = op.Data?.Length ?? 0;
            w.Write((ushort)dl); if (dl > 0) w.Write(op.Data!);
        }
        w.Flush();
        var body = ms.ToArray();
        var all = new byte[body.Length + 4];
        body.CopyTo(all, 0);
        uint crc = InstrumentCrc.Of(body);
        all[^4] = (byte)crc; all[^3] = (byte)(crc >> 8); all[^2] = (byte)(crc >> 16); all[^1] = (byte)(crc >> 24);
        return all;
    }

    /// <summary>Reads a saved history; null with the reason for anything damaged, cut short, newer than this code, or absurd. A history whose saved final bytes differ from a replay (the corruptor changed) is returned with the saved final bytes as <see cref="Current"/> and <see cref="ReplayMatchesSaved"/> false.</summary>
    public static InstrumentHistory? Deserialize(byte[] b, out string? problem)
    {
        problem = null;
        if (b.Length < 4 + 1 + 1 + 4 + 4 + 2 + 2 + 4) { problem = "cut short"; return null; }
        uint crc = (uint)(b[^4] | b[^3] << 8 | b[^2] << 16 | b[^1] << 24);
        if (crc != InstrumentCrc.Of(b.AsSpan(0, b.Length - 4)) && !InstrumentMutants.On(InstrumentMutants.CrcNotChecked)) { problem = "damaged (checksum)"; return null; }
        try
        {
            using var r = new BinaryReader(new MemoryStream(b, 0, b.Length - 4));
            if (r.ReadUInt32() != Magic) { problem = "not an instrument history"; return null; }
            int version = r.ReadByte();
            if (version != Version) { problem = version > Version ? "from a newer version" : "unknown version"; return null; }
            r.ReadByte();
            int cl = r.ReadInt32();
            if (cl < 0 || cl > b.Length) { problem = "damaged"; return null; }
            var clean = InstrumentCodec.Unpack(r.ReadBytes(cl), out string? p1);
            if (clean == null) { problem = "the clean instrument is " + p1; return null; }
            int fl = r.ReadInt32();
            if (fl < 0 || fl > b.Length) { problem = "damaged"; return null; }
            var fin = InstrumentCodec.Unpack(r.ReadBytes(fl), out string? p2);
            if (fin == null) { problem = "the final instrument is " + p2; return null; }
            int count = r.ReadUInt16(), cursor = r.ReadUInt16();
            if (count > MaxOperations || cursor > count) { problem = "damaged (operation count)"; return null; }
            var h = new InstrumentHistory(clean);
            for (int i = 0; i < count; i++)
            {
                var kind = (OpKind)r.ReadByte();
                if (kind < OpKind.Randomise || kind > OpKind.Unlock) { problem = "damaged (operation kind)"; return null; }
                int flags = r.ReadByte(), a = r.ReadInt32(), bb = r.ReadInt32();
                ulong seed = r.ReadUInt64();
                int tl = r.ReadByte();
                string target = System.Text.Encoding.UTF8.GetString(r.ReadBytes(tl));
                int dl = r.ReadUInt16();
                byte[]? data = dl > 0 ? r.ReadBytes(dl) : null;
                h.ops.Add(new InstrumentOp(kind, target, a, bb, seed, flags, data));
            }
            if (r.BaseStream.Position != r.BaseStream.Length) { problem = "damaged (trailing bytes)"; return null; }
            h.cursor = cursor;
            h.current = h.Build(cursor);
            if (!h.current.Inst.SameValues(fin))
            {
                // the corruptor changed since: what was saved is the truth
                h.ReplayMatchesSaved = false;
                h.current = new EditState(fin) { };
                foreach (var l in h.Build(cursor).Locks) h.current.Locks.Add(l);
            }
            return h;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException or OverflowException)
        {
            problem = "damaged (" + e.Message + ")";
            return null;
        }
    }
}
