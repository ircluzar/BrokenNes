using System.Runtime.InteropServices;
using System;
using cor64.IO;
using NLog;

namespace cor64.PIF
{
    public class PIFController : N64MemoryDevice
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly MemMappedBuffer m_Rom = new MemMappedBuffer(0x7C0, MemMappedBuffer.MemModel.SINGLE_READONLY);
        private readonly MemMappedBuffer m_Ram = new MemMappedBuffer(0x40, MemMappedBuffer.MemModel.SINGLE_READ_WRITE);

        public const uint ADDRESS_JOY_CHANNEL = 0x1FC007C4U;

        public const int PIF_RAM_OFFSET = 0x7C0;
        public const int COMMAND_BYTE_OFFSET = 0x3F;

        private byte m_CommandByte;

        [Flags]
        private enum PifCommands : byte {
            None = 0,
            JoyBus = 0x1, // Joybus communication
            CRX105 = 0x2, // Request for challenge-response
            AllowNormalOperation = 0x8, // Request for continue operation, otherwise console locksup (must be sent within 5 seconds)
            DisablePifRomAccess = 0x10, // Request to disable read access to the PIF ROM
            CICChecksumVerify = 0x20, // CIC checksum verified result
            Clear = 0x40 // Clear PIF RAM
        }

        private enum JoyBusCommand : byte {
            Info = 0,
            ControllerState = 1,
            ReadEEPROM = 4,
            WriteEEPROM = 5,
            Reset = 0xFF
        }

        private const int END = -2; // 0xFE
        private const int NOP = -1; // 0xFF

        private readonly ushort[] m_LastJoyconStatus = new ushort[4];

        // [cer64plus] Cartridge EEPROM (4 Kbit). Contents are part of savestates; EepromPath, when set, persists writes.
        private readonly byte[] m_Eeprom = new byte[512];
        private string m_EepromPath;

        public string EepromPath {
            get => m_EepromPath;
            set {
                m_EepromPath = value;
                if (value != null && System.IO.File.Exists(value)) {
                    var bytes = System.IO.File.ReadAllBytes(value);
                    Array.Copy(bytes, Eeprom, Math.Min(bytes.Length, Eeprom.Length));
                    Log.Info("EEPROM loaded from {0}", value);
                }
            }
        }

        // [cer64plus] Battery save as bytes (for hosts that keep their own save files, e.g. BrokenNes ExportSave/ImportSave).
        public byte[] ExportEeprom() => (byte[])Eeprom.Clone();
        public void ImportEeprom(byte[] data) {
            var e = Eeprom;
            Array.Clear(e);
            if (data != null) Array.Copy(data, e, Math.Min(data.Length, e.Length));
        }

        // [cer64plus] 16 Kbit EEPROM (256 blocks), chosen per game (SaveDb). Kept apart from m_Eeprom so the 4 Kbit path
        // and the savestate format stay exactly as they were; travels through the battery save API.
        [NonSerialized] public bool Eeprom16K;
        // [boot_dkr] The cart has SRAM/FlashRAM, so no EEPROM on joybus channel 4: commands there get the "no device"
        // answer (rx | 0x80), as on hardware (ares: the EEPROM only exists when the cartridge has one). Answering as an
        // EEPROM made Kirby 64 (U) take its EEPROM path at the file select and stall. COR64_BOOT_DKR_NOEEP=0: old (always EEPROM).
        [NonSerialized] public bool NoCartEeprom;
        public static readonly bool s_NoEepEnabled = Environment.GetEnvironmentVariable("COR64_BOOT_DKR_NOEEP") != "0";
        [NonSerialized] private readonly byte[] m_Eeprom16 = new byte[2048];
        private byte[] Eeprom => Eeprom16K ? m_Eeprom16 : m_Eeprom;

