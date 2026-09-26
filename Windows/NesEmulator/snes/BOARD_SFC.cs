using System;

namespace NesEmulator.Snes;

/// <summary>
/// The SNES board - the SFC family's fourth member, and the reason the family cannot simply be
/// dropped into NES.cs: the 24-bit bus, 128KB WRAM, CPU I/O block ($4200-$421F), DMA and the
/// master-clock scheduler all live here. "The board follows the game": SNES games get this board,
/// NES games keep NES.cs, and cross-family cores meet through bridges, not by editing either board.
///
/// Time is counted in master clocks (21.477 MHz). One scanline is 1364 master clocks and an NTSC
/// frame is 262 lines - the same grid the NES PPU runs on, which is what will make NES/SNES core
/// mixing timing-compatible later.
///
/// PHASE 1 scope: general-purpose DMA (HDMA registers are stored but not run), H/V IRQ, NMI,
/// auto-joypad, the multiply/divide unit, and a fake SPC700 handshake on $2140-$2143 until APU_SFC
/// exists.
/// </summary>
public sealed class BOARD_SFC : ISnesBus
{
    public const int ClocksPerLine = 1364, LinesPerFrame = 262, VBlankLine = 225;

    public CPU_SFC Cpu { get; }
    public PPU_SFC Ppu { get; }
    public SnesCartridge Cart { get; }

    public readonly byte[] Wram = new byte[0x20000];
    private uint wramPortAddress;

    // ---- Timing ----
    public long MasterClock { get; private set; }
    public long FrameCount { get; private set; }
    public int Scanline { get; private set; }
    private int lineClock;
    private bool inVBlank, frameReady;
    private long autoJoyBusyUntil;

    // ---- CPU I/O ($4200-$421F) ----
    private byte nmitimen, wrio = 0xFF, wrmpya = 0xFF, wrmpyb, memsel, hdmaen;
    private ushort wrdiv = 0xFFFF, rddiv, rdmpy, htime = 0x1FF, vtime = 0x1FF;
    private bool nmiFlag, irqFlag, chipIrq;   // chipIrq: the cartridge chip's line (SA-1), ORed into the CPU's IRQ input
    private byte mdr;   // CPU open bus: the last value on the data bus

    // ---- Controllers ----
    /// <summary>Held buttons per port, in SNES serial order: bit15=B ... bit4=R (see <see cref="SnesButtons"/>).</summary>
    public readonly ushort[] Pads = new ushort[2];
    private readonly ushort[] autoJoy = new ushort[4];
    private readonly uint[] serialShift = new uint[2];
    private bool joyLatch;

    // ---- DMA ($43x0-$43xA) ----
    private readonly byte[] dmaRegs = new byte[0x80];

    // ---- Audio unit ($2140-$217F) ----
    public ISnesApu Apu { get; }

    /// <summary>Cartridge chip (DSP-1, ...) consulted before ROM/SRAM decoding, or null.</summary>
    public ISnesCoprocessor? Coprocessor { get; }

    /// <param name="apu">Audio unit; defaults to the silent <see cref="APU_HLE"/> loader stand-in.</param>
    /// <param name="coprocessor">Cartridge chip, when the game has one and its firmware was found.</param>
    public BOARD_SFC(SnesCartridge cart, ISnesApu? apu = null, ISnesCoprocessor? coprocessor = null)
    {
        Cart = cart;
        Coprocessor = coprocessor;
        Apu = apu ?? new APU_HLE();
        Ppu = new PPU_SFC { CounterSource = () => (lineClock >> 2, Scanline) };
        Cpu = new CPU_SFC(this);
        Coprocessor?.Attach(level => { chipIrq = level; Cpu.SetIrq(irqFlag || chipIrq); }, BuildPageTable);
        for (int i = 0; i < 0x80; i++) dmaRegs[i] = 0xFF;
        Reset();
    }

