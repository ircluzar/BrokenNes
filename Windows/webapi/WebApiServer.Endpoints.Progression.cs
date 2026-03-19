using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private void RegisterProgressionEndpoints(WebApplication app)
        {
            app.MapGet("/api/progression", async () =>
            {
                var save = await _progressionSave.LoadAsync();
                return Results.Ok(new
                {
                    success = true,
                    unlockedWebmodules = save.UnlockedWebmodules,
                    unlockedBackgrounds = save.UnlockedBackgrounds,
                    unlockedNullProviders = save.UnlockedNullProviders,
                    preferredBackgroundId = save.PreferredBackgroundId,
                    preferredNullProviderId = save.PreferredNullProviderId,
                    pendingUnlocks = save.PendingUnlocks,
                    legacyFeatures = new
                    {
                        savestatesUnlocked = save.SavestatesUnlocked,
                        rtcUnlocked = save.RtcUnlocked,
                        ghUnlocked = save.GhUnlocked,
                        imagineUnlocked = save.ImagineUnlocked,
                        debugUnlocked = save.DebugUnlocked
                    }
                });
            });

            app.MapGet("/api/progression/roster", async () =>
            {
                var save = await _progressionSave.LoadAsync();
                var webmodules = WebModuleManager.DiscoverModules()
                    .Select(module => new
                    {
                        id = module.FolderName,
                        title = module.Name,
                        description = module.Config.Description,
                        displayMode = module.DisplayMode.ToString(),
                        unlocked = _progressionSave.IsWebmoduleUnlocked(save, module.FolderName)
                    })
                    .OrderBy(module => module.id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var backgrounds = (_getAvailableBackgrounds?.Invoke() ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name) && name != "---")
                    .Select(name => new
                    {
                        id = name,
                        unlocked = _progressionSave.IsBackgroundUnlocked(save, name),
                        equipped = string.Equals(save.PreferredBackgroundId, name, StringComparison.OrdinalIgnoreCase)
                    })
                    .OrderBy(entry => entry.id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var nullProviders = (_getAvailableNullProviders?.Invoke() ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => new
                    {
                        id = name,
                        unlocked = _progressionSave.IsNullProviderUnlocked(save, name),
                        equipped = string.Equals(save.PreferredNullProviderId, name, StringComparison.OrdinalIgnoreCase)
                    })
                    .OrderBy(entry => entry.id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return Results.Ok(new
                {
                    success = true,
                    webmodules,
                    backgrounds,
                    nullProviders
                });
            });

            app.MapPost("/api/progression/claim-pending", async () =>
            {
                var bundles = await _progressionSave.ClaimPendingAsync();
                return Results.Ok(new { success = true, pendingUnlocks = bundles });
            });

            app.MapPost("/api/progression/acknowledge", async (HttpContext context) =>
            {
                var body = await context.Request.ReadFromJsonAsync<ProgressionAcknowledgeRequest>();
                if (body == null || body.RewardIds.Length == 0)
                {
                    return Results.BadRequest(new { success = false, error = "At least one reward id is required" });
                }

                var updatedCount = await _progressionSave.AcknowledgePendingAsync(body.RewardIds);
                RefreshProgressionUi();
                return Results.Ok(new { success = true, updatedCount });
            });

            app.MapPost("/api/progression/equip-background", async (HttpContext context) =>
            {
                if (_setBackground == null)
                {
                    return Results.BadRequest(new { success = false, error = "Background setting not available" });
                }

                var body = await context.Request.ReadFromJsonAsync<SetBackgroundRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Name))
                {
                    return Results.BadRequest(new { success = false, error = "Name is required" });
                }

                var equipped = await _progressionSave.SetPreferredBackgroundAsync(body.Name);
                if (!equipped)
                {
                    return Results.BadRequest(new { success = false, error = "Background is locked" });
                }

                if (_uiControl != null && _uiControl.InvokeRequired)
                {
                    _uiControl.Invoke(() => _setBackground(body.Name));
                }
                else
                {
                    _setBackground(body.Name);
                }

                return Results.Ok(new { success = true, name = body.Name });
            });

            app.MapPost("/api/progression/equip-null-provider", async (HttpContext context) =>
            {
                if (_setNullProvider == null)
                {
                    return Results.BadRequest(new { success = false, error = "Null provider setting not available" });
                }

                var body = await context.Request.ReadFromJsonAsync<SetNullProviderRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Name))
                {
                    return Results.BadRequest(new { success = false, error = "Name is required" });
                }

                var equipped = await _progressionSave.SetPreferredNullProviderAsync(body.Name);
                if (!equipped)
                {
                    return Results.BadRequest(new { success = false, error = "Null provider is locked" });
                }

                if (_uiControl != null && _uiControl.InvokeRequired)
                {
                    _uiControl.Invoke(() => _setNullProvider(body.Name));
                }
                else
                {
                    _setNullProvider(body.Name);
                }

                return Results.Ok(new { success = true, name = body.Name });
            });
        }
    }
}