using System;
using System.IO;
using System.Text;

namespace NesEmulator.Gb;

/// <summary>Joypad bits for <see cref="BOARD_GB.Buttons"/>.</summary>
[Flags]
public enum GbButtons : byte { Right = 1, Left = 2, Up = 4, Down = 8, A = 16, B = 32, Select = 64, Start = 128 }

/// <summary>
/// The Game Boy / Game Boy Color mainboard: memory map, timer, interrupt controller, joypad, serial port,
/// OAM DMA and (CGB) WRAM banking, double speed and VRAM DMA. Owns the CPU, PPU, APU and cartridge; every CPU
/// memory access advances the whole machine by one M-cycle before it happens.
///
/// No boot ROM is used or needed: the machine starts in the documented post-boot state, like skipping the logo.
/// </summary>
public sealed class BOARD_GB : IGbCpuBus
{
    public const double DmgFps = 4194304.0 / 70224;   // ~59.73

    public readonly GbModel Model;
    public readonly GbCartridge Cart;
    public readonly CPU_GB Cpu;
    public readonly PPU_GB Ppu;
    public readonly APU_GB Apu;
    /// <summary>Game Boy Color hardware running a Game Boy Color game (full colour mode).</summary>
    public bool CgbMode => Model == GbModel.Cgb && Cart.SupportsCgb;

    private readonly byte[] wram;
    private readonly byte[] hram = new byte[0x7F];
    private byte ie, iflag = 0xE1;
    private int wramBank = 1;

    public GbButtons Buttons;
    private byte joypSelect = 0x30;
    private byte lastJoypLines = 0x0F;

    /// <summary>Everything the game sent out of the serial port (Blargg's tests print their results here).</summary>
    public readonly StringBuilder SerialOut = new();

    /// <summary>Called before each instruction with the PC it is about to execute (test runners, tracers).</summary>
    public Action<ushort>? InstructionHook;

    public long CycleCount { get; private set; }      // M-cycles since power-on
    public bool DoubleSpeed { get; private set; }

    public BOARD_GB(GbCartridge cart, GbModel model)
    {
        Model = model;
        Cart = cart;
        wram = new byte[model == GbModel.Cgb ? 0x8000 : 0x2000];
        Cpu = new CPU_GB(this);
        Ppu = new PPU_GB(model);
        Apu = new APU_GB(model);
        Ppu.RequestInterrupt = bit =>
        {
            byte mask = (byte)(1 << bit);
            iflag |= mask; irqNew |= mask;
            if (Ppu.InterruptLate) irqLate |= mask;
        };
        Ppu.HBlankStarted = () => { if (hdmaActive) hdmaRequest = true; };
        Reset();
    }

    public void Reset()
    {
        bool cgbGame = Cart.SupportsCgb;
        Cpu.ResetPostBoot(Model, cgbGame);
        Ppu.CompatMode = Model == GbModel.Cgb && !cgbGame;
        Ppu.ResetPostBoot();
        if (Model == GbModel.Dmg && Cart.Rom.Length >= 0x134) Ppu.LoadBootLogo(Cart.Rom.AsSpan(0x104, 48));
        if (Ppu.CompatMode) GbCompatPalettes.Apply(Ppu, Cart);
        Apu.ResetPostBoot();
        Array.Clear(wram); Array.Clear(hram);
        ie = 0; iflag = 0xE1; wramBank = 1; joypSelect = 0x00;
        divCounter = Model == GbModel.Cgb ? (ushort)0x1EA0 : (ushort)0xABC8;
        tima = 0; tma = 0; tac = 0xF8; timaState = 0;
        sb = 0; sc = 0x7E; serialBits = 0; serialCounter = 0;
        dmaActive = false; dmaPendingCycles = 0; dmaIndex = 0; dmaSource = 0; dmaReg = 0xFF;
        hdmaActive = false; hdmaRequest = false; hdmaBlocks = 0; hdmaSrc = 0; hdmaDst = 0;
        key1 = 0; DoubleSpeed = false;
        CycleCount = 0; clockAccumulator = 0;
        SerialOut.Clear();
    }

    // =================================================================================== frame loop
    public long FrameCount => Ppu.FrameCount;

