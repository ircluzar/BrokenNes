using System;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
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

            // POST /api/ui/hide-menu - Hide the top menu bar
            app.MapPost("/api/ui/hide-menu", () =>
            {
                if (_hideMenu == null)
                {
                    return Results.BadRequest(new { success = false, error = "Hide menu handler not available" });
                }

                try
                {
                    // Invoke on UI thread if we have a control reference
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_hideMenu);
                    }
                    else
                    {
                        _hideMenu();
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

            // POST /api/ui/show-menu - Show the top menu bar
            app.MapPost("/api/ui/show-menu", () =>
            {
                if (_showMenu == null)
                {
                    return Results.BadRequest(new { success = false, error = "Show menu handler not available" });
                }

                try
                {
                    // Invoke on UI thread if we have a control reference
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_showMenu);
                    }
                    else
                    {
                        _showMenu();
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
    }
}
