using System;
using System.Collections.Generic;
using System.Linq;

namespace NesEmulator
{
    /// <summary>
    /// Memory domain information for API access
    /// </summary>
    public class MemoryDomainInfo
    {
        public string Name { get; set; } = "";
        public int Size { get; set; }
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// CPU register state
    /// </summary>
    public class CpuRegisters
    {
        public ushort PC { get; set; }
        public byte A { get; set; }
        public byte X { get; set; }
        public byte Y { get; set; }
        public byte P { get; set; }
        public ushort SP { get; set; }
    }

    /// <summary>
    /// Extension methods for NES class to support memory domain API
    /// </summary>
    public static class NesMemoryExtensions
    {
        /// <summary>
        /// Get list of all available memory domains
        /// </summary>
        public static List<MemoryDomainInfo> GetAvailableMemoryDomains(this NES nes)
        {
            // Get actual sizes from cartridge (use 0 if not available)
            int prgRomSize = nes.GetActualPrgRomSize();
            int prgRamSize = nes.GetActualPrgRamSize();
            int chrSize = nes.GetActualChrSize();
            
            var domains = new List<MemoryDomainInfo>
            {
                new MemoryDomainInfo
                {
                    Name = "System RAM",
                    Size = 0x800, // 2KB
                    Description = "NES system RAM (mirrored at $0000-$07FF)"
                },
                new MemoryDomainInfo
                {
                    Name = "CPU Bus",
                    Size = 0x10000, // 64KB
                    Description = "Full CPU address space"
                },
                new MemoryDomainInfo
                {
                    Name = "PRG ROM",
                    Size = prgRomSize,
                    Description = "Cartridge PRG ROM data"
                },
                new MemoryDomainInfo
                {
                    Name = "PRG RAM",
                    Size = prgRamSize,
                    Description = "Cartridge PRG RAM/SRAM"
                },
                new MemoryDomainInfo
                {
                    Name = "CHR",
                    Size = chrSize,
                    Description = "Cartridge CHR ROM/RAM"
                }
            };

            return domains;
        }

        /// <summary>
        /// Get size of a specific memory domain
        /// </summary>
        public static int GetMemoryDomainSize(this NES nes, string domainName)
        {
            return domainName switch
            {
                "System RAM" => 0x800,
                "CPU Bus" => 0x10000,
                "PRG ROM" => nes.GetActualPrgRomSize(),
                "PRG RAM" => nes.GetActualPrgRamSize(),
                "CHR" => nes.GetActualChrSize(),
                _ => throw new ArgumentException($"Unknown memory domain: {domainName}")
            };
        }

        /// <summary>
        /// Read a single byte from a memory domain
        /// </summary>
        public static byte PeekMemory(this NES nes, string domainName, int address)
        {
            return domainName switch
            {
                "System RAM" => nes.PeekSystemRam(address),
                "CPU Bus" => nes.PeekCpu((ushort)address),
                "PRG ROM" => nes.PeekPrg(address),
                "PRG RAM" => nes.PeekPrgRam(address),
                "CHR" => nes.PeekChr(address),
                _ => throw new ArgumentException($"Unknown memory domain: {domainName}")
            };
        }

        /// <summary>
        /// Write a single byte to a memory domain
        /// </summary>
        public static void PokeMemory(this NES nes, string domainName, int address, byte value)
        {
            switch (domainName)
            {
                case "System RAM":
                    nes.PokeSystemRam(address, value);
                    break;
                case "CPU Bus":
                    nes.PokeCpu((ushort)address, value);
                    break;
                case "PRG ROM":
                    nes.PokePrg(address, value);
                    break;
                case "PRG RAM":
                    nes.PokePrgRam(address, value);
                    break;
                case "CHR":
                    nes.PokeChr(address, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown memory domain: {domainName}");
            }
        }

        /// <summary>
        /// Read multiple bytes from a memory domain
        /// </summary>
        public static byte[] PeekMemoryRange(this NES nes, string domainName, int address, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = nes.PeekMemory(domainName, address + i);
            }
            return data;
        }

