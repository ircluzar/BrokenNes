// Round 6 (inject): the saved form of what was pushed into a game: the project section INJ1.
//
// The pushed bytes themselves are in the project already (the game's whole machine is in GAM1); what is NOT in any machine state is the table of overrides (a SNES voice's forced envelope, a Genesis key-on substitution, the DAC stream's
// edited sound) and the ORIGINAL bytes that "Remove from game" puts back. This section holds exactly that: version, flags, the records, the volume memory of the Genesis hook, a CRC-32 over all of it. A damaged, cut, newer or absurd section is
// refused with a reason and the project opens without the pushes (the editor says so), never half trusted. A project without the section opens exactly as before; an older plugin skips the section by its length.
using System.IO.Compression;
using System.Text;

namespace BrokenNes2.Instruments;

public static class GamePushFile
{
    /// <summary>"INJ1" read as a little-endian number, like the project's other section tags.</summary>
    public const uint Tag = 0x314A4E49;
    public const int Version = 1, MaxRecords = 64, MaxRaw = 12_000_000;
    /// <summary>The longest a stored section may be (the project's section reader refuses anything longer without reading it).</summary>
    public const int MaxStored = 6_000_000;

    public static byte[] Write(IReadOnlyList<PushRecord> records, byte[] hookState)
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            w.Write((ushort)records.Count);
            foreach (var r in records)
            {
                w.Write((byte)r.Kind); w.Write(r.Index);
                var name = Encoding.UTF8.GetBytes(r.Name.Length > 80 ? r.Name[..80] : r.Name);
                w.Write((byte)name.Length); w.Write(name);
                w.Write((byte)r.P.Length); foreach (int p in r.P) w.Write(p);
                w.Write(r.A.Length); w.Write(r.A);
                w.Write(r.B.Length); w.Write(r.B);
                w.Write((byte)r.Regions.Count);
                foreach (var g in r.Regions)
                {
                    var dom = Encoding.UTF8.GetBytes(g.Domain);
                    w.Write((byte)dom.Length); w.Write(dom);
                    w.Write(g.Address); w.Write(g.Pushed.Length); w.Write(g.Original); w.Write(g.Pushed);
                }
            }
            w.Write((ushort)hookState.Length); w.Write(hookState);
        }
        byte[] body = raw.ToArray();
        byte[] stored;
        using (var ms = new MemoryStream())
        {
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(body);
            stored = ms.ToArray();
        }
        var all = new byte[6 + stored.Length + 4];
        all[0] = Version; all[1] = 1;
        BitConverter.TryWriteBytes(all.AsSpan(2), body.Length);
        stored.CopyTo(all, 6);
        uint crc = InstrumentCrc.Of(all.AsSpan(0, all.Length - 4));
        BitConverter.TryWriteBytes(all.AsSpan(all.Length - 4), crc);
        return all;
    }

    /// <summary>Reads a saved section; null (with the reason) for anything that is not one. Never trusted.</summary>
    public static List<PushRecord>? Read(byte[] data, out byte[] hookState, out string? problem)
    {
        hookState = [];
        problem = null;
        if (data.Length < 11) { problem = "cut short"; return null; }
        if (data[0] != Version) { problem = data[0] > Version ? "from a newer version" : "of an unknown version"; return null; }
        uint crc = BitConverter.ToUInt32(data, data.Length - 4);
        if (crc != InstrumentCrc.Of(data.AsSpan(0, data.Length - 4)) && !PushMutants.On(PushMutants.CrcNotChecked)) { problem = "damaged (checksum)"; return null; }
        if ((data[1] & ~1) != 0) { problem = "damaged (flags)"; return null; }
        int rawLen = BitConverter.ToInt32(data, 2);
        if (rawLen < 4 || rawLen > MaxRaw) { problem = "damaged (length)"; return null; }
        byte[] body;
        try
        {
            if ((data[1] & 1) != 0)
            {
                using var z = new DeflateStream(new MemoryStream(data, 6, data.Length - 10), CompressionMode.Decompress);
                body = new byte[rawLen];
                int got = 0, n;
                while (got < rawLen && (n = z.Read(body, got, rawLen - got)) > 0) got += n;
                if (got != rawLen || z.ReadByte() != -1) { problem = "damaged (its data does not unpack to the length it says)"; return null; }
            }
            else
            {
                if (data.Length - 10 != rawLen) { problem = "damaged (length)"; return null; }
                body = data.AsSpan(6, rawLen).ToArray();
            }
        }
        catch (InvalidDataException) { problem = "damaged (its data does not unpack)"; return null; }
        try
        {
            using var r = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
            int count = r.ReadUInt16();
            if (count > MaxRecords) { problem = "damaged (too many records)"; return null; }
            var list = new List<PushRecord>();
            for (int i = 0; i < count; i++)
            {
                var rec = new PushRecord();
                byte kind = r.ReadByte();
                if (kind < 1 || kind > 5) { problem = "from a newer version (an unknown kind of push)"; return null; }
                rec.Kind = (PushKind)kind; rec.Index = r.ReadInt32();
                if (rec.Index < 0 || rec.Index > 100_000) { problem = "damaged (an instrument number)"; return null; }
                int nl = r.ReadByte(); rec.Name = Encoding.UTF8.GetString(r.ReadBytes(nl));
                int pc = r.ReadByte(); if (pc > 16) { problem = "damaged (parameters)"; return null; }
                rec.P = new int[pc]; for (int k = 0; k < pc; k++) rec.P[k] = r.ReadInt32();
                int al = r.ReadInt32(); if (al < 0 || al > 2_000_000 || al > body.Length) { problem = "damaged (a length)"; return null; }
                rec.A = r.ReadBytes(al); if (rec.A.Length != al) { problem = "cut short"; return null; }
                int bl = r.ReadInt32(); if (bl < 0 || bl > 2_000_000 || bl > body.Length) { problem = "damaged (a length)"; return null; }
                rec.B = r.ReadBytes(bl); if (rec.B.Length != bl) { problem = "cut short"; return null; }
                int rc = r.ReadByte(); if (rc > 8) { problem = "damaged (regions)"; return null; }
                for (int k = 0; k < rc; k++)
                {
                    var reg = new PushRegion();
                    int dl = r.ReadByte(); reg.Domain = Encoding.UTF8.GetString(r.ReadBytes(dl));
                    reg.Address = r.ReadInt32(); int len = r.ReadInt32();
                    if (len < 0 || len > 0x10000 || reg.Address < 0) { problem = "damaged (a region)"; return null; }
                    reg.Original = r.ReadBytes(len); reg.Pushed = r.ReadBytes(len);
                    if (reg.Original.Length != len || reg.Pushed.Length != len) { problem = "cut short"; return null; }
                    rec.Regions.Add(reg);
                }
                problem = Check(rec);
                if (problem != null) return null;
                list.Add(rec);
            }
            int hl = r.ReadUInt16(); hookState = r.ReadBytes(hl);
            if (hookState.Length != hl) { problem = "cut short"; return null; }
            if (r.BaseStream.Position != body.Length) { problem = "damaged (trailing bytes)"; return null; }
            return list;
        }
        catch (EndOfStreamException) { problem = "cut short"; return null; }
    }

    /// <summary>The shape each kind of record must have: anything else is refused, whatever the checksum says.</summary>
    private static string? Check(PushRecord r)
    {
        switch (r.Kind)
        {
            case PushKind.SnesSample:
                if (r.B.Length != SnesPusher.EntryBytes || r.Index > 255 || r.Regions.Count == 0) return "damaged (a SNES record)";
                if ((r.B[1] & ~15) != 0 || (r.B[5] & ~7) != 0 || (r.B[6] & ~7) != 0 || Math.Abs((short)(r.B[7] | r.B[8] << 8)) > 4800) return "damaged (a SNES value out of range)";
                foreach (var g in r.Regions) if (g.Domain != "ARAM" || g.Address + g.Pushed.Length > 0x10000) return "damaged (a SNES region)";
                return null;
            case PushKind.OpnPatch:
                if (r.A.Length is not (30 or 31) || r.B.Length != r.A.Length || r.Index > 5 || r.P.Length < 1) return "damaged (a Genesis patch record)";
                return null;
            case PushKind.OpnDac:
                if (r.A.Length < 12 || r.A.Length > 65536 || r.B.Length > 65536 || r.P.Length < 8 || r.P[0] < 1 || r.P[1] < 1) return "damaged (a Genesis DAC record)";
                return null;
            case PushKind.GbWave:
                if (r.A.Length != 16 || r.B.Length != 16) return "damaged (a Game Boy wave record)";
                foreach (var g in r.Regions) if (g.Domain != "WAVE" || g.Address != 0 || g.Pushed.Length != 16) return "damaged (a Game Boy wave region)";
                return null;
            case PushKind.OpllUser:
                if (r.A.Length != 8 || r.B.Length != 8) return "damaged (a YM2413 record)";
                return null;
        }
        return "damaged";
    }
}

/// <summary>Deliberate bugs for the checks of this library (tests only; zero in normal use): each check is shown to fail when its bug is switched on.</summary>
public static class PushMutants
{
    public static volatile int Active;
    public static bool On(int id) => Active == id;
    public const int CrcNotChecked = 1, SnesDropsSample = 2, SnesForgetsEnvelope = 3, RemoveLeavesOverrides = 4, RemoveLeavesBytes = 5, OpnIgnoresVolume = 6, DacRateIgnored = 7, SnesDropsTuning = 8, SnesDropsVolume = 9,
        OpnNoSubstitution = 10, DacNoSubstitution = 11, GbNoSubstitution = 12, OpllNoSubstitution = 13, RestoreSkipsHooks = 14, PushForgetsFarm = 15, OpnRemoveKeepsPatch = 16, SnesDropsMix = 17, OpnStateNotRestored = 18, OpnTlAccumulates = 19;
}
