using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The 24C01 serial EEPROM of the Game Gear baseball cartridges (<see cref="Eeprom24c01"/>, the <see cref="SmsMapperKind.SegaEeprom"/> mapper) driven the way a cartridge's program drives it: a bit-banging master that writes the data line and the clock
/// into the $8000 window and reads the chip's side of the data line back. The master here is written from the part's data sheet (start, address and read/write byte, acknowledge, data, stop), not from any game; the games themselves
/// (World Series Baseball, The Majors Pro Baseball) are the end-to-end check of the same chip in <c>--smstest --roms</c> (their title screens, which only appear when the driver gets its acknowledge).
/// </summary>
internal static class EepromSuite
{
    /// <summary>A bit-banging two-wire master on a cartridge's window.</summary>
    private sealed class Master
    {
        private readonly SmsCartridge cart;
        private readonly bool poison;        // the poison: a read is started with the write bit, so the chip waits for data instead of sending it
        private bool sda = true, scl = true;
        /// <param name="resume">The chip is part-way through a transfer (a state was loaded into it): the master takes up the lines where that transfer left them (data released, clock low) instead of starting from idle.</param>
        public Master(SmsCartridge cart, bool poison = false, bool resume = false)
        {
            this.cart = cart; this.poison = poison;
            cart.Write(0xFFFC, 0x08);
            if (resume) { sda = true; scl = false; } else Put(true, true);
        }

        private void Put(bool d, bool c) { sda = d; scl = c; cart.Write(0x8000, (byte)((d ? 1 : 0) | (c ? 2 : 0))); }
        public bool ChipLow => (cart.Read(0x8000) & 1) == 0;       // the chip pulls the data line low (an acknowledge, or a 0 it sends)
        public void Start() { Put(true, true); Put(false, true); Put(false, false); }
        public void Stop() { Put(false, false); Put(false, true); Put(true, true); }

        /// <summary>Sends a byte, most significant bit first, and returns whether the chip acknowledged.</summary>
        public bool Send(int b, int bits = 8)
        {
            for (int i = bits - 1; i >= 0; i--) { bool bit = ((b >> i) & 1) != 0; Put(bit, false); Put(bit, true); Put(bit, false); }
            Put(true, false); Put(true, true);
            bool ack = ChipLow;                                      // the ninth clock: the chip answers
            Put(true, false);
            return ack;
        }

        /// <summary>Reads a byte; <paramref name="ack"/> asks for the next one (the master pulls the line low on the ninth clock), false ends the read.</summary>
        public int Receive(bool ack)
        {
            int v = 0;
            Put(true, false);                                        // the master lets go of the line
            for (int i = 0; i < 8; i++) { Put(true, true); v = (v << 1) | (ChipLow ? 0 : 1); Put(true, false); }
            Put(!ack, false); Put(!ack, true); Put(!ack, false); Put(true, false);
            return v;
        }

        public bool WriteByte(int address, int value) { Start(); bool a1 = Send(address << 1); bool a2 = a1 && Send(value); Stop(); return a1 && a2; }
        public int ReadByte(int address) { Start(); bool a = Send((address << 1) | (poison ? 0 : 1)); int v = a ? Receive(false) : -1; Stop(); return v; }
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        byte[] Banks() { var rom = new byte[0x10000]; for (int b = 0; b < 4; b++) Array.Fill(rom, (byte)b, b * 0x4000, 0x4000); return rom; }
        bool poison = ctx.Poison == "eeprom";

        var cart = new SmsCartridge(Banks(), SmsMapperKind.SegaEeprom);
        yield return new SmsCheck("the EEPROM cartridge offers a save from the start (HasBattery) and its save is the chip's 128 bytes, erased", cart.HasBattery && cart.ExportSave().Length == 128 && cart.ExportSave().All(b => b == 0xFF), "");
        yield return new SmsCheck("with $FFFC bit 3 clear slot 2 is ROM, with it set the window is the chip (a read no longer returns the ROM bank)", cart.Read(0x8000) == 2 && SetWindow(cart), "");