    public void Reset()
    {
        nmitimen = 0; memsel = 0; hdmaen = 0; nmiFlag = irqFlag = false;
        BuildPageTable();
        UpdateNextEvent();
        Apu.Reset();
        Coprocessor?.Reset();
        Ppu.Reset();
        Ppu.BeginFrame();
        Cpu.Reset();
    }

    /// <summary>Optional per-instruction observer for debug tooling (PC sampling, tracing).</summary>
    public Action<CPU_SFC>? InstructionHook;

    public byte NmiTimen => nmitimen;
    public long NmiCount { get; private set; }

    /// <summary>Run until the next vblank start (one video frame).</summary>
    public void RunFrame()
    {
        frameReady = false;
        long guard = MasterClock + ClocksPerLine * (long)LinesPerFrame * 4;
        var hook = InstructionHook;
        while (!frameReady && MasterClock < guard)
        {
            hook?.Invoke(Cpu);
            Cpu.Step();
        }
    }

    // =====================================================================================
    //  Clock
    // =====================================================================================

    /// <summary>HDMA transfers happen once per line at the start of hblank (dot ~276).</summary>
    private const int HdmaClock = 276 * 4;
    private int vblankLine = VBlankLine;   // 225, or 240 with overscan; latched at frame start
    private int stall;                     // master clocks HDMA stole from the CPU, charged on the next tick

    /// <summary>false = reference paths only (per-access event checks, bank/offset decoding).</summary>
    public static bool FastPaths = true;

    // lineClock value at which the next thing can happen on this line (H-IRQ, HDMA, line end).
    // Accesses that stay below it only advance the clocks.
    private int nextEvent;

    private void Tick(int clocks)
    {
        if (FastPaths && stall == 0 && lineClock + clocks < nextEvent)
        {
            MasterClock += clocks;
            lineClock += clocks;
            return;
        }
        TickReference(clocks);
        UpdateNextEvent();
    }

    /// <summary>Earliest lineClock at which TickReference has work to do. Conservative is fine.</summary>
    private void UpdateNextEvent()
    {
        int next = lineClock < HdmaClock ? HdmaClock : ClocksPerLine;
        int mode = nmitimen & 0x30;
        if (mode == 0x10 || (mode == 0x30 && Scanline == vtime))
        {
            int target = htime * 4;
            if (target > lineClock && target < next) next = target;
        }
        nextEvent = next;
    }

    private void TickReference(int clocks)
    {
        if (stall != 0) { clocks += stall; stall = 0; }
        MasterClock += clocks;
        int before = lineClock;
        lineClock += clocks;
        int to = Math.Min(lineClock, ClocksPerLine);
        CheckHIrq(before, to);
        if (before < HdmaClock && HdmaClock <= to) RunHdmaLine();
        while (lineClock >= ClocksPerLine)
        {
            lineClock -= ClocksPerLine;
            NextLine();
            to = Math.Min(lineClock, ClocksPerLine);
            CheckHIrq(-1, to);
            if (HdmaClock <= to) RunHdmaLine();
        }
    }

    private void NextLine()
    {
        Scanline++;
        Coprocessor?.RunTo(MasterClock);
        if (Scanline == LinesPerFrame) StartFrame();
        else if (Scanline == vblankLine)
        {
            inVBlank = true;
            nmiFlag = true;
            if ((nmitimen & 0x80) != 0) { Cpu.RaiseNmi(); NmiCount++; }
            if ((nmitimen & 0x01) != 0) RunAutoJoypad();
            Ppu.OnVBlankStart();
            Apu.RunTo(MasterClock);   // keep audio flowing even when the game leaves the ports alone
            frameReady = true;
        }
        else if (Scanline < vblankLine)
        {
            long t = SnesProfiler.Begin();
            Ppu.RenderLine(Scanline);
            if (t != 0) SnesProfiler.PpuTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        }
        // V-IRQ alone fires at the start of the matching line.
        if ((nmitimen & 0x30) == 0x20 && Scanline == vtime) RaiseIrq();
    }

