using System;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-cart-test</c>: the cartridge hardware the whole-library sweep found missing or wrong, checked on synthetic ROMs: the serial EEPROMs (the datasheet protocol of the X24C01 and the 24C02 to 24C64 on bit-banged
/// lines, and the three publishers' wirings through the cartridge's own addresses), the SRAM rules (mapped from power-on when the window is free of ROM, mirrored past the declared range, odd/even bytes by address,
/// the headerless RAM that a first write brings in, the battery report), the region the version register reports, and a stopped 68000 waking for the vertical interrupt. The expectations come from the chip datasheets and
/// from the behaviour of the games measured against BlastEm (the track B3b record says which); each group is meant to fail when its part of the emulator is broken (see the proofs in the record).
/// </summary>
internal static class MdCartTest
{
    private static int pass, fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  (" + detail + ")")}");
    }

    private static void Group(string title) => Console.WriteLine($"-- {title}");

    /// <summary>Which cartridges the app accepts as Genesis games: the header is "SEGA" at $100; a 32X, a Pico and an SVP cartridge (Virtua Racing: MK-1229) need hardware that is not emulated and are not accepted (a plain run would look like a bug of the game).</summary>
    private static void Detection()
    {
        Group("cartridges the app takes for Genesis games (and the three it refuses because their hardware is not emulated)");
        byte[] Rom(string console, string serial)
        {
            var r = new byte[0x20000];
            Array.Fill(r, (byte)0xFF);
            void Put(int at, string s) { for (int i = 0; i < s.Length; i++) r[at + i] = (byte)s[i]; }
            Put(0x100, console.PadRight(16)); Put(0x180, serial.PadRight(14));
            return r;
        }
        Check("a plain header (SEGA GENESIS, serial GM 00001009-00) is a Genesis game", SegaRomFormat.Detect(Rom("SEGA GENESIS", "GM 00001009-00"), "g.bin") == ConsoleKind.Genesis);
        Check("a SEGA MEGA DRIVE header too", SegaRomFormat.Detect(Rom("SEGA MEGA DRIVE", "GM T-12345 -00"), "g.bin") == ConsoleKind.Genesis);
        Check("a 32X header is refused", SegaRomFormat.Detect(Rom("SEGA 32X", "GM MK-84501-00"), "g.bin") == null);
        Check("a Pico header is refused", SegaRomFormat.Detect(Rom("SEGA PICO", "GM 00000000-00"), "g.bin") == null);
        Check("the SVP cartridge (Virtua Racing, serial MK-1229) is refused", SegaRomFormat.Detect(Rom("SEGA GENESIS", "GM MK-1229 -00"), "g.bin") == null);
        Check("another MK- serial (Sonic 3 is MK-1079) is not", SegaRomFormat.Detect(Rom("SEGA GENESIS", "GM MK-1079 -00"), "g.bin") == ConsoleKind.Genesis);
    }

    public static int Run(string[] args)
    {
        pass = fail = 0;
        // a group that throws is a failed check (with the exception), not a crashed run
        foreach (var (name, group) in new (string, Action)[] { ("EEPROM chips", Eeprom), ("EEPROM wiring", Wiring), ("three-wire EEPROM", ThreeWire), ("SRAM", Sram), ("region", Region), ("STOP", Stop), ("detection", Detection) })
        {
            try { group(); }
            catch (Exception e) { Check($"group '{name}' ran to its end", false, e.ToString().Replace('\n', ' ')); }
        }
        Console.WriteLine($"== {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // =====================================================================================================================
    //  A two-wire master on the chip's lines
    // =====================================================================================================================

    private sealed class Bus
    {
        public readonly MdEeprom E;
        public Bus(MdEeprom e) { E = e; Set(true, true); }
        public void Set(bool sda, bool scl) => E.Drive(sda, scl);
        public void Start() { Set(true, false); Set(true, true); Set(false, true); Set(false, false); }
        public void Stop() { Set(false, false); Set(false, true); Set(true, true); }
        /// <summary>One byte out (MSB first) and the acknowledge clock: returns true when the chip pulled SDA low on the ninth clock.</summary>
        public bool Write(int b)
        {
            for (int i = 7; i >= 0; i--) { bool bit = (b >> i & 1) != 0; Set(bit, false); Set(bit, true); Set(bit, false); }
            Set(true, false); Set(true, true);
            bool ack = !E.SdaLine;
            Set(true, false);
            return ack;
        }
        /// <summary>One byte in (SDA released), then the master's acknowledge (true) or not.</summary>
        public int Read(bool ack)
        {
            int v = 0;
            Set(true, false);
            for (int i = 0; i < 8; i++) { Set(true, true); v = v << 1 | (E.SdaLine ? 1 : 0); Set(true, false); }
            Set(!ack, false); Set(!ack, true); Set(!ack, false); Set(true, false);
            return v;
        }
    }

    private static void Eeprom()
    {
        Group("serial EEPROM chips (datasheet protocol on bit-banged lines)");
        var x = new MdEeprom(MdEepromChip.X24C01);
        Check("a blank chip reads $FF", x.Data.All(b => b == 0xFF) && x.Data.Length == 128);
        var b1 = new Bus(x);
        b1.Start();
        bool ack = b1.Write(0x2A << 1);            // address $2A, write
        bool ack2 = b1.Write(0xA5);
        b1.Stop();
        Check("X24C01: a write (7-bit address + R/W=0, then the data byte) is acknowledged twice and committed at STOP", ack && ack2 && x.Data[0x2A] == 0xA5, $"{ack} {ack2} {x.Data[0x2A]:X2}");
        b1.Start(); b1.Write(0x2A << 1 | 1); int r1 = b1.Read(false); b1.Stop();
        Check("X24C01: a read of that address (R/W=1) returns the byte", r1 == 0xA5, $"{r1:X2}");
        b1.Start(); b1.Write(0x2A << 1); b1.Write(0x11);
        Check("X24C01: a byte is not stored before STOP", x.Data[0x2A] == 0xA5);
        b1.Stop();
        Check("... and is after it", x.Data[0x2A] == 0x11);
        b1.Start(); b1.Write(0x7E << 1 | 1); int ra = b1.Read(true), rb = b1.Read(true), rc = b1.Read(false); b1.Stop();
        Check("X24C01: a sequential read runs on and wraps at the end of the array ($7E, $7F, $00)", ra == 0xFF && rb == 0xFF && rc == 0xFF);
        // page write: the 4-byte page wraps inside itself
        b1.Start(); b1.Write(0x06 << 1); b1.Write(1); b1.Write(2); b1.Write(3); b1.Write(4); b1.Stop();
        Check("X24C01: a page write starting at $06 wraps within its 4-byte page ($06, $07, $04, $05 get 1, 2, 3, 4)", x.Data[6] == 1 && x.Data[7] == 2 && x.Data[4] == 3 && x.Data[5] == 4, $"{x.Data[4]:X2} {x.Data[5]:X2} {x.Data[6]:X2} {x.Data[7]:X2}");

        var c2 = new MdEeprom(MdEepromChip.C24C02);
        var b2 = new Bus(c2);
        b2.Start(); bool a1 = b2.Write(0xA0); bool a2 = b2.Write(0x40); bool a3 = b2.Write(0x5A); b2.Stop();
        Check("24C02: device byte $A0, word address, data: three acknowledges, the byte lands at $40", a1 && a2 && a3 && c2.Data[0x40] == 0x5A);
        b2.Start(); b2.Write(0xA0); b2.Write(0x40); b2.Start(); bool ar = b2.Write(0xA1); int rr = b2.Read(false); b2.Stop();
        Check("24C02: a random read (write the address, repeated START, device byte with R/W=1) returns it", ar && rr == 0x5A, $"{rr:X2}");
        b2.Start(); bool wrong = b2.Write(0xB0); b2.Stop();
        Check("24C02: a device byte that is not 1010xxxx gets no acknowledge", !wrong);
        b2.Start(); b2.Write(0xA0); b2.Write(0xF8); for (int i = 0; i < 10; i++) b2.Write(0x10 + i); b2.Stop();
        Check("24C02: an 8-byte page write starting at $F8 wraps to $F8 inside its page (the last two bytes overwrite the first two)", c2.Data[0xF8] == 0x18 && c2.Data[0xF9] == 0x19 && c2.Data[0xFF] == 0x17, $"{c2.Data[0xF8]:X2} {c2.Data[0xF9]:X2}");

        var c8 = new MdEeprom(MdEepromChip.C24C08);
        var b8 = new Bus(c8);
        b8.Start(); b8.Write(0xA2); b8.Write(0x10); b8.Write(0x77); b8.Stop();       // page bit 0 set: byte address $110
        b8.Start(); b8.Write(0xA6); b8.Write(0x10); b8.Write(0x88); b8.Stop();       // page 3: $310
        Check("24C08: the page bits of the device byte are bits 8 and 9 of the address ($A2 -> $110, $A6 -> $310)", c8.Data[0x110] == 0x77 && c8.Data[0x310] == 0x88 && c8.Data[0x10] == 0xFF && c8.Data.Length == 1024);
        var c64 = new MdEeprom(MdEepromChip.C24C64);
        var b64 = new Bus(c64);
        b64.Start(); b64.Write(0xA0); b64.Write(0x12); b64.Write(0x34); b64.Write(0xC3); b64.Stop();
        Check("24C64: two address bytes", c64.Data[0x1234] == 0xC3 && c64.Data.Length == 8192);

        // a chip that is mid-byte when a START comes in starts over; a STOP with no write pending changes nothing
        var c3 = new MdEeprom(MdEepromChip.C24C02); var b3 = new Bus(c3);
        b3.Start(); b3.Set(true, false); b3.Set(true, true); b3.Set(false, false);    // three clocks of a byte, then
        b3.Start(); bool ok = b3.Write(0xA0); b3.Write(0x00); b3.Write(0x99); b3.Stop();
        Check("a START in the middle of a byte begins a new transfer", ok && c3.Data[0] == 0x99);
    }

    // =====================================================================================================================
    //  The cartridge's wiring
    // =====================================================================================================================

    private static byte[] BareRom(int size, string regions = "JUE", string serial = "GM 00000000-00", Action<byte[]>? patch = null)
    {
        var rom = new byte[size];
        void L(int at, uint v) { rom[at] = (byte)(v >> 24); rom[at + 1] = (byte)(v >> 16); rom[at + 2] = (byte)(v >> 8); rom[at + 3] = (byte)v; }
        L(0, 0xFFFE00); L(4, 0x200);
        for (int v = 2; v < 64; v++) L(v * 4, 0x400);
        Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100);
        Encoding.ASCII.GetBytes("(C)TEST 2026.OCT").CopyTo(rom, 0x110);
        Encoding.ASCII.GetBytes("CART TEST ROM".PadRight(48)).CopyTo(rom, 0x120);
        Encoding.ASCII.GetBytes("CART TEST ROM".PadRight(48)).CopyTo(rom, 0x150);
        Encoding.ASCII.GetBytes(serial.PadRight(14)).CopyTo(rom, 0x180);
        Encoding.ASCII.GetBytes(regions.PadRight(16)).CopyTo(rom, 0x1F0);
        rom[0x400] = 0x60; rom[0x401] = 0xFE;
        patch?.Invoke(rom);
        return rom;
    }

    private static void Header(byte[] r, uint start, uint end, byte type0 = 0xF8, byte type1 = 0x20)
    {
        Encoding.ASCII.GetBytes("RA").CopyTo(r, 0x1B0); r[0x1B2] = type0; r[0x1B3] = type1;
        r[0x1B4] = (byte)(start >> 24); r[0x1B5] = (byte)(start >> 16); r[0x1B6] = (byte)(start >> 8); r[0x1B7] = (byte)start;
        r[0x1B8] = (byte)(end >> 24); r[0x1B9] = (byte)(end >> 16); r[0x1BA] = (byte)(end >> 8); r[0x1BB] = (byte)end;
    }

    private static void Wiring()
    {
        Group("EEPROM wiring through the cartridge's addresses (one publisher each)");
        // Sega: SDA bit 0 / SCL bit 1 of the byte at $200001
        var sega = new MdCartridge(BareRom(0x100000, "U", "GM MK-1215 -00"));
        Check("a game in the table gets its EEPROM and the battery image is the chip's contents", sega.Eeprom != null && sega.Eeprom.Chip == MdEepromChip.X24C01 && sega.EepromWiring == MdEepromWiring.Sega && ReferenceEquals(sega.Sram, sega.Eeprom!.Data) && sega.HasBattery);
        void SegaSet(bool sda, bool scl) => sega.WriteByte(0x200001, (byte)((sda ? 1 : 0) | (scl ? 2 : 0)));
        void SegaStart() { SegaSet(true, false); SegaSet(true, true); SegaSet(false, true); SegaSet(false, false); }
        void SegaStop() { SegaSet(false, false); SegaSet(false, true); SegaSet(true, true); }
        void SegaByte(int b) { for (int i = 7; i >= 0; i--) { bool bit = (b >> i & 1) != 0; SegaSet(bit, false); SegaSet(bit, true); SegaSet(bit, false); } SegaSet(true, false); SegaSet(true, true); SegaSet(true, false); }
        SegaStart(); SegaByte(0x05 << 1); SegaByte(0xC9); SegaStop();
        Check("Sega wiring ($200001: SDA bit 0, SCL bit 1): a write sequence stores the byte", sega.Eeprom!.Data[5] == 0xC9, $"{sega.Eeprom.Data[5]:X2}");
        SegaStart(); SegaByte(0x05 << 1 | 1);
        int v = 0; SegaSet(true, false);
        for (int i = 0; i < 8; i++) { SegaSet(true, true); v = v << 1 | (sega.ReadByte(0x200001) & 1); SegaSet(true, false); }
        Check("... and reads it back on bit 0 of the byte at $200001", v == 0xC9, $"{v:X2}");
        Check("... while the rest of cartridge space is still ROM", sega.ReadByte(0x400) == 0x60 && sega.ReadByte(0x200002) == sega.ReadByte(0x2));

        // Acclaim: SCL = bit 0 of $200000, SDA = bit 0 of $200001
        var acc = new MdCartridge(BareRom(0x300000, "JUE", "GM T-081276 00"));
        Check("an Acclaim cartridge is in the table with a 24C02 and its own wiring", acc.Eeprom != null && acc.Eeprom.Chip == MdEepromChip.C24C02 && acc.EepromWiring == MdEepromWiring.Acclaim);
        void AccSda(bool s) => acc.WriteByte(0x200001, (byte)(s ? 1 : 0));
        void AccScl(bool s) => acc.WriteByte(0x200000, (byte)(s ? 1 : 0));
        void AccStart() { AccSda(true); AccScl(false); AccScl(true); AccSda(false); AccScl(false); }
        void AccStop() { AccSda(false); AccScl(true); AccSda(true); }
        bool AccByte(int b) { for (int i = 7; i >= 0; i--) { bool bit = (b >> i & 1) != 0; AccSda(bit); AccScl(true); AccScl(false); } AccSda(true); AccScl(true); bool a = (acc.ReadByte(0x200001) & 1) == 0; AccScl(false); return a; }
        AccStart(); bool k1 = AccByte(0xA0), k2 = AccByte(0x21), k3 = AccByte(0x3C); AccStop();
        Check("Acclaim wiring (SCL on the byte at $200000, SDA at $200001, read back from $200001 bit 0): the chip acknowledges and stores", k1 && k2 && k3 && acc.Eeprom!.Data[0x21] == 0x3C);

        // Codemasters: SDA bit 0 / SCL bit 1 of $300000; SDA read back in bit 7 of $380001
        var cm = new MdCartridge(BareRom(0x100000, "E", "GM T-120106-50"));
        Check("a Codemasters cartridge gets a 24C08 with its wiring", cm.Eeprom != null && cm.Eeprom.Chip == MdEepromChip.C24C08 && cm.EepromWiring == MdEepromWiring.Codemasters);
        void CmSet(bool sda, bool scl) => cm.WriteByte(0x300000, (byte)((sda ? 1 : 0) | (scl ? 2 : 0)));
        void CmStart() { CmSet(true, false); CmSet(true, true); CmSet(false, true); CmSet(false, false); }
        void CmStop() { CmSet(false, false); CmSet(false, true); CmSet(true, true); }
        bool CmByte(int b) { for (int i = 7; i >= 0; i--) { bool bit = (b >> i & 1) != 0; CmSet(bit, false); CmSet(bit, true); CmSet(bit, false); } CmSet(true, false); CmSet(true, true); bool a = (cm.ReadByte(0x380001) & 0x80) == 0; CmSet(true, false); return a; }
        CmStart(); bool m1 = CmByte(0xA4), m2 = CmByte(0x08), m3 = CmByte(0xEE); CmStop();
        Check("Codemasters wiring (write $300000 bits 0 and 1, read $380001 bit 7): acknowledged and stored on page 2 ($A4 -> byte $208)", m1 && m2 && m3 && cm.Eeprom!.Data[0x208] == 0xEE, $"{m1} {m2} {m3} {cm.Eeprom.Data[0x208]:X2}");
        Check("... the other bits of $380001 are ROM", (cm.ReadByte(0x380001) & 0x7F) == (cm.Rom[0x80001 & (cm.Rom.Length - 1)] & 0x7F));

        // a header that declares an EEPROM nobody knows: reported, not silently RAM
        var unknown = new MdCartridge(BareRom(0x100000, "U", "GM X-UNKNOWN 00", r => Header(r, 0x200001, 0x200001, 0xE8, 0x40)));
        Check("a header that declares an EEPROM for a game not in the table runs without one and says so", unknown.Eeprom == null && unknown.EepromUnsupported != null && !unknown.HasBattery && unknown.Mapper.Contains("no wiring"), unknown.Mapper);
    }

    // =====================================================================================================================
    //  The three-wire EEPROM (Mega Man: The Wily Wars)
    // =====================================================================================================================

    /// <summary>A bit-banging master on the 93C46's lines as the cartridge exposes them: CS, SK and DI are bits 2, 1 and 0 of a write to $200001, DO is bit 0 of a read of it.</summary>
    private sealed class Wire
    {
        private readonly MdCartridge c;
        public Wire(MdCartridge cart) { c = cart; }
        public void Pins(bool cs, bool sk, bool di) => c.WriteByte(0x200001, (byte)((cs ? 4 : 0) | (sk ? 2 : 0) | (di ? 1 : 0)));
        public bool Do => (c.ReadByte(0x200001) & 1) != 0;
        public void Begin() { Pins(false, false, false); Pins(true, false, false); }
        public void End() { Pins(false, false, false); }
        /// <summary>One bit in: DI set, a rising and a falling edge of SK.</summary>
        public void Bit(bool b) { Pins(true, false, b); Pins(true, true, b); Pins(true, false, b); }
        public void Bits(int value, int count) { for (int i = count - 1; i >= 0; i--) Bit((value >> i & 1) != 0); }
        /// <summary>One clock with DI low and the level of DO read after the rising edge, as the game does.</summary>
        public bool ClockRead() { Pins(true, false, false); Pins(true, true, false); bool v = Do; Pins(true, false, false); return v; }
        public int Read(int word)
        {
            Begin(); Bits(0b0001_10, 6 - 0); Bits(word, 6);             // leading zeros are ignored; here: 0 0 0 1 | 1 0 | address
            int v = 0; for (int i = 0; i < 16; i++) v = v << 1 | (ClockRead() ? 1 : 0);
            End(); return v;
        }
        public void Command(int op, int arg6) { Begin(); Bit(true); Bits(op, 2); Bits(arg6, 6); End(); }
        public void Write(int word, int value) { Begin(); Bit(true); Bits(1, 2); Bits(word, 6); Bits(value, 16); End(); }
    }

    private static void ThreeWire()
    {
        Group("the three-wire (Microwire 93C46) EEPROM: the datasheet protocol, through the cartridge's pins");
        var cart = new MdCartridge(BareRom(0x200000, "E", "GM T-12046 -00"));
        Check("a game in the table (Mega Man: The Wily Wars) gets a 128-byte three-wire EEPROM as its battery image", cart.Microwire != null && cart.Eeprom == null && cart.Sram.Length == 128 && ReferenceEquals(cart.Sram, cart.Microwire!.Data) && cart.HasBattery && cart.Mapper.Contains("three-wire"), cart.Mapper);
        var w = new Wire(cart); var chip = cart.Microwire!;
        Check("a blank chip reads $FFFF at every word", Enumerable.Range(0, 64).All(a => w.Read(a) == 0xFFFF));
        Check("DO reads as 1 (ready) with CS low and between commands", w.Do);
        w.Write(5, 0x1234);
        Check("a WRITE with writing still disabled (power-up) changes nothing", w.Read(5) == 0xFFFF && chip.Writes == 0);
        w.Command(0b00, 0b110000);          // EWEN: 1 00 11xxxx
        Check("EWEN enables writing", chip.WriteEnabled);
        w.Write(5, 0x1234); w.Write(63, 0xBEEF); w.Write(0, 0x0001);
        Check("WRITE stores a 16-bit word at its 6-bit address (words 0, 5 and 63)", w.Read(5) == 0x1234 && w.Read(63) == 0xBEEF && w.Read(0) == 0x0001 && w.Read(6) == 0xFFFF, $"{w.Read(5):X4} {w.Read(63):X4} {w.Read(0):X4}");
        Check("the battery image holds the words big-endian (word 5 at bytes 10 and 11)", cart.Sram[10] == 0x12 && cart.Sram[11] == 0x34 && cart.Sram[126] == 0xBE && cart.Sram[127] == 0xEF);
        w.Command(0b11, 5);                 // ERASE word 5
        Check("ERASE sets one word to $FFFF and leaves its neighbours", w.Read(5) == 0xFFFF && w.Read(63) == 0xBEEF);
        // a sequential read: one command, the clocks run on into the next word
        w.Begin(); w.Bits(0b110, 3); w.Bits(62, 6);
        int a = 0, b = 0; for (int i = 0; i < 16; i++) a = a << 1 | (w.ClockRead() ? 1 : 0); for (int i = 0; i < 16; i++) b = b << 1 | (w.ClockRead() ? 1 : 0); w.End();
        w.Begin(); w.Bits(0b110, 3); w.Bits(63, 6); int c63 = 0; for (int i = 0; i < 16; i++) c63 = c63 << 1 | (w.ClockRead() ? 1 : 0); int wrap = 0; for (int i = 0; i < 16; i++) wrap = wrap << 1 | (w.ClockRead() ? 1 : 0); w.End();
        Check("a READ runs on into the next word while SK keeps clocking (62 then 63; 63 then 0)", a == 0xFFFF && b == 0xBEEF && c63 == 0xBEEF && wrap == 0x0001, $"{a:X4} {b:X4} {c63:X4} {wrap:X4}");
        // the dummy zero bit: DO is low after the address and before the first data bit
        w.Begin(); w.Bits(0b110, 3); w.Bits(63, 6);
        Check("DO is a dummy zero after the last address bit's clock, before data bit 15", !w.Do);
        w.End();
        w.Command(0b00, 0b100000);          // ERAL: 1 00 10xxxx
        Check("ERAL erases the whole chip", Enumerable.Range(0, 64).All(x => w.Read(x) == 0xFFFF));
        w.Begin(); w.Bit(true); w.Bits(0b00, 2); w.Bits(0b01, 2); w.Bits(0, 4); w.Bits(0xA55A, 16); w.End();       // WRAL: 1 00 01xxxx + data
        Check("WRAL writes one value to every word", Enumerable.Range(0, 64).All(x => w.Read(x) == 0xA55A));
        w.Command(0b00, 0b000000);          // EWDS
        w.Write(7, 0x0000);
        Check("EWDS disables writing again (a WRITE after it changes nothing)", !chip.WriteEnabled && w.Read(7) == 0xA55A);
        // the programming happens when CS falls, and only for a complete command
        w.Command(0b00, 0b110000);
        w.Begin(); w.Bit(true); w.Bits(1, 2); w.Bits(9, 6); w.Bits(0x7777, 15); w.End();          // one data bit short
        Check("a WRITE cut short (15 of 16 data bits) is not programmed", w.Read(9) == 0xA55A);
        w.Begin(); w.Bits(0b0001_01, 6); w.Bits(9, 6); w.Bits(0x7777, 16);
        Check("the word is not changed before CS falls ...", chip.Data[18] == 0xA5);
        w.End();
        Check("... and is after it (three leading zero bits before the start bit were ignored)", w.Read(9) == 0x7777);
        // the pad at $200000 (the even byte of a word write) is not the chip's
        cart.WriteByte(0x200000, 0xFF);
        Check("a write to the even byte $200000 does not touch the chip", w.Read(9) == 0x7777 && cart.StrayWrites == 0);
        // the cartridge's state carries the chip
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms); cart.SaveState(bw); bw.Flush();
        var other = new MdCartridge(BareRom(0x200000, "E", "GM T-12046 -00")); ms.Position = 0; other.LoadState(new BinaryReader(ms));
        Check("a cartridge state restores the chip's words and its write-enable latch", other.Microwire!.Data.AsSpan().SequenceEqual(chip.Data) && other.Microwire.WriteEnabled == chip.WriteEnabled);
        // Rockman Megaworld (Japan) is the same cartridge
        var jp = new MdCartridge(BareRom(0x200000, "J", "GM T-12053 -00"));
        Check("Rockman Megaworld (Japan, serial T-12053) has the same chip", jp.Microwire != null);
        // the real game: it formats an empty chip at its first start (erase and write every word), then finds its data on the second
        string? wily = Directory.Exists(MdRoster.Dir) ? Directory.EnumerateFiles(MdRoster.Dir, "Mega Man - The Wily Wars*").FirstOrDefault() : null;
        if (wily == null) { Console.WriteLine("  SKIP  Mega Man: The Wily Wars from the library (not found)"); return; }
        byte[] rom = File.ReadAllBytes(wily);
        var s1 = new MdSession(rom, "M68K", "MD", "MD");
        for (int f = 0; f < 600; f++) { s1.SetPad(0, PadButtons.None); s1.RunFrame(); }
        var mw1 = s1.Board.Cart.Microwire!;
        Check("Mega Man: The Wily Wars on a blank chip formats it at start (64 words erased and written, writing enabled by the game)", mw1.Writes >= 64 && mw1.Erases >= 64 && mw1.WriteEnabled, $"writes {mw1.Writes}, erases {mw1.Erases}, enabled {mw1.WriteEnabled}");
        byte[] save = s1.ExportSave();
        var s2 = new MdSession(rom, "M68K", "MD", "MD"); s2.ImportSave(save);
        for (int f = 0; f < 600; f++) { s2.SetPad(0, PadButtons.None); s2.RunFrame(); }
        var mw2 = s2.Board.Cart.Microwire!;
        Check("started again from that save the game reads the chip and writes nothing (it recognises its own data)", mw2.Writes == 0 && mw2.Erases == 0 && mw2.Reads > 0 && s2.ExportSave().AsSpan().SequenceEqual(save), $"writes {mw2.Writes}, erases {mw2.Erases}, words read {mw2.Reads}");
        var s3 = new MdSession(rom, "M68K", "MD", "MD");
        var corrupt = (byte[])save.Clone(); for (int i = 0; i < corrupt.Length; i++) corrupt[i] ^= 0x5A;
        s3.ImportSave(corrupt);
        for (int f = 0; f < 600; f++) { s3.SetPad(0, PadButtons.None); s3.RunFrame(); }
        Check("started from damaged data the game formats it again (the check can fail)", s3.Board.Cart.Microwire!.Writes >= 64, $"writes {s3.Board.Cart.Microwire!.Writes}");
    }

    // =====================================================================================================================
    //  SRAM
    // =====================================================================================================================

    private static void Sram()
    {
        Group("battery SRAM: where it is, what answers, what is saved");
        // HardBall 95: a 3 MB ROM with SRAM at $300001, which the ROM does not cover: mapped from power-on
        var big = new MdCartridge(BareRom(0x300000, "F", "GM T-119176-00", r => Header(r, 0x300001, 0x30FFFF)));
        big.WriteByte(0x300001, 0x5A);
        Check("SRAM whose window is free of ROM (3 MB ROM, SRAM at $300001) is in the map at power-on", big.SramMapped && big.ReadByte(0x300001) == 0x5A && big.Sram.Length == 0x8000);
        // a 2 MB ROM with SRAM at $200001 is free too; a 4 MB one covers it and needs $A130F1
        var four = new MdCartridge(BareRom(0x400000, "U", "GM 00000000-00", r => Header(r, 0x200001, 0x203FFF)));
        Check("... a 4 MB ROM covers the window at $200001: not mapped until $A130F1 switches it on", !four.SramMapped);
        four.WriteMapper(0xA130F1, 1);
        Check("... and then it is", four.SramMapped);

        // Barkley Shut Up and Jam 2: 2 KB of RAM declared at $200001-$200FFF, written all over the window: mirrors
        var bark = new MdCartridge(BareRom(0x200000, "JUE", "GM T-000000-00", r => Header(r, 0x200001, 0x200FFF, 0xF8, 0x40)));
        bark.WriteByte(0x200801, 0x77); bark.WriteByte(0x230801, 0x88);
        Check("past the declared range the addresses mirror (a 2 KB chip written at $200801 and $230801 is one byte)", bark.Sram.Length == 0x800 && bark.Sram[0x400] == 0x88 && bark.ReadByte(0x200801) == 0x88 && bark.ReadByte(0x3E0801) == 0x88, $"len {bark.Sram.Length} mapped {bark.SramMapped}");

        // MLBPA Baseball: type $E840 (which says EEPROM elsewhere) but an 8 KB window: RAM, on the odd bytes
        var mlb = new MdCartridge(BareRom(0x100000, "UEJ", "MK 00001211-00", r => Header(r, 0x200001, 0x203FFF, 0xE8, 0x40)));
        mlb.WriteByte(0x200001, 0x12); mlb.WriteByte(0x200003, 0x34);
        Check("$E840 on a kilobyte window is RAM on the odd bytes (the addresses decide, not the type bytes)", mlb.Eeprom == null && mlb.HasSramHeader && mlb.SramOdd && !mlb.SramEven && mlb.Sram.Length == 0x2000 && mlb.Sram[0] == 0x12 && mlb.Sram[1] == 0x34 && mlb.EepromUnsupported == null);
        var even = new MdCartridge(BareRom(0x100000, "U", "GM 00000000-00", r => Header(r, 0x200000, 0x2000FE)));
        even.WriteByte(0x200000, 0x21); even.WriteByte(0x200001, 0x22);
        Check("even to even is the high data byte (even addresses hold it, the odd byte is not RAM)", even.SramEven && !even.SramOdd && even.Sram[0] == 0x21 && even.ReadByte(0x200001) == 0xFF);
        var junk = new MdCartridge(BareRom(0x100000, "J", "GM 00000000-00", r => Header(r, 0xFF0000, 0xFFFFFF, 0x20, 0x20)));
        Check("a header whose window is in the work RAM ($FF0000, Mahjongg Lover) is junk: no SRAM", !junk.HasSramHeader);

        // headerless: the RAM appears with the first write
        var bare = new MdCartridge(BareRom(0x100000, "U", "GM 00000000-00", r => { r[0x200 + 1] = 0x7B; r[0x200000 & 0xFFFFF] = 0x7B; }));
        int romByte = bare.ReadByte(0x200001);
        Check("a cartridge with no SRAM header and a ROM of 2 MB or less reports no battery before anything is written", bare.SramHeaderless && !bare.HasBattery && !bare.SramMapped);
        bare.WriteByte(0x200000, 0xEE);       // the even byte: not RAM
        Check("... a write to the even byte does not bring RAM in", !bare.SramMapped);
        bare.WriteByte(0x200001, 0x42);
        Check("... the first write to the odd byte maps 32 KB of RAM, keeps the byte and the battery is reported", bare.SramMapped && bare.HasBattery && bare.ReadByte(0x200001) == 0x42 && bare.Sram.Length == 0x8000 && romByte != 0x42);
        Check("... the 32 KB of odd bytes spans 64 KB of addresses and mirrors through the window ($210001 and $3F0001 are the byte at $200001)", bare.ReadByte(0x210001) == 0x42 && bare.ReadByte(0x3F0001) == 0x42);
        bare.WriteByte(0x210003, 0x43);
        Check("... a write through a mirror lands in the same RAM", bare.ReadByte(0x200003) == 0x43 && bare.Sram[1] == 0x43);
        var bare2 = new MdCartridge(BareRom(0x100000, "U", "GM 00000000-00"));
        var save = new byte[0x8000]; save[5] = 0x99;
        var sess = new MdSession(BareRom(0x100000, "U", "GM 00000000-00"), "M68K", "MD", "MD");
        sess.ImportSave(save);
        Check("a saved game loaded into a headerless cartridge puts its RAM in the map from the start (and so the battery is reported)", sess.HasBattery && sess.Board.Cart.ReadByte(0x20000B) == 0x99, $"{sess.HasBattery}");
        Check("a headerless ROM over 2 MB has no such RAM (the window is the ROM's)", !new MdCartridge(BareRom(0x300000, "U", "GM 00000000-00")).SramHeaderless);
    }

    // =====================================================================================================================
    //  Region
    // =====================================================================================================================

    private static void Region()
    {
        Group("the version register's overseas flag (BlastEm runs Golden Axe III and J. League Pro Striker, which show a region-lock screen on an overseas console)");
        bool Ov(string regions) => new MdCartridge(BareRom(0x100000, regions)).Overseas;
        Check("J: domestic", !Ov("J"));
        Check("U: overseas", Ov("U"));
        Check("E: overseas", Ov("E"));
        Check("JUE: overseas (the Americas are listed)", Ov("JUE"));
        Check("JE: domestic (Japan and Europe, not the Americas)", !Ov("JE"));
        Check("UE: overseas", Ov("UE"));
        Check("hex masks: 1 (Japan) domestic, 4 (Americas) overseas, 8 (Europe) overseas, 5 (Japan + Americas) overseas", !Ov("1") && Ov("4") && Ov("8") && Ov("5"));
    }

    // =====================================================================================================================
    //  STOP
    // =====================================================================================================================

    private static void Stop()
    {
        Group("a stopped 68000 wakes for the vertical interrupt (it made no bus cycle to sample the level at)");
        // main: move.w #$8120,$C00004 (VINT on) ; move.w #$2000,SR ; loop: stop #$2000 ; bra loop     handler (vector 30): addq.w #1,$FF0000 ; rte
        var rom = BareRom(0x20000, "JUE", "GM 00000000-00", r =>
        {
            void Put(int at, params ushort[] w) { for (int i = 0; i < w.Length; i++) { r[at + i * 2] = (byte)(w[i] >> 8); r[at + i * 2 + 1] = (byte)w[i]; } }
            r[0x78] = 0; r[0x79] = 0; r[0x7A] = 0x05; r[0x7B] = 0x00;       // vector 30 -> $500
            Put(0x200, 0x33FC, 0x8120, 0x00C0, 0x0004, 0x46FC, 0x2000, 0x4E72, 0x2000, 0x60FA);
            Put(0x500, 0x5279, 0x00FF, 0x0000, 0x4E73);
        });
        var s = new MdSession(rom, "M68K", "MD", "MD");
        for (int i = 0; i < 30; i++) s.RunFrame();
        int count = s.Board.Ram[0] << 8 | s.Board.Ram[1];
        Check("a game that sleeps in STOP gets its vertical interrupt every frame (30 frames: 29 to 30 handler runs)", count >= 29 && count <= 30, $"{count}");
        Check("... and the processor is stopped between them", s.Board.Cpu.Stopped || count > 0);
    }
}
