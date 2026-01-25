using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register CPU State Access API endpoints
        /// </summary>
        private void RegisterCpuStateEndpoints(WebApplication app)
        {
            // GET /api/cpu/registers - Get CPU registers
            app.MapGet("/api/cpu/registers", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var regs = nes.GetCpuRegisters();
                    return Results.Ok(new
                    {
                        success = true,
                        registers = new
                        {
                            PC = $"0x{regs.PC:X4}",
                            A = $"0x{regs.A:X2}",
                            X = $"0x{regs.X:X2}",
                            Y = $"0x{regs.Y:X2}",
                            P = $"0x{regs.P:X2}",
                            SP = $"0x{regs.SP:X4}"
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/cpu/core - Get CPU core ID
            app.MapGet("/api/cpu/core", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var coreId = nes.GetCpuCoreIdentifier();
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

            // GET /api/cpu/cores - Get available CPU cores
            app.MapGet("/api/cpu/cores", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var cores = nes.GetAvailableCpuCores();
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

            // GET /api/cpu/state - Get full CPU state snapshot
            app.MapGet("/api/cpu/state", () =>
            {
                var nes = _getNes();
                if (nes == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
                }

                try
                {
                    var state = nes.GetCpuStateSnapshot();
                    return Results.Ok(new
                    {
                        success = true,
                        state = state
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