    private void StartFrame()
    {
        Scanline = 0;
        inVBlank = false;
        nmiFlag = false;
        FrameCount++;
        Ppu.BeginFrame();
        vblankLine = Ppu.VisibleHeight + 1;
        HdmaInit();
    }

    private void CheckHIrq(int fromClock, int toClock)
    {
        int mode = nmitimen & 0x30;
        if (mode is not (0x10 or 0x30)) return;
        if (mode == 0x30 && Scanline != vtime) return;
        int target = htime * 4;
        if (fromClock < target && target <= toClock) RaiseIrq();
    }

    private void RaiseIrq() { irqFlag = true; Cpu.SetIrq(true); }
    private void ClearIrq() { irqFlag = false; Cpu.SetIrq(chipIrq); }

    private void RunAutoJoypad()
    {
        autoJoy[0] = Pads[0]; autoJoy[1] = Pads[1]; autoJoy[2] = autoJoy[3] = 0;
        autoJoyBusyUntil = MasterClock + 4224;
    }

    // =====================================================================================
    //  ISnesBus
    // =====================================================================================

    public void Idle() => Tick(6);

    public byte Read(uint address)
    {
        if (FastPaths)
        {
            ref Page page = ref pages[address >> 12];
            byte[]? data = page.Data;
            if (data != null)
            {
                // Copy before Tick: a chip running inside Tick (SA-1, Super FX) may rebuild the table.
                int index = page.Offset + (int)(address & 0xFFF);
                Tick(page.Speed);
                return mdr = data[index];
            }
        }
        uint bank = address >> 16, offset = address & 0xFFFF;
        Tick(AccessClocks(bank, offset));
        return mdr = ReadNoTick(bank, offset);
    }

    public void Write(uint address, byte value)
    {
        if (FastPaths)
        {
            ref Page page = ref pages[address >> 12];
            if (page.Writable)
            {
                byte[] data = page.Data!;
                int index = page.Offset + (int)(address & 0xFFF);
                Tick(page.Speed);
                mdr = value;
                data[index] = value;
                return;
            }
        }
        uint bank = address >> 16, offset = address & 0xFFFF;
        Tick(AccessClocks(bank, offset));
        mdr = value;
        WriteNoTick(bank, offset, value);
    }

    // ---- Page table: 4KB pages over the 24-bit bus that map straight to WRAM or ROM ----
    // Everything else (I/O, SRAM, open bus) has no Data and goes through the decoding path.

    private struct Page
    {
        public byte[]? Data;
        public int Offset;
        public int Speed;
        public bool Writable;
    }

    private readonly Page[] pages = new Page[4096];

    private void BuildPageTable()
    {
        for (int p = 0; p < pages.Length; p++)
        {
            uint bank = (uint)p >> 4, offset = (uint)(p & 15) << 12;
            ref Page page = ref pages[p];
            page = default;
            if (bank is 0x7E or 0x7F)
            {
                page = new Page { Data = Wram, Offset = (int)((bank - 0x7E) << 16 | offset), Speed = AccessClocks(bank, offset), Writable = true };
            }
            else if (IsSystemBank(bank) && offset < 0x8000)
            {
                if (offset < 0x2000) page = new Page { Data = Wram, Offset = (int)offset, Speed = AccessClocks(bank, offset), Writable = true };
            }
            else if (Coprocessor != null && Coprocessor.Owns(bank, offset))
            {
                // Chip ports stay on the slow path; chip-mapped ROM (SA-1 banks) may still be direct.
                if (Coprocessor.TryMapPage(bank, offset, out var data, out int index))
                    page = new Page { Data = data, Offset = index, Speed = AccessClocks(bank, offset) };
            }
            else if (Cart.TryMapRomLinear(bank, offset, 0x1000, out int romIndex))
            {
                page = new Page { Data = Cart.Rom, Offset = romIndex, Speed = AccessClocks(bank, offset) };
            }
        }
    }

