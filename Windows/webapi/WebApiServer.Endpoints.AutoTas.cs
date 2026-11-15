using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public sealed class AutoTasCommandBody { public string? Command { get; set; } public int? Chapter { get; set; } public bool? Clean { get; set; } public float? Speed { get; set; } }

    public sealed class AutoTasCaptureBody { public string? Path { get; set; } }

    public partial class WebApiServer
    {
        /// <summary>The AutoTAS package of the running game, the panel's state (set by the main form).</summary>
        public Func<object>? GetAutoTasStatus { get; set; }
        /// <summary>Opens the chapter panel (when the game has a package); returns the status.</summary>
        public Func<object>? AutoTasOpenPanel { get; set; }
        /// <summary>A PNG of the open panel, or null.</summary>
        public Func<byte[]?>? AutoTasPanelPng { get; set; }
        /// <summary>The panel's buttons: (command, chapter, clean, speed) -> the message the panel shows.</summary>
        public Func<string, int?, bool?, float?, Task<string>>? AutoTasCommand { get; set; }

        /// <summary>Writes the running game's state to a file under the AutoTAS root (path relative to it); returns the full path.</summary>
        public Func<string, Task<string>>? AutoTasCaptureState { get; set; }

        /// <summary>
        /// AutoTAS (docs/projects/autotas-plan.md section 5). GET /api/autotas/status; POST /api/autotas/panel (open it); GET /api/autotas/panel.png;
        /// POST /api/autotas/capture-state {path} (writes the game's state under the AutoTAS root, for package building); POST /api/autotas/command {command: jump|play|pause|takeover|return|speed, chapter, clean (default true), speed}: the same as the panel's buttons.
        /// </summary>
        private void RegisterAutoTasEndpoints(WebApplication app)
        {
            IResult NotWired() => Results.BadRequest(new { success = false, error = "AutoTAS is not wired" });
            app.MapGet("/api/autotas/status", () => GetAutoTasStatus != null ? Results.Ok(GetAutoTasStatus()) : NotWired());
            app.MapPost("/api/autotas/panel", () => AutoTasOpenPanel != null ? Results.Ok(AutoTasOpenPanel()) : NotWired());
            app.MapGet("/api/autotas/panel.png", () =>
            {
                if (AutoTasPanelPng == null) return NotWired();
                var png = AutoTasPanelPng();
                return png == null ? Results.NotFound(new { success = false, error = "the AutoTAS panel is not open" }) : Results.File(png, "image/png");
            });
            app.MapPost("/api/autotas/capture-state", async (HttpContext ctx) =>
            {
                if (AutoTasCaptureState == null) return NotWired();
                AutoTasCaptureBody? b = null;
                if (ctx.Request.ContentLength is > 0) b = await ctx.Request.ReadFromJsonAsync<AutoTasCaptureBody>();
                try { return Results.Ok(new { success = true, path = await AutoTasCaptureState(b?.Path ?? "") }); }
                catch (Exception e) when (e is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException) { return Results.BadRequest(new { success = false, error = e.Message }); }
            });
            app.MapPost("/api/autotas/command", async (HttpContext ctx) =>
            {
                if (AutoTasCommand == null) return NotWired();
                AutoTasCommandBody? b = null;
                if (ctx.Request.ContentLength is > 0) b = await ctx.Request.ReadFromJsonAsync<AutoTasCommandBody>();
                string cmd = (b?.Command ?? "").Trim().ToLowerInvariant();
                string msg = await AutoTasCommand(cmd, b?.Chapter, b?.Clean, b?.Speed);
                return Results.Ok(new { success = true, message = msg, status = GetAutoTasStatus?.Invoke() });
            });
        }
    }
}
