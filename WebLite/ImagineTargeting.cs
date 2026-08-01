using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using NesEmulator;

namespace BrokenNes
{
    // WebLite-only: a simplified, touch-driven port of the Windows "ImagineBug" / "Target the
    // Beam" webmodule. Deliberately kept out of the shared Emulator.Imagine.cs (Windows/Web don't
    // need it) but reuses the shared NES.ImagineTargetConfig/PPU_IMG scanline-capture mechanism
    // and the existing ImaginePredictSpanAsync/ApplyImaginePatchAsync prediction pipeline that
    // ImagineBugAsync already uses for its untargeted "Imagine a bug" flow.
    public partial class Emulator
    {
        public bool ImagineTargetingActive { get; private set; }

        public async Task StartImagineTargetingAsync()
        {
            if (nes == null) { Status.Set("Imagine: no ROM loaded"); return; }
            ImagineTargetingActive = true;
            StateHasChanged();
            try
            {
                _selfRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("imagineTargetOverlay.start", "nes-canvas", _selfRef);
            }
            catch { }
        }

        public async Task CancelImagineTargetingAsync()
        {
            ImagineTargetingActive = false;
            StateHasChanged();
            try { await JS.InvokeVoidAsync("imagineTargetOverlay.stop"); } catch { }
        }

        [JSInvokable]
        public async Task OnImagineTargetSelected(double y0, double y1)
        {
            ImagineTargetingActive = false;
            StateHasChanged();
            if (nes == null) return;
            int startLine = (int)Math.Clamp(Math.Round(y0 * 239), 0, 239);
            int endLine = (int)Math.Clamp(Math.Round(y1 * 239), 0, 239);
            await ImagineTargetedBugAsync(startLine, endLine);
        }

        public async Task ImagineTargetedBugAsync(int rangeStart, int rangeEnd)
        {
            if (nes == null) return;
            if (!ImagineModelLoaded)
            {
                await ImagineLoadModelAsyncPublic();
                if (!ImagineModelLoaded) { Status.Set("Imagine: load model first"); return; }
            }
            try
            {
                await PauseEmulation();

                // Switch to the Imagine-hooked PPU core if it isn't already active, preserving
                // state across the swap exactly like the Windows set-targeted-mode endpoint does.
                var originalPpuId = nes.GetPpuCoreId();
                if (!originalPpuId.Contains("IMG", StringComparison.OrdinalIgnoreCase))
                {
                    var ppuState = nes.GetPpuState();
                    if (!nes.SetPpuCore("IMG"))
                    {
                        Status.Set("Imagine: PPU_IMG unavailable");
                        await StartEmulation();
                        return;
                    }
                    try { nes.SetPpuState(ppuState); } catch { }
                }

                int lo = Math.Min(rangeStart, rangeEnd);
                int hi = Math.Max(rangeStart, rangeEnd);
                nes.ImagineTargetConfig = new ImagineTargetConfig
                {
                    Mode = ImagineTargetMode.ScanlineRange,
                    RangeStart = lo,
                    RangeEnd = hi,
                    TargetScanline = lo,
                    Enabled = true
                };

                ImagineBusy = true; StateHasChanged();
                nes.RunFrame();
                var capture = nes.LastImagineCapture;
                nes.ImagineTargetConfig = null; // one-shot: clear targeting once captured

                if (capture == null)
                {
                    Status.Set($"Imagine: no instruction captured in scanlines {lo}-{hi}, try again");
                    return;
                }

                var pc = capture.PC;
                if (pc < 0x8000)
                {
                    Status.Set("Imagine: captured PC not in PRG ROM, try a different range");
                    return;
                }

                int len = Math.Clamp(ImagineBytesToGenerate, 1, 32);
                var tokens = BuildTokens128AroundPc(pc, len, out int holeStart, out int holeEnd);
                var bytes = await ImaginePredictSpanAsync(tokens, holeStart, holeEnd, ImagineTemperature, ImagineTopK);
                var applied = await ApplyImaginePatchAsync(pc, bytes);
                Status.Set(applied
                    ? $"Imagine: targeted bug applied at {pc:X4} (scanline {capture.Scanline})"
                    : "Imagine: could not apply patch (mapper?)");
            }
            catch (Exception ex)
            {
                ImagineLastError = ex.Message;
                Status.Set("Imagine: targeted flow failed");
            }
            finally
            {
                ImagineBusy = false; StateHasChanged();
                try { await StartEmulation(); } catch { }
            }
        }
    }
}
