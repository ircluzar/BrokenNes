using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>Which video chip: the 315-5124 of the Mark III and early Master Systems, the 315-5246 of the Master System II, the 315-5378 of the Game Gear, or the TMS9918A of the SG-1000 and SC-3000.</summary>
public enum SmsVdpRevision { Sms1, Sms2, GameGear, Tms9918 }

/// <summary>
/// The Master System / Game Gear / SG-1000 video chip: 16 KB of VRAM, colour RAM, registers 0-10, the control-port protocol, the line and frame interrupts, the V and H counters, Mode 4 (the
/// Master System's own mode: scrolling background and 64 sprites) and the four TMS9918 modes. It runs a scanline at a time but renders <i>lazily</i>: every port access first catches the
/// current line's picture up to the master-clock time of the access, so a register or palette write in the middle of a line changes exactly the pixels after it.
/// </summary>
/// <remarks>
/// <para>Clean-room, from the public documentation: Charles MacDonald's "Sega Master System VDP documentation" (2002), the SMS Power wiki (VDP registers, VDP status, Interrupt mechanism, Port 3E),
/// the Game Gear Hardware Reference Manual, and FluBBa's SMS VDP Test (its readme states where on the line each event falls). BizHawk's VDP and every GPL emulator's were not read.</para>
/// <para><b>Time.</b> A line is 342 pixels = 684 master clocks = 228 Z80 cycles. The board tells the VDP the master-clock position inside the line (<c>mc</c>, 0..684) at every access and runs
/// <see cref="RunTo"/> to each event time it announces in <see cref="NextEventMc"/>. Position 0 is the pixel whose 9-bit counter reads $1E6 (H counter $F3); the V counter changes at counter
/// $1E8 (H $F4); the frame-interrupt flag is set at counter $1EA (H $F5). The timing constants below are the fixed positions the VDP test measured on hardware, found by running that test.</para>
/// <para><b>Not modelled:</b> VRAM access slots (CPU writes are never delayed), the zoomed-sprite bug of the 315-5124, the 315-5124's address masks on registers 3, 4 and 6 (those on 2 and 5 are),
/// light-pen and phaser TH latching beyond the TH rising edge, and the Game Gear's SMS-compatibility palette mode.</para>
/// </remarks>
public partial class SmsVdp
{
    // ---- timing constants (master clocks within a line, origin at counter $1E6) -------------------------------------------------------------------------
    // Found by running FluBBa's VDP test (Workshop --smstest --scan) against the board's access timing (BOARD_SMS.IoPhaseMc = 0): each test passes over a window of a few master clocks, one Z80
    // cycle wide at most (a port access is sampled at Z80 cycle granularity: 3 master clocks); the value used is inside the window of its test and inside the readme's ordering (the line
    // interrupt one pixel, two master clocks, after the frame interrupt). VINT flag: tests 32 and 35 pass for 4-6; line IRQ: test 34 for 7-9; collision (pixel x = $83 flags at H $47): test 52 for
    // about 1-3 past the pixel's own time; overflow: test 60 for 4-6.
    public const int LineMc = 684;                 // 342 pixels x 2
    /// <summary>The V counter takes its new value (H counter $F4).</summary>
    public int VCounterMc = 4;
    /// <summary>The frame-interrupt flag is set, and the interrupt line with it (H counter $F5).</summary>
    public int VintFlagMc = 5;
    /// <summary>The line counter underflows and raises the line interrupt.</summary>
    public int HintMc = 8;
    /// <summary>The horizontal scroll value is latched (H counter $F3).</summary>
    public int ScrollLatchMc = 0;
    /// <summary>Master clock at which pixel 0 of the picture is drawn; pixel x follows at two master clocks per pixel.</summary>
    public int PixelOriginMc = 52 + 2 * 11;
    /// <summary>1 = no sprite collisions in the border lines above and below the picture, as the oracle (Mesen 2.1.1) behaves (FluBBa's VDP test #46 says the hardware does set the flag there). A tool setting for comparing
    /// games with the oracle (<c>--tune OracleNoBorderCollision=1</c>), never on in the app.</summary>
    public int OracleNoBorderCollision;
    /// <summary>1 = the line counter powers up at 0 instead of 255, as the oracle (Mesen 2.1.1) has it: it runs no BIOS, so the counter underflows at the first picture line of its first frame and a line interrupt is
    /// pending from then on until the game reads the status port (a game that enables the line interrupt before its first status read takes that interrupt at its first <c>EI</c>: Shining Force II on the Game Gear does, one
    /// interrupt the real machine does not take, because its BIOS has reloaded the counter every frame before the cartridge starts). A tool setting for comparing games with the oracle
    /// (<c>--tune OracleLineCounterAtPowerOn=1</c>), never on in the app.</summary>
    public int OracleLineCounterAtPowerOn;

