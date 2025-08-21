using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace BrokenNes
{
    public partial class Emulator
    {
        private async Task ResetAsync()
        {
            try
            {
                Logger.LogInformation("Resetting emulation (Emulator.ResetAsync)");
                bool wasRunning = nesController.IsRunning;
                if (wasRunning) await PauseEmulation();
                try { nes?.FlushSoundFont(); } catch {}
                try { await JS.InvokeVoidAsync("nesInterop.flushSoundFont"); } catch {}
                if (corruptor.AutoCorrupt)
                {
                    corruptor.AutoCorrupt = false;
                    corruptor.LastBlastInfo = "Auto-corrupt disabled (reset)";
                }
                if (!string.IsNullOrEmpty(nesController.CurrentRomName))
                {
                    nesController.RomFileName = nesController.CurrentRomName;
                    if (nesController.UploadedRoms.ContainsKey(nesController.CurrentRomName))
                    {
                        var data = nesController.UploadedRoms[nesController.CurrentRomName];
                        if (nes == null) nes = new NesEmulator.NES();
                        nes.RomName = nesController.CurrentRomName;
                        var prevApuSuffix = nesController.ApuCoreSel;
                        nes.LoadROM(data);
                        if (!string.IsNullOrEmpty(prevApuSuffix)) { try { nes.SetApuCore(prevApuSuffix); } catch {} }
                        ApplySelectedCores();
                        try {
                            if (nesController.ApuCoreSel == "FMC") nes.SetApuCore(NesEmulator.NES.ApuCore.Jank);
                            else if (nesController.ApuCoreSel == "FIX") nes.SetApuCore(NesEmulator.NES.ApuCore.Modern);
                            else if (nesController.ApuCoreSel == "QN") nes.SetApuCore(NesEmulator.NES.ApuCore.QuickNes);
                        } catch {}
                        BuildMemoryDomains();
                    }
                    else
                    {
                        await LoadRomFromServer();
                        SetApuCoreSelFromEmu();
                    }
                }
                nesController.FrameCount = 0;
                nesController.LastFrameCount = 0;
                nesController.Fps = 0;
                try { await JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch {}
                if (wasRunning) await StartEmulation();
                Status.Set("Emulation reset (clean ROM reloaded)");
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to reset emulation");
                nesController.ErrorMessage = $"Failed to reset: {ex.Message}\n{ex.StackTrace}";
            }
        }
        public Task ResetAsyncPublic() => ResetAsync();
    }
}
