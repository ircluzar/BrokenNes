using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using NesEmulator;

namespace BrokenNes.Services;

/// <summary>
/// Durable cartridge save persistence for BrokenNes Lite.
///
/// WHY THIS EXISTS: Lite had none. Every byte a cartridge wrote to its battery-backed PRG RAM, and
/// every byte a self-flashing cart (mapper 30 / UNROM-512) programmed into its own flash, lived
/// only in the tab's heap. Closing the tab, navigating away, or having a mobile browser discard the
/// backgrounded page threw all of it away silently - which for VRUN: Corrupt the World means the
/// game's Start+Select save does nothing you can ever come back to.
///
/// TWO INDEPENDENT MEDIA, NEITHER MAY SHORT-CIRCUIT THE OTHER. A UNROM-512 has flash and no WRAM at
/// all, so `GetPrgRamSize() == 0` must not stop the flash from being saved; an ordinary MMC1/MMC3
/// battery game has WRAM and no flash, so `HasMapperBatteryMemory == false` must not stop the PRG
/// RAM from being saved. Handling both is what makes ordinary battery games work on Lite as a side
/// effect of the flash work. They are stored under two separate keys for the same reason the
/// desktop keeps two separate files: folding them together would need a container header, and the
/// two blobs already have their own framing conventions (the flash blob is self-describing, the PRG
/// RAM image is deliberately raw so it interchanges with other emulators' .sav files).
///
/// STORAGE IS INDEXEDDB, NOT LOCALSTORAGE. A chip-erased mapper-30 blob is 525,336 bytes; base64'd
/// into localStorage that is ~700KB of a ~5MB origin quota for ONE save of ONE game, written
/// synchronously on the emulation thread. IndexedDB stores the Uint8Array directly via structured
/// clone (no base64 tax) and asynchronously, and Lite already migrated ROM blobs off localStorage
/// into the same `nesStorage` DB - see nesInterop.migrateLocalStorageRoms. Keys go in the existing
/// `kv` store beside the ROM data; no DB version bump was needed.
///
/// KEYS ARE CONTENT-ADDRESSED AND IDENTICAL TO THE DESKTOP'S. The identity is
/// NES.ComputeGameIdentity().GameId - "nes_" + sha1(PRG||CHR) - reduced to alphanumerics, exactly
/// mirroring Windows/MainForm/MainForm.BatteryRam.cs GetBatteryRamPathForNes. That means a save
/// made in Lite and a save made on the desktop name the same cartridge, so the blobs are
/// interchangeable if the user ever moves one across by hand. It deliberately is NOT keyed on
/// NesController.CurrentRomName: that namespace belongs to savestates, and a filename does not
/// identify a cartridge (two builds of the same homebrew share it, and the same ROM renamed does
/// not).
/// </summary>
public sealed class BatterySaveService
{
    /// <summary>
    /// Namespace for every key this service owns, so a listing of the `kv` store separates these
    /// from prefs (`pref_*`), audio volumes and savestate chunks at a glance. The `v1` is the key
    /// LAYOUT, not the blob format - the flash blob carries its own version byte and its own ROM
    /// fingerprint (see Mapper30.ExportNonVolatileMemory), so bumping this is only ever needed if
    /// the *naming* scheme changes.
    /// </summary>
    public const string KeyPrefix = "battery_v1:";

    /// <summary>Suffixes mirror the desktop's two filenames for the same game identity.</summary>
    private const string PrgRamSuffix = ".sav";
    private const string FlashSuffix = ".flash.sav";

    private readonly IJSRuntime _js;

    /// <summary>
    /// Which game <see cref="_lastPersistedPrgRam"/>/<see cref="_lastPersistedFlash"/> describe.
    /// Without this, switching ROMs would compare the new cartridge's bytes against the previous
    /// one's and could wrongly decide "unchanged, skip the write".
    /// </summary>
    private string? _trackedKeyBase;

