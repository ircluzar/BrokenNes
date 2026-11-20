// [dbg_rsp] RSP view for the debugger: vector unit state (VU registers, accumulator, VCO/VCC/VCE, DIV), SP status and
// semaphore, the OSTask header in DMEM, and a microcode identifier. Everything is read on demand by the debugger
// (between frames); nothing here runs on the emulation path.
//
// Microcode identification, in order:
//   1. CRC-32 of the ucode text (the task's ucode pointer, min(ucode_size, 0x800) bytes, the same CRC as
//      RspTaskRecord.UcodeCrc) against UcodeTable.Known, entries measured on cor64 from the games named there.
//   2. The version string Nintendo's SDK graphics microcodes carry in their data segment: "RSP Gfx ucode F3DEX fifo
//      1.23 Yoshitaka Yasumoto 1998 Nintendo." (F3DEX/F3DLX/F3DLP/F3DZEX/L3DEX/S2DEX and their 2.x successors) or
//      "RSP SW Version: 2.0D, 04-01-96" (Fast3D, gspF3D). This is a property of the ucode image (the same fact
//      GLideN64 and others key on); the scan here is cor64's own code.
//   3. The OSTask type (1 graphics, 2 audio, 4 JPEG, 6/7 HVQ/HVQM) when nothing else matched.
// No reference emulator's code or tables were copied: cen64 and ares (permissive) have no ucode table, and the GPL ones
// (GLideN64, mupen64plus-rsp-hle, Project64) were consulted for behaviour only.
using System;
using System.Collections.Generic;
using System.Text;

namespace cor64.Debug
{
    /// <summary>The OSTask structure the CPU leaves at DMEM 0xFC0 for rspboot (libultra sptask.h layout).</summary>
    public sealed class OSTaskInfo
    {
        public uint Type, Flags, UcodeBoot, UcodeBootSize, Ucode, UcodeSize, UcodeData, UcodeDataSize,
            DramStack, DramStackSize, OutputBuff, OutputBuffSize, DataPtr, DataSize, YieldDataPtr, YieldDataSize;

        public static readonly string[] FieldNames = {
            "type", "flags", "ucode_boot", "ucode_boot_size", "ucode", "ucode_size", "ucode_data", "ucode_data_size",
            "dram_stack", "dram_stack_size", "output_buff", "output_buff_size", "data_ptr", "data_size", "yield_data_ptr", "yield_data_size" };

        /// <summary>Decodes 16 big-endian words; word(i) returns DMEM word 0xFC0 + 4 * i.</summary>
        public static OSTaskInfo Decode(Func<int, uint> word)
        {
            var w = new uint[16];
            for (int i = 0; i < 16; i++) w[i] = word(i);
            return new OSTaskInfo
            {
                Type = w[0], Flags = w[1], UcodeBoot = w[2], UcodeBootSize = w[3], Ucode = w[4], UcodeSize = w[5], UcodeData = w[6], UcodeDataSize = w[7],
                DramStack = w[8], DramStackSize = w[9], OutputBuff = w[10], OutputBuffSize = w[11], DataPtr = w[12], DataSize = w[13], YieldDataPtr = w[14], YieldDataSize = w[15],
            };
        }

        public uint this[int i] => i switch
        {
            0 => Type, 1 => Flags, 2 => UcodeBoot, 3 => UcodeBootSize, 4 => Ucode, 5 => UcodeSize, 6 => UcodeData, 7 => UcodeDataSize,
            8 => DramStack, 9 => DramStackSize, 10 => OutputBuff, 11 => OutputBuffSize, 12 => DataPtr, 13 => DataSize, 14 => YieldDataPtr, _ => YieldDataSize,
        };

        public static string TypeName(uint t) => t switch
        {
            1 => "gfx", 2 => "audio", 3 => "video", 4 => "jpeg", 6 => "hvq", 7 => "hvqm", _ => "type" + t,
        };

        public override string ToString() => $"{TypeName(Type)} ucode={Ucode:X8}+{UcodeSize:X} data={UcodeData:X8}+{UcodeDataSize:X} data_ptr={DataPtr:X8}+{DataSize:X}";
    }

    public sealed class UcodeInfo
    {
        /// <summary>Short name, e.g. "F3DEX 1.23", "Fast3D", "audio (SM64 aspMain)".</summary>
        public string Name;
        /// <summary>Family: F3D, F3DEX, F3DLX, F3DLP, F3DZEX, F3DEX2, F3DZEX2, L3DEX, L3DEX2, S2DEX, S2DEX2, Turbo3D, audio, jpeg, hvq, unknown.</summary>
        public string Family;
        /// <summary>How it was identified: "crc", "string" or "type".</summary>
        public string Source;
        public uint Crc;
        /// <summary>The raw version string from the ucode data, when one was found.</summary>
        public string VersionString;
        public override string ToString() => $"{Name} [{Source} {Crc:X8}]";
    }

