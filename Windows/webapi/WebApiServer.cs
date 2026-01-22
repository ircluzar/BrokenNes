using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NesEmulator;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>
    /// Lightweight HTTP API server for webmodule integration.
    /// Listens only on localhost:42067 to avoid requiring admin privileges.
    /// </summary>
    public class WebApiServer : IDisposable
    {
        private readonly int _port = 42067;
        private readonly string _loopbackAddress = "127.0.0.1";
        private IHost? _host;
        private Func<NES?> _getNes;
        private Func<Corruptor?> _getCorruptor;
        private CancellationTokenSource? _cancellationTokenSource;

        public WebApiServer(Func<NES?> getNes, Func<Corruptor?>? getCorruptor = null)
        {
            _getNes = getNes;
            _getCorruptor = getCorruptor ?? (() => null);
        }

        /// <summary>
        /// Start the web API server on localhost:42067
        /// </summary>
        public async Task StartAsync()
        {
            if (_host != null)
            {
                throw new InvalidOperationException("Web API server is already running");
            }

            _cancellationTokenSource = new CancellationTokenSource();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppDomain.CurrentDomain.BaseDirectory
            });

            // Configure to listen only on loopback
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, _port);
            });

            // Suppress most logging to avoid console spam
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            // Enable CORS for webmodule access
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()  // Allow all origins since we're already restricted to loopback
                    .AllowAnyMethod()
                    .AllowAnyHeader();
                });
            });

            var app = builder.Build();

            app.UseCors();

            // Register API endpoints
            RegisterMemoryAccessEndpoints(app);
            RegisterCpuStateEndpoints(app);
            RegisterPpuStateEndpoints(app);
            RegisterApuStateEndpoints(app);
            RegisterRtcEndpoints(app);
            RegisterGlitchHarvesterEndpoints(app);

            _host = app;

            // Start server in background
            await _host.StartAsync(_cancellationTokenSource.Token);
        }

        /// <summary>
        /// Stop the web API server
        /// </summary>
        public async Task StopAsync()
        {
            if (_host != null)
            {
                _cancellationTokenSource?.Cancel();
                await _host.StopAsync();
                _host.Dispose();
                _host = null;
            }
        }

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

                    nes.PokeMemory(form.Domain, form.Address, form.Value);
                    return Results.Ok(new
                    {
                        success = true,
                        domain = form.Domain,
                        address = form.Address,
                        value = form.Value
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
                    var data = nes.PeekMemoryRange(domain, address, length);
                    return Results.Ok(new
                    {
                        success = true,
                        domain = domain,
                        address = address,
                        length = length,
                        data = data
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
        }

        /// <summary>
        /// Register CPU State Access API endpoints
        /// </summary>
        private void RegisterCpuStateEndpoints(WebApplication app)
        {
            // GET /api/cpu/registers - Get CPU registers
            app.MapGet("/api/cpu/registers", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var regs = nes.GetCpuRegisters();
                    return Results.Ok(new
                    {
                        success = true,
                        registers = new
                        {
                            PC = $"0x{regs.PC:X4}",
                            A = $"0x{regs.A:X2}",
                            X = $"0x{regs.X:X2}",
                            Y = $"0x{regs.Y:X2}",
                            P = $"0x{regs.P:X2}",
                            SP = $"0x{regs.SP:X4}"
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/cpu/core - Get CPU core ID
            app.MapGet("/api/cpu/core", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var coreId = nes.GetCpuCoreIdentifier();
                    return Results.Ok(new
                    {
                        success = true,
                        coreId = coreId
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/cpu/cores - Get available CPU cores
            app.MapGet("/api/cpu/cores", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var cores = nes.GetAvailableCpuCores();
                    return Results.Ok(new
                    {
                        success = true,
                        cores = cores
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/cpu/state - Get full CPU state snapshot
            app.MapGet("/api/cpu/state", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var state = nes.GetCpuStateSnapshot();
                    return Results.Ok(new
                    {
                        success = true,
                        state = state
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Register PPU State Access API endpoints
        /// </summary>
        private void RegisterPpuStateEndpoints(WebApplication app)
        {
            // GET /api/ppu/framebuffer - Get screen pixels
            app.MapGet("/api/ppu/framebuffer", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var framebuffer = nes.GetFramebuffer();
                    return Results.Ok(new
                    {
                        success = true,
                        width = 256,
                        height = 240,
                        format = "RGBA",
                        data = framebuffer
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/ppu/core - Get PPU core ID
            app.MapGet("/api/ppu/core", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var coreId = nes.GetPpuCoreIdentifier();
                    return Results.Ok(new
                    {
                        success = true,
                        coreId = coreId
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/ppu/cores - Get available PPU cores
            app.MapGet("/api/ppu/cores", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var cores = nes.GetAvailablePpuCores();
                    return Results.Ok(new
                    {
                        success = true,
                        cores = cores
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/ppu/state - Get full PPU state snapshot
            app.MapGet("/api/ppu/state", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var state = nes.GetPpuStateSnapshot();
                    return Results.Ok(new
                    {
                        success = true,
                        state = state
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Register APU State Access API endpoints
        /// </summary>
        private void RegisterApuStateEndpoints(WebApplication app)
        {
            // GET /api/apu/core - Get APU core ID
            app.MapGet("/api/apu/core", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var coreId = nes.GetApuCoreIdentifier();
                    return Results.Ok(new
                    {
                        success = true,
                        coreId = coreId
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/apu/cores - Get available APU cores
            app.MapGet("/api/apu/cores", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var cores = nes.GetAvailableApuCores();
                    return Results.Ok(new
                    {
                        success = true,
                        cores = cores
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Register Real-Time Corruptor (RTC) API endpoints
        /// </summary>
        private void RegisterRtcEndpoints(WebApplication app)
        {
            // Placeholder endpoints for RTC - these would connect to a Corruptor instance
            // For now, they return minimal responses to satisfy the API contract
            
            // GET /api/rtc/domains - Get memory domains available for corruption
            app.MapGet("/api/rtc/domains", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var domains = nes.GetAvailableMemoryDomains();
                    return Results.Ok(new
                    {
                        success = true,
                        domains = domains.Select(d => new
                        {
                            name = d.Name,
                            size = d.Size,
                            description = d.Description,
                            selected = d.Name == "System RAM" || d.Name == "PRG ROM"
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/rtc/domains/selection - Set memory domain selection
            app.MapPost("/api/rtc/domains/selection", async (HttpContext context) =>
            {
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<DomainSelectionRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // This would update the corruptor's domain selection
                    return Results.Ok(new
                    {
                        success = true,
                        selectedDomains = form.SelectedDomains
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/intensity - Get corruption intensity
            app.MapGet("/api/rtc/intensity", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    intensity = 1 // Default value
                });
            });

            // POST /api/rtc/intensity - Set corruption intensity
            app.MapPost("/api/rtc/intensity", async (HttpContext context) =>
            {
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<IntensityRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var intensity = Math.Clamp(form.Intensity, 1, 65535);
                    return Results.Ok(new
                    {
                        success = true,
                        intensity = intensity
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/blast-type - Get current blast type
            app.MapGet("/api/rtc/blast-type", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    blastType = "RANDOM"
                });
            });

            // POST /api/rtc/blast-type - Set blast type
            app.MapPost("/api/rtc/blast-type", async (HttpContext context) =>
            {
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<BlastTypeRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var validTypes = new[] { "RANDOM", "TILT", "RANDOMTILT", "NOP", "BITFLIP", "IMAGINE_NEXT", "IMAGINE_RANDOM" };
                    if (!validTypes.Contains(form.BlastType?.ToUpperInvariant()))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid blast type" });
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        blastType = form.BlastType?.ToUpperInvariant()
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/rtc/blast - Execute a corruption blast
            app.MapPost("/api/rtc/blast", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    message = "Blast executed",
                    writesApplied = 1
                });
            });

            // GET /api/rtc/auto-corrupt - Get auto-corrupt state
            app.MapGet("/api/rtc/auto-corrupt", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    autoCorrupt = false
                });
            });

            // POST /api/rtc/auto-corrupt - Toggle auto-corrupt
            app.MapPost("/api/rtc/auto-corrupt", async (HttpContext context) =>
            {
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<AutoCorruptRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        autoCorrupt = form.Enabled
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/rtc/let-it-rip - Apply "Let It Rip" preset
            app.MapPost("/api/rtc/let-it-rip", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    message = "Let It Rip applied",
                    intensity = 1,
                    autoCorrupt = true,
                    selectedDomains = new[] { "PRG ROM", "System RAM" }
                });
            });

            // GET /api/rtc/crash-behavior - Get current crash handling mode
            app.MapGet("/api/rtc/crash-behavior", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                var crashed = nes.IsCrashed();
                return Results.Ok(new
                {
                    success = true,
                    crashBehavior = "RedScreen",
                    crashed = crashed
                });
            });

            // POST /api/rtc/crash-behavior - Set crash handling mode
            app.MapPost("/api/rtc/crash-behavior", async (HttpContext context) =>
            {
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<CrashBehaviorRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var validBehaviors = new[] { "RedScreen", "IgnoreErrors", "ImagineFix" };
                    if (!validBehaviors.Contains(form.Behavior))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid crash behavior" });
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        crashBehavior = form.Behavior
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/stubborn-mode - Get stubborn mode state
            app.MapGet("/api/rtc/stubborn-mode", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    stubbornMode = false
                });
            });

            // POST /api/rtc/stubborn-mode - Set stubborn mode
            app.MapPost("/api/rtc/stubborn-mode", async (HttpContext context) =>
            {
                try
                {
                    var form = await context.Request.ReadFromJsonAsync<StubbornModeRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var nes = _getNes();
                    if (nes != null)
                    {
                        nes.SetStubbornFixEnabled(form.Enabled);
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        stubbornMode = form.Enabled
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/last-blast - Get info about last corruption operation
            app.MapGet("/api/rtc/last-blast", () =>
            {
                return Results.Ok(new
                {
                    success = true,
                    lastBlast = new
                    {
                        timestamp = DateTime.UtcNow.ToString("o"),
                        blastType = "RANDOM",
                        intensity = 1,
                        writesApplied = 1,
                        domainsAffected = new[] { "System RAM" }
                    }
                });
            });

            // GET /api/ppu/oam - Get OAM (sprite) data
            app.MapGet("/api/ppu/oam", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                // OAM is 256 bytes (64 sprites * 4 bytes each)
                // For now, return empty array as placeholder
                var oamData = new byte[256];
                
                return Results.Ok(new
                {
                    success = true,
                    size = 256,
                    spriteCount = 64,
                    data = oamData
                });
            });

            // GET /api/apu/channels - Get APU channels state
            app.MapGet("/api/apu/channels", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    channels = new
                    {
                        pulse1 = new { enabled = true, frequency = 440, volume = 15 },
                        pulse2 = new { enabled = true, frequency = 440, volume = 15 },
                        triangle = new { enabled = true, frequency = 220, volume = 15 },
                        noise = new { enabled = false, frequency = 0, volume = 0 },
                        dmc = new { enabled = false, frequency = 0, volume = 0 }
                    }
                });
            });

            // POST /api/cpu/registers - Set CPU registers
            app.MapPost("/api/cpu/registers", async (HttpContext context) =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<SetRegistersRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // Note: This is a placeholder. The actual NES class doesn't have SetCpuRegisters yet
                    // You would need to implement this in the NES class or use SetCpuState with a crafted state object
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Registers updated (placeholder)",
                        registers = form
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        /// <summary>
        /// Register Glitch Harvester (GH) API endpoints
        /// </summary>
        private void RegisterGlitchHarvesterEndpoints(WebApplication app)
        {
            // GET /api/gh/base-states - Get all base states
            app.MapGet("/api/gh/base-states", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var baseStates = gh.GetAllBaseStates();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        selectedId = gh.SelectedBaseId,
                        baseStates = baseStates.Select(b => new
                        {
                            b.Id,
                            b.Name,
                            b.Created
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/base-state - Add a new base state
            app.MapPost("/api/gh/base-state", async (HttpContext context) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<AddBaseStateRequest>();
                    var name = form?.Name;
                    
                    var gh = corruptor.GlitchHarvester;
                    var baseState = gh.AddBaseState(nes, name);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        baseState = new
                        {
                            baseState.Id,
                            baseState.Name,
                            baseState.Created
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/base-state/{id} - Get selected base state ID
            app.MapGet("/api/gh/selected-base", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                var gh = corruptor.GlitchHarvester;
                return Results.Ok(new
                {
                    success = true,
                    selectedId = gh.SelectedBaseId
                });
            });

            // POST /api/gh/select-base - Set selected base state
            app.MapPost("/api/gh/select-base", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<SelectBaseRequest>();
                    if (form == null || string.IsNullOrEmpty(form.Id))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.SelectBaseState(form.Id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        selectedId = gh.SelectedBaseId
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/load-base - Load selected base state
            app.MapPost("/api/gh/load-base", () =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.LoadSelectedBase(nes);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Base state loaded"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/base-state/{id} - Delete a base state
            app.MapDelete("/api/gh/base-state/{id}", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.DeleteBaseState(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Base state deleted"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/load-on-operation - Get load on operation setting
            app.MapGet("/api/gh/load-on-operation", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                var gh = corruptor.GlitchHarvester;
                return Results.Ok(new
                {
                    success = true,
                    loadOnOperation = gh.LoadOnOperation
                });
            });

            // POST /api/gh/load-on-operation - Set load on operation setting
            app.MapPost("/api/gh/load-on-operation", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<LoadOnOperationRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.LoadOnOperation = form.Enabled;
                    
                    return Results.Ok(new
                    {
                        success = true,
                        loadOnOperation = gh.LoadOnOperation
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/corrupt-and-stash - Corrupt and add to stash
            app.MapPost("/api/gh/corrupt-and-stash", () =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var entry = gh.CorruptAndStash(nes);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        entry = new
                        {
                            entry.Id,
                            entry.Name,
                            entry.BaseStateId,
                            entry.Created,
                            writeCount = entry.Writes.Count
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/stash - Get all stash entries
            app.MapGet("/api/gh/stash", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var stash = gh.GetStash();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        stash = stash.Select(e => new
                        {
                            e.Id,
                            e.Name,
                            e.BaseStateId,
                            e.Created,
                            writeCount = e.Writes.Count
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stash/{id}/replay - Replay a stash entry
            app.MapPost("/api/gh/stash/{id}/replay", (string id) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.ReplayStashEntry(nes, id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stash entry replayed"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stash/{id}/promote - Promote stash entry to stockpile
            app.MapPost("/api/gh/stash/{id}/promote", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var entry = gh.PromoteToStockpile(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        entry = new
                        {
                            entry.Id,
                            entry.Name,
                            entry.Created
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/stash/{id} - Delete a stash entry
            app.MapDelete("/api/gh/stash/{id}", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.DeleteStashEntry(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stash entry deleted"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/stash - Clear all stash entries
            app.MapDelete("/api/gh/stash", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.ClearStash();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stash cleared"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/stockpile - Get all stockpile entries
            app.MapGet("/api/gh/stockpile", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var stockpile = gh.GetStockpile();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        stockpile = stockpile.Select(e => new
                        {
                            e.Id,
                            e.Name,
                            e.BaseStateId,
                            e.Created,
                            writeCount = e.Writes.Count
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stockpile/{id}/replay - Replay a stockpile entry
            app.MapPost("/api/gh/stockpile/{id}/replay", (string id) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.ReplayStockpileEntry(nes, id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile entry replayed"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // PUT /api/gh/stockpile/{id}/rename - Rename a stockpile entry
            app.MapPut("/api/gh/stockpile/{id}/rename", async (string id, HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<RenameRequest>();
                    if (form == null || string.IsNullOrWhiteSpace(form.Name))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.RenameStockpileEntry(id, form.Name);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile entry renamed"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/stockpile/{id} - Delete a stockpile entry
            app.MapDelete("/api/gh/stockpile/{id}", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.DeleteStockpileEntry(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile entry deleted"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/stockpile/export - Export stockpile as JSON
            app.MapGet("/api/gh/stockpile/export", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var json = gh.ExportStockpile();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        json = json
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stockpile/import - Import stockpile from JSON
            app.MapPost("/api/gh/stockpile/import", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<ImportRequest>();
                    if (form == null || string.IsNullOrWhiteSpace(form.Json))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.ImportStockpile(form.Json);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile imported"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _host?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        // Request models
        private class PokeRequest
        {
            public string Domain { get; set; } = "";
            public int Address { get; set; }
            public byte Value { get; set; }
        }

        private class PokeRangeRequest
        {
            public string Domain { get; set; } = "";
            public int Address { get; set; }
            public int[] Data { get; set; } = Array.Empty<int>();
        }

        private class DomainSelectionRequest
        {
            public string[] SelectedDomains { get; set; } = Array.Empty<string>();
        }

        private class IntensityRequest
        {
            public int Intensity { get; set; }
        }

        private class BlastTypeRequest
        {
            public string BlastType { get; set; } = "";
        }

        private class AutoCorruptRequest
        {
            public bool Enabled { get; set; }
        }

        private class CrashBehaviorRequest
        {
            public string Behavior { get; set; } = "";
        }

        private class StubbornModeRequest
        {
            public bool Enabled { get; set; }
        }

        private class SetRegistersRequest
        {
            public ushort? PC { get; set; }
            public byte? A { get; set; }
            public byte? X { get; set; }
            public byte? Y { get; set; }
            public byte? P { get; set; }
            public ushort? SP { get; set; }
        }
        
        private class AddBaseStateRequest
        {
            public string? Name { get; set; }
        }
        
        private class SelectBaseRequest
        {
            public string Id { get; set; } = "";
        }
        
        private class LoadOnOperationRequest
        {
            public bool Enabled { get; set; }
        }
        
        private class RenameRequest
        {
            public string Name { get; set; } = "";
        }
        
        private class ImportRequest
        {
            public string Json { get; set; } = "";
        }
    }
}
