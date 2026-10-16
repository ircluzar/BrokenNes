using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// A 93C46-style serial EEPROM (64 words of 16 bits, the Microwire interface): four lines, chip select CS, clock SK, data in DI and data out DO, written from the part's public datasheet. Mega Man: The Wily Wars
/// (Rockman Megaworld) carries one on bits 2, 1 and 0 of the byte at $200001 (CS, SK, DI) and reads DO in bit 0 of the same byte (found by tracing the game: after a leading run of three zero bits it sends the start bit, a
/// two-bit opcode and a six-bit word address, and reads sixteen data bits after the rising clock edges that follow).
/// </summary>
/// <remarks>
/// <para>Protocol: with CS high, every rising edge of SK takes in the level of DI. Zeros before the first 1 are ignored; the 1 is the start bit. Then two opcode bits: <c>10</c> READ, <c>01</c> WRITE, <c>11</c> ERASE and
/// <c>00</c> followed by two more bits that choose EWDS (<c>00</c>), WRAL (<c>01</c>), ERAL (<c>10</c>) or EWEN (<c>11</c>) (and four don't-care bits). READ, WRITE and ERASE then take the six-bit word address; WRITE and WRAL
/// take sixteen data bits, most significant first. READ answers with a dummy zero bit on DO after the last address bit's rising edge and then a data bit per rising edge, the next word following the last bit of the one before.
/// Programming commands do nothing until a write enable (EWEN) has been given; the chip powers up with writing disabled. The programming cycle starts when CS falls after the last bit and only then, and only if the command
/// was complete; it is instantaneous here (a real part takes milliseconds, during which DO reads busy), so DO reads ready (1) whenever CS is high and the chip is not shifting data out. With CS low DO is not driven and
/// reads as 1 (the cartridge's pull-up). A blank chip reads $FFFF.</para>
/// </remarks>
public sealed class MdMicrowire
{
    public const int Words = 64;
    /// <summary>The 128 bytes of the chip, word n at bytes 2n (high) and 2n+1: the battery image.</summary>
    public byte[] Data { get; } = new byte[Words * 2];

    private bool cs, sk, di;
    private enum St { Idle, Start, Op, SubOp, Address, WriteData, Reading, Done }
    private St state = St.Idle;
    private int opBits, op, subBits, sub, addrBits, addr, dataBits, dataShift;
    private bool writeEnabled, wral;
    private bool dataOut = true;           // DO while CS is high
    private int readWord, readBit;         // the word being shifted out (the address of it) and the next bit (15 down to 0), -1 = the dummy bit is next
    private bool pendingProgram;           // a complete write or erase command waits for CS to fall
    private int pendingKind, pendingAddr, pendingData;

    // diagnostics
    public long Commands, Reads, Writes, Erases;
    public int Transactions => (int)Commands;

    public MdMicrowire() { Array.Fill(Data, (byte)0xFF); }

    /// <summary>DO as the 68000 reads it.</summary>
    public bool DoLine => !cs || dataOut;
    public bool WriteEnabled => writeEnabled;

    private int Word(int a) => Data[a * 2] << 8 | Data[a * 2 + 1];
    private void SetWord(int a, int v) { Data[a * 2] = (byte)(v >> 8); Data[a * 2 + 1] = (byte)v; }

    /// <summary>The 68000 side sets the three lines (the cartridge calls this after a write to the byte that carries them).</summary>
    public void Drive(bool newCs, bool newSk, bool newDi)
    {
        di = newDi;
        if (newCs != cs)
        {
            cs = newCs;
            if (cs) { state = St.Start; opBits = subBits = addrBits = dataBits = 0; dataOut = true; pendingProgram = false; wral = false; }
            else FallingCs();
        }
        if (newSk != sk)
        {
            sk = newSk;
            if (sk && cs) Rising();
        }
    }

    private void FallingCs()
    {
        if (pendingProgram)
        {
            Commands++;
            if (pendingKind == 1) { SetWord(pendingAddr, pendingData); Writes++; }
            else if (pendingKind == 2) { SetWord(pendingAddr, 0xFFFF); Erases++; }
            else if (pendingKind == 3) { for (int a = 0; a < Words; a++) SetWord(a, 0xFFFF); Erases++; }
            else if (pendingKind == 4) { for (int a = 0; a < Words; a++) SetWord(a, pendingData); Writes++; }
            pendingProgram = false;
        }
        state = St.Idle; dataOut = true;
    }

