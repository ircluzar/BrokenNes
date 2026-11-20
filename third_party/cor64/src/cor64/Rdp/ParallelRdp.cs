// [cer64plus] GPU RDP through parallel-rdp (MIT, Themaister; vendored in native/parallel-rdp-standalone, built as
// native/prdp -> cor64_prdp.dll). RDRAM is imported as Vulkan host memory, so the GPU renders straight into cor64's RDRAM
// and the VI scanout path is unchanged. Commands are forwarded word for word; at SYNC_FULL the emulator waits for the GPU
// before raising the DP interrupt, which keeps emulation deterministic (RDRAM is coherent exactly when software expects it).
using System;
using System.Runtime.InteropServices;
using cor64.IO;
using cor64.Mips;
using cor64.RCP;
using NLog;
using static cor64.RCP.DPCInterface;

namespace cor64.Rdp
{
    public sealed class ParallelRdp : DrawProcessor
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr prdp_create(IntPtr rdram, uint rdramSize, uint hiddenSize, uint flags);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe void prdp_enqueue(IntPtr h, uint numWords, uint* words);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_sync_full(IntPtr h);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe void prdp_enqueue_batch(IntPtr h, uint totalWords, uint* packed);

        // [cer64plus] SM64 kicks DPC_END after nearly every command (~1 command per list), and each prdp_enqueue took ~1 us
        // (ring lock + waking parallel-rdp's worker, which then contends for the lock). Commands are now collected across
        // kicks and handed over in one native call at SYNC_FULL (or when the buffer fills): same commands, same order,
        // so the GPU's output is unchanged; only when the worker starts differs. COR64_RDPH_DEFER=0 restores per-command calls.
        private static readonly bool s_Defer = Environment.GetEnvironmentVariable("COR64_RDPH_DEFER") != "0";
        private const int BatchFlushWords = 32 * 1024;
        // [f5_speed3] hand the batch to the GPU worker once it holds this many words (<= 32K), so the GPU starts on a frame
        // before SYNC_FULL and a mid-frame readback wait finds most of it done. Same commands, same order.
        // COR64_PRDP_BATCHW=32768 = the old size.
        private static readonly int s_BatchWords = Math.Clamp(int.TryParse(Environment.GetEnvironmentVariable("COR64_PRDP_BATCHW"), out int bw) ? bw : 2048, 256, BatchFlushWords);
        [NonSerialized] private uint[] m_Batch = new uint[BatchFlushWords + 64];
        [NonSerialized] private int m_BatchN;

        private unsafe void FlushBatch()
        {
            if (m_BatchN == 0) return;
            fixed (uint* b = m_Batch) prdp_enqueue_batch(m_Handle, (uint)m_BatchN, b);
            m_BatchN = 0;
        }

        private unsafe void Enqueue(int words, uint* src)
        {
            if (!s_Defer) { m_AccLive = true; prdp_enqueue(m_Handle, (uint)words, src); return; }
            m_Batch[m_BatchN++] = (uint)words;
            for (int i = 0; i < words; i++) m_Batch[m_BatchN++] = src[i];
            if (m_BatchN >= s_BatchWords) {
                FlushBatch();
                if (s_Ho1GWait) TokenMarks(prdp_signal(m_Handle)); // [climb4 rdp_handoff1] this batch's own token
                if (!m_AccLive) { MidFlushes++; m_AccLive = true; PublishAcc(); } // [rdp_readback] now running without a token
            }
        }
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_destroy(IntPtr h);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern ulong prdp_signal(IntPtr h);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_wait(IntPtr h, ulong token);

        // Default (deferred): at SYNC_FULL submit and raise DP at once, and wait for that frame's GPU work at the next
        // SYNC_FULL. Deterministic as long as the CPU/RSP do not read pixels the RDP is still producing (checked by goldens;
        // SM64 audio is bit-identical and frames repeat exactly). COR64_PRDP_SYNC=full waits at every SYNC_FULL.
        private static readonly bool s_Drop = Environment.GetEnvironmentVariable("COR64_PRDP_DROP") == "1"; // measurement only: ceiling test
        private static readonly string s_SyncEnv = Environment.GetEnvironmentVariable("COR64_PRDP_SYNC");
        private static bool s_Deferred = s_SyncEnv != "full";
        // [vi_scanout] Games that read RDP output the deferred path does not track (CPU/RSP reads of a buffer still being
        // drawn) wait at every SYNC_FULL. re2 (NREE): with deferral it hangs on the black screen after the gore warning,
        // host-timing dependent; with a full sync it reaches the title/file menus every run. Keyed by the game code's
        // two middle letters, like COR64_RSP_SCALE. COR64_PRDP_SYNC=full/deferred overrides the table.
        private static readonly System.Collections.Generic.HashSet<string> s_FullSyncGames = new() { "RE", "BC", "N6", "ZL", "PD", "PW", "FX", "WO" }; // [x64_crash] + NBC NN6 CZL NPD NPW NFX NWO: CPU reads pixels the in-flight batch is still writing (IL vs native and run-to-run differed)
        public static void ConfigureForGame(string gameCode)
        {
            if (s_SyncEnv == "full" || s_SyncEnv == "deferred") { s_Deferred = s_SyncEnv != "full"; return; }
            if (Environment.GetEnvironmentVariable("COR64_X64CRASH_PRDPGAME") == "0") { s_Deferred = true; return; } // table off
            // [rdp_readback] the access guard makes deferral exact for every game; the table only applies with the guard off
            s_Deferred = RdpGuard.Enabled || !(gameCode != null && gameCode.Length >= 3 && s_FullSyncGames.Contains(gameCode.Substring(1, 2)));
        }

        // Probe whether the native library loads (a full Vulkan check happens at AttachMemory).
        public static bool IsAvailable(bool required) {
            try { return NativeLibrary.TryLoad("cor64_prdp", typeof(ParallelRdp).Assembly, null, out _) || (required ? throw new DllNotFoundException("cor64_prdp.dll not found") : false); }
            catch (DllNotFoundException) when (!required) { return false; }
        }
        [NonSerialized] private ulong m_PendingToken; // GPU timeline token: belongs to this process, never saved

        // [cer64plus] Deferred mode leaves the last SYNC_FULL's work in flight; a save or load must not race with it.
        public override void Drain()
        {
            if (m_Handle == IntPtr.Zero) return;
            FlushBatch();
            prdp_sync_full(m_Handle);
            m_PendingToken = 0;
            ClearLive(); ClearMarks(); m_AccLive = !s_Defer;
        }

