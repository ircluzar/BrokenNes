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

            // POST /api/apu/channels/enable - Set enabled APU channels
            app.MapPost("/api/apu/channels/enable", async (HttpContext context) =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var body = await context.Request.ReadFromJsonAsync<ApuChannelEnableRequest>();
                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Channel mask is required" });
                    }

                    // Channel mask: bit 0=Pulse1, bit 1=Pulse2, bit 2=Triangle, bit 3=Noise, bit 4=DMC
                    var channelMask = body.ChannelMask ?? 0x1F; // Default: all channels enabled
                    nes.SetApuChannelEnableMask(channelMask);

                    return Results.Ok(new
                    {
                        success = true,
                        channelMask = channelMask,
                        channels = new
                        {
                            pulse1 = (channelMask & 0x01) != 0,
                            pulse2 = (channelMask & 0x02) != 0,
                            triangle = (channelMask & 0x04) != 0,
                            noise = (channelMask & 0x08) != 0,
                            dmc = (channelMask & 0x10) != 0
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }

        private class ApuChannelEnableRequest
        {
            public int? ChannelMask { get; set; }
        }
    }
}
