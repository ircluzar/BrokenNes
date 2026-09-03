using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Http;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using BrokenNes.Windows.Rendering;
using BrokenNes.Windows.WebApi;

namespace BrokenNes.Windows.Helpers
{
    /// <summary>
    /// Helper class for managing WebView2 instances and initialization
    /// </summary>
    public static class WebViewHelper
    {
        private static CoreWebView2Environment? _sharedEnvironment;
        private static readonly HttpClient ProxyHttpClient = new();

        /// <summary>
        /// Tracks each initialized WebView2 plus the id of the document-created script that
        /// defines window.BROKENNES_API_BASE, so the value can be re-injected when the API
        /// server's real port becomes known (or changes).
        /// </summary>
        private sealed class ApiBaseInjection
        {
            public required WeakReference<WebView2> View { get; init; }
            public string? ScriptId { get; set; }
            /// <summary>Null means "the currently registered script says: not resolved yet".</summary>
            public string? InjectedBaseUrl { get; set; }
        }

        private static readonly List<ApiBaseInjection> ApiBaseInjections = new();
        private static readonly object ApiBaseLock = new();

        /// <summary>
        /// Serialises the whole check-then-act of <see cref="ApplyApiBaseAsync"/>.
        ///
        /// The entry state it guards is only written AFTER an await, so with a plain field check
        /// several concurrent fire-and-forget RefreshApiBaseAsync calls all passed the "already
        /// current" guard and each registered its own document-created script. Only the last
        /// ScriptId was remembered, so the earlier registrations were leaked permanently and every
        /// future document ran the same injection two or three times. ApiBaseLock cannot be held
        /// across the await, so readiness is gated with an async semaphore instead.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim ApiBaseGate = new(1, 1);

        private static string GetBaseDirectoryProfileSuffix()
        {
            var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(baseDirectory));
            return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
        }