    private int AccessClocks(uint bank, uint offset)
    {
        if ((bank & 0x40) != 0)                           // $40-$7F, $C0-$FF
            return bank >= 0xC0 && (memsel & 1) != 0 ? 6 : 8;
        if (offset < 0x2000) return 8;
        if (offset < 0x4000) return 6;
        if (offset < 0x4200) return 12;                   // old-style joypad ports: NES speed
        if (offset < 0x6000) return 6;
        if (offset < 0x8000) return 8;
        return bank >= 0x80 && (memsel & 1) != 0 ? 6 : 8;
    }

    private static bool IsSystemBank(uint bank) => (bank & 0x40) == 0;

    private byte ReadNoTick(uint bank, uint offset)
    {
        if (bank is 0x7E or 0x7F) return Wram[(bank - 0x7E) << 16 | offset];
        if (IsSystemBank(bank) && offset < 0x8000)
        {
            if (offset < 0x2000) return Wram[offset];
            if (offset >= 0x2100 && offset < 0x2200) return ReadBBus((byte)offset);
            if (offset is 0x4016 or 0x4017) return ReadSerialPad((int)offset - 0x4016);
            if (offset >= 0x4200 && offset < 0x4220) return ReadCpuIo(offset);
            if (offset >= 0x4300 && offset < 0x4380) return dmaRegs[offset & 0x7F];
        }
        if (Coprocessor != null && Coprocessor.Owns(bank, offset)) return Coprocessor.Read(bank, offset, MasterClock);
        return Cart.TryRead(bank, offset, out byte v) ? v : mdr;
    }

    private void WriteNoTick(uint bank, uint offset, byte value)
    {
        if (bank is 0x7E or 0x7F) { Wram[(bank - 0x7E) << 16 | offset] = value; return; }
        if (IsSystemBank(bank) && offset < 0x8000)
        {
            if (offset < 0x2000) { Wram[offset] = value; return; }
            if (offset >= 0x2100 && offset < 0x2200) { WriteBBus((byte)offset, value); return; }
            if (offset == 0x4016) { WriteJoyLatch(value); return; }
            if (offset >= 0x4200 && offset < 0x4220) { WriteCpuIo(offset, value); return; }
            if (offset >= 0x4300 && offset < 0x4380) { dmaRegs[offset & 0x7F] = value; return; }
        }
        if (Coprocessor != null && Coprocessor.Owns(bank, offset)) { Coprocessor.Write(bank, offset, value, MasterClock); return; }
        Cart.Write(bank, offset, value);
    }

    // ---- B-bus ($2100-$21FF) ----

    private byte ReadBBus(byte reg)
    {
        if (reg < 0x40) { int v = Ppu.ReadRegister(reg); return v < 0 ? mdr : (byte)v; }
        if (reg < 0x80) { Apu.RunTo(MasterClock); return Apu.ReadPort(reg & 3); }
        if (reg == 0x80) { byte v = Wram[wramPortAddress]; wramPortAddress = (wramPortAddress + 1) & 0x1FFFF; return v; }
        return mdr;
    }

    private void WriteBBus(byte reg, byte value)
    {
        if (reg < 0x40) { Ppu.WriteRegister(reg, value); return; }
        if (reg < 0x80) { Apu.RunTo(MasterClock); Apu.WritePort(reg & 3, value); return; }
        switch (reg)
        {
            case 0x80: Wram[wramPortAddress] = value; wramPortAddress = (wramPortAddress + 1) & 0x1FFFF; break;
            case 0x81: wramPortAddress = (wramPortAddress & 0x1FF00) | value; break;
            case 0x82: wramPortAddress = (wramPortAddress & 0x100FF) | (uint)value << 8; break;
            case 0x83: wramPortAddress = (wramPortAddress & 0x0FFFF) | (uint)(value & 1) << 16; break;
        }
    }

    // ---- CPU I/O ----

