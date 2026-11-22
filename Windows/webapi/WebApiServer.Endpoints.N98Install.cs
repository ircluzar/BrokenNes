using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>nano98 install flow (docs/projects/nano98-format.md "Agent recipe"): POST /api/n98/install/ACTION with a JSON body, the same actions and JSON
    /// results as NesEmulator.X86.N98Install.Do / DoStatic, plus the app's own: start {disc | drive, machine, sound, layers[], settle} (the install seat runs
    /// in the app's window), open {path} (a finished package), close. GET /api/n98/install = status. The screen: GET /api/console/framebuffer.</summary>
    public partial class WebApiServer
    {
        /// <summary>(action, body) -> result JSON; set by MainForm (MainForm.N98Install.cs).</summary>
        public Func<string, JsonObject, Task<JsonObject>>? N98InstallAction { get; set; }

        private void RegisterN98InstallEndpoints(WebApplication app)
        {
            app.MapGet("/api/n98/install", async () => await Run("status", new JsonObject()));
            app.MapPost("/api/n98/install/{action}", async (string action, HttpContext ctx) =>
            {
                JsonObject body;
                try { body = ctx.Request.ContentLength is > 0 ? (await JsonNode.ParseAsync(ctx.Request.Body)) as JsonObject ?? new JsonObject() : new JsonObject(); }
                catch (Exception e) { return Results.BadRequest(new { ok = false, error = "bad JSON: " + e.Message }); }
                return await Run(action, body);
            });

            async Task<IResult> Run(string action, JsonObject body)
            {
                if (N98InstallAction == null) return Results.BadRequest(new { ok = false, error = "not available" });
                try { return Results.Content((await N98InstallAction(action, body)).ToJsonString(), "application/json"); }
                catch (Exception e) { return Results.Content(new JsonObject { ["ok"] = false, ["error"] = e.Message }.ToJsonString(), "application/json", null, 400); }
            }
        }
    }
}
