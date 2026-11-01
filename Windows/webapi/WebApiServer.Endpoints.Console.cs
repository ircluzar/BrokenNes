using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>BrokenNes 2: what the app is running beyond the NES (a SNES / Game Boy console session).</summary>
    public sealed class ConsoleStatus
    {
        public string Console { get; set; } = "nes";
        public string ConsoleName { get; set; } = "NES";
        public string? Game { get; set; }
        public string? Description { get; set; }
        public string? Cpu { get; set; }
        public string? Ppu { get; set; }
        public string? Apu { get; set; }
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public double Fps { get; set; }
        /// <summary>When the picture crosses a bridge (a Sega game on a NES / Game Boy / SNES picture chip, a NES game on the Master System or Genesis chip): what the bridge is.</summary>
        public string? PictureBridge { get; set; }
        /// <summary>What the picture bridge could not carry, by name, totals since the game started (each loss is counted, none hidden).</summary>
        public System.Collections.Generic.Dictionary<string, long>? PictureBridgeCounters { get; set; }
        /// <summary>The same counters in one line.</summary>
        public string? PictureBridgeSummary { get; set; }
        /// <summary>A Sega game's video standard ("Ntsc" / "Pal") and board ("Model1", "Sms2", "GameGear", ...) as the running board has them; null for any other console.</summary>
        public string? Region { get; set; }
        public string? Model { get; set; }
        /// <summary>Whether the game has a battery save, whether the session has save states (and, when it does not, why), and a remark about the last event (a core swap that restarted the game, and why).</summary>
        public bool? HasBattery { get; set; }
        public bool? SupportsState { get; set; }
        public string? StateUnsupportedReason { get; set; }
        public string? Note { get; set; }
    }

    public sealed class SegaSettingsRequest { public string? Region { get; set; } public string? GenesisModel { get; set; } public string? MasterSystemModel { get; set; } }

    public partial class WebApiServer
    {
        /// <summary>The running console (set by the main form).</summary>
        public Func<ConsoleStatus>? GetConsoleStatus { get; set; }
        /// <summary>The picture of a SNES / Game Boy session as RGBA bytes with its size, or null when the NES runs.</summary>
        public Func<(int width, int height, byte[] rgba)?>? GetConsoleFrame { get; set; }
        /// <summary>Switch the base console ("nes", "snes", "gb", "gbc").</summary>
        public Action<string>? SelectConsole { get; set; }
        /// <summary>Pick a core for a console slot ("cpu" / "ppu" / "apu", an id from GET /api/console/cores).</summary>
        public Func<string, string, bool>? SelectConsoleCore { get; set; }
        /// <summary>The core menus of the selected console: family + id + label per slot.</summary>
        public Func<object>? GetConsoleCoreMenus { get; set; }
        /// <summary>The Game Boy look: (look key, invert background, invert sprites).</summary>
        public Func<(string look, bool invertBackground, bool invertSprites)>? GetGbLook { get; set; }
        /// <summary>Change the Game Boy look; null leaves that part alone.</summary>
        public Action<string?, bool?, bool?>? SetGbLook { get; set; }

        /// <summary>Why the last quick save / load through the API failed, or null: the opt-in flag is off, the session has no save states (and why), or the state itself was refused.</summary>
        public Func<string?>? QuickStateReason { get; set; }

        /// <summary>The answer of POST /api/emulator/quick-save-state and quick-load-state: success, the reason when it failed, and <c>gated</c> only when the reason is the opt-in flag (it used to be true for every failure).</summary>
        private object StateResult(bool success)
        {
            string? reason = success ? null : QuickStateReason?.Invoke();
            return new { success, gated = !success && reason != null && reason.Contains("enableWebmoduleSavestateDebugShortcuts", StringComparison.Ordinal), reason };
        }

        /// <summary>The Sega machine settings (video standard, Genesis board, Master System board) and the choices for each, plus what the running game is.</summary>
        public Func<object>? GetSegaSettings { get; set; }
        /// <summary>Change them (null leaves one alone); false when a key is not one of the choices. A running game of a console the change concerns restarts on it.</summary>
        public Func<string?, string?, string?, bool>? SetSegaSettings { get; set; }

        public sealed class GbLookRequest { public string? Look { get; set; } public bool? InvertBackground { get; set; } public bool? InvertSprites { get; set; } }

        private void RegisterConsoleEndpoints(WebApplication app)
        {
            // GET /api/console/gb-look - the Game Boy look (green / grey / pocket / color) and the cross-console inversions
            app.MapGet("/api/console/gb-look", () =>
            {
                if (GetGbLook == null) return Results.BadRequest(new { success = false, error = "not available" });
                var (look, bg, obj) = GetGbLook();
                return Results.Ok(new { success = true, look, invertBackground = bg, invertSprites = obj });
            });
            // POST /api/console/gb-look {"look": "pocket", "invertBackground": false, "invertSprites": true} - any field optional
            app.MapPost("/api/console/gb-look", async (HttpContext context) =>
            {
                if (GetGbLook == null || SetGbLook == null) return Results.BadRequest(new { success = false, error = "not available" });
                var body = await context.Request.ReadFromJsonAsync<GbLookRequest>();
                if (body == null) return Results.BadRequest(new { success = false, error = "look, invertBackground and/or invertSprites is required" });
                SetGbLook(body.Look, body.InvertBackground, body.InvertSprites);
                var (look, bg, obj) = GetGbLook();
                return Results.Ok(new { success = true, look, invertBackground = bg, invertSprites = obj });
            });

            // GET /api/console/sega - the Sega machine settings: region ("auto" / "ntsc" / "pal"), genesisModel ("auto" / "1" / "2" / "3"), masterSystemModel ("auto" / "markiii" / "sms1" / "sms2"), the choices, and the machine a running game is
            app.MapGet("/api/console/sega", () =>
                GetSegaSettings == null ? Results.BadRequest(new { success = false, error = "not available" })
                                        : Results.Ok(new { success = true, settings = GetSegaSettings() }));
            // POST /api/console/sega {"region": "pal", "genesisModel": "2", "masterSystemModel": "sms1"} - any field optional; a running game of that console restarts on the change
            app.MapPost("/api/console/sega", async (HttpContext context) =>
            {
                if (GetSegaSettings == null || SetSegaSettings == null) return Results.BadRequest(new { success = false, error = "not available" });
                var body = await context.Request.ReadFromJsonAsync<SegaSettingsRequest>();
                if (body == null || (body.Region == null && body.GenesisModel == null && body.MasterSystemModel == null))
                    return Results.BadRequest(new { success = false, error = "region, genesisModel and/or masterSystemModel is required" });
                if (!SetSegaSettings(body.Region, body.GenesisModel, body.MasterSystemModel))
                    return Results.BadRequest(new { success = false, error = "a value is not one of the choices (GET /api/console/sega lists them)", settings = GetSegaSettings() });
                return Results.Ok(new { success = true, settings = GetSegaSettings(), status = GetConsoleStatus?.Invoke() });
            });

            // GET /api/console - the console, the game and the cores
            app.MapGet("/api/console", () =>
                GetConsoleStatus == null ? Results.BadRequest(new { success = false, error = "not available" })
                                         : Results.Ok(new { success = true, status = GetConsoleStatus() }));

            // GET /api/console/cores - the CPU / PPU / APU menus of the selected console, own family first
            app.MapGet("/api/console/cores", () =>
                GetConsoleCoreMenus == null ? Results.BadRequest(new { success = false, error = "not available" })
                                            : Results.Ok(new { success = true, menus = GetConsoleCoreMenus() }));

            // POST /api/console/select/{console} - switch console (nes, snes, gb, gbc)
            app.MapPost("/api/console/select/{console}", (string console) =>
            {
                if (SelectConsole == null) return Results.BadRequest(new { success = false, error = "not available" });
                SelectConsole(console);
                return Results.Ok(new { success = true, status = GetConsoleStatus?.Invoke() });
            });

            // POST /api/console/core/{slot}/{id} - pick a core for the selected console (the game keeps running when the session can swap it in place; the status says if it had to restart, and why)
            app.MapPost("/api/console/core/{slot}/{id}", (string slot, string id) =>
            {
                if (SelectConsoleCore == null) return Results.BadRequest(new { success = false, error = "not available" });
                bool ok = SelectConsoleCore(slot, Uri.UnescapeDataString(id));
                return ok ? Results.Ok(new { success = true, status = GetConsoleStatus?.Invoke() })
                          : Results.BadRequest(new { success = false, error = $"no {slot} core '{id}' on this console" });
            });

            // GET /api/console/framebuffer - the picture at its real size, whichever console runs
            app.MapGet("/api/console/framebuffer", () =>
            {
                var frame = GetConsoleFrame?.Invoke();
                if (frame is { } f) return Results.Ok(new { success = true, width = f.width, height = f.height, format = "RGBA", data = f.rgba });
                var nes = _getNes();
                if (nes == null) return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                return Results.Ok(new { success = true, width = 256, height = 240, format = "RGBA", data = nes.GetFramebuffer() });
            });
        }
    }
}
