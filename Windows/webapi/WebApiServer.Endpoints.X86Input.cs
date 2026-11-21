using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator.X86;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>The x86 PC's keyboard and PS/2 mouse driven by a script (POST /api/x86/input), frame-paced like the Workshop's x86boot --type
    /// (<see cref="PcInputScript"/>). The emulation thread runs the due steps before each frame (<see cref="BeforeFrame"/>).</summary>
    public static class ApiPcInput
    {
        private static readonly object gate = new();
        private static readonly Queue<PcInputScript> scripts = new();
        private static volatile bool any;

        public static void Enqueue(PcInputScript s) { lock (gate) { scripts.Enqueue(s); any = true; } }
        public static void Clear() { lock (gate) { scripts.Clear(); any = false; } }
        public static int Pending { get { lock (gate) { int n = 0; foreach (var s in scripts) n += s.Pending; return n; } } }

        /// <summary>The emulation thread, under the emulation lock, before an x86 frame: one volatile read when nothing is queued.</summary>
        public static void BeforeFrame(PcMachine m)
        {
            if (!any) return;
            lock (gate)
            {
                while (scripts.Count > 0 && scripts.Peek().Pending == 0) scripts.Dequeue();
                if (scripts.Count == 0) { any = false; return; }
                scripts.Peek().BeforeFrame(m);
            }
        }
    }

    public sealed class X86InputRequest
    {
        public string? Script { get; set; }
        public bool Clear { get; set; }
    }

    public partial class WebApiServer
    {
        private void RegisterX86InputEndpoints(WebApplication app)
        {
            // POST /api/x86/input {script} - queue keyboard / mouse input for the running x86 PC (behind enableDebuggerControl); {clear:true} drops what is queued
            app.MapPost("/api/x86/input", (X86InputRequest? r) =>
            {
                if (!DebugControlAllowed) return ControlRefused();
                if (r == null) return Results.BadRequest(new { success = false, error = "a body {script} or {clear: true}" });
                if (r.Clear) { ApiPcInput.Clear(); return Results.Ok(new { success = true, cleared = true }); }
                if (string.IsNullOrEmpty(r.Script)) return Results.BadRequest(new { success = false, error = "script is empty" });
                if (GetConsoleSession?.Invoke() is not PcSession) return Results.BadRequest(new { success = false, error = "the running console is not the x86 PC" });
                PcInputScript s;
                try { s = PcInputScript.Parse(r.Script); }
                catch (ArgumentException ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }
                ApiPcInput.Enqueue(s);
                return Results.Ok(new { success = true, steps = s.Pending, pending = ApiPcInput.Pending });
            });

            // GET /api/x86/input - steps still queued (0 = everything ran)
            app.MapGet("/api/x86/input", () => Results.Ok(new { success = true, pending = ApiPcInput.Pending, control = DebugControlAllowed }));
        }
    }
}
