// [cer64plus] Cartridge save memory on PI domain 2, address 2 (0x08000000-0x0FFFFFFF): SRAM (32 KB, or 96 KB in three
// 32 KB banks at 0x08000000/0x08040000/0x08080000) and FlashRAM (128 KB, Macronix-style command/status protocol).
// Also the per-game save-type database (SaveDb) and the EEPROM size choice (4 Kbit / 16 Kbit) handed to the PIF.
//
// Switch: COR64_CARTSAVES=0 restores the old behaviour (no domain-2 device, EEPROM always 4 Kbit).
//
// Not part of savestates on purpose (the state format is unchanged): the contents travel through the battery save API
// (N64Session.ExportSave/ImportSave), like the EEPROM does for Reset.
//
// FlashRAM protocol (written from the libultra-visible behaviour; ares (MIT) consulted for the ID value):
//   CPU write32 0x08010000 = command << 24 | arg:
//     0xD2 status mode         0xE1 identify mode (PI DMA read returns the 8-byte silicon ID)
//     0xF0 read-array mode     (PI DMA read: flash byte offset = cart - 0x08000000; a *2 mapping was tried and
//                              broke Pokemon Stadium 2, which re-formatted its save on every boot; see FINDINGS.md)
//     0x4B sector erase setup  (page = arg & 0xFFFF; erases the 16 KB sector holding page*128)
//     0x3C chip erase setup    0x78 run the erase (status |= 0x08)
//     0xB4 page-buffer mode    (PI DMA write fills the 128-byte page buffer)
//     0xA5 program page        (page = arg & 0xFFFF; page buffer -> flash; status |= 0x04)
//   CPU read32 0x08000000 = status flags (libultra keeps the low byte); CPU write32 0x08000000 = status (clear).
// Operations complete instantly (no busy period).
using System;
using System.Collections.Generic;

namespace cor64.IO
{
    public enum CartSaveType { None, Eeprom4K, Eeprom16K, Sram32K, Sram96K, FlashRam }

    public static class SaveDb
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("COR64_CARTSAVES") != "0";

        private static readonly bool s_Eep16Fix = Environment.GetEnvironmentVariable("COR64_SAVEDB_EEP16") != "0";

        private static readonly bool s_JfFlash = Environment.GetEnvironmentVariable("COR64_SAVEDB_JFFLASH") != "0";