        // Waits only for the last SYNC_FULL's work (deferred mode); the CPU keeps overlapping the GPU within a frame.
        private void WaitPending() => WaitAll();

        // Hidden RDRAM is GPU-only memory; it is copied here so savestates carry it (null in states from other renderers).
        private byte[] m_HiddenSnapshot;
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern uint prdp_hidden_size(IntPtr h);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_read_hidden(IntPtr h, byte[] dst, uint size);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_write_hidden(IntPtr h, byte[] src, uint size);

        public override void BeforeStateSave()
        {
            Drain();
            if (m_Handle == IntPtr.Zero) return;
            uint n = prdp_hidden_size(m_Handle);
            if (m_HiddenSnapshot == null || m_HiddenSnapshot.Length != n) m_HiddenSnapshot = new byte[n];
            prdp_read_hidden(m_Handle, m_HiddenSnapshot, n);
            m_TmemSnapshot ??= new byte[0x1000];
            prdp_read_tmem(m_Handle, m_TmemSnapshot);
        }

        private byte[] m_TmemSnapshot;
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_read_tmem(IntPtr h, byte[] dst);
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_write_tmem(IntPtr h, byte[] src);

        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern int prdp_reset(IntPtr h);

        public override void AfterStateLoad()
        {
            if (m_Handle == IntPtr.Zero) return;
            // Fresh GPU-side RDP state, so nothing from the previous timeline leaks in, then the saved hidden RDRAM.
            m_BatchN = 0; // never sent: the restored state replaces it
            if (prdp_reset(m_Handle) == 0) throw new InvalidOperationException("parallel-rdp could not restart after a state load");
            m_PendingToken = 0;
            ResetTracking();
            ReplayLatched();
            if (m_HiddenSnapshot != null) prdp_write_hidden(m_Handle, m_HiddenSnapshot, (uint)m_HiddenSnapshot.Length);
            if (m_TmemSnapshot != null) prdp_write_tmem(m_Handle, m_TmemSnapshot);
        }

        [NonSerialized] private IntPtr m_Handle;
        private readonly uint[] m_Words = new uint[44];
        public static long Commands, FullSyncs, WaitTicks, ScanWaitTicks;

        public override string Description => "parallel-rdp (Vulkan)";
        public override string Id => cor64.Cores.N64Cores.PpuParallel;
        public override void BeforeScanout()
        {
            if (m_PendingToken == 0 && !m_AccLive) return;
            // [cer64plus] Triple-buffered games scan a buffer the in-flight batch never writes; then there is nothing to wait
            // for here (the batch is still finished at the next SYNC_FULL, before any save, and before ScanOut if it overlaps).
            if (s_ScanSkip && !PendingOverlapsScan(VideoInterface)) { ScanSkips++; return; }
            long tw = System.Diagnostics.Stopwatch.GetTimestamp(); WaitPending(); ScanWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tw;
        }

        public override bool ScanOut(Video vi, ref uint[] frame, out int width, out int height)
        {
            if ((m_PendingToken != 0 || m_AccLive) && PendingOverlapsScan(vi)) WaitPending(); // the origin may have moved since the VI interrupt
            return base.ScanOut(vi, ref frame, out width, out height);
        }

        // [cer64plus] Without DPACK the deferred SYNC_FULL batch is only finished at the next SYNC_FULL or scan-out, so a
        // game that reuses texture memory or reads the frame it just drew after the DP interrupt races the GPU (Blast Corps:
        // title or black screen depending on host timing). With DPACK the batch is finished when the CPU clears the DP
        // interrupt. Costs ~9% wall on SM64, so it is per game (s_DpAckDb, set at cart load); COR64_TIMING_DPACK=1/0 overrides.
        private static readonly string s_DpAckEnv = Environment.GetEnvironmentVariable("COR64_TIMING_DPACK");
        private static readonly System.Collections.Generic.HashSet<string> s_DpAckDb = new() {
            "BC", // Blast Corps: reads RDP output after the DP interrupt; title or black depending on host timing without it
        };
        public static bool DpAckFor(string gameCode)
        {
            if (s_DpAckEnv == "1") return true;
            if (s_DpAckEnv == "0") return false;
            if (RdpGuard.Enabled) return false; // [rdp_readback] the access guard covers it
            return gameCode != null && gameCode.Length >= 3 && s_DpAckDb.Contains(gameCode.Substring(1, 2));
        }
        public static bool DpAckOn;
        private static bool s_DpAck => DpAckOn;
        [NonSerialized] private Action m_DpAckHook;
        public static long DpAckWaits;
        private void OnDpAck() { if (m_PendingToken != 0 || m_AccLive) { DpAckWaits++; WaitPending(); } }

        private static readonly bool s_ScanSkip = Environment.GetEnvironmentVariable("COR64_PRDP_SCANSKIP") != "0";
        public static long ScanSkips;
        private static readonly bool s_ScanExact = Environment.GetEnvironmentVariable("COR64_RDP_READBACK_SCANEXACT") != "0";

