using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
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
                            _switchViewMode(ViewMode.Emulator, false);
                        });
                    }
                    else
                    {
                        _switchViewMode(ViewMode.Emulator, false);
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

            // POST /api/navigation/go-to-overlay - Switch to overlay mode (transparent WebView over emulator)
            app.MapPost("/api/navigation/go-to-overlay", () =>
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

                    // Switch to Overlay mode on UI thread (skipNavigation=true to preserve current page content)
                    // Also hide continue button if displayed (Story mode and other overlays shouldn't show it)
                    if (_uiControl.InvokeRequired)
                    {
                        _uiControl.BeginInvoke((MethodInvoker)delegate
                        {
                            _hideContinueButton?.Invoke();
                            _switchViewMode(ViewMode.Overlay, true);
                        });
                    }
                    else
                    {
                        _hideContinueButton?.Invoke();
                        _switchViewMode(ViewMode.Overlay, true);
                    }

                    Console.WriteLine("[WebApi] Switched to Overlay mode via API");
                    return Results.Ok(new
                    {
                        success = true,
                        mode = "Overlay"
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
