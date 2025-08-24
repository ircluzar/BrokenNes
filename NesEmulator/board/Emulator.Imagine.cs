using System;
using System.Threading.Tasks;

namespace BrokenNes
{
    public partial class Emulator
    {
    // =============== Imagine: UI + JSInterop state ===============
    public int ImagineEpoch { get; set; } = 25;
    public bool ImagineModelLoaded { get; private set; }
    public string ImagineEpLabel { get; private set; } = string.Empty; // wasm|webgl
    public string ImagineLastError { get; private set; } = string.Empty;

        public sealed class ImagineDebugSnapshot
        {
            public string CpuCoreId { get; set; } = string.Empty;
            public ushort PC { get; set; }
            public byte A { get; set; }
            public byte X { get; set; }
            public byte Y { get; set; }
            public byte P { get; set; }
            public ushort SP { get; set; }
            public bool IRQ { get; set; }
            public bool NMI { get; set; }
            public bool InPrgRom { get; set; }
            public byte[] Prev8 { get; set; } = Array.Empty<byte>();
            public byte[] Next16 { get; set; } = Array.Empty<byte>();
        }

        public bool ImagineModalOpen { get; private set; }
        public ImagineDebugSnapshot? ImagineSnapshot { get; private set; }
    public int ImagineBytesToGenerate { get; set; } = 4; // 1..32
    public float ImagineTemperature { get; set; } = 0.4f; // 0.2..0.7 typical
    public int? ImagineTopK { get; set; } = 32; // null or 20..50 typical
    public byte[]? ImaginePredictedBytes { get; private set; }
    public bool ImagineBusy { get; private set; }

        public void OpenImagineModal()
        {
            ImagineModalOpen = true;
            StateHasChanged();
        }

        public void CloseImagineModal()
        {
            ImagineModalOpen = false;
            StateHasChanged();
        }

        private sealed class JsLoadModelResult
        {
            public bool ok { get; set; }
            public string? info { get; set; }
            public string? error { get; set; }
        }

        public async Task ImagineLoadModelAsyncPublic()
        {
            try
            {
                ImagineLastError = string.Empty;
                Status.Set($"Imagine: loading epoch {ImagineEpoch}...");
                var res = await JS.InvokeAsync<JsLoadModelResult>("imagine.loadModel", new object?[] { ImagineEpoch });
                if (res != null && res.ok)
                {
                    ImagineModelLoaded = true;
                    ImagineEpLabel = res.info ?? string.Empty;
                    Status.Set($"Imagine: loaded epoch {ImagineEpoch} (EP: {ImagineEpLabel})");
                }
                else
                {
                    ImagineModelLoaded = false;
                    ImagineEpLabel = string.Empty;
                    ImagineLastError = res?.error ?? "Unknown error";
                    Status.Set($"Imagine: failed to load epoch {ImagineEpoch}");
                }
            }
            catch (Exception ex)
            {
                ImagineModelLoaded = false;
                ImagineEpLabel = string.Empty;
                ImagineLastError = ex.Message;
            }
            finally
            {
                StateHasChanged();
            }
        }

        public async Task FreezeAndFetchNextInstructionAsync()
        {
            try
            {
                // Pause emulation to freeze state
                await PauseEmulation();
            }
            catch { }

            ushort pc = 0; byte a=0,x=0,y=0,p=0; ushort sp=0; bool irq=false, nmi=false; string coreId = string.Empty;
            try
            {
                coreId = nes?.GetCpuCoreId() ?? string.Empty;
            }
            catch { coreId = string.Empty; }

            try
            {
                var regs = nes?.GetCpuRegs() ?? (PC: (ushort)0, A:(byte)0, X:(byte)0, Y:(byte)0, P:(byte)0, SP:(ushort)0);
                pc = regs.PC; a = regs.A; x = regs.X; y = regs.Y; p = regs.P; sp = regs.SP;
            }
            catch { }

            try
            {
                // Try to capture irq/nmi requested flags via shared state if available
                var obj = nes?.GetCpuState();
                if (obj is NesEmulator.CpuSharedState s)
                {
                    irq = s.irqRequested; nmi = s.nmiRequested;
                }
            }
            catch { }

            bool inPrg = pc >= 0x8000 && pc <= 0xFFFF;
            var prev = new byte[8];
            var next = new byte[16];
            try
            {
                if (inPrg && nes != null)
                {
                    // Previous 8 bytes: PC-8 .. PC-1
                    for (int i = 0; i < 8; i++)
                    {
                        ushort addr = (ushort)(pc - (8 - i));
                        prev[i] = nes.PeekCpu(addr);
                    }
                    // Next 16 bytes: PC .. PC+15
                    for (int i = 0; i < 16; i++)
                    {
                        ushort addr = (ushort)(pc + i);
                        next[i] = nes.PeekCpu(addr);
                    }
                }
                else
                {
                    prev = Array.Empty<byte>();
                    next = Array.Empty<byte>();
                }
            }
            catch { prev = Array.Empty<byte>(); next = Array.Empty<byte>(); }

            ImagineSnapshot = new ImagineDebugSnapshot
            {
                CpuCoreId = coreId,
                PC = pc,
                A = a,
                X = x,
                Y = y,
                P = p,
                SP = sp,
                IRQ = irq,
                NMI = nmi,
                InPrgRom = inPrg,
                Prev8 = prev,
                Next16 = next
            };

            StateHasChanged();
        }