        // [rdp_readback] RDRAM pages the commands since the last SYNC_FULL can write (W) or read (R): m_Acc (managed, not yet
        // running on the GPU while the commands sit in m_Batch). Pages of work that is running are in RdpGuard's tables.
        // Not part of savestates (field order defines the format); after a load the latched commands replay through TrackCommand.
        private const byte PW = 1, PR = 2;
        [NonSerialized] private byte[] m_Acc = new byte[RdpGuard.Pages];
        [NonSerialized] private int[] m_AccList = new int[RdpGuard.Pages];
        [NonSerialized] private int m_AccN;
        [NonSerialized] private int[] m_LiveList = new int[RdpGuard.Pages]; // pages set in RdpGuard's tables
        [NonSerialized] private int m_LiveN;
        // [rdp_readback] exact byte ranges the work behind TableW may write, for the scan-out check: pages are 4 KB and a
        // framebuffer that ends mid-page would otherwise look written whenever the next buffer is drawn (a wait per frame).
        private const int MaxRanges = RdpGuard.MaxRanges;
        [NonSerialized] private uint[] m_AccLo = new uint[MaxRanges], m_AccHi = new uint[MaxRanges], m_LiveLo = RdpGuard.WLo, m_LiveHi = RdpGuard.WHi;
        [NonSerialized] private int m_AccRN;
        private ref int m_LiveRN => ref RdpGuard.WN;
        [NonSerialized] private bool m_AccRAll;
        private ref bool m_LiveRAll => ref RdpGuard.WAll;
        // [rdp_sync1] the same exact ranges for what the work may read (texture loads, a read-only z buffer), so a CPU or DMA
        // write to a 4 KB page that only shares it with a pending texture no longer waits (Aidyn, ASB99: ~12 waits a frame).
        // The ranges come from the same marks as TableR, so nothing the pages caught is missed. Overflow widens a range.
        [NonSerialized] private uint[] m_AccRLo = new uint[RdpGuard.MaxRRanges], m_AccRHi = new uint[RdpGuard.MaxRRanges];
        [NonSerialized] private int m_AccRRN;
        // Commands handed to parallel-rdp without a timeline token yet (a 32K-word batch flush between SYNC_FULLs, or
        // COR64_RDPH_DEFER=0): their pages go straight into the tables and a hit signals before waiting.
        [NonSerialized] private bool m_AccLive;
        [NonSerialized] private uint m_CImg, m_CWidth = 1024, m_CBpp = 4, m_ZImg, m_ScLines = 1024, m_ScX = 1024;
        [NonSerialized] private uint m_TImg, m_TWidth = 1, m_TSize;
        [NonSerialized] private bool m_ZSet, m_ZUpd = true, m_DrawDirty = true;
        public static long GuardWaits, MidFlushes;

        private void ResetTracking()
        {
            ClearLive(); ClearMarks();
            Array.Clear(m_Acc); m_AccN = 0; m_AccRN = 0; m_AccRRN = 0; m_AccRAll = false; m_AccLive = !s_Defer;
            m_CImg = 0; m_CWidth = 1024; m_CBpp = 4; m_ZImg = 0; m_ScLines = 1024; m_ScX = 1024;
            m_TImg = 0; m_TWidth = 1; m_TSize = 0; m_ZSet = false; m_ZUpd = true; m_DrawDirty = true;
        }

        private unsafe void SetLive(int p, byte bits)
        {
            byte* ta = RdpGuard.TableA;
            if (ta[p] == 0) { m_LiveList[m_LiveN++] = p; ta[p] = 1; }
            if ((bits & PW) != 0) RdpGuard.TableW[p] = 1;
            if ((bits & PR) != 0) RdpGuard.TableR[p] = 1;
            RdpGuard.Armed = true;
        }

        private unsafe void ClearLive()
        {
            byte* ta = RdpGuard.TableA, tw = RdpGuard.TableW;
            for (int i = 0; i < m_LiveN; i++) { int p = m_LiveList[i]; ta[p] = 0; tw[p] = 0; RdpGuard.TableR[p] = 0; }
            m_LiveN = 0; m_LiveRN = 0; m_LiveRAll = false; RdpGuard.RN = 0;
            RdpGuard.Armed = false;
        }

        // The accumulated pages become in flight (their commands were just handed to the GPU).
        private void PublishAcc()
        {
            for (int i = 0; i < m_AccN; i++) SetLive(m_AccList[i], m_Acc[m_AccList[i]]);
            if (m_AccRAll) m_LiveRAll = true;
            else for (int i = 0; i < m_AccRN; i++) AddRange(m_LiveLo, m_LiveHi, ref m_LiveRN, ref m_LiveRAll, m_AccLo[i], m_AccHi[i]);
            if (RdpGuard.RExact) for (int i = 0; i < m_AccRRN; i++) AddRRange(RdpGuard.RLo, RdpGuard.RHi, ref RdpGuard.RN, m_AccRLo[i], m_AccRHi[i]);
        }

        private void ClearAcc()
        {
            for (int i = 0; i < m_AccN; i++) m_Acc[m_AccList[i]] = 0;
            m_AccN = 0; m_AccRN = 0; m_AccRRN = 0; m_AccRAll = false; m_DrawDirty = true;
        }

        private static void AddRange(uint[] los, uint[] his, ref int n, ref bool all, uint lo, uint hi)
        {
            if (all) return;
            for (int i = 0; i < n; i++) if (lo >= los[i] && hi <= his[i]) return;
            if (n == MaxRanges) { all = true; return; }
            los[n] = lo; his[n++] = hi;
        }

        // Adds [lo, hi) to a read-range list; when it is full, the range closest to the new one grows to cover it.
        private static void AddRRange(uint[] los, uint[] his, ref int n, uint lo, uint hi)
        {
            if (n > 0 && lo <= his[n - 1] && hi >= los[n - 1]) { los[n - 1] = Math.Min(los[n - 1], lo); his[n - 1] = Math.Max(his[n - 1], hi); return; } // overlaps the last one
            int best = -1; ulong bestGap = ulong.MaxValue;
            for (int i = 0; i < n; i++) {
                if (lo >= los[i] && hi <= his[i]) return;
                ulong gap = hi < los[i] ? los[i] - hi : lo > his[i] ? lo - his[i] : 0;
                if (gap < bestGap) { bestGap = gap; best = i; }
            }
            if (n < los.Length && bestGap != 0) { los[n] = lo; his[n++] = hi; return; }
            los[best] = Math.Min(los[best], lo); his[best] = Math.Max(his[best], hi);
        }

        private void MarkPages(uint lo, uint hi, byte bits)
        {
            if (s_Ho1GWait && !m_MarksOff) AddMark(lo, hi, bits);
            if ((bits & PR) != 0 && RdpGuard.RExact) {
                AddRRange(m_AccRLo, m_AccRHi, ref m_AccRRN, lo, hi);
                if (m_AccLive) AddRRange(RdpGuard.RLo, RdpGuard.RHi, ref RdpGuard.RN, lo, hi);
            }
            if ((bits & PW) != 0) {
                AddRange(m_AccLo, m_AccHi, ref m_AccRN, ref m_AccRAll, lo, hi);
                if (m_AccLive) AddRange(m_LiveLo, m_LiveHi, ref m_LiveRN, ref m_LiveRAll, lo, hi);
            }
            for (uint p = lo >> RdpGuard.PageShift, last = (hi - 1) >> RdpGuard.PageShift; p <= last; p++) {
                byte o = m_Acc[p];
                if ((o & bits) != bits) {
                    if (o == 0) m_AccList[m_AccN++] = (int)p;
                    m_Acc[p] = (byte)(o | bits);
                }
                if (m_AccLive) SetLive((int)p, bits);
            }
        }