    /// <summary>
    /// The bytes we have CONFIRMED are in IndexedDB, used only to skip no-op writes so a game that
    /// never touches its save RAM does not churn storage every ten seconds. Updated only after a
    /// write reports success, so a failed write is naturally retried on the next autosave.
    /// </summary>
    private byte[]? _lastPersistedPrgRam;
    private byte[]? _lastPersistedFlash;

    /// <summary>
    /// The write currently in progress, if any. IndexedDB writes are async while the autosave is
    /// fired from inside a frame, so without this a slow write (or a 525KB one) could have several
    /// autosaves stacked behind it, each carrying older bytes than the last.
    /// </summary>
    private Task<bool>? _inFlight;

    public BatterySaveService(IJSRuntime js)
    {
        _js = js;
    }

    // ===================== Snapshot =====================

    /// <summary>
    /// A self-consistent copy of the cartridge's non-volatile media, taken synchronously so the
    /// emulator cannot advance underneath the write. It carries its own KEYS rather than looking
    /// them up at write time: a ROM switch between snapshot and write would otherwise file this
    /// game's save under the next game's key.
    /// </summary>
    public sealed class Snapshot
    {
        public string KeyBase { get; init; } = string.Empty;
        public string PrgRamKey { get; init; } = string.Empty;
        public string FlashKey { get; init; } = string.Empty;
        public byte[]? PrgRam { get; init; }
        public byte[]? Flash { get; init; }
    }

    /// <summary>What happened to the flash blob on load, so the caller can tell the player.</summary>
    public enum FlashLoadOutcome
    {
        /// <summary>Cartridge has no flash medium; nothing was attempted.</summary>
        NotApplicable,
        /// <summary>Cartridge has flash, but nothing is stored for it yet.</summary>
        Missing,
        /// <summary>Stored blob was read and mapped in.</summary>
        Applied,
        /// <summary>Stored blob is a real save, but for a different ROM. Left untouched.</summary>
        NotForThisRom,
        /// <summary>Stored blob is damaged or unreadable. Left untouched.</summary>
        Rejected,
    }

    public sealed class LoadReport
    {
        public bool PrgRamApplied { get; set; }
        public int PrgRamBytes { get; set; }
        public FlashLoadOutcome Flash { get; set; } = FlashLoadOutcome.NotApplicable;

        /// <summary>
        /// A short line worth showing the player, or null when the load was unremarkable. The two
        /// non-Applied outcomes matter because in both cases a file exists that we deliberately did
        /// not overwrite - the player should know their save is still there and why it did not load.
        /// </summary>
        public string? Notice => Flash switch
        {
            FlashLoadOutcome.NotForThisRom => "Cartridge save found, but it belongs to a different build of this ROM - left untouched.",
            FlashLoadOutcome.Rejected => "Stored cartridge save could not be read - left untouched, nothing was overwritten.",
            _ => null,
        };
    }

    // ===================== Key derivation =====================