    /// <summary>Run until the PPU finishes a frame (a blank frame's worth of time when the LCD is off).</summary>
    public void RunFrame()
    {
        long target = Ppu.FrameCount + 1;
        int guard = 0;
        while (Ppu.FrameCount < target && guard++ < 2_000_000) StepInstruction();
        Cart.AdvanceClock(clockAccumulator); clockAccumulator = 0;
    }

    public void StepInstruction()
    {
        if (Cpu.Stopped)
        {
            // STOP: the system clock halts until a button is pressed. Keep the frame cadence going for the frontend.
            if ((byte)Buttons != 0) Cpu.Wake();
            else { Idle(); return; }
        }
        InstructionHook?.Invoke(Cpu.PC);
        Cpu.Step();
    }

    // =================================================================================== one M-cycle
    private int clockAccumulator;
    // PPU interrupts raised during the current M-cycle land after the CPU's bus access in that cycle (a read of
    // IF does not see them yet, a write to IF does not clear them); the "late" ones also land after the point
    // where a halted CPU samples IF, so they wake it one M-cycle later. Dispatch after the cycle sees all of them.
    private byte irqNew, irqLate;

    private void Cycle()
    {
        CycleCount++;
        irqNew = 0; irqLate = 0;
        int dots = DoubleSpeed ? 2 : 4;
        clockAccumulator += dots;

        TickTimer();
        TickSerial();
        TickOamDma();
        Ppu.Tick(dots);
        Apu.Tick(dots);
    }

    // IGbCpuBus -----------------------------------------------------------------------------
    public byte Read(ushort address)
    {
        if (hdmaRequest) RunHdmaBlock();
        Cycle();
        return ReadBus(address, cpu: true);
    }

    public void Write(ushort address, byte value)
    {
        if (hdmaRequest) RunHdmaBlock();
        Cycle();
        WriteBus(address, value);
    }

    public void Idle()
    {
        if (hdmaRequest) RunHdmaBlock();
        Cycle();
    }

    public byte PendingInterrupts => (byte)(ie & iflag & ~(Cpu.Halted ? irqLate : 0) & 0x1F);
    public void AcknowledgeInterrupt(int bit) => iflag &= (byte)~(1 << bit);

    public bool Stop()
    {
        divCounter = 0;   // STOP resets DIV
        if (Model == GbModel.Cgb && (key1 & 1) != 0)
        {
            DoubleSpeed = !DoubleSpeed;
            key1 = 0;
            for (int i = 0; i < 2050; i++) Cycle();   // the switch stalls the CPU for ~2050 M-cycles
            return true;
        }
        return false;
    }

    // =================================================================================== memory map
    /// <summary>Read without side effects or time (debuggers, test runners).</summary>
    public byte Peek(ushort a) => ReadBus(a, cpu: false);

    private byte ReadBus(ushort a, bool cpu)
    {
        if (cpu && dmaActive && a < 0xFF80 && a >= 0xFE00 && a < 0xFEA0) return 0xFF;
        switch (a >> 12)
        {
            case 0x0: case 0x1: case 0x2: case 0x3: case 0x4: case 0x5: case 0x6: case 0x7:
                return Cart.ReadRom(a);
            case 0x8: case 0x9: return Ppu.ReadVram(a);
            case 0xA: case 0xB: return Cart.ReadRam(a);
            case 0xC: return wram[a & 0x0FFF];
            case 0xD: return wram[(wramBank << 12) | (a & 0x0FFF)];
            case 0xE: return wram[a & 0x0FFF];
            default:
                if (a < 0xFE00) return wram[(wramBank << 12) | (a & 0x0FFF)];
                if (a < 0xFF00) return dmaActive && cpu ? (byte)0xFF : Ppu.ReadOam(a);
                if (a >= 0xFF80 && a < 0xFFFF) return hram[a - 0xFF80];
                if (a == 0xFFFF) return ie;
                return ReadIo(a & 0xFF);
        }
    }

    private void WriteBus(ushort a, byte v)
    {
        switch (a >> 12)
        {
            case 0x0: case 0x1: case 0x2: case 0x3: case 0x4: case 0x5: case 0x6: case 0x7:
                Cart.WriteRom(a, v); return;
            case 0x8: case 0x9: Ppu.WriteVram(a, v); return;
            case 0xA: case 0xB: Cart.WriteRam(a, v); return;
            case 0xC: wram[a & 0x0FFF] = v; return;
            case 0xD: wram[(wramBank << 12) | (a & 0x0FFF)] = v; return;
            case 0xE: wram[a & 0x0FFF] = v; return;
            default:
                if (a < 0xFE00) { wram[(wramBank << 12) | (a & 0x0FFF)] = v; return; }
                if (a < 0xFF00) { if (!dmaActive) Ppu.WriteOam(a, v); return; }
                if (a >= 0xFF80 && a < 0xFFFF) { hram[a - 0xFF80] = v; return; }
                if (a == 0xFFFF) { ie = v; return; }
                WriteIo(a & 0xFF, v); return;
        }
    }

