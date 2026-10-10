using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// A 24C01-type two-wire serial EEPROM (128 bytes, the I2C-style part in the 24Cxx family that has no device address bits) as a Game Gear cartridge wires it: the program writes a byte into the cartridge's $8000-$BFFF window whose bit 0 is
/// the data line (SDA) and bit 1 the clock (SCL), and reads the chip's side of the data line in bit 0. The line is open-drain: a program that writes a 1 lets it float high and a 0 pulls it low, and the chip can pull it low too (that is how
/// it answers a byte with an acknowledge, and how it sends data). The chip sees the line as the AND of the two; the program reads only what the chip does to it.
/// <para><b>How it was found:</b> World Series Baseball's driver (Game Gear, 1994) writes only the values 0-3 to the window, in the pattern of this bus (a 1 to 0 step of the data line while the clock is high, then eight bits, each with a clock
/// pulse, and a test of bit 0 after the eighth), and waits for the acknowledge. No emulator in the oracle set runs it. The chip model is the 24C01's data sheet behaviour: a start condition (data falling while the clock is high), a byte of
/// seven address bits and a read/write bit (most significant first), an acknowledge; a write takes a data byte (and acknowledges it) into the address, the address then counting up inside its 8-byte page; a read sends the byte at the current
/// address most significant bit first and, if the program acknowledges, the next one; a stop condition (data rising while the clock is high) ends the transfer. The write cycle (about 10 ms on the part) is not made to take time.</para>
/// <para>The memory is the cartridge's save file: 128 bytes.</para>
/// </summary>
public sealed class Eeprom24c01
{
    public const int Size = 128;
    private readonly byte[] mem = new byte[Size];

    private bool scl = true, masterSda = true;     // the lines as the program last set them (idle: both high)
    private bool slaveSda = true;                  // false while the chip pulls the data line low
    private enum St { Idle, Address, AckAddress, WriteData, AckWrite, ReadData, MasterAck }
    private St state = St.Idle;
    private int bits, shift;
    private int address;
    private bool reading;

    public Eeprom24c01() { Array.Fill(mem, (byte)0xFF); }

    public byte[] Memory => mem;
    public bool Dirty { get; private set; }
    public void Load(byte[] data) { Array.Fill(mem, (byte)0xFF); Array.Copy(data, mem, Math.Min(data.Length, mem.Length)); Dirty = false; }
    public void ClearDirty() => Dirty = false;

    /// <summary>What a read of the cartridge window returns: bit 0 is the chip's own pull on the data line (low while it acknowledges or sends a 0, high when it lets go) - not the line with the program's own drive mixed in: World Series Baseball's
    /// bus-clearing routine writes the data line low and then waits for bit 0 to read high, which only a read of the chip's side can answer. The clock as written is in bit 1.</summary>
    public byte Read() => (byte)((slaveSda ? 1 : 0) | (scl ? 2 : 0));

    /// <summary>A write to the cartridge window: bit 0 the data line, bit 1 the clock.</summary>
    public void Write(byte value)
    {
        bool newSda = (value & 1) != 0, newScl = (value & 2) != 0;
        bool oldSda = masterSda, oldScl = scl;
        masterSda = newSda; scl = newScl;

        if (oldScl && newScl && oldSda != newSda)
        {
            // data moved while the clock stayed high: start (falling) or stop (rising)
            if (!newSda) { state = St.Address; bits = 0; shift = 0; slaveSda = true; }
            else { state = St.Idle; slaveSda = true; }
            return;
        }
        if (!oldScl && newScl) ClockRising();
        else if (oldScl && !newScl) ClockFalling();
    }

    private bool Line => masterSda && slaveSda;

    private void ClockRising()
    {
        switch (state)
        {
            case St.Address:
            case St.WriteData:
                shift = (shift << 1) | (Line ? 1 : 0);
                bits++;
                break;
            case St.MasterAck:
                // the program answers a byte it read: low (acknowledge) asks for the next one, high ends the read
                if (masterSda) state = St.Idle; else { address = (address & ~7) | ((address + 1) & 7); }
                break;
        }
    }

    private void ClockFalling()
    {
        switch (state)
        {
            case St.Address:
                if (bits == 8)
                {
                    address = (shift >> 1) & 0x7F; reading = (shift & 1) != 0;
                    state = St.AckAddress; slaveSda = false;
                }
                break;
            case St.AckAddress:
                slaveSda = true;
                if (reading) { state = St.ReadData; bits = 0; shift = mem[address]; PresentReadBit(); }
                else { state = St.WriteData; bits = 0; shift = 0; }
                break;
            case St.WriteData:
                if (bits == 8)
                {
                    if (mem[address] != (byte)shift) { mem[address] = (byte)shift; Dirty = true; }
                    address = (address & ~7) | ((address + 1) & 7);
                    state = St.AckWrite; slaveSda = false;
                }
                break;
            case St.AckWrite:
                slaveSda = true; state = St.WriteData; bits = 0; shift = 0;
                break;
            case St.ReadData:
                bits++;
                if (bits == 8) { slaveSda = true; state = St.MasterAck; }
                else PresentReadBit();
                break;
            case St.MasterAck:
                // after the acknowledge clock: the next byte starts (if the program acknowledged, ClockRising moved the address on)
                if (state == St.MasterAck) { state = St.ReadData; bits = 0; shift = mem[address]; PresentReadBit(); }
                break;
        }
    }

    private void PresentReadBit() => slaveSda = ((shift >> (7 - bits)) & 1) != 0;

    public void SaveState(BinaryWriter w)
    {
        w.Write(mem); w.Write(scl); w.Write(masterSda); w.Write(slaveSda); w.Write((int)state); w.Write(bits); w.Write(shift); w.Write(address); w.Write(reading);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(mem, 0, mem.Length);
        scl = r.ReadBoolean(); masterSda = r.ReadBoolean(); slaveSda = r.ReadBoolean(); state = (St)r.ReadInt32(); bits = r.ReadInt32(); shift = r.ReadInt32(); address = r.ReadInt32(); reading = r.ReadBoolean();
    }
}
