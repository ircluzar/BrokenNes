using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private async Task ApplyProgressionGateAsync(HttpContext context, RequestDelegate next)
        {
            var lockMessage = await GetProgressionLockMessageAsync(context.Request.Path);
            if (lockMessage != null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { success = false, error = lockMessage });
                return;
            }

            await next(context);
        }

        private async Task<string?> GetProgressionLockMessageAsync(PathString path)
        {
            if (path.StartsWithSegments("/api/rtc", System.StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/api/gh", System.StringComparison.OrdinalIgnoreCase))
            {
                return await IsWebmoduleUnlockedAsync("GlitchHarvester")
                    ? null
                    : "RTC + Glitch Harvester is locked";
            }

            if (path.StartsWithSegments("/api/timejump", System.StringComparison.OrdinalIgnoreCase))
            {
                return await IsWebmoduleUnlockedAsync("TimeJump")
                    ? null
                    : "TimeJump is locked";
            }

            if (path.StartsWithSegments("/api/imagine", System.StringComparison.OrdinalIgnoreCase))
            {
                return await IsWebmoduleUnlockedAsync("ImagineBug")
                    ? null
                    : "ImagineBug is locked";
            }

            return null;
        }

        private async Task<bool> IsWebmoduleUnlockedAsync(string moduleId)
        {
            var save = await _progressionSave.LoadAsync();
            return _progressionSave.IsWebmoduleUnlocked(save, moduleId);
        }

        private async Task<bool> IsImagineUnlockedAsync()
        {
            return await IsWebmoduleUnlockedAsync("ImagineBug");
        }
    }
}