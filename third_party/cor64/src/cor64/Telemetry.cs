// [cer64plus] lightweight counters for health/speed measurement. Written from emulation threads, read by sampler.
namespace cor64
{
    public static class Telemetry
    {
        public static long Ticks;        // N64System.Tick calls (~CPU instructions)
        public static long ViInterrupts; // VI interrupts raised
        public static long AiLenWrites;  // AI_LEN writes (audio buffers submitted)
        public static long AiBytes;      // total bytes submitted to AI
        public static uint AiDacRate;    // last AI_DACRATE value
        public static uint AiBitRate;
        public static long AiDramAddr;
        public static long RspStarts;    // RSP un-halt events (tasks)
        public static long RdpListRuns;
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> RdpCmd = new();
        public static volatile int CaptureRdp;
#if FASTER_VI
        public static int ViSpeedupEff => ViSpeedup; // [timing] VI speed-up in effect (1 without FASTER_VI)
#else
        public static int ViSpeedupEff => 1;
#endif
        public static readonly int ViSpeedupBase = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_VI_SPEEDUP"), out var vs) && vs >= 1 ? vs : 11;
        // [ingame_fix2] per-game VI speed-up floor (see ViSpeedupFor); equals ViSpeedupBase unless a cart entry raises it.
        public static int ViSpeedup = ViSpeedupBase;
        private static readonly bool s_IngFix2Robotech = System.Environment.GetEnvironmentVariable("COR64_INGFIX2_ROBOTECH") != "0";
        // [ingame_fix2] Robotech: Crystal Dreams (beta, CRC1 75A0B893, no game code): its game loop is not VI-capped, so at
        // VI_SPEEDUP=1 (one CPU step = one 93.75 MHz cycle, i.e. an R4300 at IPC 1, faster than hardware with its cache/RDRAM
        // stalls) it queues more libaudio events per audio frame than the param pool holds; __allocParam returns 0 and
        // 8006D7E4 stores through it (TLB refill at va 8, then the corridor freezes). VI_SPEEDUP 2 still fails, 3 plays
        // (space stage). Hardware's effective CPU rate sits between 1 and 3, so raise the floor to 3 for this cart only.
        // COR64_INGFIX2_ROBOTECH=0 disables it.
        public static int ViSpeedupFor(uint crc1) => s_IngFix2Robotech && crc1 == 0x75A0B893u && ViSpeedupBase < 3 ? 3 : ViSpeedupBase;
        // [visp_fix1] COR64_VISPFIX1_CAP (default on; =0 off): per-game VI speed-up ceiling for games that still stall at the
        // default 11 after the Count fix (COR64_VISPFIX1_COUNT). Swept with the catalog/nav scripts: Monster Truck Madness
        // and Doom 64 are black at 11 and play at 6 (Count 3); Toukon Road Brave Spirits is black at 11 and 6, menus at 3;
        // Cruis'n Exotica is black at 11 and 3 and reaches the player select at 1. Only lowers the factor (never raises it).
        // per-game ceilings: GameDb.Setting.ViCap rows (Session/GameDb.cs), gated by COR64_VISPFIX1_CAP
        public static int ViSpeedupFor(uint crc1, string gameCode)
        {
            int v = ViSpeedupFor(crc1);
            if (cor64.Session.GameDb.TryGet(cor64.Session.GameDb.Setting.ViCap, cor64.Session.GameDb.Id(gameCode), out var c) && c < v) v = c;
            return v;
        }
        public static int DumpGfxTask = int.TryParse(System.Environment.GetEnvironmentVariable("COR64_DUMP_GFX"), out var dg) ? dg : 0;
        public static unsafe byte* RdramPtr;
        public static bool TraceUcode = System.Environment.GetEnvironmentVariable("COR64_TRACE_UCODE") == "1";
        public static readonly long[] RspPcCount = new long[0x400]; // executions per RSP IMEM word  // RDP command list executions
    }
}
