using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public sealed class BugCaptureRequest { public string? Note { get; set; } }
    public sealed class BugEnableRequest { public bool Enabled { get; set; } }

    public partial class WebApiServer
    {
        /// <summary>A change to the game from outside it (a memory write, a corruption, a register write): the bug capture's history starts again after it (set by the main form).</summary>
        public Action<string>? ExternalChange { get; set; }
        /// <summary>The bug capture's state (set by the main form).</summary>
        public Func<object>? GetBugStatus { get; set; }
        /// <summary>Saves a capture of the last 30 s with this note, no dialog: (bundle folder or null, message, seconds, frames, start, no-movie reason).</summary>
        public Func<string, Task<(string? path, string message, double seconds, long frames, string start, string? noMovie)>>? CaptureBug { get; set; }
        public Action<bool>? SetBugCaptureEnabled { get; set; }

        /// <summary>
        /// The bug capture (BrokenDev D6): GET /api/bugs/status, POST /api/bugs/capture {note} (what F9 does, the note given instead of asked; the
        /// bundle lands where F9's would), POST /api/bugs/enabled {enabled} (Emulator > Keep the Last 30 s). Reading and capturing change nothing in
        /// the game: a capture copies the ring between frames.
        /// </summary>
        private void RegisterBugEndpoints(WebApplication app)
        {
            app.MapGet("/api/bugs/status", () => GetBugStatus != null ? Results.Ok(GetBugStatus()) : Results.BadRequest(new { success = false, error = "the bug capture is not wired" }));

            app.MapPost("/api/bugs/capture", async (HttpContext ctx) =>
            {
                if (CaptureBug == null) return Results.BadRequest(new { success = false, error = "the bug capture is not wired" });
                BugCaptureRequest? r = null;
                if (ctx.Request.ContentLength is > 0) r = await ctx.Request.ReadFromJsonAsync<BugCaptureRequest>();
                var (path, message, seconds, frames, start, noMovie) = await CaptureBug(r?.Note ?? "");
                return path != null
                    ? Results.Ok(new { success = true, path, message, seconds, frames, start, noMovie })
                    : Results.BadRequest(new { success = false, error = message });
            });

            app.MapPost("/api/bugs/enabled", async (HttpContext ctx) =>
            {
                if (SetBugCaptureEnabled == null) return Results.BadRequest(new { success = false, error = "the bug capture is not wired" });
                var r = await ctx.Request.ReadFromJsonAsync<BugEnableRequest>();
                if (r == null) return Results.BadRequest(new { success = false, error = "body: {\"enabled\": true|false}" });
                SetBugCaptureEnabled(r.Enabled);
                return Results.Ok(GetBugStatus?.Invoke());
            });
        }
    }
}
