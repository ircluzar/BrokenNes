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
    public partial class WebApiServer : IDisposable
    {
        private readonly int _port = 42067;
        private readonly string _loopbackAddress = "127.0.0.1";
        private IHost? _host;
        private Func<NES?> _getNes;
        private Func<Corruptor?> _getCorruptor;
        private Func<ImagineEngine?> _getImagineEngine;
        private Func<NesEmulator.RetroAchievements.AchievementsEngine?> _getAchievementsEngine;
        private Action<NesEmulator.RetroAchievements.AchievementsEngine?> _setAchievementsEngine;
        private NesEmulator.RetroAchievements.AchievementsEngine? _localAchievementsEngine;
        private Action<string>? _setCrashBehavior;
        private CancellationTokenSource? _cancellationTokenSource;
        private Func<WebView2?> _getWebView;
        private Action<ViewMode, bool>? _switchViewMode;
        private Control? _uiControl;
        private Action? _closeAllMenus;
        private Action? _toggleFullscreen;
        private Func<AudioEngine?> _getAudioEngine;
        private Func<string, bool, Task<bool>>? _loadBuiltInRom;
        private Action? _resumeEmulation;
        private Action? _hideContinueButton;

        public bool IsRunning => _host != null;

        public WebApiServer(Func<NES?> getNes, Func<Corruptor?>? getCorruptor = null, Func<ImagineEngine?>? getImagineEngine = null, Action<string>? setCrashBehavior = null, Func<WebView2?>? getWebView = null, Action<ViewMode, bool>? switchViewMode = null, Control? uiControl = null, Action? closeAllMenus = null, Action? toggleFullscreen = null, Func<AudioEngine?>? getAudioEngine = null, Func<string, bool, Task<bool>>? loadBuiltInRom = null, Action? resumeEmulation = null, Action? hideContinueButton = null, Func<NesEmulator.RetroAchievements.AchievementsEngine?>? getAchievementsEngine = null, Action<NesEmulator.RetroAchievements.AchievementsEngine?>? setAchievementsEngine = null)
        {
            _getNes = getNes;
            _getCorruptor = getCorruptor ?? (() => null);
            _getImagineEngine = getImagineEngine ?? (() => null);
            _localAchievementsEngine = null;
            _getAchievementsEngine = getAchievementsEngine != null
                ? () => getAchievementsEngine() ?? _localAchievementsEngine
                : () => _localAchievementsEngine;
            _setAchievementsEngine = engine =>
            {
                _localAchievementsEngine = engine;
                setAchievementsEngine?.Invoke(engine);
            };
            _setCrashBehavior = setCrashBehavior;
            _getWebView = getWebView ?? (() => null);
            _switchViewMode = switchViewMode;
            _uiControl = uiControl;
            _getAudioEngine = getAudioEngine ?? (() => null);
            _closeAllMenus = closeAllMenus;
            _toggleFullscreen = toggleFullscreen;
            _loadBuiltInRom = loadBuiltInRom;
            _resumeEmulation = resumeEmulation;
            _hideContinueButton = hideContinueButton;
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
            RegisterAchievementsEndpoints(app);
            RegisterNavigationEndpoints(app);
            RegisterCardEndpoints(app);
            RegisterCoresEndpoints(app);
            RegisterSaveEndpoints(app);
            RegisterUIEndpoints(app);
            RegisterAudioEndpoints(app);
            RegisterEmulatorEndpoints(app);
            RegisterShaderEndpoints(app);

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
    }
}
