using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>POST /api/widemess/enhance: the enhancements to switch (each optional; a missing one stays as it is). <c>UseProfile</c> true switches on what the game's profile recommends, false
    /// switches every enhancement off; it is applied first, then the others.</summary>
    public sealed class WideMessEnhanceRequest
    {
        public bool? Widescreen { get; set; }
        public bool? NoFlicker { get; set; }
        public bool? BetterSound { get; set; }
        public bool? FastCpu { get; set; }
        public bool? UseProfile { get; set; }
    }

    /// <summary>WideMess milestone 6 (docs/projects/widemess-spec.md section 6.3): the console's enhancements of the SNES game running on the WideMess console, for tests and tools. They change only
    /// the running game, never the settings file.</summary>
    public partial class WebApiServer
    {
        /// <summary>The running game's profile (or why there is none), what is on, what is offered and why not.</summary>
        public Func<object>? GetWideMessEnhance { get; set; }
        /// <summary>Switch enhancements: (null when done, or the reason it was refused, nothing changed; the state after).</summary>
        public Func<WideMessEnhanceRequest, (string? error, object state)>? SetWideMessEnhance { get; set; }
        /// <summary>A scratch profile for the running game (its JSON, checked like a file; held in memory only): (null, or why it was refused).</summary>
        public Func<string, string?>? SetWideMessScratchProfile { get; set; }

        private void RegisterWideMessEndpoints(WebApplication app)
        {
            // GET /api/widemess/enhance - the running SNES game's WideMess enhancements: profile, on, offered
            app.MapGet("/api/widemess/enhance", () =>
                GetWideMessEnhance == null ? Results.BadRequest(new { success = false, error = "not available" }) : Results.Ok(new { success = true, state = GetWideMessEnhance() }));

            // POST /api/widemess/enhance { widescreen, noFlicker, betterSound, fastCpu, useProfile } - switch them (a refusal says why and changes nothing)
            app.MapPost("/api/widemess/enhance", (WideMessEnhanceRequest req) =>
            {
                if (SetWideMessEnhance == null) return Results.BadRequest(new { success = false, error = "not available" });
                var (error, state) = SetWideMessEnhance(req);
                return error == null ? Results.Ok(new { success = true, state }) : Results.Ok(new { success = false, error, state });
            });

            // POST /api/widemess/profile <a profile's JSON> - a scratch profile for the running game (memory only; the profiles folder is untouched)
            app.MapPost("/api/widemess/profile", async (HttpRequest request) =>
            {
                if (SetWideMessScratchProfile == null) return Results.BadRequest(new { success = false, error = "not available" });
                using var reader = new StreamReader(request.Body);
                string json = await reader.ReadToEndAsync();
                string? error = SetWideMessScratchProfile(json);
                return error == null ? Results.Ok(new { success = true, state = GetWideMessEnhance?.Invoke() }) : Results.Ok(new { success = false, error });
            });
        }
    }
}