        // Key: the two-letter game id in the header (bytes 0x3C-0x3D), all regions. Facts from cartridge hardware.
        private static readonly Dictionary<string, CartSaveType> s_Db = new() {
            // EEPROM 4 Kbit
            ["SM"] = CartSaveType.Eeprom4K, // Super Mario 64
            ["KT"] = CartSaveType.Eeprom4K, // Mario Kart 64
            ["FX"] = CartSaveType.Eeprom4K, // Star Fox 64 / Lylat Wars
            ["WR"] = CartSaveType.Eeprom4K, // Wave Race 64
            ["LB"] = CartSaveType.Eeprom4K, // Mario Party
            ["MW"] = CartSaveType.Eeprom4K, // Mario Party 2
            ["GE"] = CartSaveType.Eeprom4K, // GoldenEye 007
            ["N6"] = CartSaveType.Eeprom4K, // Dr. Mario 64
            ["BC"] = CartSaveType.Eeprom4K, // Blast Corps
            ["PW"] = CartSaveType.Eeprom4K, // Pilotwings 64
            ["BK"] = CartSaveType.Eeprom4K, // Banjo-Kazooie
            ["RS"] = CartSaveType.Eeprom4K, // Star Wars: Rogue Squadron
            ["NA"] = CartSaveType.Eeprom4K, // Star Wars Episode I: Battle for Naboo
            ["IJ"] = CartSaveType.Eeprom4K, // Indiana Jones and the Infernal Machine
            ["WO"] = CartSaveType.Eeprom4K, // World Driver Championship (ares: no cart save, Controller Pak only; kept:
                                            // an unused EEPROM is harmless and there is no "no save" type here)
            ["MO"] = CartSaveType.Eeprom4K, // Monopoly (was labelled Mischief Makers; ares' table)
            ["TM"] = CartSaveType.Eeprom4K, // Mischief Makers / Yuke Yuke!! Trouble Makers (ares' table)
            ["SW"] = CartSaveType.Eeprom4K, // Star Wars: Shadows of the Empire
            ["DY"] = CartSaveType.Eeprom4K, // Diddy Kong Racing (was 16K: its osEepromRead wants type 0x8000 and, on any
                                            // other answer, returns without releasing SI access, so the next read deadlocks)
            // EEPROM 16 Kbit
            ["YS"] = CartSaveType.Eeprom16K, // Yoshi's Story
            ["PD"] = CartSaveType.Eeprom16K, // Perfect Dark
            ["DO"] = CartSaveType.Eeprom16K, // Donkey Kong 64
            ["FU"] = CartSaveType.Eeprom16K, // Conker's Bad Fur Day
            ["B7"] = CartSaveType.Eeprom16K, // Banjo-Tooie
            ["MX"] = CartSaveType.Eeprom16K, // Excitebike 64
            ["MV"] = CartSaveType.Eeprom16K, // Mario Party 3
            ["K4"] = CartSaveType.Eeprom16K, // Kirby 64 (U/E, J rev 2+); J rev 0/1 is SRAM 32 KB, see Lookup (ares/cen64 facts)
            ["M8"] = CartSaveType.Eeprom16K, // Mario Tennis (16 Kbit EEPROM per ares/cen64 databases; was SRAM)
            // SRAM 32 KB
            ["ZL"] = CartSaveType.Sram32K, // Zelda: Ocarina of Time
            ["AL"] = CartSaveType.Sram32K, // Super Smash Bros.
            ["FZ"] = CartSaveType.Sram32K, // F-Zero X
            ["B5"] = CartSaveType.Sram32K, // Resident Evil 2 / BioHazard 2
            ["MF"] = CartSaveType.Sram32K, // Mario Golf
            ["TE"] = CartSaveType.Sram32K, // 1080 Snowboarding
            // FlashRAM 128 KB
            ["ZS"] = CartSaveType.FlashRam, // Zelda: Majora's Mask
            ["PF"] = CartSaveType.FlashRam, // Pokemon Snap
            ["P3"] = CartSaveType.FlashRam, // Pokemon Stadium 2 / Stadium Kin Gin
            ["PO"] = CartSaveType.FlashRam, // Pokemon Stadium (US/EU)
            ["MQ"] = CartSaveType.FlashRam, // Paper Mario
            ["PN"] = CartSaveType.FlashRam, // Pokemon Puzzle League
            // [emu_fix3] Jet Force Gemini: FlashRAM 128 KB (ares NJF MX29L1100; mupen64plus agrees). It was EEPROM 16 Kbit:
            // "Game Saved" went nowhere and a reset showed New Game. COR64_SAVEDB_JFFLASH=0: the old EEPROM 16 Kbit.
        };

        /// <summary>Save type for a 4-character header game code (e.g. "NSME"); None when unknown.</summary>
        // [quirk_fix1] Kirby 64 Japan: revisions 0/1 (header byte 0x3F) save to SRAM 32 KB, revision 2+ to EEPROM 16 Kbit
        // (ares/cen64 databases). COR64_SAVEDB_K4REV=0: every Japanese Kirby 64 is SRAM (the old rule).
        private static readonly bool s_K4Rev = Environment.GetEnvironmentVariable("COR64_SAVEDB_K4REV") != "0";

        public static CartSaveType Lookup(string gameCode, int revision)
        {
            if (s_K4Rev && s_Eep16Fix && revision >= 2 && gameCode != null && gameCode.Length > 3 && gameCode.Substring(1, 2) == "K4" && gameCode[3] == 'J')
                return CartSaveType.Eeprom16K;
            return Lookup(gameCode);
        }

        public static int Revision(byte[] rom)
        {
            if (rom == null || rom.Length < 0x40) return 0;
            int sw = rom[0] == 0x80 ? 0 : rom[0] == 0x37 ? 1 : rom[0] == 0x40 ? 3 : 0;
            return rom[0x3F ^ sw];
        }

