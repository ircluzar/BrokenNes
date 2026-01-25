using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Memory Access API endpoints
        /// </summary>
        private void RegisterMemoryAccessEndpoints(WebApplication app)
        {
            // GET /api/memory/domains - Get list of available memory domains
            app.MapGet("/api/memory/domains", () =>
            {
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