    private byte ReadCpuIo(uint offset)
    {
        switch (offset)
        {
            case 0x4210: { byte v = (byte)((nmiFlag ? 0x80 : 0) | (mdr & 0x70) | 0x02); nmiFlag = false; return v; }
            case 0x4211: { byte v = (byte)((irqFlag ? 0x80 : 0) | (mdr & 0x7F)); ClearIrq(); return v; }
            case 0x4212:
            {
                bool hblank = lineClock < 4 || lineClock >= 274 * 4;
                bool joyBusy = MasterClock < autoJoyBusyUntil;
                return (byte)((inVBlank ? 0x80 : 0) | (hblank ? 0x40 : 0) | (joyBusy ? 0x01 : 0) | (mdr & 0x3E));
            }
            case 0x4213: return wrio;
            case 0x4214: return (byte)rddiv;
            case 0x4215: return (byte)(rddiv >> 8);
            case 0x4216: return (byte)rdmpy;
            case 0x4217: return (byte)(rdmpy >> 8);
        }
        if (offset >= 0x4218) { ushort w = autoJoy[(offset - 0x4218) >> 1]; return (offset & 1) == 0 ? (byte)w : (byte)(w >> 8); }
        return mdr;
    }

    private void WriteCpuIo(uint offset, byte value)
    {
        switch (offset)
        {
            case 0x4200:
            {
                bool nmiWasOff = (nmitimen & 0x80) == 0;
                nmitimen = value;
                if (nmiWasOff && (value & 0x80) != 0 && nmiFlag) Cpu.RaiseNmi();   // enabling mid-vblank fires at once
                if ((value & 0x30) == 0) { ClearIrq(); }
                UpdateNextEvent();
                break;
            }
            case 0x4201: wrio = value; break;
            case 0x4202: wrmpya = value; break;
            case 0x4203: wrmpyb = value; rdmpy = (ushort)(wrmpya * value); rddiv = value; break;
            case 0x4204: wrdiv = (ushort)((wrdiv & 0xFF00) | value); break;
            case 0x4205: wrdiv = (ushort)((wrdiv & 0x00FF) | value << 8); break;
            case 0x4206:
                if (value == 0) { rddiv = 0xFFFF; rdmpy = wrdiv; }
                else { rddiv = (ushort)(wrdiv / value); rdmpy = (ushort)(wrdiv % value); }
                break;
            case 0x4207: htime = (ushort)((htime & 0x100) | value); UpdateNextEvent(); break;
            case 0x4208: htime = (ushort)((htime & 0x0FF) | (value & 1) << 8); UpdateNextEvent(); break;
            case 0x4209: vtime = (ushort)((vtime & 0x100) | value); UpdateNextEvent(); break;
            case 0x420A: vtime = (ushort)((vtime & 0x0FF) | (value & 1) << 8); UpdateNextEvent(); break;
            case 0x420B: RunDma(value); break;
            case 0x420C:
                hdmaen = value;
                hdmaenLog[hdmaenLogPos++ & 7] = (FrameCount, Scanline, lineClock, value);   // diagnostics
                break;
            case 0x420D:
                if (((memsel ^ value) & 1) != 0) { memsel = value; BuildPageTable(); }   // FastROM changes page speeds
                memsel = value;
                break;
        }
    }

    // ---- Controllers ----

    private void WriteJoyLatch(byte value)
    {
        bool latch = (value & 1) != 0;
        if (latch) { serialShift[0] = Pads[0]; serialShift[1] = Pads[1]; }
        joyLatch = latch;
    }

    private byte ReadSerialPad(int port)
    {
        if (joyLatch) serialShift[port] = Pads[port];
        byte bit = (byte)((serialShift[port] >> 15) & 1);
        serialShift[port] = (serialShift[port] << 1) | 1;   // reads past 16 bits return 1
        return (byte)((mdr & 0xFC) | bit);
    }

    // ---- General-purpose DMA ----