    /// <summary>Master clocks the collision flag trails the pixel that caused it.</summary>
    public int CollisionDelayMc = 2;
    /// <summary>The sprite-overflow flag is set this far into the line it is detected on.</summary>
    public int OverflowFlagMc = 5;

    // ---- configuration -------------------------------------------------------------------------------------------------------------------------------
    public SmsVdpRevision Revision { get; }
    public bool Pal { get; }
    public int TotalLines { get; }
    private readonly bool gg;

    // ---- memory --------------------------------------------------------------------------------------------------------------------------------------
    public readonly byte[] Vram = new byte[0x4000];
    private readonly byte[] cram = new byte[64];      // SMS: 32 bytes of --BBGGRR; GG: 32 x 12-bit as little-endian pairs
    private readonly uint[] palArgb = new uint[32];   // CRAM as 0xAARRGGBB
    public readonly byte[] Regs = new byte[16];

    /// <summary>The picture: 256 x <see cref="FrameHeight"/> pixels, 0xAARRGGBB (a 256 x 240 buffer is allocated for the extended modes).</summary>
    public readonly uint[] Frame = new uint[256 * 240];
    public int FrameHeight { get; private set; } = 192;

    // ---- port state ----------------------------------------------------------------------------------------------------------------------------------
    private int addr;
    private int code;
    /// <summary>The address register and the code register (0-1 VRAM, 3 colour RAM): where the next data-port byte goes (the debugger's VRAM watches).</summary>
    public int DebugAddress => addr;
    public int DebugCode => code;
    private bool latch;
    private byte latchByte;
    private byte readBuffer;
    private byte ggCramLatch;

    // ---- status and interrupts -----------------------------------------------------------------------------------------------------------------------
    private bool vintFlag, ovrFlag, colFlag, lineIrqPending;
    private int fifthSpriteNumber = 0x1F;      // TMS9918 status bits 4-0
    public long Frames { get; private set; }

    /// <summary>The INT line the Z80 sees: a pending frame interrupt with register 1 bit 5, or a pending line interrupt with register 0 bit 4.</summary>
    public bool Irq => (vintFlag && (Regs[1] & 0x20) != 0) || (lineIrqPending && (Regs[0] & 0x10) != 0);

    // ---- line state ----------------------------------------------------------------------------------------------------------------------------------
    /// <summary>The frame line being run: 0 is the first picture line.</summary>
    public int Line { get; private set; }
    private int activeHeight = 192;
    private int vcounter;
    private int lineCounter;
    private int hscrollLatched;
    private int vscrollLatched;
    private int hCounterLatch;
    private int renderedX;
    private int curMc;
    private bool lineDrawn;
    private bool lineDisplayOn;       // whether the line being drawn shows the picture (register 1 bit 6 as the line began, or switched on since)

    // timed events of the current line
    private const int EvScroll = 0, EvVCounter = 1, EvVint = 2, EvHint = 3;
    private readonly int[] evTime = new int[4];
    private readonly int[] evKind = new int[4];
    private int evCount, evNext;
    private int colAtMc = int.MaxValue, ovrAtMc = int.MaxValue;

    /// <summary>The master-clock time of the next thing that changes state (an event or a flag coming due); the board calls <see cref="RunTo"/> when its clock reaches it.</summary>
    public int NextEventMc { get; private set; } = int.MaxValue;

    /// <summary>Called with (frame line, master clock in the line, register, value) for every register write: the tracing hook of the Workshop.</summary>
    public Action<int, int, int, byte>? RegisterWriteHook;
    /// <summary>A tool hook: a colour RAM write has just taken effect (line, the picture x the dot was drawn at or -1 when no dot was, the colour RAM byte index, the colour of the dot, the master clock into the line the chip is at).</summary>
    public Action<int, int, int, uint, int>? CramDotHook;
    /// <summary>Called with (code register, address, value, frame line, master clock in the line) for every write to the data port (VRAM or colour RAM).</summary>
    public Action<int, int, byte, int, int>? DataWriteHook;