    private byte ReadIo(int r)
    {
        switch (r)
        {
            case 0x00: return ReadJoypad();
            case 0x01: return sb;
            case 0x02: return (byte)(sc | (Model == GbModel.Cgb ? 0x7C : 0x7E));
            case 0x04: return (byte)(divCounter >> 8);
            case 0x05: return tima;
            case 0x06: return tma;
            case 0x07: return (byte)(tac | 0xF8);
            case 0x0F: return (byte)(iflag & ~irqNew | 0xE0);
            case 0x46: return dmaReg;
            case 0x4D: return Model == GbModel.Cgb ? (byte)(0x7E | (DoubleSpeed ? 0x80 : 0) | (key1 & 1)) : (byte)0xFF;
            case 0x55: return Model == GbModel.Cgb ? (byte)((hdmaActive ? 0 : 0x80) | ((hdmaBlocks - 1) & 0x7F)) : (byte)0xFF;
            case 0x70: return Model == GbModel.Cgb ? (byte)(0xF8 | wramBank) : (byte)0xFF;
        }
        if (r >= 0x10 && r <= 0x3F) return Apu.ReadRegister(r);
        if (r >= 0x40 && r <= 0x4B || r == 0x4F || r >= 0x68 && r <= 0x6C) return Ppu.ReadRegister(r);
        return 0xFF;
    }

    private void WriteIo(int r, byte v)
    {
        switch (r)
        {
            case 0x00: joypSelect = (byte)(v & 0x30); UpdateJoypadIrq(); return;
            case 0x01: sb = v; return;
            case 0x02: sc = (byte)(v & (Model == GbModel.Cgb ? 0x83 : 0x81)); if ((sc & 0x81) == 0x81) { serialBits = 8; serialCounter = 0; } return;
            case 0x04: SetDiv(0); return;
            case 0x05:
                if (timaState == 2) return;            // the reload cycle wins over a TIMA write
                if (timaState == 1) timaState = 0;     // writing during the overflow cycle cancels the reload
                tima = v; return;
            case 0x06: tma = v; if (timaState == 2) tima = v; return;
            case 0x07:
            {
                bool before = TimerInput();
                tac = (byte)(v & 7);
                if (before && !TimerInput()) IncrementTima();
                return;
            }
            case 0x0F: iflag = (byte)(v & 0x1F | irqNew); return;
            case 0x46: dmaReg = v; dmaSource = (ushort)(v << 8); dmaPendingCycles = 2; return;
            case 0x4D: if (Model == GbModel.Cgb) key1 = (byte)(v & 1); return;
            case 0x51: hdmaSrc = (ushort)((hdmaSrc & 0x00F0) | v << 8); return;
            case 0x52: hdmaSrc = (ushort)((hdmaSrc & 0xFF00) | (v & 0xF0)); return;
            case 0x53: hdmaDst = (ushort)((hdmaDst & 0x00F0) | (v & 0x1F) << 8); return;
            case 0x54: hdmaDst = (ushort)((hdmaDst & 0x1F00) | (v & 0xF0)); return;
            case 0x55: if (Model == GbModel.Cgb) StartVramDma(v); return;
            case 0x70: if (Model == GbModel.Cgb) { wramBank = v & 7; if (wramBank == 0) wramBank = 1; } return;
        }
        if (r >= 0x10 && r <= 0x3F) { Apu.WriteRegister(r, v); return; }
        if (r >= 0x40 && r <= 0x4B || r == 0x4F || r >= 0x68 && r <= 0x6C) Ppu.WriteRegister(r, v);
    }

    // =================================================================================== joypad
    private byte ReadJoypad()
    {
        byte lines = JoypadLines();
        return (byte)(0xC0 | joypSelect | lines);
    }