    private static readonly int[][] DmaPatterns =
    {
        new[] { 0 }, new[] { 0, 1 }, new[] { 0, 0 }, new[] { 0, 0, 1, 1 },
        new[] { 0, 1, 2, 3 }, new[] { 0, 1, 0, 1 }, new[] { 0, 0 }, new[] { 0, 0, 1, 1 },
    };

    private void RunDma(byte channels)
    {
        if (channels == 0) return;
        Tick(8);
        for (int ch = 0; ch < 8; ch++)
        {
            if ((channels & (1 << ch)) == 0) continue;
            int r = ch << 4;
            byte dmap = dmaRegs[r], bbad = dmaRegs[r + 1];
            ushort aAddr = (ushort)(dmaRegs[r + 2] | dmaRegs[r + 3] << 8);
            byte aBank = dmaRegs[r + 4];
            int count = dmaRegs[r + 5] | dmaRegs[r + 6] << 8;
            if (count == 0) count = 0x10000;
            bool toA = (dmap & 0x80) != 0;
            int step = (dmap & 0x08) != 0 ? 0 : (dmap & 0x10) != 0 ? -1 : 1;
            var pattern = DmaPatterns[dmap & 7];

            Tick(8);
            for (int n = 0; count > 0; n++, count--)
            {
                byte bReg = (byte)(bbad + pattern[n % pattern.Length]);
                if (toA) WriteNoTick(aBank, aAddr, ReadBBus(bReg));
                else WriteBBus(bReg, ReadNoTick(aBank, aAddr));
                aAddr = (ushort)(aAddr + step);
                Tick(8);
            }
            dmaRegs[r + 2] = (byte)aAddr; dmaRegs[r + 3] = (byte)(aAddr >> 8);
            dmaRegs[r + 5] = 0; dmaRegs[r + 6] = 0;
        }
    }

    // ---- HDMA: per-line register streams (gradients, wavy scroll, windows...) ----
    // The reload/advance sequence follows bsnes/higan's approach (from memory); see THIRD_PARTY_NOTICES.md.
    // Channel registers reused from DMA: $43x8/9 = table address (A2A), $43xA = line counter,
    // $43x5/6 + $43x7 = indirect data address.

    private readonly bool[] hdmaDone = new bool[8], hdmaDoTransfer = new bool[8];
    private readonly int[] hdmaBytes = new int[8], hdmaBytesLastFrame = new int[8];   // diagnostics
    private readonly (long frame, int line, int clock, byte value)[] hdmaenLog = new (long, int, int, byte)[8];
    private int hdmaenLogPos;

    /// <summary>Diagnostics: the enabled HDMA channels and what each one writes.</summary>
    public string DescribeHdma()
    {
        var sb = new System.Text.StringBuilder($"HDMAEN={hdmaen:X2}");
        sb.Append(" recent $420C writes:");
        for (int k = 0; k < 8; k++)
        {
            var e = hdmaenLog[(hdmaenLogPos + k) & 7];
            if (e.frame != 0 || e.value != 0) sb.Append($" [f{e.frame} L{e.line} c{e.clock} ={e.value:X2}]");
        }
        for (int ch = 0; ch < 8; ch++)
        {
            if ((hdmaen & (1 << ch)) == 0) continue;
            int r = ch << 4;
            sb.Append($" | ch{ch}: DMAP={dmaRegs[r]:X2} -> $21{dmaRegs[r + 1]:X2} table=${dmaRegs[r + 4]:X2}:{dmaRegs[r + 3]:X2}{dmaRegs[r + 2]:X2}");
            if ((dmaRegs[r] & 0x40) != 0) sb.Append($" indirect bank ${dmaRegs[r + 7]:X2}");
            sb.Append($" [done={hdmaDone[ch]} nltr={dmaRegs[r + 0xA]:X2} a2a={dmaRegs[r + 9]:X2}{dmaRegs[r + 8]:X2} bytesLastFrame={hdmaBytesLastFrame[ch]}]");
        }
        return sb.ToString();
    }

