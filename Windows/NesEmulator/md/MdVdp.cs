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
/// The Genesis video chip (YM7101 / 315-5313) as far as this milestone takes it: VRAM, CRAM, VSRAM and the registers, the command and data port protocol, the write FIFO, DMA (68000 to VDP,
/// fill, copy), the status register, the H and V counters and the vertical and horizontal interrupts. It draws nothing yet: the picture renderer is the next milestone, so the sprite overflow and
/// collision flags are never set and <see cref="RenderDebugView"/> shows the memories, not the screen.
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

    /// <summary>VINT comes this long after the vertical blank begins (the plan's 788 master clocks in H40, 770 in H32).</summary>
    public int VintDelay => H40 ? 788 : 770;
    /// <summary>The horizontal interrupt and the line's counter step fall here in the line (the start of horizontal blank).</summary>
    public const int HIntOffset = 2816;
    /// <summary>Horizontal blank covers the line from here to its end and from its start to <see cref="HBlankEnd"/>.</summary>
    public const int HBlankStart = 2816, HBlankEnd = 220;
    /// <summary>The V counter steps this far into a line (hcount $A5 in H40, in the horizontal blank).</summary>
    public const int VCounterStepOffset = 2640;

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
    private struct FifoEntry { public int Code; public uint Address; public ushort Data; public int SlotsLeft; }
    private readonly FifoEntry[] fifo = new FifoEntry[4];
    private int fifoHead, fifoCount;
    private ushort lastFifoData;       // the last word the 68000 or a DMA put in the FIFO (a data port read while a write target is set returns it)

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
        dma = DmaKind.None; fillArmed = false; dmaLength = 0; dmaSlotsLeft = 0;
        DataWrites = ControlWrites = FifoStalls = DmaWords = 0; FifoStallClocks = 0;
        // the post-boot state the console's own code leaves is the game's business; the chip itself powers on with everything zero
        hintTime = lineStart + HIntOffset; slotCursor = 0;
    }

    // =================================================================================================================
    //  Access slots
    // =================================================================================================================

    /// <summary>
    /// The slots of one line that are free for the CPU, the FIFO and DMA, as offsets in master clocks from the line start: <c>[h40][active]</c>. Every line has <c>Total</c> slots of equal length; the refresh
    /// slots are never free and the fetch slots of an active line are not either. The numbers of free slots per line are Sega's documented ones (H40 18 active, 205 blanked; H32 16 and 167).
    /// </summary>
    public static class SlotTable
    {
        public static readonly int[][][] Free = Build();

        private static int[][][] Build()
        {
            var t = new int[2][][];
            for (int w = 0; w < 2; w++)
            {
                bool h40 = w == 1;
                int total = h40 ? 210 : 171, refresh = h40 ? 5 : 4;
                int freeBlank = total - refresh, freeActive = h40 ? 18 : 16;
                t[w] = new int[2][];
                // blanked: every slot but the refresh ones (spread evenly)
                var refreshSlots = new bool[total];
                for (int r = 0; r < refresh; r++) refreshSlots[(int)((r + 0.5) * total / refresh)] = true;
                var blank = new System.Collections.Generic.List<int>();
                for (int s = 0; s < total; s++) if (!refreshSlots[s]) blank.Add(s * ClocksPerLine / total);
                t[w][0] = blank.ToArray();
                // active: the few that the pattern, sprite and refresh fetches leave
                var active = new int[freeActive];
                for (int j = 0; j < freeActive; j++) active[j] = (int)((j + 0.5) * ClocksPerLine / freeActive);
                t[w][1] = active;
                if (blank.Count != freeBlank) throw new InvalidOperationException("slot table: blanked free-slot count");
            }
            return t;
        }
    }

    private int[] FreeSlotsNow(int line)
    {
        bool active = DisplayEnabled && line < ActiveLines;
        return SlotTable.Free[H40 ? 1 : 0][active ? 1 : 0];
    }

    /// <summary>The master clock of the first free slot at or after <paramref name="from"/> (within the current line, else the next).</summary>
    private long NextFreeSlot(long from)
    {
        long off = from - lineStart;
        var table = FreeSlotsNow(Line);
        int lo = 0, hi = table.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (table[mid] < off) lo = mid + 1; else hi = mid; }
        if (lo < table.Length) return lineStart + table[lo];
        int nextLine = Line + 1 >= linesPerFrame ? 0 : Line + 1;
        return lineStart + ClocksPerLine + FreeSlotsNow(nextLine)[0];
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
            bool work = fifoCount > 0 || dma == DmaKind.Bus || dma == DmaKind.Copy || (dma == DmaKind.Fill && !fillArmed);
            if (work) slot = NextFreeSlot(Math.Max(Time, slotCursor));
            long next = t;
            if (evt < next) next = evt;
            if (slot < next) next = slot;
            if (next < Time) next = Time;
            Time = next;
            if (work && slot <= next) { ConsumeSlot(); continue; }
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
        // lines 0 to the one after the last active line step the counter; the others reload it
        if (Line <= ActiveLines)
        {
            if (hintCounter == 0) { hintPending = true; hintCounter = Reg[10]; }
            else hintCounter--;
        }
        else hintCounter = Reg[10];
    }

    private void FireVint()
    {
        vintPending = true;
        if (!z80IntAsserted) { z80IntAsserted = true; host.SetZ80Interrupt(true, Time); }
        z80IntClear = Time + ClocksPerLine;
    }

    private void StartNextLine()
    {
        lineStart += ClocksPerLine;
        Line++;
        if (Line >= linesPerFrame)
        {
            Line = 0; Frames++; FrameCompleted = true;
            oddFrame = Interlace != 0 && !oddFrame;
        }
        hintTime = lineStart + HIntOffset;
        if (Line == ActiveLines) vintTime = lineStart + VintDelay;     // the vertical blank begins; VINT follows after its delay
    }

    /// <summary>Master clock of the next thing that changes the VDP's visible state: a line boundary, an interrupt or flag, or a free slot with work waiting.</summary>
    public long NextWorkTime()
    {
        long next = NextEvent();
        bool work = fifoCount > 0 || dma == DmaKind.Bus || dma == DmaKind.Copy || (dma == DmaKind.Fill && !fillArmed);
        if (work) { long s = NextFreeSlot(Math.Max(Time, slotCursor)); if (s < next) next = s; }
        return next;
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
        if (level == 6) vintPending = false;
        else if (level == 4) hintPending = false;
    }

    // =================================================================================================================
    //  Work done in a slot
    // =================================================================================================================

    private void ConsumeSlot()
    {
        slotCursor = Time + 1;
        // a bus transfer moves its words through the write FIFO, as every write does: it fetches while there is room
        if (dma == DmaKind.Bus) FeedBusDma();
        if (fifoCount > 0)
        {
            ref var e = ref fifo[fifoHead];
            if (--e.SlotsLeft <= 0)
            {
                WriteTarget(e.Code, e.Address, e.Data);
                fifoHead = fifoHead + 1 & 3; fifoCount--;
            }
            return;
        }
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
    private void PushFifo(int c, uint addr, ushort data)
    {
        int idx = fifoHead + fifoCount & 3;
        fifo[idx] = new FifoEntry { Code = c, Address = addr, Data = data, SlotsLeft = SlotsFor(c) };
        fifoCount++;
        lastFifoData = data;
    }

    /// <summary>A bus transfer's next words: read from the 68000's space into the FIFO while it has room (the 68000 is held, so the reads cost it nothing), until the length runs out.</summary>
    private void FeedBusDma()
    {
        while (dmaLength > 0 && fifoCount < 4)
        {
            ushort w = host.ReadBusWord(dmaSource);
            PushFifo(dmaTargetCode, address, w);
            DmaWords++;
            address = (address + (uint)AutoIncrement) & 0x1FFFF;
            // the source wraps inside its 128 KB window
            dmaSource = (dmaSource & 0xFE0000) | ((dmaSource + 2) & 0x1FFFF);
            if (--dmaLength <= 0) { FinishDma(); break; }
        }
    }

    /// <summary>Write a word to VRAM, CRAM or VSRAM at <paramref name="addr"/> (the address register as the command left it).</summary>
    private void WriteTarget(int c, uint addr, ushort data)
    {
        switch (c & 0xF)
        {
            case 1:      // VRAM: a word at an odd address lands byte-swapped
            {
                int a = (int)(addr & 0xFFFF);
                if ((a & 1) != 0) data = (ushort)(data << 8 | data >> 8);
                a &= ~1;
                Vram[a] = (byte)(data >> 8); Vram[a + 1] = (byte)data;
                break;
            }
            case 3: Cram[(addr >> 1) & 0x3F] = (ushort)(data & 0x0EEE); break;
            case 5:
            {
                // VSRAM holds 40 words: a write to one of the other addresses of its 64-word range is lost, and a read there returns entry 0 (BlastEm and Nemesis' fill-to-VSRAM tests: entries 40-63 read as entry 0)
                int i = (int)(addr >> 1) & 0x3F;
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
        Reg[reg] = value;
        if (reg == 0) hvLatched = (value & 2) == 0 ? -1 : hvLatched >= 0 ? hvLatched : HvNow();
        if (reg == 1 && VintEnabled && !vintBefore) vintEnableFrom = Time + IplDelayMasterClocks;
        if (reg == 0 && HintEnabled && !hintBefore) hintEnableFrom = Time + IplDelayMasterClocks;
        Trace?.Invoke($"reg {reg:X2}={value:X2}");
        if (reg == 10 && Line > ActiveLines) hintCounter = value;
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
        if (oddFrame) s |= 0x10;
        if (InVBlank || !DisplayEnabled) s |= 0x08;
        if (InHBlank) s |= 0x04;
        if (DmaBusy) s |= 0x02;
        if (Region == SegaRegion.Pal) s |= 0x01;
        return (ushort)s;
    }

    /// <summary>The status register is ten bits wide; the top six of the word the 68000 reads are the bus's (on hardware whatever the prefetch left there), which the board fills in.</summary>
    public const int StatusMask = 0x03FF;

    public bool InVBlank => Line >= ActiveLines;
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
    public int HCounterAt(int offset)
    {
        if (H40)
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
        sb.Append(" | no picture renderer yet: sprite flags are never set");
        return sb.ToString();
    }

    public void SaveState(BinaryWriter w)
    {
        w.Write(Vram); foreach (ushort c in Cram) w.Write(c); foreach (ushort v in Vsram) w.Write(v); w.Write(Reg);
        w.Write(Time); w.Write(lineStart); w.Write(Line); w.Write(Frames); w.Write(oddFrame);
        w.Write(vintPending); w.Write(hintPending); w.Write(hintCounter); w.Write(vintTime); w.Write(hintTime); w.Write(z80IntClear); w.Write(z80IntAsserted);
        w.Write(pendingCommand); w.Write(commandFirst); w.Write(code); w.Write(address); w.Write(readBuffer); w.Write(readReadyAt); w.Write(readCacheCode); w.Write(readCacheAddress); w.Write(readStale); w.Write(hvLatched);
        w.Write(fifoHead); w.Write(fifoCount);
        foreach (var e in fifo) { w.Write(e.Code); w.Write(e.Address); w.Write(e.Data); w.Write(e.SlotsLeft); }
        w.Write((int)dma); w.Write(fillArmed); w.Write(dmaSource); w.Write(dmaLength); w.Write(dmaSlotsLeft); w.Write(fillData); w.Write(dmaTargetCode); w.Write(fillSourceStart); w.Write(fillLengthStart);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(Vram, 0, Vram.Length); for (int i = 0; i < Cram.Length; i++) Cram[i] = r.ReadUInt16(); for (int i = 0; i < Vsram.Length; i++) Vsram[i] = r.ReadUInt16(); r.Read(Reg, 0, Reg.Length);
        Time = r.ReadInt64(); lineStart = r.ReadInt64(); Line = r.ReadInt32(); Frames = r.ReadInt64(); oddFrame = r.ReadBoolean();
        vintPending = r.ReadBoolean(); hintPending = r.ReadBoolean(); hintCounter = r.ReadInt32(); vintTime = r.ReadInt64(); hintTime = r.ReadInt64(); z80IntClear = r.ReadInt64(); z80IntAsserted = r.ReadBoolean();
        pendingCommand = r.ReadBoolean(); commandFirst = r.ReadUInt16(); code = r.ReadInt32(); address = r.ReadUInt32(); readBuffer = r.ReadUInt16(); readReadyAt = r.ReadInt64(); readCacheCode = r.ReadInt32(); readCacheAddress = r.ReadUInt32(); readStale = r.ReadBoolean(); hvLatched = r.ReadInt32();
        fifoHead = r.ReadInt32(); fifoCount = r.ReadInt32();
        for (int i = 0; i < fifo.Length; i++) fifo[i] = new FifoEntry { Code = r.ReadInt32(), Address = r.ReadUInt32(), Data = r.ReadUInt16(), SlotsLeft = r.ReadInt32() };
        dma = (DmaKind)r.ReadInt32(); fillArmed = r.ReadBoolean(); dmaSource = r.ReadUInt32(); dmaLength = r.ReadInt32(); dmaSlotsLeft = r.ReadInt32(); fillData = r.ReadUInt16(); dmaTargetCode = r.ReadInt32(); fillSourceStart = r.ReadInt32(); fillLengthStart = r.ReadInt32();
    }
}
