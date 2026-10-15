using System;
using System.IO;
using System.Text;

namespace NesEmulator.Sega;

/// <summary>What the VDP needs from the machine around it.</summary>
public interface IMdVdpHost
{
    /// <summary>A word of 68000 address space for DMA (the 68000 is held, so the read costs it nothing). ROM, RAM and the Z80 area answer; anything else reads as open bus.</summary>
    ushort ReadBusWord(uint address);

    /// <summary>The VDP's vertical-interrupt output to the Z80's /INT: asserted at the start of vertical blanking for one scanline.</summary>
    void SetZ80Interrupt(bool asserted, long masterClock);
}

/// <summary>
/// The Genesis video chip (YM7101 / 315-5313): VRAM, CRAM, VSRAM and the registers, the command and data port protocol, the write FIFO, DMA (68000 to VDP,
/// fill, copy), the status register, the H and V counters, the vertical and horizontal interrupts, and (in MdVdpRender.cs) the picture: planes, window, sprites, priority, shadow and highlight.
/// <see cref="RenderDebugView"/> and <see cref="RenderPlaneDump"/> still show the memories and the bare planes for debugging.
/// </summary>
/// <remarks>
/// <para><b>Time.</b> The VDP runs on the machine's master clock (3420 clocks a line in both widths). <see cref="RunTo"/> brings it to a time; everything it does is placed by the time
/// it is brought to, so the 68000 sees register reads, flag changes and FIFO room at the master-clock moment of its access. Work that needs the VDP's memory port (FIFO entries,
/// DMA words, read-ahead) is done in <b>access slots</b>: each line has a table of the slots that are free for it (data, see <see cref="SlotTable"/>): 210 slots in H40 and 171 in H32, of
/// which the refresh slots (5 and 4) are never free and, while the display is drawing, the fetch slots are not either (18 free in H40, 16 in H32; 205 and 167 in blanking), the numbers
/// of Sega's documentation. A VRAM word needs two slots, CRAM and VSRAM one. The slot table is the seam for a later slot-by-slot VDP.</para>
/// <para><b>Approximations that the next milestone tunes against test ROMs:</b> the slots are spread evenly through the line (hardware has irregular slot lengths around HSYNC); the V counter
/// steps at <see cref="VCounterStepOffset"/>; the horizontal-blank flag spans a fixed stretch; the vertical-blank flag, VINT and the Z80 interrupt follow the line numbers.</para>
/// </remarks>
public sealed partial class MdVdp
{
    // ---- memories and registers
    public readonly byte[] Vram = new byte[0x10000];
    public readonly ushort[] Cram = new ushort[64];
    public readonly ushort[] Vsram = new ushort[64];
    public readonly byte[] Reg = new byte[32];

    private IMdVdpHost host = NullHost.Instance;
    public SegaTimeline Timeline { get; }
    public SegaRegion Region => Timeline.Region;
    private readonly int linesPerFrame;
    public const int ClocksPerLine = SegaTiming.GenesisMasterClocksPerLine;

    public MdVdp(SegaTimeline timeline)
    {
        Timeline = timeline;
        linesPerFrame = timeline.LinesPerFrame;
        Reset();
    }

    /// <summary>The board connects the VDP to the machine around it (the DMA's source bus and the Z80's interrupt line).</summary>
    public void Attach(IMdVdpHost host) => this.host = host;

    private sealed class NullHost : IMdVdpHost
    {
        public static readonly NullHost Instance = new();
        public ushort ReadBusWord(uint address) => 0xFFFF;
        public void SetZ80Interrupt(bool asserted, long masterClock) { }
    }

    // ---- decoded registers
    public bool H40 => (Reg[12] & 0x81) == 0x81;
    public bool V30 => (Reg[1] & 0x08) != 0;
    public bool DisplayEnabled => (Reg[1] & 0x40) != 0;
    public bool Mode5 => (Reg[1] & 0x04) != 0;
    public bool DmaEnabled => (Reg[1] & 0x10) != 0;
    public bool VintEnabled => (Reg[1] & 0x20) != 0;
    public bool HintEnabled => (Reg[0] & 0x10) != 0;
    public bool HvCounterLatch => (Reg[0] & 0x02) != 0;
    public int ActiveLines => V30 ? 240 : 224;
    public int AutoIncrement => Reg[15];
    /// <summary>Interlace mode from register 12 bits 2-1: 0 off, 1 mode 1 (double-height lines), 3 mode 2.</summary>
    public int Interlace => Reg[12] >> 1 & 3;
    public int Slots => H40 ? 210 : 171;

    // ---- clock and position
    /// <summary>The master clock the VDP has run to.</summary>
    public long Time { get; private set; }
    private long lineStart;
    /// <summary>The line the VDP is on (0 is the first active line; the frame has 262 lines NTSC, 313 PAL).</summary>
    public int Line { get; private set; }
    public long Frames { get; private set; }
    /// <summary>Set when the frame wrapped (line 0 began) since it was last cleared by <see cref="ClearFrameFlag"/>.</summary>
    public bool FrameCompleted { get; private set; }
    public void ClearFrameFlag() => FrameCompleted = false;
    private bool oddFrame;

    // ---- interrupts
    private bool vintPending, hintPending;
    private int hintCounter;
    private long vintTime = long.MaxValue, hintTime = long.MaxValue, z80IntClear = long.MaxValue;
    private long vintEnableFrom, hintEnableFrom;     // an enable bit that has just been set takes effect after the VDP's own delay
    private long slotCursor;                         // the earliest time the next slot may be used (the one just used is spent)
    private bool z80IntAsserted;

    /// <summary>The interrupt lines have a short delay after a register write (the VDP asserts IPL 90 to 210 ns later, which the 68000's sampling turns into the one-instruction latency).</summary>
    public const long IplDelayMasterClocks = 8;

    /// <summary>VINT comes this long after the V counter steps to the first line after the picture (the plan's 788 master clocks in H40, 770 in H32; the step is in the line before, see <see cref="VCounterStepOffset"/>).</summary>
    public int VintDelay => H40 ? VintFromStepH40 : VintFromStepH32;
    public static int VintFromStepH40 = 788, VintFromStepH32 = 770;
    /// <summary>The VBlank status flag rises (and falls) this long after the V counter's step: measured on the oracle by status reads against HV reads (drift sampling, Track B4a): 33 clocks in H40, 49 in H32.</summary>
    public int VBlankFlagDelay => H40 ? VBlankFlagDelayH40 : VBlankFlagDelayH32;
    public static int VBlankFlagDelayH40 = 33, VBlankFlagDelayH32 = 49;
    /// <summary>The horizontal interrupt's counter steps this far into a line: 2642 clocks in H40, 2670 in H32 (fitted to the oracle's handler-entry histograms over 20,000 interrupts: about the V counter's step, not the start of horizontal blank).</summary>
    public int HIntOffset => H40 ? HIntOffsetH40 : HIntOffsetH32;
    public static int HIntOffsetH40 = 2642, HIntOffsetH32 = 2670;
    /// <summary>The lines whose end the interrupt counter counts: the pre-render line and lines 0 to ActiveLines-1 (the oracle's HINT handlers read the V counter 0 to 224: the line's end, after the counter's step; R10 = n first fires at the end of line n-1, then every n+1 lines, and never for n above 224); false: the old rule, lines 0 to ActiveLines.</summary>
    public static bool HIntPreRenderRule = true;
    /// <summary>The horizontal-blank status flag is set from here to the end of the line (it falls with the next line's H counter 0): 2855 clocks in H40 (counter $165), 2975 in H32 (counter $129), measured on the oracle.</summary>
    public int HBlankStart => H40 ? HBlankStartH40 : HBlankStartH32;
    public static int HBlankStartH40 = 2855, HBlankStartH32 = 2975, HBlankEnd = 0;
    /// <summary>The V counter steps this far into a line: H counter $14A (top byte $A5) in H40, $10A ($85) in H32, in the horizontal blank (measured on the oracle with straight HV scans: the first sample after a V change
    /// reads $A5 at the earliest in H40 and $85 in H32).</summary>
    public int VCounterStepOffset => H40 ? VCounterStepH40 : VCounterStepH32;
    public static int VCounterStepH40 = 2640, VCounterStepH32 = 2660;