        private static string GetPrimaryUserDataFolder()
        {
            var basePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrokenNes",
                "WebView2",
                GetBaseDirectoryProfileSuffix());
            Directory.CreateDirectory(basePath);
            return basePath;
        }

        private static string GetFallbackUserDataFolder()
        {
            var fallbackPath = Path.Combine(
                Path.GetTempPath(),
                "BrokenNes",
                "WebView2",
                $"{GetBaseDirectoryProfileSuffix()}-{Environment.ProcessId}");
            Directory.CreateDirectory(fallbackPath);
            return fallbackPath;
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder, CoreWebView2EnvironmentOptions options)
        {
            Console.WriteLine($"[WebView2] Using user data folder: {userDataFolder}");
            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options).ConfigureAwait(false);
        }
        
        /// <summary>
        /// Gets or creates a shared WebView2 environment with autoplay enabled
        /// </summary>
        private static async Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
        {
            if (_sharedEnvironment != null)
                return _sharedEnvironment;
                
            // Create environment with Chromium flags to enable autoplay
            var options = new CoreWebView2EnvironmentOptions();
            
            // CRITICAL: Disable autoplay policy to allow AudioContext without user gesture
            // --autoplay-policy=no-user-gesture-required allows audio to play automatically
            options.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";

            var primaryUserDataFolder = GetPrimaryUserDataFolder();
            try
            {
                _sharedEnvironment = await CreateEnvironmentAsync(primaryUserDataFolder, options).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                var fallbackUserDataFolder = GetFallbackUserDataFolder();
                Console.WriteLine($"[WebView2] Access denied for primary user data folder. Falling back to: {fallbackUserDataFolder}");
                _sharedEnvironment = await CreateEnvironmentAsync(fallbackUserDataFolder, options).ConfigureAwait(false);
            }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070005)
            {
                var fallbackUserDataFolder = GetFallbackUserDataFolder();
                Console.WriteLine($"[WebView2] Primary user data folder failed with access denied. Falling back to: {fallbackUserDataFolder}");
                _sharedEnvironment = await CreateEnvironmentAsync(fallbackUserDataFolder, options).ConfigureAwait(false);
            }
                
            return _sharedEnvironment;
        }
        
        /// <summary>
        /// Script injected before any page script runs, telling webmodules which local API
        /// belongs to THIS process.
        ///
        /// baseUrl is null while the server has not bound yet. In that case we inject an explicit
        /// NOT-READY sentinel rather than the legacy fixed URL: on an instance that ends up on a
        /// fallback port, the legacy port 42067 belongs to a DIFFERENT BrokenNes process, and a
        /// document created in that window would happily talk to it. webapi.js treats
        /// BROKENNES_API_BASE_READY === false as "route through the in-process proxy / fail", never
        /// as "guess 42067". The script is re-injected with the real URL as soon as it is known.
        /// </summary>
        private static string BuildApiBaseScript(string? baseUrl)
        {
            if (baseUrl == null)
            {
                return "window.BROKENNES_API_BASE = null; window.BROKENNES_API_BASE_READY = false;";
            }

            return $"window.BROKENNES_API_BASE = {JsonSerializer.Serialize(baseUrl)}; window.BROKENNES_API_BASE_READY = true;";
        }

        private static ApiBaseInjection GetOrCreateInjection(WebView2 webView)
        {
            lock (ApiBaseLock)
            {
                // Drop entries whose WebView2 has been collected.
                ApiBaseInjections.RemoveAll(entry => !entry.View.TryGetTarget(out _));

                foreach (var entry in ApiBaseInjections)
                {
                    if (entry.View.TryGetTarget(out var existing) && ReferenceEquals(existing, webView))
                    {
                        return entry;
                    }
                }

                var created = new ApiBaseInjection { View = new WeakReference<WebView2>(webView) };
                ApiBaseInjections.Add(created);
                return created;
            }
        }

        /// <summary>
        /// (Re-)injects window.BROKENNES_API_BASE into the given WebView2 for all future
        /// navigations, and also sets it on the currently loaded document.
        /// Must be called on the UI thread.
        /// </summary>
        public static async Task ApplyApiBaseAsync(WebView2? webView)
        {
            if (webView == null || webView.IsDisposed) return;

            CoreWebView2 core;
            try
            {
                if (webView.CoreWebView2 == null) return; // Not initialized yet; init path will inject.
                core = webView.CoreWebView2;
            }
            catch
            {
                return;
            }

            // Null until the server has actually bound. Never fall back to the legacy fixed port
            // here: with several instances running, that port belongs to another process.
            var baseUrl = WebApiEndpoint.ResolvedBaseUrl;
            var entry = GetOrCreateInjection(webView);

            // Everything from the guard to the state write must be atomic with respect to other
            // callers, otherwise concurrent calls each register a script and all but the last one
            // are leaked (they stay registered forever, running on every future document).
            await ApiBaseGate.WaitAsync();
            try
            {
                if (string.Equals(entry.InjectedBaseUrl, baseUrl, StringComparison.Ordinal) && entry.ScriptId != null)
                {
                    return; // Already current.
                }

                if (entry.ScriptId != null)
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(entry.ScriptId); } catch { }
                    entry.ScriptId = null;
                    entry.InjectedBaseUrl = null;
                }

                var script = BuildApiBaseScript(baseUrl);
                entry.ScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                entry.InjectedBaseUrl = baseUrl;

                // Also update the document that is already loaded, if any.
                try { await core.ExecuteScriptAsync(script); } catch { }

                Console.WriteLine($"[WebView2] window.BROKENNES_API_BASE = {baseUrl ?? "<unresolved - API not bound yet>"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebView2] Failed to inject BROKENNES_API_BASE: {ex.Message}");
            }
            finally
            {
                ApiBaseGate.Release();
            }
        }

        /// <summary>
        /// Re-injects the API base URL into every initialized WebView2. Called once the API
        /// server has bound its real ports. Must be called on the UI thread.
        /// </summary>
        public static async Task RefreshApiBaseAsync()
        {
            List<WebView2> views = new();
            lock (ApiBaseLock)
            {
                ApiBaseInjections.RemoveAll(entry => !entry.View.TryGetTarget(out _));
                foreach (var entry in ApiBaseInjections)
                {
                    if (entry.View.TryGetTarget(out var view))
                    {
                        views.Add(view);
                    }
                }
            }

            foreach (var view in views)
            {
                await ApplyApiBaseAsync(view);
            }
        }

        /// <summary>
        /// Waits (up to <paramref name="timeout"/>) for this process's Web API to report a real
        /// bound port, returning its base URL, or null if it never resolved. Used instead of the
        /// legacy fixed-port guess anywhere a wrong answer would send traffic to a different
        /// BrokenNes instance.
        /// </summary>
        private static async Task<string?> WaitForResolvedApiBaseAsync(TimeSpan timeout)
        {
            var resolved = WebApiEndpoint.ResolvedBaseUrl;
            if (resolved != null) return resolved;

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25).ConfigureAwait(false);
                resolved = WebApiEndpoint.ResolvedBaseUrl;
                if (resolved != null) return resolved;
            }

            return null;
        }

        public static async Task<bool> InitializeWebViewAsync(WebView2 webView, bool showErrorDialog = true)
        {
            if (webView == null) return false;
            
            try
            {
                // Use our custom environment with autoplay enabled
                var environment = await GetOrCreateEnvironmentAsync();
                try
                {
                    await webView.EnsureCoreWebView2Async(environment);
                }
                catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070005)
                {
                    var fallbackUserDataFolder = GetFallbackUserDataFolder();
                    var fallbackOptions = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
                    };
                    Console.WriteLine($"[WebView2] EnsureCoreWebView2Async failed with access denied. Retrying with isolated profile: {fallbackUserDataFolder}");
                    _sharedEnvironment = await CreateEnvironmentAsync(fallbackUserDataFolder, fallbackOptions).ConfigureAwait(false);
                    await webView.EnsureCoreWebView2Async(_sharedEnvironment);
                }
                
                // Configure WebView2 settings
                var settings = webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = true;
                settings.IsWebMessageEnabled = true;
                
                // Enable transparency for overlay mode
                webView.DefaultBackgroundColor = Color.Transparent;

                // Tell page scripts which local API belongs to this process, before any of them run.
                await ApplyApiBaseAsync(webView);

                // Set up shared virtual host mapping for all webmodules
                string webmodulesPath = WebModuleManager.GetWebModulesDirectory();
                if (Directory.Exists(webmodulesPath))
                {
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        WebModuleManager.SharedVirtualHostName,
                        webmodulesPath,
                        Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                    
                    Console.WriteLine($"[WebView2] Mapped {WebModuleManager.SharedVirtualHostName} to {webmodulesPath}");
                }
                else
                {
                    Console.WriteLine($"[WebView2] Warning: Webmodules directory not found at {webmodulesPath}");
                }
                
                // Set up API proxy - intercept /api/* requests and proxy to localhost
                webView.CoreWebView2.AddWebResourceRequestedFilter($"https://{WebModuleManager.SharedVirtualHostName}/api/*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += (sender, e) =>
                {
                    var uri = e.Request.Uri;
                    if (uri.StartsWith($"https://{WebModuleManager.SharedVirtualHostName}/api/"))
                    {
                        var deferral = e.GetDeferral();
                        Task.Run(async () =>
                        {
                            try
                            {
                                // Extract the API path and proxy to localhost HTTP (avoid cert issues)
                                var apiPath = uri.Substring($"https://{WebModuleManager.SharedVirtualHostName}".Length);

                                // Resolved per-request so the proxy always targets THIS process's
                                // API, whatever port it ended up binding. If the server has not
                                // bound yet, wait briefly rather than firing at the legacy fixed
                                // port - on a fallback-port instance that port is owned by ANOTHER
                                // BrokenNes process, and silently talking to it is worse than a
                                // short delay or an honest 503.
                                var resolvedBaseUrl = await WaitForResolvedApiBaseAsync(TimeSpan.FromSeconds(5));
                                if (resolvedBaseUrl == null)
                                {
                                    Console.WriteLine($"[WebView2] API proxy refused {uri}: this process's API has not bound yet");
                                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                        null, 503, "Service Unavailable", "Retry-After: 1\r\n");
                                    return;
                                }

                                var localUrl = $"{resolvedBaseUrl}{apiPath}";

                                Console.WriteLine($"[WebView2] Proxying API request: {uri} -> {localUrl}");
                                
                                using var requestMessage = new HttpRequestMessage(new HttpMethod(e.Request.Method), localUrl);

                                foreach (var header in e.Request.Headers)
                                {
                                    if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value))
                                    {
                                        requestMessage.Content ??= new ByteArrayContent([]);
                                        requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                                    }
                                }

                                var requestContent = e.Request.Content;
                                if (requestContent != null)
                                {
                                    using var requestBuffer = new MemoryStream();
                                    if (requestContent.CanSeek)
                                    {
                                        requestContent.Position = 0;
                                    }

                                    await requestContent.CopyToAsync(requestBuffer);
                                    requestBuffer.Position = 0;
                                    requestMessage.Content = new ByteArrayContent(requestBuffer.ToArray());

                                    foreach (var header in e.Request.Headers)
                                    {
                                        requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                                    }
                                }

                                using var response = await ProxyHttpClient.SendAsync(requestMessage);
                                var responseBytes = await response.Content.ReadAsByteArrayAsync();
                                var responseStream = new MemoryStream(responseBytes);
                                var responseHeaders = new StringBuilder();

                                foreach (var header in response.Headers)
                                {
                                    responseHeaders.Append(header.Key).Append(": ").AppendJoin(", ", header.Value).Append("\r\n");
                                }

                                foreach (var header in response.Content.Headers)
                                {
                                    responseHeaders.Append(header.Key).Append(": ").AppendJoin(", ", header.Value).Append("\r\n");
                                }

                                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                    responseStream,
                                    (int)response.StatusCode,
                                    response.ReasonPhrase,
                                    responseHeaders.ToString());
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[WebView2] API proxy error: {ex.Message}");
                                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                    null, 500, "Internal Server Error", "");
                            }
                            finally
                            {
                                deferral.Complete();
                            }
                        });
                    }
                };
                
                Console.WriteLine("WebView2 initialized successfully with transparency");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebView2 initialization error: {ex.Message}");
                if (showErrorDialog)
                {
                    MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", 
                        "WebView2 Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }
        }

        public static WebView2 CreateWebView(Control parent)
        {
             var webView = new WebView2
             {
                 Visible = false // Start hidden
             };
             
             // Ensure we add it to the parent controls if specified
             if (parent != null)
             {
                 parent.Controls.Add(webView);
             }
             
             return webView;
        }

        public static bool IsAvailable(WebView2 webView, bool isInitialized, bool initializationFailed = false, bool showMessage = true)
        {
            if (webView == null)
            {
                if (showMessage)
                {
                    MessageBox.Show("WebView2 is not available.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return false;
            }

            if (initializationFailed)
            {
                if (showMessage)
                {
                    MessageBox.Show("WebView2 failed to initialize. Check the earlier WebView2 error dialog for details.",
                        "WebView2 Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return false;
            }

            if (!isInitialized)
            {
                 if (showMessage)
                {
                    MessageBox.Show("WebView2 is still initializing. Please try again in a moment.", 
                        "Please Wait", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return false;
            }

            return true;
        }

        public static void NavigateToUri(WebView2 webView, string uri)
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                webView.Source = new Uri(uri);
            }
        }

        public static void NavigateToString(WebView2 webView, string htmlContent)
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                webView.CoreWebView2.NavigateToString(htmlContent);
            }
        }

        public static void SetLayout(WebView2 webView, Point location, Size size, bool visible = true)
        {
            if (webView != null)
            {
                webView.Location = location;
                webView.Size = size;
                webView.Visible = visible;
                if (visible)
                {
                    webView.BringToFront();
                }
            }
        }
    }
}