    public SmsVdp(SmsVdpRevision revision, bool pal)
    {
        Revision = revision; Pal = pal; gg = revision == SmsVdpRevision.GameGear;
        TotalLines = pal ? 313 : 262;
        PowerOn();
    }

    /// <summary>True when the chip can draw Mode 4 and the extended heights: every revision but the TMS9918.</summary>
    private bool HasMode4 => Revision != SmsVdpRevision.Tms9918;

    /// <summary>What colour RAM holds when the Master System BIOS hands over to a cartridge (the logo's palette); the Game Gear BIOS leaves it zero. Hardware powers up with
    /// undefined contents; these are the values the oracle shows, so the first frames of a game that turns the display on before loading its palette compare.</summary>
    private static readonly byte[] SmsPostBiosCram =
    {
        0x00, 0x3F, 0x3E, 0x3F, 0x30, 0x30, 0x38, 0x3F, 0x37, 0x3F, 0, 0, 0, 0, 0, 0,
        0x00, 0x03, 0x30, 0x0F, 0x07, 0x16, 0x3F, 0x02, 0x00, 0x0F, 0, 0, 0, 0, 0, 0,
    };

    /// <summary>
    /// The state the BIOS leaves behind (the machine here never runs one). Master System: registers $36 $60 $FF $00 $00 $FF $FB $00 $00 $00 $FF, the logo palette in colour RAM,
    /// the address register at $3D6A in VRAM-write mode. Game Gear: $06 $80 $FF $FF $FF $FF $FB $00 $00 $00 $FF, colour RAM zero. These are what the oracle shows at the first
    /// instruction of a cartridge (probed with a one-instruction ROM); the BIOS-less state published for other emulators differs in register 1 (display off) only.
    /// </summary>
    /// <param name="postBios">False for a machine that runs a BIOS image: the chip then powers up cold (every register and colour entry zero, the address at 0) and the BIOS sets it up itself.</param>
    public void PowerOn(bool postBios = true)
    {
        Array.Clear(Vram); Array.Clear(cram); Array.Clear(Regs);
        addr = 0; code = 0;
        if (!postBios) { }
        else if (gg)
        {
            byte[] r = { 0x06, 0x80, 0xFF, 0xFF, 0xFF, 0xFF, 0xFB, 0x00, 0x00, 0x00, 0xFF };
            Array.Copy(r, Regs, r.Length);
        }
        else if (Revision == SmsVdpRevision.Tms9918)
        {
            Regs[1] = 0x80;
        }
        else
        {
            byte[] r = { 0x36, 0x60, 0xFF, 0x00, 0x00, 0xFF, 0xFB, 0x00, 0x00, 0x00, 0xFF };
            Array.Copy(r, Regs, r.Length);
            Array.Copy(SmsPostBiosCram, cram, 32);
            addr = 0x3D6A; code = 1;
        }
        latch = false; readBuffer = 0; ggCramLatch = 0;
        vintFlag = ovrFlag = colFlag = lineIrqPending = false; fifthSpriteNumber = 0x1F; fifthSeen = false;
        for (int i = 0; i < 32; i++) palArgb[i] = ConvertColor(gg ? cram[i * 2] | cram[i * 2 + 1] << 8 : cram[i]);
        lineCounter = OracleLineCounterAtPowerOn != 0 ? 0 : 0xFF; hscrollLatched = 0; vscrollLatched = 0; hCounterLatch = 0;
        Line = 0; vcounter = 0; Frames = 0; renderedX = 256; curMc = 0;
        evCount = evNext = 0; colAtMc = ovrAtMc = int.MaxValue; NextEventMc = int.MaxValue;
        pendHead = pendCount = 0; lineBase = 0; lastWriteTime = 0;
        Array.Clear(Frame);
        FrameHeight = 192; activeHeight = 192;
    }

    // =================================================================================== heights, counters

    /// <summary>The number of picture lines: 192, or 224 / 240 in the extended Mode 4 (315-5246 and Game Gear only).</summary>
    private int ComputeHeight()
    {
        if (Revision is SmsVdpRevision.Sms2 or SmsVdpRevision.GameGear && (Regs[0] & 0x04) != 0 && (Regs[0] & 0x02) != 0)
        {
            bool m1 = (Regs[1] & 0x10) != 0, m3 = (Regs[1] & 0x08) != 0;
            if (m1 && !m3) return 224;
            if (m3 && !m1) return 240;
        }
        return 192;
    }

