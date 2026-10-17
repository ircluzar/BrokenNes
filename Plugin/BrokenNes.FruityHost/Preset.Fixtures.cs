// Round 6 (presets): the instruments the preset checks run on (the same snapshots the instrument-model checks use: made-up ones, and real games from the user's library when they are there - never committed) and the little helpers that turn an
// instrument into a preset the way the Instruments tab's "Send to Presets" does (the instrument as it is now, the clean one, the history with its seeds, the context).
using System.Security.Cryptography;
using System.Text;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

internal sealed record PresetSource(string Name, object Snapshot, bool Real, string Game, string Sha1);

internal static class PresetFixtures
{
    public static string Sha1Of(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static readonly Dictionary<string, string> Games = new()
    {
        ["snes-testsample"] = "Made-up SNES game.sfc", ["snes-samples"] = "Made-up SNES game (four samples).sfc", ["gb-dmg"] = "Made-up Game Boy game.gb", ["gb-cgb"] = "Made-up Game Boy Color game.gbc",
        ["psg"] = "Made-up Master System game.sms", ["opll"] = "Made-up Master System FM game.sms", ["opn"] = "Made-up Genesis game.md", ["nes"] = "Made-up NES game.nes",
        ["smw"] = "Super Mario World (U) [!].smc", ["sonic1"] = "Sonic the Hedgehog (JUE) [!].bin", ["zelda-gb"] = "Zelda - Link's Awakening (G) [!].gb", ["dkc"] = "Donkey Kong Country (U) (V1.2).smc",
        ["drmario-gbc"] = "Dr. Mario DX.gbc", ["addams-gg"] = "Addams Family, The (UE).gg", ["alexkidd-sms"] = "Alex Kidd in Miracle World.sms", ["argos-smsfm"] = "Argos no Juujiken (J).sms", ["contra-nes"] = "Contra (U).nes",
    };

    public static List<PresetSource> Synthetic() => ImodelFixtures.Synthetic().Select(f => new PresetSource(f.Name, f.Snapshot, false, Games.GetValueOrDefault(f.Name, f.Name), Sha1Of(f.Name))).ToList();

    public static List<PresetSource> Real() => ImodelFixtures.Real().Select(f => new PresetSource(f.Name, f.Snapshot, true, Games.GetValueOrDefault(f.Name, f.Name), Sha1Of(f.Name))).ToList();

    public static List<PresetSource> All() => [.. Synthetic(), .. Real()];

    public const long Created = 1_792_000_000;                // a fixed time: the library never reads a clock, and the checks need the same bytes every run

    /// <summary>The history of an instrument with <paramref name="steps"/> corruption operations of every kind, seeded from <paramref name="seed"/>.</summary>
    public static InstrumentHistory Corrupt(Instrument inst, int steps, ulong seed)
    {
        var h = new InstrumentHistory(inst);
        for (int i = 0; i < steps; i++)
        {
            ulong s = seed * 1_000_003UL + (ulong)i * 7919UL + 11;
            switch (i % 3)
            {
                case 0: h.Randomise(0.35, s, true); break;
                case 1: h.Nudge("", 1 + i % 3, s, 1, true); break;
                default: h.FlipBits("", 1 + i % 4, s, true); break;
            }
        }
        return h;
    }

    /// <summary>What "Send to Presets" makes of an instrument after <paramref name="steps"/> corruptions.</summary>
    public static PresetData Make(PresetSource src, Instrument inst, int steps, ulong seed, string? name = null)
    {
        var h = Corrupt(inst, steps, seed);
        var now = h.Current;
        var d = PresetMaker.Make(name ?? $"{inst.Name} ({(steps > 0 ? "corrupted" : "clean")})", inst.Name, src.Game, src.Sha1, steps == 0 ? "As the game had it." : $"Corrupted: {steps} steps from the game's own.", Created,
            InstrumentCodec.Pack(now), InstrumentCodec.Pack(h.Clean), h.Serialize(), PresetContext.Of(src.Snapshot, now), out string? problem);
        if (d == null) throw new InvalidOperationException("could not make a preset: " + problem);
        return d;
    }

    public static IReadOnlyList<Instrument> Instruments(PresetSource s) => ImodelChecks.Src(s.Snapshot).List();
}

internal static class PresetRaw
{
    /// <summary>A preset container assembled by hand (the format of PresetFile), with the checksum fixed or not: the way the checks make files that are damaged in ways a checksum cannot see.</summary>
    public static byte[] Build(int version, int flags, byte[] header, IEnumerable<(uint Tag, byte[] Data)> blobs, bool fixCrc = true, byte[]? trailing = null, int? headerLengthLie = null)
    {
        var list = blobs.ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x50324E42u); w.Write((ushort)version); w.Write((ushort)flags); w.Write((uint)(headerLengthLie ?? header.Length));
        w.Write(header);
        w.Write((ushort)list.Count);
        foreach (var (tag, data) in list) { w.Write(tag); w.Write((uint)data.Length); w.Write(data); }
        if (trailing != null) w.Write(trailing);
        w.Flush();
        var body = ms.ToArray();
        var all = new byte[body.Length + 4];
        body.CopyTo(all, 0);
        uint crc = fixCrc ? InstrumentCrc.Of(body) : 0xDEADBEEF;
        BitConverter.GetBytes(crc).CopyTo(all, body.Length);
        return all;
    }

    /// <summary>The parts of a written preset file, pulled apart: header bytes and the (tag, data) list.</summary>
    public static (byte[] Header, List<(uint Tag, byte[] Data)> Blobs) Split(byte[] file)
    {
        int hl = (int)BitConverter.ToUInt32(file, 8);
        var header = file.AsSpan(12, hl).ToArray();
        int p = 12 + hl, count = file[p] | file[p + 1] << 8; p += 2;
        var blobs = new List<(uint, byte[])>();
        for (int i = 0; i < count; i++)
        {
            uint tag = BitConverter.ToUInt32(file, p); int len = (int)BitConverter.ToUInt32(file, p + 4); p += 8;
            blobs.Add((tag, file.AsSpan(p, len).ToArray())); p += len;
        }
        return (header, blobs);
    }

    public static byte[] WithHeader(byte[] file, Func<string, string> edit)
    {
        var (h, blobs) = Split(file);
        return Build(1, 0, Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(h))), blobs);
    }

    public static byte[] WithBlob(byte[] file, uint tag, Func<byte[], byte[]> edit)
    {
        var (h, blobs) = Split(file);
        for (int i = 0; i < blobs.Count; i++) if (blobs[i].Tag == tag) blobs[i] = (tag, edit(blobs[i].Data));
        return Build(1, 0, h, blobs);
    }

    public static byte[] WithoutBlob(byte[] file, uint tag)
    {
        var (h, blobs) = Split(file);
        blobs.RemoveAll(b => b.Tag == tag);
        return Build(1, 0, h, blobs);
    }
}
