using System;
using System.IO;

namespace cor64.PIF
{
    // [cer64plus] lane pif_pads: joybus devices for ports 1-4 (controller present/absent, Controller Pak, Rumble Pak).
    // Behaviour studied from ares (MIT): status 05 00 <pak>, absent devices set bit 7 of the rx length byte, pak reads
    // and writes on 32-byte blocks with the 5-bit address CRC and the 8-bit data CRC.
    // COR64_PIF_PADS_JOYBUS=0 restores the old answers (one 0x50 0x00 0x00 device on port 1, FF FF FF on 2-4).
    // COR64_PIF_PADS=<n>      connected controllers, ports 1..n (default 4; ports above n answer "no device").
    // COR64_PIF_PAK=<p1,p2,..> pak per port: none | controller (cpak, mempak) | rumble. Default none (what SM64 saw).
    // COR64_PIF_PAK_DIR=<dir> where Controller Pak images live (<dir>/<cart crc>_p<n>.mpk); unset = in memory only.
    // [quirk_fix1] transfer (tpak): Transfer Pak, see TransferPak.cs. COR64_PAK_GAMEDB=0: no per-game default paks
    // (today only World Driver Championship, which saves to a Controller Pak on port 1 only).
    public enum PakKind { None, Controller, Rumble, Transfer }

    public sealed class JoybusPorts
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("COR64_PIF_PADS_JOYBUS") != "0";

        public readonly bool[] Connected = new bool[4];
        public readonly PakKind[] Pak = new PakKind[4];
        private readonly byte[][] m_PakRam = new byte[4][];
        private readonly string[] m_PakPath = new string[4];
        public string PakDir;
        public string GameKey = "game";
        /// <summary>Controller-state reads (joybus command 01) since power-on: the TAS runner's lag-frame detection. Not in save states.</summary>
        public long PadReads;
        private readonly TransferPak[] m_Tpak = new TransferPak[4];
        private TransferPak Tpak(int port) => m_Tpak[port] ??= new TransferPak();
        private readonly bool m_PakFromEnv = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COR64_PIF_PAK"));
        private static readonly bool s_PakGameDb = Environment.GetEnvironmentVariable("COR64_PAK_GAMEDB") != "0";

        /// <summary>Per-game default paks, only when COR64_PIF_PAK is not set.</summary>
        public void ApplyGameDefaults(string gameCode)
        {
            if (gameCode == null || gameCode.Length < 3) return;
            string id = gameCode.Substring(1, 2);
            // [audio_rdp1] COR64_AUDRDP1_JOE3PADS (default on; =0 off): Jeopardy! (NJOE) accepts no input at all when more
            // than 3 controllers are present at boot (ares does the same: max 3 ports for NJOE). Not when COR64_PIF_PADS is set.
            if (s_AudRdp1Joe3 && gameCode.Substring(0, 3) == "NJO" && Environment.GetEnvironmentVariable("COR64_PIF_PADS") == null) Connected[3] = false;
            if (m_PakFromEnv || !s_PakGameDb) return;
            if (id == "WO" && Pak[0] == PakKind.None) SetPak(0, PakKind.Controller); // World Driver Championship
            // [catalog_fix1] COR64_PAK_DEFAULT (default on; =0 off): a blank Controller Pak on port 1 for every game, as most
            // players had one. 27 catalog games sat on a "No Controller Pak" prompt without it. The certified games keep
            // what they were certified with (no pak) through the exception list below, so their frames do not change.
            // [catalog_fix6] COR64_CATFIX6_PAKEXC (default on; =0 off): games that reject the default Controller Pak.
            // Chameleon Twist (NCTx) probes port 1 as a Rumble Pak and loops on "Rumble Pak malfunctioning" with a cpak.
            bool pakExc = s_CatFix6PakExc && Array.IndexOf(s_CatFix6NoPak, id) >= 0;
            if (s_PakDefault && !pakExc && Pak[0] == PakKind.None && Array.IndexOf(s_NoDefaultPak, id) < 0) SetPak(0, PakKind.Controller);
            // [catalog_fix3] COR64_VRU (default on; =0 off): Hey You, Pikachu! (NPGx) wants the Voice Recognition Unit on port 4.
            if (s_VruOn && id == "PG") { Vru[3] = new VruDevice(gameCode[3] == 'J'); Connected[3] = true; }
        }

