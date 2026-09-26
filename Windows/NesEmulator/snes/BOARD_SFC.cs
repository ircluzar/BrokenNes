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
    private bool nmiFlag, irqFlag;
    private byte mdr;   // CPU open bus: the last value on the data bus

    // ---- Controllers ----
    /// <summary>Held buttons per port, in SNES serial order: bit15=B ... bit4=R (see <see cref="SnesButtons"/>).</summary>
    public readonly ushort[] Pads = new ushort[2];
    private readonly ushort[] autoJoy = new ushort[4];
    private readonly uint[] serialShift = new uint[2];
    private bool joyLatch;

    // ---- DMA ($43x0-$43xA) ----
    private readonly byte[] dmaRegs = new byte[0x80];

    // ---- Fake SMP (until APU_SFC): answers the IPL boot handshake ----
    private readonly byte[] apuToCpu = { 0xAA, 0xBB, 0x00, 0x00 };

    public BOARD_SFC(SnesCartridge cart)
    {
        Cart = cart;
        Ppu = new PPU_SFC { CounterSource = () => (lineClock >> 2, Scanline) };
        Cpu = new CPU_SFC(this);
        for (int i = 0; i < 0x80; i++) dmaRegs[i] = 0xFF;
        Reset();
    }

    public void Reset()
    {
        nmitimen = 0; memsel = 0; hdmaen = 0; nmiFlag = irqFlag = false;
        Ppu.Reset();
        Cpu.Reset();
    }

    /// <summary>Run until the next vblank start (one video frame).</summary>
    public void RunFrame()
    {
        frameReady = false;
        long guard = MasterClock + ClocksPerLine * (long)LinesPerFrame * 4;
        while (!frameReady && MasterClock < guard) Cpu.Step();
    }

    // =====================================================================================
    //  Clock
    // =====================================================================================

    private void Tick(int clocks)
    {
        MasterClock += clocks;
        int before = lineClock;
        lineClock += clocks;
        CheckHIrq(before, Math.Min(lineClock, ClocksPerLine));
        while (lineClock >= ClocksPerLine)
        {
            lineClock -= ClocksPerLine;
            NextLine();
            CheckHIrq(-1, lineClock);
        }
    }

    private void NextLine()
    {
        Scanline++;
        if (Scanline == VBlankLine)
        {
            inVBlank = true;
            nmiFlag = true;
            if ((nmitimen & 0x80) != 0) Cpu.RaiseNmi();
            if ((nmitimen & 0x01) != 0) RunAutoJoypad();
            Ppu.OnVBlankStart();
            frameReady = true;
        }
        else if (Scanline == LinesPerFrame)
        {
            Scanline = 0;
            inVBlank = false;
            nmiFlag = false;
            FrameCount++;
        }
        // V-IRQ alone fires at the start of the matching line.
        if ((nmitimen & 0x30) == 0x20 && Scanline == vtime) RaiseIrq();
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
        uint bank = address >> 16, offset = address & 0xFFFF;
        Tick(AccessClocks(bank, offset));
        return mdr = ReadNoTick(bank, offset);
    }

    public void Write(uint address, byte value)
    {
        uint bank = address >> 16, offset = address & 0xFFFF;
        Tick(AccessClocks(bank, offset));
        mdr = value;
        WriteNoTick(bank, offset, value);
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
        Cart.Write(bank, offset, value);
    }

    // ---- B-bus ($2100-$21FF) ----

    private byte ReadBBus(byte reg)
    {
        if (reg < 0x40) { int v = Ppu.ReadRegister(reg); return v < 0 ? mdr : (byte)v; }
        if (reg < 0x80) return apuToCpu[reg & 3];
        if (reg == 0x80) { byte v = Wram[wramPortAddress]; wramPortAddress = (wramPortAddress + 1) & 0x1FFFF; return v; }
        return mdr;
    }

    private void WriteBBus(byte reg, byte value)
    {
        if (reg < 0x40) { Ppu.WriteRegister(reg, value); return; }
        if (reg < 0x80) { FakeSmpWrite(reg & 3, value); return; }
        switch (reg)
        {
            case 0x80: Wram[wramPortAddress] = value; wramPortAddress = (wramPortAddress + 1) & 0x1FFFF; break;
            case 0x81: wramPortAddress = (wramPortAddress & 0x1FF00) | value; break;
            case 0x82: wramPortAddress = (wramPortAddress & 0x100FF) | (uint)value << 8; break;
            case 0x83: wramPortAddress = (wramPortAddress & 0x0FFFF) | (uint)(value & 1) << 16; break;
        }
    }

    /// <summary>
    /// Stand-in for the SPC700 until APU_SFC exists: echoing each port back is enough for the IPL
    /// upload loop most games run at boot (they write a counter and wait to read it back).
    /// </summary>
    private void FakeSmpWrite(int port, byte value) => apuToCpu[port] = value;

    // ---- CPU I/O ----

    private byte ReadCpuIo(uint offset)
    {
        switch (offset)
        {
            case 0x4210: { byte v = (byte)((nmiFlag ? 0x80 : 0) | (mdr & 0x70) | 0x02); nmiFlag = false; return v; }
            case 0x4211: { byte v = (byte)((irqFlag ? 0x80 : 0) | (mdr & 0x7F)); irqFlag = false; Cpu.SetIrq(false); return v; }
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
                if ((value & 0x30) == 0) { irqFlag = false; Cpu.SetIrq(false); }
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
            case 0x4207: htime = (ushort)((htime & 0x100) | value); break;
            case 0x4208: htime = (ushort)((htime & 0x0FF) | (value & 1) << 8); break;
            case 0x4209: vtime = (ushort)((vtime & 0x100) | value); break;
            case 0x420A: vtime = (ushort)((vtime & 0x0FF) | (value & 1) << 8); break;
            case 0x420B: RunDma(value); break;
            case 0x420C: hdmaen = value; break;
            case 0x420D: memsel = value; break;
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
