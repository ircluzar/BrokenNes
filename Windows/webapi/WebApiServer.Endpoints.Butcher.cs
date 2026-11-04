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

        // The running system Butchering mode drives: the console session when one is up (Game Boy today), else the NES.
        // A session that is not a host (SNES, Sega) answers "not yet" - never the NES behind it.
        private IButcherHost? ButcherHost(out string console)
        {
            var cs = GetConsoleSession?.Invoke();
            if (cs != null) { console = cs is IButcherHost h ? h.GetButcherReach().Console : "other"; return cs as IButcherHost; }
            var nes = _getNes();
            console = nes != null ? "nes" : "none";
            return nes;
        }

        private void RegisterButcherEndpoints(WebApplication app)
        {
            // GET /api/butcher/reach - what this game and these cores let the mode reach
            app.MapGet("/api/butcher/reach", () =>
            {
                var host = ButcherHost(out var console);
                if (host == null)
                    return Results.Ok(new { success = true, console, supported = false,
                        reason = console == "none" ? "Load a game first." : "Butchering mode runs on NES and Game Boy games for now." });
                var reach = host.GetButcherReach();
                return Results.Ok(new { success = true, console, supported = true, reach, consoleAddressLines = reach.ConsoleAddressLines });
            });

            // GET /api/butcher/settings - the controls in force
            app.MapGet("/api/butcher/settings", () =>
            {
                var host = ButcherHost(out _);
                return Results.Ok(new { success = true, settings = host?.Butcher?.Settings ?? new ButcherSettings() });
            });

            // POST /api/butcher/settings - take a full set of controls
            app.MapPost("/api/butcher/settings", async (HttpContext context) =>
            {
                var host = ButcherHost(out _);
                if (host == null) return Results.BadRequest(new { success = false, error = "No game Butchering mode can run on" });
                try
                {
                    var s = await context.Request.ReadFromJsonAsync<ButcherSettings>(ButcherJson);
                    if (s == null) return Results.BadRequest(new { success = false, error = "Invalid settings" });
                    host.ApplyButcher(s);
                    return Results.Ok(new { success = true, enabled = s.Enabled });
                }
                catch (Exception ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }
            });

            // GET /api/butcher/stats - the RAM profiler's live view (watches, confirmed variables, nudges running)
            app.MapGet("/api/butcher/stats", () =>
            {
                var b = ButcherHost(out _)?.Butcher;
                if (b == null) return Results.Ok(new { success = true, watches = 0, confirmed = 0, nudging = 0 });
                var (w, c, n) = b.RamStats();
                return Results.Ok(new { success = true, watches = w, confirmed = c, nudging = n });
            });

            // POST /api/butcher/off - every effect off (the hooks detach at the next frame)
            app.MapPost("/api/butcher/off", () =>
            {
                ButcherHost(out _)?.ApplyButcher(new ButcherSettings());
                return Results.Ok(new { success = true });
            });

            // GET /api/butcher/presets - the named presets (NES)
            app.MapGet("/api/butcher/presets", () =>
            {
                lock (ButcherPresetLock)
                {
                    ButcherHost(out var console);
                    var d = LoadButcherPresets(console);
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
                    ButcherHost(out var console);
                    lock (ButcherPresetLock)
                    {
                        var d = LoadButcherPresets(console);
                        d[name] = r.Settings;
                        SaveButcherPresets(console, d);
                    }
                    return Results.Ok(new { success = true, name });
                }
                catch (Exception ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }
            });

            // DELETE /api/butcher/presets/{name}
            app.MapDelete("/api/butcher/presets/{name}", (string name) =>
            {
                ButcherHost(out var console);
                lock (ButcherPresetLock)
                {
                    var d = LoadButcherPresets(console);
                    bool removed = d.Remove(name);
                    if (removed) SaveButcherPresets(console, d);
                    return Results.Ok(new { success = true, removed });
                }
            });
        }
    }
}
