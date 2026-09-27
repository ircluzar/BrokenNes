using System;
using System.IO;
using System.Text;

namespace NesEmulator.Gb;

public enum GbModel { Dmg, Cgb }

/// <summary>
/// A Game Boy cartridge: ROM, optional RAM, and the memory bank controller that maps them.
/// Supported: ROM only, MBC1 (and MBC1 multicarts), MBC2, MBC3 (with the real-time clock), MBC5 (with rumble).
/// Header layout and controller behaviour follow Pan Docs "The Cartridge Header" / "MBCs".
/// </summary>
public sealed class GbCartridge
{
    public readonly byte[] Rom;
    public readonly byte[] Ram;
    public readonly string Title;
    public readonly byte TypeCode;
    /// <summary>Header $143: $80 = works on both, $C0 = Game Boy Color only.</summary>
    public readonly byte CgbFlag;
    public readonly bool HasBattery, HasRtc, HasRumble;
    public readonly string MapperName;
    private readonly GbMapper mapper;

    public bool SupportsCgb => (CgbFlag & 0x80) != 0;
    public bool RumbleOn => mapper is Mbc5 m && m.Rumble;
    public GbRtc? Rtc => (mapper as Mbc3)?.Clock;
    /// <summary>MBC7 carts (Kirby Tilt 'n Tumble) carry an accelerometer.</summary>
    public bool HasTilt => mapper is Mbc7;
    /// <summary>Accelerometer input for MBC7 carts, in g: +X = tilted right, +Y = tilted towards the player.</summary>
    public void SetTilt(float x, float y) { if (mapper is Mbc7 m) { m.TiltX = x; m.TiltY = y; } }

    private GbCartridge(byte[] rom)
    {
        if (rom.Length < 0x150) throw new InvalidDataException("Not a Game Boy ROM: shorter than the cartridge header.");
        // Pad to a power of two of at least 32 KiB so bank masks work on odd dumps.
        int size = 0x8000; while (size < rom.Length) size <<= 1;
        Rom = new byte[size]; Array.Copy(rom, Rom, rom.Length);
        for (int i = rom.Length; i < size; i++) Rom[i] = 0xFF;

        CgbFlag = rom[0x143];
        int titleLen = (CgbFlag & 0x80) != 0 ? 15 : 16;
        var sb = new StringBuilder();
        for (int i = 0; i < titleLen; i++) { byte c = rom[0x134 + i]; if (c == 0) break; sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '?'); }
        Title = sb.ToString().Trim();
        TypeCode = rom[0x147];