        private void SaveEeprom() {
            if (m_EepromPath == null) return;
            try {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(m_EepromPath)));
                System.IO.File.WriteAllBytes(m_EepromPath, Eeprom);
            }
            catch (Exception e) { Log.Warn("EEPROM save failed: " + e.Message); }
        }

        public PIFController(N64MemoryController n64MemoryController) : base(n64MemoryController, 0x100000)
        {
            StaticMap(m_Rom, m_Ram);
        }

        private readonly sbyte[] m_StickX = new sbyte[4];
        private readonly sbyte[] m_StickY = new sbyte[4];

        public void JoyWriteState(int slot, JoyController.ButtonPress buttons, sbyte x, sbyte y) {
            m_LastJoyconStatus[slot] = (ushort)buttons;
            m_StickX[slot] = x;
            m_StickY[slot] = y;
        }

        public byte[] ReadRam() {
            byte[] buffer = new byte[64];
            for (int i = 0; i < 64; i++) {
                // always read in BE form
                buffer[i] = m_Ram.ReadPtr.Offset(N64Endianess.Address8(i)).AsType_8();
            }
            return buffer;
        }

        private static bool HasCommand(PifCommands commands, PifCommands testFlags) {
            return (commands & testFlags) == testFlags;
        }

        public void ReadCommandByte() {
            m_CommandByte = ReadRamU8(COMMAND_BYTE_OFFSET);
            // Console.WriteLine("Command byte read: {0:X2}", m_CommandByte);
        }

        private static readonly bool s_PifTrace = Environment.GetEnvironmentVariable("COR64_JOY_TRACE") == "3";
        public void ProcessPifCommands() {
            PifCommands cmd = (PifCommands)m_CommandByte;
            if (s_PifTrace) Console.Error.WriteLine("[pif] " + Convert.ToHexString(ReadRam()));

            if (cmd == PifCommands.None) return;

            // Log.Debug("Requested PIF Command: {0}", cmd.ToString());

            if (HasCommand(cmd, PifCommands.JoyBus)) {
                // Console.WriteLine("Joybus command pending");
                ParseJoybus();
            }

            if (s_ChallengeOn && HasCommand(cmd, PifCommands.CRX105)) {
                Cic6105Challenge();
            }

            m_CommandByte = 0;
        }

        // [cer64plus] CIC-NUS-6105 challenge/response (PIF command 0x02), used by 6105 games (Banjo-Tooie, Jet Force
        // Gemini, ...) as an anti-copy check. The challenge is 30 nibbles in PIF RAM 0x30..0x3E; the PIF forwards them to
        // the CIC and puts the CIC's 30 answer nibbles back in the same place, clears 0x2E/0x2F and the command byte.
        // The CIC's answer function is the publicly documented one (reverse engineered by X-Scale): every nibble is
        // (key + 5 * challenge) mod 16, and the next key comes from one of two 16-entry tables picked by the answer.
        private static readonly bool s_ChallengeOn = Environment.GetEnvironmentVariable("COR64_CIC6105_CHALLENGE") != "0";
        private static readonly byte[] s_CicLut0 = { 0x4, 0x7, 0xA, 0x7, 0xE, 0x5, 0xE, 0x1, 0xC, 0xF, 0x8, 0xF, 0x6, 0x3, 0x6, 0x9 };
        private static readonly byte[] s_CicLut1 = { 0x4, 0x1, 0xA, 0x7, 0xE, 0x5, 0xE, 0x1, 0xC, 0x9, 0x8, 0x5, 0x6, 0x3, 0xC, 0x9 };

        public static byte[] Cic6105Answer(byte[] challengeNibbles) {
            var answer = new byte[challengeNibbles.Length];
            int key = 0xB;
            bool useLut1 = false;
            for (int i = 0; i < challengeNibbles.Length; i++) {
                int r = (key + 5 * challengeNibbles[i]) & 0xF;
                answer[i] = (byte)r;
                key = (useLut1 ? s_CicLut1 : s_CicLut0)[r];
                int sign = (r >> 3) & 1;
                int magnitude = (sign == 1 ? ~r : r) & 7;
                bool next = (magnitude % 3 == 1) ? sign == 1 : sign == 0;
                if (useLut1 && (r == 0x1 || r == 0x9)) next = true;
                if (useLut1 && (r == 0xB || r == 0xE)) next = false;
                useLut1 = next;
            }
            return answer;
        }

        private void Cic6105Challenge() {
            var nibbles = new byte[30];
            for (int i = 0; i < 15; i++) {
                byte b = ReadRamU8(0x30 + i);
                nibbles[i * 2] = (byte)(b >> 4);
                nibbles[i * 2 + 1] = (byte)(b & 0xF);
            }
            var answer = Cic6105Answer(nibbles);
            WriteRamU8(0x2E, 0);
            WriteRamU8(0x2F, 0);
            for (int i = 0; i < 15; i++) WriteRamU8(0x30 + i, (byte)((answer[i * 2] << 4) | answer[i * 2 + 1]));
            WriteRamU8(COMMAND_BYTE_OFFSET, 0);
        }

        private void Copy(int ramOffset, byte[] buffer, int index, int len) {
            for (int i = 0; i < len; i++) {
                buffer[index + i] = m_Ram.ReadPtr.Offset(N64Endianess.Address8(ramOffset + i)).AsType_8();
            }
        }

        // [cer64plus] lane pif_pads: port devices (not part of savestates; Controller Pak images persist via PakDir).
        private static readonly bool s_Ch4Trace = Environment.GetEnvironmentVariable("COR64_JOY_TRACE") == "1";
        [NonSerialized] public CartRtc Rtc; // [catalog_fix3] cartridge RTC (null = none)
        [NonSerialized] private JoybusPorts m_Ports;
        public JoybusPorts Ports => m_Ports ??= new JoybusPorts();

        private (ushort, byte, byte) PadRead(int ch) => (m_LastJoyconStatus[ch], unchecked((byte)m_StickX[ch]), unchecked((byte)m_StickY[ch]));

        private void ParseJoybusPorts() {
            var ports = Ports;
            int channel = 0, offset = 0;
            var input = new byte[64];
            var output = new byte[64];
            while (offset < 64 && channel < 5) {
                byte send = ReadRamU8(offset++);
                if (send == 0xFE) break;
                if (send == 0xFF) continue;
                if (send == 0x00) { channel++; continue; }
                if (send == 0xFD) { channel++; continue; } // channel reset
                if (offset >= 64) break;
                int rxOff = offset;
                byte recvRaw = ReadRamU8(offset++);
                if (recvRaw == 0xFE) break;
                int s = send & 0x3F, r = recvRaw & 0x3F;
                if (offset + s + r > 64) break;
                bool skip = (send & 0xC0) != 0;
                for (int k = 0; k < s; k++) input[k] = ReadRamU8(offset + k);
                int respOff = offset + s;
                offset += s + r;
                if (!skip) {
                    if (s_Ch4Trace && channel == 4 && s > 0) Console.Error.WriteLine($"[joy] ch4 cmd={input[0]:X2} s={s} r={r}");
                    if (channel < 4) {
                        Array.Clear(output);
                        bool valid = ports.Comm(channel, input, s, r, output, PadRead);
                        WriteRamU8(rxOff, (byte)(valid ? r : (0x80 | r)));
                        if (valid) for (int k = 0; k < r; k++) WriteRamU8(respOff + k, output[k]);
                    }
                    else if (Rtc != null && s > 0 && input[0] >= 0x06 && input[0] <= 0x08) { // [catalog_fix3] COR64_CART_RTC
                        Array.Clear(output);
                        bool valid = Rtc.Comm(input, s, r, output);
                        WriteRamU8(rxOff, (byte)(valid ? r : (0x80 | r)));
                        if (valid) for (int k = 0; k < r; k++) WriteRamU8(respOff + k, output[k]);
                    }
                    else if (NoCartEeprom && s_NoEepEnabled) {
                        WriteRamU8(rxOff, (byte)(0x80 | r));
                    }
                    else {
                        byte[] data = new byte[s];
                        Array.Copy(input, data, s);
                        if (s > 0) ExecuteJoybusCommands(data, channel, respOff);
                    }
                }
                channel++;
            }
        }

        public void ParseJoybus() {
            if (JoybusPorts.Enabled) { ParseJoybusPorts(); return; }
            int channel = 0;

            for (int i = 0; i < 64; i++) {
                if (channel > 4) return;

                int tx = ReadJoyByte(i);

                // Console.WriteLine("Off: {0} | Channel: {1} | TX: {2}", i, channel, tx);

                switch (tx) {
                    case END: return;
                    case NOP: continue;
                    case 0x0: { channel++; break; }
                    default: {
                        int rx = ReadJoyByte(i + 1);

                        if (rx == END) return;

                        byte[] data = new byte[tx];
                        Copy(i + 2, data, 0, tx);

                        // Console.WriteLine("Data Size: {0}", data.Length);
                        // Console.Write("Command Data: ");
                        // for (int d = 0; d < data.Length; d++) { 
                        //     Console.Write(data[d].ToString("X2"));
                        // }
                        // Console.WriteLine();

                        ExecuteJoybusCommands(data, channel, i + 2 + tx);

                        i += tx;
                        i += rx;
                        i++;

                        // Log.Debug("offset pushed to {0} (RX:{1})", i, rx);

                        channel++;
                        break;
                    }
                }
            }
        }

        private void ExecuteJoybusCommands(byte[] data, int channel, int responseOffset) {
            JoyBusCommand command = (JoyBusCommand)data[0];

            // Log.Debug("Joy Command Execute: CH: {0} {1} TX={2}", channel, command.ToString(), data.Length);

            if (CompatLog.On) {
                int c = data[0];
                if (channel == 4 && (c == 4 || c == 5) && data.Length >= 2 && data[1] > 0x3F && !Eeprom16K) CompatLog.Note("save:eeprom16k", $"block={data[1]}");
                else if (channel <= 3 && (c == 2 || c == 3)) CompatLog.Note("pak:controller-pak-" + (c == 2 ? "read" : "write"), $"ch={channel}");
                else if (c != 0 && c != 0xFF && c != 1 && c != 4 && c != 5) CompatLog.Note("joy:unknown-cmd", $"ch={channel} cmd={c:X2}");
                if (channel == 4 && (c == 6 || c == 7)) CompatLog.Note("cart:rtc", $"cmd={c:X2}");
            }
            switch (command) {
                default: break;

                case JoyBusCommand.Reset:
                case JoyBusCommand.Info: {

                    switch (channel) {
                        default: break;

                        case 0:
                        case 1:
                        case 2:
                        case 3: {
                            // Log.Debug("PIF Read controller Info");

                            // just report 1 active controller in slot 0 for now
                            if (channel == 0) {
                                WriteRamU8(responseOffset++, 0x50);
                                WriteRamU8(responseOffset++, 0x00);
                                WriteRamU8(responseOffset, 0x00); // no pak inserted
                                break;
                            }
                            else {
                                WriteRamU8(responseOffset++, 0xFF);
                                WriteRamU8(responseOffset++, 0xFF);
                                WriteRamU8(responseOffset, 0xFF);
                                break;
                            }
                        }

                        case 4: {
                            // Log.Debug("PIF EEEPROM Info");
                            WriteRamU16(responseOffset, Eeprom16K ? (ushort)0x00C0 : (ushort)0x0080); responseOffset+=2;
                            WriteRamU8(responseOffset, 0x00);
                            break;
                        }
                    }

                    break;
                }

                case JoyBusCommand.ControllerState: {
                    if (channel >= 0 && channel <= 3) {
                        // var buttonPresses = N64Endianess.JoyconRead(m_LastJoyconStatus[channel]);
                        JoyController.ButtonPress buttonPresses = (JoyController.ButtonPress)m_LastJoyconStatus[channel];
                        // Console.WriteLine("PIF: Buttons Pressed: {1} = {0:X4}", buttonPresses, channel);

                        byte x_dir = unchecked((byte)m_StickX[channel]);
                        byte y_dir = unchecked((byte)m_StickY[channel]);

                        WriteRamU16(responseOffset, (ushort)buttonPresses); // Buttons
                        responseOffset+=2;
                        WriteRamU8(responseOffset++, x_dir); // X Axis
                        WriteRamU8(responseOffset++, y_dir); // Y Axis
                    }
                    else {
                        WriteRamU8(responseOffset++, 0);
                        WriteRamU8(responseOffset++, 0);
                        WriteRamU8(responseOffset++, 0);
                    }
                    WriteRamU8(responseOffset, 0); // Error num
                    break;
                }

                // [cer64plus] 4 Kbit EEPROM: 64 blocks of 8 bytes. Read: [04, block] -> 8 bytes. Write: [05, block, 8 bytes] -> status.
                case JoyBusCommand.ReadEEPROM: {
                    if (channel == 4 && data.Length >= 2) {
                        if (Eeprom16K) { int blk = data[1]; for (int b = 0; b < 8; b++) WriteRamU8(responseOffset + b, m_Eeprom16[blk * 8 + b]); break; }
                        int block = data[1] & 0x3F;
                        for (int b = 0; b < 8; b++) WriteRamU8(responseOffset + b, m_Eeprom[block * 8 + b]);
                    }
                    break;
                }

                case JoyBusCommand.WriteEEPROM: {
                    if (channel == 4 && data.Length >= 10) {
                        int block = data[1] & 0x3F;
                        if (Eeprom16K) { int blk = data[1]; for (int b = 0; b < 8; b++) m_Eeprom16[blk * 8 + b] = data[2 + b]; }
                        else
                        for (int b = 0; b < 8; b++) m_Eeprom[block * 8 + b] = data[2 + b];
                        WriteRamU8(responseOffset, 0x00);
                        SaveEeprom();
                    }
                    break;
                }

            }
        }

        private byte ReadRamU8(int offset) {
            return m_Ram.ReadPtr.Offset(N64Endianess.Address8(offset)).AsType_8();
        }

        private void WriteRamU8(int offset, byte value) {
            m_Ram.WritePtr.Offset(N64Endianess.Address8(offset)).AsType_8(value);
        }

        private void WriteRamU16(int offset, ushort value) {
            m_Ram.WritePtr.Offset(N64Endianess.Address16(offset)).AsType_16(value);
        }

        private sbyte ReadJoyByte(int offset) => (sbyte)m_Ram.ReadPtr.Offset(N64Endianess.Address8(offset)).AsType_8();


        // public byte PifCtrl {
        //     get => m_Ram.ReadPtr.Offset(N64Endianess.Address8(SR_OFFSET)).AsType_8();
        //     set => m_Ram.WritePtr.Offset(N64Endianess.Address8(SR_OFFSET)).AsType_8(value);
        // }

        public override string Name => "PIF Module Memory";
    }
}