    /// <summary>The V counter value (what port $7E reads) for frame line <paramref name="n"/>: the counter skips ahead on the way to the blanking lines, in a pattern per height and region.</summary>
    public int VCounterFor(int n, int height)
    {
        if (!Pal)
        {
            return height switch
            {
                192 => n <= 0xDA ? n : n - 6,
                224 => n <= 0xEA ? n : n - 6,
                _ => n <= 0xFF ? n : n - 256,
            };
        }
        return height switch
        {
            192 => n <= 0xF2 ? n : n - 57,
            224 => n <= 0xFF ? n : n <= 258 ? n - 256 : 0xCA + (n - 259),
            _ => n <= 0xFF ? n : n <= 266 ? n - 256 : 0xD2 + (n - 267),
        };
    }

    /// <summary>The H counter byte at master-clock position <paramref name="mc"/> in the line: the 9-bit pixel counter runs $1E6-$1FF, $000-$127, $1D2-$1E5 across the 342 pixels and the
    /// port reads its top eight bits.</summary>
    public static int HCounterAt(int mc)
    {
        int q = mc >> 1;
        if (q < 0) q = 0; else if (q > 341) q = 341;
        int counter = q < 2 ? 486 + q : q < 26 ? 488 + (q - 2) : q < 322 ? q - 26 : 466 + (q - 322);
        return counter >> 1;
    }

    // =================================================================================== line control (the board drives these)

    /// <summary>Starts frame line <paramref name="n"/> (0 = the first picture line). The line's events are queued; the board runs <see cref="RunTo"/> as time passes.</summary>
    public void BeginLine(int n)
    {
        lineBase += LineMc;
        Line = n;
        if (n == 0) { FrameHeightLatch(); }
        activeHeight = ComputeHeight();
        evCount = 0; evNext = 0;
        Queue(ScrollLatchMc, EvScroll);
        Queue(VCounterMc, EvVCounter);
        Queue(HintMc, EvHint);
        Queue(VintFlagMc, EvVint);
        curMc = 0; renderedX = 0; lineDrawn = false;
        colAtMc = ovrAtMc = int.MaxValue;
        lineDisplayOn = (Regs[1] & 0x40) != 0;
        Refresh();
    }

    private void Queue(int time, int kind)
    {
        int i = evCount++;
        while (i > 0 && evTime[i - 1] > time) { evTime[i] = evTime[i - 1]; evKind[i] = evKind[i - 1]; i--; }
        evTime[i] = time; evKind[i] = kind;
    }

    private void FrameHeightLatch()
    {
        vscrollLatched = Regs[9];
    }

    /// <summary>Finishes the line: runs to the end and draws the rest of the picture.</summary>
    public void EndLine()
    {
        RunTo(LineMc);
        if (Line < activeHeight && renderedX < 256) RenderSpan(renderedX, 256);
        renderedX = 256;
        if (Line == activeHeight - 1) { FrameHeight = activeHeight; }
        if (Line == TotalLines - 1) Frames++;
    }

    /// <summary>Advances to master-clock position <paramref name="mc"/> in the current line: fires the events and lands the writes that have come due, in time order, drawing the pixels between them.</summary>
    public void RunTo(int mc)
    {
        if (mc > LineMc) mc = LineMc;
        if (mc < curMc) mc = curMc;
        while (true)
        {
            int t = int.MaxValue, which = 0;
            if (evNext < evCount) { t = evTime[evNext]; which = 1; }
            if (pendCount > 0) { long pt = pend[pendHead].Time - lineBase; if (pt < t) { t = (int)pt; which = 2; } }
            if (colAtMc < t) { t = colAtMc; which = 3; }
            if (ovrAtMc < t) { t = ovrAtMc; which = 4; }
            if (t > mc) break;
            if (t < curMc) t = curMc;
            RenderUpTo(t);
            curMc = t;
            switch (which)
            {
                case 1: Fire(evKind[evNext]); evNext++; break;
                case 2:
                    {
                        var op = pend[pendHead];
                        pendHead = (pendHead + 1) % pend.Length; pendCount--;
                        ApplyWrite(op.Kind, op.Index, op.Value);
                        break;
                    }
                case 3: colAtMc = int.MaxValue; colFlag = true; break;
                default: ovrAtMc = int.MaxValue; ovrFlag = true; break;
            }
        }
        RenderUpTo(mc);
        curMc = mc;
        Refresh();
    }

