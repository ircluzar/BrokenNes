using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>The serial EEPROM chips Genesis cartridges carry (all two-wire, I2C-style): the 1 kbit part with a seven-bit address in its first byte, and the 24Cxx family with a device-select byte.</summary>
public enum MdEepromChip
{
    /// <summary>X24C01 / 24C01: 128 bytes; the first byte after START is a 7-bit address and the read/write bit.</summary>
    X24C01,
    /// <summary>24C02: 256 bytes, one word-address byte.</summary>
    C24C02,
    /// <summary>24C04: 512 bytes, one page bit in the device byte.</summary>
    C24C04,
    /// <summary>24C08: 1 KB, two page bits.</summary>
    C24C08,
    /// <summary>24C16: 2 KB, three page bits.</summary>
    C24C16,
    /// <summary>24C32: 4 KB, two word-address bytes.</summary>
    C24C32,
    /// <summary>24C64: 8 KB, two word-address bytes.</summary>
    C24C64,
}

/// <summary>
/// A serial EEPROM on two lines, SDA (open drain, shared by the 68000 side and the chip) and SCL (driven by the 68000 side only), written from the public datasheets of the X24C01 and the
/// 24C02-24C64 family. The cartridge connects the lines to bits of a few addresses (<see cref="MdCartridge"/> knows which); this class only sees the two levels.
/// </summary>
/// <remarks>
/// <para>Protocol (all parts): START is SDA falling while SCL is high, STOP is SDA rising while SCL is high; data is sampled on SCL's rising edge and the chip changes SDA only after SCL has fallen; every byte
/// is followed by a ninth clock on which the receiver pulls SDA low (ACK). The first byte after START selects the part and the direction: 1010 A2 A1 A0 R/W for the 24Cxx family (for the 04, 08 and 16 the
/// address pins A0-A2 are replaced by page bits 8-10 of the byte address), the X24C01 has seven address bits then R/W. A write sends the word address (one or two bytes; none on the X24C01, it is in the first
/// byte) and data bytes that are held in a page buffer and committed at STOP (the address wraps inside the page: 4 bytes for the X24C01, 8 for the 24C02/04, 16 for the 08/16, 32 for the 32/64). A read after a write's
/// address (a repeated START) or a "current address" read returns bytes from the address pointer, which advances after each byte the master acknowledges, wrapping at the end of the array.</para>
/// <para>The internal write cycle is instantaneous here (a real chip takes milliseconds and does not acknowledge meanwhile): a game that polls for the acknowledge sees it at once. A blank chip reads $FF.</para>
/// </remarks>
public sealed class MdEeprom
{
    public MdEepromChip Chip { get; }
    public byte[] Data { get; }
    private readonly int pageSize, wordAddressBytes;
    private readonly bool pageBits;

    private bool sda = true, scl = true;      // the levels the 68000 side drives (idle: both released high)
    private bool chipSda = true;               // what the chip drives (true = released)

    private enum St { Idle, Device, WordHigh, Word, WriteData, AckSent, Read, ReadAck }
    private St state = St.Idle;
    private St afterAck = St.Idle;             // where the transfer goes once the chip's acknowledge clock has finished
    private int shift, bitCount;               // bits received so far in the current byte
    private int address;                       // byte address pointer
    private int devicePage;                    // page bits (8 and up) of the last device byte (24C04/08/16)
    private readonly byte[] pageBuffer = new byte[64];
    private int pageCount, pageStart;
    private bool writing;                      // a write transfer reached its data phase
    private int readByte, rdBit;               // the byte being shifted out and the next bit of it
    private bool rdAcked;

    // diagnostics for the sweep and the traces
    public long Starts, Stops, BytesIn, BytesOut, Commits;
    public int LastDevice = -1;
    /// <summary>How many times each device-select byte was sent, and the highest byte address read or committed (which part a game really talks to shows here).</summary>
    public readonly int[] DeviceHistogram = new int[256];
    public int MaxAddress = -1;

