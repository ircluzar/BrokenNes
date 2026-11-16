using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrokenNes.Windows.AutoTas
{
    /// <summary>
    /// One game variable from the game's dossier (BrokenDev/tools/autotas/games/&lt;game&gt;/dossier.json), copied into the package as
    /// variables.json (either the dossier itself or {"variables": [...]}). The AutoTAS variables stripe shows them live, priority 1 first.
    /// <see cref="Display"/> is optional: without it the formatting is read from <see cref="Format"/>/<see cref="Read"/> (the dossier's words).
    /// </summary>
    public sealed class AutoTasVariable
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("address")] public string Address { get; set; } = "";
        [JsonPropertyName("size")] public int Size { get; set; } = 1;
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("meaning")] public string? Meaning { get; set; }
        [JsonPropertyName("read")] public string? Read { get; set; }
        [JsonPropertyName("priority")] public int Priority { get; set; } = 3;
        [JsonPropertyName("verified")] public bool Verified { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("display")] public AutoTasDisplay? Display { get; set; }
    }

    /// <summary>
    /// Explicit formatting: kind = u8 | s8 | hex | le | be | digits | hilo | popcount | bytes | enum | bits; hi = the high byte's address (hilo);
    /// scale multiplies; mask ANDs first; enum maps values to names; bits names the bits (bit 0 first) and shows the set ones.
    /// </summary>
    public sealed class AutoTasDisplay
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("hi")] public string? Hi { get; set; }
        [JsonPropertyName("scale")] public long? Scale { get; set; }
        [JsonPropertyName("mask")] public long? Mask { get; set; }
        [JsonPropertyName("plus")] public long? Plus { get; set; }
        [JsonPropertyName("enum")] public Dictionary<string, string>? Enum { get; set; }
        [JsonPropertyName("bits")] public List<string>? Bits { get; set; }
    }

    /// <summary>A variable made ready to show: its addresses parsed and its formatter chosen once.</summary>
    public sealed class AutoTasVariableView
    {
        public AutoTasVariable Var { get; }
        public int Addr { get; }
        public int Hi { get; } = -1;
        private readonly string kind;
        private readonly long scale, plus;
        private readonly long? mask;
        private readonly Dictionary<long, string>? names;
        private readonly List<string>? bits;

        public AutoTasVariableView(AutoTasVariable v)
        {
            Var = v;
            Addr = ParseAddr(v.Address);
            var d = v.Display;
            string fmt = (v.Format ?? "").ToLowerInvariant(), read = (v.Read ?? "").ToLowerInvariant();
            scale = d?.Scale ?? (Regex.IsMatch(fmt, @"\bx10\b") || read.Contains("*10") && !read.Contains("*100") ? 10 : 1);
            plus = d?.Plus ?? (Regex.Match(read, @"\]\s*\+\s*(\d+)\s*$") is { Success: true } m ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
            mask = d?.Mask;
            bits = d?.Bits;
            if (d?.Hi != null) Hi = ParseAddr(d.Hi);
            else if (Regex.Match(fmt, @"high byte \$([0-9a-f]{4})") is { Success: true } hm && !read.Contains("per slot")) Hi = ParseAddr("$" + hm.Groups[1].Value);
            if (d?.Enum != null) names = d.Enum.ToDictionary(kv => ParseNum(kv.Key), kv => kv.Value);
            else if (fmt.StartsWith("enum") || fmt == "same enum") names = ParseEnum(fmt);
            kind = d?.Kind?.ToLowerInvariant() ?? Guess(v, fmt, read);
            // a tail "_map" copy says "same enum": borrow nothing, show the number (the stripe borrows the sibling's names in Load)
        }

        internal void BorrowNames(AutoTasVariableView other) { if (names == null && other.names != null) { namesBorrowed = other.names; } }
        private Dictionary<long, string>? namesBorrowed;

        private static string Guess(AutoTasVariable v, string fmt, string read)
        {
            if (fmt.StartsWith("enum") || fmt == "same enum") return "enum";
            if (fmt.Contains("decimal digits")) return "digits";
            if (fmt.Contains("big-endian")) return "be";
            if (fmt.Contains("little-endian")) return "le";
            if (read.Contains("popcount")) return v.Size > 1 ? "popcount" : "pmeter";
            if (fmt.StartsWith("s8") || read.StartsWith("signed")) return "s8";
            if (v.Size > 4) return "bytes";
            if (v.Size > 1) return read == "bytes" || fmt.Contains("per slot") ? "bytes" : "le";
            return "u8";
        }

        public static int ParseAddr(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("$")) s = s[1..]; else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int a) ? a : -1;
        }

        private static long ParseNum(string s) => s.StartsWith("$") || s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ParseAddr(s) : long.TryParse(s, out long n) ? n : -1;

        /// <summary>"enum 0 small,1 big,2 fire" -> {0: small, 1: big, 2: fire}.</summary>
        private static Dictionary<long, string>? ParseEnum(string fmt)
        {
            var d = new Dictionary<long, string>();
            foreach (Match m in Regex.Matches(fmt, @"(\$?[0-9a-f]+)\s+([a-z][\w\- ]*)"))
                if (!m.Groups[1].Value.Equals("enum", StringComparison.OrdinalIgnoreCase)) d[ParseNum(m.Groups[1].Value)] = m.Groups[2].Value.Trim();
            return d.Count > 0 ? d : null;
        }

        /// <summary>The value as text, reading bytes with <paramref name="peek"/> (an address -> byte, side-effect free; -1 = not readable).</summary>
        public string Format(Func<int, int> peek)
        {
            if (Addr < 0) return "?";
            int n = Math.Max(1, Var.Size);
            int B(int i) => peek(Addr + i);
            if (B(0) < 0) return "-";
            string unit = string.IsNullOrEmpty(Var.Unit) ? "" : " " + Var.Unit;
            long Post(long x) { if (mask is long mk) x &= mk; return x * scale + plus; }
            switch (kind)
            {
                case "s8": return ((sbyte)(byte)B(0)).ToString(CultureInfo.InvariantCulture) + unit;
                case "hex": return "$" + string.Concat(Enumerable.Range(0, n).Reverse().Select(i => B(i).ToString("X2")));
                case "digits": { long x = 0; for (int i = 0; i < n; i++) x = x * 10 + (B(i) & 0x0F); return Post(x).ToString(CultureInfo.InvariantCulture) + unit; }
                case "be": { long x = 0; for (int i = 0; i < n; i++) x = (x << 8) | (byte)B(i); return Post(x).ToString("N0", CultureInfo.InvariantCulture) + unit; }
                case "le": { long x = 0; for (int i = n - 1; i >= 0; i--) x = (x << 8) | (byte)B(i); if (Hi >= 0) x |= (long)(byte)peek(Hi) << 8; return Post(x).ToString(CultureInfo.InvariantCulture) + unit; }
                case "popcount": { int c = 0; for (int i = 0; i < n; i++) c += BitOperations.PopCount((uint)(byte)B(i)); return c.ToString(CultureInfo.InvariantCulture) + unit; }
                case "pmeter": { int b = B(0); return new string('>', BitOperations.PopCount((uint)(b & 0x3F))).PadRight(6, '.') + ((b & 0x40) != 0 ? " P" : ""); }
                case "bytes": { var parts = Enumerable.Range(0, Math.Min(n, 8)).Select(i => B(i).ToString("X2")); return string.Join(" ", parts) + (n > 8 ? " .." : ""); }
                case "bits":
                {
                    int b = B(0); var on = new List<string>();
                    for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) on.Add(bits != null && i < bits.Count ? bits[i] : "b" + i);
                    return on.Count == 0 ? "-" : string.Join(",", on);
                }
                case "enum":
                {
                    long x = Post((byte)B(0));
                    var map = names ?? namesBorrowed;
                    return map != null && map.TryGetValue(x, out var nm) ? $"{nm} ({x})" : x.ToString(CultureInfo.InvariantCulture);
                }
                default:
                {
                    long x = (byte)B(0);
                    if (Hi >= 0) x |= (long)(byte)peek(Hi) << 8;
                    return Post(x).ToString(CultureInfo.InvariantCulture) + unit;
                }
            }
        }
    }

    /// <summary>The package's variables.json (or a dossier.json beside it), parsed, priority 1 first.</summary>
    public static class AutoTasVariables
    {
        private sealed class Doc { [JsonPropertyName("variables")] public List<AutoTasVariable>? Variables { get; set; } }

        public static List<AutoTasVariableView> Load(AutoTasPackage p, out string note)
        {
            note = "";
            string? path = p.ExistingPath("variables.json") ?? p.ExistingPath("dossier.json");
            if (path == null) { note = "this package has no variables.json"; return new(); }
            try
            {
                string text = File.ReadAllText(path).TrimStart();
                var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                var vars = text.StartsWith("[") ? JsonSerializer.Deserialize<List<AutoTasVariable>>(text, opt) : JsonSerializer.Deserialize<Doc>(text, opt)?.Variables;
                var views = (vars ?? new()).Where(v => v.Name.Length > 0).Select((v, i) => (v, i)).OrderBy(t => t.v.Priority).ThenBy(t => t.i).Select(t => new AutoTasVariableView(t.v)).ToList();
                // "same enum": a "<name>_map"-style twin borrows the names of the variable whose name it starts with
                foreach (var v in views)
                    if ((v.Var.Format ?? "").Trim().Equals("same enum", StringComparison.OrdinalIgnoreCase))
                        foreach (var o in views) if (!ReferenceEquals(o, v) && v.Var.Name.StartsWith(o.Var.Name, StringComparison.OrdinalIgnoreCase)) v.BorrowNames(o);
                if (views.Count == 0) note = "variables.json lists no variables";
                return views;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { note = "variables.json: " + e.Message; return new(); }
        }
    }
}
