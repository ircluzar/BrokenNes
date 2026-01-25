using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private void RegisterSaveEndpoints(WebApplication app)
        {
            // GET /api/save - Get game save data (owned cores, etc.)
            app.MapGet("/api/save", async () =>
            {
                try
                {
                    var savePath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "BrokenNes",
                        "gamesave.json"
                    );

                    if (!System.IO.File.Exists(savePath))
                    {
                        // Return default empty save
                        return Results.Ok(new
                        {
                            ownedCpuIds = new string[] { "FMC" },
                            ownedPpuIds = new string[] { "FMC" },
                            ownedApuIds = new string[] { "FMC" },
                            ownedClockIds = new string[0],
                            ownedShaderIds = new string[] { "PX" }
                        });
                    }

                    var json = await System.IO.File.ReadAllTextAsync(savePath);
                    var options = new System.Text.Json.JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    };
                    var save = System.Text.Json.JsonSerializer.Deserialize<GameSaveDto>(json, options);

                    return Results.Ok(save);
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
