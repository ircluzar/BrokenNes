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

        /// <summary>
        /// Same idea as <see cref="lastPersistedBatteryRam"/>, for the mapper flash blob. This one
        /// matters more, not less: the blob is up to ~525KB once the cart has been chip-erased, and
        /// without this it was re-serialised and rewritten on EVERY autosave - a half-megabyte disk
        /// write every ten seconds, forever, for a game that had saved once an hour ago.
        ///
        /// Must be cleared in <see cref="ResetBatteryRamAutoSaveTracking"/> alongside its PRG-RAM
        /// sibling. If it survived a ROM switch, the first autosave for the NEW game would compare
        /// the new game's blob against the OLD game's and could skip a legitimate first write -
        /// turning a performance fix into data loss.
        /// </summary>
        private byte[]? lastPersistedMapperBattery;

        /// <summary>
        /// Hard stop on mapper-flash persistence for the rest of this ROM's session.
        ///
        /// Set only when we failed to read an existing .flash.sav AND failed to move it out of the
        /// way. In that situation the player's (unreadable, but possibly recoverable) bytes are
        /// still sitting at the canonical path, and this cartridge's in-memory flash does not
        /// represent them - so writing anything there would destroy the only copy. Refusing to
        /// write costs the session's saves; writing costs the file. The file wins.
        ///
        /// When the quarantine rename DOES succeed we deliberately do not set this: the original
        /// bytes are preserved under a .rejected name, the canonical path is free, and NES's own
        /// <see cref="NES.SuppressBatteryAutosave"/> already blocks writes until the game performs
        /// a genuine flash write - at which point the player really has saved and it must persist.
        /// </summary>
        /// <remarks>volatile: written on the UI thread by a ROM load, read on the emulation thread
        /// by the periodic autosave.</remarks>
        private volatile bool mapperBatteryPersistenceDisabled;

        /// <summary>
        /// Latches the one-time "skipping writes because the core says memory and disk have
        /// deliberately diverged" log line, so a suppressed autosave does not print every 10s.
        /// </summary>
        private volatile bool loggedMapperBatterySuppression;

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

            // The embedded placeholder ROM is not a game and has no progress worth keeping. The
            // guard lives here rather than at each call site because it was previously missing from
            // the ROM-SWITCH saves (LoadRomBytes, LoadTestRom, the API load path), so every single
            // ROM load rewrote test.nes's own all-zero .sav for no reason. OnFormClosing, Close Rom
            // and the autosave already guarded; centralising makes the set complete and keeps a
            // future call site from reintroducing the same hole.
            if (string.Equals(targetNes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SavePrgRamForNes(targetNes, onlyIfChanged);
            SaveMapperBatteryForNes(targetNes, onlyIfChanged);
        }

        private void LoadBatteryRamForNes(NES? targetNes)
        {
            // Cleared unconditionally, and BEFORE the null check, because this is the one call that
            // reliably marks the boundary between one cartridge's session and the next. The flag is
            // about a specific file we could not read; a different ROM (or no ROM) means it no
            // longer applies. It deliberately is NOT cleared in ResetBatteryRamAutoSaveTracking,
            // which every caller runs AFTER this - that would wipe the protection the load just set.
            mapperBatteryPersistenceDisabled = false;
            loggedMapperBatterySuppression = false;

            if (targetNes == null)
            {
                return;
            }

            LoadPrgRamForNes(targetNes);
            LoadMapperBatteryForNes(targetNes);
        }

        // The two media are independent: a cartridge can have flash save data and no usable PRG-RAM
        // (UNROM-512 has no WRAM at all), so neither path may short-circuit the other.
        /// <param name="onlyIfChanged">
        /// When true, skip the write if the serialised blob is byte-identical to what we last
        /// persisted. See <see cref="lastPersistedMapperBattery"/> for why this is not optional.
        /// </param>
        private void SaveMapperBatteryForNes(NES targetNes, bool onlyIfChanged)
        {
            byte[]? blob;
            try
            {
                if (!targetNes.HasMapperBatteryMemory)
                {
                    return;
                }

                // We could not read this cartridge's existing save file and could not move it aside
                // either, so it is still sitting at the canonical path. Never write over it.
                if (mapperBatteryPersistenceDisabled)
                {
                    return;
                }

                // The core is telling us that the in-memory flash and the file on disk have
                // deliberately diverged - the usual cause is loading an older savestate, which rolls
                // the flash back to whatever it held when that state was taken. Committing that
                // rollback would discard every in-game save the player made since. The flag clears
                // itself the moment the running game performs a genuine flash write, so a player who
                // loads a state and then saves in-game still gets that save persisted.
                if (targetNes.SuppressBatteryAutosave)
                {
                    if (!loggedMapperBatterySuppression)
                    {
                        loggedMapperBatterySuppression = true;
                        Console.WriteLine("[BatteryRAM] Mapper flash writes suppressed: in-memory flash was rolled back (savestate load or failed import). Will resume when the game next saves.");
                    }
                    return;
                }
                loggedMapperBatterySuppression = false;

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
                // Same lock as the PRG-RAM image, and for the same reason: the periodic autosave
                // runs on the emulation thread while the shutdown / ROM-switch saves run on the UI
                // thread. Unlocked, both writers used the SAME "<hash>.flash.sav.tmp" path, so one
                // could File.Move a temp file the other was still writing - promoting a truncated
                // blob over the player's real save, which is precisely the failure the
                // write-then-rename exists to prevent.
                lock (batteryRamFileLock)
                {
                    if (onlyIfChanged
                        && lastPersistedMapperBattery != null
                        && lastPersistedMapperBattery.AsSpan().SequenceEqual(blob))
                    {
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                    // Write-then-rename, because this runs from OnFormClosing. A save interrupted
                    // mid-write would otherwise leave a truncated file where the player's progress
                    // was, and the flash blob is large enough (up to 512KB) for that window to be
                    // real.
                    var tempPath = savePath + ".tmp";
                    File.WriteAllBytes(tempPath, blob);
                    File.Move(tempPath, savePath, overwrite: true);

                    lastPersistedMapperBattery = blob;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to save mapper flash: {ex.Message}");
                Diagnostics.ShutdownDiagnostics.Log($"[BatteryRAM] mapper flash save failed: {ex}");
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

                // The import is all-or-nothing and reports its verdict, so "did not load" must never
                // be mistaken for "loaded and empty". Acting on the result is what stops this
                // cartridge's blank flash being autosaved straight over the file we just failed to
                // read - which would turn one unreadable save into a permanently destroyed one.
                var result = targetNes.ImportMapperBatteryMemory(File.ReadAllBytes(savePath));
                if (result == NonVolatileImportResult.Applied)
                {
                    return;
                }

                var reason = result == NonVolatileImportResult.NotForThisRom
                    // Same game identity, different PRG contents: almost certainly a real save that
                    // belongs to another build of the ROM, hand-copied into place. Worth keeping.
                    ? "it belongs to a different ROM build"
                    // Damaged, truncated, or written by a format version we cannot read.
                    : "it is damaged or unreadable";
                Console.WriteLine($"[BatteryRAM] Refused to load mapper flash from '{Path.GetFileName(savePath)}': {reason}.");

                // Move it out of the firing line rather than leaving it to be clobbered. Nothing is
                // deleted - the bytes survive under a .rejected name and can be inspected or
                // restored by hand - and the canonical path is freed so the player's next real
                // in-game save has somewhere to go.
                if (TryQuarantineMapperBatteryFile(savePath, out var quarantinePath))
                {
                    Console.WriteLine($"[BatteryRAM] Preserved it as '{Path.GetFileName(quarantinePath)}'.");
                    Diagnostics.ShutdownDiagnostics.Log($"[BatteryRAM] quarantined unreadable mapper flash ({result}) as {quarantinePath}");
                }
                else
                {
                    // Could not rename it, so the only copy of those bytes is still at the path we
                    // would write to. Stop writing for the rest of this ROM's session; losing this
                    // session's saves is recoverable, overwriting the file is not.
                    mapperBatteryPersistenceDisabled = true;
                    Console.WriteLine("[BatteryRAM] Could not move it aside, so mapper flash saving is disabled for this session to avoid overwriting it.");
                    Diagnostics.ShutdownDiagnostics.Log($"[BatteryRAM] mapper flash persistence disabled: could not quarantine {savePath} ({result})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatteryRAM] Failed to load mapper flash: {ex.Message}");
                // We do not know whether the file is intact, so treat it as untouchable. NES also
                // arms its own suppression on any non-Applied import, but an exception can be thrown
                // before the import is even attempted (an unreadable file, for instance).
                mapperBatteryPersistenceDisabled = true;
            }
        }

        /// <summary>
        /// Renames a save file we could not read to a "<c>.rejected</c>" sibling, picking a free
        /// name so an earlier rejected file is never overwritten - each one may be somebody's only
        /// copy of a real save. Returns false if the file could not be moved at all.
        /// </summary>
        private static bool TryQuarantineMapperBatteryFile(string savePath, out string quarantinePath)
        {
            quarantinePath = savePath + ".rejected";
            for (int attempt = 0; attempt < 100; attempt++)
            {
                var candidate = attempt == 0
                    ? savePath + ".rejected"
                    : $"{savePath}.rejected.{DateTime.Now:yyyyMMddHHmmss}.{attempt}";
                try
                {
                    if (File.Exists(candidate))
                    {
                        continue;
                    }

                    // No overwrite: if another process won the race to this name, throw and try the
                    // next candidate rather than destroying whatever it just put there.
                    File.Move(savePath, candidate);
                    quarantinePath = candidate;
                    return true;
                }
                catch
                {
                    // Locked, denied, or lost a race - fall through to the next candidate name.
                }
            }

            return false;
        }

        private void SavePrgRamForNes(NES targetNes, bool onlyIfChanged)
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

            // SaveBatteryRamForNes also skips test.nes, but bail out here too so the placeholder ROM
            // does not pay for an export/compare it can never act on.
            if (string.Equals(targetNes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SaveBatteryRamForNes(targetNes, onlyIfChanged: true);
        }

        /// <summary>
        /// Forget what we think is on disk. Called when the ROM changes so the first autosave for
        /// the new game always writes rather than comparing against the previous game's bytes.
        ///
        /// BOTH cached images must be cleared. Leaving either behind means the first autosave after
        /// a ROM switch diffs the new game's bytes against the old game's, and an unlucky match
        /// would skip a legitimate first write - data loss introduced by a performance optimisation.
        /// </summary>
        private void ResetBatteryRamAutoSaveTracking()
        {
            lock (batteryRamFileLock)
            {
                lastPersistedBatteryRam = null;
                lastPersistedMapperBattery = null;
            }
            batteryAutoSaveFrameCounter = 0;
        }
    }
}