    // ---- the port protocol
    private bool pendingCommand;
    private ushort commandFirst;
    private int code;                  // CD5-CD0
    private uint address;              // 17 bits used (VRAM 64 KB: 16)
    private ushort readBuffer;
    private long readReadyAt;
    private int readCacheCode;         // what the read-ahead was taken for, so that it can be taken again after the FIFO's writes land
    private uint readCacheAddress;
    private int hvLatched = -1;       // the HV counter frozen by R0 bit 1, or -1 while it runs
    private bool readStale;            // the read-ahead was taken while writes were still queued; the word is fetched again once they are done

    /// <summary>The access code of the current command (bits CD5-CD0: 0 VRAM read, 1 VRAM write, 3 CRAM write, 5 VSRAM write, 8 CRAM read, 4 VSRAM read).</summary>
    public int Code => code;
    public uint Address => address;
    public bool CommandPending => pendingCommand;

    // ---- the write FIFO
    private struct FifoEntry { public int Code; public uint Address; public ushort Data; public int SlotsLeft; public bool FromDma; }
    private readonly FifoEntry[] fifo = new FifoEntry[4];
    private int fifoHead, fifoCount;
    private ushort lastFifoData;       // the last word the 68000 or a DMA put in the FIFO (a data port read while a write target is set returns it)

    /// <summary>Census counters (Workshop --md-census): writes that land while a picture line is being drawn (mid-frame tricks), and the interrupts the 68000 took.</summary>
    public long ActiveRegWrites, ActiveCramWrites, ActiveVsramWrites, ActiveVramWrites, HintTaken, VintTaken;
    public readonly long[] ActiveRegWritesBy = new long[24];

    /// <summary>The writes the VDP has taken from the 68000 and Z80 (data port words and register/command writes), for tests.</summary>
    public long DataWrites, ControlWrites, FifoStalls, DmaWords;
    public long FifoStallClocks;

    // ---- DMA
    private enum DmaKind { None, Bus, Fill, Copy }
    private DmaKind dma = DmaKind.None;
    private bool fillArmed;            // fill DMA waits for the next data port write
    private uint dmaSource;            // byte address (68000 space) for a bus transfer, VRAM byte address for a copy
    private int dmaLength;
    private int dmaSlotsLeft;          // slots the word or byte in progress still needs
    private ushort fillData;
    private int dmaTargetCode;
    private int fillSourceStart, fillLengthStart;     // a fill steps the source address registers by its length, as a transfer does
    private long dmaStartTime;         // the DMA engine's first fetch is no earlier than this (the command's time plus its start-up)
    private long dmaNextFetch;         // a bus transfer fetches a word every DmaFetchClocks while the FIFO has room: not before this
    private int dmaInFifo;             // how many of the FIFO's entries are words of the running transfer
    /// <summary>Master clocks between the slot that lets a held 68000 go (a full FIFO, a finished DMA) and the 68000 running again.</summary>
    public static int ReleaseLatency = 16;
    /// <summary>The release latency and the fetch period scale with the slot length: H32 slots are 20 clocks, H40 ones 16.</summary>
    public int ReleaseClocks => ReleaseLatency * (H40 ? 16 : 20) / 16;
    public static int DmaReleaseLatency = 16;
    public int DmaReleaseClocks => DmaReleaseLatency * (H40 ? 16 : 20) / 16;
    private int FetchClocks => DmaFetchClocks * (H40 ? 16 : 20) / 16;

    /// <summary>How long after the command word a DMA waits before its first fetch (master clocks), and the time between fetches while the FIFO has room: a bus transfer reads the 68000's bus at its own pace and only the
    /// writes into VRAM, CRAM and VSRAM need slots. Measured against the oracle with FIFO-synchronised starts (the direct colour demo's lead-in): in blanked lines the 68000 runs again one tick (16 clocks) per word
    /// after the command; with the FIFO partly full the first two words are fetched together.</summary>
    public static int DmaStartDelay = 56, DmaFetchClocks = 16, DmaFifoLimit = 4, DmaBlankLimit = 2;
    /// <summary>The DMA engine fetches nothing while the VDP is in a refresh slot.</summary>
    public static bool DmaSkipsRefresh = true;

    /// <summary>The VDP holds the 68000 while a bus DMA runs.</summary>
    public bool DmaHaltsCpu => dma == DmaKind.Bus;
    public bool DmaBusy => dma != DmaKind.None || fillArmed;

    /// <summary>Called with a line describing each port write, when set (traces and debugging; null in normal runs).</summary>
    public Action<string>? Trace;

    public void Reset()
    {
        Array.Clear(Vram); Array.Clear(Cram); Array.Clear(Vsram); Array.Clear(Reg);
        Time = 0; lineStart = 0; Line = 0; Frames = 0; FrameCompleted = false; oddFrame = false;
        vintPending = hintPending = false; hintCounter = 0; vintTime = hintTime = z80IntClear = long.MaxValue; vintEnableFrom = hintEnableFrom = 0; z80IntAsserted = false;
        pendingCommand = false; commandFirst = 0; code = 0; address = 0; readBuffer = 0; readReadyAt = 0; hvLatched = -1; readStale = false;
        fifoHead = fifoCount = 0; lastFifoData = 0;
        dma = DmaKind.None; fillArmed = false; dmaLength = 0; dmaSlotsLeft = 0; dmaStartTime = 0; dmaNextFetch = 0; dmaInFifo = 0;
        DataWrites = ControlWrites = FifoStalls = DmaWords = 0; FifoStallClocks = 0;
        // the post-boot state the console's own code leaves is the game's business; the chip itself powers on with everything zero
        hintTime = lineStart + HIntOffset; slotCursor = 0;
        InitRender();
        BeginLineRender();
    }

    // =================================================================================================================
    //  Access slots
    // =================================================================================================================