        // [lo, lo + len) of RDRAM; addresses are 24-bit and wrap at 8 MB as the RDP's do.
        private void Mark(uint lo, ulong len, byte bits)
        {
            if (len == 0) return;
            lo &= 0x7FFFFF;
            if (len >= 0x800000) { MarkPages(0, 0x800000, bits); return; }
            ulong hi = lo + len;
            if (hi <= 0x800000) MarkPages(lo, (uint)hi, bits);
            else { MarkPages(lo, 0x800000, bits); MarkPages(0, (uint)(hi - 0x800000), bits); }
        }

        // Every pixel a draw can touch lies within the scissor box of the current color (and depth) image. The box only
        // changes with set_color_image / set_z_image / set_scissor / set_other_modes, so it is marked once per change.
        private static readonly bool s_Ho1DrawEnd = Environment.GetEnvironmentVariable("COR64_RDPHO1_DRAWEND") != "0";
        private void NoteDraw()
        {
            if (!m_DrawDirty) return;
            m_DrawDirty = false;
            ulong rows = (ulong)m_ScLines * m_CWidth + (m_ScX > m_CWidth ? m_ScX - m_CWidth : 0) + (s_Ho1DrawEnd ? 0u : 2u);
            // [climb4 rdp_handoff1] COR64_RDPHO1_DRAWEND: the last pixel a draw can touch is (lines-1)*width + x-1, so the span
            // ends at lines*width pixels (x <= width), rounded up to 8 bytes (parallel-rdp keeps RDRAM as host words; a pixel's
            // store stays inside its aligned word). The old +2 pixels of slack reached into the buffer right after Gauntlet's
            // frame buffer, which the game DMAs to while the frame is in flight (~1 guard wait a frame).
            if (s_Ho1DrawEnd) {
                Mark(m_CImg, (rows * m_CBpp + 7) & ~7UL, PW);
                if (m_ZSet) Mark(m_ZImg, (rows * 2 + 7) & ~7UL, m_ZUpd ? PW : PR);
                return;
            }
            Mark(m_CImg, rows * m_CBpp, PW);
            if (m_ZSet && m_ZUpd) Mark(m_ZImg, rows * 2, PW);
            else if (m_ZSet) Mark(m_ZImg, rows * 2, PR);
        }

        // Texel bytes per texel for set_texture_image's size field (4-bit rounds up).
        private uint TexBytes => m_TSize == 0 ? 1u : 1u << (int)(m_TSize - 1);

        // RDRAM a load_block / load_tile / load_tlut reads: rows tl..th of the texture image (plus one row of slack).
        private static readonly bool s_LbExact = Environment.GetEnvironmentVariable("COR64_SPDCAT15_LBEXACT") != "0";
        private static readonly bool s_LoadLog = Environment.GetEnvironmentVariable("COR64_SPDCAT15_LOADLOG") == "1"; // diagnostics only
        private void NoteLoad(uint op, uint w0, uint w1)
        {
            uint tl = (w0 & 0xFFF) >> 2, th = (w1 & 0xFFF) >> 2, sh = (w1 >> 12) & 0xFFF;
            ulong bpp = TexBytes, rowBytes = (ulong)m_TWidth * bpp;
            ulong lo = tl * rowBytes, hi;
            if (s_LoadLog) System.Console.Error.WriteLine($"LOAD op={op:X2} timg={m_TImg:X6} w={m_TWidth} sz={m_TSize} w0={w0:X8} w1={w1:X8}");
            if (op != 0x33 && s_LbExact) {
                // [speed_cat15] load_tile / load_tlut read texels sl..sh of rows tl..th (TLUT entries are 16-bit), so the
                // span runs from row tl's first texel to row th's last, widened to 8-byte words.
                uint sl = ((w0 >> 12) & 0xFFF) >> 2, sz = op == 0x30 ? 2u : m_TSize, shi = sh >> 2;
                ulong stride = ((ulong)m_TWidth << (int)sz) >> 1;
                ulong a = m_TImg + tl * stride + (((ulong)sl << (int)sz) >> 1);
                ulong b = m_TImg + (ulong)th * stride + ((((ulong)shi + 1 << (int)sz) + 1) >> 1);
                if (th < tl || b <= a) b = a + 8;
                a &= ~7UL; b = (b + 7) & ~7UL;
                Mark((uint)a, b - a, PR);
                return;
            }
            if (op == 0x33 && s_LbExact) {
                // [speed_cat15] COR64_SPDCAT15_LBEXACT: load_block reads texels sl..sh of row tl in whole 64-bit words
                // (parallel-rdp, MIT: the load is an 8-byte-aligned span), so the range is exact up to 8-byte alignment.
                uint sl = ((w0 >> 12) & 0xFFF) >> 2, sz = m_TSize;
                ulong st = (((ulong)tl * m_TWidth + sl) << (int)sz) >> 1;
                ulong n = sh >= sl ? (((ulong)(sh - sl + 1) << (int)sz) + 1) >> 1 : 0;
                ulong a = m_TImg + st, b = a + n;
                a &= ~7UL; b = (b + 7) & ~7UL;
                Mark((uint)a, b - a, PR);
                return;
            }
            if (op == 0x33) hi = lo + (sh + 1) * bpp + 16;                       // load_block: sh = last texel index
            else hi = (th + 1UL) * rowBytes + ((sh >> 2) + 1UL) * Math.Max(bpp, 2) + 16; // load_tile / load_tlut
            if (lo > 0x800000) lo = 0;
            Mark(m_TImg + (uint)lo, hi - lo, PR);
        }

