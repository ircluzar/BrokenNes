namespace NesEmulator.Plugin;

/// <summary>
/// Symbols from a Mesen label file (.mlb), as NESFab writes next to a ROM. Plugin hosts find
/// every address of a driver ROM by NAME (e.g. VRUN's live-audio mailbox, <c>lb_*</c>), so
/// nothing is hard-coded on either side.
/// <para>Line format: <c>Type:Address[-End]:Name[:Comment]</c>. Types used here:
/// <c>R</c> / <c>NesInternalRam</c> = CPU RAM ($0000-$07FF), <c>P</c> / <c>NesPrgRom</c> = PRG-ROM
/// file offset (after the iNES header).</para>
/// </summary>
public sealed class MlbSymbols
{
    private readonly Dictionary<string, (int Start, int End)> ram = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Start, int End)> prg = new(StringComparer.Ordinal);

    public static MlbSymbols Parse(string text)
    {
        var s = new MlbSymbols();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(':');
            if (parts.Length < 3 || parts[2].Length == 0) continue;
            var range = parts[1].Split('-');
            if (!int.TryParse(range[0], System.Globalization.NumberStyles.HexNumber, null, out int start)) continue;
            int end = start;
            if (range.Length > 1 && !int.TryParse(range[1], System.Globalization.NumberStyles.HexNumber, null, out end)) continue;
            var table = parts[0] switch
            {
                "R" or "NesInternalRam" => s.ram,
                "P" or "NesPrgRom" => s.prg,
                _ => null,
            };
            table?.TryAdd(parts[2], (start, end));
        }
        return s;
    }

    public int RamCount => ram.Count;
    public int PrgCount => prg.Count;

    /// <summary>CPU RAM address of a symbol, and its length in bytes.</summary>
    public bool TryGetRam(string name, out ushort address, out int length)
    {
        if (ram.TryGetValue(name, out var r)) { address = (ushort)r.Start; length = r.End - r.Start + 1; return true; }
        address = 0; length = 0; return false;
    }

    /// <summary>PRG-ROM file offset (after the 16-byte iNES header) of a symbol, and its length.</summary>
    public bool TryGetPrg(string name, out int offset, out int length)
    {
        if (prg.TryGetValue(name, out var r)) { offset = r.Start; length = r.End - r.Start + 1; return true; }
        offset = 0; length = 0; return false;
    }

    public ushort Ram(string name) =>
        TryGetRam(name, out var a, out _) ? a : throw new KeyNotFoundException($"RAM symbol '{name}' not in the .mlb");
}
