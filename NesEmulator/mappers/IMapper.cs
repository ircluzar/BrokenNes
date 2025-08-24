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
}
}