    /// <summary>Draws the picture pixels whose time is before master-clock position <paramref name="t"/>.</summary>
    private void RenderUpTo(int t)
    {
        if (Line >= activeHeight) return;
        int xEnd = (t - PixelOriginMc + 1) >> 1;
        if (xEnd > 256) xEnd = 256;
        if (xEnd > renderedX) { RenderSpan(renderedX, xEnd); renderedX = xEnd; }
    }

    private void Refresh()
    {
        int next = evNext < evCount ? evTime[evNext] : int.MaxValue;
        if (colAtMc < next) next = colAtMc;
        if (ovrAtMc < next) next = ovrAtMc;
        if (pendCount > 0) { long pt = pend[pendHead].Time - lineBase; if (pt < next) next = (int)pt; }
        NextEventMc = next;
    }

    private void Fire(int kind)
    {
        switch (kind)
        {
            case EvScroll:
                hscrollLatched = Regs[8];
                BeginLineSprites();
                break;
            case EvVCounter:
                vcounter = VCounterFor(Line, activeHeight);
                break;
            case EvHint:
                if (Line <= activeHeight)
                {
                    if (lineCounter == 0) { lineCounter = Regs[10]; lineIrqPending = true; }
                    else lineCounter--;
                }
                else lineCounter = Regs[10];
                break;
            case EvVint:
                // the frame interrupt belongs to the line after the first blanking line: V counter $C1 in the 192-line mode (the line counter's reload happens there too)
                if (Line == activeHeight + 1) vintFlag = true;
                break;
        }
    }

    // =================================================================================== ports

    /// <summary>A read of the V counter port ($7E).</summary>
    public byte ReadVCounter(int mc) { RunTo(mc); return (byte)vcounter; }

    /// <summary>A read of the H counter port ($7F): the value latched when TH last went high.</summary>
    public byte ReadHCounter() => (byte)hCounterLatch;

    /// <summary>The TH line went from low to high: the H counter is captured.</summary>
    public void LatchHCounter(int mc) { RunTo(mc); hCounterLatch = HCounterAt(mc); }

    /// <summary>A read of the data port ($BE).</summary>
    public byte ReadData(int mc)
    {
        RunTo(mc);
        latch = false;
        byte v = readBuffer;
        readBuffer = FetchVram(addr);
        addr = (addr + 1) & 0x3FFF;
        return v;
    }

    /// <summary>
    /// The byte a read of VRAM address <paramref name="a"/> fetches: the CPU's accesses go through one queue, so a write the program made before the read (still waiting for its access slot) has landed by the time the read's
    /// fetch happens. A program that copies VRAM to VRAM a byte at a time with the source one byte behind the destination (Game Gear Shinobi's decompressor: the oracle returns the byte just written) reads it back.
    /// </summary>
    private byte FetchVram(int a)
    {
        for (int i = pendCount - 1; i >= 0; i--)
        {
            ref var w = ref pend[(pendHead + i) % pend.Length];
            if (w.Kind == OpVram && w.Index == a) return w.Value;
        }
        return Vram[a];
    }

    // =================================================================================== the write pipeline
    // The chip does not act on a CPU write the instant it arrives. A register write, or a byte for VRAM or colour RAM, takes effect about 31 pixels later (WriteLatencyMc master clocks), and
    // a byte for memory while the picture is being drawn (display on, a picture line) waits for the next of eight access slots in the line, one every 32 pixels (SlotFirstMc, SlotPeriodMc).
    // With the display off the byte shows through on screen as a dot of the colour written at the moment it lands. Measured by sweeping the time of a write through a line against the oracle
    // (Workshop/Sega/mesen sweeps): the effect edge sits at the first slot at or after the write plus 31 pixels, slots at x = 27 + 32k; registers and display-off writes at write + 31 pixels.

    /// <summary>Master clocks from a CPU write to its effect (31 pixels).</summary>
    public int WriteLatencyMc = -1;
    /// <summary>The first VRAM / colour RAM access slot of a picture line, in master clocks into the line (pixel 27).</summary>
    public int SlotFirstMc = 128;
    /// <summary>Master clocks between slots (32 pixels).</summary>
    public int SlotPeriodMc = 64;
    /// <summary>Slots per picture line.</summary>
    public int SlotCount = 8;
    /// <summary>Switch the whole pipeline off (writes act at once): a tool setting for isolating a problem.</summary>
    public bool WritesImmediate;