    public static class UcodeTable
    {
        /// <summary>Known ucode text CRCs (RspTaskRecord.UcodeCrc), measured on cor64 from the game named.</summary>
        public static readonly IReadOnlyDictionary<uint, (string name, string family, string game)> Known = new Dictionary<uint, (string, string, string)>
        {
            // measured on cor64 with DebugRspTests.MeasureUcodes (1200 frames from boot, then 20 debugger frames)
            [0x451E953E] = ("Fast3D 2.0D", "F3D", "Super Mario 64 (U)"),
            [0x49158204] = ("audio ABI1 (aspMain)", "audio", "Super Mario 64 (U), Wave Race 64, GoldenEye 007, Pilotwings 64"),
            [0x534B511C] = ("Fast3D 2.0D", "F3D", "Pilotwings 64"),
            [0xF4A01485] = ("Fast3D 2.0D", "F3D", "Wave Race 64"),
            [0x75C184D9] = ("Fast3D 2.0G", "F3D", "GoldenEye 007"),
            [0x41623235] = ("F3DEX 0.95", "F3DEX", "Mario Kart 64 (U)"),
            [0xC2E77243] = ("audio ABI1 (Mario Kart 64)", "audio", "Mario Kart 64 (U)"),
            [0xD175B335] = ("audio (F-Zero X)", "audio", "F-Zero X"),
            [0x01076FD9] = ("F3DEX2 fifo 2.04H", "F3DEX2", "Super Smash Bros. (string seen through its audio task's data pointer)"),
            [0xBDF0D59A] = ("audio (Super Smash Bros.)", "audio", "Super Smash Bros."),
        };

        static readonly string[] s_Families = { "F3DZEX2", "F3DEX2", "F3DLX2", "F3DLP2", "L3DEX2", "S2DEX2", "F3DZEX", "F3DEX", "F3DLX", "F3DLP", "L3DEX", "S2DEX", "Turbo3D", "F3DFLX" };

        /// <summary>Identifies a task's microcode. rdram(phys) reads a big-endian RDRAM word (no side effects).</summary>
        public static UcodeInfo Identify(OSTaskInfo task, uint crc, Func<uint, uint> rdram)
        {
            if (Known.TryGetValue(crc, out var k))
                return new UcodeInfo { Name = k.name, Family = k.family, Source = "crc", Crc = crc, VersionString = task.Type == 1 ? FindVersion(task, rdram) : null };
            // only graphics tasks: SSB's audio task points ucode_data at the graphics ucode's data (its string would name F3DEX2)
            string v = task.Type == 1 ? FindVersion(task, rdram) : null;
            if (v != null) { var (n, f) = FromVersion(v); return new UcodeInfo { Name = n, Family = f, Source = "string", Crc = crc, VersionString = v }; }
            string fam = task.Type switch { 1 => "unknown", 2 => "audio", 4 => "jpeg", 6 => "hvq", 7 => "hvq", _ => "unknown" };
            string name = task.Type switch { 1 => "gfx (unidentified)", 2 => "audio (unidentified)", _ => OSTaskInfo.TypeName(task.Type) + " (unidentified)" };
            return new UcodeInfo { Name = name, Family = fam, Source = "type", Crc = crc };
        }

        /// <summary>Bytes of ucode text the CRC covers. Not the whole 0x1000: games pass SP_UCODE_SIZE (0x1000) for a shorter text, and
        /// what follows it in RDRAM changes from task to task (MK64's audio), so only the first 2 KB give a stable id.</summary>
        public const uint IdBytes = 0x800;

        /// <summary>CRC-32 (IEEE) of min(ucode_size, IdBytes) bytes of ucode text, as RspTaskRecord.UcodeCrc.</summary>
        public static uint TextCrc(OSTaskInfo task, Func<uint, uint> rdram)
        {
            uint crc = 0xFFFFFFFF, src = task.Ucode & 0x7FFFFF, len = (task.UcodeSize == 0 ? IdBytes : Math.Min(task.UcodeSize, IdBytes)) & ~3u; // size 0: GoldenEye / Pilotwings audio
            for (uint a = 0; a < len; a += 4) { uint w = rdram(src + a); for (int b = 24; b >= 0; b -= 8) crc = Crc(crc, (byte)(w >> b)); }
            return ~crc;
        }