    /// <summary>The first picture pixel of a line comes this many master clocks after the line's H counter is 0: 12 counter ticks (24 pixels) in either width (measured against the oracle: a register write made
    /// at a given H counter reading shows 24 pixels further left than the old assumption "pixel 0 is counter 0" put it; in H32 the 171 slots of a blanked line then sit on one 20-clock grid, slot 12 = pixel 0).</summary>
    public int PixelOrigin => H40 ? SlotTable.OriginH40 : SlotTable.OriginH32;

    /// <summary>
    /// The slots of one line that are free for the CPU, the FIFO and DMA: <c>Free[h40][active]</c> are the offsets in master clocks from the line's H counter 0, sorted, of the slots in the line's <i>window</i>:
    /// from the V counter's step in the line before (<see cref="VCounterStepOffset"/>, so some offsets are negative) up to the step in this line. Which pattern a slot follows is decided by the line that owns the
    /// window (found with the probe ROMs: the FIFO of the last blank line drains as fast as a blank line until the step and as slowly as a picture line after it).
    /// A line is 210 slots (H40) or 171 (H32). Slot s starts 16 (20 in H32) clocks after slot s-1, except that the H40 line's last 38 slots share the 668 clocks the horizontal sync leaves; the picture's pixel 0 is
    /// slot 12, a slot is two pixels. A blanked line has 12 non-free slots in H40 (six refresh pairs, <see cref="BlankRefreshH40"/>) and 4 in H32; a picture line only a few free ones (18 in H40, 16 in H32, measured
    /// as the pixel columns of colour RAM dots and, for the slots before pixel 0, by when a stalled 68000 is let go). All of it is data: <see cref="Rebuild"/> after changing a list.
    /// </summary>
    public static class SlotTable
    {
        /// <summary>Master clocks from a line's H counter 0 to its picture pixel 0 (12 ticks).</summary>
        public static int OriginH40 = 192, OriginH32 = 240;

        /// <summary>Where in a line the slot pattern of the next line takes over (the window boundary): offsets from H counter 0. H40: the V counter's step. H32: any value from 2790 to 2920 reproduces the oracle's picture of Nemesis'
        /// CRAM flicker test pixel for pixel (the slots between the last picture line's dots and the tail slots; below 2790 the dots of the last line move, at 2960 the tail slots do): 2800 is the first slot start in that range.</summary>
        public static int CutH40 = 2640, CutH32 = 2800;

        /// <summary>The picture pixel of each free slot of a drawn line (the oracle's dots sit at these columns on every line) and the H counter ticks (slot numbers) of the free slots before pixel 0.</summary>
        public static int[] ActivePixelsH40 = { 12, 44, 60, 76, 108, 124, 140, 172, 188, 204, 236, 252, 268, 298, 300 };
        public static int[] ActivePixelsH32 = { 12, 44, 60, 76, 108, 124, 140, 172, 188, 204, 234, 236 };
        public static int[] ActivePreTicksH40 = { 187, 2, 10 };
        public static int[] ActivePreTicksH32 = { 148, 159, 2, 11 };
        /// <summary>Slots (counting from slot 0 at the line's H counter 0) that a blanked line cannot use: the refresh pairs. H40 has six pairs (five in the picture, the sixth in the horizontal blank); H32 four single slots.</summary>
        public static int[] BlankRefreshH40 = { 26, 29, 59, 62, 90, 93, 122, 125, 154, 157, 186, 189 };
        public static int[] BlankRefreshH32 = { 26, 58, 90, 122 };

        // a slot is one tick of the H counter's 8-bit value: tick i is the i-th value of the line's sequence ($00-$B6 then $E4-$FF in H40, $00-$93 then $E9-$FF in H32)
        private static readonly int[][] tickStart = new int[2][];

        public static int[][][] Free = Build();

        public static void Rebuild() { tickStart[0] = tickStart[1] = null; Free = Build(); }

        /// <summary>Master clocks from H counter 0 at which tick (slot) <paramref name="s"/> starts, following the H counter's own function (<see cref="HCounter9"/>).</summary>
        public static int SlotStart(bool h40, int s)
        {
            var t = tickStart[h40 ? 1 : 0] ??= BuildTickStarts(h40);
            return t[s];
        }

        /// <summary>Clocks to wait from line offset <paramref name="off"/> (taken modulo the line) until the refresh slot it falls in is over (0 when it is not in one): the DMA engine fetches nothing during a refresh slot.</summary>
        public static int RefreshWait(bool h40, int off)
        {
            off %= ClocksPerLine;
            var t = tickStart[h40 ? 1 : 0] ??= BuildTickStarts(h40);
            int lo = 0, hi = t.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (t[mid] <= off) lo = mid; else hi = mid - 1; }     // the tick this offset is in
            foreach (int r in h40 ? BlankRefreshH40 : BlankRefreshH32)
                if (r == lo) return (lo + 1 < t.Length ? t[lo + 1] : ClocksPerLine) - off;
            return 0;
        }

        private static int[] BuildTickStarts(bool h40)
        {
            int lastMain = h40 ? 0xB6 : 0x93, firstJump = h40 ? 0xE4 : 0xE9, ticks = h40 ? 211 : 171;
            var t = new int[ticks]; for (int i = 0; i < ticks; i++) t[i] = -1;
            for (int off = 0; off < ClocksPerLine; off++)
            {
                int h8 = HCounter9(h40, off) >> 1;
                int idx = h8 <= lastMain ? h8 : h8 >= firstJump ? lastMain + 1 + h8 - firstJump : -1;
                if (idx >= 0 && idx < ticks && t[idx] < 0) t[idx] = off;
            }
            for (int i = 0; i < ticks; i++) if (t[i] < 0) t[i] = i > 0 ? t[i - 1] : 0;
            return t;
        }

        private static int[][][] Build()
        {
            var t = new int[2][][];
            for (int w = 0; w < 2; w++)
            {
                bool h40 = w == 1;
                int total = h40 ? 210 : 171;
                int origin = h40 ? OriginH40 : OriginH32, pxDiv = h40 ? 8 : 10;
                t[w] = new int[2][];
                // the pattern of a counter line, offsets 0..3419: blanked first
                var blank = new System.Collections.Generic.List<int>();
                var refresh = new System.Collections.Generic.HashSet<int>(h40 ? BlankRefreshH40 : BlankRefreshH32);
                for (int s = 0; s < total; s++) if (!refresh.Contains(s)) blank.Add(SlotStart(h40, s));
                var active = new System.Collections.Generic.List<int>();
                foreach (int tick in h40 ? ActivePreTicksH40 : ActivePreTicksH32) active.Add(SlotStart(h40, tick));
                foreach (int px in h40 ? ActivePixelsH40 : ActivePixelsH32) active.Add(origin + px * pxDiv);
                t[w][0] = Window(blank, h40);
                t[w][1] = Window(active, h40);
                if (t[w][0].Length != (h40 ? 198 : 167)) throw new InvalidOperationException("slot table: blanked free-slot count");
                if (t[w][1].Length != (h40 ? 18 : 16)) throw new InvalidOperationException("slot table: picture free-slot count");
            }
            return t;
        }

