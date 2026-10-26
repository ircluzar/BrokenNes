using System;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public sealed class DebugPortEnableRequest { public bool Enabled { get; set; } }

    public partial class WebApiServer
    {
        /// <summary>The debug port's state and events after a seq, at most N (set by the main form).</summary>
        public Func<long, int, object>? GetDebugPort { get; set; }
        public Action<bool>? SetDebugPortEnabled { get; set; }

        /// <summary>
        /// The BrokenDev debug port (milestone D7): GET /api/debug/port?since=&amp;limit= (on or off, where it listens, the counts, the events after
        /// a seq, the named timers), POST /api/debug/port/enabled {enabled} (Emulator > Debug Port). Reading changes nothing in the game; the port
        /// itself only listens.
        /// </summary>
        private void RegisterDebugPortEndpoints(WebApplication app)
        {
            app.MapGet("/api/debug/port", (HttpContext ctx) =>
            {
                if (GetDebugPort == null) return Results.BadRequest(new { success = false, error = "the debug port is not wired" });
                long since = long.TryParse(ctx.Request.Query["since"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
                int limit = int.TryParse(ctx.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 200;
                return Results.Ok(GetDebugPort(since, limit));
            });

            app.MapPost("/api/debug/port/enabled", async (HttpContext ctx) =>
            {
                if (SetDebugPortEnabled == null) return Results.BadRequest(new { success = false, error = "the debug port is not wired" });
                var r = await ctx.Request.ReadFromJsonAsync<DebugPortEnableRequest>();
                if (r == null) return Results.BadRequest(new { success = false, error = "body: {\"enabled\": true|false}" });
                SetDebugPortEnabled(r.Enabled);
                return Results.Ok(GetDebugPort?.Invoke(0, 1));
            });
        }
    }
}
