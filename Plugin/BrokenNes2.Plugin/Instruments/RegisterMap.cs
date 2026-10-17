namespace BrokenNes2.Instruments;

/// <summary>
/// Where each field of an instrument sits in the register bytes of a snapshot: a byte, a shift and a width. One table drives both directions: reading sets every field from the bytes; writing changes only the bits of the fields
/// whose value differs from what the snapshot already holds, leaving every other bit (the ones no field covers, and the fields that did not change) exactly as it was. That is what makes "read, write unchanged" give identical bytes.
/// </summary>
internal sealed class RegisterMap
{
    private readonly record struct Slot(string Id, int Byte, int Shift, int Bits);
    private readonly List<Slot> slots = new();

    public RegisterMap Add(string id, int byteIndex, int shift, int bits) { slots.Add(new Slot(id, byteIndex, shift, bits)); return this; }

    /// <summary>Sets the instrument's fields from <paramref name="regs"/> (fields the map does not know are left alone).</summary>
    public void Read(byte[] regs, Instrument inst)
    {
        foreach (var s in slots)
        {
            var f = inst.Field(s.Id);
            if (f == null) continue;
            int v = regs[s.Byte] >> s.Shift & ((1 << s.Bits) - 1);
            f.Value = f.Kind == FieldKind.Signed && (v >> (s.Bits - 1) & 1) != 0 ? v - (1 << s.Bits) : v;
        }
    }

    /// <summary>Writes into <paramref name="regs"/> the fields of <paramref name="edited"/> that differ from <paramref name="current"/>; returns how many it wrote.</summary>
    public int Write(byte[] regs, Instrument edited, Instrument current)
    {
        int n = 0;
        foreach (var s in slots)
        {
            var f = edited.Field(s.Id);
            var c = current.Field(s.Id);
            if (f == null || c == null || f.Value == c.Value) continue;
            int mask = ((1 << s.Bits) - 1) << s.Shift;
            regs[s.Byte] = (byte)((regs[s.Byte] & ~mask) | ((f.Value << s.Shift) & mask));
            n++;
        }
        return n;
    }

    public IEnumerable<string> Ids => slots.Select(s => s.Id);
}
