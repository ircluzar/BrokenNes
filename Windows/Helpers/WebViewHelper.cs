using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
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
        public static async void InitializeWebViewAsync(WebView2 webView, Action<bool> onInitialized)
        {
            if (webView == null) return;
            
            try
            {
                await webView.EnsureCoreWebView2Async(null);
                
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
                
                onInitialized?.Invoke(true);
                Console.WriteLine("WebView2 initialized successfully with transparency");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebView2 initialization error: {ex.Message}");
                MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", 
                    "WebView2 Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                onInitialized?.Invoke(false);
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

        public static bool IsAvailable(WebView2 webView, bool isInitialized, bool showMessage = true)
        {
            if (webView == null)
            {
                if (showMessage)
                {
                    MessageBox.Show("WebView2 is not available.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
