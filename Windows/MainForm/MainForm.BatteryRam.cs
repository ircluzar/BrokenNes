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

        /// <summary>
        /// Serialises battery-RAM writes. Since the periodic autosave runs on the emulation thread
        /// while the shutdown/ROM-switch saves run on the UI thread, two writes to the same .sav
        /// could otherwise interleave.
        /// </summary>
        private readonly object batteryRamFileLock = new object();

        /// <summary>
        /// The bytes we believe are already on disk. Used only to skip no-op writes - a game that
        /// is not touching its save RAM should not cause a disk write every ten seconds.
        /// </summary>
        private byte[]? lastPersistedBatteryRam;

        private void SaveBatteryRamForNes(NES? targetNes)
        {
            SaveBatteryRamForNes(targetNes, onlyIfChanged: false);
        }

        /// <summary>
        /// Writes the cartridge's battery-backed PRG RAM to disk.
        ///
        /// The write is atomic (temp file + replace) on purpose. This file is a player's actual
        /// save game, and the process can disappear at any instant - Task Manager, a power cut, a
        /// crash, or (as the 2026-09-04 investigation established) some other tool on the machine
        /// running a process-name-wide taskkill. A plain File.WriteAllBytes that is interrupted
        /// halfway leaves a truncated .sav, which is worse than losing the last few seconds: it
        /// destroys the save that was previously there and loaded fine.
        /// </summary>
        /// <param name="onlyIfChanged">
        /// When true, skip the write if the RAM is byte-identical to what we last persisted. Used
        /// by the periodic autosave so idle play does not churn the disk.
        /// </param>
        private void SaveBatteryRamForNes(NES? targetNes, bool onlyIfChanged)
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

                lock (batteryRamFileLock)
                {
                    if (onlyIfChanged
                        && lastPersistedBatteryRam != null
                        && lastPersistedBatteryRam.AsSpan().SequenceEqual(payload))
                    {
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

                    // Write beside the real file, then replace it in one step, so an interrupted
                    // save can only ever leave a stray .tmp - never a half-written .sav.
                    var tempPath = savePath + ".tmp";
                    File.WriteAllBytes(tempPath, payload);
                    File.Move(tempPath, savePath, overwrite: true);

                    lastPersistedBatteryRam = payload;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to save PRG RAM: {ex.Message}");
                Diagnostics.ShutdownDiagnostics.Log($"[BatteryRAM] save failed: {ex}");
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

        /// <summary>How many emulated frames between autosave checks (~10s at 60 fps).</summary>
        private const int BatteryAutoSaveFrameInterval = 600;

        private int batteryAutoSaveFrameCounter;

        /// <summary>
        /// Periodic battery-RAM autosave, called once per emulated frame from the emulation loop.
        ///
        /// WHY THIS EXISTS: before it, the ONLY writes of a player's save RAM were on graceful
        /// paths - OnFormClosing, Close Rom, and switching ROMs. Anything that ended the process
        /// abruptly threw away every byte of progress since the ROM was loaded, silently and with
        /// no way to recover it. That is a data-loss bug independent of *why* the process ended,
        /// and it is the reason the "silent process death" report was so damaging: not the exit
        /// itself, but that hours of play went with it.
        ///
        /// Cost is near zero: it only does anything once every <see cref="BatteryAutoSaveFrameInterval"/>
        /// frames, only for cartridges that actually have battery RAM, and it skips the write
        /// entirely when the RAM has not changed since the last save.
        /// </summary>
        private void MaybeAutoSaveBatteryRam()
        {
            if (++batteryAutoSaveFrameCounter < BatteryAutoSaveFrameInterval)
            {
                return;
            }
            batteryAutoSaveFrameCounter = 0;

            var targetNes = nes;
            if (targetNes == null)
            {
                return;
            }

            // The embedded placeholder ROM is not a game and has no progress worth keeping; the
            // graceful-shutdown path skips it too, so match that.
            if (string.Equals(targetNes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SaveBatteryRamForNes(targetNes, onlyIfChanged: true);
        }

        /// <summary>
        /// Forget what we think is on disk. Called when the ROM changes so the first autosave for
        /// the new game always writes rather than comparing against the previous game's bytes.
        /// </summary>
        private void ResetBatteryRamAutoSaveTracking()
        {
            lock (batteryRamFileLock)
            {
                lastPersistedBatteryRam = null;
            }
            batteryAutoSaveFrameCounter = 0;
        }
    }
}
