// [cer64plus] Compatibility log: records hardware features a game touches that cor64 does not emulate (or only partly),
// for the compat harness (SessionProbe compat mode, compat/README.md). Default off: COR64_COMPAT_LOG=1 turns it on.
// Every hook is `if (CompatLog.On) ...` on a cold path or behind a static readonly bool the JIT folds away, and it only
// records: it never changes emulation behaviour. Process-wide (the harness runs one game per process).
using System.Collections.Concurrent;

namespace cor64
{
    public static class CompatLog
    {
        public static readonly bool On = System.Environment.GetEnvironmentVariable("COR64_COMPAT_LOG") == "1";

        /// <summary>RDRAM base pointer for the ucode hash (set by the host when On).</summary>
        public static unsafe byte* RdramPtr;

        public sealed class Entry { public long Count; public long FirstTick; public string Detail; }

        /// <summary>key ("area:what") -> count and first detail.</summary>
        public static readonly ConcurrentDictionary<string, Entry> Events = new();

        /// <summary>RSP tasks seen: "type:hash" -> count.</summary>
        public static readonly ConcurrentDictionary<string, long> Ucodes = new();

        public static void Note(string key, string detail = null)
        {
            var e = Events.GetOrAdd(key, _ => new Entry { FirstTick = Telemetry.Ticks, Detail = detail });
            System.Threading.Interlocked.Increment(ref e.Count);
        }

        // ---- CPU/RSP physical memory access outside emulated devices (CPU loads/stores go through the controller) ----
        public static void MemAccess(long address)
        {
            uint a = (uint)address;
            string k = null;
            if (a >= 0x08000000 && a < 0x10000000) k = a >= 0x08010000 ? "save:flashram-cpu" : "save:sram-or-flash-cpu";
            else if (a >= 0x05000000 && a < 0x06000000) k = "cart:64dd-regs";
            else if (a >= 0x06000000 && a < 0x08000000) k = "cart:64dd-ipl-rom";
            else if (a >= 0x04900000 && a < 0x05000000) k = "mem:unmapped-0490";
            else if (a >= 0x1FD00000 && a < 0x80000000) k = "cart:dom1-addr3";
            if (k != null) Note(k, $"addr={a:X8}");
        }

        // ---- PI DMA ----
        public static void PiDma(uint cart, uint dram, int len, bool toCart)
        {
            string dir = toCart ? "write" : "read";
            if (cart >= 0x08000000 && cart < 0x10000000) Note((cart >= 0x08010000 ? "save:flashram-dma-" : "save:sram-or-flash-dma-") + dir, $"cart={cart:X8} len={len}");
            else if (cart >= 0x05000000 && cart < 0x08000000) Note("cart:64dd-dma-" + dir, $"cart={cart:X8} len={len}");
            else if (cart >= 0x10000000 && cart < 0x1FC00000 && toCart) Note("cart:rom-dma-write", $"cart={cart:X8} len={len}");
            else if (cart < 0x05000000 || cart >= 0x20000000) Note("pi:dma-odd-address-" + dir, $"cart={cart:X8} len={len}");
        }

        // ---- RSP task start: identify the microcode by hashing its text (ucode, ucode_size) in RDRAM (OSTask in DMEM 0xFC0) ----
        public static long Acc2AudTasks, Acc2GfxTasks;
        public static readonly bool TraceTasks = System.Environment.GetEnvironmentVariable("COR64_COMPAT_TASKTRACE") == "1";
        public static unsafe void RspTask(uint type, uint ucode, uint ucodeSize, uint ucodeData, uint dataSize)
        {
            // [rsp_task] Not every RSP start is an OSTask: the CIC-6105 IPL3 runs its own RSP program before the game
            // (pd, dk64, conker, bt, mm, ...), with whatever DMEM 0xFC0 holds (seen: 0x8BC43B5D, ucode=DBD5C14D).
            // OSTask types are 1..7 (M_GFXTASK, M_AUDTASK, M_VIDTASK, ..., M_HVQMTASK). The ucode pointer cannot be used: a
            // resumed yielded task (flags=OS_TASK_YIELDED) keeps a KSEG0 ucode pointer (mm), which SP DMA masks to 24 bits.
            if (type == 0 || type > 7) {
                if (TraceTasks) System.Console.WriteLine($"TASK non-OSTask RSP start (IPL3?) dmem0xFC0={type:X8} ucode={ucode:X8}");
                Ucodes.AddOrUpdate("boot:non-ostask", 1, (_, v) => v + 1);
                return;
            }
            // [keys_leak] libultra sptask.h: 3 M_VIDTASK, 4 M_NJPEGTASK (OoT backgrounds, Pokemon Stadium 2), 5 M_FBTASK,
            // 6 M_HVQTASK, 7 M_HVQMTASK. cor64 runs every task's microcode at LLE, so a non-gfx/audio task is not an
            // unimplemented feature; its ucode is listed as "<kind>:<hash>" and must still be checked into known_ucode.tsv.
            // COR64_KEYS_TASKFLAG=1 flags them again (old behaviour).
            if (type != 1 && type != 2 && s_TaskFlag) { Note("rsp:task-type-other", $"type={type}"); }
            ulong h = 1469598103934665603UL;
            if (RdramPtr != null) {
                uint n = ucodeSize == 0 || ucodeSize > 0x800 ? 0x800 : ucodeSize; // first 2 KB: ucode_size often covers trailing buffers that change
                uint b = ucode & 0x7FFFFF; if (b + n > 0x800000) n = 0;
                for (uint i = 0; i < n; i++) { h ^= RdramPtr[b + i]; h *= 1099511628211UL; }
                // text only: ucode_data is a per-task buffer (yielded tasks point it at the yield buffer)
            }
            if (TraceTasks) System.Console.WriteLine($"TASK type={type} ucode={ucode:X8} size={ucodeSize:X} data={ucodeData:X8} dsize={dataSize:X} hash={h:X16}");
            Ucodes.AddOrUpdate($"{TaskKind(type)}:{h:X16}", 1, (_, v) => v + 1);
            if (type == 2) Acc2AudTasks++; else if (type == 1) Acc2GfxTasks++; // [accuracy2] diagnostics (COR64_ACC2_AILOG)
        }
        private static readonly bool s_TaskFlag = System.Environment.GetEnvironmentVariable("COR64_KEYS_TASKFLAG") == "1";
        private static string TaskKind(uint type) => type switch { 1 => "gfx", 2 => "audio", 3 => "video", 4 => "jpeg", 5 => "fb", 6 => "hvq", 7 => "hvqm", _ => "t" + type };
    }
}
