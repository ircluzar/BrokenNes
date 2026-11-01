namespace BrokenNes2.Instruments;

/// <summary>
/// The saved form of one instrument, for presets, projects and histories: the values of its fields in the order of its kind's schema, then its blobs, behind a small versioned header, with a CRC-32 over everything.
/// <code>
///   "INS" u8 version | u8 console | u8 kind | i32 index | i32 origin | fields: zig-zag varints in schema order | blobs: varint length + bytes | u32 CRC-32 of everything before
/// </code>
/// The schema (field ids, ranges, names) is code, not data: a saved instrument stores values only, which keeps it small (a Genesis patch is about 60 bytes), and <see cref="Unpack"/> checks every value against the schema's range and refuses
/// a stream that does not fit it. A future change to a kind's field list therefore needs a new <see cref="Version"/> (and the old reader kept).
/// </summary>
public static class InstrumentCodec
{
    public const int Version = 1;
    private const uint Magic = 0x00534E49;      // "INS"

    /// <summary>An instrument of this kind with the schema's fields at their defaults and blobs of the given length (zero where the kind has none).</summary>
    public static Instrument? NewTemplate(ConsoleId console, InstrumentKind kind, int index, int blobLength = 0) => kind switch
    {
        InstrumentKind.SnesSample => SnesInstruments.Template(index, blobLength),
        InstrumentKind.OpnPatch or InstrumentKind.OpnDac when console == ConsoleId.Genesis => SegaInstruments.Template(console, kind, index, Math.Max(1, blobLength)),
        InstrumentKind.OpllUser or InstrumentKind.OpllRom when console == ConsoleId.MasterSystem => SegaInstruments.Template(console, kind, index),
        InstrumentKind.PsgTones or InstrumentKind.PsgNoise when console is ConsoleId.MasterSystem or ConsoleId.Genesis => SegaInstruments.Template(console, kind, index),
        InstrumentKind.GbPulse1 or InstrumentKind.GbPulse2 or InstrumentKind.GbWave or InstrumentKind.GbNoise when console == ConsoleId.GameBoy => GbInstruments.Template(kind),
        InstrumentKind.NesPulse1 or InstrumentKind.NesPulse2 or InstrumentKind.NesTriangle or InstrumentKind.NesNoise or InstrumentKind.NesDmc when console == ConsoleId.Nes => NesInstruments.Template(kind, blobLength),
        _ => null,
    };

    private static void PutVarint(List<byte> o, uint v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    private static uint ZigZag(int v) => (uint)(v << 1 ^ v >> 31);
    private static int UnZigZag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    private static bool GetVarint(byte[] b, ref int pos, out uint v)
    {
        v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (pos >= b.Length) return false;
            byte x = b[pos++];
            v |= (uint)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return true;
        }
        return false;
    }

    public static byte[] Pack(Instrument inst)
    {
        var o = new List<byte> { (byte)'I', (byte)'N', (byte)'S', Version, (byte)inst.Console, (byte)inst.Kind };
        o.AddRange(BitConverter.GetBytes(inst.Index));
        o.AddRange(BitConverter.GetBytes(inst.Origin));
        foreach (var f in inst.Fields) PutVarint(o, ZigZag(f.Value));
        foreach (var b in inst.Blobs) { PutVarint(o, (uint)b.Data.Length); o.AddRange(b.Data); }
        uint crc = InstrumentCrc.Of(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(o));
        o.Add((byte)crc); o.Add((byte)(crc >> 8)); o.Add((byte)(crc >> 16)); o.Add((byte)(crc >> 24));
        return o.ToArray();
    }

    /// <summary>The instrument the bytes hold, or null with the reason (cut short, damaged by the checksum, from a newer version, a kind this code does not know, a value or length outside its schema).</summary>
    public static Instrument? Unpack(byte[] b, out string? problem)
    {
        problem = null;
        if (b.Length < 18) { problem = "cut short"; return null; }
        if (b[0] != 'I' || b[1] != 'N' || b[2] != 'S') { problem = "not an instrument"; return null; }
        if (b[3] != Version) { problem = b[3] > Version ? "from a newer version" : "of an unknown version"; return null; }
        uint crc = (uint)(b[^4] | b[^3] << 8 | b[^2] << 16 | b[^1] << 24);
        if (crc != InstrumentCrc.Of(b.AsSpan(0, b.Length - 4)) && !InstrumentMutants.On(InstrumentMutants.CrcNotChecked)) { problem = "damaged (checksum)"; return null; }
        var console = (ConsoleId)b[4];
        var kind = (InstrumentKind)b[5];
        int index = BitConverter.ToInt32(b, 6), origin = BitConverter.ToInt32(b, 10);
        var probe = NewTemplate(console, kind, index, 0);
        if (probe == null || probe.Console != console) { problem = "of a kind this version does not know"; return null; }
        int pos = 14, end = b.Length - 4;
        var values = new int[probe.Fields.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!GetVarint(b, ref pos, out uint v) || pos > end) { problem = "cut short"; return null; }
            values[i] = UnZigZag(v);
        }
        var blobs = new byte[probe.Blobs.Length][];
        for (int i = 0; i < blobs.Length; i++)
        {
            if (!GetVarint(b, ref pos, out uint len) || pos > end || len > (uint)(end - pos)) { problem = "cut short"; return null; }
            blobs[i] = b.AsSpan(pos, (int)len).ToArray();
            pos += (int)len;
        }
        if (pos != end) { problem = "damaged (trailing bytes)"; return null; }
        var inst = NewTemplate(console, kind, index, blobs.Length > 0 ? blobs[0].Length : 0)!;
        inst.Origin = origin;
        for (int i = 0; i < values.Length; i++)
        {
            var f = inst.Fields[i];
            if (values[i] < f.Min || values[i] > f.Max) { problem = $"damaged ({f.Id} = {values[i]} is outside {f.Min} to {f.Max})"; return null; }
            f.Value = values[i];
        }
        for (int i = 0; i < blobs.Length; i++)
        {
            var bl = inst.Blobs[i];
            if (blobs[i].Length < bl.MinLength || blobs[i].Length > bl.MaxLength) { problem = $"damaged ({bl.Id} is {blobs[i].Length} bytes)"; return null; }
            bl.Data = blobs[i];
        }
        return inst;
    }
}