    private const int OpVram = 0, OpCram = 1, OpRegister = 2;
    private struct PendingWrite { public long Time; public int Kind; public int Index; public byte Value; }
    private readonly PendingWrite[] pend = new PendingWrite[32];
    private int pendHead, pendCount;
    private long lineBase;          // the master-clock time of the start of the current line, from power-on
    private long lastWriteTime;

    /// <summary>The position the picture is drawn to when a write arrives at <paramref name="mc"/>: the write's own effect time when that is before the arrival (a negative latency), so pixels after it are not drawn yet.</summary>
    private int WriteTime(int mc) => WriteLatencyMc < 0 ? mc + WriteLatencyMc : mc;

    private void EnqueueWrite(int kind, int index, byte value, int mc)
    {
        if (WritesImmediate) { ApplyWrite(kind, index, value); return; }
        long t = lineBase + mc + WriteLatencyMc;
        if (kind != OpRegister && (Regs[1] & 0x40) != 0 && Line < activeHeight)
        {
            long rel = t - lineBase;
            if (rel <= SlotFirstMc) t = lineBase + SlotFirstMc;
            else
            {
                long k = (rel - SlotFirstMc + SlotPeriodMc - 1) / SlotPeriodMc;
                t = k < SlotCount ? lineBase + SlotFirstMc + k * SlotPeriodMc : lineBase + LineMc + SlotFirstMc;
            }
        }
        if (t < lastWriteTime) t = lastWriteTime;   // writes land in the order they were made
        lastWriteTime = t;
        if (pendCount == pend.Length) { ApplyWrite(pend[pendHead].Kind, pend[pendHead].Index, pend[pendHead].Value); pendHead = (pendHead + 1) % pend.Length; pendCount--; }
        pend[(pendHead + pendCount++) % pend.Length] = new PendingWrite { Time = t, Kind = kind, Index = index, Value = value };
        Refresh();
    }

    private void ApplyWrite(int kind, int index, byte value)
    {
        switch (kind)
        {
            case OpVram: Vram[index] = value; break;
            case OpCram:
                WriteCram(index, value);
                // With the display off the picture shows the backdrop, and a colour RAM write that lands while the line is being drawn shows through as a one-pixel dot of the colour written
                // (the oracle does this; on the Master System the value is the 6-bit colour of the byte, on the Game Gear the colour the pair completes)
                if (!lineDisplayOn && Line < activeHeight && renderedX < 256 && (!gg || (index & 1) != 0))
                {
                    CramDotHook?.Invoke(Line, renderedX, index, palArgb[gg ? (index >> 1) & 0x1F : index & 0x1F], curMc);
                    Frame[Line * 256 + renderedX++] = palArgb[gg ? (index >> 1) & 0x1F : index & 0x1F];
                }
                else CramDotHook?.Invoke(Line, -1, index, palArgb[gg ? (index >> 1) & 0x1F : index & 0x1F], curMc);   // no dot (a tool sees the write anyway)
                break;
            default:
                RegisterWriteHook?.Invoke(Line, curMc, index, value);
                Regs[index] = value;
                // turning the display off blanks the rest of the line at once; turning it on only shows from the next line (measured: a line in which the bit goes high stays blank)
                if (index == 1 && (value & 0x40) == 0) lineDisplayOn = false;
                break;
        }
    }

    /// <summary>A write to the data port ($BE).</summary>
    public void WriteData(byte value, int mc)
    {
        RunTo(WriteTime(mc));
        latch = false;
        DataWriteHook?.Invoke(code, addr, value, Line, curMc);
        EnqueueWrite(code == 3 ? OpCram : OpVram, addr, value, mc);
        readBuffer = value;
        addr = (addr + 1) & 0x3FFF;
    }

    /// <summary>A read of the status port ($BF): the flags, which it clears together with the latch and the interrupt lines.</summary>
    public byte ReadStatus(int mc)
    {
        RunTo(mc);
        latch = false;
        int s = (vintFlag ? 0x80 : 0) | (ovrFlag ? 0x40 : 0) | (colFlag ? 0x20 : 0);
        // TMS9918: bits 4-0 are the number of the fifth sprite of a line while the flag is set, and 0 otherwise (the oracle's reading of probe programs with 0, 1, 5, 20 and 31 sprites and no line with five);
        // the Master System's Mode 4 reads them as ones
        s |= Revision == SmsVdpRevision.Tms9918 ? (fifthSeen ? fifthSpriteNumber : 0) : 0x1F;
        vintFlag = ovrFlag = colFlag = false; lineIrqPending = false;
        fifthSpriteNumber = 0x1F; fifthSeen = false;
        return (byte)s;
    }

