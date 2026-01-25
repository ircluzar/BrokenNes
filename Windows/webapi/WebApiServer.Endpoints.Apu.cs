using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register APU State Access API endpoints
        /// </summary>
        private void RegisterApuStateEndpoints(WebApplication app)
        {
            // GET /api/apu/core - Get APU core ID
            app.MapGet("/api/apu/core", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var coreId = nes.GetApuCoreIdentifier();
                    return Results.Ok(new
                    {
                        success = true,
                        coreId = coreId
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/apu/cores - Get available APU cores
            app.MapGet("/api/apu/cores", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var cores = nes.GetAvailableApuCores();
                    return Results.Ok(new
                    {
                        success = true,
                        cores = cores
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