        var m = new Master(cart);
        bool ack = m.WriteByte(5, 0xA7);
        yield return new SmsCheck("a start, address 5 + write bit, data $A7 and a stop: the chip acknowledges both bytes", ack, ack ? "" : "no acknowledge");
        var m2 = new Master(cart, poison);
        yield return new SmsCheck("a random read of address 5 returns the byte just written", m2.ReadByte(5) == 0xA7, $"read {m2.ReadByte(5):X2}");
        yield return new SmsCheck("an address nobody wrote reads as erased ($FF)", m2.ReadByte(77) == 0xFF, "");
        yield return new SmsCheck("the save file holds the byte at its address (the cartridge reports the memory as written)", cart.ExportSave()[5] == 0xA7 && cart.Eeprom!.Dirty, "");

        // page write: address wraps inside the 8-byte page
        m2.Start(); bool pa = m2.Send(6 << 1); bool p1 = m2.Send(0x11), p2 = m2.Send(0x22), p3 = m2.Send(0x33); m2.Stop();
        bool wrapped = m2.ReadByte(6) == 0x11 && m2.ReadByte(7) == 0x22 && m2.ReadByte(0) == 0x33 && m2.ReadByte(8) == 0xFF;
        yield return new SmsCheck("a write of three bytes from address 6 lands at 6, 7 and then wraps inside the 8-byte page to 0, not on to 8", pa && p1 && p2 && p3 && wrapped, "");

        // sequential read: the master acknowledges to get the next byte
        m2.Start(); m2.Send((6 << 1) | 1); int r0 = m2.Receive(true), r1 = m2.Receive(true), r2 = m2.Receive(false); m2.Stop();
        yield return new SmsCheck("a sequential read (acknowledging each byte) returns the bytes at 6, 7 and 0", r0 == 0x11 && r1 == 0x22 && r2 == 0x33, $"{r0:X2} {r1:X2} {r2:X2}");

        // an interrupted transfer: a stop in the middle of the address leaves memory alone and the next transfer works
        m2.Start(); m2.Send(0x7F, 5); m2.Stop();
        yield return new SmsCheck("a stop in the middle of a command does nothing, and the next command is answered", m2.ReadByte(5) == 0xA7, "");

        // the save file round trip
        byte[] saved = cart.ExportSave();
        var other = new SmsCartridge(Banks(), SmsMapperKind.SegaEeprom);
        other.ImportSave(saved);
        yield return new SmsCheck("a save imported into a fresh cartridge is what a program then reads from the chip", new Master(other).ReadByte(5) == 0xA7 && new Master(other).ReadByte(6) == 0x11, "");

        // state: saved in the middle of a transfer, loaded into another cartridge, the transfer finishes there
        var a = new SmsCartridge(Banks(), SmsMapperKind.SegaEeprom); var ma = new Master(a);
        ma.Start(); ma.Send(9 << 1);                                    // address byte sent and acknowledged; the data byte next
        byte[] state; using (var ms = new MemoryStream()) { using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) a.SaveState(w); state = ms.ToArray(); }
        var b = new SmsCartridge(Banks(), SmsMapperKind.SegaEeprom);
        using (var r = new BinaryReader(new MemoryStream(state))) b.LoadState(r);
        var mb = new Master(b, false, resume: true);                     // the chip keeps its place in the transfer
        bool done = mb.Send(0x5C); mb.Stop();
        yield return new SmsCheck("a state saved between the address byte and the data byte, loaded into another cartridge, takes the data byte and writes it", done && new Master(b).ReadByte(9) == 0x5C, "");
    }

    private static bool SetWindow(SmsCartridge cart)
    {
        cart.Write(0xFFFC, 0x08);
        bool window = cart.Read(0x8000) != 2 && cart.Read(0x8000) != 0;   // not a ROM bank number: the chip's byte (a high data line and a clock bit)
        cart.Write(0xFFFC, 0x00);
        return window && cart.Read(0x8000) == 2;
    }
}
