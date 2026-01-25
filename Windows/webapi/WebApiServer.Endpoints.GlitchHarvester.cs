using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Glitch Harvester (GH) API endpoints
        /// </summary>
        private void RegisterGlitchHarvesterEndpoints(WebApplication app)
        {
            // GET /api/gh/base-states - Get all base states
            app.MapGet("/api/gh/base-states", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var baseStates = gh.GetAllBaseStates();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        selectedId = gh.SelectedBaseId,
                        baseStates = baseStates.Select(b => new
                        {
                            b.Id,
                            b.Name,
                            b.Created
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/base-state - Add a new base state
            app.MapPost("/api/gh/base-state", async (HttpContext context) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<AddBaseStateRequest>();
                    var name = form?.Name;
                    
                    var gh = corruptor.GlitchHarvester;
                    var baseState = gh.AddBaseState(nes, name);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        baseState = new
                        {
                            baseState.Id,
                            baseState.Name,
                            baseState.Created
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/base-state/{id} - Get selected base state ID
            app.MapGet("/api/gh/selected-base", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                var gh = corruptor.GlitchHarvester;
                return Results.Ok(new
                {
                    success = true,
                    selectedId = gh.SelectedBaseId
                });
            });

            // POST /api/gh/select-base - Set selected base state
            app.MapPost("/api/gh/select-base", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<SelectBaseRequest>();
                    if (form == null || string.IsNullOrEmpty(form.Id))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.SelectBaseState(form.Id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        selectedId = gh.SelectedBaseId
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/load-base - Load base state (by ID or currently selected)
            app.MapPost("/api/gh/load-base", async (HttpContext context) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    
                    // Try to read request body for optional ID
                    var form = await context.Request.ReadFromJsonAsync<LoadBaseRequest>();
                    
                    if (form != null && !string.IsNullOrEmpty(form.Id))
                    {
                        // Load specific base state by ID
                        gh.LoadBaseState(nes, form.Id);
                    }
                    else
                    {
                        // Load currently selected base state (backward compatibility)
                        gh.LoadSelectedBase(nes);
                    }
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Base state loaded"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/base-state/{id} - Delete a base state
            app.MapDelete("/api/gh/base-state/{id}", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.DeleteBaseState(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Base state deleted"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/load-on-operation - Get load on operation setting
            app.MapGet("/api/gh/load-on-operation", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                var gh = corruptor.GlitchHarvester;
                return Results.Ok(new
                {
                    success = true,
                    loadOnOperation = gh.LoadOnOperation
                });
            });

            // POST /api/gh/load-on-operation - Set load on operation setting
            app.MapPost("/api/gh/load-on-operation", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<LoadOnOperationRequest>();
                    if (form == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.LoadOnOperation = form.Enabled;
                    
                    return Results.Ok(new
                    {
                        success = true,
                        loadOnOperation = gh.LoadOnOperation
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/corrupt-and-stash - Corrupt and add to stash
            app.MapPost("/api/gh/corrupt-and-stash", (HttpContext context) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    
                    // Try to read the request body for optional base state ID
                    CorruptAndStashRequest? form = null;
                    try
                    {
                        form = context.Request.ReadFromJsonAsync<CorruptAndStashRequest>().Result;
                    }
                    catch { /* Ignore parse errors */ }
                    
                    // Use the provided base state ID if available, otherwise use the selected one
                    var entry = (form?.Id != null) 
                        ? gh.CorruptAndStash(nes, form.Id)
                        : gh.CorruptAndStash(nes);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        entry = new
                        {
                            entry.Id,
                            entry.Name,
                            entry.BaseStateId,
                            entry.Created,
                            writeCount = entry.Writes.Count
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/stash - Get all stash entries
            app.MapGet("/api/gh/stash", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var stash = gh.GetStash();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        stash = stash.Select(e => new
                        {
                            e.Id,
                            e.Name,
                            e.BaseStateId,
                            e.Created,
                            writeCount = e.Writes.Count
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stash/{id}/replay - Replay a stash entry
            app.MapPost("/api/gh/stash/{id}/replay", (string id) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.ReplayStashEntry(nes, id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stash entry replayed"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stash/{id}/promote - Promote stash entry to stockpile
            app.MapPost("/api/gh/stash/{id}/promote", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var entry = gh.PromoteToStockpile(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        entry = new
                        {
                            entry.Id,
                            entry.Name,
                            entry.Created
                        }
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/stash/{id} - Delete a stash entry
            app.MapDelete("/api/gh/stash/{id}", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.DeleteStashEntry(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stash entry deleted"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/stash - Clear all stash entries
            app.MapDelete("/api/gh/stash", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.ClearStash();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stash cleared"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/stockpile - Get all stockpile entries
            app.MapGet("/api/gh/stockpile", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var stockpile = gh.GetStockpile();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        stockpile = stockpile.Select(e => new
                        {
                            e.Id,
                            e.Name,
                            e.BaseStateId,
                            e.Created,
                            writeCount = e.Writes.Count
                        })
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stockpile/{id}/replay - Replay a stockpile entry
            app.MapPost("/api/gh/stockpile/{id}/replay", (string id) =>
            {
                var nes = _getNes();
                var corruptor = _getCorruptor();
                if (nes == null || corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Emulator or corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.ReplayStockpileEntry(nes, id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile entry replayed"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // PUT /api/gh/stockpile/{id}/rename - Rename a stockpile entry
            app.MapPut("/api/gh/stockpile/{id}/rename", async (string id, HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<RenameRequest>();
                    if (form == null || string.IsNullOrWhiteSpace(form.Name))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.RenameStockpileEntry(id, form.Name);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile entry renamed"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // DELETE /api/gh/stockpile/{id} - Delete a stockpile entry
            app.MapDelete("/api/gh/stockpile/{id}", (string id) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    gh.DeleteStockpileEntry(id);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile entry deleted"
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/gh/stockpile/export - Export stockpile as JSON
            app.MapGet("/api/gh/stockpile/export", () =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var gh = corruptor.GlitchHarvester;
                    var json = gh.ExportStockpile();
                    
                    return Results.Ok(new
                    {
                        success = true,
                        json = json
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/gh/stockpile/import - Import stockpile from JSON
            app.MapPost("/api/gh/stockpile/import", async (HttpContext context) =>
            {
                var corruptor = _getCorruptor();
                if (corruptor == null)
                {
                    return Results.BadRequest(new { success = false, error = "Corruptor not initialized" });
                }

                try
                {
                    var form = await context.Request.ReadFromJsonAsync<ImportRequest>();
                    if (form == null || string.IsNullOrWhiteSpace(form.Json))
                    {
                        return Results.BadRequest(new { success = false, error = "Invalid request" });
                    }

                    var gh = corruptor.GlitchHarvester;
                    gh.ImportStockpile(form.Json);
                    
                    return Results.Ok(new
                    {
                        success = true,
                        message = "Stockpile imported"
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