    /// <summary>A write to the control port ($BF): the first byte is the low half of the address (or a register's value), the second selects the operation.</summary>
    public void WriteControl(byte value, int mc)
    {
        RunTo(WriteTime(mc));
        if (!latch)
        {
            latchByte = value;
            addr = (addr & 0x3F00) | value;
            latch = true;
            return;
        }
        latch = false;
        code = value >> 6;
        addr = ((value & 0x3F) << 8) | latchByte;
        switch (code)
        {
            case 0:
                readBuffer = FetchVram(addr);
                addr = (addr + 1) & 0x3FFF;
                break;
            case 2:
                WriteRegister(value & 0x0F, latchByte, mc);
                break;
        }
    }

    private void WriteRegister(int reg, byte value, int mc)
    {
        int limit = Revision == SmsVdpRevision.Tms9918 ? 8 : 11;
        if (reg >= limit) return;   // registers 11-15 do nothing
        EnqueueWrite(OpRegister, reg, value, mc);
    }

    private void WriteCram(int address, byte value)
    {
        if (gg)
        {
            if ((address & 1) == 0) ggCramLatch = value;
            else
            {
                int i = (address >> 1) & 0x1F;
                cram[i * 2] = ggCramLatch; cram[i * 2 + 1] = (byte)(value & 0x0F);
                palArgb[i] = ConvertColor(ggCramLatch | (value & 0x0F) << 8);
            }
        }
        else
        {
            int i = address & 0x1F;
            cram[i] = (byte)(value & 0x3F);
            palArgb[i] = ConvertColor(value & 0x3F);
        }
    }

    /// <summary>Colour RAM contents as 0xAARRGGBB, for tools.</summary>
    public ReadOnlySpan<uint> Palette => palArgb;
    public byte CramByte(int i) => cram[i];

    /// <summary>Writes one byte of colour RAM directly (a tool, not the CPU's port): the Master System keeps 32 colours of --BBGGRR in bytes 0 to 31; the Game Gear 32 colours of 12 bits in little-endian pairs (bytes 0 to 63). The colour the picture uses is recomputed.</summary>
    public void SetCramByte(int index, byte value)
    {
        if (gg)
        {
            if ((uint)index >= 64u) return;
            cram[index] = (byte)(index % 2 == 1 ? value & 0x0F : value);
            int i = index >> 1;
            palArgb[i] = ConvertColor(cram[i * 2] | cram[i * 2 + 1] << 8);
        }
        else
        {
            if ((uint)index >= 32u) return;
            cram[index] = (byte)(value & 0x3F);
            palArgb[index] = ConvertColor(value & 0x3F);
        }
    }

    /// <summary>The 2-bit colour levels of the Master System's CRAM as 8-bit values. The chip's output is analog; these are the levels Mesen shows (the 2-bit value widened to 5 bits as
    /// 0, 10, 21, 31 and scaled to 8), chosen so frames compare pixel for pixel with the oracle.</summary>
    internal static readonly byte[] Level2 = { 0x00, 0x52, 0xAD, 0xFF };
    /// <summary>The 4-bit levels of the Game Gear's CRAM (the value widened to 5 bits, then scaled to 8).</summary>
    internal static readonly byte[] Level4 = BuildLevel4();
    private static byte[] BuildLevel4()
    {
        var t = new byte[16];
        for (int i = 0; i < 16; i++) { int c5 = (i << 1) | (i >> 3); t[i] = (byte)((c5 * 255 + 15) / 31); }
        return t;
    }

    private uint ConvertColor(int v)
    {
        if (gg) return 0xFF000000u | (uint)(Level4[v & 0xF] << 16 | Level4[(v >> 4) & 0xF] << 8 | Level4[(v >> 8) & 0xF]);
        return 0xFF000000u | (uint)(Level2[v & 3] << 16 | Level2[(v >> 2) & 3] << 8 | Level2[(v >> 4) & 3]);
    }

    // ---- reads for tools and the board ----
    public int VCounter => vcounter;
    public int AddressRegister => addr;
    public int CodeRegister => code;
    public bool VintPending => vintFlag;
    public bool LineIrqPending => lineIrqPending;
    public int LineCounterValue => lineCounter;
    public bool DisplayEnabled => (Regs[1] & 0x40) != 0;
    public int ActiveHeight => activeHeight;

