using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NesEmulator.X86;

/// <summary>
/// The nano98 driver catalog (tools/nano98/drivers.json; user design 2026-11-21: "a repository of supported drivers, archived like oldversions"):
/// id, card PCI ids, version, file, size, sha256, original source, licence note. A driver layer names its catalog id. Resolution: the local archive
/// first (BROKENNES_N98_DRIVERS, T:\BrokenDev\Archive\drivers, %LOCALAPPDATA%\BrokenNes\nano98\drivers), then the catalog's download_base /
/// BROKENNES_N98_DRIVER_BASE (not set: no public hosting yet). Every file is checked against its sha256.
/// Driver choice per package: the package pins exact layer ids (system.layers); BROKENNES_N98_LAYER_MAP ("old=new;old2=new2") overrides a pinned
/// layer with another (the packaging window's setting; the user has not decided the default policy yet, so pinning stays the default).
/// </summary>
public static class N98Drivers
{
    public static string CatalogPath()
    {
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "x86", "nano98", "drivers.json") })
            if (File.Exists(p)) return p;
        return Path.Combine(N98Seat.ToolsDir(), "drivers.json");
    }

    public static JsonArray Catalog() => (JsonNode.Parse(File.ReadAllText(CatalogPath()))!["drivers"] as JsonArray) ?? new JsonArray();

    public static IEnumerable<string> ArchiveDirs()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_DRIVERS") is { Length: > 0 } d) yield return d;
        yield return @"T:\BrokenDev\Archive\drivers";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "nano98", "drivers");
    }

    /// <summary>The local file of a catalog driver (sha256 checked), or why it is not available.</summary>
    public static string Resolve(string id)
    {
        var e = Catalog().OfType<JsonObject>().FirstOrDefault(x => (string?)x["id"] == id) ?? throw new FileNotFoundException($"driver '{id}' is not in the catalog ({CatalogPath()})");
        if (e["inbox"]?.GetValue<bool>() == true) throw new InvalidOperationException($"driver '{id}' is part of Windows 98 SE: it comes from the Windows 98 SE files disc");
        string file = (string)e["file"]!, sha = (string)e["sha256"]!;
        foreach (var dir in ArchiveDirs())
        {
            string p = Path.Combine(dir, file);
            if (File.Exists(p))
            {
                using var f = File.OpenRead(p);
                if (Convert.ToHexString(SHA256.HashData(f)).Equals(sha, StringComparison.OrdinalIgnoreCase)) return p;
            }
        }
        string? baseUrl = Environment.GetEnvironmentVariable("BROKENNES_N98_DRIVER_BASE") ?? (string?)JsonNode.Parse(File.ReadAllText(CatalogPath()))!["download_base"];
        if (string.IsNullOrEmpty(baseUrl)) throw new FileNotFoundException($"driver '{id}' ({file}) is not in the local driver archive ({string.Join("; ", ArchiveDirs())}) and no download place is set");
        throw new NotSupportedException($"driver '{id}': downloading from {baseUrl} is not built yet (no public hosting decided)");
    }

    /// <summary>The user's saved override map (the packaging window's "Driver overrides" setting): LOCALAPPDATA/BrokenNes/nano98/layer_map.txt.</summary>
    public static string MapFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "nano98", "layer_map.txt");
    public static string SavedMap() { try { return File.Exists(MapFile) ? File.ReadAllText(MapFile).Trim() : ""; } catch (IOException) { return ""; } }
    public static void SaveMap(string map) { Directory.CreateDirectory(Path.GetDirectoryName(MapFile)!); if (map.Trim().Length == 0) File.Delete(MapFile); else File.WriteAllText(MapFile, map.Trim()); }

    /// <summary>A pinned layer id after the user's override map (BROKENNES_N98_LAYER_MAP "old=new;...", else the saved setting).</summary>
    public static string MapLayer(string id)
    {
        string map = Environment.GetEnvironmentVariable("BROKENNES_N98_LAYER_MAP") is { Length: > 0 } e ? e : SavedMap();
        foreach (var kv in map.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (kv.Split('=', 2) is { Length: 2 } p && p[0].Trim() == id) return p[1].Trim();
        return id;
    }
}