        private static readonly bool s_AudRdp1Joe3 = Environment.GetEnvironmentVariable("COR64_AUDRDP1_JOE3PADS") != "0";
        private static readonly bool s_CatFix6PakExc = Environment.GetEnvironmentVariable("COR64_CATFIX6_PAKEXC") != "0";
        private static readonly string[] s_CatFix6NoPak = { "CT" };
        private static readonly bool s_VruOn = Environment.GetEnvironmentVariable("COR64_VRU") != "0";
        public readonly VruDevice[] Vru = new VruDevice[4];

        private static readonly bool s_JoyTraceAll = Environment.GetEnvironmentVariable("COR64_JOY_TRACE") == "2";
        private static readonly bool s_JoyTrace = s_JoyTraceAll || Environment.GetEnvironmentVariable("COR64_JOY_TRACE") == "1";
        private static readonly bool s_PakDefault = Environment.GetEnvironmentVariable("COR64_PAK_DEFAULT") != "0";
        // 2-char game ids of the certified games (compat/games.tsv): SM64 MK64 SF64 OoT WR64 SSB DKR MP1 Kirby Yoshi Snap
        // GE007 F-Zero X Mario Tennis Dr. Mario MM PD DK64 Conker BT JFG Rogue Naboo Indy WDC RE2 PStad2 Blast PW64 Excite.
        public static bool IsCertified(string gameCode) => gameCode != null && gameCode.Length >= 3 && Array.IndexOf(s_NoDefaultPak, gameCode.Substring(1, 2)) >= 0;
        private static readonly string[] s_NoDefaultPak = {
            "SM", "KT", "FX", "ZL", "WR", "AL", "DY", "LB", "K4", "YS", "PF", "GE", "FZ", "M8", "N6",
            "ZS", "PD", "DO", "FU", "B7", "JF", "RS", "NA", "IJ", "WO", "B5", "P3", "BC", "PW", "MX" };

        public JoybusPorts()
        {
            int n = 4; // all four present by default: what SM64 has always seen (its hashes depend on it)
            if (int.TryParse(Environment.GetEnvironmentVariable("COR64_PIF_PADS"), out int v)) n = Math.Clamp(v, 0, 4);
            for (int i = 0; i < 4; i++) Connected[i] = i < n;
            var paks = (Environment.GetEnvironmentVariable("COR64_PIF_PAK") ?? "").Split(',');
            for (int i = 0; i < 4 && i < paks.Length; i++) Pak[i] = ParsePak(paks[i]);
            PakDir = Environment.GetEnvironmentVariable("COR64_PIF_PAK_DIR");
        }

        // [quirk_fix2] Rumble Pak motor state (COR64_RUMBLE_EVENT=0 disables). No hardware to drive: front ends may
        // subscribe to RumbleChanged; with COR64_COMPAT_LOG=1 (or COR64_RUMBLE_LOG=1) each change is noted/printed.
        public static readonly bool RumbleEvents = Environment.GetEnvironmentVariable("COR64_RUMBLE_EVENT") != "0";
        private static readonly bool s_RumbleLog = Environment.GetEnvironmentVariable("COR64_RUMBLE_LOG") == "1";
        public static event Action<int, bool> RumbleChanged;
        public static long RumbleToggles;
        private readonly bool[] m_Rumble = new bool[4];
        public bool RumbleState(int port) => port >= 0 && port < 4 && m_Rumble[port];
        private void SetRumble(int port, bool on)
        {
            if (!RumbleEvents || port < 0 || port >= 4 || m_Rumble[port] == on) return;
            m_Rumble[port] = on; RumbleToggles++;
            if (CompatLog.On) CompatLog.Note("joy:rumble", $"ch={port} {(on ? "on" : "off")}");
            if (s_RumbleLog) Console.WriteLine($"RUMBLE p{port + 1} {(on ? "on" : "off")}");
            RumbleChanged?.Invoke(port, on);
        }