        /// <summary>
        /// Write multiple bytes to a memory domain
        /// </summary>
        public static void PokeMemoryRange(this NES nes, string domainName, int address, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                nes.PokeMemory(domainName, address + i, data[i]);
            }
        }

        // Helper methods to get actual memory sizes from cartridge
        private static int GetActualPrgRomSize(this NES nes)
        {
            try
            {
                return nes.GetPrgRomSize();
            }
            catch
            {
                return 0; // Fallback
            }
        }

        private static int GetActualPrgRamSize(this NES nes)
        {
            try
            {
                return nes.GetPrgRamSize();
            }
            catch
            {
                return 0; // Fallback - better to show 0 than fake size
            }
        }

        private static int GetActualChrSize(this NES nes)
        {
            try
            {
                return nes.GetChrSize();
            }
            catch
            {
                return 0; // Fallback - better to show 0 than fake size
            }
        }

        // === CPU State Access ===

        /// <summary>
        /// Get CPU registers
        /// </summary>
        public static CpuRegisters GetCpuRegisters(this NES nes)
        {
            var (pc, a, x, y, p, sp) = nes.GetCpuRegs();
            return new CpuRegisters
            {
                PC = pc,
                A = a,
                X = x,
                Y = y,
                P = p,
                SP = sp
            };
        }

        /// <summary>
        /// Get CPU core ID (implementation name)
        /// </summary>
        public static string GetCpuCoreIdentifier(this NES nes)
        {
            return nes.GetCpuCoreId();
        }

        /// <summary>
        /// Get full CPU state snapshot
        /// </summary>
        public static object GetCpuStateSnapshot(this NES nes)
        {
            return nes.GetCpuState();
        }

        // === PPU State Access ===

        /// <summary>
        /// Get framebuffer (screen pixels)
        /// </summary>
        public static byte[] GetFramebuffer(this NES nes)
        {
            var fb = nes.GetFrameBuffer();
            if (fb == null) return new byte[256 * 240 * 4];
            
            // Convert to byte array
            byte[] result = new byte[fb.Length * 4];
            for (int i = 0; i < fb.Length; i++)
            {
                uint pixel = fb[i];
                result[i * 4 + 0] = (byte)((pixel >> 16) & 0xFF); // R
                result[i * 4 + 1] = (byte)((pixel >> 8) & 0xFF);  // G
                result[i * 4 + 2] = (byte)(pixel & 0xFF);         // B
                result[i * 4 + 3] = (byte)((pixel >> 24) & 0xFF); // A
            }
            return result;
        }

        /// <summary>
        /// Get PPU core ID (implementation name)
        /// </summary>
        public static string GetPpuCoreIdentifier(this NES nes)
        {
            return nes.GetPpuCoreId();
        }

        /// <summary>
        /// Get full PPU state snapshot
        /// </summary>
        public static object GetPpuStateSnapshot(this NES nes)
        {
            return nes.GetPpuState();
        }

        // === APU State Access ===

        /// <summary>
        /// Get APU core ID (implementation name)
        /// </summary>
        public static string GetApuCoreIdentifier(this NES nes)
        {
            return nes.GetApuCoreId();
        }

        /// <summary>
        /// Get available CPU core IDs
        /// </summary>
        public static List<string> GetAvailableCpuCores(this NES nes)
        {
            return nes.GetCpuCoreIds().ToList();
        }

        /// <summary>
        /// Get available PPU core IDs
        /// </summary>
        public static List<string> GetAvailablePpuCores(this NES nes)
        {
            return nes.GetPpuCoreIds().ToList();
        }

        /// <summary>
        /// Get available APU core IDs
        /// </summary>
        public static List<string> GetAvailableApuCores(this NES nes)
        {
            return nes.GetApuCoreIds().ToList();
        }
    }
}