    private void HdmaInit()
    {
        Array.Copy(hdmaBytes, hdmaBytesLastFrame, 8);
        Array.Clear(hdmaBytes);
        for (int ch = 0; ch < 8; ch++)
        {
            // Every channel is re-armed at line 0, enabled or not. A channel the game enables later in
            // the frame then runs from whatever table address and line counter it holds; games write
            // those directly for a mid-frame start (Super Ghouls 'n Ghosts does at line 38).
            hdmaDone[ch] = false; hdmaDoTransfer[ch] = false;
            if ((hdmaen & (1 << ch)) == 0) continue;
            int r = ch << 4;
            dmaRegs[r + 8] = dmaRegs[r + 2]; dmaRegs[r + 9] = dmaRegs[r + 3];
            dmaRegs[r + 0xA] = 0;
            HdmaReload(ch);
        }
    }

    /// <summary>When the line counter's low 7 bits run out, fetch the next table entry.</summary>
    private void HdmaReload(int ch)
    {
        int r = ch << 4;
        if ((dmaRegs[r + 0xA] & 0x7F) != 0) return;
        byte bank = dmaRegs[r + 4];
        ushort a2a = (ushort)(dmaRegs[r + 8] | dmaRegs[r + 9] << 8);
        byte count = ReadNoTick(bank, a2a++);
        dmaRegs[r + 0xA] = count;
        hdmaDone[ch] = count == 0;
        hdmaDoTransfer[ch] = !hdmaDone[ch];
        stall += 8;
        if ((dmaRegs[r] & 0x40) != 0 && !hdmaDone[ch])
        {
            dmaRegs[r + 5] = ReadNoTick(bank, a2a++);
            dmaRegs[r + 6] = ReadNoTick(bank, a2a++);
            stall += 16;
        }
        dmaRegs[r + 8] = (byte)a2a; dmaRegs[r + 9] = (byte)(a2a >> 8);
    }

    private void RunHdmaLine()
    {
        if (hdmaen == 0 || Scanline >= vblankLine) return;
        stall += 18;
        for (int ch = 0; ch < 8; ch++)
        {
            if ((hdmaen & (1 << ch)) == 0 || hdmaDone[ch]) continue;
            int r = ch << 4;
            byte dmap = dmaRegs[r], bbad = dmaRegs[r + 1];
            if (hdmaDoTransfer[ch])
            {
                bool indirect = (dmap & 0x40) != 0, toA = (dmap & 0x80) != 0;
                int lo = indirect ? r + 5 : r + 8;   // address register that advances
                byte bank = indirect ? dmaRegs[r + 7] : dmaRegs[r + 4];
                foreach (int offset in DmaPatterns[dmap & 7])
                {
                    ushort addr = (ushort)(dmaRegs[lo] | dmaRegs[lo + 1] << 8);
                    byte bReg = (byte)(bbad + offset);
                    if (toA) WriteNoTick(bank, addr, ReadBBus(bReg));
                    else WriteBBus(bReg, ReadNoTick(bank, addr));
                    addr++;
                    dmaRegs[lo] = (byte)addr; dmaRegs[lo + 1] = (byte)(addr >> 8);
                    stall += 8;
                    hdmaBytes[ch]++;
                }
            }
            dmaRegs[r + 0xA]--;
            hdmaDoTransfer[ch] = (dmaRegs[r + 0xA] & 0x80) != 0;   // repeat mode transfers every line
            HdmaReload(ch);
        }
    }
}

/// <summary>SNES pad bits in serial (shift-out) order, as stored in <see cref="BOARD_SFC.Pads"/>.</summary>
[Flags]
public enum SnesButtons : ushort
{
    None = 0,
    R = 1 << 4, L = 1 << 5, X = 1 << 6, A = 1 << 7,
    Right = 1 << 8, Left = 1 << 9, Down = 1 << 10, Up = 1 << 11,
    Start = 1 << 12, Select = 1 << 13, Y = 1 << 14, B = 1 << 15,
}