    private void Rising()
    {
        switch (state)
        {
            case St.Start:
                if (di) { state = St.Op; opBits = 0; op = 0; }
                break;
            case St.Op:
                op = op << 1 | (di ? 1 : 0);
                if (++opBits == 2) { if (op == 0) { state = St.SubOp; subBits = 0; sub = 0; } else { state = St.Address; addrBits = 0; addr = 0; } }
                break;
            case St.SubOp:
                sub = sub << 1 | (di ? 1 : 0);
                if (++subBits == 2) { state = St.Address; addrBits = 0; addr = 0; }       // the four bits after it are don't-care address bits
                break;
            case St.Address:
                addr = addr << 1 | (di ? 1 : 0);
                if (++addrBits == (op == 0 ? 4 : 6)) AddressDone();
                break;
            case St.WriteData:
                dataShift = dataShift << 1 | (di ? 1 : 0);
                if (++dataBits == 16)
                {
                    pendingProgram = writeEnabled;
                    pendingKind = wral ? 4 : 1; pendingAddr = addr & 63; pendingData = dataShift & 0xFFFF;
                    state = St.Done;
                }
                break;
            case St.Reading:
                ShiftOut();      // the bit now on DO is the one this edge shifted out
                break;
        }
    }

    private void ShiftOut()
    {
        // DO shows bit readBit of readWord after this edge; the next edge moves on (after bit 0 comes bit 15 of the next word)
        dataOut = ((Word(readWord) >> readBit) & 1) != 0;
        if (readBit == 0) { readBit = 15; readWord = (readWord + 1) & 63; Reads++; }
        else readBit--;
    }

    private void AddressDone()
    {
        if (op == 2)           // READ: a dummy zero on DO now, the word's bits on the edges that follow
        {
            Commands++;
            readWord = addr & 63; readBit = 15; dataOut = false; state = St.Reading;      // the next edge shows bit 15 of the word
        }
        else if (op == 1) { dataBits = 0; dataShift = 0; state = St.WriteData; }
        else if (op == 3)      // ERASE
        {
            if (writeEnabled) { pendingProgram = true; pendingKind = 2; pendingAddr = addr & 63; }
            state = St.Done;
        }
        else                   // 00: the sub-opcode picks the command
        {
            switch (sub)
            {
                case 0: writeEnabled = false; Commands++; state = St.Done; break;                                                  // EWDS
                case 3: writeEnabled = true; Commands++; state = St.Done; break;                                                   // EWEN
                case 2: if (writeEnabled) { pendingProgram = true; pendingKind = 3; } state = St.Done; break;                        // ERAL
                case 1: dataBits = 0; dataShift = 0; wral = true; state = St.WriteData; break;                                        // WRAL: 16 data bits follow
            }
        }
    }

    public void Reset() { cs = sk = di = false; state = St.Idle; dataOut = true; pendingProgram = false; writeEnabled = false; }

    // ---- save state

    public void SaveState(BinaryWriter w)
    {
        w.Write(Data);
        w.Write(cs); w.Write(sk); w.Write(di); w.Write((int)state);
        w.Write(opBits); w.Write(op); w.Write(subBits); w.Write(sub); w.Write(addrBits); w.Write(addr); w.Write(dataBits); w.Write(dataShift);
        w.Write(writeEnabled); w.Write(wral); w.Write(dataOut); w.Write(readWord); w.Write(readBit); w.Write(pendingProgram); w.Write(pendingKind); w.Write(pendingAddr); w.Write(pendingData);
        w.Write(Commands); w.Write(Reads); w.Write(Writes); w.Write(Erases);
    }

    public void LoadState(BinaryReader r)
    {
        MdVdp.ReadAll(r, Data);
        cs = r.ReadBoolean(); sk = r.ReadBoolean(); di = r.ReadBoolean(); state = (St)r.ReadInt32();
        opBits = r.ReadInt32(); op = r.ReadInt32(); subBits = r.ReadInt32(); sub = r.ReadInt32(); addrBits = r.ReadInt32(); addr = r.ReadInt32(); dataBits = r.ReadInt32(); dataShift = r.ReadInt32();
        writeEnabled = r.ReadBoolean(); wral = r.ReadBoolean(); dataOut = r.ReadBoolean(); readWord = r.ReadInt32(); readBit = r.ReadInt32(); pendingProgram = r.ReadBoolean(); pendingKind = r.ReadInt32(); pendingAddr = r.ReadInt32(); pendingData = r.ReadInt32();
        Commands = r.ReadInt64(); Reads = r.ReadInt64(); Writes = r.ReadInt64(); Erases = r.ReadInt64();
    }
}
