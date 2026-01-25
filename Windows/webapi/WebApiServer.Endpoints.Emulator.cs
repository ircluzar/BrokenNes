using System;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private void RegisterEmulatorEndpoints(WebApplication app)
        {
            // POST /api/emulator/resume - Resume emulation (used by Story mode and other overlays)
            app.MapPost("/api/emulator/resume", () =>
            {
                if (_resumeEmulation == null)
                {
                    return Results.BadRequest(new { success = false, error = "Resume emulation not available" });
                }

                try
                {
                    // Invoke on UI thread if we have a UI control
                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke(_resumeEmulation);
                    }
                    else
                    {
                        _resumeEmulation();
                    }

                    Console.WriteLine("[WebApi] Emulation resumed via API");
                    return Results.Ok(new { success = true });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

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

                    bool success = await _loadBuiltInRom(body.Filename, body.PreserveShader);

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
    }
}