        public static PakKind ParsePak(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant()) {
                case "controller": case "cpak": case "mempak": case "memory": return PakKind.Controller;
                case "rumble": case "rpak": return PakKind.Rumble;
                case "transfer": case "tpak": return TransferPak.Enabled ? PakKind.Transfer : PakKind.None;
                default: return PakKind.None;
            }
        }

        public void SetPak(int port, PakKind kind) { Pak[port] = kind; m_PakRam[port] = null; m_Tpak[port] = null; }

        public byte[] PakRam(int port)
        {
            if (m_PakRam[port] != null) return m_PakRam[port];
            var ram = new byte[0x8000];
            string path = PakDir != null ? Path.Combine(PakDir, GameKey + "_p" + (port + 1) + ".mpk") : null;
            if (path != null && File.Exists(path)) {
                var b = File.ReadAllBytes(path);
                Array.Copy(b, ram, Math.Min(b.Length, ram.Length));
            }
            else FormatControllerPak(ram, port);
            m_PakPath[port] = path;
            return m_PakRam[port] = ram;
        }

        private void SavePak(int port)
        {
            var path = m_PakPath[port];
            if (path == null) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); File.WriteAllBytes(path, m_PakRam[port]); }
            catch (Exception) { }
        }

        public byte[] ExportPak(int port) => (byte[])PakRam(port).Clone();
        public void ImportPak(int port, byte[] data) { var r = PakRam(port); Array.Clear(r); if (data != null) Array.Copy(data, r, Math.Min(data.Length, r.Length)); }

        // Blank libultra file system, one bank (32 KB). Fixed serial so runs are deterministic.
        public static void FormatControllerPak(byte[] ram, int port)
        {
            Array.Clear(ram);
            foreach (int area in new[] { 1, 3, 4, 6 }) {
                int b = area * 0x20;
                ram[b + 0x01] = 0x15;
                PutU32(ram, b + 0x04, 0x00043A1Bu + (uint)port);
                PutU32(ram, b + 0x08, 0x02C0FFEEu);
                ram[b + 0x18] = 0x00; ram[b + 0x19] = 0x01; // device id
                ram[b + 0x1A] = 0x01;                     // banks
                ram[b + 0x1B] = 0x00;                     // version
                ushort sum = 0, inv = 0;
                for (int h = 0; h < 14; h++) {
                    ushort d = (ushort)(ram[b + h * 2] << 8 | ram[b + h * 2 + 1]);
                    sum += d; inv += (ushort)~d;
                }
                ram[b + 0x1C] = (byte)(sum >> 8); ram[b + 0x1D] = (byte)sum;
                ram[b + 0x1E] = (byte)(inv >> 8); ram[b + 0x1F] = (byte)inv;
            }
            // inode table (page 1) and its copy (page 2): pages 5..127 empty (0x0003), checksum in byte 1
            foreach (int page in new[] { 1, 2 }) {
                for (int slot = 5; slot < 128; slot++) ram[0x100 * page + slot * 2 + 1] = 0x03;
                byte c = 0;
                for (int i = 2; i < 0x100; i++) c += ram[0x100 * page + i];
                ram[0x100 * page + 1] = c;
            }
        }

        private static void PutU32(byte[] r, int o, uint v) { r[o] = (byte)(v >> 24); r[o + 1] = (byte)(v >> 16); r[o + 2] = (byte)(v >> 8); r[o + 3] = (byte)v; }

        public static byte AddressCrc(ushort address)
        {
            int crc = 0;
            for (int i = 0; i < 16; i++) {
                int x = (crc & 0x10) != 0 ? 0x15 : 0;
                crc = (crc << 1) & 0x1F;
                if ((address & 0x8000) != 0) crc |= 1;
                address <<= 1;
                crc ^= x;
            }
            return (byte)crc;
        }

        public static byte DataCrcN(byte[] data, int off, int n)
        {
            int crc = 0;
            for (int i = 0; i <= n; i++)
                for (int j = 7; j >= 0; j--) {
                    int x = (crc & 0x80) != 0 ? 0x85 : 0;
                    crc = (crc << 1) & 0xFF;
                    if (i < n && (data[off + i] & (1 << j)) != 0) crc |= 1;
                    crc ^= x;
                }
            return (byte)crc;
        }

        public static byte DataCrc(byte[] data, int off)
        {
            int crc = 0;
            for (int i = 0; i < 33; i++)
                for (int j = 7; j >= 0; j--) {
                    int x = (crc & 0x80) != 0 ? 0x85 : 0;
                    crc = (crc << 1) & 0xFF;
                    if (i < 32 && (data[off + i] & (1 << j)) != 0) crc |= 1;
                    crc ^= x;
                }
            return (byte)crc;
        }

        /// <summary>One controller-port command. Returns false when no device answers (the PIF then sets bit 7 of rx).</summary>
        public bool Comm(int port, byte[] input, int send, int recv, byte[] output, Func<int, (ushort buttons, byte x, byte y)> pad)
        {
            if (s_JoyTrace && send >= 1 && (input[0] != 0x01 || s_JoyTraceAll)) Console.Error.WriteLine($"[joy] p{port} conn={Connected[port]} pak={Pak[port]} cmd={input[0]:X2} s={send} r={recv} a={(send>=3?((input[1]<<8|input[2])&~31).ToString("X4"):"-")}" + (input[0]==3 && send>=4 ? " d0=" + input[3].ToString("X2") : ""));
            if (!Connected[port] || send < 1) return false;
            if (Vru[port] != null) return Vru[port].Comm(input, send, recv, output);
            var pak = Pak[port];
            switch (input[0]) {
                case 0x00: case 0xFF:
                    output[0] = 0x05; output[1] = 0x00; output[2] = pak != PakKind.None ? (byte)0x01 : (byte)0x02;
                    return true;
                case 0x01: {
                    PadReads++;
                    var p = pad(port);
                    output[0] = (byte)(p.buttons >> 8); output[1] = (byte)p.buttons; output[2] = p.x; output[3] = p.y;
                    return true;
                }
                case 0x02: { // read pak: [02, addrH, addrL|crc] -> 32 bytes + data CRC
                    if (send < 3 || recv < 1) return true;
                    ushort address = (ushort)((input[1] << 8 | input[2]) & ~31);
                    Array.Clear(output, 0, Math.Min(recv, 33));
                    bool bad = pak == PakKind.None || AddressCrc(address) != (input[2] & 0x1F);
                    int n = Math.Min(recv, 32);
                    if (!bad) {
                        if (pak == PakKind.Controller) {
                            var ram = PakRam(port);
                            for (int i = 0; i < n; i++) output[i] = address + i <= 0x7FFF ? ram[address + i] : (byte)0;
                        }
                        else if (pak == PakKind.Transfer) {
                            var tp = Tpak(port);
                            for (int i = 0; i < n; i++) output[i] = tp.Read((address + i) & 0xFFFF);
                        }
                        else for (int i = 0; i < n; i++) { int a = address + i; output[i] = a <= 0x7FFF ? (byte)0 : a <= 0x8FFF ? (byte)0x80 : (byte)0; }
                    }
                    if (recv >= 33) { output[32] = DataCrc(output, 0); if (bad) output[32] ^= 0xFF; }
                    return true;
                }
                case 0x03: { // write pak: [03, addrH, addrL|crc, 32 bytes] -> data CRC
                    if (send < 4 || recv < 1) return true;
                    ushort address = (ushort)((input[1] << 8 | input[2]) & ~31);
                    int len = Math.Min(send - 3, 32);
                    bool bad = pak == PakKind.None || AddressCrc(address) != (input[2] & 0x1F);
                    if (!bad && pak == PakKind.Controller && address <= 0x7FFF) {
                        var ram = PakRam(port);
                        for (int i = 0; i < len && address + i <= 0x7FFF; i++) ram[address + i] = input[3 + i];
                        SavePak(port);
                    }
                    else if (!bad && pak == PakKind.Transfer) {
                        var tp = Tpak(port);
                        for (int i = 0; i < len; i++) tp.Write((address + i) & 0xFFFF, input[3 + i]);
                        tp.Flush();
                    }
                    // rumble: 0x8000 identify writes are accepted; 0xC000-0xCFFF writes set the motor (event + log only)
                    else if (!bad && pak == PakKind.Rumble && address >= 0xC000 && address <= 0xCFFF && len > 0) SetRumble(port, input[3 + len - 1] != 0);
                    output[0] = len == 32 ? DataCrc(input, 3) : (byte)0;
                    if (bad) output[0] ^= 0xFF;
                    return true;
                }
                default:
                    if (CompatLog.On) CompatLog.Note("joy:unknown-cmd", $"ch={port} cmd={input[0]:X2}");
                    return false;
            }
        }
    }
}