        int ramSize = rom[0x149] switch { 1 => 0x800, 2 => 0x2000, 3 => 0x8000, 4 => 0x20000, 5 => 0x10000, _ => 0 };
        switch (TypeCode)
        {
            case 0x00 when rom.Length > 0x8000:
                // Unlicensed Wisdom Tree boards say "ROM only" but carry up to 1 MiB, switched 32 KiB at a time.
                mapper = new WisdomTree(this); MapperName = "WISDOM TREE"; break;
            case 0x00: case 0x08: case 0x09: mapper = new RomOnly(this); MapperName = "ROM"; HasBattery = TypeCode == 0x09; break;
            case 0x01: case 0x02: case 0x03:
                bool multi = Mbc1.LooksLikeMulticart(Rom);
                mapper = new Mbc1(this, multi); MapperName = multi ? "MBC1M" : "MBC1"; HasBattery = TypeCode == 0x03; break;
            case 0x05: case 0x06: mapper = new Mbc2(this); MapperName = "MBC2"; HasBattery = TypeCode == 0x06; ramSize = 512; break;
            case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13:
                HasRtc = TypeCode is 0x0F or 0x10; HasBattery = TypeCode is 0x0F or 0x10 or 0x13;
                mapper = new Mbc3(this, HasRtc); MapperName = HasRtc ? "MBC3+RTC" : "MBC3"; break;
            case 0x19: case 0x1A: case 0x1B: case 0x1C: case 0x1D: case 0x1E:
                HasRumble = TypeCode >= 0x1C; HasBattery = TypeCode is 0x1B or 0x1E;
                mapper = new Mbc5(this, HasRumble); MapperName = HasRumble ? "MBC5+RUMBLE" : "MBC5"; break;
            case 0x22: mapper = new Mbc7(this); MapperName = "MBC7 (tilt)"; HasBattery = true; ramSize = 256; break;
            case 0xFE: mapper = new Huc3(this); MapperName = "HuC-3"; HasBattery = true; HasRtc = true; break;
            case 0xFF: mapper = new Huc1(this); MapperName = "HuC-1"; HasBattery = true; break;
            default:
                throw new NotSupportedException($"Cartridge type ${TypeCode:X2} is not supported yet.");
        }
        Ram = new byte[ramSize];
        for (int i = 0; i < Ram.Length; i++) Ram[i] = 0xFF;
    }

    public static GbCartridge Load(byte[] rom) => new(rom);

    public byte ReadRom(ushort a) => mapper.ReadRom(a);
    public void WriteRom(ushort a, byte v) => mapper.WriteRom(a, v);
    public byte ReadRam(ushort a) => mapper.ReadRam(a);
    public void WriteRam(ushort a, byte v) => mapper.WriteRam(a, v);
    /// <summary>Advance the cartridge's own clock (MBC3 RTC) by T-cycles at the 4.194304 MHz base rate.</summary>
    public void AdvanceClock(int tCycles) { (mapper as Mbc3)?.Clock?.Advance(tCycles); (mapper as Huc3)?.Advance(tCycles); }

    public void SaveState(BinaryWriter w) { w.Write(Ram.Length); w.Write(Ram); mapper.SaveState(w); }
    public void LoadState(BinaryReader r) { int n = r.ReadInt32(); r.ReadBytes(n).CopyTo(Ram, 0); mapper.LoadState(r); }

    // =================================================================================== mappers
    private abstract class GbMapper
    {
        protected readonly GbCartridge Cart;
        protected readonly int RomMask, RamMask;
        protected GbMapper(GbCartridge c) { Cart = c; RomMask = c.Rom.Length / 0x4000 - 1; RamMask = Math.Max(c.Ram == null ? 0 : 0, 0); }
        public abstract byte ReadRom(ushort a);
        public abstract void WriteRom(ushort a, byte v);
        public abstract byte ReadRam(ushort a);
        public abstract void WriteRam(ushort a, byte v);
        public virtual void SaveState(BinaryWriter w) { }
        public virtual void LoadState(BinaryReader r) { }

        protected byte RomAt(int bank, ushort a) => Cart.Rom[((bank & RomMask) << 14) | (a & 0x3FFF)];
        protected byte RamAt(int bank, ushort a)
        {
            var ram = Cart.Ram; if (ram.Length == 0) return 0xFF;
            return ram[((bank << 13) | (a & 0x1FFF)) % ram.Length];
        }
        protected void RamSet(int bank, ushort a, byte v)
        {
            var ram = Cart.Ram; if (ram.Length == 0) return;
            ram[((bank << 13) | (a & 0x1FFF)) % ram.Length] = v;
        }
    }

    private sealed class RomOnly : GbMapper
    {
        public RomOnly(GbCartridge c) : base(c) { }
        public override byte ReadRom(ushort a) => Cart.Rom[a & 0x7FFF];
        public override void WriteRom(ushort a, byte v) { }
        public override byte ReadRam(ushort a) => RamAt(0, a);
        public override void WriteRam(ushort a, byte v) => RamSet(0, a, v);
    }

    private sealed class WisdomTree : GbMapper
    {
        private int bank;
        public WisdomTree(GbCartridge c) : base(c) { }
        // A write to $0000-$3FFF selects the 32 KiB bank named by the low byte of the address.
        public override byte ReadRom(ushort a) => Cart.Rom[((bank << 15) | (a & 0x7FFF)) & (Cart.Rom.Length - 1)];
        public override void WriteRom(ushort a, byte v) { if (a < 0x4000) bank = a & 0xFF; }
        public override byte ReadRam(ushort a) => 0xFF;
        public override void WriteRam(ushort a, byte v) { }
        public override void SaveState(BinaryWriter w) => w.Write(bank);
        public override void LoadState(BinaryReader r) => bank = r.ReadInt32();
    }

    /// <summary>Hudson HuC-1: MBC1-like banking plus an infrared port (no partner, so it never sees light).</summary>
    private sealed class Huc1 : GbMapper
    {
        private bool ir; private int romBank = 1, ramBank;
        public Huc1(GbCartridge c) : base(c) { }
        public override byte ReadRom(ushort a) => a < 0x4000 ? RomAt(0, a) : RomAt(romBank, a);
        public override void WriteRom(ushort a, byte v)
        {
            switch (a >> 13)
            {
                case 0: ir = (v & 0x0F) == 0x0E; break;
                case 1: romBank = v & 0x3F; if (romBank == 0) romBank = 1; break;
                case 2: ramBank = v & 3; break;
            }
        }
        public override byte ReadRam(ushort a) => ir ? (byte)0xC0 : RamAt(ramBank, a);
        public override void WriteRam(ushort a, byte v) { if (!ir) RamSet(ramBank, a, v); }
        public override void SaveState(BinaryWriter w) { w.Write(ir); w.Write(romBank); w.Write(ramBank); }
        public override void LoadState(BinaryReader r) { ir = r.ReadBoolean(); romBank = r.ReadInt32(); ramBank = r.ReadInt32(); }
    }

    /// <summary>
    /// Hudson HuC-3: banking, RAM, infrared and a clock reached through a nibble-wide command port
    /// ($0B = command, $0C = response, $0D = status). The clock keeps minutes-of-day and a day count.
    /// </summary>
    private sealed class Huc3 : GbMapper
    {
        private int mode, romBank = 1, ramBank, value, address;
        private readonly byte[] mem = new byte[256];   // clock nibble memory; $00-$02 minutes, $03-$05 days
        private long sub;
        public Huc3(GbCartridge c) : base(c) { }
        public void Advance(int t)
        {
            sub += t;
            while (sub >= 4194304L * 60)
            {
                sub -= 4194304L * 60;
                int min = mem[0] | mem[1] << 4 | mem[2] << 8, day = mem[3] | mem[4] << 4 | mem[5] << 8;
                if (++min >= 1440) { min = 0; day = (day + 1) & 0xFFF; }
                mem[0] = (byte)(min & 15); mem[1] = (byte)(min >> 4 & 15); mem[2] = (byte)(min >> 8 & 15);
                mem[3] = (byte)(day & 15); mem[4] = (byte)(day >> 4 & 15); mem[5] = (byte)(day >> 8 & 15);
            }
        }
        public override byte ReadRom(ushort a) => a < 0x4000 ? RomAt(0, a) : RomAt(romBank, a);
        public override void WriteRom(ushort a, byte v)
        {
            switch (a >> 13)
            {
                case 0: mode = v & 0x0F; break;
                case 1: romBank = v & 0x7F; if (romBank == 0) romBank = 1; break;
                case 2: ramBank = v & 3; break;
            }
        }
        public override byte ReadRam(ushort a) => mode switch
        {
            0x0 or 0xA => RamAt(ramBank, a),
            0xC => (byte)(0x80 | value),
            0xD => 0x01,                    // the clock is always ready
            0xE => 0xC0,                    // infrared: dark
            _ => 0xFF,
        };
        public override void WriteRam(ushort a, byte v)
        {
            if (mode == 0xA) { RamSet(ramBank, a, v); return; }
            if (mode != 0xB) return;
            int arg = v & 0x0F;
            switch (v >> 4 & 7)
            {
                case 1: value = mem[address] & 15; address = (address + 1) & 0xFF; break;   // read and advance
                case 3: mem[address] = (byte)arg; address = (address + 1) & 0xFF; break;   // write and advance
                case 4: address = (address & 0xF0) | arg; break;
                case 5: address = (address & 0x0F) | arg << 4; break;
                case 6: value = 1; break;                                                  // extended commands: acknowledge
            }
        }
        public override void SaveState(BinaryWriter w) { w.Write(mode); w.Write(romBank); w.Write(ramBank); w.Write(value); w.Write(address); w.Write(mem); w.Write(sub); }
        public override void LoadState(BinaryReader r) { mode = r.ReadInt32(); romBank = r.ReadInt32(); ramBank = r.ReadInt32(); value = r.ReadInt32(); address = r.ReadInt32(); r.ReadBytes(256).CopyTo(mem, 0); sub = r.ReadInt64(); }
    }

    /// <summary>
    /// MBC7: banking, a two-axis accelerometer and a 93LC56 serial EEPROM (128 x 16-bit words, kept in
    /// <see cref="Ram"/> so battery saves work as usual). Both enable latches must be set to reach $A000.
    /// </summary>
    private sealed class Mbc7 : GbMapper
    {
        public float TiltX, TiltY;
        private bool en1, en2; private int romBank = 1;
        private int latchedX = 0x8000, latchedY = 0x8000;
        // EEPROM serial state
        private bool cs, clk, di, doBit = true, writeEnable;
        private int shift, bits, readWord = -1, readBits, command = -1;
        public Mbc7(GbCartridge c) : base(c) { }
        private static readonly bool Log = Environment.GetEnvironmentVariable("GB_MBC7_LOG") == "1";
        public override byte ReadRom(ushort a) => a < 0x4000 ? RomAt(0, a) : RomAt(romBank, a);
        public override void WriteRom(ushort a, byte v)
        {
            if (a < 0x2000) en1 = v == 0x0A;
            else if (a < 0x4000) romBank = v & 0x7F;
            else if (a < 0x6000) en2 = v == 0x40;
        }
        public override byte ReadRam(ushort a)
        {
            if (!en1 || !en2 || a >= 0xB000) return 0xFF;
            return ((a >> 4) & 0x0F) switch
            {
                2 => (byte)latchedX, 3 => (byte)(latchedX >> 8), 4 => (byte)latchedY, 5 => (byte)(latchedY >> 8),
                6 => 0x00,
                8 => (byte)((cs ? 0x80 : 0) | (clk ? 0x40 : 0) | (di ? 0x02 : 0) | (doBit ? 1 : 0)),
                _ => 0xFF,
            };
        }
        public override void WriteRam(ushort a, byte v)
        {
            if (!en1 || !en2 || a >= 0xB000) return;
            switch ((a >> 4) & 0x0F)
            {
                case 0: if (v == 0x55) { latchedX = latchedY = 0x8000; } break;
                case 1:
                    if (v == 0xAA && latchedX == 0x8000)
                    {
                        // 1 g is about 0x70 counts either side of the 0x81D0 centre.
                        latchedX = Math.Clamp(0x81D0 + (int)(TiltX * 0x70), 0, 0xFFFF);
                        latchedY = Math.Clamp(0x81D0 + (int)(TiltY * 0x70), 0, 0xFFFF);
                    }
                    break;
                case 8: EepromPins((v & 0x80) != 0, (v & 0x40) != 0, (v & 0x02) != 0); break;
            }
        }

        private int Word(int i) => Cart.Ram[i * 2] << 8 | Cart.Ram[i * 2 + 1];
        private void SetWord(int i, int w) { Cart.Ram[i * 2] = (byte)(w >> 8); Cart.Ram[i * 2 + 1] = (byte)w; }

        private void EepromPins(bool newCs, bool newClk, bool newDi)
        {
            if (!newCs) { cs = false; clk = newClk; di = newDi; shift = 0; bits = 0; readWord = -1; command = -1; return; }
            bool rising = newCs && cs && !clk && newClk;
            cs = true; clk = newClk; di = newDi;
            if (!rising) return;

            if (readWord >= 0)
            {
                // Streaming a READ: the dummy 0 is already on DO; each clock then shifts out one data bit, MSB first,
                // and sequential reads continue into the next word.
                doBit = (Word(readWord) >> (16 - readBits) & 1) != 0;
                if (++readBits > 16) { readBits = 1; readWord = (readWord + 1) & 0x7F; }
                return;
            }
            if (command < 0 && bits == 0 && !di) return;   // wait for the start bit (data words may begin with 0)
            shift = shift << 1 | (di ? 1 : 0); bits++;
            if (command < 0 && bits == 11)
            {
                int op = shift >> 8 & 3, addr = shift & 0x7F;
                if (Log) Console.Error.WriteLine($"EEPROM cmd op={op} addr={addr:X2} raw={shift:X3} we={writeEnable}");
                switch (op)
                {
                    case 2: readWord = addr; readBits = 1; doBit = false; shift = 0; bits = 0; return;           // READ
                    case 3: if (writeEnable) SetWord(addr, 0xFFFF); Done(); return;                            // ERASE
                    case 1: command = 1 << 8 | addr; shift = 0; bits = 0; return;                               // WRITE: 16 data bits follow
                    default:
                        switch (shift >> 6 & 3)
                        {
                            case 3: writeEnable = true; Done(); return;                                          // EWEN
                            case 0: writeEnable = false; Done(); return;                                         // EWDS
                            case 2: if (writeEnable) for (int i = 0; i < 128; i++) SetWord(i, 0xFFFF); Done(); return;   // ERAL
                            default: command = 2 << 8; shift = 0; bits = 0; return;                               // WRAL: 16 data bits
                        }
                }
            }
            if (command >= 0 && bits == 16)
            {
                if (Log) Console.Error.WriteLine($"EEPROM data {(command >> 8 == 1 ? "WRITE" : "WRAL")} addr={command & 0x7F:X2} value={shift & 0xFFFF:X4}");
                if (writeEnable)
                {
                    if (command >> 8 == 1) SetWord(command & 0x7F, shift & 0xFFFF);
                    else for (int i = 0; i < 128; i++) SetWord(i, shift & 0xFFFF);
                }
                Done();
            }
        }

        private void Done() { shift = 0; bits = 0; command = -1; doBit = true; }   // ready

        public override void SaveState(BinaryWriter w)
        {
            w.Write(en1); w.Write(en2); w.Write(romBank); w.Write(latchedX); w.Write(latchedY);
            w.Write(cs); w.Write(clk); w.Write(di); w.Write(doBit); w.Write(writeEnable); w.Write(shift); w.Write(bits); w.Write(readWord); w.Write(readBits); w.Write(command);
        }
        public override void LoadState(BinaryReader r)
        {
            en1 = r.ReadBoolean(); en2 = r.ReadBoolean(); romBank = r.ReadInt32(); latchedX = r.ReadInt32(); latchedY = r.ReadInt32();
            cs = r.ReadBoolean(); clk = r.ReadBoolean(); di = r.ReadBoolean(); doBit = r.ReadBoolean(); writeEnable = r.ReadBoolean();
            shift = r.ReadInt32(); bits = r.ReadInt32(); readWord = r.ReadInt32(); readBits = r.ReadInt32(); command = r.ReadInt32();
        }
    }

    private sealed class Mbc1 : GbMapper
    {
        private bool ramg, mode; private int bank1 = 1, bank2; private readonly bool multi;
        public Mbc1(GbCartridge c, bool multicart) : base(c) { multi = multicart; }

        /// <summary>MBC1M: 1 MiB made of four 256 KiB games, each with its own Nintendo logo; BANK2 is shifted by 4, not 5.</summary>
        public static bool LooksLikeMulticart(byte[] rom)
        {
            if (rom.Length != 0x100000) return false;
            int logos = 0;
            for (int game = 0; game < 4; game++)
            {
                int o = game * 0x40000 + 0x104; bool same = true;
                for (int i = 0; i < 0x30 && same; i++) same = rom[o + i] == rom[0x104 + i];
                if (same) logos++;
            }
            return logos >= 2;
        }

        private int Shift => multi ? 4 : 5;
        private int Low => multi ? bank1 & 0x0F : bank1;
        public override byte ReadRom(ushort a) => a < 0x4000
            ? RomAt(mode ? bank2 << Shift : 0, a)
            : RomAt(bank2 << Shift | Low, a);
        public override void WriteRom(ushort a, byte v)
        {
            switch (a >> 13)
            {
                case 0: ramg = (v & 0x0F) == 0x0A; break;
                case 1: bank1 = v & 0x1F; if (bank1 == 0) bank1 = 1; break;
                case 2: bank2 = v & 3; break;
                default: mode = (v & 1) != 0; break;
            }
        }
        private int RamBank => mode ? bank2 : 0;
        public override byte ReadRam(ushort a) => ramg ? RamAt(RamBank, a) : (byte)0xFF;
        public override void WriteRam(ushort a, byte v) { if (ramg) RamSet(RamBank, a, v); }
        public override void SaveState(BinaryWriter w) { w.Write(ramg); w.Write(mode); w.Write(bank1); w.Write(bank2); }
        public override void LoadState(BinaryReader r) { ramg = r.ReadBoolean(); mode = r.ReadBoolean(); bank1 = r.ReadInt32(); bank2 = r.ReadInt32(); }
    }

    private sealed class Mbc2 : GbMapper
    {
        private bool ramg; private int bank = 1;
        public Mbc2(GbCartridge c) : base(c) { }
        public override byte ReadRom(ushort a) => a < 0x4000 ? RomAt(0, a) : RomAt(bank, a);
        public override void WriteRom(ushort a, byte v)
        {
            if (a >= 0x4000) return;
            if ((a & 0x100) == 0) ramg = (v & 0x0F) == 0x0A;
            else { bank = v & 0x0F; if (bank == 0) bank = 1; }
        }
        // 512 half-bytes, mirrored across $A000-$BFFF; the upper nibble reads back as 1s.
        public override byte ReadRam(ushort a) => ramg ? (byte)(Cart.Ram[a & 0x1FF] | 0xF0) : (byte)0xFF;
        public override void WriteRam(ushort a, byte v) { if (ramg) Cart.Ram[a & 0x1FF] = (byte)(v | 0xF0); }
        public override void SaveState(BinaryWriter w) { w.Write(ramg); w.Write(bank); }
        public override void LoadState(BinaryReader r) { ramg = r.ReadBoolean(); bank = r.ReadInt32(); }
    }

    private sealed class Mbc3 : GbMapper
    {
        private bool ramg; private int romBank = 1, sel; private byte latchWrite = 0xFF;
        public readonly GbRtc? Clock;
        public Mbc3(GbCartridge c, bool rtc) : base(c) { if (rtc) Clock = new GbRtc(); }
        public override byte ReadRom(ushort a) => a < 0x4000 ? RomAt(0, a) : RomAt(romBank, a);
        public override void WriteRom(ushort a, byte v)
        {
            switch (a >> 13)
            {
                case 0: ramg = (v & 0x0F) == 0x0A; break;
                case 1: romBank = v & 0x7F; if (romBank == 0) romBank = 1; break;
                case 2: sel = v & 0x0F; break;
                default:
                    if (latchWrite == 0 && v == 1) Clock?.Latch();
                    latchWrite = v; break;
            }
        }
        public override byte ReadRam(ushort a)
        {
            if (!ramg) return 0xFF;
            if (sel <= 3) return RamAt(sel, a);
            return Clock != null && sel >= 8 && sel <= 0x0C ? Clock.Read(sel) : (byte)0xFF;
        }
        public override void WriteRam(ushort a, byte v)
        {
            if (!ramg) return;
            if (sel <= 3) RamSet(sel, a, v);
            else if (Clock != null && sel >= 8 && sel <= 0x0C) Clock.Write(sel, v);
        }
        public override void SaveState(BinaryWriter w) { w.Write(ramg); w.Write(romBank); w.Write(sel); w.Write(latchWrite); Clock?.SaveState(w); }
        public override void LoadState(BinaryReader r) { ramg = r.ReadBoolean(); romBank = r.ReadInt32(); sel = r.ReadInt32(); latchWrite = r.ReadByte(); Clock?.LoadState(r); }
    }

    private sealed class Mbc5 : GbMapper
    {
        private bool ramg; private int romBank = 1, ramBank; private readonly bool rumbleCart;
        public bool Rumble { get; private set; }
        public Mbc5(GbCartridge c, bool rumble) : base(c) { rumbleCart = rumble; }
        public override byte ReadRom(ushort a) => a < 0x4000 ? RomAt(0, a) : RomAt(romBank, a);
        public override void WriteRom(ushort a, byte v)
        {
            if (a < 0x2000) ramg = v == 0x0A;
            else if (a < 0x3000) romBank = (romBank & 0x100) | v;
            else if (a < 0x4000) romBank = (romBank & 0xFF) | (v & 1) << 8;
            else if (a < 0x6000)
            {
                if (rumbleCart) { Rumble = (v & 8) != 0; ramBank = v & 7; } else ramBank = v & 0x0F;
            }
        }
        public override byte ReadRam(ushort a) => ramg ? RamAt(ramBank, a) : (byte)0xFF;
        public override void WriteRam(ushort a, byte v) { if (ramg) RamSet(ramBank, a, v); }
        public override void SaveState(BinaryWriter w) { w.Write(ramg); w.Write(romBank); w.Write(ramBank); }
        public override void LoadState(BinaryReader r) { ramg = r.ReadBoolean(); romBank = r.ReadInt32(); ramBank = r.ReadInt32(); }
    }
}

