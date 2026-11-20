using System;
using System.IO;

namespace cor64.PIF
{
    // [quirk_fix1] Transfer Pak (COR64_PIF_PAK=transfer on a port; COR64_PIF_TPAK=0 turns "transfer" back into "none").
    // Pak register map adapted from ares (ISC/MIT, n64/controller/gamepad/transfer-pak.cpp, itself from BizHawk):
    //   0x8000-0x9FFF  write 0x84 = power on, 0xFE = power off; read 0x84 when on (0 when off)
    //   0xA000-0xAFFF  GB address bank (0..3) for the 0xC000 window
    //   0xB000-0xBFFF  status: b0 cart access enabled, b2-3 reset state, b6 no cartridge, b7 pak powered;
    //                  write b0 = enable cart access (a rising edge powers the GB cartridge)
    //   0xC000-0xFFFF  GB bus 0x4000*bank + (addr - 0xC000)
    // With no GB ROM (COR64_TPAK_GBROM unset or missing) status bit 6 says "no cartridge" and cart reads return 0.
    // COR64_TPAK_GBROM=<path to .gb/.gbc>  COR64_TPAK_GBSAVE=<path> (default: the ROM path with .sav). Saves on RAM writes.
    public sealed class TransferPak
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("COR64_PIF_TPAK") != "0";

        private static readonly string s_NoCartStyle = Environment.GetEnvironmentVariable("COR64_TPAK_NOCART");
        private bool m_PakEnable, m_CartEnable;
        private int m_Bank, m_ResetState;
        private readonly GbCart m_Cart;

        public TransferPak()
        {
            var rom = Environment.GetEnvironmentVariable("COR64_TPAK_GBROM");
            if (!string.IsNullOrEmpty(rom) && File.Exists(rom)) {
                var save = Environment.GetEnvironmentVariable("COR64_TPAK_GBSAVE");
                if (string.IsNullOrEmpty(save)) save = Path.ChangeExtension(rom, ".sav");
                try { m_Cart = new GbCart(File.ReadAllBytes(rom), save); } catch (Exception) { m_Cart = null; }
            }
        }

        public bool HasCart => m_Cart != null;

        public byte Read(int address)
        {
            address &= 0x7FFF;
            if (!m_PakEnable) return 0;
            if (address <= 0x1FFF) return 0x84;
            if (address <= 0x2FFF) return (byte)m_Bank;
            if (address <= 0x3FFF) {
                int s = (m_CartEnable ? 1 : 0) | (m_ResetState & 3) << 2 | (m_Cart == null ? 0x40 : 0) | 0x80;
                if (m_Cart == null && s_NoCartStyle == "cen64") s = 0x40;
                if (m_Cart == null && s_NoCartStyle == "mupen") s = (m_ResetState & 3) << 2 | 0x80;
                if (m_CartEnable && m_ResetState == 3) m_ResetState = 2;
                else if (!m_CartEnable && m_ResetState == 2) m_ResetState = 1;
                else if (!m_CartEnable && m_ResetState == 1) m_ResetState = 0;
                return (byte)s;
            }
            if (!m_CartEnable || m_Cart == null) return 0;
            return m_Cart.Read(0x4000 * m_Bank + address - 0x4000);
        }

        public void Write(int address, byte data)
        {
            address &= 0x7FFF;
            if (address <= 0x1FFF) {
                bool was = m_PakEnable;
                if (data == 0x84) m_PakEnable = true;
                else if (data == 0xFE) m_PakEnable = false;
                if (!was && m_PakEnable) { m_Bank = 3; m_CartEnable = false; m_ResetState = 0; if (CompatLog.On) CompatLog.Note("tpak:power-on", ""); }
                return;
            }
            if (!m_PakEnable) return;
            if (address <= 0x2FFF) { m_Bank = data > 3 ? 0 : data; return; }
            if (address <= 0x3FFF) {
                bool was = m_CartEnable;
                m_CartEnable = (data & 1) != 0;
                if (!was && m_CartEnable) { m_ResetState = 3; m_Cart?.Power(); if (CompatLog.On) CompatLog.Note("tpak:cart-on", m_Cart != null ? "cart" : "no cart"); }
                return;
            }
            if (!m_CartEnable || m_Cart == null) return;
            m_Cart.Write(0x4000 * m_Bank + address - 0x4000, data);
        }