    public MdEeprom(MdEepromChip chip)
    {
        Chip = chip;
        (int size, int page, int wab) = chip switch
        {
            MdEepromChip.X24C01 => (128, 4, 0),
            MdEepromChip.C24C02 => (256, 8, 1),
            MdEepromChip.C24C04 => (512, 8, 1),
            MdEepromChip.C24C08 => (1024, 16, 1),
            MdEepromChip.C24C16 => (2048, 16, 1),
            MdEepromChip.C24C32 => (4096, 32, 2),
            _ => (8192, 32, 2),
        };
        Data = new byte[size];
        Array.Fill(Data, (byte)0xFF);
        pageSize = page; wordAddressBytes = wab;
        pageBits = chip is MdEepromChip.C24C04 or MdEepromChip.C24C08 or MdEepromChip.C24C16;
    }

    /// <summary>The level on SDA as the 68000 side reads it: the wired AND of its own drive and the chip's.</summary>
    public bool SdaLine => sda && chipSda;

    /// <summary>The chip's own drive of SDA (true = released, so the line follows the master).</summary>
    public bool ChipSda => chipSda;

    /// <summary>The 68000 side's current drive of the lines.</summary>
    public bool MasterSda => sda;
    public bool MasterScl => scl;

    /// <summary>The 68000 side sets SDA and SCL (the cartridge calls this after any write that touches either).</summary>
    public void Drive(bool newSda, bool newScl)
    {
        // when both change in one write: SDA first while SCL still has its old level (a START or STOP needs SCL high, a data change needs it low), then SCL
        if (newSda != sda)
        {
            bool oldSda = sda;
            sda = newSda;
            if (scl)
            {
                if (oldSda && !newSda) Start();
                else if (!oldSda && newSda) Stop();
            }
        }
        if (newScl != scl)
        {
            scl = newScl;
            if (scl) Rising(); else Falling();
        }
    }

    private void Start()
    {
        Starts++;
        if (writing && pageCount > 0) Commit();
        state = St.Device; shift = 0; bitCount = 0; chipSda = true; writing = false; pageCount = 0;
    }

    private void Stop()
    {
        Stops++;
        if (writing && pageCount > 0) Commit();
        state = St.Idle; chipSda = true; writing = false; pageCount = 0;
    }

    private void Commit()
    {
        // the bytes land from the page's start address on, wrapping inside the page
        int a = pageStart;
        for (int i = 0; i < pageCount; i++)
        {
            if (a > MaxAddress) MaxAddress = a;
            Data[a % Data.Length] = pageBuffer[i];
            a = a & ~(pageSize - 1) | a + 1 & pageSize - 1;
        }
        address = a % Data.Length;
        pageCount = 0;
        Commits++;
    }

    /// <summary>SCL rising: sample SDA into the byte being received; during a read, sample the master's acknowledge after the eighth bit.</summary>
    private void Rising()
    {
        switch (state)
        {
            case St.Device: case St.WordHigh: case St.Word: case St.WriteData:
                if (bitCount < 8) { shift = shift << 1 | (sda ? 1 : 0); bitCount++; }
                break;
            case St.ReadAck:
                rdAcked = !sda;
                break;
        }
    }

