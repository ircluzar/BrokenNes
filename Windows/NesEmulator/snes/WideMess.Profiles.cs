using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NesEmulator.Snes;

/// <summary>The four console enhancements of a SNES game on the WideMess (docs/projects/widemess-spec.md section 6.1).</summary>
public enum WmsEnhancement { Widescreen, NoFlicker, BetterSound, FastCpu }

/// <summary>A profile's verdict on an enhancement (spec 6.2).</summary>
public enum WmsVerdict
{
    /// <summary>Not offered for this game.</summary>
    Unavailable,
    /// <summary>Verified.</summary>
    Good,
    /// <summary>Works, with the issues the profile lists.</summary>
    Risky,
    /// <summary>Verified not to work; still offered, labelled.</summary>
    Broken,
}

/// <summary>A SNES game's WideMess profile (spec 6.2): <c>WideMess/profiles/*.json</c>, format <c>widemess-profile-1</c>.</summary>
public sealed class WmsProfile
{
    public const string Format = "widemess-profile-1";
    public static readonly string[] EnhancementKeys = { "widescreen", "noFlicker", "betterSound", "fastCpu" };

    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>The ROM file it was made with (information only).</summary>
    public string File { get; init; } = "";
    public string[] Sha1 { get; init; } = Array.Empty<string>();
    public WmsWidescreen? Widescreen { get; init; }
    public WmsVerdict[] Verdicts { get; init; } = new WmsVerdict[4];
    public WmsEnhancement[] Recommended { get; init; } = Array.Empty<WmsEnhancement>();
    public string[] Issues { get; init; } = Array.Empty<string>();
    public string VerifiedDate { get; init; } = "";
    public string VerifiedHow { get; init; } = "";

    public WmsVerdict Verdict(WmsEnhancement e) => Verdicts[(int)e];

    public static string VerdictText(WmsVerdict v) => v switch { WmsVerdict.Good => "good", WmsVerdict.Risky => "risky", WmsVerdict.Broken => "broken", _ => "unavailable" };
    public static string Key(WmsEnhancement e) => EnhancementKeys[(int)e];

