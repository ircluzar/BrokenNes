using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NesEmulator;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        private string? GetBatteryRamPathForNes(NES? targetNes)
        {
            if (targetNes == null)
            {
                return null;
            }

            string identityKey = string.Empty;
            try
            {
                identityKey = targetNes.ComputeGameIdentity().GameId;
            }
            catch
            {
                identityKey = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(identityKey))
            {
                var fallback = !string.IsNullOrWhiteSpace(targetNes.RomPath)
                    ? Path.GetFileName(targetNes.RomPath)
                    : targetNes.RomName;
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    return null;
                }

                var normalized = fallback.Trim().ToLowerInvariant();
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
                identityKey = $"rom_{hash}";
            }

            var safeName = new string(identityKey.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(safeName))
            {
                return null;
            }

            var saveDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrokenNes",
                "BatterySaves");
            return Path.Combine(saveDir, safeName + ".sav");
        }

        private void SaveBatteryRamForNes(NES? targetNes)
        {
            if (targetNes == null)
            {
                return;
            }

            var prgRamSize = targetNes.GetPrgRamSize();
            if (prgRamSize <= 0)
            {
                return;
            }

            var savePath = GetBatteryRamPathForNes(targetNes);
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return;
            }

            try
            {
                var payload = new byte[prgRamSize];
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = targetNes.PeekPrgRam(i);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                File.WriteAllBytes(savePath, payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to save PRG RAM: {ex.Message}");
            }
        }

        private void LoadBatteryRamForNes(NES? targetNes)
        {
            if (targetNes == null)
            {
                return;
            }

            var prgRamSize = targetNes.GetPrgRamSize();
            if (prgRamSize <= 0)
            {
                return;
            }

            var savePath = GetBatteryRamPathForNes(targetNes);
            if (string.IsNullOrWhiteSpace(savePath) || !File.Exists(savePath))
            {
                return;
            }

            try
            {
                var payload = File.ReadAllBytes(savePath);
                var count = Math.Min(payload.Length, prgRamSize);
                for (int i = 0; i < count; i++)
                {
                    targetNes.PokePrgRam(i, payload[i]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to load PRG RAM: {ex.Message}");
            }
        }

        private void SaveBatteryRamForCurrentRom()
        {
            SaveBatteryRamForNes(nes);
        }

        private void LoadBatteryRamForCurrentRom()
        {
            LoadBatteryRamForNes(nes);
        }
    }
}
