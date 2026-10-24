using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace NesEmulator.Debugging;

/// <summary>
/// A symbol from a build: where it lives (<see cref="Domain"/> + <see cref="Address"/>, with a <see cref="Bank"/> for banked Game Boy
/// addresses) and how many bytes it covers. Domains, console-neutral: "cpu" (a CPU address: 16-bit, or 24-bit on the SNES), "prg"
/// (a NES PRG ROM offset), "ram" (NES internal RAM), "sram" (cartridge RAM offset), "rom" (ROM offset), "wram", "io", "spc".
/// The machine binds a symbol to one of its canonical spaces (<see cref="DebugCpu.BindSymbol"/>).
/// </summary>
public sealed class DebugSymbol
{
    public string Name { get; init; } = "";
    public string Domain { get; init; } = "cpu";
    public uint Address { get; init; }
    /// <summary>The bank of a banked CPU address (GBDK / WLA on the Game Boy), or -1.</summary>
    public int Bank { get; init; } = -1;
    public int Size { get; internal set; } = 1;
    /// <summary>False when <see cref="Size"/> was inferred (distance to the next symbol) rather than given by the file.</summary>
    public bool SizeKnown { get; internal set; }
    public string File { get; init; } = "";
    public string? Comment { get; init; }
    public override string ToString() => $"{Name} = {Domain}:{(Bank >= 0 ? $"{Bank:X2}:" : "")}${Address:X} ({Size} byte{(Size == 1 ? "" : "s")}{(SizeKnown ? "" : ", inferred")})";
}

/// <summary>The symbols loaded into a debugger, from any number of files. Names are case-sensitive, with a case-insensitive fallback when unambiguous.</summary>
public sealed class SymbolTable
{
    private readonly List<DebugSymbol> all = new();
    private readonly Dictionary<string, DebugSymbol> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DebugSymbol>> byLower = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DebugSymbol>> byBase = new(StringComparer.Ordinal);
    public IReadOnlyList<DebugSymbol> All => all;
    public int Version { get; private set; }
    public List<string> Files { get; } = new();