/// <summary>
/// MBC3 real-time clock: seconds, minutes, hours and a 9-bit day counter with halt and carry flags, plus the
/// latched copy software reads. Runs on emulated time (T-cycles), so it stays deterministic.
/// </summary>
public sealed class GbRtc
{
    private int s, m, h, d; private bool halt, carry;
    private readonly byte[] latched = new byte[5];
    private long sub;   // T-cycles into the current second
    private const long CyclesPerSecond = 4194304;

    public void Advance(int tCycles)
    {
        if (halt) return;
        sub += tCycles;
        while (sub >= CyclesPerSecond) { sub -= CyclesPerSecond; TickSecond(); }
    }

    /// <summary>Add wall-clock time that passed while the game was off (from a save file).</summary>
    public void AdvanceSeconds(long seconds) { if (halt) return; for (long i = 0; i < seconds; i++) TickSecond(); }

    private void TickSecond()
    {
        // Registers count within their bit width; out-of-range values written by software wrap at the width, not at 60.
        s = (s + 1) & 0x3F; if (s != 60) return; s = 0;
        m = (m + 1) & 0x3F; if (m != 60) return; m = 0;
        h = (h + 1) & 0x1F; if (h != 24) return; h = 0;
        d++; if (d > 0x1FF) { d = 0; carry = true; }
    }