    /// <summary>A profile from its JSON. Every key is checked: an unknown key, a missing one or a bad value throws <see cref="InvalidDataException"/> with the reason (never half-read).</summary>
    public static WmsProfile Parse(string json, string source)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException e) { throw new InvalidDataException($"{source}: not valid JSON ({e.Message})"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: a profile is a JSON object");
            Keys(root, source, "the profile", new[] { "format", "title", "sha1", "enhancements", "recommended", "issues", "verified" }, new[] { "file", "widescreen" });
            string format = Str(root, "format", source);
            if (format != Format) throw new InvalidDataException($"{source}: format '{format}' (this BrokenNes reads {Format})");
            var sha = root.GetProperty("sha1");
            if (sha.ValueKind != JsonValueKind.Array || sha.GetArrayLength() == 0) throw new InvalidDataException($"{source}: sha1 is a list of at least one SHA-1");
            var hashes = new List<string>();
            foreach (var h in sha.EnumerateArray())
            {
                string s = h.ValueKind == JsonValueKind.String ? h.GetString()!.ToLowerInvariant() : "";
                if (s.Length != 40 || !s.All(Uri.IsHexDigit)) throw new InvalidDataException($"{source}: '{h}' is not a SHA-1 (40 hex digits)");
                hashes.Add(s);
            }
            var en = root.GetProperty("enhancements");
            if (en.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: enhancements is an object");
            Keys(en, source, "enhancements", EnhancementKeys, Array.Empty<string>());
            var verdicts = new WmsVerdict[4];
            for (int i = 0; i < 4; i++)
            {
                string v = Str(en, EnhancementKeys[i], source);
                verdicts[i] = v switch
                {
                    "good" => WmsVerdict.Good, "risky" => WmsVerdict.Risky, "broken" => WmsVerdict.Broken, "unavailable" => WmsVerdict.Unavailable,
                    _ => throw new InvalidDataException($"{source}: enhancements.{EnhancementKeys[i]} is '{v}' (good, risky, broken or unavailable)"),
                };
            }
            WmsWidescreen? wide = null;
            if (root.TryGetProperty("widescreen", out var w)) wide = ParseWidescreen(w, source);
            if (verdicts[0] != WmsVerdict.Unavailable && wide == null) throw new InvalidDataException($"{source}: widescreen is offered ({VerdictText(verdicts[0])}) but the profile has no widescreen rules");
            if (verdicts[0] == WmsVerdict.Unavailable && wide != null) throw new InvalidDataException($"{source}: widescreen rules for a game whose widescreen is unavailable");
            var rec = new List<WmsEnhancement>();
            foreach (var r in Arr(root, "recommended", source))
            {
                int i = Array.IndexOf(EnhancementKeys, r.ValueKind == JsonValueKind.String ? r.GetString() : null);
                if (i < 0) throw new InvalidDataException($"{source}: recommended lists '{r}' (one of {string.Join(", ", EnhancementKeys)})");
                if (verdicts[i] == WmsVerdict.Unavailable || verdicts[i] == WmsVerdict.Broken) throw new InvalidDataException($"{source}: recommends {EnhancementKeys[i]}, which it calls {VerdictText(verdicts[i])}");
                if (!rec.Contains((WmsEnhancement)i)) rec.Add((WmsEnhancement)i);
            }
            var issues = Arr(root, "issues", source).Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()! : throw new InvalidDataException($"{source}: issues are strings")).ToArray();
            var ver = root.GetProperty("verified");
            if (ver.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: verified is an object");
            Keys(ver, source, "verified", new[] { "date", "how" }, Array.Empty<string>());
            return new WmsProfile
            {
                Source = source,
                Title = Str(root, "title", source),
                File = root.TryGetProperty("file", out var f) ? (f.ValueKind == JsonValueKind.String ? f.GetString()! : throw new InvalidDataException($"{source}: file is a string")) : "",
                Sha1 = hashes.ToArray(),
                Widescreen = wide,
                Verdicts = verdicts,
                Recommended = rec.ToArray(),
                Issues = issues,
                VerifiedDate = Str(ver, "date", source),
                VerifiedHow = Str(ver, "how", source),
            };
        }
    }

    private static readonly string[] LayerKeys = { "bg1", "bg2", "bg3", "bg4", "sprites" };

    private static WmsWidescreen ParseWidescreen(JsonElement w, string source)
    {
        if (w.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: widescreen is an object");
        Keys(w, source, "widescreen", new[] { "layers" }, new[] { "masks", "windows", "drawnAhead" });
        var layers = w.GetProperty("layers");
        if (layers.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: widescreen.layers is an object");
        Keys(layers, source, "widescreen.layers", LayerKeys, Array.Empty<string>());
        var ws = new WmsWidescreen();
        for (int i = 0; i < 5; i++)
        {
            string r = Str(layers, LayerKeys[i], source);
            ws.Layers[i] = r switch
            {
                "extend" => WmsSideRule.Extend,
                "repeat" when i < 4 => WmsSideRule.Repeat,
                "fill" => WmsSideRule.Fill,
                "drawn" when i < 4 => WmsSideRule.Drawn,
                _ => throw new InvalidDataException($"{source}: widescreen.layers.{LayerKeys[i]} is '{r}' ({(i < 4 ? "extend, repeat, fill or drawn" : "extend or fill")})"),
            };
        }
        if (w.TryGetProperty("drawnAhead", out var da))
        {
            // { "bg1": 16 }: pixels past the SNES edge the game draws a drawn layer's map ahead of its scroll (default 16)
            if (da.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: widescreen.drawnAhead is an object of layer: pixels");
            Keys(da, source, "widescreen.drawnAhead", Array.Empty<string>(), LayerKeys[..4]);
            foreach (var p in da.EnumerateObject())
            {
                int i = Array.IndexOf(LayerKeys, p.Name);
                if (ws.Layers[i] != WmsSideRule.Drawn) throw new InvalidDataException($"{source}: widescreen.drawnAhead.{p.Name}: the layer's rule is not drawn");
                int px = Int(p.Value, $"widescreen.drawnAhead.{p.Name}", source);
                if (px < 0 || px > PPU_WMS.SnesOrigin) throw new InvalidDataException($"{source}: widescreen.drawnAhead.{p.Name} is {px} (0-72 pixels)");
                ws.DrawnAhead[i] = px;
            }
        }
        if (w.TryGetProperty("windows", out var wn))
        {
            string v = wn.ValueKind == JsonValueKind.String ? wn.GetString()! : "";
            ws.WindowsReachEdge = v switch { "reach" => true, "snes" => false, _ => throw new InvalidDataException($"{source}: widescreen.windows is '{v}' (reach or snes)") };
        }
        var masks = new List<WmsMask>();
        if (w.TryGetProperty("masks", out var ms))
        {
            if (ms.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{source}: widescreen.masks is a list");
            int n = 0;
            foreach (var m in ms.EnumerateArray())
            {
                string at = $"widescreen.masks[{n++}]";
                if (m.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: {at} is an object");
                Keys(m, source, at, new[] { "why", "layers", "rows", "rule" }, new[] { "side", "from", "to", "when" });
                var mask = new WmsMask { Why = Str(m, "why", source) };
                string rule = Str(m, "rule", source);
                mask.Rule = rule switch
                {
                    "fill" => WmsSideRule.Fill, "repeat" => WmsSideRule.Repeat, "black" => WmsSideRule.Black,
                    _ => throw new InvalidDataException($"{source}: {at}.rule is '{rule}' (fill, repeat or black)"),
                };
                foreach (var l in Arr(m, "layers", source))
                {
                    string name = l.ValueKind == JsonValueKind.String ? l.GetString()! : "";
                    if (name == "all") { mask.Layers |= 0x1F; continue; }
                    int i = Array.IndexOf(LayerKeys, name);
                    if (i < 0) throw new InvalidDataException($"{source}: {at}.layers lists '{l}' (bg1-bg4, sprites or all)");
                    mask.Layers |= 1 << i;
                }
                if (mask.Layers == 0) throw new InvalidDataException($"{source}: {at} covers no layer");
                if (mask.Rule == WmsSideRule.Black && mask.Layers != 0x1F) throw new InvalidDataException($"{source}: {at}: a black mask covers every layer (layers: [\"all\"])");
                if (mask.Rule == WmsSideRule.Repeat && (mask.Layers & 0x10) != 0) throw new InvalidDataException($"{source}: {at}: sprites cannot repeat");
                var rows = Arr(m, "rows", source).ToArray();
                if (rows.Length != 2 || rows.Any(r => r.ValueKind != JsonValueKind.Number)) throw new InvalidDataException($"{source}: {at}.rows is [top, bottom]");
                mask.Top = rows[0].GetInt32(); mask.Bottom = rows[1].GetInt32();
                if (mask.Top < 0 || mask.Bottom > 238 || mask.Top > mask.Bottom) throw new InvalidDataException($"{source}: {at}.rows {mask.Top}-{mask.Bottom} (0-238, top first)");
                string side = m.TryGetProperty("side", out var sd) ? (sd.ValueKind == JsonValueKind.String ? sd.GetString()! : "") : "both";
                (mask.Left, mask.Right) = side switch { "both" => (true, true), "left" => (true, false), "right" => (false, true), _ => throw new InvalidDataException($"{source}: {at}.side is '{side}' (both, left or right)") };
                mask.From = m.TryGetProperty("from", out var fr) ? Int(fr, $"{at}.from", source) : 0;
                mask.To = m.TryGetProperty("to", out var to) ? Int(to, $"{at}.to", source) : PPU_WMS.SnesOrigin;
                if (mask.From < 0 || mask.To > PPU_WMS.SnesOrigin || mask.From >= mask.To) throw new InvalidDataException($"{source}: {at}: from {mask.From} to {mask.To} (0-72 pixels out from the SNES edge, from < to)");
                if (m.TryGetProperty("when", out var when)) mask.When = ParseWhen(when, source, $"{at}.when");
                masks.Add(mask);
            }
        }
        if (masks.Count > 63) throw new InvalidDataException($"{source}: {masks.Count} masks (at most 63)");
        ws.Masks = masks.ToArray();
        return ws;
    }

    /// <summary>The SNES registers a scene condition names, as JSON keys, in <see cref="WmsRegister"/> order.</summary>
    public static readonly string[] RegisterKeys = { "bgmode", "bg1sc", "bg2sc", "bg3sc", "bg4sc", "bg12nba", "bg34nba", "tm", "ts", "obsel", "setini", "cgwsel", "cgadsub" };

    /// <summary>A mask's scene conditions: { "register": "$VV" or "$VV/$MM" } (hex; the register ANDed with the mask must equal the value; no mask = all 8 bits).</summary>
    private static WmsCondition[] ParseWhen(JsonElement w, string source, string at)
    {
        if (w.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: {at} is an object of register: \"$VV\" or \"$VV/$MM\"");
        var list = new List<WmsCondition>();
        foreach (var p in w.EnumerateObject())
        {
            int r = Array.IndexOf(RegisterKeys, p.Name);
            if (r < 0) throw new InvalidDataException($"{source}: {at} names '{p.Name}' (one of {string.Join(", ", RegisterKeys)})");
            string text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : "";
            var parts = text.Split('/');
            byte value = 0, mask = 0xFF;
            bool ok = parts.Length <= 2 && TryHexByte(parts[0], out value) && (parts.Length == 1 || TryHexByte(parts[1], out mask));
            if (!ok) throw new InvalidDataException($"{source}: {at}.{p.Name} is '{text}' (\"$VV\" or \"$VV/$MM\", hex)");
            if ((value & ~mask) != 0) throw new InvalidDataException($"{source}: {at}.{p.Name}: the value ${value:X2} has bits outside the mask ${mask:X2}, so it never matches");
            list.Add(new WmsCondition((WmsRegister)r, mask, value));
        }
        return list.ToArray();
    }

    private static bool TryHexByte(string s, out byte b)
    {
        s = s.Trim();
        if (s.StartsWith('$')) s = s[1..]; else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        b = 0;
        return s.Length is 1 or 2 && byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    private static void Keys(JsonElement o, string source, string what, string[] required, string[] optional)
    {
        foreach (var p in o.EnumerateObject())
            if (!required.Contains(p.Name) && !optional.Contains(p.Name)) throw new InvalidDataException($"{source}: {what} has an unknown key '{p.Name}'");
        foreach (var r in required)
            if (!o.TryGetProperty(r, out _)) throw new InvalidDataException($"{source}: {what} has no '{r}'");
    }

    private static string Str(JsonElement o, string key, string source)
    {
        var v = o.GetProperty(key);
        return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"{source}: '{key}' is a string");
    }

    private static int Int(JsonElement v, string what, string source) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : throw new InvalidDataException($"{source}: {what} is a whole number");

    private static IEnumerable<JsonElement> Arr(JsonElement o, string key, string source)
    {
        var v = o.GetProperty(key);
        if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{source}: '{key}' is a list");
        return v.EnumerateArray().ToArray();
    }
}

/// <summary>What looking a game up found: its profile, or none, or a profile file that could not be read (and why).</summary>
public sealed record WmsProfileLookup(WmsProfile? Profile, string? Error)
{
    public static readonly WmsProfileLookup None = new(null, null);
    /// <summary>One line for menus and the API.</summary>
    public string Describe() =>
        Profile != null ? $"profile: {Profile.Title} (verified {Profile.VerifiedDate})"
        : Error != null ? $"the profile for this game is broken: {Error}"
        : "no profile for this game";
}

/// <summary>The profiles folder (spec 6.2): <c>WideMess/profiles</c> beside the program, read once, every file checked.</summary>
public static class WmsProfiles
{
    private static string directory = Path.Combine(AppContext.BaseDirectory, "WideMess", "profiles");
    private static Dictionary<string, WmsProfileLookup>? bySha1;
    private static List<string> errors = new();
    private static readonly object gate = new();

    /// <summary>The folder (Workshop's tests point it at the repository's). Setting it forgets what was read.</summary>
    public static string Directory
    {
        get => directory;
        set { lock (gate) { directory = value; bySha1 = null; } }
    }

    /// <summary>The files that could not be read, and why (each also answers for the hashes it names, when it names any).</summary>
    public static IReadOnlyList<string> Errors { get { Ensure(); return errors; } }

    /// <summary>Every profile read.</summary>
    public static IReadOnlyList<WmsProfile> All { get { var d = Ensure(); return d.Values.Where(l => l.Profile != null).Select(l => l.Profile!).Distinct().ToList(); } }

    public static void Reload() { lock (gate) bySha1 = null; }

    /// <summary>The profile for a ROM image's SHA-1 (lower-case hex).</summary>
    public static WmsProfileLookup Find(string sha1) => Ensure().TryGetValue(sha1.ToLowerInvariant(), out var l) ? l : WmsProfileLookup.None;

    /// <summary>The SHA-1 a profile names a game by (spec 6.2): of the ROM image, without a 512-byte copier header.</summary>
    public static string ImageSha1(byte[] rom)
    {
        var image = rom.Length % 1024 == 512 ? rom.AsSpan(512) : rom.AsSpan();
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(image)).ToLowerInvariant();
    }

    private static Dictionary<string, WmsProfileLookup> Ensure()
    {
        lock (gate)
        {
            if (bySha1 != null) return bySha1;
            var d = new Dictionary<string, WmsProfileLookup>();
            var errs = new List<string>();
            if (System.IO.Directory.Exists(directory))
            {
                foreach (var file in System.IO.Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(file);
                    try
                    {
                        var p = WmsProfile.Parse(System.IO.File.ReadAllText(file), name);
                        foreach (var h in p.Sha1)
                        {
                            if (d.TryGetValue(h, out var other)) throw new InvalidDataException($"{name}: SHA-1 {h} is already {other.Profile?.Source ?? "claimed"}'s");
                        }
                        foreach (var h in p.Sha1) d[h] = new WmsProfileLookup(p, null);
                    }
                    catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        errs.Add(e.Message);
                        foreach (var h in HashesIn(file)) if (!d.ContainsKey(h)) d[h] = new WmsProfileLookup(null, e.Message);
                    }
                }
            }
            errors = errs;
            return bySha1 = d;
        }
    }

    /// <summary>The SHA-1s a file that failed its check names, when it is JSON enough to say: so the game it was for reports the broken profile rather than "no profile".</summary>
    private static IEnumerable<string> HashesIn(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("sha1", out var sha) && sha.ValueKind == JsonValueKind.Array)
                return sha.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()!.ToLowerInvariant()).Where(h => h.Length == 40).ToArray();
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return Array.Empty<string>();
    }
}
