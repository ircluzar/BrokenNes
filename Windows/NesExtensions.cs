using System;
using NesEmulator;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Helper extensions for NES class to make it easier to work with in WinForms
    /// </summary>
    public static class NesExtensions
    {
        // NES button indices:
        // 0 = A, 1 = B, 2 = Select, 3 = Start, 4 = Up, 5 = Down, 6 = Left, 7 = Right
        
        private static bool[] player1Buttons = new bool[8];
        private static bool[] player2Buttons = new bool[8];
        
        /// <summary>
        /// Set a specific button state for a player
        /// </summary>
        /// <param name="nes">NES instance</param>
        /// <param name="player">Player number (0 or 1)</param>
        /// <param name="buttonIndex">Button index (0-7)</param>
        /// <param name="pressed">True if pressed, false if released</param>
        public static void SetButton(this NES nes, int player, int buttonIndex, bool pressed)
        {
            if (buttonIndex < 0 || buttonIndex >= 8) return;
            
            if (player == 0)
            {
                player1Buttons[buttonIndex] = pressed;
                nes.SetInput(player1Buttons);
            }
            else if (player == 1)
            {
                player2Buttons[buttonIndex] = pressed;
                nes.SetInputs(player1Buttons, player2Buttons);
            }
        }
        
        /// <summary>
        /// Clear all button states for all players
        /// </summary>
        public static void ClearButtons(this NES nes)
        {
            Array.Clear(player1Buttons, 0, player1Buttons.Length);
            Array.Clear(player2Buttons, 0, player2Buttons.Length);
            nes.SetInputs(player1Buttons, player2Buttons);
        }

        /// <summary>
        /// Get available memory domains
        /// </summary>
        public static System.Collections.Generic.List<(string Name, int Size, string Description)> GetAvailableMemoryDomains(this NES nes)
        {
            var domains = new System.Collections.Generic.List<(string, int, string)>
            {
                ("System RAM", 2048, "2KB internal RAM"),
                ("CPU Bus", 65536, "Full CPU address space"),
                ("PRG ROM", nes.GetPrgRomSize(), "PRG ROM data"),
                ("PRG RAM", 8192, "PRG RAM/Save RAM"),
                ("CHR", 8192, "CHR ROM/RAM data")
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
                "System RAM" => 2048,
                "CPU Bus" => 65536,
                "PRG ROM" => nes.GetPrgRomSize(),
                "PRG RAM" => 8192,
                "CHR" => 8192,
                _ => throw new ArgumentException($"Unknown domain: {domainName}")
            };
        }

        /// <summary>
        /// Peek memory from a specific domain
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
                _ => throw new ArgumentException($"Unknown domain: {domainName}")
            };
        }

        /// <summary>
        /// Poke memory to a specific domain
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
                    throw new ArgumentException($"Unknown domain: {domainName}");
            }
        }

        /// <summary>
        /// Peek a range of memory from a specific domain
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
        /// Poke a range of memory to a specific domain
        /// </summary>
        public static void PokeMemoryRange(this NES nes, string domainName, int address, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                nes.PokeMemory(domainName, address + i, data[i]);
            }
        }

        /// <summary>
        /// Get CPU registers
        /// </summary>
        public static (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetCpuRegisters(this NES nes)
        {
            return nes.GetCpuRegs();
        }

        /// <summary>
        /// Get available CPU cores
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<string> GetAvailableCpuCores(this NES nes)
        {
            return nes.GetCpuCoreIds();
        }

        /// <summary>
        /// Get current CPU core identifier
        /// </summary>
        public static string GetCpuCoreIdentifier(this NES nes)
        {
            return nes.GetCpuCoreId();
        }

        /// <summary>
        /// Get CPU state snapshot as JSON
        /// </summary>
        public static object GetCpuStateSnapshot(this NES nes)
        {
            return nes.GetCpuState();
        }

        /// <summary>
        /// Get framebuffer
        /// </summary>
        public static byte[] GetFramebuffer(this NES nes)
        {
            return nes.GetFrameBuffer();
        }

        /// <summary>
        /// Get current PPU core identifier
        /// </summary>
        public static string GetPpuCoreIdentifier(this NES nes)
        {
            return nes.GetPpuCoreId();
        }

        /// <summary>
        /// Get available PPU cores
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<string> GetAvailablePpuCores(this NES nes)
        {
            return nes.GetPpuCoreIds();
        }

        /// <summary>
        /// Get PPU state snapshot as JSON
        /// </summary>
        public static object GetPpuStateSnapshot(this NES nes)
        {
            return nes.GetPpuState();
        }

        /// <summary>
        /// Get current APU core identifier
        /// </summary>
        public static string GetApuCoreIdentifier(this NES nes)
        {
            return nes.GetApuCoreId();
        }

        /// <summary>
        /// Get available APU cores
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<string> GetAvailableApuCores(this NES nes)
        {
            return nes.GetApuCoreIds();
        }
        
        /// <summary>
        /// Create a GlitchHarvesterEngine instance for the given corruptor
        /// </summary>
        public static GlitchHarvesterEngine GetGlitchHarvester(this Corruptor corruptor)
        {
            return corruptor.GlitchHarvester;
        }
    }
}
