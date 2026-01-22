namespace NesEmulator
{
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
}
}
