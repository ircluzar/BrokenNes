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
    }

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

        private void RegisterConsoleEndpoints(WebApplication app)
        {
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

            // POST /api/console/core/{slot}/{id} - pick a core for the selected console (SNES / Game Boy picks restart the game)
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