    // =================================================================================== state

    public void SaveState(BinaryWriter w)
    {
        w.Write(Vram); w.Write(cram); w.Write(Regs);
        w.Write(addr); w.Write(code); w.Write(latch); w.Write(latchByte); w.Write(readBuffer); w.Write(ggCramLatch);
        w.Write(vintFlag); w.Write(ovrFlag); w.Write(colFlag); w.Write(lineIrqPending); w.Write(fifthSpriteNumber);
        w.Write(Line); w.Write(vcounter); w.Write(lineCounter); w.Write(hscrollLatched); w.Write(vscrollLatched); w.Write(hCounterLatch);
        w.Write(Frames);
        // where the chip is inside the line it is part-way through (a frame ends 21 Z80 cycles into the first blanking line), the queue of writes still waiting for their access slot, and the picture so far
        w.Write(fifthSeen); w.Write(activeHeight); w.Write(FrameHeight); w.Write(renderedX); w.Write(curMc); w.Write(lineDrawn); w.Write(lineDisplayOn);
        w.Write(evCount); w.Write(evNext); for (int i = 0; i < evTime.Length; i++) { w.Write(evTime[i]); w.Write(evKind[i]); }
        w.Write(colAtMc); w.Write(ovrAtMc);
        w.Write(pendHead); w.Write(pendCount); w.Write(lineBase); w.Write(lastWriteTime);
        for (int i = 0; i < pend.Length; i++) { w.Write(pend[i].Time); w.Write(pend[i].Kind); w.Write(pend[i].Index); w.Write(pend[i].Value); }
        w.Write(sprLine); foreach (uint c in lineBuf) w.Write(c);
        w.Write(tmsBg); w.Write(tmsSprOcc); w.Write(tmsSprColor);
        foreach (uint p in Frame) w.Write(p);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(Vram, 0, Vram.Length); r.Read(cram, 0, cram.Length); r.Read(Regs, 0, Regs.Length);
        addr = r.ReadInt32(); code = r.ReadInt32(); latch = r.ReadBoolean(); latchByte = r.ReadByte(); readBuffer = r.ReadByte(); ggCramLatch = r.ReadByte();
        vintFlag = r.ReadBoolean(); ovrFlag = r.ReadBoolean(); colFlag = r.ReadBoolean(); lineIrqPending = r.ReadBoolean(); fifthSpriteNumber = r.ReadInt32();
        Line = r.ReadInt32(); vcounter = r.ReadInt32(); lineCounter = r.ReadInt32(); hscrollLatched = r.ReadInt32(); vscrollLatched = r.ReadInt32(); hCounterLatch = r.ReadInt32();
        Frames = r.ReadInt64();
        fifthSeen = r.ReadBoolean(); activeHeight = r.ReadInt32(); FrameHeight = r.ReadInt32(); renderedX = r.ReadInt32(); curMc = r.ReadInt32(); lineDrawn = r.ReadBoolean(); lineDisplayOn = r.ReadBoolean();
        evCount = r.ReadInt32(); evNext = r.ReadInt32(); for (int i = 0; i < evTime.Length; i++) { evTime[i] = r.ReadInt32(); evKind[i] = r.ReadInt32(); }
        colAtMc = r.ReadInt32(); ovrAtMc = r.ReadInt32();
        pendHead = r.ReadInt32(); pendCount = r.ReadInt32(); lineBase = r.ReadInt64(); lastWriteTime = r.ReadInt64();
        for (int i = 0; i < pend.Length; i++) pend[i] = new PendingWrite { Time = r.ReadInt64(), Kind = r.ReadInt32(), Index = r.ReadInt32(), Value = r.ReadByte() };
        r.Read(sprLine, 0, sprLine.Length); for (int i = 0; i < lineBuf.Length; i++) lineBuf[i] = r.ReadUInt32();
        r.Read(tmsBg, 0, tmsBg.Length); r.Read(tmsSprOcc, 0, tmsSprOcc.Length); r.Read(tmsSprColor, 0, tmsSprColor.Length);
        for (int i = 0; i < Frame.Length; i++) Frame[i] = r.ReadUInt32();
        for (int i = 0; i < 32; i++)
            palArgb[i] = ConvertColor(gg ? cram[i * 2] | (cram[i * 2 + 1] & 0x0F) << 8 : cram[i] & 0x3F);
        Refresh();
    }
}
