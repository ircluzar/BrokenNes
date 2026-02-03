using System;
using System.Collections.Generic;
using System.Linq;
using BrokenNes;
using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes.Windows
{
    public sealed class ImagineEngine : ICorruptorEmulatorHooks
    {
        private readonly NES nes;
        private readonly Corruptor corruptor;
        private readonly Random rng = new Random();

        public int Epoch { get; set; } = 30;
        public bool ModelLoaded { get; private set; }
        public string EpLabel { get; private set; } = string.Empty;
        public string LastError { get; private set; } = string.Empty;
        public ImagineSnapshot? Snapshot { get; private set; }
        public byte[]? PredictedBytes { get; private set; }
        public int BytesToGenerate { get; set; } = 2; // 1..32
        public float Temperature { get; set; } = 0.4f; // 0.0..1.5
        public int? TopK { get; set; } = 1;

        public ImagineEngine(NES nes, Corruptor corruptor)
        {
            this.nes = nes;
            this.corruptor = corruptor;
            try { SetupTargetedImagine(); } catch { }
        }

        public void SetupTargetedImagine()
        {
            nes.ImagineTargetedShot = (pc, captureData) =>
            {
                ImagineTargetedBug(pc, captureData);
            };
        }

        public bool LoadModel(int epoch)
        {
            try
            {
                Epoch = epoch;
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", $"6502_span_predictor_epoch{epoch}.onnx");
                
                if (!File.Exists(modelPath))
                {
                    LastError = $"Model file not found: {modelPath}";
                    ModelLoaded = false;
                    EpLabel = string.Empty;
                    return false;
                }
                
                // TODO: Load actual ONNX model using Microsoft.ML.OnnxRuntime when integrated
                ModelLoaded = true;
                EpLabel = $"epoch{epoch}";
                LastError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ModelLoaded = false;
                EpLabel = string.Empty;
                return false;
            }
        }

        public ImagineSnapshot CaptureSnapshot()
        {
            var regs = nes.GetCpuRegs();
            ushort pc = regs.PC;
            var snap = new ImagineSnapshot
            {
                CpuCoreId = nes.GetCpuCoreId(),
                PC = pc,
                A = regs.A,
                X = regs.X,
                Y = regs.Y,
                P = regs.P,
                SP = regs.SP,
                IRQ = false,
                NMI = false,
                InPrgRom = pc >= 0x8000 && pc <= 0xFFFF
            };

            try
            {
                var state = nes.GetCpuState();
                if (state is NesEmulator.CpuSharedState s)
                {
                    snap.IRQ = s.irqRequested;
                    snap.NMI = s.nmiRequested;
                }
            }
            catch { }

            if (snap.InPrgRom)
            {
                int prevLen = Math.Min(8, (int)pc);
                snap.Prev8 = new byte[prevLen];
                for (int i = 0; i < prevLen; i++)
                {
                    snap.Prev8[i] = nes.PeekCpu((ushort)(pc - prevLen + i));
                }

                int look = Math.Min(16, 0x10000 - (int)pc);
                snap.Next16 = new byte[look];
                for (int i = 0; i < look; i++)
                {
                    snap.Next16[i] = nes.PeekCpu((ushort)(pc + i));
                }
            }

            Snapshot = snap;
            return snap;
        }

        public byte[] PredictFromSnapshot()
        {
            if (Snapshot == null) throw new InvalidOperationException("No Imagine snapshot captured.");
            return GeneratePatch(Snapshot.PC, BytesToGenerate);
        }

        public bool ApplyPatch(ushort pc, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            var writes = new List<BlastInstruction>(bytes.Length);

            for (int i = 0; i < bytes.Length; i++)
            {
                ushort addr = (ushort)(pc + i);
                if (!(nes.TryCpuToPrgIndex(addr, out int prgIdx) || TryCpuToPrgIndexByContent(addr, out prgIdx)))
                {
                    LastError = "Unable to map CPU address to PRG ROM.";
                    return false;
                }
                writes.Add(new BlastInstruction { Domain = "PRG", Address = prgIdx, Value = bytes[i] });
            }

            foreach (var w in writes)
            {
                nes.PokePrg(w.Address, (byte)w.Value);
            }

            try
            {
                corruptor.GhStash.Add(new HarvestEntry
                {
                    Name = $"Imagine PC={pc:X4} L={bytes.Length} E{Epoch}",
                    Writes = writes,
                    Created = DateTime.UtcNow
                });
            }
            catch { }

            PredictedBytes = bytes.ToArray();
            LastError = string.Empty;
            return true;
        }

        public bool ImagineBug()
        {
            if (!ModelLoaded)
            {
                LastError = "Model not loaded";
                return false;
            }

            var snap = CaptureSnapshot();
            if (!snap.InPrgRom)
            {
                LastError = "PC not in PRG ROM";
                return false;
            }

            var bytes = GeneratePatch(snap.PC, BytesToGenerate);
            return ApplyPatch(snap.PC, bytes);
        }

        /// <summary>
        /// Targeted Imagine Bug: Apply corruption at specific PC captured during scanline.
        /// </summary>
        public bool ImagineTargetedBug(ushort pc, ImagineCaptureData captureData)
        {
            if (!ModelLoaded)
            {
                LastError = "Model not loaded";
                return false;
            }

            if (pc < 0x8000 || pc > 0xFFFF)
            {
                LastError = $"PC ${pc:X4} not in PRG ROM range";
                return false;
            }

            try
            {
                var bytes = GeneratePatch(pc, BytesToGenerate);
                bool applied = ApplyPatch(pc, bytes);

                if (applied)
                {
                    try
                    {
                        var lastEntry = corruptor.GhStash.LastOrDefault();
                        if (lastEntry != null)
                        {
                            lastEntry.Name = $"IMG SL{captureData.Scanline} PC=${pc:X4} {captureData.FramePhase} L={bytes.Length} E{Epoch}";
                        }
                    }
                    catch { }
                }

                return applied;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public void ImagineFromPc(ushort pc, int bytesToGenerate)
        {
            if (!ModelLoaded) return;
            int len = Math.Clamp(bytesToGenerate, 1, 32);
            try
            {
                var bytes = GeneratePatch(pc, len);
                ApplyPatch(pc, bytes);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private byte[] GeneratePatch(ushort pc, int length)
        {
            int len = Math.Clamp(length, 1, 32);
            var bytes = new byte[len];
            double mutate = Math.Clamp(Temperature / 1.5, 0.05, 0.95);

            for (int i = 0; i < len; i++)
            {
                ushort addr = (ushort)(pc + i);
                byte orig = nes.PeekCpu(addr);
                double roll = rng.NextDouble();

                if (roll < mutate)
                {
                    bytes[i] = (byte)(orig ^ (1 << rng.Next(8)));
                }
                else if (roll < mutate + 0.2)
                {
                    bytes[i] = 0xEA; // NOP bias
                }
                else
                {
                    bytes[i] = (byte)rng.Next(256);
                }
            }

            return bytes;
        }

        private bool TryCpuToPrgIndexByContent(ushort addr, out int prgIndex)
        {
            prgIndex = -1;
            if (addr < 0x8000 || addr > 0xFFFF) return false;

            int prgSize = nes.GetPrgRomSize();
            if (prgSize <= 0) return false;

            int simpleIdx = addr - 0x8000;
            if (simpleIdx >= 0 && simpleIdx < prgSize)
            {
                byte cpuB = nes.PeekCpu(addr);
                byte prgB = nes.PeekPrg(simpleIdx);
                if (cpuB == prgB)
                {
                    int look = Math.Min(16, Math.Min(0x10000 - addr, prgSize - simpleIdx));
                    int ok = 0;
                    for (int i = 1; i < look; i++)
                    {
                        if (nes.PeekCpu((ushort)(addr + i)) != nes.PeekPrg(simpleIdx + i)) break;
                        ok++;
                    }
                    if (ok >= 3)
                    {
                        prgIndex = simpleIdx;
                        return true;
                    }
                }
            }

            byte anchor = nes.PeekCpu(addr);
            var candidates = new List<int>(16);
            for (int i = 0; i < prgSize; i++)
            {
                if (nes.PeekPrg(i) == anchor) candidates.Add(i);
            }
            if (candidates.Count == 0) return false;

            int bestIdx = -1; int bestScore = -1;
            int cap = Math.Min(32, 0x10000 - addr);
            foreach (var c in candidates)
            {
                int maxForward = Math.Min(cap, prgSize - c);
                if (maxForward <= 0) continue;
                int score = 0;
                for (int k = 1; k < maxForward; k++)
                {
                    if (nes.PeekCpu((ushort)(addr + k)) != nes.PeekPrg(c + k)) break;
                    score++;
                }
                if (score > bestScore)
                {
                    bestScore = score; bestIdx = c;
                    if (bestScore >= 16) break;
                }
            }

            if (bestIdx >= 0)
            {
                prgIndex = bestIdx;
                return true;
            }

            return false;
        }
    }

    public sealed class ImagineSnapshot
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
}