        static uint[] s_CrcT;
        internal static uint Crc(uint c, byte b)
        {
            var t = s_CrcT;
            if (t == null)
            {
                t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint x = i; for (int k = 0; k < 8; k++) x = (x & 1) != 0 ? 0xEDB88320u ^ (x >> 1) : x >> 1; t[i] = x; }
                s_CrcT = t;
            }
            return t[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        /// <summary>Scans the ucode data segment (up to 0x1000 bytes) for an SDK version string.</summary>
        public static string FindVersion(OSTaskInfo task, Func<uint, uint> rdram)
        {
            uint src = task.UcodeData & 0x7FFFFF, len = Math.Min(task.UcodeDataSize == 0 ? 0x800u : task.UcodeDataSize, 0x1000u) & ~3u;
            if (len == 0 || src + len > 0x800000) return null;
            var b = new byte[len];
            for (uint a = 0; a < len; a += 4) { uint w = rdram(src + a); b[a] = (byte)(w >> 24); b[a + 1] = (byte)(w >> 16); b[a + 2] = (byte)(w >> 8); b[a + 3] = (byte)w; }
            foreach (var key in new[] { "RSP Gfx ucode ", "RSP SW Version" })
            {
                int i = IndexOf(b, key);
                if (i < 0) continue;
                int e = i;
                while (e < b.Length && b[e] >= 0x20 && b[e] < 0x7F) e++;
                return Encoding.ASCII.GetString(b, i, e - i).Trim();
            }
            return null;
        }

        static int IndexOf(byte[] b, string s)
        {
            for (int i = 0; i + s.Length <= b.Length; i++)
            {
                int j = 0;
                while (j < s.Length && b[i + j] == (byte)s[j]) j++;
                if (j == s.Length) return i;
            }
            return -1;
        }

        /// <summary>"RSP Gfx ucode F3DEX.NoN fifo 1.23 Yoshitaka ..." -> ("F3DEX.NoN fifo 1.23", "F3DEX"); "RSP SW Version: 2.0D ..." -> Fast3D.</summary>
        public static (string name, string family) FromVersion(string v)
        {
            if (v.StartsWith("RSP SW Version", StringComparison.Ordinal))
            {
                int c = v.IndexOf(':'); string ver = c >= 0 ? v[(c + 1)..].Split(',')[0].Trim() : "";
                return ("Fast3D " + ver, "F3D");
            }
            string rest = v["RSP Gfx ucode ".Length..];
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = new List<string>();
            foreach (var p in parts) { if (p == "Yoshitaka" || p.EndsWith("Nintendo.", StringComparison.Ordinal)) break; name.Add(p); }
            string first = parts.Length > 0 ? parts[0] : "";
            string family = "unknown";
            foreach (var f in s_Families) if (first.StartsWith(f, StringComparison.OrdinalIgnoreCase)) { family = f; break; }
            // the 2.x line names itself F3DEX/F3DZEX with a 2.xx version
            if (family is "F3DEX" or "F3DZEX" or "F3DLX" or "F3DLP" or "L3DEX" or "S2DEX")
                foreach (var p in parts) if (p.Length > 1 && p[0] == '2' && p[1] == '.') { family += "2"; break; }
            return (string.Join(" ", name), family);
        }
    }

    /// <summary>A snapshot of the RSP vector unit and SP registers.</summary>
    public sealed class RspState
    {
        /// <summary>Vector registers: V[r][e] is lane e (0 = the most significant halfword, as VU element numbering).</summary>
        public ushort[][] V = new ushort[32][];
        /// <summary>Accumulator lanes, 48 bits each (hi:mid:lo).</summary>
        public ulong[] Acc = new ulong[8];
        public ushort Vco, Vcc; public byte Vce;
        public int DivIn, DivOut; public bool DivDoublePrecision;
        public uint SpStatus, Semaphore, Pc;
        public OSTaskInfo Task;

        public static readonly string[] StatusBits = {
            "halt", "broke", "dma_busy", "dma_full", "io_full", "single_step", "intr_on_break",
            "sig0", "sig1", "sig2", "sig3", "sig4", "sig5", "sig6", "sig7" };

        public IEnumerable<string> StatusFlags()
        {
            for (int i = 0; i < StatusBits.Length; i++) if ((SpStatus >> i & 1) != 0) yield return StatusBits[i];
        }

        public string VectorHex(int r) => string.Join(" ", Array.ConvertAll(V[r], x => x.ToString("X4")));
    }

    /// <summary>[dbg_rsp] The RSP side of the debugger target. Call between frames.</summary>
    public interface IDebugRsp : IDebugTarget
    {
        RspState GetRspState();
        /// <summary>The OSTask header currently in DMEM 0xFC0.</summary>
        OSTaskInfo GetOSTask();
        /// <summary>Identifies the microcode of the OSTask in DMEM (the last task the CPU started).</summary>
        UcodeInfo CurrentUcode();
        /// <summary>Writes vector register r (0..31), lanes 0..7 (lane 0 most significant).</summary>
        bool SetRspVector(int r, ReadOnlySpan<ushort> lanes);
    }
}