        public static CartSaveType Lookup(string gameCode)
        {
            if (gameCode == null || gameCode.Length < 3) return CartSaveType.None;
            string id = gameCode.Substring(1, 2);
            // COR64_SAVEDB_EEP16=0: the old table (Mario Tennis and Kirby 64 as SRAM 32 KB)
            if ((id == "M8" || id == "K4") && !s_Eep16Fix) return CartSaveType.Sram32K;
            if (id == "JF") return s_JfFlash ? CartSaveType.FlashRam : CartSaveType.Eeprom16K;
            if (id == "K4" && gameCode.Length > 3 && gameCode[3] == 'J') return CartSaveType.Sram32K; // J revisions 0/1 (revision byte not seen here)
            if (s_Db.TryGetValue(id, out var t)) return t;
            return SaveDbFull.TryLookup(gameCode, out var f) ? f : CartSaveType.None; // [catalog_fix1] COR64_SAVEDB_FULL
        }

        public static string GameCode(byte[] rom)
        {
            if (rom == null || rom.Length < 0x40) return null;
            int sw = rom[0] == 0x80 ? 0 : rom[0] == 0x37 ? 1 : rom[0] == 0x40 ? 3 : 0; // z64 / v64 / n64 byte order
            var c = new char[4];
            for (int i = 0; i < 4; i++) c[i] = (char)rom[(0x3B + i) ^ sw];
            return new string(c);
        }
    }

    public sealed class CartSave
    {
        public const uint Base = 0x08000000;
        private const int FlashSize = 0x20000;

        public CartSaveType Type { get; private set; } // None = not known yet: the first access picks SRAM or FlashRAM
        private byte[] m_Data = Array.Empty<byte>();

        // FlashRAM
        private enum FlashMode { Status, Id, Read, Erase, PageBuffer, Program }
        private FlashMode m_Mode;
        private uint m_Status;
        private int m_EraseOffset, m_EraseLength;
        private readonly byte[] m_Page = new byte[128];
        private static readonly byte[] s_FlashId = { 0x11, 0x11, 0x80, 0x01, 0x00, 0xC2, 0x00, 0x1E };

        public CartSave(CartSaveType type) { SetType(type); }

        /// <summary>Bytes the game has written (DMA, CPU stores, flash program/erase) since power-on or import; diagnostics.</summary>
        public long Writes;

        public bool IsDomain2 => Type == CartSaveType.Sram32K || Type == CartSaveType.Sram96K || Type == CartSaveType.FlashRam;
        public bool Auto => Type == CartSaveType.None;

        public void SetType(CartSaveType type)
        {
            Type = type;
            int n = type switch { CartSaveType.Sram32K => 0x8000, CartSaveType.Sram96K => 0x18000, CartSaveType.FlashRam => FlashSize, _ => 0 };
            var old = m_Data;
            m_Data = new byte[n];
            if (type == CartSaveType.FlashRam) Array.Fill(m_Data, (byte)0xFF);
            Array.Copy(old, m_Data, Math.Min(old.Length, n));
            ResetFlash();
        }

        private void ResetFlash() { m_Mode = FlashMode.Status; m_Status = 0; m_EraseOffset = 0; m_EraseLength = 0; Array.Clear(m_Page); }

        public byte[] Export() => (byte[])m_Data.Clone();

        public void Import(byte[] data)
        {
            if (Auto && data != null) {
                if (data.Length == FlashSize) SetType(CartSaveType.FlashRam);
                else if (data.Length == 0x18000) SetType(CartSaveType.Sram96K);
                else if (data.Length == 0x8000) SetType(CartSaveType.Sram32K);
            }
            if (Type == CartSaveType.FlashRam) Array.Fill(m_Data, (byte)0xFF); else Array.Clear(m_Data);
            if (data != null) Array.Copy(data, m_Data, Math.Min(data.Length, m_Data.Length));
            ResetFlash();
            Writes = 0;
        }

        // ---- heuristics for games not in the database ----
        private void AutoPick(bool flash)
        {
            if (!Auto) return;
            SetType(flash ? CartSaveType.FlashRam : CartSaveType.Sram32K);
        }

        private int SramIndex(uint off)
        {
            int bank = (int)(off >> 18) & 3; // 96 KB: banks at +0, +0x40000, +0x80000
            int i = bank * 0x8000 + (int)(off & 0x7FFF);
            return i < m_Data.Length ? i : -1;
        }

        // ---- CPU access (big-endian bus values) ----
        public uint Read32(uint address)
        {
            if (s_Trace) Console.WriteLine($"SAVE cpuread {address:X8} mode={m_Mode} status={m_Status:X2}");
            uint off = address - Base;
            if (Auto) AutoPick(true); // libultra drives FlashRAM by CPU I/O; SRAM only by DMA
            if (Type == CartSaveType.FlashRam) {
                return m_Mode == FlashMode.Read ? Be32(FlashReadIndex(off & ~3u)) : m_Status;
            }
            int i = SramIndex(off & ~3u);
            return i < 0 ? 0 : Be32(i);
        }

        public void Write32(uint address, uint value)
        {
            uint off = address - Base;
            if (Auto) AutoPick(true); // libultra drives FlashRAM by CPU I/O; SRAM only by DMA
            if (Type == CartSaveType.FlashRam) {
                if (off >= 0x10000) FlashCommand(value);
                else m_Status = value & 0xFF;
                return;
            }
            int i = SramIndex(off & ~3u);
            if (i < 0) return;
            Writes += 4; m_Data[i] = (byte)(value >> 24); m_Data[i + 1] = (byte)(value >> 16); m_Data[i + 2] = (byte)(value >> 8); m_Data[i + 3] = (byte)value;
        }

        public byte Read8(uint address) => (byte)(Read32(address & ~3u) >> (int)((3 - (address & 3)) * 8));
        public ushort Read16(uint address) => (ushort)(Read32(address & ~3u) >> (int)((2 - (address & 2)) * 8));
        public ulong Read64(uint address) => ((ulong)Read32(address) << 32) | Read32(address + 4);

        public void Write8(uint address, byte value)
        {
            if (Type == CartSaveType.FlashRam || Auto) { Write32(address & ~3u, (uint)value << (int)((3 - (address & 3)) * 8)); return; }
            int i = SramIndex(address - Base); if (i >= 0) m_Data[i] = value;
        }

        public void Write16(uint address, ushort value)
        {
            if (Type == CartSaveType.FlashRam || Auto) { Write32(address & ~3u, (uint)value << (int)((2 - (address & 2)) * 8)); return; }
            int i = SramIndex(address - Base); if (i >= 0 && i + 1 < m_Data.Length) { m_Data[i] = (byte)(value >> 8); m_Data[i + 1] = (byte)value; }
        }

        public void Write64(uint address, ulong value) { Write32(address, (uint)(value >> 32)); Write32(address + 4, (uint)value); }

        private uint Be32(int i) => i < 0 || i + 3 >= m_Data.Length ? 0 : (uint)(m_Data[i] << 24 | m_Data[i + 1] << 16 | m_Data[i + 2] << 8 | m_Data[i + 3]);

        // [pibe_saves] the ID we report (00C2001E, MX29L1100) is a word-indexed part: in read-array mode the PI offset is
        // in 16-bit units, so the byte index is offset << 1 (ares cartridge/flash.cpp arrayOffset). The old raw PI copy
        // delivered the ID word-swapped, so games fell back to byte addressing; COR64_CPUDIFF_PIBE=0 keeps that.
        private static readonly bool s_WordIndexed = Environment.GetEnvironmentVariable("COR64_CPUDIFF_PIBE") != "0";
        private int FlashReadIndex(uint off) => (int)((s_WordIndexed ? off << 1 : off) & (FlashSize - 1));

        private static readonly bool s_Trace = Environment.GetEnvironmentVariable("COR64_CARTSAVES_TRACE") == "1";
        private void FlashCommand(uint value)
        {
            if (s_Trace) Console.WriteLine($"FLASH cmd {value:X8} mode={m_Mode} status={m_Status:X2}");
            int page = (int)(value & 0xFFFF);
            switch (value >> 24) {
                case 0xD2: m_Mode = FlashMode.Status; break;
                case 0xE1: m_Mode = FlashMode.Id; break;
                case 0xF0: m_Mode = FlashMode.Read; break;
                case 0x4B: m_Mode = FlashMode.Erase; m_EraseOffset = (page * 128) & (FlashSize - 1) & ~0x3FFF; m_EraseLength = 0x4000; break;
                case 0x3C: m_Mode = FlashMode.Erase; m_EraseOffset = 0; m_EraseLength = FlashSize; break;
                case 0x78:
                    if (m_Mode == FlashMode.Erase) { Array.Fill(m_Data, (byte)0xFF, m_EraseOffset, m_EraseLength); m_Status |= 0x08; Writes += m_EraseLength; }
                    m_Mode = FlashMode.Status;
                    break;
                case 0xB4: m_Mode = FlashMode.PageBuffer; break;
                case 0xA5: {
                    int o = (page * 128) & (FlashSize - 1);
                    Array.Copy(m_Page, 0, m_Data, o, 128); Writes += 128;
                    m_Status |= 0x04;
                    m_Mode = FlashMode.Status;
                    break;
                }
                default: break;
            }
        }

        // ---- PI DMA ----
        /// <summary>Cart -> RDRAM (PI_WR_LEN): fills buffer[index..index+count) from cart address.</summary>
        // [emu_fix4] COR64_FLASH_DMACONTIG (default on): a PI DMA is split into 128-byte blocks, and each block used to
        // double its own offset (block 2 read 0x100 instead of 0x80, skipping half of every file: MM's owl save). ares
        // (ISC) flash.cpp doubles only the DMA start offset and reads contiguously from there; dmaStart passes that start.
        private static readonly bool s_FlashContig = Environment.GetEnvironmentVariable("COR64_FLASH_DMACONTIG") != "0";
        public void DmaRead(uint cart, byte[] buffer, int index, int count, uint dmaStart = uint.MaxValue)
        {
            if (s_Trace) Console.WriteLine($"SAVE dmaread {cart:X8} n={count} mode={m_Mode}");
            uint off = cart - Base;
            if (Auto) AutoPick(false);
            if (Type == CartSaveType.FlashRam) {
                int rbase = FlashReadIndex(off);
                if (s_FlashContig && dmaStart != uint.MaxValue && cart >= dmaStart)
                    rbase = (int)((uint)FlashReadIndex(dmaStart - Base) + (cart - dmaStart));
                for (int k = 0; k < count; k++) {
                    byte b = m_Mode switch {
                        FlashMode.Read => m_Data[rbase + k & (FlashSize - 1)],
                        FlashMode.Id => s_FlashId[k & 7],
                        _ => (k & 3) == 3 ? (byte)m_Status : (byte)0,
                    };
                    buffer[index + k] = b;
                }
                return;
            }
            for (int k = 0; k < count; k++) { int i = SramIndex(off + (uint)k); buffer[index + k] = i < 0 ? (byte)0 : m_Data[i]; }
        }

        /// <summary>RDRAM -> cart (PI_RD_LEN).</summary>
        public void DmaWrite(uint cart, byte[] buffer, int count)
        {
            if (s_Trace) Console.WriteLine($"SAVE dmawrite {cart:X8} n={count} mode={m_Mode}");
            uint off = cart - Base;
            if (Auto) AutoPick(false);
            if (Type == CartSaveType.FlashRam) {
                if (m_Mode == FlashMode.PageBuffer) for (int k = 0; k < count; k++) m_Page[(int)(off + k) & 127] = buffer[k];
                return;
            }
            Writes += count;
            for (int k = 0; k < count; k++) { int i = SramIndex(off + (uint)k); if (i >= 0) m_Data[i] = buffer[k]; }
        }
    }
}
