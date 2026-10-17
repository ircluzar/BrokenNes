using System;
using System.IO;
using System.Linq;
using BrokenNes.CorruptorModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator.Corruption;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>
    /// Glitch Harvester on console games (SNES, Game Boy, Master System, Game Gear, Genesis) and the stockpile file: what the farm can store, the stockpile saved to and opened from a file, the running game's memories.
    /// The existing /api/gh/* and /api/rtc/blast routes reach the same engine and take the console path when a console game runs.
    /// </summary>
    public partial class WebApiServer
    {
        /// <summary>The running console game's way into the Glitch Harvester (set by the main form).</summary>
        public Func<IConsoleGhHost?>? GetConsoleGh { get; set; }
        /// <summary>What the farm can do with the running game now (set by the main form).</summary>
        public Func<object>? GetFarmInfo { get; set; }
        /// <summary>Asks the player for a stockpile file (true: to save to, false: to open); null when they cancel (set by the main form).</summary>
        public Func<bool, string?>? PickStockpilePath { get; set; }
        /// <summary>The folder stockpiles are kept in by default.</summary>
        public Func<string>? GetStockpileFolder { get; set; }
        /// <summary>The NES's RTC domain sizes: probed (false, what it has always done) or true (EmulatorConfig.CorruptionTrueDomainSizes).</summary>
        public Func<bool>? GetTrueDomainSizes { get; set; }
        public Action<bool>? SetTrueDomainSizes { get; set; }
        /// <summary>The running console game's memories (set by the main form): peek, poke.</summary>
        public Func<System.Collections.Generic.IReadOnlyList<IMemoryDomain>>? GetConsoleDomains { get; set; }
        /// <summary>Runs work between frames of the running console game (set by the main form).</summary>
        public Func<Func<object>, object>? ConsoleBetweenFrames { get; set; }

        private IConsoleGhHost? ConsoleGhHost() => GetConsoleGh?.Invoke() is { Farm: not null } h ? h : null;

        private static bool AllowOtherGame(HttpContext context) => context.Request.Query["allowOtherGame"] is { Count: > 0 } q && (q[0] == "1" || string.Equals(q[0], "true", StringComparison.OrdinalIgnoreCase));

        private static string SummaryOf(HarvestEntry e)
        {
            if (e.Writes.Count == 0) return "no writes";
            var groups = e.Writes.GroupBy(w => w.Domain).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(6).Select(g => $"{g.Key} x{g.Count()}");
            return $"{e.Writes.Count} write{(e.Writes.Count == 1 ? "" : "s")}: " + string.Join(", ", groups);
        }

        private static string AnchorKindOf(HarvestEntry e) => e.Anchor != null ? e.Anchor.Kind.ToString().ToLowerInvariant() : e.State.Length > 0 ? "state" : "none";

        private void RegisterGlitchHarvesterConsoleEndpoints(WebApplication app)
        {
            // GET /api/gh/status - what the farm can do now (console, how a position is stored, the recording) and the state of the stockpile file
            app.MapGet("/api/gh/status", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null) return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                var gh = corruptor.GlitchHarvester;
                return Results.Ok(new
                {
                    success = true,
                    farm = GetFarmInfo?.Invoke(),
                    stockpile = new { path = gh.StockpilePath, name = gh.StockpileName, dirty = gh.StockpileDirty, count = gh.Stockpile.Count, folder = GetStockpileFolder?.Invoke() },
                    trueDomainSizes = GetTrueDomainSizes?.Invoke() ?? false,
                });
            });

            // GET /api/gh/stockpile/files - the stockpile files in the default folder
            app.MapGet("/api/gh/stockpile/files", () =>
            {
                string folder = GetStockpileFolder?.Invoke() ?? "";
                try
                {
                    var files = Directory.Exists(folder)
                        ? new DirectoryInfo(folder).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(200)
                            .Select(f => new { name = f.Name, path = f.FullName, bytes = f.Length, modified = f.LastWriteTimeUtc }).ToList()
                        : null;
                    return Results.Ok(new { success = true, folder, files = files ?? (object)Array.Empty<object>() });
                }
                catch (Exception ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }
            });

            // POST /api/gh/stockpile/save - save the stockpile to a file ({ path? }: no path asks the player with a Save dialog)
            app.MapPost("/api/gh/stockpile/save", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null) return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                try
                {
                    var form = await ReadOptionalAsync<StockpileFileRequest>(context);
                    var gh = corruptor.GlitchHarvester;
                    string? path = form?.Path;
                    if (string.IsNullOrWhiteSpace(path)) path = PickStockpilePath?.Invoke(true);
                    if (string.IsNullOrWhiteSpace(path)) return Results.Ok(new { success = false, cancelled = true, message = "no file chosen; nothing was saved" });
                    if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += Stockpile.FileSuffix;
                    if (!string.IsNullOrWhiteSpace(form?.Name)) gh.StockpileName = form!.Name!.Trim();
                    gh.SaveStockpileFile(path);
                    return Results.Ok(new { success = true, path, count = gh.Stockpile.Count, bytes = new FileInfo(path).Length, message = $"saved {gh.Stockpile.Count} entr{(gh.Stockpile.Count == 1 ? "y" : "ies")} to {Path.GetFileName(path)}" });
                }
                catch (Exception ex) { return Results.BadRequest(new { success = false, error = "could not save the stockpile: " + ex.Message }); }
            });

            // POST /api/gh/stockpile/open - open a stockpile file ({ path?, replace? }: no path asks the player; replace defaults to true)
            app.MapPost("/api/gh/stockpile/open", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null) return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                try
                {
                    var form = await ReadOptionalAsync<StockpileFileRequest>(context);
                    var gh = corruptor.GlitchHarvester;
                    string? path = form?.Path;
                    if (string.IsNullOrWhiteSpace(path)) path = PickStockpilePath?.Invoke(false);
                    if (string.IsNullOrWhiteSpace(path)) return Results.Ok(new { success = false, cancelled = true, message = "no file chosen" });
                    var result = gh.OpenStockpileFile(path, replace: form?.Replace ?? true);
                    return Results.Ok(new { success = true, path, imported = result.Added, legacy = result.Legacy, refusals = result.Refusals, warnings = result.Warnings, message = $"opened {Path.GetFileName(path)}: {result.Summary}" });
                }
                catch (Exception ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }
            });

            // GET/POST /api/rtc/true-domain-sizes - the NES's RTC memory domains at their true sizes (default off: the probed sizes the corruptor has always used)
            app.MapGet("/api/rtc/true-domain-sizes", () => Results.Ok(new { success = true, enabled = GetTrueDomainSizes?.Invoke() ?? false }));
            app.MapPost("/api/rtc/true-domain-sizes", async (HttpContext context) =>
            {
                var form = await ReadOptionalAsync<LoadOnOperationRequest>(context);
                if (form == null || SetTrueDomainSizes == null) return Results.BadRequest(new { success = false, error = "Invalid request" });
                SetTrueDomainSizes(form.Enabled);
                return Results.Ok(new { success = true, enabled = GetTrueDomainSizes?.Invoke() ?? false });
            });

            // GET /api/console/domains - the running console game's memories (true sizes)
            app.MapGet("/api/console/domains", () =>
            {
                var domains = GetConsoleDomains?.Invoke();
                if (domains == null || domains.Count == 0) return Results.BadRequest(new { success = false, error = "no console game is running (the NES has /api/memory/domains)" });
                return Results.Ok(new { success = true, domains = domains.Select(d => new { id = d.Id, label = d.Label, size = d.Size, kind = d.Kind.ToString().ToLowerInvariant(), writable = d.Writable, unavailable = d.Unavailable }) });
            });

            // GET /api/console/peek?domain=&address=&length= - bytes of one of the running console game's memories
            app.MapGet("/api/console/peek", (string domain, int address, int? length) =>
            {
                var domains = GetConsoleDomains?.Invoke();
                if (domains == null || domains.Count == 0) return Results.BadRequest(new { success = false, error = "no console game is running" });
                var d = MachineDomains.Find(domains, domain);
                if (d == null) return Results.BadRequest(new { success = false, error = $"no memory called '{domain}' (it has: {string.Join(", ", domains.Select(x => x.Id))})" });
                if (d.Unavailable != null) return Results.BadRequest(new { success = false, error = $"'{d.Id}' is not available: {d.Unavailable}" });
                int n = Math.Clamp(length ?? 1, 1, 4096);
                if (address < 0 || address >= d.Size) return Results.BadRequest(new { success = false, error = $"'{d.Id}' is {d.Size:N0} bytes long" });
                n = Math.Min(n, d.Size - address);
                var bytes = (int[])ConsoleBetweenFrames!(() => Enumerable.Range(address, n).Select(a => (int)d.Peek(a)).ToArray());
                return Results.Ok(new { success = true, domain = d.Id, address, length = n, data = bytes });
            });

            // POST /api/console/poke { domain, address, value } - write one byte of the running console game's memory (between frames)
            app.MapPost("/api/console/poke", async (HttpContext context) =>
            {
                var form = await ReadOptionalAsync<ConsolePokeRequest>(context);
                var domains = GetConsoleDomains?.Invoke();
                if (form == null || domains == null || domains.Count == 0) return Results.BadRequest(new { success = false, error = "no console game is running, or an invalid request" });
                var d = MachineDomains.Find(domains, form.Domain ?? "");
                if (d == null) return Results.BadRequest(new { success = false, error = $"no memory called '{form.Domain}' (it has: {string.Join(", ", domains.Select(x => x.Id))})" });
                if (d.Unavailable != null || !d.Writable) return Results.BadRequest(new { success = false, error = $"'{d.Id}' cannot be written: {d.Unavailable ?? "read only"}" });
                if (form.Address < 0 || form.Address >= d.Size || form.Value is < 0 or > 255) return Results.BadRequest(new { success = false, error = $"address or value out of range ('{d.Id}' is {d.Size:N0} bytes; a value is 0 to 255)" });
                var (before, after) = ((int, int))ConsoleBetweenFrames!(() => { int b = d.Peek(form.Address); d.Poke(form.Address, (byte)form.Value); return (b, (int)d.Peek(form.Address)); });
                return Results.Ok(new { success = true, domain = d.Id, address = form.Address, before, after });
            });
        }

        private static async System.Threading.Tasks.Task<T?> ReadOptionalAsync<T>(HttpContext context) where T : class
        {
            try { return context.Request.ContentLength is 0 ? null : await context.Request.ReadFromJsonAsync<T>(); }
            catch { return null; }
        }
    }

    internal class StockpileFileRequest
    {
        public string? Path { get; set; }
        public string? Name { get; set; }
        public bool? Replace { get; set; }
    }

    internal class ConsolePokeRequest
    {
        public string? Domain { get; set; }
        public int Address { get; set; }
        public int Value { get; set; }
    }
}
