using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Windows.Forms;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Shader API endpoints for managing visual effects
        /// </summary>
        private void RegisterShaderEndpoints(WebApplication app)
        {
            // GET /api/shader/current - Get current shader name and enabled state
            app.MapGet("/api/shader/current", () =>
            {
                try
                {
                    var shaderName = Rendering.NesShaderControl.GetCurrentShaderName();
                    var useShader = true;
                    
                    return Results.Ok(new
                    {
                        success = true,
                        shader = shaderName,
                        enabled = useShader
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

            // POST /api/shader/set - Set shader by name
            app.MapPost("/api/shader/set", async (HttpContext context) =>
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<SetShaderRequest>();
                    if (body == null || string.IsNullOrWhiteSpace(body.ShaderName))
                    {
                        return Results.BadRequest(new { success = false, error = "Shader name is required" });
                    }

                    if (_uiControl == null || _uiControl.IsDisposed)
                    {
                        return Results.BadRequest(new { success = false, error = "UI control not available" });
                    }

                    var useStoryCutsceneOverride = string.Equals(
                        body.OverrideReason,
                        "story-cutscene",
                        StringComparison.OrdinalIgnoreCase);
                    var useDeckOverride = string.Equals(
                        body.OverrideReason,
                        "deck-enforced",
                        StringComparison.OrdinalIgnoreCase);
                    var useOverride = useStoryCutsceneOverride || useDeckOverride;

                    bool success = false;
                    if (_uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke((MethodInvoker)delegate
                        {
                            if (useOverride)
                            {
                                if (_setShaderOverride != null)
                                {
                                    _setShaderOverride(body.ShaderName);
                                    success = true;
                                }
                                else
                                {
                                    success = Rendering.NesShaderControl.SwitchShader(body.ShaderName);
                                }
                            }
                            else if (_setShader != null)
                            {
                                _setShader(body.ShaderName);
                                success = true;
                            }
                            else
                            {
                                success = Rendering.NesShaderControl.SwitchShader(body.ShaderName);
                            }
                        });
                    }
                    else
                    {
                        if (useOverride)
                        {
                            if (_setShaderOverride != null)
                            {
                                _setShaderOverride(body.ShaderName);
                                success = true;
                            }
                            else
                            {
                                success = Rendering.NesShaderControl.SwitchShader(body.ShaderName);
                            }
                        }
                        else if (_setShader != null)
                        {
                            _setShader(body.ShaderName);
                            success = true;
                        }
                        else
                        {
                            success = Rendering.NesShaderControl.SwitchShader(body.ShaderName);
                        }
                    }

                    if (success)
                    {
                        return Results.Ok(new
                        {
                            success = true,
                            shader = body.ShaderName,
                            overrideApplied = useStoryCutsceneOverride
                        });
                    }
                    else
                    {
                        return Results.BadRequest(new
                        {
                            success = false,
                            error = "Failed to switch shader"
                        });
                    }
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

            // POST /api/shader/enable - Enable shaders
            app.MapPost("/api/shader/enable", () =>
            {
                try
                {
                    if (_uiControl == null || _uiControl.IsDisposed)
                    {
                        return Results.BadRequest(new { success = false, error = "UI control not available" });
                    }

                    if (_uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke((MethodInvoker)delegate
                        {
                            Rendering.NesShaderControl.EnableShaders();
                        });
                    }
                    else
                    {
                        Rendering.NesShaderControl.EnableShaders();
                    }

                    return Results.Ok(new { success = true, enabled = true });
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

            // POST /api/shader/disable - Shaders are always-on
            app.MapPost("/api/shader/disable", () =>
            {
                try
                {
                    return Results.Ok(new { success = true, enabled = true, message = "Shaders are always enabled." });
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