        private static readonly bool s_CimgLog = Environment.GetEnvironmentVariable("COR64_F5BOOT_CIMGLOG") == "1"; // diagnostics only
        private void TrackCommand(uint op, uint w0, uint w1)
        {
            switch (op) {
                case 0x3F: { if (s_CimgLog && ((w1 & 0xFFFFFF) != m_CImg || ((w0 & 0x3FF) + 1) != m_CWidth)) System.Console.WriteLine($"CIMG {w1 & 0xFFFFFF:X6} w={(w0 & 0x3FF) + 1} fmt={(w0>>21)&7} sz={(w0>>19)&3}");
                m_CImg = w1 & 0xFFFFFF; m_CWidth = (w0 & 0x3FF) + 1; m_CBpp = Math.Max(1u, (1u << (int)((w0 >> 19) & 3)) >> 1); m_DrawDirty = true; break; }
                case 0x3E: m_ZImg = w1 & 0xFFFFFF; m_ZSet = true; m_DrawDirty = true; break;
                case 0x2D: m_ScLines = ((w1 & 0xFFF) + 3) >> 2; m_ScX = (((w1 >> 12) & 0xFFF) + 3) >> 2; m_DrawDirty = true; break;
                case 0x2F: { bool z = (w1 & 0x20) != 0; if (z != m_ZUpd) { m_ZUpd = z; m_DrawDirty = true; } break; }
                case 0x3D: m_TImg = w1 & 0xFFFFFF; m_TWidth = (w0 & 0x3FF) + 1; m_TSize = (w0 >> 19) & 3; break;
                case 0x30: case 0x33: case 0x34: NoteLoad(op, w0, w1); break;
                case 0x24: case 0x25: case 0x36: NoteDraw(); break;
                default: if (op >= 0x08 && op <= 0x0F) NoteDraw(); break;
            }
        }

