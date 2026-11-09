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
        /// <summary>The Butchering popout window (MainForm.ButcherPopout.cs): status / open / dock / exit / prompt.</summary>
        public Func<string, System.Threading.Tasks.Task<object>>? ButcherPopoutControl { get; set; }

        // %APPDATA%\BrokenNes\Butchering, or the BROKENNES_SAVES_DIR test hook's folder (as the battery saves and save states), so a
        // test instance never writes into the user's own presets.
        private static string ButcherPresetPath(string console) => Path.Combine(
            Environment.GetEnvironmentVariable("BROKENNES_SAVES_DIR") is { Length: > 0 } d ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes"),
            "Butchering", $"presets-{console}.json");

        // The classification tags set from the board's right-click menu: per effect path, Unsafe (overrides the catalog's own
        // unsafe group either way) and Disgraced (kept out of rolls unless asked). One file for every console, beside the presets,
        // so the tagging work can be read back and folded into the catalog.
        private sealed class ButcherTag { public bool? Unsafe { get; set; } public bool Disgraced { get; set; } }
        private sealed class ButcherTagRequest { public string? Path { get; set; } public bool? Unsafe { get; set; } public bool? Disgraced { get; set; } }
        private static string ButcherTagPath() => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ButcherPresetPath("nes"))!, "tags.json");
        private static Dictionary<string, ButcherTag> LoadButcherTags()
        {
            try
            {
                var p = ButcherTagPath();
                if (!File.Exists(p)) return new(StringComparer.OrdinalIgnoreCase);
                var d = JsonSerializer.Deserialize<Dictionary<string, ButcherTag>>(File.ReadAllText(p), ButcherJson);
                return d != null ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }

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

        // Built-in presets ship read-only with the webmodule (Webmodules/ButcheringMode/builtins/<console>.json): listed beside the user's
        // own, marked builtIn, never deleted; saving under a built-in's name makes the user's own copy beside it.
        private static Dictionary<string, ButcherSettings> LoadBuiltInPresets(string console)
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Webmodules", "ButcheringMode", "builtins", $"{console}.json");
                if (!File.Exists(p)) return new();
                return JsonSerializer.Deserialize<Dictionary<string, ButcherSettings>>(File.ReadAllText(p), ButcherJson) ?? new();
            }
            catch { return new(); }
        }

        public sealed class ButcherPresetRequest { public string? Name { get; set; } public ButcherSettings? Settings { get; set; } }

        // The running system Butchering mode drives: the console session when one is up (Game Boy today), else the NES.
        // A session that is not a host (SNES, Sega) answers "not yet" - never the NES behind it.
        private IButcherHost? ButcherHost(out string console)
        {
            var cs = GetConsoleSession?.Invoke();
            if (cs is NesEmulator.Mix.SegaPictureSession sps) cs = sps.Inner;   // the Sega session under the picture bridge
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
                        reason = console == "none" ? "Load a game first." : "Butchering mode runs on NES, Game Boy, Master System and Game Gear games for now." });
                var reach = host.GetButcherReach();
                if (GetConsoleSession?.Invoke() is NesEmulator.Mix.SegaPictureSession { Bridged: true })
                    reach = reach with { ConsolePinsPartial = true };   // another chip draws the picture from VRAM itself: the pins reach the Sega VDP's own fetches only
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
                var host = ButcherHost(out _);
                var b = host?.Butcher;
                bool crashed = host is NES nes && nes.IsCrashed();   // a crashed NES runs no frames: the watchdog hears it from here
                if (b == null) return Results.Ok(new { success = true, watches = 0, confirmed = 0, nudging = 0, stillFrames = 0, crashed });
                var (w, c, n) = b.RamStats();
                return Results.Ok(new { success = true, watches = w, confirmed = c, nudging = n, stillFrames = b.StillFrames, crashed });
            });

            // POST /api/butcher/off - every effect off (the hooks detach at the next frame)
            // GET /api/butcher/popout - is the popout window open; POST /api/butcher/popout/{open|dock|exit|prompt} - drive it as the
            // panel's Pop out button and the close prompt's answers do (desktop only; tests and screenshots).
            app.MapGet("/api/butcher/popout", async () =>
                ButcherPopoutControl == null ? Results.Ok(new { success = false, error = "not available" }) : Results.Ok(await ButcherPopoutControl("status")));
            app.MapPost("/api/butcher/popout/{action}", async (string action) =>
                ButcherPopoutControl == null ? Results.Ok(new { success = false, error = "not available" })
                : action is "open" or "dock" or "exit" or "prompt" ? Results.Ok(await ButcherPopoutControl(action))
                : Results.Ok(new { success = false, error = "action: open, dock, exit or prompt" }));

            app.MapPost("/api/butcher/off", () =>
            {
                ButcherHost(out _)?.ApplyButcher(new ButcherSettings());
                return Results.Ok(new { success = true });
            });

            // GET /api/butcher/catalog - every effect's purity score (0 forced .. 100 pure, docs/projects/butchering-purity-plan.md), with the score on
            // the running console where the part sits elsewhere there; and the pin fault codes. Informational: nothing uses the scores yet.
            app.MapGet("/api/butcher/catalog", () =>
            {
                ButcherHost(out var console);
                Dictionary<string, ButcherTag> tags; lock (ButcherPresetLock) tags = LoadButcherTags();
                return Results.Ok(new
                {
                    success = true,
                    console,
                    effects = NesEmulator.ButcherCatalog.Effects.Select(e =>
                    {
                        tags.TryGetValue(e.Path, out var tg);
                        bool builtInUnsafe = e.Group == "unsafe";
                        return new { path = e.Path, group = e.Group, name = e.Name, purity = e.Purity, purityHere = e.PurityOn(console), tier = NesEmulator.ButcherCatalog.TierOf(e.PurityOn(console)).ToString(), why = e.Why, byConsole = e.ByConsole,
                            unsafeBuiltIn = builtInUnsafe, @unsafe = tg?.Unsafe ?? builtInUnsafe, disgraced = tg?.Disgraced ?? false };
                    }),
                    faults = NesEmulator.ButcherCatalog.Faults.Select(f => new { code = f.Code, name = f.Name, purity = f.Purity, why = f.Why }),
                    tiers = new[] { new { tier = "Pure", from = 90 }, new { tier = "Plausible", from = 70 }, new { tier = "MechanismReal", from = 40 }, new { tier = "Steered", from = 15 }, new { tier = "Forced", from = 0 } },
                    unscored = NesEmulator.ButcherCatalog.Unscored(),
                });
            });

            // GET /api/butcher/presets - the named presets (NES)
            app.MapGet("/api/butcher/presets", () =>
            {
                lock (ButcherPresetLock)
                {
                    ButcherHost(out var console);
                    var d = LoadButcherPresets(console);
                    var builtIn = LoadBuiltInPresets(console).Select(k => new { name = k.Key, settings = k.Value, builtIn = true });
                    return Results.Ok(new { success = true, presets = builtIn.Concat(d.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(k => new { name = k.Key, settings = k.Value, builtIn = false })) });
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

            // POST /api/butcher/tags {path, unsafe?, disgraced?} - set an effect's classification tags (the board's right-click menu)
            app.MapPost("/api/butcher/tags", async (HttpContext context) =>
            {
                try
                {
                    var r = await context.Request.ReadFromJsonAsync<ButcherTagRequest>(ButcherJson);
                    var entry = r?.Path == null ? null : NesEmulator.ButcherCatalog.Find(r.Path);
                    if (entry == null) return Results.BadRequest(new { success = false, error = "Unknown effect path" });
                    lock (ButcherPresetLock)
                    {
                        var d = LoadButcherTags();
                        var tg = d.TryGetValue(entry.Path, out var x) ? x : new ButcherTag();
                        bool builtIn = entry.Group == "unsafe";
                        if (r!.Unsafe is bool u) tg.Unsafe = u == builtIn ? null : u;
                        if (r.Disgraced is bool g) tg.Disgraced = g;
                        if (tg.Unsafe == null && !tg.Disgraced) d.Remove(entry.Path); else d[entry.Path] = tg;
                        var p = ButcherTagPath();
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(d, ButcherJson));
                        File.Move(p + ".tmp", p, true);
                        return Results.Ok(new { success = true, path = entry.Path, @unsafe = tg.Unsafe ?? builtIn, disgraced = tg.Disgraced });
                    }
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
