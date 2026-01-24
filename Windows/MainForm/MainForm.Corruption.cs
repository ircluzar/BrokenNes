using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrokenNes.CorruptorModels;
using NesEmulator;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        internal CorruptorSnapshot? GetCorruptorSnapshot()
        {
            // Use TryEnter with timeout to avoid blocking the UI thread
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(corruptorLock, 100, ref lockTaken);
                if (!lockTaken)
                {
                    // Couldn't get lock quickly, return null to avoid blocking
                    return null;
                }
                
                return new CorruptorSnapshot
                {
                    CorruptIntensity = corruptor.CorruptIntensity,
                    BlastType = corruptor.BlastType,
                    MemoryDomains = corruptor.MemoryDomains.ToList(),
                    AutoCorrupt = corruptor.AutoCorrupt,
                    LastBlastInfo = corruptor.LastBlastInfo,
                    StubbornMode = corruptor.StubbornMode,
                    CrashBehavior = corruptor.CrashBehavior,
                    GhBaseStates = corruptor.GhBaseStates.ToList(),
                    GhStash = corruptor.GhStash.ToList(),
                    GhStockpile = corruptor.GhStockpile.ToList(),
                    GhSelectedBaseId = corruptor.GhSelectedBaseId,
                    GhLoadOnOperation = corruptor.GhLoadOnOperation
                };
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(corruptorLock);
            }
        }

        internal void SetCorruptIntensity(int value)
        {
            lock (corruptorLock) { corruptor.OnIntensityChange(value); }
            NotifyCorruptorChanged();
        }

        internal void SetBlastType(string blastType)
        {
            lock (corruptorLock) { corruptor.OnBlastTypeChanged(blastType); }
            NotifyCorruptorChanged();
        }

        internal void SetSelectedDomains(IEnumerable<string> keys)
        {
            lock (corruptorLock) { corruptor.DomainsChanged(keys); }
            NotifyCorruptorChanged();
        }

        internal void SetAutoCorrupt(bool enabled)
        {
            lock (corruptorLock)
            {
                corruptor.AutoCorrupt = enabled;
                corruptor.LastBlastInfo = enabled ? "Auto-corrupt enabled" : "Auto-corrupt disabled";
            }
            NotifyCorruptorChanged();
        }

        internal void RequestBlast()
        {
            if (!IsEmulatorReady) return;
            QueueEmuAction(() =>
            {
                if (nes == null) return;
                lock (corruptorLock)
                {
                    corruptor.Blast(nes);
                }
                NotifyCorruptorChanged();
            });
        }

        internal void RequestLetItRip()
        {
            lock (corruptorLock) { corruptor.LetItRip(); }
            RefreshMemoryDomainsRequested();
            NotifyCorruptorChanged();
        }

        private void BuildMemoryDomains()
        {
            if (nes == null) return;
            lock (corruptorLock)
            {
                corruptor.MemoryDomains.Clear();
                try
                {
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "PRG", Label = "PRG ROM", Size = GetApproxSize(i => nes.PeekPrg(i)), Selected = false });
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "PRGRAM", Label = "PRG RAM", Size = GetApproxSize(i => nes.PeekPrgRam(i)), Selected = false });
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "CHR", Label = "CHR", Size = GetApproxSize(i => nes.PeekChr(i)), Selected = false });
                    corruptor.MemoryDomains.Add(new DomainSel { Key = "RAM", Label = "System RAM", Size = 2048, Selected = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"BuildMemoryDomains error: {ex.Message}");
                }
            }
            NotifyCorruptorChanged();
        }

        private int GetApproxSize(Func<int, byte> peek)
        {
            int size = 1024;
            int lastNonZero = 0;
            for (int i = 0; i < size; i += 128)
            {
                if (peek(i) != 0) lastNonZero = i;
            }
            for (int i = 1024; i <= 512 * 1024; i *= 2)
            {
                byte v = peek(i - 1);
                if (v != 0) lastNonZero = i - 1;
                else { size = i; break; }
            }
            return Math.Max((lastNonZero + 256) & ~255, 0);
        }

        internal void RefreshMemoryDomainsRequested()
        {
            if (!IsEmulatorReady) return;
            QueueEmuAction(BuildMemoryDomains);
        }

        internal int GetMemoryDomainSize(string domainKey)
        {
            lock (corruptorLock)
            {
                var domain = corruptor.MemoryDomains.FirstOrDefault(d => string.Equals(d.Key, domainKey, StringComparison.OrdinalIgnoreCase));
                return domain?.Size ?? 0;
            }
        }

        internal Task<byte[]> ReadMemoryAsync(string domainKey, int start, int length)
        {
            if (nes == null || length <= 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            start = Math.Max(0, start);
            int domainSize = GetMemoryDomainSize(domainKey);
            if (domainSize > 0)
            {
                length = Math.Min(length, Math.Max(0, domainSize - start));
            }

            if (length <= 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            return RunOnEmulationThreadAsync(() =>
            {
                var buffer = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    buffer[i] = PeekDomainByte(domainKey, start + i);
                }
                return buffer;
            });
        }

        internal Task WriteMemoryAsync(string domainKey, int address, byte value)
        {
            if (nes == null)
            {
                return Task.CompletedTask;
            }

            address = Math.Max(0, address);
            int domainSize = GetMemoryDomainSize(domainKey);
            if (domainSize > 0 && address >= domainSize)
            {
                return Task.CompletedTask;
            }

            return RunOnEmulationThreadAsync(() => PokeDomainByte(domainKey, address, value));
        }

        private byte PeekDomainByte(string domainKey, int address)
        {
            if (nes == null || address < 0)
            {
                return 0;
            }

            return domainKey switch
            {
                "PRG" => nes.PeekPrg(address),
                "PRGRAM" => nes.PeekPrgRam(address),
                "CHR" => nes.PeekChr(address),
                "RAM" => nes.PeekSystemRam(address),
                _ => nes.PeekSystemRam(address)
            };
        }

        private void PokeDomainByte(string domainKey, int address, byte value)
        {
            if (nes == null || address < 0)
            {
                return;
            }

            switch (domainKey)
            {
                case "PRG":
                    nes.PokePrg(address, value);
                    break;
                case "PRGRAM":
                    nes.PokePrgRam(address, value);
                    break;
                case "CHR":
                    nes.PokeChr(address, value);
                    break;
                case "RAM":
                    nes.PokeSystemRam(address, value);
                    break;
            }
        }

        internal void SetStubbornMode(bool enabled)
        {
            lock (corruptorLock) { corruptor.StubbornMode = enabled; }
            if (nes != null)
            {
                QueueEmuAction(() =>
                {
                    try { nes.SetStubbornFixEnabled(enabled); }
                    catch (Exception ex) { Console.WriteLine($"SetStubbornFixEnabled error: {ex.Message}"); }
                });
            }
            NotifyCorruptorChanged();
        }

        internal void SetCrashBehaviorFromTools(string behavior) => SetCrashBehavior(behavior);
    }
}
