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
public sealed partial class SmsVdp
{
    // ---- timing constants (master clocks within a line, origin at counter $1E6) -------------------------------------------------------------------------
    public const int LineMc = 684;                 // 342 pixels x 2
    /// <summary>The V counter takes its new value (H counter $F4).</summary>
    public int VCounterMc = 4;
    /// <summary>The frame-interrupt flag is set (H counter $F5).</summary>
    public int VintFlagMc = 8;
    /// <summary>The line counter underflows and raises the line interrupt.</summary>
    public int HintMc = 4;
    /// <summary>The horizontal scroll value is latched (H counter $F3).</summary>
    public int ScrollLatchMc = 0;
    /// <summary>Master clock at which pixel 0 of the picture is drawn; pixel x follows at two master clocks per pixel.</summary>
    public int PixelOriginMc = 52 + 2 * 11;
    /// <summary>Master clocks the collision flag trails the pixel that caused it.</summary>
    public int CollisionDelayMc = 0;
    /// <summary>The sprite-overflow flag is set this far into the line it is detected on.</summary>
    public int OverflowFlagMc = 8;

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

    public SmsVdp(SmsVdpRevision revision, bool pal)
    {
        Revision = revision; Pal = pal; gg = revision == SmsVdpRevision.GameGear;
        TotalLines = pal ? 313 : 262;
        PowerOn();
    }

    /// <summary>True when the chip can draw Mode 4 and the extended heights: every revision but the TMS9918.</summary>
    private bool HasMode4 => Revision != SmsVdpRevision.Tms9918;

    /// <summary>The state the BIOS leaves behind (the machine here never runs one): registers 0-6 = $36 $80 $FF x5, register 7-9 = 0, register 10 = $FF, address and code cleared.</summary>
    public void PowerOn()
    {
        Array.Clear(Vram); Array.Clear(cram); Array.Clear(Regs);
        Regs[0] = 0x36; Regs[1] = 0x80;
        for (int i = 2; i <= 6; i++) Regs[i] = 0xFF;
        Regs[10] = 0xFF;
        addr = 0; code = 0; latch = false; readBuffer = 0; ggCramLatch = 0;
        vintFlag = ovrFlag = colFlag = lineIrqPending = false; fifthSpriteNumber = 0x1F;
        for (int i = 0; i < 32; i++) palArgb[i] = ConvertColor(0);
        lineCounter = 0xFF; hscrollLatched = 0; vscrollLatched = 0; hCounterLatch = 0;
        Line = 0; vcounter = 0; Frames = 0; renderedX = 256; curMc = 0;
        evCount = evNext = 0; colAtMc = ovrAtMc = int.MaxValue; NextEventMc = int.MaxValue;
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

    /// <summary>Advances to master-clock position <paramref name="mc"/> in the current line: fires the events that have come due and draws the pixels up to there.</summary>
    public void RunTo(int mc)
    {
        if (mc > LineMc) mc = LineMc;
        while (evNext < evCount && evTime[evNext] <= mc)
        {
            curMc = evTime[evNext];
            Fire(evKind[evNext]);
            evNext++;
        }
        if (colAtMc <= mc) { colAtMc = int.MaxValue; colFlag = true; }
        if (ovrAtMc <= mc) { ovrAtMc = int.MaxValue; ovrFlag = true; }
        if (Line < activeHeight)
        {
            int xEnd = (mc - PixelOriginMc + 1) >> 1;
            if (xEnd > 256) xEnd = 256;
            if (xEnd > renderedX) { RenderSpan(renderedX, xEnd); renderedX = xEnd; }
        }
        curMc = mc;
        Refresh();
    }

    private void Refresh()
    {
        int next = evNext < evCount ? evTime[evNext] : int.MaxValue;
        if (colAtMc < next) next = colAtMc;
        if (ovrAtMc < next) next = ovrAtMc;
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
                if (Line == activeHeight) vintFlag = true;
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
        readBuffer = Vram[addr];
        addr = (addr + 1) & 0x3FFF;
        return v;
    }

    /// <summary>A write to the data port ($BE).</summary>
    public void WriteData(byte value, int mc)
    {
        RunTo(mc);
        latch = false;
        if (code == 3) WriteCram(addr, value);
        else Vram[addr] = value;
        readBuffer = value;
        addr = (addr + 1) & 0x3FFF;
    }

    /// <summary>A read of the status port ($BF): the flags, which it clears together with the latch and the interrupt lines.</summary>
    public byte ReadStatus(int mc)
    {
        RunTo(mc);
        latch = false;
        int s = (vintFlag ? 0x80 : 0) | (ovrFlag ? 0x40 : 0) | (colFlag ? 0x20 : 0);
        s |= Revision == SmsVdpRevision.Tms9918 ? fifthSpriteNumber : 0x1F;
        vintFlag = ovrFlag = colFlag = false; lineIrqPending = false;
        fifthSpriteNumber = 0x1F; fifthSeen = false;
        return (byte)s;
    }

    /// <summary>A write to the control port ($BF): the first byte is the low half of the address (or a register's value), the second selects the operation.</summary>
    public void WriteControl(byte value, int mc)
    {
        RunTo(mc);
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
                readBuffer = Vram[addr];
                addr = (addr + 1) & 0x3FFF;
                break;
            case 2:
                WriteRegister(value & 0x0F, latchByte);
                break;
        }
    }

    private void WriteRegister(int reg, byte value)
    {
        int limit = Revision == SmsVdpRevision.Tms9918 ? 8 : 11;
        if (reg >= limit) return;   // registers 11-15 do nothing
        RegisterWriteHook?.Invoke(Line, curMc, reg, value);
        Regs[reg] = value;
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

    /// <summary>The 2-bit colour levels of the Master System's CRAM as 8-bit values. The chip's output is analog; these are the levels Mesen shows (the 2-bit value widened to 5 bits as
    /// 0, 10, 21, 31 and scaled to 8), chosen so frames compare pixel for pixel with the oracle.</summary>
    private static readonly byte[] Level2 = { 0x00, 0x52, 0xAD, 0xFF };
    /// <summary>The 4-bit levels of the Game Gear's CRAM (the value widened to 5 bits, then scaled to 8).</summary>
    private static readonly byte[] Level4 = BuildLevel4();
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
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(Vram, 0, Vram.Length); r.Read(cram, 0, cram.Length); r.Read(Regs, 0, Regs.Length);
        addr = r.ReadInt32(); code = r.ReadInt32(); latch = r.ReadBoolean(); latchByte = r.ReadByte(); readBuffer = r.ReadByte(); ggCramLatch = r.ReadByte();
        vintFlag = r.ReadBoolean(); ovrFlag = r.ReadBoolean(); colFlag = r.ReadBoolean(); lineIrqPending = r.ReadBoolean(); fifthSpriteNumber = r.ReadInt32();
        Line = r.ReadInt32(); vcounter = r.ReadInt32(); lineCounter = r.ReadInt32(); hscrollLatched = r.ReadInt32(); vscrollLatched = r.ReadInt32(); hCounterLatch = r.ReadInt32();
        Frames = r.ReadInt64();
        for (int i = 0; i < 32; i++)
            palArgb[i] = ConvertColor(gg ? cram[i * 2] | (cram[i * 2 + 1] & 0x0F) << 8 : cram[i] & 0x3F);
    }
}