    /// <summary>
    /// Content-addressed save key for a loaded cartridge, or null if it cannot be identified.
    /// Mirrors MainForm.BatteryRam.cs GetBatteryRamPathForNes exactly (minus the directory), so
    /// desktop and Lite name the same game the same way.
    /// </summary>
    public static string? ComputeKeyBase(NES? nes)
    {
        if (nes == null) return null;

        string identity;
        try { identity = nes.ComputeGameIdentity().GameId; }
        catch { identity = string.Empty; }

        if (string.IsNullOrWhiteSpace(identity))
        {
            // No cartridge hash available (no ROM loaded, or a header the identity code refused).
            // Fall back to the filename, normalised the same way the desktop normalises it, so the
            // two still agree. Lite has no RomPath - the browser never sees one - and RomName is
            // already the bare filename the desktop would have reduced RomPath to.
            var fallback = nes.RomName;
            if (string.IsNullOrWhiteSpace(fallback)) return null;
            var normalized = fallback.Trim().ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
            identity = $"rom_{hash}";
        }

        var safe = new string(identity.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? null : safe;
    }

    private static string PrgRamKeyFor(string keyBase) => KeyPrefix + keyBase + PrgRamSuffix;
    private static string FlashKeyFor(string keyBase) => KeyPrefix + keyBase + FlashSuffix;

    // ===================== Capture =====================

    /// <summary>
    /// Take a snapshot of anything worth writing, or null if there is nothing to do. Fully
    /// synchronous on purpose: called from inside the frame loop so the bytes cannot be a mix of
    /// two different emulated instants.
    /// </summary>
    public Snapshot? Capture(NES? nes)
    {
        if (nes == null) return null;

        // The mapper layer arms this after a savestate load or an import it could not apply, i.e.
        // whenever the in-memory flash is knowingly NOT the same thing as the stored save. Writing
        // during that window is how a savestate quietly destroys a real save. It clears itself the
        // moment the game genuinely programs flash again, so this is a pause, not a permanent stop.
        try { if (nes.SuppressBatteryAutosave) return null; }
        catch { }

        var keyBase = ComputeKeyBase(nes);
        if (keyBase == null) return null;

        if (!string.Equals(keyBase, _trackedKeyBase, StringComparison.Ordinal))
        {
            // Different cartridge than the trackers describe: forget them so the first write for
            // this game always happens instead of being compared against another game's bytes.
            _trackedKeyBase = keyBase;
            _lastPersistedPrgRam = null;
            _lastPersistedFlash = null;
        }

        byte[]? prgRam = null;
        try
        {
            int size = nes.GetPrgRamSize();
            if (size > 0)
            {
                var buf = new byte[size];
                for (int i = 0; i < size; i++) buf[i] = nes.PeekPrgRam(i);
                if (_lastPersistedPrgRam == null || !_lastPersistedPrgRam.AsSpan().SequenceEqual(buf))
                    prgRam = buf;
            }
        }
        catch { }

        byte[]? flash = null;
        try
        {
            if (nes.HasMapperBatteryMemory)
            {
                var blob = nes.ExportMapperBatteryMemory();
                // A null/empty blob means the game has not programmed its flash this session.
                // Deliberately leave whatever is already stored alone: booting a flash cart and
                // quitting without saving must not erase the previous save.
                if (blob != null && blob.Length > 0
                    && (_lastPersistedFlash == null || !_lastPersistedFlash.AsSpan().SequenceEqual(blob)))
                    flash = blob;
            }
        }
        catch { }

        if (prgRam == null && flash == null) return null;
        return new Snapshot
        {
            KeyBase = keyBase,
            PrgRamKey = PrgRamKeyFor(keyBase),
            FlashKey = FlashKeyFor(keyBase),
            PrgRam = prgRam,
            Flash = flash,
        };
    }

    // ===================== Write =====================

    /// <summary>
    /// Write a snapshot to IndexedDB. Never throws - it is routinely fire-and-forgotten from the
    /// frame loop, where an unobserved exception would be both invisible and fatal to the task.
    /// </summary>
    public async Task<bool> PersistAsync(Snapshot? snap)
    {
        if (snap == null) return true;
        bool ok = true;

        if (snap.PrgRam != null)
        {
            if (await WriteBytesAsync(snap.PrgRamKey, snap.PrgRam))
            {
                // Only adopt as "known stored" if the game has not changed under us during the
                // await. If it has, these bytes describe a cartridge that is no longer loaded.
                if (string.Equals(_trackedKeyBase, snap.KeyBase, StringComparison.Ordinal))
                    _lastPersistedPrgRam = snap.PrgRam;
            }
            else ok = false;
        }

        if (snap.Flash != null)
        {
            if (await WriteBytesAsync(snap.FlashKey, snap.Flash))
            {
                if (string.Equals(_trackedKeyBase, snap.KeyBase, StringComparison.Ordinal))
                    _lastPersistedFlash = snap.Flash;
            }
            else ok = false;
        }

        return ok;
    }

    private async Task<bool> WriteBytesAsync(string key, byte[] bytes)
    {
        try { return await _js.InvokeAsync<bool>("nesInterop.idbSetBytes", key, bytes); }
        catch { return false; }
    }

    /// <summary>
    /// Periodic autosave entry point: snapshot now, write later, never block the frame.
    /// Skips entirely while a previous write is still running - the next tick, ten seconds on, will
    /// carry strictly fresher bytes anyway.
    /// </summary>
    public void QueueAutosave(NES? nes)
    {
        if (_inFlight != null && !_inFlight.IsCompleted) return;
        var snap = Capture(nes);
        if (snap == null) return;
        _inFlight = PersistAsync(snap);
    }

    /// <summary>
    /// Forced save - used on tab-hide/pagehide and before a ROM switch. Waits for any autosave
    /// already in flight first, so an older snapshot cannot land on top of this one.
    /// </summary>
    public async Task<bool> SaveNowAsync(NES? nes)
    {
        var prior = _inFlight;
        if (prior != null && !prior.IsCompleted)
        {
            try { await prior; } catch { }
        }

        var snap = Capture(nes);
        if (snap == null) return true; // nothing changed, or nothing to save - not a failure
        var task = PersistAsync(snap);
        _inFlight = task;
        return await task;
    }

    // ===================== Read =====================

    /// <summary>
    /// Restore both media for a freshly loaded cartridge. MUST be awaited after NES.LoadROM and
    /// BEFORE the first frame runs, or the game boots against a blank save and may overwrite it.
    /// </summary>
    public async Task<LoadReport> LoadForAsync(NES? nes)
    {
        var report = new LoadReport();
        if (nes == null) return report;

        var keyBase = ComputeKeyBase(nes);
        if (keyBase == null) return report;

        // A load establishes what is stored, so re-point the change trackers at this game. They are
        // filled in below only where we can be certain the stored bytes and the in-memory image
        // agree exactly; anywhere else they stay null and the first autosave simply writes.
        _trackedKeyBase = keyBase;
        _lastPersistedPrgRam = null;
        _lastPersistedFlash = null;

        // ---- PRG RAM ----
        try
        {
            int size = nes.GetPrgRamSize();
            if (size > 0)
            {
                var payload = await ReadBytesAsync(PrgRamKeyFor(keyBase));
                if (payload != null && payload.Length > 0)
                {
                    int count = Math.Min(payload.Length, size);
                    for (int i = 0; i < count; i++) nes.PokePrgRam(i, payload[i]);
                    report.PrgRamApplied = true;
                    report.PrgRamBytes = count;
                    // Only a full-size image is guaranteed to round-trip to the same bytes Capture
                    // would produce; a short one leaves the tail at power-on values.
                    if (payload.Length == size) _lastPersistedPrgRam = payload;
                }
            }
        }
        catch { }

        // ---- Mapper flash ----
        try
        {
            if (!nes.HasMapperBatteryMemory)
            {
                report.Flash = FlashLoadOutcome.NotApplicable;
            }
            else
            {
                var blob = await ReadBytesAsync(FlashKeyFor(keyBase));
                if (blob == null || blob.Length == 0)
                {
                    report.Flash = FlashLoadOutcome.Missing;
                }
                else
                {
                    var result = nes.ImportMapperBatteryMemory(blob);
                    report.Flash = result switch
                    {
                        NonVolatileImportResult.Applied => FlashLoadOutcome.Applied,
                        NonVolatileImportResult.NotForThisRom => FlashLoadOutcome.NotForThisRom,
                        _ => FlashLoadOutcome.Rejected,
                    };
                    // The blob format is a lossless round-trip of the live sectors, so on success
                    // the next Export produces exactly these bytes - recording them here saves an
                    // immediate, pointless rewrite of up to half a megabyte ten seconds from now.
                    //
                    // On the two failure outcomes the tracker stays null on purpose. NES has armed
                    // SuppressBatteryAutosave, so nothing will be written over the file until the
                    // game itself programs flash again - at which point we genuinely want a write.
                    if (result == NonVolatileImportResult.Applied) _lastPersistedFlash = blob;
                }
            }
        }
        catch
        {
            report.Flash = FlashLoadOutcome.Rejected;
        }

        return report;
    }

    private async Task<byte[]?> ReadBytesAsync(string key)
    {
        try { return await _js.InvokeAsync<byte[]?>("nesInterop.idbGetBytes", key); }
        catch { return null; }
    }
}
