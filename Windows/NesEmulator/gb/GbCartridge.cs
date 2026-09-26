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
    public void AdvanceClock(int tCycles) => (mapper as Mbc3)?.Clock?.Advance(tCycles);

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
