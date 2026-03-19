using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Net.Http;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using BrokenNes.Windows.Rendering;

namespace BrokenNes.Windows.Helpers
{
    /// <summary>
    /// Helper class for managing WebView2 instances and initialization
    /// </summary>
    public static class WebViewHelper
    {
        private static CoreWebView2Environment? _sharedEnvironment;
        private static readonly HttpClient ProxyHttpClient = new();

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
                                var localUrl = $"http://localhost:42067{apiPath}";
                                
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
