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

        // Sibling of the PRG-RAM save, in the same directory and keyed off the same game identity,
        // but a SEPARATE file. Folding the flash blob into the existing ".sav" would have meant
        // giving that file a header, which would silently invalidate every battery save already on
        // disk - the existing ones are raw 8KB PRG-RAM images with no framing at all.
        private string? GetMapperBatteryPathForNes(NES? targetNes)
        {
            var basePath = GetBatteryRamPathForNes(targetNes);
            if (string.IsNullOrWhiteSpace(basePath))
            {
                return null;
            }

            return Path.ChangeExtension(basePath, ".flash.sav");
        }

        private void SaveBatteryRamForNes(NES? targetNes)
        {
            if (targetNes == null)
            {
                return;
            }

            SavePrgRamForNes(targetNes);
            SaveMapperBatteryForNes(targetNes);
        }

        private void LoadBatteryRamForNes(NES? targetNes)
        {
            if (targetNes == null)
            {
                return;
            }

            LoadPrgRamForNes(targetNes);
            LoadMapperBatteryForNes(targetNes);
        }

        // The two media are independent: a cartridge can have flash save data and no usable PRG-RAM
        // (UNROM-512 has no WRAM at all), so neither path may short-circuit the other.
        private void SaveMapperBatteryForNes(NES targetNes)
        {
            byte[]? blob;
            try
            {
                if (!targetNes.HasMapperBatteryMemory)
                {
                    return;
                }

                blob = targetNes.ExportMapperBatteryMemory();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to read mapper battery memory: {ex.Message}");
                return;
            }

            var savePath = GetMapperBatteryPathForNes(targetNes);
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return;
            }

            // A null blob means the game has not written its flash this session. Deliberately leave
            // any existing file alone instead of deleting it: booting a flash cart and quitting
            // without saving must not erase the previous save.
            if (blob == null || blob.Length == 0)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                // Write-then-rename, because this runs from OnFormClosing. A save interrupted
                // mid-write would otherwise leave a truncated file where the player's progress was,
                // and the flash blob is large enough (up to 512KB) for that window to be real.
                var tempPath = savePath + ".tmp";
                File.WriteAllBytes(tempPath, blob);
                File.Move(tempPath, savePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to save mapper flash: {ex.Message}");
            }
        }

        private void LoadMapperBatteryForNes(NES targetNes)
        {
            try
            {
                if (!targetNes.HasMapperBatteryMemory)
                {
                    return;
                }

                var savePath = GetMapperBatteryPathForNes(targetNes);
                if (string.IsNullOrWhiteSpace(savePath) || !File.Exists(savePath))
                {
                    return;
                }

                targetNes.ImportMapperBatteryMemory(File.ReadAllBytes(savePath));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to load mapper flash: {ex.Message}");
            }
        }

        private void SavePrgRamForNes(NES targetNes)
        {
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

        private void LoadPrgRamForNes(NES targetNes)
        {
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