        // Finishes every command handed to parallel-rdp so far and clears the in-flight pages.
        private void WaitAll()
        {
            if (m_Handle == IntPtr.Zero) return;
            if (m_AccLive) { FlushBatch(); m_PendingToken = prdp_signal(m_Handle); if (s_Ho1GWait) TokenMarks(m_PendingToken); m_AccLive = !s_Defer; }
            if (m_PendingToken != 0) prdp_wait(m_Handle, m_PendingToken);
            m_PendingToken = 0;
            ClearLive();
            if (s_Ho1GWait) DropMarks(ulong.MaxValue); // what was handed over is done; marks of commands still in m_Batch stay
        }
        // [keys_leak] Granite (parallel-rdp's Vulkan layer) frees a frame context's released resources only when the device
        // moves on to the next frame context; cor64 never did, so GPU/host memory grew all run (mtennis died at ~frame
        // 19000 with 12 GB LinkedDeviceHost memory exhausted). Every Nth SYNC_FULL, after this batch is signalled, the
        // device now cycles its frame context (flushes and drains the ring, then recycles the context of two cycles ago).
        // Rendering order and results are unchanged. COR64_PRDP_FRAMECTX=<N> (default 8), 0 = old (never).
        private static readonly int s_FrameCtxEvery = int.TryParse(Environment.GetEnvironmentVariable("COR64_PRDP_FRAMECTX"), out var fc) ? fc : 8;
        [NonSerialized] private int m_FrameCtxCount;
        public static long FrameCtxCycles;
        [DllImport("cor64_prdp", CallingConvention = CallingConvention.Cdecl)] private static extern void prdp_next_frame(IntPtr h);
        private void NextFrameContext()
        {
            if (s_FrameCtxEvery <= 0 || ++m_FrameCtxCount < s_FrameCtxEvery) return;
            m_FrameCtxCount = 0; FrameCtxCycles++;
            prdp_next_frame(m_Handle);
        }
        [NonSerialized] private Action m_GuardHook;
        public static long GuardWaitTicks;
        public static long PartialWaits;
        private void OnGuardHit()
        {
            GuardWaits++; long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // [climb4 rdp_handoff1] COR64_RDPHO1_PWAIT: when the access does not touch anything the commands since the last
            // SYNC_FULL may read or write (m_Acc holds all of them, flushed or not), the conflict is with the previous frame's
            // work alone: wait for its token only and keep this frame's flushed work in flight (its pages are set again).
            if (s_Ho1GWait) GenWait();
            else if (s_Ho1PWait && m_PendingToken != 0 && RdpGuard.HitLen != 0 && !AccOverlaps(RdpGuard.HitPhys, RdpGuard.HitLen, RdpGuard.HitWrite)) {
                PartialWaits++;
                prdp_wait(m_Handle, m_PendingToken);
                m_PendingToken = 0;
                ClearLive();
                if (m_AccLive) PublishAcc();
            }
            else WaitAll();
            GuardWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
        // [climb4 rdp_handoff1] COR64_RDPHO1_GWAIT=1 (opt-in, not a win: R-range overflow makes hits repeat; needs the default deferred + batched hand-off): every mark (the exact
        // byte range a command since the last full wait may read or write) is kept with the timeline token of the batch
        // that carries it; each 2048-word hand-off gets its own token. A guard hit waits only for the newest batch whose
        // marks meet the access (a write meets W and R marks, a read W marks), then the live tables are rebuilt from the
        // marks still in flight. A hit whose access meets only finished work does not wait. A mark of a command not yet
        // handed over waits for everything, as before. Over MaxMarks marks (until the next full wait): the old full wait.
        private static readonly bool s_Ho1GWait = Environment.GetEnvironmentVariable("COR64_RDPHO1_GWAIT") == "1" && s_Defer && s_SyncEnv != "full";
        private const int MaxMarks = 1 << 16;
        [NonSerialized] private uint[] m_MLo = new uint[1024], m_MHi = new uint[1024];
        [NonSerialized] private byte[] m_MBits = new byte[1024];
        [NonSerialized] private ulong[] m_MTok = new ulong[1024];
        [NonSerialized] private int m_MarkN, m_MarkUnfl;
        [NonSerialized] private bool m_MarksOff;
        public static long GenWaits, GenSkips;
        private void AddMark(uint lo, uint hi, byte bits)
        {
            if (m_MarkN == m_MLo.Length) {
                if (m_MarkN >= MaxMarks) { m_MarksOff = true; return; }
                int n = m_MarkN * 2;
                Array.Resize(ref m_MLo, n); Array.Resize(ref m_MHi, n); Array.Resize(ref m_MBits, n); Array.Resize(ref m_MTok, n);
            }
            m_MLo[m_MarkN] = lo; m_MHi[m_MarkN] = hi; m_MBits[m_MarkN] = bits; m_MTok[m_MarkN] = 0; m_MarkN++;
        }
        private void ClearMarks() { m_MarkN = 0; m_MarkUnfl = 0; m_MarksOff = false; }
        // The commands behind the marks not yet handed over were just handed over under this token.
        private void TokenMarks(ulong tok) { for (int i = m_MarkUnfl; i < m_MarkN; i++) m_MTok[i] = tok; m_MarkUnfl = m_MarkN; }
        // Drops the marks of work finished by waiting for this token (tokens grow in hand-off order, so the marks not yet
        // handed over stay at the end).
        private void DropMarks(ulong tok)
        {
            if (tok == 0) return;
            int j = 0, unfl = -1;
            for (int i = 0; i < m_MarkN; i++) {
                if (i == m_MarkUnfl) unfl = j;
                ulong t = m_MTok[i];
                if (t != 0 && t <= tok) continue;
                m_MLo[j] = m_MLo[i]; m_MHi[j] = m_MHi[i]; m_MBits[j] = m_MBits[i]; m_MTok[j] = t; j++;
            }
            m_MarkUnfl = unfl < 0 ? j : unfl;
            m_MarkN = j;
        }
        private static readonly bool s_GLog = Environment.GetEnvironmentVariable("COR64_RDPHO1_GLOG") == "1";
        private void GenWait()
        {
            if (m_MarksOff || RdpGuard.HitLen == 0 || RdpGuard.HitPhys >= 0x800000) { WaitAll(); return; }
            uint lo = RdpGuard.HitPhys; ulong hi = Math.Min((ulong)lo + RdpGuard.HitLen, 0x800000UL);
            bool write = RdpGuard.HitWrite;
            ulong need = 0;
            for (int i = m_MarkN - 1; i >= 0; i--) {
                if ((m_MBits[i] & PW) == 0 && !write) continue;
                if (lo < m_MHi[i] && m_MLo[i] < hi) {
                    if (m_MTok[i] == 0) { if (!m_AccLive) continue; WaitAll(); return; } // not handed over: in the tables only once m_AccLive
                    if (m_MTok[i] > need) need = m_MTok[i];
                }
            }
            if (need == 0) {
                // Only finished work: nothing to wait for; the tables are rebuilt so it stops hitting. When the W ranges have
                // overflowed (more than MaxRanges, so any read of a W page hits) or the R ranges are full (widened ranges) a rebuild would hit again: then everything
                // handed over is waited for, which empties the tables of all but this frame's not-yet-handed-over marks.
                GenSkips++;
                if (s_GLog && GenSkips < 20) { var sb = new System.Text.StringBuilder(); for (int i = 0; i < m_LiveRN; i++) sb.Append($" {m_LiveLo[i]:X6}-{m_LiveHi[i]:X6}"); sb.Append(" marks:"); for (int i = 0; i < m_MarkN; i++) if (m_MLo[i] <= lo + 0x10000 && m_MHi[i] + 0x10000 >= lo) sb.Append($" {m_MLo[i]:X6}-{m_MHi[i]:X6}/{m_MBits[i]}/{m_MTok[i]}"); Console.Error.WriteLine($"GSKIP {lo:X6} w={write} live={m_AccLive} all={m_LiveRAll} n={m_MarkN}{sb}"); }
                if (m_LiveRAll || RdpGuard.RN >= RdpGuard.MaxRRanges) for (int i = 0; i < m_MarkN; i++) if (m_MTok[i] > need) need = m_MTok[i];
                if (need == 0) return; // exact tables: the hit cannot repeat after a rebuild either; leave them
            }
            if (need != 0) {
                GenWaits++;
                prdp_wait(m_Handle, need);
                if (m_PendingToken != 0 && m_PendingToken <= need) m_PendingToken = 0;
                DropMarks(need);
            }
            ClearLive();
            for (int i = 0; i < m_MarkN; i++) if (m_MTok[i] != 0 || m_AccLive) LiveMark(m_MLo[i], m_MHi[i], m_MBits[i]);
        }
        // The in-flight part of MarkPages: [lo, hi) into RdpGuard's tables and ranges.
        private void LiveMark(uint lo, uint hi, byte bits)
        {
            if ((bits & PR) != 0 && RdpGuard.RExact) AddRRange(RdpGuard.RLo, RdpGuard.RHi, ref RdpGuard.RN, lo, hi);
            if ((bits & PW) != 0) AddRange(m_LiveLo, m_LiveHi, ref m_LiveRN, ref m_LiveRAll, lo, hi);
            for (uint p = lo >> RdpGuard.PageShift, last = (hi - 1) >> RdpGuard.PageShift; p <= last; p++) SetLive((int)p, bits);
        }
        private static readonly bool s_Ho1PWait = Environment.GetEnvironmentVariable("COR64_RDPHO1_PWAIT") != "0";
        // Whether [phys, phys + len) meets the commands since the last SYNC_FULL, by the same rules as RdpGuard.Overlaps
        // (a write meets W and R marks, a read meets W marks; byte ranges decide within marked pages).
        private bool AccOverlaps(uint phys, uint len, bool write)
        {
            if (phys >= 0x800000) return false;
            ulong end = Math.Min((ulong)phys + len, 0x800000UL);
            bool wHit = false, rHit = false;
            for (uint p = phys >> RdpGuard.PageShift, last = (uint)((end - 1) >> RdpGuard.PageShift); p <= last; p++) {
                byte o = m_Acc[p];
                if ((o & PW) != 0) wHit = true;
                if (write && (o & PR) != 0) { if (!RdpGuard.RExact) return true; rHit = true; }
            }
            if (rHit) for (int i = 0; i < m_AccRRN; i++) if (phys < m_AccRHi[i] && m_AccRLo[i] < end) return true;
            if (!wHit) return false;
            if (m_AccRAll) return true;
            for (int i = 0; i < m_AccRN; i++) if (phys < m_AccHi[i] && m_AccLo[i] < end) return true;
            return false;
        }

        private unsafe bool PendingOverlapsScan(Video vi)
        {
            if (vi == null) return true;
            int w = vi.Width, h = vi.Height;
            if (w < 1 || h < 1 || w > 800 || h > 600) return RdpGuard.Armed;
            int bpp = vi.ControlReg.GetPixelMode() == VideoControlReg.PIXELMODE_32BPP ? 4 : 2;
            int stride = (int)vi.WidthReg; if (stride < w || stride > 2048) stride = w;
            uint lo = (uint)vi.ReadFramebufferAddressSafe() & 0x7FFFFF;
            ulong hi = Math.Min(0x800000UL, lo + (ulong)(stride * h * bpp));
            if (!s_ScanExact) {
                for (uint p = lo >> RdpGuard.PageShift; p <= (uint)((hi - 1) >> RdpGuard.PageShift); p++) if (RdpGuard.TableW[p] != 0) return true;
                return false;
            }
            if (m_LiveRAll) return RdpGuard.Armed;
            for (int i = 0; i < m_LiveRN; i++) if (lo < m_LiveHi[i] && m_LiveLo[i] < hi) return true;
            return false;
        }
        public override void Dispose()
        {
            if (m_Handle == IntPtr.Zero) return;
            if (RdpGuard.Waiter == (Action)m_GuardHook) RdpGuard.Waiter = null;
            ClearLive();
            prdp_destroy(m_Handle);
            m_Handle = IntPtr.Zero;
        }

        public override void AttachMemory(N64MemoryController memory)
        {
            base.AttachMemory(memory);
            var rdram = memory.RDRAM.GetRamPointer(0);
            if (((long)rdram & 0xFFFF) != 0) throw new InvalidOperationException("RDRAM must be 64 KB aligned for parallel-rdp");
            // HOST_VISIBLE_HIDDEN_RDRAM | HOST_VISIBLE_TMEM: savestates read and write them. COR64_PRDP_FLAGS overrides (measurements).
            uint flags = uint.TryParse(Environment.GetEnvironmentVariable("COR64_PRDP_FLAGS"), out var pf) ? pf : 1u | 2u;
            m_Handle = prdp_create(rdram, 8 * 1024 * 1024, 4 * 1024 * 1024, flags);
            RdpGuard.ClearAll(); // a previous session's pages
            ResetTracking();
            if (RdpGuard.Enabled) RdpGuard.Waiter = m_GuardHook ??= OnGuardHit;
            if (m_Handle == IntPtr.Zero) throw new InvalidOperationException("parallel-rdp could not start (see console); use the software RDP");
            Log.Info("RDP: parallel-rdp on Vulkan");
        }

        // Length in 64-bit words of the RDP command with this opcode.
        private static int CommandDwords(uint op)
        {
            switch (op) {
                case 0x08: return 4;  case 0x09: return 6;  case 0x0A: return 12; case 0x0B: return 14;
                case 0x0C: return 12; case 0x0D: return 14; case 0x0E: return 20; case 0x0F: return 22;
                case 0x24: case 0x25: return 2;
                default: return 1;
            }
        }

        // Words of an incomplete trailing command carried over to the next display list (commands may straddle DP_END kicks).
        private uint[] m_Pending = new uint[4096];
        private int m_PendingCount;

        // ---- RDP register state for savestates ----
        // The GPU holds the RDP's registers (modes, combiner, colors, scissor, images, tile descriptors). They are set by
        // commands, possibly frames before a save, so the last command of each kind is kept here (saved with the state)
        // and replayed into a fresh processor after a load.
        private readonly uint[] m_Latch = new uint[64 * 2];
        private ulong m_LatchValid;
        private readonly uint[] m_TileLatch = new uint[8 * 2];      // last set_tile per tile
        private readonly uint[] m_TileSizeLatch = new uint[8 * 2];  // last set_tile_size / load_block / load_tile / load_tlut per tile
        private int m_TileValid, m_TileSizeValid;

        private void Latch(uint op, uint w0, uint w1)
        {
            switch (op) {
                case 0x35: { int t = (int)(w1 >> 24) & 7; m_TileLatch[t * 2] = w0; m_TileLatch[t * 2 + 1] = w1; m_TileValid |= 1 << t; break; }
                case 0x30: case 0x32: case 0x33: case 0x34: { int t = (int)(w1 >> 24) & 7; m_TileSizeLatch[t * 2] = w0; m_TileSizeLatch[t * 2 + 1] = w1; m_TileSizeValid |= 1 << t; break; }
                case 0x2A: case 0x2B: case 0x2C: case 0x2D: case 0x2E: case 0x2F:
                case 0x37: case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D: case 0x3E: case 0x3F:
                    m_Latch[op * 2] = w0; m_Latch[op * 2 + 1] = w1; m_LatchValid |= 1UL << (int)op; break;
            }
        }

        private unsafe void ReplayLatched()
        {
            uint* c = stackalloc uint[2];
            void Send(uint a, uint b) { c[0] = a; c[1] = b; prdp_enqueue(m_Handle, 2, c); TrackCommand((a >> 24) & 0x3F, a, b); }
            for (int op = 0; op < 64; op++)
                if (op != 0x3D && (m_LatchValid & (1UL << op)) != 0) Send(m_Latch[op * 2], m_Latch[op * 2 + 1]);
            for (int t = 0; t < 8; t++) if ((m_TileValid & (1 << t)) != 0) Send(m_TileLatch[t * 2], m_TileLatch[t * 2 + 1]);
            // Loads also set the tile's size; the TMEM they write is replaced by the saved TMEM right after.
            for (int t = 0; t < 8; t++) if ((m_TileSizeValid & (1 << t)) != 0) Send(m_TileSizeLatch[t * 2], m_TileSizeLatch[t * 2 + 1]);
            if ((m_LatchValid & (1UL << 0x3D)) != 0) Send(m_Latch[0x3D * 2], m_Latch[0x3D * 2 + 1]);
            prdp_sync_full(m_Handle);
        }

        public static long HTicks, HCalls, EnqTicks, FillTicksTotal;
        private static readonly bool s_T = Environment.GetEnvironmentVariable("COR64_RSP_TIMING") == "1";
        protected override void DisplayListHandler(object sender, DPCInterface.DisplayList displayList)
        {
            if (!s_T) { Handler(displayList); return; }
            long t = System.Diagnostics.Stopwatch.GetTimestamp(); Handler(displayList); HTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t; HCalls++;
        }
        private static readonly bool s_Ho1Xbus = Environment.GetEnvironmentVariable("COR64_RDPHO1_XBUS") != "0";
        [NonSerialized] private IntPtr m_Dmem;
        // [accuracy2] diagnostics only: COR64_ACC2_RDPLOG=1 prints, every 20th SYNC_FULL, a summary of the RDP commands since the last one.
        private static readonly bool s_Acc2Log = Environment.GetEnvironmentVariable("COR64_ACC2_RDPLOG") == "1";
        private readonly int[] m_A2Ops = new int[64]; private int m_A2Sync; private readonly System.Text.StringBuilder m_A2Sb = new();
        private unsafe void Acc2Note(uint op, uint* c)
        {
            m_A2Ops[op]++;
            if (op == 0x2D || op == 0x3F || op == 0x3E || op == 0x37 || op == 0x3C || (op == 0x36 && m_A2Sb.Length < 600)) m_A2Sb.Append($" {op:X2}:{c[0]:X8}{c[1]:X8}");
            if (op >= 0x08 && op <= 0x0F && m_A2Ops[op] <= 2) m_A2Sb.Append($" T{op:X2}:{c[0]:X8}{c[1]:X8}{c[2]:X8}{c[3]:X8}");
            if (op != 0x29) return;
            if (++m_A2Sync % 20 == 0) {
                var sb = new System.Text.StringBuilder($"A2RDP #{m_A2Sync}");
                for (int i = 0; i < 64; i++) if (m_A2Ops[i] != 0) sb.Append($" {i:X2}x{m_A2Ops[i]}");
                if (m_A2Sb.Length > 3000) m_A2Sb.Length = 3000;
                System.Console.WriteLine(sb.Append(" |").Append(m_A2Sb).ToString());
            }
            Array.Clear(m_A2Ops); m_A2Sb.Clear();
        }

        private unsafe void Handler(DPCInterface.DisplayList displayList)
        {
            var dpc = DpcInterface;
            dpc.RFlags |= ReadStatusFlags.StartValid | ReadStatusFlags.EndValid | ReadStatusFlags.PipeBusy;
            var mem = Memory;
            int newWords = (int)((displayList.End - displayList.Start) / 4);
            if (m_PendingCount + newWords > m_Pending.Length) Array.Resize(ref m_Pending, Math.Max(m_Pending.Length * 2, m_PendingCount + newWords));
            if (displayList.End <= 0x800000) {
                // [cer64plus] RDRAM lists: host words already hold the big-endian word values, so copy them in one go
                // (word-by-word reads through the memory controller were ~10% of emulation time).
                uint* ram = (uint*)mem.RDRAM.GetRamPointer(0);
                fixed (uint* dst = &m_Pending[m_PendingCount])
                    Buffer.MemoryCopy(ram + (displayList.Start >> 2), dst, (long)newWords * 4, (long)newWords * 4);
                m_PendingCount += newWords;
            }
            else if (s_Ho1Xbus && displayList.Start >= 0x04000000 && displayList.End <= 0x04001000) {
                // [climb4 rdp_handoff1] COR64_RDPHO1_XBUS: XBUS lists straight from DMEM's buffer (same word layout as the
                // memory controller's Read32 sees; DMEM is never guarded), not one controller read per word.
                if (m_Dmem == IntPtr.Zero) m_Dmem = mem.SPRegs.ExportPointers().DMEMPtr;
                uint* dm = (uint*)m_Dmem;
                fixed (uint* dst = &m_Pending[m_PendingCount])
                    Buffer.MemoryCopy(dm + ((displayList.Start - 0x04000000) >> 2), dst, (long)newWords * 4, (long)newWords * 4);
                m_PendingCount += newWords;
            }
            else {
                for (int i = 0; i < newWords; i++) m_Pending[m_PendingCount++] = mem.U32(displayList.Start + i * 4); // XBUS (DMEM)
            }

            int pos = 0;
            fixed (uint* w = m_Pending) {
                while (pos + 2 <= m_PendingCount) {
                    uint op = (w[pos] >> 24) & 0x3F;
                    int words = CommandDwords(op) * 2;
                    if (pos + words > m_PendingCount) break;
                    if (!s_Drop || op == 0x29) { if (s_T) { long te = System.Diagnostics.Stopwatch.GetTimestamp(); Enqueue(words, w + pos); EnqTicks += System.Diagnostics.Stopwatch.GetTimestamp() - te; } else Enqueue(words, w + pos); }
                    if (words == 2) Latch(op, w[pos], w[pos + 1]);
                    TrackCommand(op, w[pos], w[pos + 1]);
                    if (s_Acc2Log) Acc2Note(op, w + pos);
                    if (RdpFillRate.On) RdpFillRate.Add(op, w + pos);
                    Commands++;
                    pos += words;
                    if (op == 0x29) { // SYNC_FULL
                        FlushBatch();
                        if (s_Deferred && s_DpAck && RcpInterface != null && RcpInterface.DpAcknowledged == null) RcpInterface.DpAcknowledged = m_DpAckHook ??= OnDpAck;
                        if (s_Deferred) {
                            long tw = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (m_PendingToken != 0) prdp_wait(m_Handle, m_PendingToken);
                            if (s_Ho1GWait) DropMarks(m_PendingToken);
                            ClearLive(); PublishAcc(); ClearAcc(); m_AccLive = !s_Defer; // [rdp_readback] this batch's pages are now in flight
                            m_PendingToken = prdp_signal(m_Handle);
                            if (s_Ho1GWait) TokenMarks(m_PendingToken);
                            NextFrameContext();
                            WaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tw;
                        }
                        else { prdp_sync_full(m_Handle); m_PendingToken = 0; ClearLive(); ClearMarks(); ClearAcc(); m_AccLive = !s_Defer; NextFrameContext(); }
                        FullSyncs++;
                        if (CompatLog.TraceTasks) System.Console.WriteLine($"DPSYNC full #{FullSyncs}");
                        if (RdpFillRate.On) { long ft = RdpFillRate.TakeTicks(); FillTicksTotal += ft; var ri = RcpInterface; ri.ScheduleDp(ft, () => ri.SetInterrupt(MipsInterface.INT_DP, true)); }
                        else RcpInterface.SetInterrupt(MipsInterface.INT_DP, true);
                    }
                }
            }
            if (pos > 0) { Array.Copy(m_Pending, pos, m_Pending, 0, m_PendingCount - pos); m_PendingCount -= pos; }

            dpc.RFlags &= ~(ReadStatusFlags.PipeBusy | ReadStatusFlags.StartValid | ReadStatusFlags.EndValid);
            dpc.RFlags |= ReadStatusFlags.CommandBufferReady;
        }

        // The managed command handlers are not used by this renderer.
        protected override void LoadBlock(RdpCommand command) { }
        protected override void LoadTile(RdpCommand command) { }
        protected override void LoadTlut(RdpCommand command) { }
        protected override void Rectangle(RdpCommand command) { }
        protected override void SetCombineMode(RdpCommand command) { }
        protected override void SetConvert(RdpCommand command) { }
        protected override void SetKey(RdpCommand command) { }
        protected override void SetOtherModes(RdpCommand command) { }
        protected override void SetColor(RdpCommand command) { }
        protected override void SetPrimDepth(RdpCommand command) { }
        protected override void SetScissor(RdpCommand command) { }
        protected override void SetTile(RdpCommand command) { }
        protected override void SetTileSize(RdpCommand command) { }
        protected override void SetImage(RdpCommand command) { }
        protected override void Triangle(RdpCommand command) { }
    }
}