        /// <summary>The counter line's pattern as the window of a line: offsets from the step on are the line before's end, so they go in front as negative ones.</summary>
        private static int[] Window(System.Collections.Generic.List<int> pattern, bool h40)
        {
            var w = new System.Collections.Generic.List<int>();
            int cut = h40 ? CutH40 : CutH32;
            foreach (int o in pattern) w.Add(o >= cut ? o - ClocksPerLine : o);
            w.Sort();
            return w.ToArray();
        }
    }

    private int[] FreeSlotsFor(int line)
    {
        // the picture lines and the last line of the frame (the pre-render line: the chip fetches line 0's sprites and tiles in it) have the display's slots; every other line is blanked
        bool active = DisplayEnabled && (line < ActiveLines || line == linesPerFrame - 1);
        return SlotTable.Free[H40 ? 1 : 0][active ? 1 : 0];
    }

    private static int LowerBound(int[] table, long off)
    {
        int lo = 0, hi = table.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (table[mid] < off) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// The master clock of the first free slot at or after <paramref name="from"/>. The VDP's idea of the line changes with the V counter, in the middle of the horizontal blank: from the counter's step
    /// (<see cref="VCounterStepOffset"/>) on the next line's slots apply (see <see cref="SlotTable"/>: its window starts with the end of this line).
    /// </summary>
    private long NextFreeSlot(long from)
    {
        int line = Line; long ls = lineStart; int cut = H40 ? SlotTable.CutH40 : SlotTable.CutH32;
        for (int guard = 0; guard < 4; guard++)
        {
            long off = Math.Max(from - ls, 0);
            if (off < cut)
            {
                var a = FreeSlotsFor(line);
                int i = LowerBound(a, off);
                if (i < a.Length && a[i] < cut) return ls + a[i];
                off = cut;
            }
            int next = line + 1 >= linesPerFrame ? 0 : line + 1;
            var b = FreeSlotsFor(next);
            int j = LowerBound(b, off - ClocksPerLine);
            if (j < b.Length && b[j] < 0) return ls + ClocksPerLine + b[j];       // a slot of the next window that falls before the line ends
            line = next; ls += ClocksPerLine; from = ls;
        }
        return from;
    }

    // =================================================================================================================
    //  Running
    // =================================================================================================================

    private long NextEvent()
    {
        long e = lineStart + ClocksPerLine;
        if (hintTime < e) e = hintTime;
        if (vintTime < e) e = vintTime;
        if (z80IntClear < e) e = z80IntClear;
        return e;
    }

    /// <summary>Bring the VDP to master clock <paramref name="t"/>: lines turn over, interrupts and flags change, queued work finishes in the slots it gets.</summary>
    public void RunTo(long t)
    {
        if (t <= Time) return;
        while (Time < t)
        {
            long evt = NextEvent();
            long slot = long.MaxValue;
            bool work = SlotWork;
            if (work) slot = NextFreeSlot(WorkFrom());
            long fetch = NextFetchTime();
            long next = t;
            if (evt < next) next = evt;
            if (slot < next) next = slot;
            if (fetch < next) next = fetch;
            if (next < Time) next = Time;
            Time = next;
            if (work && slot <= next) { ConsumeSlot(); continue; }       // a slot first: a write from the FIFO makes room for a word the transfer fetches at the same time
            if (fetch <= Time) { FetchDmaWord(); continue; }
            if (evt <= Time) FireEvents();
        }
        if (NextEvent() <= Time) FireEvents();      // anything due exactly at t
    }

    private void FireEvents()
    {
        if (z80IntClear <= Time) { z80IntClear = long.MaxValue; if (z80IntAsserted) { z80IntAsserted = false; host.SetZ80Interrupt(false, Time); } }
        if (hintTime <= Time) { long at = hintTime; hintTime = long.MaxValue; FireHint(); }
        if (vintTime <= Time) { vintTime = long.MaxValue; FireVint(); }
        if (lineStart + ClocksPerLine <= Time) StartNextLine();
    }

    private void FireHint()
    {
        // the lines in the counting phase step the counter; the others reload it
        if (HIntCounting(Line))
        {
            if (hintCounter == 0) { hintPending = true; hintCounter = Reg[10]; }
            else hintCounter--;
        }
        else hintCounter = Reg[10];
    }

    private bool HIntCounting(int line) => HIntPreRenderRule ? line < ActiveLines || line == linesPerFrame - 1 : line <= ActiveLines;

    private void FireVint()
    {
        vintPending = true;
        if (!z80IntAsserted) { z80IntAsserted = true; host.SetZ80Interrupt(true, Time); }
        z80IntClear = Time + ClocksPerLine;
    }

    private void StartNextLine()
    {
        FinishLineRender();
        lineStart += ClocksPerLine;
        Line++;
        if (Line >= linesPerFrame)
        {
            Line = 0; Frames++; FrameCompleted = true;
            oddFrame = Interlace != 0 && !oddFrame;
        }
        hintTime = lineStart + HIntOffset;
        // the vertical blank begins at the V counter's step in the last picture line; VINT follows that step by its delay (and so falls early in the first blank line)
        if (Line == ActiveLines - 1) vintTime = lineStart + VCounterStepOffset + VintDelay;
        BeginLineRender();
    }

    /// <summary>Master clock of the next thing that changes the VDP's visible state: a line boundary, an interrupt or flag, or a free slot with work waiting.</summary>
    public long NextWorkTime()
    {
        long next = NextEvent();
        if (SlotWork) { long s = NextFreeSlot(WorkFrom()); if (s < next) next = s; }
        long fetch = NextFetchTime();
        if (fetch < next) next = fetch;
        return next;
    }

    /// <summary>Something waits for a free slot: a FIFO entry to write, or a fill or copy to step.</summary>
    private bool SlotWork => fifoCount > 0 || dma == DmaKind.Copy || (dma == DmaKind.Fill && !fillArmed);

    /// <summary>The earliest time work may use a slot: after the last slot, and when only a fill or copy is waiting (the FIFO has nothing to write) not before the DMA engine has started.</summary>
    private long WorkFrom()
    {
        long f = Math.Max(Time, slotCursor);
        if (dma != DmaKind.None && fifoCount == 0 && dmaStartTime > f) f = dmaStartTime;
        return f;
    }

    /// <summary>When a bus transfer fetches its next word: from the engine's start on, every <see cref="DmaFetchClocks"/> while the FIFO has room (a full FIFO waits for the write that makes room).</summary>
    private long NextFetchTime()
    {
        if (dma != DmaKind.Bus || dmaLength <= 0 || fifoCount >= 4 || dmaInFifo >= (DisplayEnabled ? DmaFifoLimit : DmaBlankLimit)) return long.MaxValue;
        long t = Math.Max(Math.Max(Time, dmaNextFetch), dmaStartTime);
        if (DmaSkipsRefresh) t += SlotTable.RefreshWait(H40, (int)((t - lineStart) % ClocksPerLine));
        return t;
    }

    /// <summary>Run the VDP to its next event or slot (always at least one clock), so a caller waiting for the FIFO or for a DMA to finish always makes progress.</summary>
    public void AdvanceWork()
    {
        long n = NextWorkTime();
        RunTo(n > Time ? n : Time + 1);
    }

    /// <summary>The current line's offset in master clocks.</summary>
    public int LineOffset => (int)(Time - lineStart);

    /// <summary>The interrupt priority level the VDP is driving at master clock <paramref name="t"/> (0 none, 6 vertical, 4 horizontal), after the delay a just-enabled interrupt has.</summary>
    public int IplAt(long t)
    {
        if (vintPending && VintEnabled && t >= vintEnableFrom) return 6;
        if (hintPending && HintEnabled && t >= hintEnableFrom) return 4;
        return 0;
    }

    /// <summary>The 68000 acknowledged an interrupt of <paramref name="level"/>: the matching latch clears.</summary>
    public void AcknowledgeInterrupt(int level)
    {
        if (level == 6) { vintPending = false; VintTaken++; }
        else if (level == 4) { hintPending = false; HintTaken++; }
    }

    // =================================================================================================================
    //  Work done in a slot
    // =================================================================================================================

    private void ConsumeSlot()
    {
        slotCursor = Time + 1;
        SyncRender();       // the memories this slot may write are drawn up to the pixel being output first
        if (fifoCount > 0)
        {
            ref var e = ref fifo[fifoHead];
            if (--e.SlotsLeft <= 0)
            {
                Trace?.Invoke($"fifo commit code {e.Code:X} at line {Line}+{LineOffset} left {fifoCount - 1}");
                WriteTarget(e.Code, e.Address, e.Data);
                if (e.FromDma) dmaInFifo--;
                fifoHead = fifoHead + 1 & 3; fifoCount--;
            }
            return;
        }
        if (dma != DmaKind.None && Time >= dmaStartTime)
            switch (dma)
            {
                case DmaKind.Fill: StepFill(); break;
                case DmaKind.Copy: StepCopy(); break;
            }
    }
    private static int SlotsFor(int code) => (code & 0xF) == 1 ? 2 : 1;

    /// <summary>The word the next FIFO write would overwrite: stale or not yet written, it is the data the chip holds in that entry, and a read of CRAM, VSRAM or the 8-bit VRAM target takes its unused bits from it.</summary>
    private ushort NextFifoData => fifo[fifoHead + fifoCount & 3].Data;

    /// <summary>Put a word in the next FIFO entry (the caller made sure there is room). Writes to a target that is not a write target take their place in the FIFO too and are lost when their turn comes.</summary>
    private void PushFifo(int c, uint addr, ushort data, bool fromDma = false)
    {
        int idx = fifoHead + fifoCount & 3;
        fifo[idx] = new FifoEntry { Code = c, Address = addr, Data = data, SlotsLeft = SlotsFor(c), FromDma = fromDma };
        if (fromDma) dmaInFifo++;
        fifoCount++;
        lastFifoData = data;
    }

    /// <summary>A bus transfer's next word: read from the 68000's space into the FIFO (the 68000 is held, so the read costs it nothing). The transfer is over, and the 68000 let go, when the last word has been fetched;
    /// the word is written from the FIFO in a later slot.</summary>
    private void FetchDmaWord()
    {
        SyncRender();
        ushort w = host.ReadBusWord(dmaSource);
        Trace?.Invoke($"dma word at line {Line}+{LineOffset} fifo {fifoCount}");
        PushFifo(dmaTargetCode, address, w, true);
        DmaWords++;
        dmaNextFetch = Time + FetchClocks;
        address = (address + (uint)AutoIncrement) & 0x1FFFF;
        // the source wraps inside its 128 KB window
        dmaSource = (dmaSource & 0xFE0000) | ((dmaSource + 2) & 0x1FFFF);
        if (--dmaLength <= 0) FinishDma();
    }

    /// <summary>Write a word to VRAM, CRAM or VSRAM at <paramref name="addr"/> (the address register as the command left it).</summary>
    private void WriteTarget(int c, uint addr, ushort data)
    {
        switch (c & 0xF)
        {
            case 1:      // VRAM: a word at an odd address lands byte-swapped
            {
                if (LineIsActive) ActiveVramWrites++;
                int a = (int)(addr & 0xFFFF);
                if ((a & 1) != 0) data = (ushort)(data << 8 | data >> 8);
                a &= ~1;
                Vram[a] = (byte)(data >> 8); Vram[a + 1] = (byte)data;
                break;
            }
            case 3: OnCramWrite((int)(addr >> 1) & 0x3F, data); break;
            case 5:
            {
                // VSRAM holds 40 words: a write to one of the other addresses of its 64-word range is lost, and a read there returns entry 0 (BlastEm and Nemesis' fill-to-VSRAM tests: entries 40-63 read as entry 0)
                int i = (int)(addr >> 1) & 0x3F;
                if (LineIsActive) ActiveVsramWrites++;
                if (i < 40) Vsram[i] = (ushort)(data & 0x07FF);
                break;
            }
        }
    }

    /// <summary>
    /// What a data port read caches: the memory's own bits, and for the memories narrower than the word the rest of the word is not zero but the data left in the FIFO entry the next write would
    /// use (CRAM keeps bits 1-3, 5-7 and 9-11, VSRAM the low 11 bits, the 8-bit VRAM target the low byte). Hardware tests (Nemesis' VDP port access ROM) show this on real consoles.
    /// </summary>
    private ushort ReadSource(int c, uint addr)
    {
        ushort slot = NextFifoData;
        switch (c & 0xF)
        {
            case 0: { int a = (int)(addr & 0xFFFE); return (ushort)(Vram[a] << 8 | Vram[a + 1]); }
            case 8: return (ushort)(Cram[(addr >> 1) & 0x3F] | slot & ~0x0EEE);
            case 4: { int i = (int)(addr >> 1) & 0x3F; return (ushort)(Vsram[i < 40 ? i : 0] | slot & 0xF800); }
            case 12: return (ushort)(Vram[(addr & 0xFFFF) ^ 1] | slot & 0xFF00);       // the undocumented 8-bit read: the byte at the address with its low bit flipped
        }
        return 0;
    }

    private void StepFill()
    {
        if (--dmaSlotsLeft > 0) return;
        // the target is whatever the code register names at the time of each write (a register write after the fill command makes it no target: the fill still runs but writes nothing)
        switch (code & 0xF)
        {
            case 1: Vram[(address ^ 1) & 0xFFFF] = (byte)(fillData >> 8); break;
            default: WriteTarget(code, address, NextFifoData); break;      // CRAM and VSRAM: the stale next entry, looked at afresh for every write
        }
        address = (address + (uint)AutoIncrement) & 0x1FFFF;
        dmaSlotsLeft = 1;
        if (--dmaLength <= 0) FinishDma();
    }

    private void StepCopy()
    {
        if (dmaSlotsLeft == 0) dmaSlotsLeft = 2;
        if (--dmaSlotsLeft > 0) return;
        byte b = Vram[(dmaSource ^ 1) & 0xFFFF];            // a copy moves bytes in the VDP's own byte order, like a fill: the address with its low bit flipped on both sides
        Vram[(address ^ 1) & 0xFFFF] = b;
        dmaSource = (dmaSource + 1) & 0xFFFF;
        address = (address + (uint)AutoIncrement) & 0x1FFFF;
        if (--dmaLength <= 0) FinishDma();
    }

    private void FinishDma()
    {
        var kind = dma;
        Trace?.Invoke($"dma {kind} done at line {Line}+{LineOffset} fifo {fifoCount}");
        dma = DmaKind.None; dmaSlotsLeft = 0;
        // the length registers end at zero, the source registers where a bus transfer or copy stopped
        Reg[19] = 0; Reg[20] = 0;
        if (kind == DmaKind.Bus)
        {
            uint src = dmaSource >> 1;
            Reg[21] = (byte)src; Reg[22] = (byte)(src >> 8);
            Reg[23] = (byte)(Reg[23] & 0x80 | (src >> 16) & 0x7F);
        }
        else if (kind == DmaKind.Copy)
        {
            Reg[21] = (byte)dmaSource; Reg[22] = (byte)(dmaSource >> 8);
        }
        else if (kind == DmaKind.Fill)
        {
            int v = fillSourceStart + fillLengthStart & 0xFFFF;
            Reg[21] = (byte)v; Reg[22] = (byte)(v >> 8);
        }
    }

    // =================================================================================================================
    //  The 68000's view: ports
    // =================================================================================================================

    /// <summary>A control port write (a word; a byte write is that byte on both halves). Starts DMA when the command says so.</summary>
    public void WriteControl(ushort value, long t)
    {
        RunTo(t);
        ControlWrites++;
        if (!pendingCommand)
        {
            if ((value & 0xC000) == 0x8000)
            {
                RegisterWrite((value >> 8) & 0x1F, (byte)value);
                code = code & ~3 | 2;       // the word's top two bits (10) land in CD1:0 like any first word's: the target is no write target until a command sets it again (Nemesis: Register Writes and Code Reg)
                return;
            }
            commandFirst = value;
            code = code & ~3 | value >> 14 & 3;
            address = address & 0xC000 | (uint)(value & 0x3FFF);
            pendingCommand = true;
            return;
        }
        pendingCommand = false;
        code = code & 3 | value >> 2 & 0x3C;
        address = address & 0x3FFF | (uint)(value & 3) << 14 | (uint)(value >> 2 & 1) << 16;
        Trace?.Invoke($"cmd code={code:X2} addr={address:X4}");
        if ((code & 0x20) != 0 && DmaEnabled && dma == DmaKind.None) StartDma();
        else if ((code & 0x20) != 0 && !DmaEnabled) { /* a DMA command while DMA is disabled is an ordinary command with CD5 */ }
        if ((code & 0x30) == 0 && (code & 0xF) is 0 or 4 or 8 or 12) StartReadAhead();    // a read command fetches the first word
    }

    private void RegisterWrite(int reg, byte value)
    {
        if (reg > 10 && !Mode5) return;       // the extra registers belong to mode 5
        bool vintBefore = VintEnabled, hintBefore = HintEnabled;
        if (LineIsActive) { ActiveRegWrites++; if (reg < 24) ActiveRegWritesBy[reg]++; }
        SyncRender();
        if (reg is 1 or 7 or 12) RewindIndexStage();      // the display switch, the backdrop colour and the shadow mode act at the output, not at the fetch the stage runs ahead with
        Reg[reg] = value;
        if (reg == 0) hvLatched = (value & 2) == 0 ? -1 : hvLatched >= 0 ? hvLatched : HvNow();
        if (reg == 1 && VintEnabled && !vintBefore) vintEnableFrom = Time + IplDelayMasterClocks;
        if (reg == 0 && HintEnabled && !hintBefore) hintEnableFrom = Time + IplDelayMasterClocks;
        Trace?.Invoke($"reg {reg:X2}={value:X2}");
        if (reg == 10 && !HIntCounting(Line)) hintCounter = value;
    }

    private void StartReadAhead()
    {
        readCacheCode = code; readCacheAddress = address;
        readStale = fifoCount > 0;
        readBuffer = ReadSource(code, address);
        address = (address + (uint)AutoIncrement) & 0x1FFFF;
        readReadyAt = NextFreeSlot(Math.Max(Time, slotCursor));
    }

    /// <summary>A data port write. The word joins the FIFO; if that is full the call runs the VDP forward until there is room (<see cref="Time"/> then says when the 68000 may go on).</summary>
    public void WriteData(ushort value, long t)
    {
        RunTo(t);
        DataWrites++;
        pendingCommand = false;
        if (dma == DmaKind.Copy) return;
        // every data port write takes a FIFO entry, whatever the target: a word written while a read target is set advances the FIFO and is lost
        long stallFrom = Time;
        while (fifoCount == 4) { FifoStalls++; AdvanceWork(); }
        FifoStallClocks += Time - stallFrom;
        if (Time > stallFrom && ReleaseClocks > 0) RunTo(Time + ReleaseClocks);       // a held 68000 is let go a little after the slot that made room (measured: the oracle's reads are a tick later)
        if (dma == DmaKind.Fill)
        {
            // the 68000 is not held during a fill. A data port write meanwhile takes a FIFO entry that is written nowhere, replaces the word the fill repeats, and adds one step to the fill's
            // length (Nemesis' DP-writes-during-fill tests); the address does not move
            PushFifo(2, address, value);
            fillData = value;
            dmaLength++;
            return;
        }
        PushFifo(code, address, value);
        address = (address + (uint)AutoIncrement) & 0x1FFFF;
        if (fillArmed)
        {
            // the word that starts a fill is written like any other (it went into the FIFO above, to whatever target the code register names now); the fill that follows repeats its high byte
            // into the target the DMA command named and leaves the FIFO alone
            fillArmed = false; dma = DmaKind.Fill;
            // VRAM fills repeat the high byte of the word that started them; fills of CRAM and VSRAM write the data left in the next FIFO entry, whichever it is at the time of each write
            // (hardware, Nemesis' fill tests: the word that started the fill goes in as an ordinary write and the rest take the stale entry after it, which moves on when the 68000 writes meanwhile)
            fillData = value;
            dmaSlotsLeft = 1;
            Trace?.Invoke($"fill start data={value:X4} len={dmaLength}");
        }
    }

    /// <summary>A data port read: waits for the read-ahead word, returns it, and starts the next read.</summary>
    public ushort ReadData(long t)
    {
        RunTo(t);
        WaitForDmaIdle();
        pendingCommand = false;
        if ((code & 1) != 0) return lastFifoData;      // reading while a write command is set returns the FIFO's last word
        // make sure the FIFO has drained and the read-ahead has had its slot
        while (fifoCount > 0) AdvanceWork();
        if (readStale) { readBuffer = ReadSource(readCacheCode, readCacheAddress); readStale = false; }      // the writes queued ahead of the read have landed: it sees them
        if (readReadyAt > Time) RunTo(readReadyAt);
        ushort data = readBuffer;
        readCacheCode = code; readCacheAddress = address;
        readBuffer = ReadSource(code, address);
        address = (address + (uint)AutoIncrement) & 0x1FFFF;
        readReadyAt = NextFreeSlot(Math.Max(Time, slotCursor));
        return data;
    }

    /// <summary>A read of the status register.</summary>
    public ushort ReadStatus(long t)
    {
        RunTo(t);
        pendingCommand = false;
        int s = 0;
        if (fifoCount == 0) s |= 0x200;
        if (fifoCount == 4) s |= 0x100;
        if (vintPending) s |= 0x80;
        if (sprOverflow) s |= 0x40;
        if (sprCollision) s |= 0x20;
        sprOverflow = sprCollision = false;
        if (oddFrame) s |= 0x10;
        if (VBlankFlag || !DisplayEnabled) s |= 0x08;
        if (InHBlank) s |= 0x04;
        if (DmaBusy) s |= 0x02;
        if (Region == SegaRegion.Pal) s |= 0x01;
        Trace?.Invoke($"status {s:X3} line {Line}+{LineOffset} fifo {fifoCount}");
        return (ushort)s;
    }

    /// <summary>The status register is ten bits wide; the top six of the word the 68000 reads are the bus's (on hardware whatever the prefetch left there), which the board fills in.</summary>
    public const int StatusMask = 0x03FF;

    public bool InVBlank => Line >= ActiveLines;

    /// <summary>The status register's vertical-blank bit follows the V counter, not the line: it rises when the counter steps to the first line after the picture (224) and falls when it steps to the last line of
    /// the frame (the pre-render line, 261 NTSC), both at the counter's step in the line before (BlastEm and the probe ROMs agree: the counter's value 224 shows with the flag still low for a few pixels, the
    /// flag drops as the counter reads 255).</summary>
    public bool VBlankFlag
    {
        get
        {
            int vl = LineOffset >= VCounterStepOffset + VBlankFlagDelay ? (Line + 1 >= linesPerFrame ? 0 : Line + 1) : Line;
            return vl >= ActiveLines && vl < linesPerFrame - 1;
        }
    }
    public bool InHBlank { get { int o = LineOffset; return o >= HBlankStart || o < HBlankEnd; } }

    private void WaitForDmaIdle()
    {
        // a fill or copy in progress holds the ports; a bus DMA holds the 68000 itself, so it never gets here
        while (dma == DmaKind.Fill && !fillArmed || dma == DmaKind.Copy) AdvanceWork();
    }

    private void StartDma()
    {
        int mode = Reg[23] >> 6 & 3;
        dmaLength = Reg[19] | Reg[20] << 8;
        if (dmaLength == 0) dmaLength = 0x10000;
        dmaTargetCode = code & 0xF;
        dmaSlotsLeft = 0;
        dmaStartTime = dmaNextFetch = Time + DmaStartDelay;
        if ((mode & 2) == 0)
        {
            dma = DmaKind.Bus;
            dmaSource = (uint)((Reg[23] & 0x7F) << 17 | Reg[22] << 9 | Reg[21] << 1);
            Trace?.Invoke($"dma bus src={dmaSource:X6} len={dmaLength} -> code {dmaTargetCode:X} addr {address:X4}");
        }
        else if (mode == 2)
        {
            fillSourceStart = Reg[22] << 8 | Reg[21]; fillLengthStart = dmaLength;
            fillArmed = true; dma = DmaKind.None;
            Trace?.Invoke($"dma fill armed len={dmaLength} code {dmaTargetCode:X} addr {address:X4}");
        }
        else
        {
            dma = DmaKind.Copy;
            dmaSource = (uint)(Reg[22] << 8 | Reg[21]);
            Trace?.Invoke($"dma copy src={dmaSource:X4} len={dmaLength} -> addr {address:X4}");
        }
    }

    // =================================================================================================================
    //  H and V counters
    // =================================================================================================================

    /// <summary>The 9-bit horizontal counter at a line offset: the active part counts at the pixel clock, the HSYNC stretch at the end covers the rest of the counter's range (H40: $000-$16C then $1C9-$1FF; H32: $000-$127 then $1D2-$1FF).</summary>
    public int HCounterAt(int offset) => HCounter9(H40, offset);

    /// <summary>The H counter's 9-bit value at a line offset (the function behind <see cref="HCounterAt"/>, usable without a VDP: the slot tables are built from it).</summary>
    public static int HCounter9(bool h40, int offset)
    {
        if (h40)
        {
            const int mainEnd = 365 * 8;
            if (offset < mainEnd) return offset / 8;
            return 0x1C9 + (offset - mainEnd) * 55 / (ClocksPerLine - mainEnd);
        }
        const int mainEnd32 = 296 * 10;
        if (offset < mainEnd32) return offset / 10;
        return 0x1D2 + (offset - mainEnd32) * 46 / (ClocksPerLine - mainEnd32);
    }

    /// <summary>The 8-bit V counter value for <paramref name="line"/> (the sequences of the hardware: NTSC V28 $00-$EA then $E5-$FF, ...).</summary>
    public int VCounterFor(int line)
    {
        if (Region == SegaRegion.Ntsc) return V30 ? (line & 0xFF) : line <= 0xEA ? line : line - 6;
        // PAL: 313 lines
        if (V30) return line <= 0x10A ? (line & 0xFF) : 0xD2 + (line - 0x10B);
        return line <= 0x102 ? (line & 0xFF) : 0xCA + (line - 0x103);
    }

    /// <summary>The V and H counters as the 68000 reads them (word at $C00008): V in the high byte, the top 8 bits of H in the low one.</summary>
    public ushort ReadHvCounter(long t)
    {
        RunTo(t);
        return hvLatched >= 0 ? (ushort)hvLatched : HvNow();
    }

    /// <summary>The HV counter value now. With the latch bit (R0 bit 1) set the counter stops where it was when the bit was set; a read then keeps returning that value until the bit is cleared.</summary>
    private ushort HvNow()
    {
        int off = LineOffset;
        int line = off >= VCounterStepOffset ? (Line + 1 >= linesPerFrame ? 0 : Line + 1) : Line;
        int v = VCounterFor(line);
        if (Interlace == 3) v = v << 1 & 0xFE | v >> 7 & 1;
        int h = HCounterAt(off) >> 1 & 0xFF;
        return (ushort)(v << 8 | h);
    }

    // =================================================================================================================
    //  Debug
    // =================================================================================================================

    /// <summary>The debug picture: VRAM drawn as 4 bits-per-pixel tiles, 64 tiles to a row and 8 pixels a tile, in palette line <paramref name="palette"/>, then the 64 CRAM colours as swatches under it. Width 512, height 256 + 16.</summary>
    public void RenderDebugView(uint[] dst, int palette)
    {
        const int W = 512;
        uint[] colours = new uint[64];
        for (int i = 0; i < 64; i++)
        {
            int c = Cram[i];
            int r = (c >> 1 & 7) * 36, g = (c >> 5 & 7) * 36, b = (c >> 9 & 7) * 36;
            colours[i] = 0xFF000000u | (uint)(r << 16 | g << 8 | b);
        }
        for (int tile = 0; tile < 2048; tile++)
        {
            int tx = tile % 64 * 8, ty = tile / 64 * 8;
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 4; col++)
                {
                    byte b = Vram[tile * 32 + row * 4 + col];
                    int p0 = b >> 4, p1 = b & 15;
                    int o = (ty + row) * W + tx + col * 2;
                    dst[o] = p0 == 0 ? 0xFF000000u : colours[(palette & 3) * 16 + p0];
                    dst[o + 1] = p1 == 0 ? 0xFF000000u : colours[(palette & 3) * 16 + p1];
                }
        }
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < W; x++)
                dst[(256 + y) * W + x] = colours[x / 8 & 63];
    }

    /// <summary>
    /// A debugging picture of the two scroll planes as the nametables and VRAM hold them: plane B, then plane A over it (colour 0 transparent), the first 40 by 28 cells, <b>no scrolling, no window, no
    /// sprites, no priority, no shadow or highlight</b>. It is not the picture renderer (the next milestone); it lets test ROMs that print text on a plane be read. Width 320, height 224, row-major ARGB.
    /// </summary>
    public void RenderPlaneDump(uint[] dst)
    {
        const int W = 320, H = 224;
        uint[] colours = new uint[64];
        for (int i = 0; i < 64; i++)
        {
            int c = Cram[i];
            colours[i] = 0xFF000000u | (uint)(((c >> 1 & 7) * 36) << 16 | ((c >> 5 & 7) * 36) << 8 | (c >> 9 & 7) * 36);
        }
        uint backdrop = colours[Reg[7] & 0x3F];
        for (int i = 0; i < W * H; i++) dst[i] = backdrop;
        int widthCells = (Reg[16] & 3) switch { 0 => 32, 1 => 64, 3 => 128, _ => 32 };
        int heightCells = (Reg[16] >> 4 & 3) switch { 0 => 32, 1 => 64, 3 => 128, _ => 32 };
        int baseA = (Reg[2] & 0x38) << 10, baseB = (Reg[4] & 7) << 13;
        for (int plane = 0; plane < 2; plane++)
        {
            int nt = plane == 0 ? baseB : baseA;
            for (int cy = 0; cy < 28; cy++)
                for (int cx = 0; cx < 40; cx++)
                {
                    if (cx >= widthCells || cy >= heightCells) continue;
                    int e = Vram[nt + (cy * widthCells + cx) * 2 & 0xFFFF] << 8 | Vram[nt + (cy * widthCells + cx) * 2 + 1 & 0xFFFF];
                    int tile = e & 0x7FF, pal = e >> 13 & 3;
                    bool vflip = (e & 0x1000) != 0, hflip = (e & 0x800) != 0;
                    for (int row = 0; row < 8; row++)
                    {
                        int r = vflip ? 7 - row : row;
                        for (int col = 0; col < 8; col++)
                        {
                            int c2 = hflip ? 7 - col : col;
                            byte b = Vram[tile * 32 + r * 4 + (c2 >> 1) & 0xFFFF];
                            int px = (c2 & 1) == 0 ? b >> 4 : b & 15;
                            if (px == 0) continue;
                            dst[(cy * 8 + row) * W + cx * 8 + col] = colours[pal * 16 + px];
                        }
                    }
                }
        }
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"VDP line {Line} (+{LineOffset}) frame {Frames} | {(H40 ? "H40" : "H32")} {(V30 ? "V30" : "V28")} display {(DisplayEnabled ? "on" : "off")} dma {(DmaEnabled ? "on" : "off")} vint {(VintEnabled ? "on" : "off")} hint {(HintEnabled ? "on" : "off")} | ");
        sb.Append($"code {code:X2} addr {address:X4} fifo {fifoCount} dma {dma}{(fillArmed ? "(armed)" : "")} | regs");
        for (int i = 0; i < 24; i++) sb.Append($" {Reg[i]:X2}");
        return sb.ToString();
    }

    public void SaveState(BinaryWriter w)
    {
        w.Write(Vram); foreach (ushort c in Cram) w.Write(c); foreach (ushort v in Vsram) w.Write(v); w.Write(Reg);
        w.Write(Time); w.Write(lineStart); w.Write(Line); w.Write(Frames); w.Write(oddFrame);
        w.Write(vintPending); w.Write(hintPending); w.Write(hintCounter); w.Write(vintTime); w.Write(hintTime); w.Write(z80IntClear); w.Write(z80IntAsserted);
        w.Write(pendingCommand); w.Write(commandFirst); w.Write(code); w.Write(address); w.Write(readBuffer); w.Write(readReadyAt); w.Write(readCacheCode); w.Write(readCacheAddress); w.Write(readStale); w.Write(hvLatched);
        w.Write(fifoHead); w.Write(fifoCount);
        foreach (var e in fifo) { w.Write(e.Code); w.Write(e.Address); w.Write(e.Data); w.Write(e.SlotsLeft); w.Write(e.FromDma); }
        w.Write(dmaInFifo);
        w.Write((int)dma); w.Write(fillArmed); w.Write(dmaSource); w.Write(dmaLength); w.Write(dmaSlotsLeft); w.Write(fillData); w.Write(dmaTargetCode); w.Write(fillSourceStart); w.Write(fillLengthStart);
        w.Write(dmaStartTime); w.Write(dmaNextFetch);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(Vram, 0, Vram.Length); for (int i = 0; i < Cram.Length; i++) Cram[i] = r.ReadUInt16(); for (int i = 0; i < Vsram.Length; i++) Vsram[i] = r.ReadUInt16(); r.Read(Reg, 0, Reg.Length);
        Time = r.ReadInt64(); lineStart = r.ReadInt64(); Line = r.ReadInt32(); Frames = r.ReadInt64(); oddFrame = r.ReadBoolean();
        vintPending = r.ReadBoolean(); hintPending = r.ReadBoolean(); hintCounter = r.ReadInt32(); vintTime = r.ReadInt64(); hintTime = r.ReadInt64(); z80IntClear = r.ReadInt64(); z80IntAsserted = r.ReadBoolean();
        pendingCommand = r.ReadBoolean(); commandFirst = r.ReadUInt16(); code = r.ReadInt32(); address = r.ReadUInt32(); readBuffer = r.ReadUInt16(); readReadyAt = r.ReadInt64(); readCacheCode = r.ReadInt32(); readCacheAddress = r.ReadUInt32(); readStale = r.ReadBoolean(); hvLatched = r.ReadInt32();
        fifoHead = r.ReadInt32(); fifoCount = r.ReadInt32();
        for (int i = 0; i < fifo.Length; i++) fifo[i] = new FifoEntry { Code = r.ReadInt32(), Address = r.ReadUInt32(), Data = r.ReadUInt16(), SlotsLeft = r.ReadInt32(), FromDma = r.ReadBoolean() };
        dmaInFifo = r.ReadInt32();
        dma = (DmaKind)r.ReadInt32(); fillArmed = r.ReadBoolean(); dmaSource = r.ReadUInt32(); dmaLength = r.ReadInt32(); dmaSlotsLeft = r.ReadInt32(); fillData = r.ReadUInt16(); dmaTargetCode = r.ReadInt32(); fillSourceStart = r.ReadInt32(); fillLengthStart = r.ReadInt32();
        dmaStartTime = r.ReadInt64(); dmaNextFetch = r.ReadInt64();
        // the picture is not part of a state: the colours are rebuilt and the line in progress is drawn from where the state left it
        for (int i = 0; i < 64; i++) UpdatePal(i);
        BeginLineRender();
    }
}