    private byte JoypadLines()
    {
        int b = (byte)Buttons, lines = 0x0F;
        if ((joypSelect & 0x10) == 0) lines &= ~(b & 0x0F);          // direction keys
        if ((joypSelect & 0x20) == 0) lines &= ~(b >> 4 & 0x0F);      // action buttons
        return (byte)lines;
    }

    /// <summary>A line going low raises the joypad interrupt. Call after changing <see cref="Buttons"/>.</summary>
    public void UpdateJoypadIrq()
    {
        byte lines = JoypadLines();
        if ((lastJoypLines & ~lines & 0x0F) != 0) iflag |= 0x10;
        lastJoypLines = lines;
    }

    // =================================================================================== timer
    private ushort divCounter;        // the 16-bit counter whose upper byte is DIV
    private byte tima, tma, tac;
    private int timaState;            // 0 normal, 1 overflowed (reads 00, reload next cycle), 2 reload cycle

    private int TimerBit => (tac & 3) switch { 0 => 9, 1 => 3, 2 => 5, _ => 7 };
    private bool TimerInput() => (tac & 4) != 0 && (divCounter >> TimerBit & 1) != 0;
    private int ApuBit => DoubleSpeed ? 13 : 12;

    private void TickTimer()
    {
        if (timaState == 1) { tima = tma; iflag |= 0x04; timaState = 2; }
        else if (timaState == 2) timaState = 0;
        SetDiv((ushort)(divCounter + 4));
    }

    private void SetDiv(ushort value)
    {
        bool before = TimerInput();
        bool apuBefore = (divCounter >> ApuBit & 1) != 0;
        divCounter = value;
        if (before && !TimerInput()) IncrementTima();
        if (apuBefore && (divCounter >> ApuBit & 1) == 0) Apu.FrameSequencerStep();
    }

    private void IncrementTima()
    {
        tima++;
        if (tima == 0) timaState = 1;
    }

    // =================================================================================== serial
    private byte sb, sc;
    private int serialBits, serialCounter;

    private void TickSerial()
    {
        if (serialBits == 0 || (sc & 1) == 0) return;       // external clock with no partner never finishes
        int cyclesPerBit = (sc & 2) != 0 && Model == GbModel.Cgb ? 4 : 128;   // 8192 Hz (or 262144 Hz CGB fast)
        if (++serialCounter < cyclesPerBit) return;
        serialCounter = 0;
        if (serialBits == 8) SerialOut.Append((char)sb);
        sb = (byte)(sb << 1 | 1);                            // nothing connected: 1s shift in
        if (--serialBits == 0) { sc &= 0x7F; iflag |= 0x08; }
    }

    // =================================================================================== OAM DMA
    private bool dmaActive;
    private int dmaPendingCycles, dmaIndex;
    private ushort dmaSource;
    private byte dmaReg;

    private void TickOamDma()
    {
        if (dmaActive)
        {
            ushort src = (ushort)(dmaSource + dmaIndex);
            if (src >= 0xE000) src -= 0x2000;                  // $E0-$FF sources read the WRAM echo
            Ppu.Oam[dmaIndex] = ReadBus(src, cpu: false);
            if (++dmaIndex == 0xA0) dmaActive = false;
        }
        if (dmaPendingCycles > 0 && --dmaPendingCycles == 0)
        {
            dmaActive = true; dmaIndex = 0;
        }
    }

    // =================================================================================== CGB VRAM DMA
    private byte key1;
    private bool hdmaActive, hdmaRequest;
    private int hdmaBlocks;
    private ushort hdmaSrc, hdmaDst;

    private void StartVramDma(byte v)
    {
        if (hdmaActive && (v & 0x80) == 0) { hdmaActive = false; return; }   // cancel a running HBlank DMA
        hdmaBlocks = (v & 0x7F) + 1;
        if ((v & 0x80) != 0)
        {
            hdmaActive = true;
            if (Ppu.LcdOn && Ppu.Mode == 0) hdmaRequest = true;
        }
        else
        {
            while (hdmaBlocks > 0) CopyVramBlock();
        }
    }

    private void RunHdmaBlock()
    {
        hdmaRequest = false;
        if (!hdmaActive || Cpu.Halted) return;
        CopyVramBlock();
        if (hdmaBlocks == 0) hdmaActive = false;
    }

