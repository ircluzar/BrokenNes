using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
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
        private Action? _hideMenu;
        private Action? _showMenu;
        private Action<int>? _openControllerConfig;
        private Func<AudioEngine?> _getAudioEngine;
        private Func<string, bool, Task<bool>>? _loadBuiltInRom;
        private Action? _resumeEmulation;
        private Action? _pauseEmulation;
        private Action? _hideContinueButton;
        private Action? _resetGame;
        private Func<IEnumerable<string>>? _getAvailableBackgrounds;
        private Action<string>? _setBackground;
        private Func<IEnumerable<string>>? _getAvailableNullProviders;
        private Action<string>? _setNullProvider;
        private Action<string>? _setCpuCore;
        private Action<string>? _setPpuCore;
        private Action<string>? _setApuCore;
        private Action<string>? _setShader;
        private Action? _closeRom;
        private Func<string, Task<bool>>? _loadRomByKey;
        private Action<string, byte[]>? _loadRomFromBytes;
        private Func<bool>? _saveContinueState;
        private Func<string?, bool>? _loadContinueState;
        private Func<string?>? _getCurrentRomPath;
        private Func<string?>? _getCurrentRomName;
        private Func<string, bool>? _loadRomFromPath;
        private Action? _refreshProgressionUi;
        private readonly ProgressionSaveService _progressionSave;

        public bool IsRunning => _host != null;

        public WebApiServer(Func<NES?> getNes, Func<Corruptor?>? getCorruptor = null, Func<ImagineEngine?>? getImagineEngine = null, Action<string>? setCrashBehavior = null, Func<WebView2?>? getWebView = null, Action<ViewMode, bool>? switchViewMode = null, Control? uiControl = null, Action? closeAllMenus = null, Action? toggleFullscreen = null, Func<AudioEngine?>? getAudioEngine = null, Func<string, bool, Task<bool>>? loadBuiltInRom = null, Action? resumeEmulation = null, Action? pauseEmulation = null, Action? hideContinueButton = null, Func<NesEmulator.RetroAchievements.AchievementsEngine?>? getAchievementsEngine = null, Action<NesEmulator.RetroAchievements.AchievementsEngine?>? setAchievementsEngine = null, Action? hideMenu = null, Action? showMenu = null, Action? resetGame = null, Func<IEnumerable<string>>? getAvailableBackgrounds = null, Action<string>? setBackground = null, Func<IEnumerable<string>>? getAvailableNullProviders = null, Action<string>? setNullProvider = null, Action<string>? setCpuCore = null, Action<string>? setPpuCore = null, Action<string>? setApuCore = null, Action<string>? setShader = null, Action? closeRom = null, Func<string, Task<bool>>? loadRomByKey = null, Action<string, byte[]>? loadRomFromBytes = null, Func<bool>? saveContinueState = null, Func<string?, bool>? loadContinueState = null, Func<string?>? getCurrentRomPath = null, Func<string?>? getCurrentRomName = null, Func<string, bool>? loadRomFromPath = null, Action? refreshProgressionUi = null, Action<int>? openControllerConfig = null)
        {
            _progressionSave = new ProgressionSaveService();
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
            _hideMenu = hideMenu;
            _showMenu = showMenu;
            _openControllerConfig = openControllerConfig;
            _loadBuiltInRom = loadBuiltInRom;
            _resumeEmulation = resumeEmulation;
            _pauseEmulation = pauseEmulation;
            _hideContinueButton = hideContinueButton;
            _resetGame = resetGame;
            _getAvailableBackgrounds = getAvailableBackgrounds;
            _setBackground = setBackground;
            _getAvailableNullProviders = getAvailableNullProviders;
            _setNullProvider = setNullProvider;
            _setCpuCore = setCpuCore;
            _setPpuCore = setPpuCore;
            _setApuCore = setApuCore;
            _setShader = setShader;
            _closeRom = closeRom;
            _loadRomByKey = loadRomByKey;
            _loadRomFromBytes = loadRomFromBytes;
            _saveContinueState = saveContinueState;
            _loadContinueState = loadContinueState;
            _getCurrentRomPath = getCurrentRomPath;
            _getCurrentRomName = getCurrentRomName;
            _loadRomFromPath = loadRomFromPath;
            _refreshProgressionUi = refreshProgressionUi;
        }

        private void RefreshProgressionUi()
        {
            if (_refreshProgressionUi == null)
            {
                return;
            }

            if (_uiControl != null && _uiControl.InvokeRequired)
            {
                _uiControl.Invoke(_refreshProgressionUi);
                return;
            }

            _refreshProgressionUi();
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
                // Also listen on HTTPS for WebView2 compatibility (self-signed certificate)
                options.Listen(IPAddress.Loopback, _port + 1, listenOptions =>
                {
                    listenOptions.UseHttps(CreateSelfSignedCertificate());
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
            app.Use(ApplyProgressionGateAsync);

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
            RegisterProgressionEndpoints(app);
            RegisterUIEndpoints(app);
            RegisterAudioEndpoints(app);
            RegisterEmulatorEndpoints(app);
            RegisterShaderEndpoints(app);
            RegisterTimeJumpEndpoints(app);
            RegisterInputEndpoints(app);

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
        /// Creates a self-signed certificate for HTTPS on localhost.
        /// This avoids requiring the dotnet dev-certs on deployed machines.
        /// </summary>
        private static X509Certificate2 CreateSelfSignedCertificate()
        {
            var distinguishedName = new X500DistinguishedName("CN=localhost");
            
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                distinguishedName,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            
            // Add extensions for localhost usage
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
                    false));
            
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                    false));
            
            // Add Subject Alternative Name for localhost
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(sanBuilder.Build());
            
            // Create self-signed certificate valid for 1 year
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(1));
            
            // Export and reimport to make the private key usable on Windows
            // Use UserKeySet instead of MachineKeySet to avoid permission issues
            // (MachineKeySet can cause "network password is not correct" errors)
            var pfxBytes = certificate.Export(X509ContentType.Pfx, string.Empty);
            return new X509Certificate2(pfxBytes, string.Empty, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
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