    /// <summary>Loads a symbol file, choosing the reader by its extension and content: .mlb (Mesen / NESFab), .sym (WLA-DX, no$ / bgb style,
    /// which GBDK and RGBDS write too), .noi (GBDK / SDCC). Returns how many symbols it added.</summary>
    public int Load(string path, string console)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var lines = File.ReadAllLines(path);
        List<DebugSymbol> got = ext switch
        {
            ".mlb" => ReadMlb(lines, path),
            ".noi" => ReadNoi(lines, path),
            _ => lines.Any(l => l.TrimStart().StartsWith("DEF ", StringComparison.Ordinal)) ? ReadNoi(lines, path) : ReadSym(lines, path, console),
        };
        InferSizes(got);
        foreach (var s in got) Add(s);
        Files.Add(path);
        Version++;
        return got.Count;
    }

    public void Add(DebugSymbol s)
    {
        all.Add(s);
        byName[s.Name] = s;
        string lower = s.Name.ToLowerInvariant();
        if (!byLower.TryGetValue(lower, out var l)) byLower[lower] = l = new List<DebugSymbol>();
        l.Add(s);
        int at = s.Name.IndexOf('@');
        if (at > 0)
        {
            string b = s.Name[..at];
            if (!byBase.TryGetValue(b, out var bl)) byBase[b] = bl = new List<DebugSymbol>();
            bl.Add(s);
        }
        Version++;
    }

    /// <summary>Finds a symbol by name: exact; then case-insensitive; then a NESFab base name ("rand" for "rand@31_0") when only one symbol has it.
    /// Ambiguity is an error naming the candidates, never a guess.</summary>
    public bool TryFind(string name, out DebugSymbol sym, out string? error)
    {
        error = null;
        if (byName.TryGetValue(name, out sym!)) return true;
        if (byLower.TryGetValue(name.ToLowerInvariant(), out var l))
        {
            if (l.Count == 1) { sym = l[0]; return true; }
            error = $"'{name}' matches {l.Count} symbols that differ only in case: {string.Join(", ", l.Take(6).Select(x => x.Name))}";
            return false;
        }
        if (byBase.TryGetValue(name, out var bl))
        {
            // a NESFab variable's bytes (name@0, name@1, ...) are one variable: its first byte, with the size of all of them
            var bytes = bl.Where(x => Regex.IsMatch(x.Name, "@\\d+$")).ToList();
            if (bytes.Count == bl.Count && bytes.Count > 0)
            {
                var first = bytes.OrderBy(x => int.Parse(x.Name[(x.Name.LastIndexOf('@') + 1)..], CultureInfo.InvariantCulture)).First();
                sym = new DebugSymbol { Name = name, Domain = first.Domain, Address = first.Address, Bank = first.Bank, Size = bytes.Count, SizeKnown = true, File = first.File };
                return true;
            }
            if (bl.Count == 1) { sym = bl[0]; return true; }
            error = $"'{name}' is ambiguous: {string.Join(", ", bl.Take(8).Select(x => x.Name))}{(bl.Count > 8 ? ", ..." : "")}";
            return false;
        }
        error = $"no symbol named '{name}'";
        return false;
    }

    // ------------------------------------------------------------------------------------------------------------- Mesen .mlb (NESFab)
    // Lines "TYPE:ADDRESS[-END]:LABEL[:COMMENT]". Mesen 1 types are one letter (P R S W G), Mesen 2 types are names (NesPrgRom, ...).
    private static List<DebugSymbol> ReadMlb(string[] lines, string file)
    {
        var list = new List<DebugSymbol>();
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            var parts = line.Split(':');
            if (parts.Length < 3 || parts[2].Length == 0) continue;
            string domain = parts[0] switch
            {
                "P" or "NesPrgRom" => "prg",
                "R" or "NesInternalRam" => "ram",
                "S" or "NesSaveRam" or "W" or "NesWorkRam" => "sram",
                "G" or "NesMemory" or "NesRegister" => "cpu",
                "SnesPrgRom" or "GbPrgRom" => "rom",
                "SnesWorkRam" or "GbWorkRam" => "wram",
                "SnesSaveRam" or "GbCartRam" => "sram",
                "SnesRegister" or "GbRegister" => "io",
                "SpcRam" => "spc",
                "GbHighRam" => "hram",
                "SnesMemory" or "GbMemory" or "SpcMemory" => "cpu",
                _ => "",
            };
            if (domain == "") continue;
            string addrText = parts[1];
            int dash = addrText.IndexOf('-');
            if (!uint.TryParse(dash > 0 ? addrText[..dash] : addrText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint start)) continue;
            uint end = start;
            if (dash > 0 && !uint.TryParse(addrText[(dash + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out end)) end = start;
            list.Add(new DebugSymbol
            {
                Name = parts[2], Domain = domain, Address = start, Size = (int)(end - start + 1), SizeKnown = dash > 0, File = file,
                Comment = parts.Length > 3 && parts[3].Length > 0 ? string.Join(":", parts.Skip(3)) : null,
            });
        }
        // NESFab names each byte of a variable (pads@0, pads@1, pads@2): those are one-byte symbols for certain
        foreach (var s in list) if (Regex.IsMatch(s.Name, "@\\d+$") && s.Domain is "ram" or "sram") s.SizeKnown = true;
        return list;
    }

    // ------------------------------------------------------------------------------------------------------------- WLA-DX / no$ / bgb .sym
    // "BBAAAAAA name" (8 hex digits, no$snes as wlalink writes it) or "bb:aaaa name" (WLA's [labels], bgb / GBDK / RGBDS). Sections
    // other than [labels] (definitions, breakpoints, source maps) are skipped: a definition is a constant, not a place.
    private static readonly Regex Colon = new(@"^([0-9A-Fa-f]{1,4}):([0-9A-Fa-f]{1,6})\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex Flat = new(@"^([0-9A-Fa-f]{6,8})\s+(\S+)", RegexOptions.Compiled);

    private static List<DebugSymbol> ReadSym(string[] lines, string file, string console)
    {
        var list = new List<DebugSymbol>();
        string section = "[labels]";
        bool gb = console is "gb" or "gbc";
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[")) { section = line.ToLowerInvariant(); continue; }
            if (section != "[labels]") continue;
            var m = Colon.Match(line);
            if (m.Success)
            {
                int bank = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint addr = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                list.Add(gb ? new DebugSymbol { Name = m.Groups[3].Value, Domain = "cpu", Address = addr & 0xFFFF, Bank = bank, File = file }
                            : new DebugSymbol { Name = m.Groups[3].Value, Domain = "cpu", Address = (uint)bank << 16 | (addr & 0xFFFF), File = file });
                continue;
            }
            m = Flat.Match(line);
            if (m.Success)
            {
                uint v = uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                list.Add(gb ? new DebugSymbol { Name = m.Groups[2].Value, Domain = "cpu", Address = v & 0xFFFF, Bank = (int)(v >> 16), File = file }
                            : new DebugSymbol { Name = m.Groups[2].Value, Domain = "cpu", Address = v & 0xFFFFFF, File = file });
            }
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------------------------- GBDK / SDCC .noi
    // "DEF name 0xVALUE": a banked address carries its bank above bit 16 (0x14000 = bank 1, $4000). The linker also writes area starts
    // (s__X), lengths (l__X) and bank numbers (b_X, ___bank_X): constants, skipped.
    private static List<DebugSymbol> ReadNoi(string[] lines, string file)
    {
        var list = new List<DebugSymbol>();
        foreach (var raw in lines)
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "DEF") continue;
            string name = parts[1];
            if (name.StartsWith("l__") || name.StartsWith("s__") || name.StartsWith("b_") || name.StartsWith("___bank") || name.StartsWith(".__.")) continue;
            if (!DebugExpr.TryParseNumber(parts[2], out long v)) continue;
            list.Add(new DebugSymbol { Name = name, Domain = "cpu", Address = (uint)(v & 0xFFFF), Bank = (int)(v >> 16), File = file });
        }
        return list;
    }

    // A file without sizes: each symbol runs to the next one in the same place (capped), and a symbol sharing its address with the next is size 1.
    private static void InferSizes(List<DebugSymbol> list)
    {
        foreach (var group in list.Where(s => !s.SizeKnown).GroupBy(s => (s.Domain, s.Bank)))
        {
            var sorted = list.Where(s => s.Domain == group.Key.Domain && s.Bank == group.Key.Bank).Select(s => s.Address).Distinct().OrderBy(a => a).ToArray();
            foreach (var s in group)
            {
                int idx = Array.BinarySearch(sorted, s.Address);
                if (idx >= 0 && idx + 1 < sorted.Length)
                {
                    uint gap = sorted[idx + 1] - s.Address;
                    s.Size = (int)Math.Min(gap, 0x100);
                }
                else s.Size = 1;
            }
        }
    }
}