    private void CopyVramBlock()
    {
        for (int i = 0; i < 16; i++)
        {
            Ppu.DmaWriteVram(hdmaDst, ReadBus(hdmaSrc, cpu: false));
            hdmaSrc++; hdmaDst = (ushort)((hdmaDst + 1) & 0x1FFF);
        }
        hdmaBlocks--;
        for (int i = 0; i < (DoubleSpeed ? 16 : 8); i++) Cycle();   // the CPU is stalled while a block copies
        if (hdmaDst == 0) { hdmaBlocks = 0; }
    }

    // =================================================================================== battery + state
    public bool HasBattery => Cart.HasBattery;

    /// <summary>Battery file contents: cartridge RAM, plus the 48-byte RTC trailer for MBC3+RTC carts.</summary>
    public byte[] ExportSave()
    {
        var rtc = Cart.Rtc;
        if (rtc == null) return (byte[])Cart.Ram.Clone();
        var trailer = rtc.ExportTrailer(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var b = new byte[Cart.Ram.Length + trailer.Length];
        Cart.Ram.CopyTo(b, 0); trailer.CopyTo(b, Cart.Ram.Length);
        return b;
    }

    public void ImportSave(byte[] data)
    {
        Array.Copy(data, Cart.Ram, Math.Min(data.Length, Cart.Ram.Length));
        if (Cart.Rtc != null && data.Length >= Cart.Ram.Length + 44)
            Cart.Rtc.ImportTrailer(data, Cart.Ram.Length, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public byte[] SaveState()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write("GBST"u8); w.Write(1);
            Cpu.SaveState(w); Ppu.SaveState(w); Apu.SaveState(w); Cart.SaveState(w);
            w.Write(wram); w.Write(hram); w.Write(ie); w.Write(iflag); w.Write(wramBank); w.Write(joypSelect);
            w.Write(divCounter); w.Write(tima); w.Write(tma); w.Write(tac); w.Write(timaState);
            w.Write(sb); w.Write(sc); w.Write(serialBits); w.Write(serialCounter);
            w.Write(dmaActive); w.Write(dmaPendingCycles); w.Write(dmaIndex); w.Write(dmaSource); w.Write(dmaReg);
            w.Write(key1); w.Write(hdmaActive); w.Write(hdmaRequest); w.Write(hdmaBlocks); w.Write(hdmaSrc); w.Write(hdmaDst);
            w.Write(DoubleSpeed); w.Write(CycleCount);
        }
        return ms.ToArray();
    }

    public void LoadState(byte[] state)
    {
        using var r = new BinaryReader(new MemoryStream(state));
        if (r.ReadUInt32() != 0x54534247 || r.ReadInt32() != 1) throw new InvalidDataException("Not a BrokenNes Game Boy state.");
        Cpu.LoadState(r); Ppu.LoadState(r); Apu.LoadState(r); Cart.LoadState(r);
        r.ReadBytes(wram.Length).CopyTo(wram, 0); r.ReadBytes(hram.Length).CopyTo(hram, 0);
        ie = r.ReadByte(); iflag = r.ReadByte(); wramBank = r.ReadInt32(); joypSelect = r.ReadByte();
        divCounter = r.ReadUInt16(); tima = r.ReadByte(); tma = r.ReadByte(); tac = r.ReadByte(); timaState = r.ReadInt32();
        sb = r.ReadByte(); sc = r.ReadByte(); serialBits = r.ReadInt32(); serialCounter = r.ReadInt32();
        dmaActive = r.ReadBoolean(); dmaPendingCycles = r.ReadInt32(); dmaIndex = r.ReadInt32(); dmaSource = r.ReadUInt16(); dmaReg = r.ReadByte();
        key1 = r.ReadByte(); hdmaActive = r.ReadBoolean(); hdmaRequest = r.ReadBoolean(); hdmaBlocks = r.ReadInt32(); hdmaSrc = r.ReadUInt16(); hdmaDst = r.ReadUInt16();
        DoubleSpeed = r.ReadBoolean(); CycleCount = r.ReadInt64();
    }
}

/// <summary>
/// Colours for a DMG game on Game Boy Color hardware. The real CGB boot ROM picks from a table keyed on the
/// title checksum; without a boot ROM we apply its default greyscale-green set. (The per-game table is a TODO.)
/// </summary>
public static class GbCompatPalettes
{
    public static void Apply(PPU_GB ppu, GbCartridge cart)
    {
        ushort[] bg = { 0x7FFF, 0x5294, 0x294A, 0x0000 };
        ppu.SetCompatPalettes(bg, bg, bg);
    }
}
