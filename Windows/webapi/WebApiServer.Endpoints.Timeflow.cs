using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public sealed class TimeflowRequestBody { public string? Reason { get; set; } public string? From { get; set; } }

    public partial class WebApiServer
    {
        /// <summary>Timeflow's state: recording or not, the timeline, the space used, the agent's request and its answer (set by the main form).</summary>
        public Func<object>? GetTimeflowStatus { get; set; }
        /// <summary>An agent asks to record (reason, from): the app shows the user a prompt that does not stop the game. Returns the request's state.</summary>
        public Func<string, string, object>? RequestTimeflow { get; set; }
        /// <summary>The agent withdraws its request (if it is still waiting).</summary>
        public Func<object>? CancelTimeflowRequest { get; set; }
        /// <summary>The timelines of the running game (reading only).</summary>
        public Func<object>? GetTimeflowTimelines { get; set; }
        /// <summary>T3: the last Profile selection (made by the user in the Timeflow window), the profile windows open, the kept profiles (reading only).</summary>
        public Func<object>? GetTimeflowProfile { get; set; }
        /// <summary>T4: the last search the user made in the Timeflow window (busy, the condition, its report: hits, candidates, cost; reading only).</summary>
        public Func<object>? GetTimeflowSearch { get; set; }

        /// <summary>
        /// BrokenDev Timeflow (milestones T1 / T2, docs/projects/brokendev-timeflow-plan.md). THE CONSENT RULE: recording is off until the user switches
        /// it on (Emulator > Debug Windows > Timeflow); an agent can only ask. GET /api/timeflow/status, GET /api/timeflow/timelines, POST
        /// /api/timeflow/request {reason, from} (the app asks the user with a prompt: Allow / Decline; nothing records until the user allows it), POST
        /// /api/timeflow/request/cancel. There is deliberately NO route that switches recording on or off, and none that jumps the user's game.
        /// </summary>
        private void RegisterTimeflowEndpoints(WebApplication app)
        {
            app.MapGet("/api/timeflow/status", () => GetTimeflowStatus != null ? Results.Ok(GetTimeflowStatus()) : Results.BadRequest(new { success = false, error = "Timeflow is not wired" }));
            app.MapGet("/api/timeflow/timelines", () => GetTimeflowTimelines != null ? Results.Ok(GetTimeflowTimelines()) : Results.BadRequest(new { success = false, error = "Timeflow is not wired" }));
            app.MapGet("/api/timeflow/profile", () => GetTimeflowProfile != null ? Results.Ok(GetTimeflowProfile()) : Results.BadRequest(new { success = false, error = "Timeflow is not wired" }));
            app.MapGet("/api/timeflow/search", () => GetTimeflowSearch != null ? Results.Ok(GetTimeflowSearch()) : Results.BadRequest(new { success = false, error = "Timeflow is not wired" }));
            app.MapPost("/api/timeflow/request", async (HttpContext ctx) =>
            {
                if (RequestTimeflow == null) return Results.BadRequest(new { success = false, error = "Timeflow is not wired" });
                TimeflowRequestBody? r = null;
                if (ctx.Request.ContentLength is > 0) r = await ctx.Request.ReadFromJsonAsync<TimeflowRequestBody>();
                return Results.Ok(RequestTimeflow((r?.Reason ?? "").Trim(), string.IsNullOrWhiteSpace(r?.From) ? "an agent" : r!.From!.Trim()));
            });
            app.MapPost("/api/timeflow/request/cancel", () => CancelTimeflowRequest != null ? Results.Ok(CancelTimeflowRequest()) : Results.BadRequest(new { success = false, error = "Timeflow is not wired" }));
        }
    }
}
