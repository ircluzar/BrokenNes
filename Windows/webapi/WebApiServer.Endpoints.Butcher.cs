using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        // Butchering mode (docs/projects/butchering-mode-plan.md, engine NesEmulator/corruption/Butcher.cs). The webmodule takes the
        // entry savestate through /api/gh and drives the engine here: its reach on the running game, its controls, named presets.
        private static readonly JsonSerializerOptions ButcherJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private static readonly object ButcherPresetLock = new();

        // %APPDATA%\BrokenNes\Butchering, or the BROKENNES_SAVES_DIR test hook's folder (as the battery saves and save states), so a
        // test instance never writes into the user's own presets.
        private static string ButcherPresetPath(string console) => Path.Combine(
            Environment.GetEnvironmentVariable("BROKENNES_SAVES_DIR") is { Length: > 0 } d ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes"),
            "Butchering", $"presets-{console}.json");

        private static Dictionary<string, ButcherSettings> LoadButcherPresets(string console)
        {
            try
            {
                var p = ButcherPresetPath(console);
                if (!File.Exists(p)) return new(StringComparer.OrdinalIgnoreCase);
                var d = JsonSerializer.Deserialize<Dictionary<string, ButcherSettings>>(File.ReadAllText(p), ButcherJson);
                return d != null ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }

        private static void SaveButcherPresets(string console, Dictionary<string, ButcherSettings> presets)
        {
            var p = ButcherPresetPath(console);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(presets, ButcherJson));
            File.Move(tmp, p, true);
        }

        public sealed class ButcherPresetRequest { public string? Name { get; set; } public ButcherSettings? Settings { get; set; } }

        private void RegisterButcherEndpoints(WebApplication app)
        {
            // GET /api/butcher/reach - what this game and these cores let the mode reach
            app.MapGet("/api/butcher/reach", () =>
            {
                var nes = _getNes();
                if (nes == null || ConsoleGhHost() != null)
                    return Results.Ok(new { success = true, console = ConsoleGhHost() != null ? "other" : "none", supported = false,
                        reason = "Butchering mode runs on NES games for now." });
                return Results.Ok(new { success = true, console = "nes", supported = true, reach = nes.GetButcherReach(),
                    consoleAddressLines = Butcher.ConsoleAddressLines });
            });

            // GET /api/butcher/settings - the controls in force
            app.MapGet("/api/butcher/settings", () =>
            {
                var nes = _getNes();
                return Results.Ok(new { success = true, settings = nes?.Butcher?.Settings ?? new ButcherSettings() });
            });

            // POST /api/butcher/settings - take a full set of controls
            app.MapPost("/api/butcher/settings", async (HttpContext context) =>
            {
                var nes = _getNes();
                if (nes == null) return Results.BadRequest(new { success = false, error = "No NES game running" });
                try
                {
                    var s = await context.Request.ReadFromJsonAsync<ButcherSettings>(ButcherJson);
                    if (s == null) return Results.BadRequest(new { success = false, error = "Invalid settings" });
                    nes.ApplyButcher(s);
                    return Results.Ok(new { success = true, enabled = s.Enabled });
                }
                catch (Exception ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }
            });

            // POST /api/butcher/off - every effect off (the hooks detach at the next frame)
            app.MapPost("/api/butcher/off", () =>
            {
                var nes = _getNes();
                nes?.ApplyButcher(new ButcherSettings());
                return Results.Ok(new { success = true });
            });

            // GET /api/butcher/presets - the named presets (NES)
            app.MapGet("/api/butcher/presets", () =>
            {
                lock (ButcherPresetLock)
                {
                    var d = LoadButcherPresets("nes");
                    return Results.Ok(new { success = true, presets = d.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(k => new { name = k.Key, settings = k.Value }) });
                }
            });

            // POST /api/butcher/presets - save (or replace) a named preset
            app.MapPost("/api/butcher/presets", async (HttpContext context) =>
            {
                try
                {
                    var r = await context.Request.ReadFromJsonAsync<ButcherPresetRequest>(ButcherJson);
                    var name = r?.Name?.Trim();
                    if (string.IsNullOrEmpty(name) || name.Length > 60 || r?.Settings == null)
                        return Results.BadRequest(new { success = false, error = "A preset needs a name (1-60 characters) and settings" });
                    lock (ButcherPresetLock)
                    {
                        var d = LoadButcherPresets("nes");
                        d[name] = r.Settings;
                        SaveButcherPresets("nes", d);
                    }
                    return Results.Ok(new { success = true, name });
                }
                catch (Exception ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }
            });

            // DELETE /api/butcher/presets/{name}
            app.MapDelete("/api/butcher/presets/{name}", (string name) =>
            {
                lock (ButcherPresetLock)
                {
                    var d = LoadButcherPresets("nes");
                    bool removed = d.Remove(name);
                    if (removed) SaveButcherPresets("nes", d);
                    return Results.Ok(new { success = true, removed });
                }
            });
        }
    }
}
