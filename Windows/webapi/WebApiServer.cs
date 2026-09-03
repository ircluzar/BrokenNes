using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
    /// Process-wide record of where this instance's local Web API is actually listening.
    /// Defaults to the historical fixed endpoint so any code that reads it before the
    /// server has started still behaves the way it always did.
    /// </summary>
    public static class WebApiEndpoint
    {
        public const int PreferredHttpPort = 42067;
        public const int PreferredHttpsPort = 42068;

        public static readonly string DefaultBaseUrl = $"http://127.0.0.1:{PreferredHttpPort}";

        private static string _baseUrl = DefaultBaseUrl;
        private static int _httpPort = PreferredHttpPort;
        private static int _httpsPort = PreferredHttpsPort;
        private static int _resolved;

        /// <summary>
        /// Base URL (scheme://host:port, no trailing slash) of this process's Web API.
        /// CAUTION: before <see cref="IsResolved"/> becomes true this is only the historical
        /// GUESS (port 42067), which on a machine running several instances may belong to a
        /// DIFFERENT process. Anything that would send this process's traffic somewhere must use
        /// <see cref="ResolvedBaseUrl"/> and handle null instead.
        /// </summary>
        public static string BaseUrl => Volatile.Read(ref _baseUrl);

        public static int HttpPort => Volatile.Read(ref _httpPort);

        public static int HttpsPort => Volatile.Read(ref _httpsPort);

        /// <summary>True once a server in this process has actually bound its ports.</summary>
        public static bool IsResolved => Volatile.Read(ref _resolved) != 0;

        /// <summary>
        /// This process's real API base URL, or null while the server has not bound yet. Callers
        /// that must not cross-talk with another instance should prefer this over
        /// <see cref="BaseUrl"/> and treat null as "not ready".
        /// </summary>
        public static string? ResolvedBaseUrl => IsResolved ? BaseUrl : null;

        internal static void Set(int httpPort, int httpsPort)
        {
            Volatile.Write(ref _httpPort, httpPort);
            Volatile.Write(ref _httpsPort, httpsPort);
            Volatile.Write(ref _baseUrl, $"http://127.0.0.1:{httpPort}");
            Volatile.Write(ref _resolved, 1);
        }

        internal static void Reset()
        {
            Volatile.Write(ref _resolved, 0);
            Volatile.Write(ref _httpPort, PreferredHttpPort);
            Volatile.Write(ref _httpsPort, PreferredHttpsPort);
            Volatile.Write(ref _baseUrl, DefaultBaseUrl);
        }
    }

    /// <summary>
    /// Lightweight HTTP API server for webmodule integration.
    /// Listens only on loopback to avoid requiring admin privileges. Prefers the historical
    /// fixed ports (42067 HTTP / 42068 HTTPS) and falls back to OS-assigned ephemeral ports
    /// when another instance already owns them, so every instance gets its own working API.
    /// </summary>
    public partial class WebApiServer : IDisposable
    {
        private const int PreferredHttpPort = WebApiEndpoint.PreferredHttpPort;
        private const int PreferredHttpsPort = WebApiEndpoint.PreferredHttpsPort;

        private readonly string _loopbackAddress = "127.0.0.1";
        private IHost? _host;
        private volatile bool _isRunning;
        private int _httpPort;
        private int _httpsPort;
        private string? _instanceFilePath;
        private string? _lastStartError;
        private bool _processExitHooked;
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
        private Action<string>? _setCpuCoreOverride;
        private Action<string>? _setPpuCoreOverride;
        private Action<string>? _setApuCoreOverride;
        private Action<string>? _setShaderOverride;
        private Action? _closeRom;
        private Func<string, Task<bool>>? _loadRomByKey;
        private Action<string, byte[]>? _loadRomFromBytes;
        private Func<bool>? _saveContinueState;
        private Func<string?, bool>? _loadContinueState;
        private Func<bool>? _quickSaveState;
        private Func<bool>? _quickLoadState;
        private Func<string?>? _getCurrentRomPath;
        private Func<string?>? _getCurrentRomName;
        private Func<string, bool>? _loadRomFromPath;
        private Action? _refreshProgressionUi;
        private readonly ProgressionSaveService _progressionSave;

        /// <summary>
        /// True only once the server has genuinely bound its ports and started listening.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>Actual bound HTTP port (0 until the server is listening).</summary>
        public int HttpPort => _httpPort;

        /// <summary>Actual bound HTTPS port (0 until the server is listening).</summary>
        public int HttpsPort => _httpsPort;

        /// <summary>Base URL of this instance's API, e.g. "http://127.0.0.1:42067". Empty until listening.</summary>
        public string BaseUrl => _isRunning && _httpPort > 0 ? $"http://{_loopbackAddress}:{_httpPort}" : string.Empty;

        /// <summary>True when the preferred fixed ports were unavailable and ephemeral ports were used instead.</summary>
        public bool UsingFallbackPorts => _isRunning && _httpPort != PreferredHttpPort;

        /// <summary>
        /// Non-null when the last StartAsync attempt failed to bind. Cleared on a successful start.
        /// </summary>
        public string? LastStartError => _lastStartError;

        /// <summary>Full path of the instance discovery file written while listening, if any.</summary>
        public string? InstanceFilePath => _instanceFilePath;

        public WebApiServer(Func<NES?> getNes, Func<Corruptor?>? getCorruptor = null, Func<ImagineEngine?>? getImagineEngine = null, Action<string>? setCrashBehavior = null, Func<WebView2?>? getWebView = null, Action<ViewMode, bool>? switchViewMode = null, Control? uiControl = null, Action? closeAllMenus = null, Action? toggleFullscreen = null, Func<AudioEngine?>? getAudioEngine = null, Func<string, bool, Task<bool>>? loadBuiltInRom = null, Action? resumeEmulation = null, Action? pauseEmulation = null, Action? hideContinueButton = null, Func<NesEmulator.RetroAchievements.AchievementsEngine?>? getAchievementsEngine = null, Action<NesEmulator.RetroAchievements.AchievementsEngine?>? setAchievementsEngine = null, Action? hideMenu = null, Action? showMenu = null, Action? resetGame = null, Func<IEnumerable<string>>? getAvailableBackgrounds = null, Action<string>? setBackground = null, Func<IEnumerable<string>>? getAvailableNullProviders = null, Action<string>? setNullProvider = null, Action<string>? setCpuCore = null, Action<string>? setPpuCore = null, Action<string>? setApuCore = null, Action<string>? setShader = null, Action<string>? setCpuCoreOverride = null, Action<string>? setPpuCoreOverride = null, Action<string>? setApuCoreOverride = null, Action<string>? setShaderOverride = null, Action? closeRom = null, Func<string, Task<bool>>? loadRomByKey = null, Action<string, byte[]>? loadRomFromBytes = null, Func<bool>? saveContinueState = null, Func<string?, bool>? loadContinueState = null, Func<bool>? quickSaveState = null, Func<bool>? quickLoadState = null, Func<string?>? getCurrentRomPath = null, Func<string?>? getCurrentRomName = null, Func<string, bool>? loadRomFromPath = null, Action? refreshProgressionUi = null, Action<int>? openControllerConfig = null)
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
            _setCpuCoreOverride = setCpuCoreOverride;
            _setPpuCoreOverride = setPpuCoreOverride;
            _setApuCoreOverride = setApuCoreOverride;
            _setShaderOverride = setShaderOverride;
            _closeRom = closeRom;
            _loadRomByKey = loadRomByKey;
            _loadRomFromBytes = loadRomFromBytes;
            _saveContinueState = saveContinueState;
            _loadContinueState = loadContinueState;
            _quickSaveState = quickSaveState;
            _quickLoadState = quickLoadState;
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
        /// Builds (but does not start) a fully wired WebApplication bound to the given loopback ports.
        /// Pass 0 for either port to let the OS assign an ephemeral one.
        /// </summary>
        private WebApplication BuildApp(int httpPort, int httpsPort)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppDomain.CurrentDomain.BaseDirectory
            });

            // Configure to listen only on loopback
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, httpPort);
                // Also listen on HTTPS for WebView2 compatibility (self-signed certificate)
                options.Listen(IPAddress.Loopback, httpsPort, listenOptions =>
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

            return app;
        }

        /// <summary>
        /// Start the web API server on loopback. Tries the historical fixed ports
        /// (42067 HTTP / 42068 HTTPS) first; if binding fails - typically because another
        /// BrokenNes instance already owns them - falls back to OS-assigned ephemeral ports
        /// for both. The actually bound ports are exposed via HttpPort/HttpsPort/BaseUrl.
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Web API server is already running");
            }

            // A previous failed attempt may have left a host object behind.
            if (_host != null)
            {
                try { _host.Dispose(); } catch { }
                _host = null;
            }

            _lastStartError = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            WebApplication? app = null;
            Exception? preferredFailure = null;

            // Attempt 1: the historical fixed ports, so a lone instance behaves exactly as before.
            try
            {
                app = BuildApp(PreferredHttpPort, PreferredHttpsPort);
                await app.StartAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                preferredFailure = ex;
                if (app != null)
                {
                    try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
                    app = null;
                }
            }

            // Attempt 2: ephemeral ports for both endpoints.
            if (app == null)
            {
                Console.WriteLine($"[WebAPI] Preferred ports {PreferredHttpPort}/{PreferredHttpsPort} unavailable ({preferredFailure?.Message}). Falling back to OS-assigned ports.");

                try
                {
                    app = BuildApp(0, 0);
                    await app.StartAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (app != null)
                    {
                        try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
                    }

                    _isRunning = false;
                    _httpPort = 0;
                    _httpsPort = 0;
                    _lastStartError = $"Preferred ports failed ({preferredFailure?.Message}); ephemeral fallback also failed ({ex.Message})";
                    Console.WriteLine($"[WebAPI] FAILED to start: {_lastStartError}");
                    throw;
                }
            }

            var (httpPort, httpsPort) = ReadBoundPorts(app);
            if (httpPort <= 0)
            {
                // We are listening but cannot determine where; treat as a start failure rather
                // than reporting a bogus success.
                _lastStartError = "Server started but no bound HTTP address could be resolved";
                Console.WriteLine($"[WebAPI] FAILED to start: {_lastStartError}");
                try { await app.StopAsync().ConfigureAwait(false); } catch { }
                try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
                throw new InvalidOperationException(_lastStartError);
            }

            _host = app;
            _httpPort = httpPort;
            _httpsPort = httpsPort;
            _isRunning = true;

            WebApiEndpoint.Set(httpPort, httpsPort);

            Console.WriteLine($"[WebAPI] Listening on http://{_loopbackAddress}:{httpPort} (https {httpsPort}){(UsingFallbackPorts ? " [fallback ports]" : string.Empty)}");

            PruneStaleInstanceFiles();
            WriteInstanceFile();
            HookProcessExitCleanup();
        }

        /// <summary>
        /// Reads the ports Kestrel actually bound to, which is the only reliable source when
        /// port 0 was requested.
        /// </summary>
        private static (int httpPort, int httpsPort) ReadBoundPorts(WebApplication app)
        {
            int httpPort = 0;
            int httpsPort = 0;

            IEnumerable<string>? addresses = null;
            try
            {
                addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
            }
            catch { }

            if (addresses == null || !addresses.Any())
            {
                try { addresses = app.Urls; } catch { }
            }

            if (addresses == null)
            {
                return (0, 0);
            }

            foreach (var address in addresses)
            {
                if (string.IsNullOrWhiteSpace(address)) continue;
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) continue;

                if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    if (httpsPort == 0) httpsPort = uri.Port;
                }
                else if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                {
                    if (httpPort == 0) httpPort = uri.Port;
                }
            }

            return (httpPort, httpsPort);
        }

        /// <summary>
        /// Stop the web API server
        /// </summary>
        public async Task StopAsync()
        {
            DeleteInstanceFile();

            if (_host != null)
            {
                _cancellationTokenSource?.Cancel();
                try
                {
                    await _host.StopAsync().ConfigureAwait(false);
                }
                catch { }
                _host.Dispose();
                _host = null;
            }

            _isRunning = false;
            _httpPort = 0;
            _httpsPort = 0;
            WebApiEndpoint.Reset();
        }

        #region Instance discovery file

        /// <summary>
        /// %LOCALAPPDATA%\BrokenNes\instances - one &lt;pid&gt;.json per live instance.
        /// </summary>
        internal static string GetInstancesDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrokenNes",
                "instances");
        }

        private void WriteInstanceFile()
        {
            try
            {
                var directory = GetInstancesDirectory();
                Directory.CreateDirectory(directory);

                var path = Path.Combine(directory, $"{Environment.ProcessId}.json");
                var payload = new
                {
                    pid = Environment.ProcessId,
                    httpPort = _httpPort,
                    httpsPort = _httpsPort,
                    startedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture),
                    exePath = Environment.ProcessPath ?? AppContext.BaseDirectory
                };

                File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                _instanceFilePath = path;
                Console.WriteLine($"[WebAPI] Instance file written: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebAPI] Could not write instance discovery file: {ex.Message}");
            }
        }

        private void DeleteInstanceFile()
        {
            var path = _instanceFilePath;
            if (string.IsNullOrEmpty(path)) return;

            _instanceFilePath = null;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebAPI] Could not delete instance discovery file: {ex.Message}");
            }
        }

        private void HookProcessExitCleanup()
        {
            if (_processExitHooked) return;
            _processExitHooked = true;
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteInstanceFile();
            }
            catch { }
        }

        /// <summary>
        /// Best-effort removal of discovery files left behind by instances that died without
        /// cleaning up. Only files whose PID is provably not running are removed.
        /// </summary>
        private static void PruneStaleInstanceFiles()
        {
            try
            {
                var directory = GetInstancesDirectory();
                if (!Directory.Exists(directory)) return;

                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (!int.TryParse(name, out var pid)) continue;
                        if (pid == Environment.ProcessId) continue;

                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            if (!process.HasExited) continue; // still alive - leave it alone
                        }
                        catch (ArgumentException)
                        {
                            // No such process - stale file.
                        }
                        catch
                        {
                            continue; // Anything else: be conservative and keep the file.
                        }

                        File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        #endregion

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
            DeleteInstanceFile();
            _isRunning = false;

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