        // -------- Milestone 4 actions --------
        public async Task ImagineRunPredictionTestAsync()
        {
            if (!ImagineModelLoaded) { Status.Set("Imagine: model not loaded"); return; }
            if (ImagineSnapshot == null || !ImagineSnapshot.InPrgRom) { Status.Set("Imagine: invalid PC (not in PRG ROM)"); return; }
            var pc = ImagineSnapshot.PC;
            int L = Math.Clamp(ImagineBytesToGenerate, 1, 32);
            try
            {
                ImagineBusy = true; StateHasChanged();
                var tokens = BuildTokens128AroundPc(pc, L, out int hs, out int he);
                var bytes = await ImaginePredictSpanAsync(tokens, hs, he, ImagineTemperature, ImagineTopK);
                ImaginePredictedBytes = bytes;
                Status.Set($"Imagine: predicted {bytes.Length} byte(s) at PC={pc:X4}");
            }
            catch (Exception ex)
            {
                ImagineLastError = ex.Message; Status.Set("Imagine: prediction failed");
            }
            finally { ImagineBusy = false; StateHasChanged(); }
        }

        public async Task ImagineApplyPatchHereAsync()
        {
            if (ImagineSnapshot == null || ImaginePredictedBytes == null || ImaginePredictedBytes.Length == 0) return;
            try
            {
                var ok = await ApplyImaginePatchAsync(ImagineSnapshot.PC, ImaginePredictedBytes);
                if (ok) Status.Set($"Imagine: applied patch at {ImagineSnapshot.PC:X4} ({ImaginePredictedBytes.Length} byte(s))");
                else Status.Set("Imagine: patch not applied (mapper?)");
            }
            catch { Status.Set("Imagine: patch failed"); }
        }

        public async Task ImagineBugAsync()
        {
            if (!ImagineModelLoaded) { Status.Set("Imagine: load model first"); return; }
            try
            {
                await FreezeAndFetchNextInstructionAsync();
                if (ImagineSnapshot == null || !ImagineSnapshot.InPrgRom)
                {
                    Status.Set("Imagine: PC not in PRG ROM");
                    return;
                }
                int L = Math.Clamp(ImagineBytesToGenerate, 1, 32);
                var pc = ImagineSnapshot.PC;
                ImagineBusy = true; StateHasChanged();
                var tokens = BuildTokens128AroundPc(pc, L, out int hs, out int he);
                var bytes = await ImaginePredictSpanAsync(tokens, hs, he, ImagineTemperature, ImagineTopK);
                var ok = await ApplyImaginePatchAsync(pc, bytes);
                if (ok) { Status.Set($"Imagine: bug applied at {pc:X4} (L={bytes.Length})"); }
                else { Status.Set("Imagine: could not apply patch (mapper?)"); }
            }
            catch (Exception ex)
            {
                ImagineLastError = ex.Message; Status.Set("Imagine: failure in flow");
            }
            finally
            {
                ImagineBusy = false; StateHasChanged();
                // Resume regardless to avoid stuck pause
                try { await StartEmulation(); } catch { }
            }
        }
    }
}