        public void Flush() => m_Cart?.Flush();
    }

    // Minimal Game Boy cartridge (our own code from the Pan Docs behaviour): ROM only, MBC1, MBC2, MBC3 (with the
    // real-time clock, below), MBC5. Address = GB bus address 0x0000-0xFFFF; only ROM 0000-7FFF and RAM A000-BFFF matter.
    public sealed class GbCart
    {
        private readonly byte[] m_Rom, m_Ram;
        private readonly string m_SavePath;
        private readonly int m_Mbc; // 0 none, 1, 2, 3, 5
        private int m_RomBank = 1, m_RamBank, m_Upper;
        private bool m_RamOn, m_Mode1, m_Dirty;

        public GbCart(byte[] rom, string savePath)
        {
            m_Rom = rom.Length >= 0x8000 ? rom : Resize(rom, 0x8000);
            int t = m_Rom[0x147];
            m_Mbc = t >= 0x01 && t <= 0x03 ? 1 : t == 0x05 || t == 0x06 ? 2 : t >= 0x0F && t <= 0x13 ? 3 : t >= 0x19 && t <= 0x1E ? 5 : 0;
            int ram = m_Mbc == 2 ? 512 : m_Rom[0x149] switch { 1 => 0x800, 2 => 0x2000, 3 => 0x8000, 4 => 0x20000, 5 => 0x10000, _ => 0 };
            m_Ram = new byte[ram];
            m_SavePath = savePath;
            if (savePath != null && File.Exists(savePath)) {
                var b = File.ReadAllBytes(savePath);
                Array.Copy(b, m_Ram, Math.Min(b.Length, m_Ram.Length));
                if (m_Mbc == 3 && b.Length >= m_Ram.Length + 48) LoadRtc(b, m_Ram.Length);
            }
            if (m_Mbc == 3 && m_RtcBase == 0) m_RtcBase = Now();
        }

        // [quirk_fix2] MBC3 real-time clock (COR64_TPAK_RTC=1 default; 0 = old behaviour, registers read 0).
        // Our own code from the Pan Docs behaviour: registers 08 S, 09 M, 0A H, 0B day low, 0C day high (bit 0 day 8,
        // bit 6 halt, bit 7 day carry); writing 00 then 01 to 6000-7FFF latches. The clock runs on host time like the
        // cart's battery clock (COR64_TPAK_RTC=frozen: it only moves when the game writes it, for deterministic runs).
        // Saved after the RAM in the common 48-byte footer (BGB/VBA layout: 5 live u32, 5 latched u32, u64 unix time).
        private static readonly string s_RtcEnv = Environment.GetEnvironmentVariable("COR64_TPAK_RTC");
        private static readonly bool s_Rtc = s_RtcEnv != "0";
        private static readonly bool s_RtcFrozen = s_RtcEnv == "frozen";
        public static Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private long Now() => s_RtcFrozen ? 0 : Clock();
        private long m_RtcBase;          // unix time at which the counter was 0 (counter = seconds since then, unless halted)
        private long m_RtcHaltValue;     // counter while halted
        private bool m_RtcHalt, m_RtcCarry;
        private readonly byte[] m_RtcLatch = new byte[5];
        private int m_LatchPrev = -1;
        public bool RtcOn => s_Rtc && m_Mbc == 3;

        private long RtcCounter => m_RtcHalt || s_RtcFrozen ? m_RtcHaltValue : Math.Max(0, Now() - m_RtcBase);
        private void SetCounter(long c)
        {
            if (m_RtcHalt || s_RtcFrozen) m_RtcHaltValue = c; else m_RtcBase = Now() - c;
        }
        private void Normalize(ref long c) { const long max = 512L * 86400; while (c >= max) { c -= max; m_RtcCarry = true; } }
        private byte[] RtcRegs()
        {
            long c = RtcCounter; Normalize(ref c); if (!m_RtcHalt && !s_RtcFrozen) m_RtcBase = Now() - c; else m_RtcHaltValue = c;
            long d = c / 86400;
            return new[] { (byte)(c % 60), (byte)(c / 60 % 60), (byte)(c / 3600 % 24), (byte)(d & 0xFF),
                (byte)((d >> 8 & 1) | (m_RtcHalt ? 0x40 : 0) | (m_RtcCarry ? 0x80 : 0)) };
        }
        private void RtcWrite(int reg, byte v)
        {
            var r = RtcRegs();
            long c = RtcCounter;
            long s = r[0], m = r[1], h = r[2], d = r[3] | (r[4] & 1) << 8;
            switch (reg) {
                case 0: s = v % 60; break;
                case 1: m = v % 60; break;
                case 2: h = v % 24; break;
                case 3: d = (d & 0x100) | v; break;
                case 4:
                    d = (d & 0xFF) | (v & 1) << 8; m_RtcCarry = (v & 0x80) != 0;
                    bool halt = (v & 0x40) != 0;
                    long cur = ((d * 24 + h) * 60 + m) * 60 + s;
                    if (halt && !m_RtcHalt) { m_RtcHalt = true; m_RtcHaltValue = cur; }
                    else if (!halt && m_RtcHalt) { m_RtcHalt = false; m_RtcBase = Now() - cur; }
                    else SetCounter(cur);
                    m_Dirty = true; return;
            }
            SetCounter(((d * 24 + h) * 60 + m) * 60 + s);
            m_Dirty = true;
        }
        private void LoadRtc(byte[] b, int o)
        {
            uint U(int i) => BitConverter.ToUInt32(b, o + i * 4);
            for (int i = 0; i < 5; i++) m_RtcLatch[i] = (byte)U(5 + i);
            long t = BitConverter.ToInt64(b, o + 40);
            long d = U(3) | (U(4) & 1) << 8;
            long c = ((d * 24 + U(2)) * 60 + U(1)) * 60 + U(0);
            m_RtcHalt = (U(4) & 0x40) != 0; m_RtcCarry = (U(4) & 0x80) != 0;
            if (m_RtcHalt || s_RtcFrozen) m_RtcHaltValue = c; else m_RtcBase = t - c; // the clock kept running while saved
        }
        private byte[] RtcFooter()
        {
            var f = new byte[48]; var r = RtcRegs();
            for (int i = 0; i < 5; i++) { BitConverter.GetBytes((uint)r[i]).CopyTo(f, i * 4); BitConverter.GetBytes((uint)m_RtcLatch[i]).CopyTo(f, 20 + i * 4); }
            BitConverter.GetBytes(s_RtcFrozen ? 0L : Clock()).CopyTo(f, 40);
            return f;
        }

        private static byte[] Resize(byte[] b, int n) { var r = new byte[n]; Array.Copy(b, r, Math.Min(b.Length, n)); return r; }

        public void Power() { m_RomBank = 1; m_RamBank = 0; m_Upper = 0; m_RamOn = false; m_Mode1 = false; }

        private int RomBankCount => Math.Max(1, m_Rom.Length / 0x4000);

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) {
                int bank = m_Mbc == 1 && m_Mode1 ? (m_Upper << 5) % RomBankCount : 0;
                return m_Rom[(bank * 0x4000 + a) % m_Rom.Length];
            }
            if (a < 0x8000) {
                int bank = m_Mbc switch {
                    1 => (m_Upper << 5 | (m_RomBank == 0 ? 1 : m_RomBank)),
                    2 or 3 => m_RomBank == 0 ? 1 : m_RomBank,
                    5 => m_RomBank,
                    _ => 1,
                } % RomBankCount;
                return m_Rom[(bank * 0x4000 + a - 0x4000) % m_Rom.Length];
            }
            if (a >= 0xA000 && a < 0xC000) {
                if (!m_RamOn || m_Ram.Length == 0) return 0xFF;
                if (m_Mbc == 3 && m_RamBank >= 0x08) return RtcOn && m_RamBank <= 0x0C ? m_RtcLatch[m_RamBank - 8] : (byte)0;
                if (m_Mbc == 2) return (byte)(0xF0 | m_Ram[(a - 0xA000) & 0x1FF]);
                return m_Ram[RamOffset(a)];
            }
            return 0xFF;
        }

        private int RamOffset(int a)
        {
            int bank = m_Mbc == 1 ? (m_Mode1 ? m_Upper : 0) : m_Mbc == 0 ? 0 : m_RamBank;
            return (bank * 0x2000 + a - 0xA000) % m_Ram.Length;
        }

        public void Write(int a, byte d)
        {
            a &= 0xFFFF;
            if (a < 0x8000) {
                switch (m_Mbc) {
                    case 1:
                        if (a < 0x2000) m_RamOn = (d & 0x0F) == 0x0A;
                        else if (a < 0x4000) m_RomBank = d & 0x1F;
                        else if (a < 0x6000) m_Upper = d & 3;
                        else m_Mode1 = (d & 1) != 0;
                        break;
                    case 2:
                        if (a < 0x4000) { if ((a & 0x100) == 0) m_RamOn = (d & 0x0F) == 0x0A; else m_RomBank = d & 0x0F; }
                        break;
                    case 3:
                        if (a < 0x2000) m_RamOn = (d & 0x0F) == 0x0A;
                        else if (a < 0x4000) m_RomBank = d & 0x7F;
                        else if (a < 0x6000) m_RamBank = d & 0x0F;
                        else if (RtcOn) { if (m_LatchPrev == 0 && d == 1) RtcRegs().CopyTo(m_RtcLatch, 0); m_LatchPrev = d; }
                        break;
                    case 5:
                        if (a < 0x2000) m_RamOn = (d & 0x0F) == 0x0A;
                        else if (a < 0x3000) m_RomBank = (m_RomBank & 0x100) | d;
                        else if (a < 0x4000) m_RomBank = (m_RomBank & 0xFF) | (d & 1) << 8;
                        else if (a < 0x6000) m_RamBank = d & 0x0F;
                        break;
                }
                return;
            }
            if (a >= 0xA000 && a < 0xC000 && m_RamOn && m_Ram.Length > 0) {
                if (m_Mbc == 3 && m_RamBank >= 0x08) { if (RtcOn && m_RamBank <= 0x0C) { RtcWrite(m_RamBank - 8, d); m_RtcLatch[m_RamBank - 8] = d; } return; }
                if (m_Mbc == 2) m_Ram[(a - 0xA000) & 0x1FF] = (byte)(d & 0x0F);
                else m_Ram[RamOffset(a)] = d;
                m_Dirty = true;
            }
        }

        public void Flush()
        {
            if (!m_Dirty || m_SavePath == null) return;
            m_Dirty = false;
            try {
                if (RtcOn) { var o = new byte[m_Ram.Length + 48]; m_Ram.CopyTo(o, 0); RtcFooter().CopyTo(o, m_Ram.Length); File.WriteAllBytes(m_SavePath, o); }
                else File.WriteAllBytes(m_SavePath, m_Ram);
            } catch (Exception) { }
        }
    }
}
