using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Memory Access API endpoints
        /// </summary>
        // ---- the consoles (BrokenNes 2): while a SNES / WideMess / Game Boy / Sega game runs, the /api/memory routes answer with its
        // memories (the debugger's domains: WRAM, VRAM, CGRAM, OAM, sound RAM, SRAM, ROM, registers, the CPU bus; Game Boy WRAM, VRAM,
        // OAM, HRAM, cartridge RAM, ROM, I/O; Sega's), by the same domain ids /api/console/domains lists. While the NES runs they are as before.

        private IReadOnlyList<IMemoryDomain>? ConsoleMemory(out string console)
        {
            console = "nes";
            var s = GetConsoleSession?.Invoke();
            if (s == null) return null;
            console = Consoles.Key(s.Console);
            try { if (Debuggers.Machine(s, out _) is { } m) return m.Domains(); } catch { }
            return MachineDomains.For(s);
        }

        private T BetweenFrames<T>(Func<T> work) => ConsoleBetweenFrames != null ? (T)ConsoleBetweenFrames(() => work()!) : work();

        private static IResult NoDomain(IReadOnlyList<IMemoryDomain> domains, string? name) =>
            Results.BadRequest(new { success = false, error = $"no memory called '{name}' (it has: {string.Join(", ", domains.Select(x => x.Id))})" });

        private void RegisterMemoryAccessEndpoints(WebApplication app)
        {
            // GET /api/memory/domains - Get list of available memory domains (the running console's)
            app.MapGet("/api/memory/domains", () =>
            {
                if (ConsoleMemory(out string console) is { } cd)
                    return Results.Ok(new { success = true, console, domains = cd.Select(d => new { name = d.Id, size = d.Size, description = d.Label, kind = d.Kind.ToString().ToLowerInvariant(), writable = d.Writable, unavailable = d.Unavailable }) });
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }
                
                var domains = nes.GetAvailableMemoryDomains();
                return Results.Ok(new
                {
                    success = true,
                    domains = domains.Select(d => new
                    {
                        name = d.Name,
                        size = d.Size,
                        description = d.Description
                    })
                });
            });

            // GET /api/memory/domain/{domainName}/size - Get size of specific domain
            app.MapGet("/api/memory/domain/{domainName}/size", (string domainName) =>
            {
                if (ConsoleMemory(out _) is { } cd)
                    return MachineDomains.Find(cd, domainName) is { } dm ? Results.Ok(new { success = true, domain = dm.Id, size = dm.Size }) : NoDomain(cd, domainName);
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }
                
                try
                {
                    var size = nes.GetMemoryDomainSize(domainName);
                    return Results.Ok(new
                    {
                        success = true,
                        domain = domainName,
                        size = size
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // GET /api/memory/peek?domain={domainName}&address={address} - Read single byte
            app.MapGet("/api/memory/peek", (string domain, int address) =>
            {
                if (ConsoleMemory(out _) is { } cd)
                {
                    var dm = MachineDomains.Find(cd, domain);
                    if (dm == null) return NoDomain(cd, domain);
                    if (dm.Unavailable != null || address < 0 || address >= dm.Size) return Results.BadRequest(new { success = false, error = dm.Unavailable ?? $"'{dm.Id}' is {dm.Size:N0} bytes long" });
                    return Results.Ok(new { success = true, domain = dm.Id, address, value = BetweenFrames(() => (int)dm.Peek(address)) });
                }
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }
                
                try
                {
                    var value = nes.PeekMemory(domain, address);
                    return Results.Ok(new
                    {
                        success = true,
                        domain = domain,
                        address = address,
                        value = value
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // POST /api/memory/poke - Write single byte
            app.MapPost("/api/memory/poke", async (HttpContext context) =>
            {
                ExternalChange?.Invoke("the game's memory was written through the HTTP API");
                if (ConsoleMemory(out _) is { } cd)
                {
                    var f = await context.Request.ReadFromJsonAsync<PokeRequest>();
                    var dm = f == null ? null : MachineDomains.Find(cd, f.Domain.Trim());
                    if (dm == null) return NoDomain(cd, f?.Domain);
                    if (dm.Unavailable != null || !dm.Writable || f!.Address < 0 || f.Address >= dm.Size) return Results.BadRequest(new { success = false, error = dm.Unavailable ?? (!dm.Writable ? $"'{dm.Id}' is read only" : $"'{dm.Id}' is {dm.Size:N0} bytes long") });
                    var (b, a) = BetweenFrames(() => { int b0 = dm.Peek(f.Address); dm.Poke(f.Address, f.Value); return (b0, (int)dm.Peek(f.Address)); });
                    return Results.Ok(new { success = true, domain = dm.Id, address = f.Address, value = f.Value, beforeValue = b, afterValue = a, verified = a == f.Value });
                }
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }
                
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<PokeRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // Normalize domain name
                    var normalizedDomain = form.Domain?.Trim() ?? "";
                    
                    // Read the value before writing
                    var beforeValue = nes.PeekMemory(normalizedDomain, form.Address);
                    
                    // Write the new value
                    nes.PokeMemory(normalizedDomain, form.Address, form.Value);
                    
                    // Read back to verify
                    var afterValue = nes.PeekMemory(normalizedDomain, form.Address);
                    
                    // Log for debugging
                    System.Diagnostics.Debug.WriteLine($"[WebAPI] poke domain='{normalizedDomain}' addr={form.Address} val={form.Value:X2} before={beforeValue:X2} after={afterValue:X2}");
                    
                    return Results.Ok(new
                    {
                        success = true,
                        domain = normalizedDomain,
                        address = form.Address,
                        value = form.Value,
                        beforeValue = beforeValue,
                        afterValue = afterValue,
                        verified = afterValue == form.Value
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // GET /api/memory/peek-range?domain={domainName}&address={address}&length={length}
            app.MapGet("/api/memory/peek-range", (string domain, int address, int length) =>
            {
                if (ConsoleMemory(out _) is { } cd)
                {
                    var dm = MachineDomains.Find(cd, domain);
                    if (dm == null) return NoDomain(cd, domain);
                    if (dm.Unavailable != null || address < 0 || address >= dm.Size || length < 1) return Results.BadRequest(new { success = false, error = dm.Unavailable ?? $"'{dm.Id}' is {dm.Size:N0} bytes long" });
                    int n = Math.Min(Math.Min(length, 65536), dm.Size - address);
                    var data = BetweenFrames(() => Enumerable.Range(address, n).Select(x => (int)dm.Peek(x)).ToArray());
                    return Results.Ok(new { success = true, domain = dm.Id, address, length = n, data });
                }
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }
                
                try
                {
                    // Normalize domain name (trim whitespace)
                    var normalizedDomain = domain?.Trim() ?? "";
                    var data = nes.PeekMemoryRange(normalizedDomain, address, length);
                    
                    // Convert byte[] to int[] so JSON serializer doesn't Base64 encode it
                    var dataAsInts = data.Select(b => (int)b).ToArray();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        domain = normalizedDomain,
                        address = address,
                        length = length,
                        data = dataAsInts
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // POST /api/memory/poke-range - Write multiple bytes
            app.MapPost("/api/memory/poke-range", async (HttpContext context) =>
            {
                ExternalChange?.Invoke("the game's memory was written through the HTTP API");
                if (ConsoleMemory(out _) is { } cd)
                {
                    var f = await context.Request.ReadFromJsonAsync<PokeRangeRequest>();
                    var dm = f == null ? null : MachineDomains.Find(cd, f.Domain.Trim());
                    if (dm == null) return NoDomain(cd, f?.Domain);
                    if (dm.Unavailable != null || !dm.Writable || f!.Address < 0 || f.Address + f.Data.Length > dm.Size) return Results.BadRequest(new { success = false, error = dm.Unavailable ?? (!dm.Writable ? $"'{dm.Id}' is read only" : $"'{dm.Id}' is {dm.Size:N0} bytes long") });
                    BetweenFrames(() => { for (int i = 0; i < f.Data.Length; i++) dm.Poke(f.Address + i, (byte)f.Data[i]); return 0; });
                    return Results.Ok(new { success = true, domain = dm.Id, address = f.Address, length = f.Data.Length });
                }
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }
                
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<PokeRangeRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // Convert int[] to byte[]
                    byte[] dataBytes = form.Data.Select(x => (byte)x).ToArray();
                    nes.PokeMemoryRange(form.Domain, form.Address, dataBytes);
                    return Results.Ok(new
                    {
                        success = true,
                        domain = form.Domain,
                        address = form.Address,
                        length = form.Data.Length
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // GET /api/health - Basic health check
            app.MapGet("/api/health", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    status = "running",
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            });

            // GET /api/memory/test-poke - Diagnostic endpoint to test poke/peek consistency
            app.MapGet("/api/memory/test-poke", (string domain, int address, byte value) =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }
                
                try
                {
                    // Read original value
                    var original = nes.PeekMemory(domain, address);
                    
                    // Write test value
                    nes.PokeMemory(domain, address, value);
                    
                    // Read back immediately
                    var afterPoke = nes.PeekMemory(domain, address);
                    
                    // Write back original
                    nes.PokeMemory(domain, address, original);
                    
                    // Verify restoration
                    var afterRestore = nes.PeekMemory(domain, address);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        domain = domain,
                        address = address,
                        testValue = value,
                        original = original,
                        afterPoke = afterPoke,
                        afterRestore = afterRestore,
                        pokeWorked = afterPoke == value,
                        restoreWorked = afterRestore == original
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
        }
    }
}
