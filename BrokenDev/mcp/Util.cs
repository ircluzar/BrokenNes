using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrokenNes.Mcp;

/// <summary>An error whose message is meant for the agent (a refused control, a bad address): returned as the tool's error, never a crash.</summary>
internal sealed class ToolError : Exception
{
    public ToolError(string message) : base(message) { }
}

/// <summary>A tool's arguments, read leniently: numbers may come as JSON numbers or as text ("$7E", "0x7E", "126").</summary>
internal sealed class Args
{
    private readonly IDictionary<string, JsonElement> d;
    public Args(IDictionary<string, JsonElement>? d) { this.d = d ?? new Dictionary<string, JsonElement>(); }
    public static Args From(object? anonymous)
    {
        var el = JsonSerializer.SerializeToElement(anonymous ?? new { });
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in el.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        return new Args(dict);
    }

    public bool Has(string k) => d.TryGetValue(k, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public string? Str(string k)
    {
        if (!d.TryGetValue(k, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public string Req(string k) => Str(k) is { Length: > 0 } s ? s : throw new ToolError($"'{k}' is required");

    public long? Long(string k)
    {
        if (!d.TryGetValue(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out long n) ? n : (long)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String) return Num.Parse(v.GetString()!, k);
        return null;
    }

    public int Int(string k, int def) => Long(k) is long n ? (int)n : def;
    public long LongOr(string k, long def) => Long(k) ?? def;

    public double? Double(string k)
    {
        if (!d.TryGetValue(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return x;
        return null;
    }

    public bool Bool(string k, bool def)
    {
        if (!d.TryGetValue(k, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()!.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on",
            JsonValueKind.Number => v.GetDouble() != 0,
            _ => def,
        };
    }

    /// <summary>A list: a JSON array of strings or one comma-separated string.</summary>
    public List<string> List(string k)
    {
        var o = new List<string>();
        if (!d.TryGetValue(k, out var v)) return o;
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) o.Add(e.GetString()!); else if (e.ValueKind == JsonValueKind.Number) o.Add(e.GetRawText());
        }
        else if (v.ValueKind == JsonValueKind.String)
            o.AddRange(v.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return o;
    }

    /// <summary>Bytes: a JSON array of numbers, or a hex string ("A9 00 8D" / "A9008D").</summary>
    public byte[]? Bytes(string k)
    {
        if (!d.TryGetValue(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? (byte)e.GetInt32() : (byte)Num.Parse(e.GetString()!, k)).ToArray();
        if (v.ValueKind == JsonValueKind.Number) return new[] { (byte)v.GetInt32() };
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = new(v.GetString()!.Where(Uri.IsHexDigit).ToArray());
            if (s.Length % 2 != 0) throw new ToolError($"'{k}': an even number of hex digits is needed");
            return Convert.FromHexString(s);
        }
        return null;
    }
}

internal static class Num
{
    /// <summary>"$7E", "0x7E", "7Eh", "126", "%0101" -> the number.</summary>
    public static long Parse(string s, string what = "number")
    {
        s = s.Trim();
        try
        {
            if (s.StartsWith('$')) return long.Parse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (s.EndsWith('h') || s.EndsWith('H')) return long.Parse(s[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (s.StartsWith('%')) return Convert.ToInt64(s[1..], 2);
            if (s.StartsWith('-')) return long.Parse(s, CultureInfo.InvariantCulture);
            return long.Parse(s, CultureInfo.InvariantCulture);
        }
        catch (FormatException) { throw new ToolError($"'{s}' is not a number ({what}: decimal, $hex or 0xhex)"); }
    }

    public static string Hex(long v, int digits = 2) => "$" + v.ToString("X" + digits, CultureInfo.InvariantCulture);
    public static string HexBytes(ReadOnlySpan<byte> b) { var sb = new StringBuilder(b.Length * 3); for (int i = 0; i < b.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); } return sb.ToString(); }
}

/// <summary>JSON results: a JSON object for the structured part, PNG images beside it.</summary>
internal sealed class ToolOut
{
    public JsonObject Json { get; }
    public List<byte[]> Pngs { get; } = new();
    public ToolOut(JsonObject json) { Json = json; }
    public static implicit operator ToolOut(JsonObject j) => new(j);
}

internal static class J
{
    public static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.ToArray());
    public static JsonArray Strs(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    public static JsonNode? From(object? o) => o == null ? null : JsonSerializer.SerializeToNode(o);
    public static string Text(JsonNode n) => n.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

/// <summary>A minimal PNG writer (8-bit RGB, no filter), so pictures go back as images without System.Drawing.</summary>
internal static class Png
{
    private static readonly uint[] Crc = MakeCrc();
    private static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }

    /// <summary>Encodes 0xAARRGGBB pixels (alpha ignored), scaled up by an integer factor (nearest neighbour).</summary>
    public static byte[] FromArgb(uint[] argb, int w, int h, int scale = 1)
    {
        scale = Math.Clamp(scale, 1, 8);
        int W = w * scale, H = h * scale;
        var raw = new byte[(W * 3 + 1) * H];
        int o = 0;
        for (int y = 0; y < H; y++)
        {
            raw[o++] = 0;
            int sy = y / scale;
            for (int x = 0; x < W; x++)
            {
                uint p = argb[sy * w + x / scale];
                raw[o++] = (byte)(p >> 16); raw[o++] = (byte)(p >> 8); raw[o++] = (byte)p;
            }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)W); BE(ihdr, 4, (uint)H); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void BE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint c = 0xFFFFFFFFu;
        foreach (byte b in t) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4]; BE(crc, 0, c ^ 0xFFFFFFFFu); s.Write(crc);
    }

    /// <summary>The picture's FNV-1a 64 over its pixels and how many colours it has.</summary>
    public static (string hash, int colours) Stats(uint[] argb, int count)
    {
        ulong h = 14695981039346656037UL;
        var seen = new HashSet<uint>();
        for (int i = 0; i < count; i++) { h = (h ^ (argb[i] & 0xFFFFFF)) * 1099511628211UL; if (seen.Count <= 4096) seen.Add(argb[i] & 0xFFFFFF); }
        return (h.ToString("X16"), seen.Count);
    }
}

/// <summary>Writes 16-bit PCM WAV files.</summary>
internal static class Wav
{
    public static void Write(string path, short[] samples, int count, int rate, int channels)
    {
        using var f = new BinaryWriter(File.Create(path));
        int bytes = count * 2;
        f.Write(Encoding.ASCII.GetBytes("RIFF")); f.Write(36 + bytes); f.Write(Encoding.ASCII.GetBytes("WAVE"));
        f.Write(Encoding.ASCII.GetBytes("fmt ")); f.Write(16); f.Write((short)1); f.Write((short)channels); f.Write(rate); f.Write(rate * channels * 2); f.Write((short)(channels * 2)); f.Write((short)16);
        f.Write(Encoding.ASCII.GetBytes("data")); f.Write(bytes);
        for (int i = 0; i < count; i++) f.Write(samples[i]);
    }
}
