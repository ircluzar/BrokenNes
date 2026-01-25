using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NesEmulator;
using Microsoft.Web.WebView2.WinForms;

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
        private Func<ImagineEngine?> _getImagineEngine;
        private Action<string>? _setCrashBehavior;
        private CancellationTokenSource? _cancellationTokenSource;
        private Func<WebView2?> _getWebView;
        private Action<ViewMode>? _switchViewMode;
        private Control? _uiControl;
        private Action? _closeAllMenus;
        private Action? _toggleFullscreen;
        private Func<AudioEngine?> _getAudioEngine;
        private Func<string, Task<bool>>? _loadBuiltInRom;

        public bool IsRunning => _host != null;

        public WebApiServer(Func<NES?> getNes, Func<Corruptor?>? getCorruptor = null, Func<ImagineEngine?>? getImagineEngine = null, Action<string>? setCrashBehavior = null, Func<WebView2?>? getWebView = null, Action<ViewMode>? switchViewMode = null, Control? uiControl = null, Action? closeAllMenus = null, Action? toggleFullscreen = null, Func<AudioEngine?>? getAudioEngine = null, Func<string, Task<bool>>? loadBuiltInRom = null)
        {
            _getNes = getNes;
            _getCorruptor = getCorruptor ?? (() => null);
            _getImagineEngine = getImagineEngine ?? (() => null);
            _setCrashBehavior = setCrashBehavior;
            _getWebView = getWebView ?? (() => null);
            _switchViewMode = switchViewMode;
            _uiControl = uiControl;
            _getAudioEngine = getAudioEngine ?? (() => null);
            _closeAllMenus = closeAllMenus;
            _toggleFullscreen = toggleFullscreen;
            _loadBuiltInRom = loadBuiltInRom;
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
                // Also listen on HTTPS for WebView2 compatibility (development certificate)
                options.Listen(IPAddress.Loopback, _port + 1, listenOptions =>
                {
                    listenOptions.UseHttps();
                });
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
            RegisterImagineEndpoints(app);
            RegisterNavigationEndpoints(app);
            RegisterCardEndpoints(app);
            RegisterCoresEndpoints(app);
            RegisterSaveEndpoints(app);
            RegisterUIEndpoints(app);
            RegisterAudioEndpoints(app);
            RegisterEmulatorEndpoints(app);

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
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    return Results.Ok(new
                    {
                        success = true,
                        domains = corruptor.MemoryDomains.Select(d => new
                        {
                            key = d.Key,
                            name = d.Label,
                            size = d.Size,
                            selected = d.Selected
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
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<DomainSelectionRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    // Update domain selection
                    corruptor.DomainsChanged(form.SelectedDomains ?? Array.Empty<string>());
                    
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
                var corruptor = _getCorruptor();
                return Results.Ok(new
                {
                    success = true,
                    intensity = corruptor?.CorruptIntensity ?? 1
                });
            });

            // POST /api/rtc/intensity - Set corruption intensity
            app.MapPost("/api/rtc/intensity", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<IntensityRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var intensity = Math.Clamp(form.Intensity, 1, 65535);
                    corruptor.CorruptIntensity = intensity;
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
                var corruptor = _getCorruptor();
                return Results.Ok(new
                {
                    success = true,
                    blastType = corruptor?.BlastType ?? "RANDOM"
                });
            });

            // POST /api/rtc/blast-type - Set blast type
            app.MapPost("/api/rtc/blast-type", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

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

                    corruptor.BlastType = form.BlastType?.ToUpperInvariant() ?? "RANDOM";
                    return Results.Ok(new
                    {
                        success = true,
                        blastType = corruptor.BlastType
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

                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    corruptor.Blast(nes);
                    return Results.Ok(new
                    {
                        success = true,
                        message = corruptor.LastBlastInfo,
                        writesApplied = corruptor.CorruptIntensity
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/auto-corrupt - Get auto-corrupt state
            app.MapGet("/api/rtc/auto-corrupt", () =>
            {
                var corruptor = _getCorruptor();
                return Results.Ok(new
                {
                    success = true,
                    autoCorrupt = corruptor?.AutoCorrupt ?? false
                });
            });

            // POST /api/rtc/auto-corrupt - Toggle auto-corrupt
            app.MapPost("/api/rtc/auto-corrupt", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<AutoCorruptRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    corruptor.AutoCorrupt = form.Enabled;
                    return Results.Ok(new
                    {
                        success = true,
                        autoCorrupt = corruptor.AutoCorrupt
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
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    corruptor.LetItRip();
                    return Results.Ok(new
                    {
                        success = true,
                        message = corruptor.LastBlastInfo,
                        intensity = corruptor.CorruptIntensity,
                        autoCorrupt = corruptor.AutoCorrupt,
                        selectedDomains = corruptor.MemoryDomains
                            .Where(d => d.Selected)
                            .Select(d => d.Label)
                            .ToArray()
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/rtc/crash-behavior - Get current crash handling mode
            app.MapGet("/api/rtc/crash-behavior", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                var corruptor = _getCorruptor();
                var crashed = nes.IsCrashed();
                return Results.Ok(new
                {
                    success = true,
                    crashBehavior = corruptor?.CrashBehavior ?? "IgnoreErrors",
                    crashed = crashed
                });
            });

            // POST /api/rtc/crash-behavior - Set crash handling mode
            app.MapPost("/api/rtc/crash-behavior", async (HttpContext context) =>
            {
                try
                {
                    var corruptor = _getCorruptor();
                    if (corruptor == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                    }

                    var nes = _getNes();
                    if (nes == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                    }

                    var form = await context.Request.ReadFromJsonAsync<CrashBehaviorRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var behavior = form.Behavior ?? "IgnoreErrors";
                    
                    // Use the callback to properly save config if available
                    if (_setCrashBehavior != null)
                    {
                        _setCrashBehavior(behavior);
                    }
                    else
                    {
                        // Fallback: just update corruptor and emulator
                        corruptor.CrashBehavior = behavior;
                        
                        // Actually apply the crash behavior to the emulator
                        switch (behavior)
                        {
                            case "IgnoreErrors":
                                nes.SetCrashBehavior(NES.CrashBehavior.IgnoreErrors);
                                break;
                            case "ImagineFix":
                                nes.SetCrashBehavior(NES.CrashBehavior.ImagineFix);
                                break;
                            default: // "RedScreen"
                                nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);
                                break;
                        }
                    }
                    
                    return Results.Ok(new
                    {
                        success = true,
                        crashBehavior = corruptor.CrashBehavior
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

            // POST /api/gh/load-base - Load base state (by ID or currently selected)
            app.MapPost("/api/gh/load-base", async (HttpContext context) =>
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
                    
                    // Try to read request body for optional ID
                    var form = await context.Request.ReadFromJsonAsync<LoadBaseRequest>();
                    
                    if (form != null && !string.IsNullOrEmpty(form.Id))
                    {
                        // Load specific base state by ID
                        gh.LoadBaseState(nes, form.Id);
                    }
                    else
                    {
                        // Load currently selected base state (backward compatibility)
                        gh.LoadSelectedBase(nes);
                    }
                    
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
            app.MapPost("/api/gh/corrupt-and-stash", (HttpContext context) =>
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
                    
                    // Try to read the request body for optional base state ID
                    CorruptAndStashRequest? form = null;
                    try
                    {
                        form = context.Request.ReadFromJsonAsync<CorruptAndStashRequest>().Result;
                    }
                    catch { /* Ignore parse errors */ }
                    
                    // Use the provided base state ID if available, otherwise use the selected one
                    var entry = (form?.Id != null) 
                        ? gh.CorruptAndStash(nes, form.Id)
                        : gh.CorruptAndStash(nes);
                    
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

        /// <summary>
        /// Register Imagine (AI-Powered Corruption) API endpoints
        /// </summary>
        private void RegisterImagineEndpoints(WebApplication app)
        {
            // GET /api/imagine/model-loaded - Check if model is loaded
            app.MapGet("/api/imagine/model-loaded", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    modelLoaded = imagine.ModelLoaded
                });
            });

            // GET /api/imagine/epoch - Get current epoch number
            app.MapGet("/api/imagine/epoch", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    epoch = imagine.Epoch,
                    label = imagine.EpLabel
                });
            });

            // POST /api/imagine/epoch - Set epoch to load
            app.MapPost("/api/imagine/epoch", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<EpochRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    imagine.Epoch = form.Epoch;
                    
                    return Results.Ok(new
                    {
                        success = true,
                        epoch = imagine.Epoch
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/load-model - Load AI model by epoch
            app.MapPost("/api/imagine/load-model", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<EpochRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    bool loaded = imagine.LoadModel(form.Epoch);
                    
                    return Results.Ok(new
                    {
                        success = loaded,
                        modelLoaded = imagine.ModelLoaded,
                        epoch = imagine.Epoch,
                        label = imagine.EpLabel
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/imagine/generation-params - Get generation parameters
            app.MapGet("/api/imagine/generation-params", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    bytesToGenerate = imagine.BytesToGenerate,
                    temperature = imagine.Temperature,
                    topK = imagine.TopK
                });
            });

            // POST /api/imagine/generation-params - Set generation parameters
            app.MapPost("/api/imagine/generation-params", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<GenerationParamsRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    if (form.BytesToGenerate.HasValue)
                        imagine.BytesToGenerate = form.BytesToGenerate.Value;
                    
                    if (form.Temperature.HasValue)
                        imagine.Temperature = form.Temperature.Value;
                    
                    if (form.TopK.HasValue)
                        imagine.TopK = form.TopK.Value;
                    
                    return Results.Ok(new
                    {
                        success = true,
                        bytesToGenerate = imagine.BytesToGenerate,
                        temperature = imagine.Temperature,
                        topK = imagine.TopK
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/freeze-and-fetch - Capture CPU state snapshot
            app.MapPost("/api/imagine/freeze-and-fetch", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var snapshot = imagine.CaptureSnapshot();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Snapshot captured",
                        snapshot = new
                        {
                            snapshot.CpuCoreId,
                            snapshot.PC,
                            snapshot.A,
                            snapshot.X,
                            snapshot.Y,
                            snapshot.P,
                            snapshot.SP,
                            snapshot.IRQ,
                            snapshot.NMI,
                            snapshot.InPrgRom,
                            prev8 = snapshot.Prev8,
                            next16 = snapshot.Next16
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/imagine/cpu-snapshot - Read captured CPU state
            app.MapGet("/api/imagine/cpu-snapshot", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                if (imagine.Snapshot == null)
                {
                    return Results.BadRequest(new { success = false, error = "No snapshot captured" });
                }

                var snapshot = imagine.Snapshot;
                return Results.Ok(new
                {
                    success = true,
                    snapshot = new
                    {
                        snapshot.CpuCoreId,
                        snapshot.PC,
                        snapshot.A,
                        snapshot.X,
                        snapshot.Y,
                        snapshot.P,
                        snapshot.SP,
                        snapshot.IRQ,
                        snapshot.NMI,
                        snapshot.InPrgRom,
                        prev8 = snapshot.Prev8,
                        next16 = snapshot.Next16
                    }
                });
            });

            // POST /api/imagine/run-prediction - Generate predicted bytes from current state
            app.MapPost("/api/imagine/run-prediction", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var predictedBytes = imagine.PredictFromSnapshot();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        predictedBytes = predictedBytes,
                        length = predictedBytes.Length
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/apply-patch - Write predicted bytes to memory
            app.MapPost("/api/imagine/apply-patch", async (HttpContext context) =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<ApplyPatchRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    bool applied = imagine.ApplyPatch(form.Pc, form.Bytes);
                    
                    return Results.Ok(new
                    {
                        success = applied,
                        message = applied ? "Patch applied" : "Failed to apply patch",
                        error = applied ? null : imagine.LastError
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/imagine/imagine-a-bug - Automatic corruption using AI prediction
            app.MapPost("/api/imagine/imagine-a-bug", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                try
                {
                    bool success = imagine.ImagineBug();
                    
                    return Results.Ok(new
                    {
                        success = success,
                        message = success ? "Bug imagined successfully" : "Failed to imagine bug",
                        error = success ? null : imagine.LastError,
                        predictedBytes = imagine.PredictedBytes
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/imagine/predicted-bytes - Get last AI prediction result
            app.MapGet("/api/imagine/predicted-bytes", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                if (imagine.PredictedBytes == null)
                {
                    return Results.Ok(new
                    {
                        success = true,
                        predictedBytes = (byte[]?)null,
                        length = 0
                    });
                }

                return Results.Ok(new
                {
                    success = true,
                    predictedBytes = imagine.PredictedBytes,
                    length = imagine.PredictedBytes.Length
                });
            });

            // GET /api/imagine/last-error - Get last Imagine error message
            app.MapGet("/api/imagine/last-error", () =>
            {
                var imagine = _getImagineEngine();
                if (imagine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Imagine engine not initialized" });
                }

                return Results.Ok(new
                {
                    success = true,
                    lastError = imagine.LastError
                });
            });
        }

        public void Dispose()
        {
            try
            {
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }
            
            try
            {
                _host?.Dispose();
            }
            catch { }
            
            try
            {
                _cancellationTokenSource?.Dispose();
            }
            catch { }
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
        
        private class LoadBaseRequest
        {
            public string? Id { get; set; }
        }
        
        private class CorruptAndStashRequest
        {
            public string? Id { get; set; }
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
        
        private class EpochRequest
        {
            public int Epoch { get; set; }
        }
        
        private class GenerationParamsRequest
        {
            public int? BytesToGenerate { get; set; }
            public float? Temperature { get; set; }
            public int? TopK { get; set; }
        }
        
        private class ApplyPatchRequest
        {
            public ushort Pc { get; set; }
            public byte[] Bytes { get; set; } = Array.Empty<byte>();
        }

        private class NavigateRequest
        {
            public string Url { get; set; } = "";
        }

        /// <summary>
        /// Register Navigation/Routing API endpoints
        /// </summary>
        private void RegisterNavigationEndpoints(WebApplication app)
        {
            // POST /api/navigation/navigate - Navigate to a page
            app.MapPost("/api/navigation/navigate", async (HttpContext context) =>
            {
                var webView = _getWebView();
                if (webView == null || webView.CoreWebView2 == null)
                {
                    return Results.BadRequest(new { success = false, error = "WebView not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<NavigateRequest>();
                    if (form == null || string.IsNullOrEmpty(form.Url))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid URL" });
                    }

                    // Navigate within the Blazor app
                    string script = $"window.location.href = '{form.Url.Replace("'", "\\'")}'";
                    await webView.CoreWebView2.ExecuteScriptAsync(script);

                    return Results.Ok(new
                    {
                        success = true,
                        url = form.Url
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

            // GET /api/navigation/query-params - Get URL query parameters
            app.MapGet("/api/navigation/query-params", async () =>
            {
                var webView = _getWebView();
                if (webView == null || webView.CoreWebView2 == null)
                {
                    return Results.BadRequest(new { success = false, error = "WebView not initialized" });
                }

                try
                {
                    // Get current URL from WebView
                    string script = "window.location.search";
                    string queryString = await webView.CoreWebView2.ExecuteScriptAsync(script);
                    
                    // Remove quotes from JSON string result
                    queryString = queryString.Trim('"');
                    
                    // Parse query string
                    var queryParams = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(queryString) && queryString.StartsWith("?"))
                    {
                        var collection = HttpUtility.ParseQueryString(queryString);
                        foreach (string key in collection.AllKeys)
                        {
                            if (key != null)
                            {
                                queryParams[key] = collection[key] ?? "";
                            }
                        }
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        queryParams = queryParams
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

            // GET /api/navigation/build-url - Build URL with parameters
            app.MapGet("/api/navigation/build-url", (string basePath, string? queryParams) =>
            {
                try
                {
                    string url = basePath;
                    if (!string.IsNullOrEmpty(queryParams))
                    {
                        // Parse query params if provided as JSON string or key=value pairs
                        if (queryParams.Contains("="))
                        {
                            // Already in query string format
                            url += "?" + queryParams;
                        }
                        else
                        {
                            // Assume JSON format - parse and convert
                            try
                            {
                                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(queryParams);
                                if (parsed != null && parsed.Any())
                                {
                                    var queryStringParams = string.Join("&", parsed.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
                                    url += "?" + queryStringParams;
                                }
                            }
                            catch
                            {
                                // If JSON parsing fails, just append as-is
                                url += "?" + queryParams;
                            }
                        }
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        url = url
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

            // GET /api/navigation/current-route - Get current page path
            app.MapGet("/api/navigation/current-route", async () =>
            {
                var webView = _getWebView();
                if (webView == null || webView.CoreWebView2 == null)
                {
                    return Results.BadRequest(new { success = false, error = "WebView not initialized" });
                }

                try
                {
                    // Get current pathname from WebView
                    string script = "window.location.pathname";
                    string pathname = await webView.CoreWebView2.ExecuteScriptAsync(script);
                    
                    // Remove quotes from JSON string result
                    pathname = pathname.Trim('"');

                    // Get full URL too
                    string fullUrlScript = "window.location.href";
                    string fullUrl = await webView.CoreWebView2.ExecuteScriptAsync(fullUrlScript);
                    fullUrl = fullUrl.Trim('"');

                    return Results.Ok(new
                    {
                        success = true,
                        pathname = pathname,
                        fullUrl = fullUrl
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

            // POST /api/navigation/go-to-emulator - Switch to emulator mode (hide webform)
            app.MapPost("/api/navigation/go-to-emulator", () =>
            {
                try
                {
                    if (_switchViewMode == null)
                    {
                        return Results.BadRequest(new { success = false, error = "View mode switching not available" });
                    }

                    if (_uiControl == null || _uiControl.IsDisposed)
                    {
                        return Results.BadRequest(new { success = false, error = "UI control not available" });
                    }

                    // Switch to Emulator mode on UI thread, which hides the webform
                    if (_uiControl.InvokeRequired)
                    {
                        _uiControl.BeginInvoke((MethodInvoker)delegate
                        {
                            _switchViewMode(ViewMode.Emulator);
                        });
                    }
                    else
                    {
                        _switchViewMode(ViewMode.Emulator);
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        mode = "Emulator"
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

        /// <summary>
        /// Register Card SVG API endpoints
        /// </summary>
        private void RegisterCardEndpoints(WebApplication app)
        {
            // GET /api/card/{domain}/{id} - Get SVG for a specific card
            app.MapGet("/api/card/{domain}/{id}", (string domain, string id) =>
            {
                try
                {
                    // Initialize registries
                    CoreRegistry.Initialize();

                    // Build card model from core metadata
                    CoreCardModel? cardModel = null;
                    var normalizedDomain = domain.ToUpperInvariant();

                    if (normalizedDomain == "CPU")
                    {
                        var cpuTypes = CoreRegistry.CpuTypes;
                        if (cpuTypes.TryGetValue(id, out var type))
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = SafeGetStaticString(type, "CoreName") ?? id,
                                Description = SafeGetStaticString(type, "Description") ?? string.Empty,
                                Rating = SafeGetStaticInt(type, "Rating") ?? 0,
                                Performance = SafeGetStaticInt(type, "Performance") ?? 0,
                                FooterNote = SafeGetStaticString(type, "Category") ?? "CPU",
                                Domain = "CPU"
                            };
                        }
                    }
                    else if (normalizedDomain == "PPU")
                    {
                        var ppuTypes = CoreRegistry.PpuTypes;
                        if (ppuTypes.TryGetValue(id, out var type))
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = SafeGetStaticString(type, "CoreName") ?? id,
                                Description = SafeGetStaticString(type, "Description") ?? string.Empty,
                                Rating = SafeGetStaticInt(type, "Rating") ?? 0,
                                Performance = SafeGetStaticInt(type, "Performance") ?? 0,
                                FooterNote = SafeGetStaticString(type, "Category") ?? "PPU",
                                Domain = "PPU"
                            };
                        }
                    }
                    else if (normalizedDomain == "APU")
                    {
                        var apuTypes = CoreRegistry.ApuTypes;
                        if (apuTypes.TryGetValue(id, out var type))
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = SafeGetStaticString(type, "CoreName") ?? id,
                                Description = SafeGetStaticString(type, "Description") ?? string.Empty,
                                Rating = SafeGetStaticInt(type, "Rating") ?? 0,
                                Performance = SafeGetStaticInt(type, "Performance") ?? 0,
                                FooterNote = SafeGetStaticString(type, "Category") ?? "APU",
                                Domain = "APU"
                            };
                        }
                    }
                    else if (normalizedDomain == "CLOCK")
                    {
                        // ClockRegistry not available in Windows project - return placeholder
                        cardModel = new CoreCardModel
                        {
                            Id = id,
                            ShortName = id,
                            DisplayName = id,
                            Description = "Clock timing core",
                            Rating = 0,
                            Performance = 0,
                            FooterNote = "CLOCK",
                            Domain = "CLOCK"
                        };
                    }
                    else if (normalizedDomain == "SHADER")
                    {
                        // Get shader metadata from HLSL file comments
                        var shaderInfo = Rendering.NesShaderControl.GetShaderInfoById(id);
                        if (shaderInfo != null)
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = !string.IsNullOrWhiteSpace(shaderInfo.DisplayName) ? shaderInfo.DisplayName : id,
                                Description = !string.IsNullOrWhiteSpace(shaderInfo.Description) ? shaderInfo.Description : "Shader effect",
                                Rating = shaderInfo.Rating,
                                Performance = shaderInfo.Performance,
                                FooterNote = !string.IsNullOrWhiteSpace(shaderInfo.Category) ? shaderInfo.Category : "SHADER",
                                Domain = "SHADER"
                            };
                        }
                        else
                        {
                            // Fallback for unknown shader IDs
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = id,
                                Description = "Shader effect",
                                Rating = 0,
                                Performance = 0,
                                FooterNote = "SHADER",
                                Domain = "SHADER"
                            };
                        }
                    }

                    if (cardModel == null)
                    {
                        return Results.NotFound(new { success = false, error = $"Card not found: {domain}/{id}" });
                    }

                    // Render SVG
                    var svg = CardSvgRenderer.Render(cardModel);

                    // Return as SVG content type
                    return Results.Content(svg, "image/svg+xml");
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

            // Helper methods for safe reflection access (support both static and instance properties)
            static string? SafeGetStaticString(Type type, string propertyName)
            {
                try
                {
                    // First try static property
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        return prop.GetValue(null) as string;
                    }
                    // Fall back to instance property - use GetUninitializedObject to avoid constructor issues
                    prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        // Create uninitialized instance without calling constructor (avoids dependency issues)
                        var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                        if (instance != null)
                        {
                            return prop.GetValue(instance) as string;
                        }
                    }
                }
                catch { }
                return null;
            }

            static int? SafeGetStaticInt(Type type, string propertyName)
            {
                try
                {
                    // First try static property
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        return (int?)prop.GetValue(null);
                    }
                    // Fall back to instance property - use GetUninitializedObject to avoid constructor issues
                    prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        // Create uninitialized instance without calling constructor (avoids dependency issues)
                        var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                        if (instance != null)
                        {
                            return (int?)prop.GetValue(instance);
                        }
                    }
                }
                catch { }
                return null;
            }

            static T? SafeGet<T>(Func<T> getter)
            {
                try { return getter(); } catch { return default; }
            }

            static T? SafeGetStruct<T>(Func<T> getter) where T : struct
            {
                try { return getter(); } catch { return null; }
            }
        }

        private void RegisterCoresEndpoints(WebApplication app)
        {
            // GET /api/cores - Get metadata for all available cores
            app.MapGet("/api/cores", () =>
            {
                try
                {
                    // Initialize core registry
                    CoreRegistry.Initialize();

                    var result = new
                    {
                        cpu = GetCpuMetadata(),
                        ppu = GetPpuMetadata(),
                        apu = GetApuMetadata(),
                        clock = new List<object>(), // ClockRegistry not available in Windows project
                        shader = GetShaderMetadata()
                    };

                    return Results.Ok(result);
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

            static List<object> GetCpuMetadata()
            {
                try
                {
                    var cpuTypes = CoreRegistry.CpuTypes;
                    var result = new List<object>();
                    
                    foreach (var kvp in cpuTypes)
                    {
                        var id = kvp.Key;
                        var type = kvp.Value;
                        result.Add(new
                        {
                            id = id,
                            name = SafeGetInstanceString(type, "CoreName") ?? id,
                            description = SafeGetInstanceString(type, "Description") ?? $"CPU core {id}",
                            performance = SafeGetInstanceInt(type, "Performance") ?? 0,
                            rating = SafeGetInstanceInt(type, "Rating") ?? 3,
                            category = SafeGetInstanceString(type, "Category") ?? "Processor"
                        });
                    }
                    
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            static List<object> GetPpuMetadata()
            {
                try
                {
                    var ppuTypes = CoreRegistry.PpuTypes;
                    var result = new List<object>();
                    
                    foreach (var kvp in ppuTypes)
                    {
                        var id = kvp.Key;
                        var type = kvp.Value;
                        result.Add(new
                        {
                            id = id,
                            name = SafeGetInstanceString(type, "CoreName") ?? id,
                            description = SafeGetInstanceString(type, "Description") ?? $"PPU core {id}",
                            performance = SafeGetInstanceInt(type, "Performance") ?? 0,
                            rating = SafeGetInstanceInt(type, "Rating") ?? 3,
                            category = SafeGetInstanceString(type, "Category") ?? "Graphics"
                        });
                    }
                    
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            static List<object> GetApuMetadata()
            {
                try
                {
                    var apuTypes = CoreRegistry.ApuTypes;
                    var result = new List<object>();
                    
                    foreach (var kvp in apuTypes)
                    {
                        var id = kvp.Key;
                        var type = kvp.Value;
                        result.Add(new
                        {
                            id = id,
                            name = SafeGetInstanceString(type, "CoreName") ?? id,
                            description = SafeGetInstanceString(type, "Description") ?? $"APU core {id}",
                            performance = SafeGetInstanceInt(type, "Performance") ?? 0,
                            rating = SafeGetInstanceInt(type, "Rating") ?? 3,
                            category = SafeGetInstanceString(type, "Category") ?? "Audio"
                        });
                    }
                    
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            static List<object> GetClockMetadata()
            {
                // Clock cores from Windows/NesEmulator/clocks
                var clockDescriptions = new Dictionary<string, (string Name, string Description, int Performance, int Rating)>
                {
                    ["FMC"] = ("Standard Clock", "Default NES clock timing", 0, 5),
                    ["TRB"] = ("Turbo Clock", "Overclocked CPU timing for faster gameplay", 2, 4),
                    ["CLR"] = ("Clear Clock", "Ultra-precise timing with reduced jitter", 1, 4)
                };

                var result = new List<object>();
                foreach (var kv in clockDescriptions)
                {
                    result.Add(new
                    {
                        id = kv.Key,
                        name = kv.Value.Name,
                        description = kv.Value.Description,
                        performance = kv.Value.Performance,
                        rating = kv.Value.Rating,
                        category = "Clock"
                    });
                }
                return result;
            }

            static List<object> GetShaderMetadata()
            {
                try
                {
                    var result = new List<object>();
                    // Use the HlslMetadataParser to get actual metadata from HLSL shader files
                    foreach (Rendering.NesShaderManager.ShaderType shaderType in Enum.GetValues(typeof(Rendering.NesShaderManager.ShaderType)))
                    {
                        var id = shaderType.ToString();
                        var info = Rendering.NesShaderControl.GetShaderInfo(shaderType);
                        result.Add(new
                        {
                            id = id,
                            name = info.DisplayName,
                            description = info.Description,
                            performance = info.Performance,
                            rating = info.Rating,
                            category = info.Category
                        });
                    }
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            // Helper to read instance properties from core types by creating a temporary instance
            static string? SafeGetInstanceString(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        // Try to create instance with parameterless constructor or Bus constructor
                        object? instance = null;
                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        if (ctor != null)
                        {
                            instance = ctor.Invoke(null);
                        }
                        else
                        {
                            // Try constructor with Bus parameter (common for cores)
                            var busCtor = type.GetConstructor(new[] { typeof(Bus) });
                            if (busCtor != null)
                            {
                                instance = busCtor.Invoke(new object?[] { null });
                            }
                        }
                        if (instance != null)
                        {
                            return prop.GetValue(instance) as string;
                        }
                    }
                }
                catch { }
                return null;
            }

            static int? SafeGetInstanceInt(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        // Try to create instance with parameterless constructor or Bus constructor
                        object? instance = null;
                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        if (ctor != null)
                        {
                            instance = ctor.Invoke(null);
                        }
                        else
                        {
                            // Try constructor with Bus parameter (common for cores)
                            var busCtor = type.GetConstructor(new[] { typeof(Bus) });
                            if (busCtor != null)
                            {
                                instance = busCtor.Invoke(new object?[] { null });
                            }
                        }
                        if (instance != null)
                        {
                            return (int?)prop.GetValue(instance);
                        }
                    }
                }
                catch { }
                return null;
            }

            static string? SafeGetStaticString(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        return prop.GetValue(null) as string;
                    }
                }
                catch { }
                return null;
            }

            static int? SafeGetStaticInt(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        return (int?)prop.GetValue(null);
                    }
                }
                catch { }
                return null;
            }

            static T? SafeGet<T>(Func<T> getter)
            {
                try { return getter(); } catch { return default; }
            }

            static T? SafeGetStruct<T>(Func<T> getter) where T : struct
            {
                try { return getter(); } catch { return null; }
            }
        }

        private void RegisterSaveEndpoints(WebApplication app)
        {
            // GET /api/save - Get game save data (owned cores, etc.)
            app.MapGet("/api/save", async () =>
            {
                try
                {
                    var savePath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "BrokenNes",
                        "gamesave.json"
                    );

                    if (!System.IO.File.Exists(savePath))
                    {
                        // Return default empty save
                        return Results.Ok(new
                        {
                            ownedCpuIds = new string[] { "FMC" },
                            ownedPpuIds = new string[] { "FMC" },
                            ownedApuIds = new string[] { "FMC" },
                            ownedClockIds = new string[0],
                            ownedShaderIds = new string[] { "PX" }
                        });
                    }

                    var json = await System.IO.File.ReadAllTextAsync(savePath);
                    var options = new System.Text.Json.JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    };
                    var save = System.Text.Json.JsonSerializer.Deserialize<GameSaveDto>(json, options);

                    return Results.Ok(save);
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

        private class GameSaveDto
        {
            public int Level { get; set; } = 1;
            public bool LevelCleared { get; set; } = false;
            public List<string>? Achievements { get; set; }
            public bool SavestatesUnlocked { get; set; } = false;
            public bool RtcUnlocked { get; set; } = false;
            public bool GhUnlocked { get; set; } = false;
            public bool ImagineUnlocked { get; set; } = false;
            public bool DebugUnlocked { get; set; } = false;
            public bool SeenStory { get; set; } = false;
            public string[]? OwnedCpuIds { get; set; }
            public string[]? OwnedPpuIds { get; set; }
            public string[]? OwnedApuIds { get; set; }
            public string[]? OwnedClockIds { get; set; }
            public string[]? OwnedShaderIds { get; set; }
            public string? PreferredCpuId { get; set; }
            public string? PreferredPpuId { get; set; }
            public string? PreferredApuId { get; set; }
            public string? PreferredShaderId { get; set; }
            public bool PendingDeckContinue { get; set; } = false;
            public string? PendingDeckContinueRom { get; set; }
            public string? PendingDeckContinueTitle { get; set; }
            public DateTime? PendingDeckContinueAtUtc { get; set; }
            public bool UnderConstructionAcknowledged { get; set; } = false;
            public bool AllCoresUnlockedCongrats { get; set; } = false;
            public Dictionary<string, string>? MasqueradeRomToGameId { get; set; }
        }

        /// <summary>
        /// Register UI Control API endpoints
        /// </summary>
        private void RegisterUIEndpoints(WebApplication app)
        {
            // POST /api/ui/close-menus - Close all open menus
            app.MapPost("/api/ui/close-menus", () =>
            {
                if (_closeAllMenus == null)
                {
                    return Results.BadRequest(new { success = false, error = "Close menus handler not available" });
                }

                try
                {
                    // Invoke on UI thread if we have a control reference
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_closeAllMenus);
                    }
                    else
                    {
                        _closeAllMenus();
                    }

                    return Results.Ok(new { success = true });
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
            
            // POST /api/ui/toggle-fullscreen - Toggle fullscreen mode
            app.MapPost("/api/ui/toggle-fullscreen", () =>
            {
                if (_toggleFullscreen == null)
                {
                    return Results.BadRequest(new { success = false, error = "Toggle fullscreen handler not available" });
                }

                try
                {
                    // Invoke on UI thread if we have a control reference
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_toggleFullscreen);
                    }
                    else
                    {
                        _toggleFullscreen();
                    }

                    return Results.Ok(new { success = true });
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

        /// <summary>
        /// Register Audio Engine API endpoints
        /// </summary>
        private void RegisterAudioEndpoints(WebApplication app)
        {
            // GET /api/audio/music/current - Get currently playing music
            app.MapGet("/api/audio/music/current", () =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                return Results.Ok(new
                {
                    success = true,
                    currentFile = audioEngine.CurrentMusicFile,
                    isPlaying = audioEngine.IsMusicPlaying
                });
            });

            // GET /api/audio/music/list - List available music files
            app.MapGet("/api/audio/music/list", () =>
            {
                try
                {
                    var musicFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "music");
                    if (!Directory.Exists(musicFolder))
                    {
                        return Results.Ok(new { success = true, files = Array.Empty<string>() });
                    }

                    var files = Directory.GetFiles(musicFolder)
                        .Select(Path.GetFileName)
                        .Where(f => f != null && (f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(f => f)
                        .ToArray();

                    return Results.Ok(new { success = true, files });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/audio/sfx/list - List available SFX files
            app.MapGet("/api/audio/sfx/list", () =>
            {
                try
                {
                    var sfxFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "sfx");
                    if (!Directory.Exists(sfxFolder))
                    {
                        return Results.Ok(new { success = true, files = Array.Empty<string>() });
                    }

                    var files = Directory.GetFiles(sfxFolder)
                        .Select(Path.GetFileName)
                        .Where(f => f != null && (f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(f => f)
                        .ToArray();

                    return Results.Ok(new { success = true, files });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/sfx/play - Play a sound effect
            app.MapPost("/api/audio/sfx/play", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    await audioEngine.PlaySfxAsync(body.Filename);
                    return Results.Ok(new { success = true, filename = body.Filename });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/music/play - Play music directly
            app.MapPost("/api/audio/music/play", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    var loop = body.Loop ?? true;
                    await audioEngine.PlayMusicAsync(body.Filename, loop);
                    return Results.Ok(new { success = true, filename = body.Filename, loop });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/music/request - Request music with crossfade
            app.MapPost("/api/audio/music/request", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    var loop = body.Loop ?? true;
                    var fadeDurationMs = body.FadeDurationMs ?? 1000;
                    await audioEngine.RequestMusicAsync(body.Filename, loop, fadeDurationMs);
                    return Results.Ok(new { success = true, filename = body.Filename, loop, fadeDurationMs });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/audio/music/stop - Stop music with fade-out
            app.MapPost("/api/audio/music/stop", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioPlayRequest>();
                    var fadeDurationMs = body?.FadeDurationMs ?? 1000;
                    await audioEngine.StopMusicAsync(fadeDurationMs);
                    return Results.Ok(new { success = true, fadeDurationMs });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/audio/volume - Get current volume levels
            app.MapGet("/api/audio/volume", () =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                return Results.Ok(new
                {
                    success = true,
                    musicVolume = audioEngine.MusicVolume,
                    sfxVolume = audioEngine.SfxVolume
                });
            });

            // POST /api/audio/volume - Set volume levels
            app.MapPost("/api/audio/volume", async (HttpContext context) =>
            {
                var audioEngine = _getAudioEngine();
                if (audioEngine == null)
                {
                    return Results.BadRequest(new { success = false, error = "Audio engine not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<AudioVolumeRequest>();
                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Volume data is required" });
                    }

                    if (body.MusicVolume.HasValue)
                        audioEngine.MusicVolume = body.MusicVolume.Value;
                    
                    if (body.SfxVolume.HasValue)
                        audioEngine.SfxVolume = body.SfxVolume.Value;

                    return Results.Ok(new
                    {
                        success = true,
                        musicVolume = audioEngine.MusicVolume,
                        sfxVolume = audioEngine.SfxVolume
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        // Request classes for audio endpoints
        private class AudioPlayRequest
        {
            public string? Filename { get; set; }
            public bool? Loop { get; set; }
            public int? FadeDurationMs { get; set; }
        }

        private class AudioVolumeRequest
        {
            public float? MusicVolume { get; set; }
            public float? SfxVolume { get; set; }
        }

        private void RegisterEmulatorEndpoints(WebApplication app)
        {
            // POST /api/emulator/load-builtin-rom - Load a built-in ROM file (for story mode)
            app.MapPost("/api/emulator/load-builtin-rom", async (HttpContext context) =>
            {
                if (_loadBuiltInRom == null)
                {
                    return Results.BadRequest(new { success = false, error = "ROM loading not available" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<LoadBuiltInRomRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.Filename))
                    {
                        return Results.BadRequest(new { success = false, error = "Filename is required" });
                    }

                    bool success = await _loadBuiltInRom(body.Filename);

                    return Results.Ok(new
                    {
                        success,
                        filename = body.Filename
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        private class LoadBuiltInRomRequest
        {
            public string? Filename { get; set; }
        }
    }
}
