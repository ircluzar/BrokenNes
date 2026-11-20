using System;

namespace cor64.PIF
{
    // [catalog_fix3] COR64_CART_RTC (default on; =0 off): the cartridge real-time clock on joybus channel 4, commands
    // 0x06 status, 0x07 read block, 0x08 write block. Behaviour after ares n64/cartridge/rtc.cpp and joybus.cpp (ISC
    // licence): 4 blocks of 8 bytes; block 0 holds the control word (bit 2 stops the clock, bits 8/9 write-protect blocks
    // 1/2), block 2 the BCD time (sec, min, hour|0x80, day, weekday, month, year % 100, century). Status bit 7 = stopped.
    // Games: Doubutsu no Mori (NAF). The clock starts at a fixed date and runs on emulated time (VI interrupts), so runs
    // stay deterministic; COR64_CART_RTC=host starts it from the host clock instead.
    public sealed class CartRtc
    {
        private static readonly string s_Mode = Environment.GetEnvironmentVariable("COR64_CART_RTC") ?? "1";
        private static readonly string[] s_Games = { "AF" }; // Doubutsu no Mori

        public static bool WantedFor(string gameCode) =>
            s_Mode != "0" && gameCode != null && gameCode.Length >= 3 && Array.IndexOf(s_Games, gameCode.Substring(1, 2)) >= 0;

        private readonly byte[] m_Ram = new byte[32];
        private byte m_Status;      // bit 7: stopped
        private int m_WriteLock;    // bit n: block n write protected
        private long m_LastVi;
        private double m_Frac;

        public CartRtc()
        {
            DateTime t = s_Mode == "host" ? DateTime.Now : new DateTime(2001, 4, 14, 12, 0, 0); // a Saturday
            m_Ram[16] = Bcd(t.Second); m_Ram[17] = Bcd(t.Minute); m_Ram[18] = (byte)(Bcd(t.Hour) | 0x80);
            m_Ram[19] = Bcd(t.Day); m_Ram[20] = Bcd((int)t.DayOfWeek); m_Ram[21] = Bcd(t.Month);
            m_Ram[22] = Bcd(t.Year % 100); m_Ram[23] = Bcd(t.Year / 100);
            m_LastVi = Telemetry.ViInterrupts;
        }

        private static byte Bcd(int v) => (byte)((v / 10) << 4 | v % 10);
        private static int Dec(int v) => (v >> 4) * 10 + (v & 15);

        private void Sync()
        {
            long vi = Telemetry.ViInterrupts;
            long d = vi - m_LastVi; m_LastVi = vi;
            if ((m_Status & 0x80) != 0 || d <= 0) return;
            double fps = 93750000.0 / Math.Max(1, cor64.RCP.Video.FieldTicks);
            m_Frac += d / fps;
            int secs = (int)m_Frac; m_Frac -= secs;
            if (secs > 0) Advance(secs);
        }

        private void Advance(int n)
        {
            int sec = Dec(m_Ram[16]), min = Dec(m_Ram[17]), hour = Dec(m_Ram[18] & 0x7F), day = Dec(m_Ram[19]);
            int wday = Dec(m_Ram[20]), mon = Dec(m_Ram[21]), year = Dec(m_Ram[22]) + 100 * Dec(m_Ram[23]);
            while (n-- > 0) {
                if (++sec < 60) continue;
                sec = 0; if (++min < 60) continue;
                min = 0; if (++hour < 24) continue;
                hour = 0; if (++wday == 7) wday = 0;
                int dim = mon >= 1 && mon <= 12 ? DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), mon) : 31;
                if (++day > dim) { day = 1; if (++mon == 13) { mon = 1; year++; } }
            }
            m_Ram[16] = Bcd(sec); m_Ram[17] = Bcd(min); m_Ram[18] = (byte)(Bcd(hour) | 0x80); m_Ram[19] = Bcd(day);
            m_Ram[20] = Bcd(wday); m_Ram[21] = Bcd(mon); m_Ram[22] = Bcd(year % 100); m_Ram[23] = Bcd(year / 100);
        }

        /// <summary>One channel-4 command. Returns false when it is not an RTC command (or is malformed).</summary>
        public bool Comm(byte[] input, int send, int recv, byte[] output)
        {
            if (send < 1) return false;
            switch (input[0]) {
                case 0x06:
                    if (recv < 3) return false;
                    output[0] = 0x00; output[1] = 0x10; output[2] = m_Status;
                    return true;
                case 0x07: {
                    if (send < 2 || recv < 9) return false;
                    Sync();
                    int b = input[1] & 3;
                    for (int i = 0; i < 8; i++) output[i] = m_Ram[b * 8 + i];
                    output[8] = m_Status;
                    return true;
                }
                case 0x08: {
                    if (send < 10 || recv < 1) return false;
                    Sync();
                    int b = input[1] & 3;
                    if ((m_WriteLock & (1 << b)) == 0) {
                        for (int i = 0; i < 8; i++) m_Ram[b * 8 + i] = input[2 + i];
                        if (b == 0) {
                            int control = m_Ram[0] << 8 | m_Ram[1]; // big-endian half at offset 0
                            m_WriteLock = (m_WriteLock & ~6) | (((control >> 8) & 3) << 1);
                            bool run = (control & 4) == 0;
                            m_Status = (byte)(run ? m_Status & 0x7F : m_Status | 0x80);
                        }
                    }
                    output[0] = m_Status;
                    return true;
                }
            }
            return false;
        }
    }
}