    /// <summary>SCL falling: the chip changes its output (acknowledges a received byte, shifts out the next data bit).</summary>
    private void Falling()
    {
        switch (state)
        {
            case St.Device:
                if (bitCount == 8) DeviceByte(shift & 0xFF);
                break;
            case St.WordHigh:
                if (bitCount == 8)
                {
                    BytesIn++; address = (shift & 0xFF) << 8; Acknowledge(St.Word);
                }
                break;
            case St.Word:
                if (bitCount == 8)
                {
                    BytesIn++;
                    address = ((wordAddressBytes == 2 ? address & 0xFF00 : devicePage) | shift & 0xFF) % Data.Length;
                    pageStart = address; pageCount = 0; writing = true;
                    Acknowledge(St.WriteData);
                }
                break;
            case St.WriteData:
                if (bitCount == 8)
                {
                    BytesIn++;
                    if (pageCount < pageBuffer.Length) pageBuffer[pageCount++] = (byte)shift;
                    Acknowledge(St.WriteData);
                }
                break;
            case St.AckSent:
                // the ninth clock has finished: release SDA and carry on
                chipSda = true;
                state = afterAck; shift = 0; bitCount = 0;
                if (state == St.Read) { rdBit = 0; readByte = Data[address % Data.Length]; BytesOut++; if (address > MaxAddress) MaxAddress = address; chipSda = (readByte & 0x80) != 0; }
                break;
            case St.Read:
                // the master has clocked bit rdBit in: present the next one, or release SDA for the master's acknowledge
                rdBit++;
                if (rdBit < 8) chipSda = (readByte >> 7 - rdBit & 1) != 0;
                else { chipSda = true; state = St.ReadAck; rdAcked = false; }
                break;
            case St.ReadAck:
                if (rdAcked)
                {
                    address = (address + 1) % Data.Length;
                    rdBit = 0; readByte = Data[address]; BytesOut++; chipSda = (readByte & 0x80) != 0; state = St.Read;
                }
                else { state = St.Idle; chipSda = true; }
                break;
        }
    }

    private void Acknowledge(St next)
    {
        chipSda = false; afterAck = next; state = St.AckSent; shift = 0; bitCount = 0;
    }

    private void DeviceByte(int b)
    {
        LastDevice = b; DeviceHistogram[b & 0xFF]++;
        BytesIn++;
        bool rd = (b & 1) != 0;
        if (Chip == MdEepromChip.X24C01)
        {
            address = b >> 1 & 0x7F;
            if (rd) Acknowledge(St.Read);
            else { pageStart = address; pageCount = 0; writing = true; Acknowledge(St.WriteData); }
            return;
        }
        if ((b & 0xF0) != 0xA0) { chipSda = true; state = St.Idle; return; }      // another part's address: no acknowledge
        if (pageBits)
        {
            int bitsMask = Chip switch { MdEepromChip.C24C04 => 1, MdEepromChip.C24C08 => 3, _ => 7 };
            devicePage = (b >> 1 & bitsMask) << 8;
            address = devicePage | address & 0xFF;
        }
        if (rd) Acknowledge(St.Read);
        else Acknowledge(wordAddressBytes == 2 ? St.WordHigh : St.Word);
    }

    public void Reset()
    {
        sda = scl = true; chipSda = true; state = St.Idle; writing = false; pageCount = 0; bitCount = 0; shift = 0;
    }

    public void SaveState(BinaryWriter w)
    {
        w.Write(Data);
        w.Write(sda); w.Write(scl); w.Write(chipSda); w.Write((int)state); w.Write((int)afterAck); w.Write(shift); w.Write(bitCount); w.Write(address); w.Write(devicePage);
        w.Write(pageCount); w.Write(pageStart); w.Write(writing); w.Write(readByte); w.Write(rdBit); w.Write(rdAcked);
        w.Write(pageBuffer);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(Data, 0, Data.Length);
        sda = r.ReadBoolean(); scl = r.ReadBoolean(); chipSda = r.ReadBoolean(); state = (St)r.ReadInt32(); afterAck = (St)r.ReadInt32(); shift = r.ReadInt32(); bitCount = r.ReadInt32(); address = r.ReadInt32(); devicePage = r.ReadInt32();
        pageCount = r.ReadInt32(); pageStart = r.ReadInt32(); writing = r.ReadBoolean(); readByte = r.ReadInt32(); rdBit = r.ReadInt32(); rdAcked = r.ReadBoolean();
        r.Read(pageBuffer, 0, pageBuffer.Length);
    }
}
