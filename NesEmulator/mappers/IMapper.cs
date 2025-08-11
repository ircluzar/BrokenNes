namespace NesEmulator
{
public interface IMapper {
    void Reset();
    
    byte CPURead(ushort address);
    void CPUWrite(ushort address, byte value);

    byte PPURead(ushort address);
    void PPUWrite(ushort address, byte value);

    // Save/Load mapper-specific state (PRG/CHR RAM handled at cartridge level separately)
    object GetMapperState();
    void SetMapperState(object state);
}
}