    public void Latch()
    {
        latched[0] = (byte)s; latched[1] = (byte)m; latched[2] = (byte)h; latched[3] = (byte)d;
        latched[4] = (byte)((d >> 8 & 1) | (halt ? 0x40 : 0) | (carry ? 0x80 : 0));
    }

    public byte Read(int reg) => reg switch
    {
        8 => (byte)(latched[0] | 0xC0), 9 => (byte)(latched[1] | 0xC0), 10 => (byte)(latched[2] | 0xE0), 11 => latched[3], _ => (byte)(latched[4] | 0x3E),
    };

    public void Write(int reg, byte v)
    {
        switch (reg)
        {
            case 8: s = v & 0x3F; sub = 0; break;
            case 9: m = v & 0x3F; break;
            case 10: h = v & 0x1F; break;
            case 11: d = (d & 0x100) | v; break;
            default: d = (d & 0xFF) | (v & 1) << 8; halt = (v & 0x40) != 0; carry = (v & 0x80) != 0; break;
        }
        Latch();
    }

    public void SaveState(BinaryWriter w) { w.Write(s); w.Write(m); w.Write(h); w.Write(d); w.Write(halt); w.Write(carry); w.Write(sub); w.Write(latched); }
    public void LoadState(BinaryReader r) { s = r.ReadInt32(); m = r.ReadInt32(); h = r.ReadInt32(); d = r.ReadInt32(); halt = r.ReadBoolean(); carry = r.ReadBoolean(); sub = r.ReadInt64(); r.ReadBytes(5).CopyTo(latched, 0); }

