namespace NesEmulator
{
// Outcome of IMapper.ImportNonVolatileMemory.
//
// WHY THIS IS NOT A void: a save that fails to load is not a neutral event. The caller's very
// next act is usually an autosave, which would write the mapper's blank flash straight over the
// file it just failed to read - turning a recoverable bad load (wrong ROM build, half-copied
// file) into permanent loss of the player's progress. The caller therefore has to be able to
// tell "loaded" from "did not load", and ideally why.
public enum NonVolatileImportResult
{
    // The blob was valid for this cartridge and its contents are now live in the mapper.
    Applied,

    // Well-formed save data for THIS mapper, but recorded against a different ROM - a different
    // chip size, or the same size with different contents. Almost certainly somebody's real save
    // for another build sitting under a shared filename: keep the file, do not overwrite it.
    NotForThisRom,

    // Not usable as this mapper's save data at all: wrong magic, wrong format version, truncated,
    // internally inconsistent, or this mapper has no non-volatile medium to load into.
    Rejected
}

public interface IMapper {
    void Reset();
    
    byte CPURead(ushort address);
    void CPUWrite(ushort address, byte value);

    byte PPURead(ushort address);
    void PPUWrite(ushort address, byte value);

    // Map CPU address ($8000-$FFFF) to PRG ROM byte index for the currently selected bank(s).
    // Returns true when the address resolves to PRG ROM; false if out of range or mapped to PRG RAM/unsupported.
    bool TryCpuToPrgIndex(ushort address, out int prgIndex);

    // Save/Load mapper-specific state (PRG/CHR RAM handled at cartridge level separately)
    object GetMapperState();
    void SetMapperState(object state);
        uint GetChrBankSignature();

    // Optional hint to inform mapper of current PPU fetch phase; default is no-op for mappers that don't care.
    void PpuPhaseHint(bool isSpriteFetch, bool objSize16, bool renderingEnabled) {}

    // Optional: notify mapper that the PPU just fetched a nametable tile index (address in $2000-$2FFF).
    // Mappers like MMC5 (Mode 1) use this to track the last NT tile read for ExRAM-based features.
    void PpuNtFetch(ushort ntAddress) {}

    // Optional: for MMC5 Mode 1, provide a per-tile BG palette index (0..3) derived from ExRAM.
    // Return -1 when not applicable so the PPU can fall back to regular attribute table logic.
    int GetMmc5Mode1BgPaletteIndex() { return -1; }

    // Optional: hint for PPU to adjust behavior when MMC5 Mode 1 BG mapping is active.
    // Default false; mappers that support MMC5 Mode 1 can override to return true for BG.
    bool IsMmc5Mode1BgActive() { return false; }

    // Optional: override nametable read/write routing (e.g., MMC5 $5105 ExRAM-as-NT and Fill Mode).
    // Return true when handled (value provided or write consumed); false to let PPU fall back to CIRAM.
    bool TryPpuNametableRead(ushort address, out byte value) { value = 0; return false; }
    bool TryPpuNametableWrite(ushort address, byte value) { return false; }

    // Optional: return per-quadrant nametable mode for an address in $2000-$2FFF when applicable.
    // Values: 0=CIRAM A, 1=CIRAM B, 2=ExRAM, 3=Fill. Return -1 when the mapper doesn’t define this.
    int GetMmc5NtModeForAddress(ushort address) { return -1; }

    // Optional: cartridge-side storage that must outlive the process, for boards whose save medium
    // is NOT the generic $6000-$7FFF PRG-RAM the battery path already handles. UNROM-512's
    // self-flashing variant is the motivating case: the game "saves" by reprogramming its own PRG
    // flash chip, so its save data lives in the mapper's flash shadow and is invisible to
    // Cartridge.prgRAM. Without this hook such a cartridge cannot save at all - the writes survive
    // a savestate and die with the process.
    //
    // The blob is deliberately OPAQUE to callers. Only the mapper knows the granularity its own
    // silicon works at (for UNROM-512, 4KB flash sectors plus their programmed/erased status), so
    // letting each mapper own its encoding keeps that hardware knowledge next to the emulation of
    // it, instead of smeared across the persistence layer. Callers just move bytes.
    //
    // Contract: ExportNonVolatileMemory() returns null when there is nothing worth persisting (no
    // battery, or nothing programmed yet) so the caller can skip writing a file at all.
    //
    // ImportNonVolatileMemory() must tolerate a blob it does not recognise or that does not fit
    // the currently loaded ROM, because .sav files outlive the ROM revisions beside them - but it
    // must SAY SO rather than failing silently (see NonVolatileImportResult), and it must be
    // all-or-nothing: a blob that is rejected for any reason has to leave the mapper's existing
    // non-volatile contents exactly as they were. Half-applying a save is worse than not loading
    // it, because the result is a chimera of two builds that looks loadable.
    bool HasNonVolatileMemory { get { return false; } }
    byte[]? ExportNonVolatileMemory() { return null; }
    NonVolatileImportResult ImportNonVolatileMemory(byte[] data) { return NonVolatileImportResult.Rejected; }

    // Monotonic count of writes the RUNNING GAME has made to this mapper's non-volatile medium.
    //
    // Deliberately NOT part of GetMapperState()/SetMapperState(): it is host-side instrumentation,
    // not emulated hardware, and it must keep counting forward across a savestate load so the
    // persistence layer can tell "the game has saved again since then" from "nothing has happened
    // since I rolled the flash back". NES.SuppressBatteryAutosave is the consumer.
    ulong NonVolatileWriteGeneration { get { return 0; } }

    // Optional: report that this mapper does not decode/drive the CPU data bus for the given
    // address (e.g. NROM's unmapped $4020-$5FFF expansion area), so Bus.cs can fall back to the
    // open-bus value instead of treating the mapper's own "not mine" sentinel as real data.
    // Default false preserves existing behavior for mappers whose CPURead always returns a value
    // it actually drives within the ranges Bus.cs calls it for.
    bool IsCpuReadOpenBus(ushort address) { return false; }
}
}