    /// <summary>The 48-byte trailer BGB/VBA-M append to .sav files (current + latched registers as 32-bit LE, then a unix time).</summary>
    public byte[] ExportTrailer(long unixNow)
    {
        var b = new byte[48];
        int[] cur = { s, m, h, d & 0xFF, (d >> 8 & 1) | (halt ? 0x40 : 0) | (carry ? 0x80 : 0) };
        for (int i = 0; i < 5; i++) { BitConverter.GetBytes(cur[i]).CopyTo(b, i * 4); BitConverter.GetBytes((int)latched[i]).CopyTo(b, 20 + i * 4); }
        BitConverter.GetBytes(unixNow).CopyTo(b, 40);
        return b;
    }

    public void ImportTrailer(byte[] b, int offset, long unixNow)
    {
        s = BitConverter.ToInt32(b, offset) & 0x3F; m = BitConverter.ToInt32(b, offset + 4) & 0x3F; h = BitConverter.ToInt32(b, offset + 8) & 0x1F;
        int dl = BitConverter.ToInt32(b, offset + 12) & 0xFF, dh = BitConverter.ToInt32(b, offset + 16);
        d = dl | (dh & 1) << 8; halt = (dh & 0x40) != 0; carry = (dh & 0x80) != 0;
        for (int i = 0; i < 5; i++) latched[i] = (byte)BitConverter.ToInt32(b, offset + 20 + i * 4);
        long saved = b.Length >= offset + 48 ? BitConverter.ToInt64(b, offset + 40) : unixNow;
        if (unixNow > saved) AdvanceSeconds(Math.Min(unixNow - saved, 60L * 60 * 24 * 512));
    }
}
