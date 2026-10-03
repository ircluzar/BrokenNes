using System;
using System.IO;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>The Master System / Game Gear / SG-1000 pad (and console buttons) as the board reads them. Bits are "pressed" here; the ports read them active-low.</summary>
[Flags]
public enum SmsButtons : ushort
{
    None = 0,
    Up = 1, Down = 2, Left = 4, Right = 8,
    /// <summary>Button 1 (TL).</summary>
    One = 16,
    /// <summary>Button 2 (TR).</summary>
    Two = 32,
    /// <summary>The Game Gear's Start button (port $00 bit 7).</summary>
    Start = 64,
    /// <summary>The Master System's Pause button: an NMI on the Z80.</summary>
    Pause = 128,
    /// <summary>The Master System's Reset button (readable on port $DD bit 4; it resets nothing by itself).</summary>
    Reset = 256,
}

/// <summary>What the Z80 sees of the board: a thin struct so the generic core calls straight into the board (see <see cref="IZ80Bus"/>).</summary>
public readonly struct SmsBus : IZ80Bus
{
    private readonly BOARD_SMS b;
    public SmsBus(BOARD_SMS board) { b = board; }
    public byte FetchOpcode(ushort address) => b.ReadMemory(address);
    public byte Read(ushort address) => b.ReadMemory(address);
    public void Write(ushort address, byte value) => b.WriteMemory(address, value);
    public byte In(ushort port) => b.ReadPort(port);
    public void Out(ushort port, byte value) => b.WritePort(port, value);
    public byte InterruptVector() => 0xFF;   // the VDP puts nothing on the bus: the Z80 reads $FF (RST $38) in IM 0 and ignores it in IM 1
}

/// <summary>
/// The Master System / Game Gear / SG-1000 mainboard: the Z80 with the console's I/O decode, 8 KB of RAM, the cartridge, the VDP (picture, V/H counters, interrupt line), the sound
/// unit, the two pads and the Game Gear's extra ports. It runs a frame as 262 (NTSC) or 313 (PAL) lines of 228 Z80 cycles, ticking the Z80 one T-state at a time and running the VDP's
/// events at their master-clock positions in between.
/// No BIOS is used or needed: the machine starts in the documented post-boot state (memory control $AB, the VDP registers the BIOS leaves, SP = $DFF0).
/// </summary>
public sealed class BOARD_SMS
{
    public readonly ConsoleKind Console;
    public readonly MasterSystemModel Model;
    public readonly SegaTimeline Timeline;
    public readonly SmsCartridge Cart;
    public readonly SmsVdp Vdp;
    public Z80<SmsBus> Cpu { get; private set; }
    public ISmsSound Sound { get; private set; }
    public readonly byte[] Ram = new byte[0x2000];

    public bool IsGameGear => Model == MasterSystemModel.GameGear;
    public bool IsSg => Model is MasterSystemModel.Sg1000 or MasterSystemModel.Sc3000;
    public SegaRegion Region => Timeline.Region;
    public double FramesPerSecond => Timeline.FramesPerSecond;

    /// <summary>The master-clock offset between the start of a Z80 T-state and the moment the VDP sees a port access made in it.</summary>
    public int IoPhaseMc = 0;
    /// <summary>The master-clock lead at which a VDP event reaches the Z80's interrupt input relative to the start of the T-state it falls in.</summary>
    public int EventPhaseMc = 0;

    // ---- I/O state
    private byte port3E, port3F = 0xFF;
    private bool cartEnabled = true, ramEnabled = true, ioEnabled = true;
    private SmsButtons pad1, pad2;
    private bool pauseHeld;
    private byte ggExtData = 0xFF, ggExtConfig = 0x7F, ggSend, ggSerial;

    private int lineCycle;                 // the Z80 T-state being run, 0..227
    private long soundCycle;               // Z80 cycles the sound unit has been run to
    public long Frames { get; private set; }

    /// <summary>Called at every instruction boundary with the program counter (tracing; a null hook costs nothing).</summary>
    public Action<ushort>? InstructionHook;
    /// <summary>Called for every port access: (is a write, port, value, frame line, Z80 cycle in the line). Tracing; a null hook costs nothing.</summary>
    public Action<bool, ushort, byte, int, int>? PortHook;

    public BOARD_SMS(SmsCartridge cart, ConsoleKind console, MasterSystemModel model, SegaRegion region, int sampleRate,
                     SegaBoardParts<SmsBus, Z80<SmsBus>, SmsVdp, ISmsSound>? parts = null)
    {
        parts ??= SegaBoardParts<SmsBus, Z80<SmsBus>, SmsVdp, ISmsSound>.Stock;
        Console = console; Model = model; Cart = cart;
        Timeline = SegaTimeline.MasterSystem(region, console);
        var revision = model switch
        {
            MasterSystemModel.GameGear => SmsVdpRevision.GameGear,
            MasterSystemModel.Sg1000 or MasterSystemModel.Sc3000 => SmsVdpRevision.Tms9918,
            MasterSystemModel.MarkIII or MasterSystemModel.Sms1 => SmsVdpRevision.Sms1,
            _ => SmsVdpRevision.Sms2,
        };
        Vdp = parts.BuildPpu(() => new SmsVdp(revision, region == SegaRegion.Pal));
        var machine = new SegaMachine(Timeline, GenesisModel.Model1, sampleRate);
        PsgVariant variant = IsSg ? PsgVariant.Ti : PsgVariant.Sega;
        Sound = parts.BuildApu(machine, m => new SmsSound(Timeline.MasterHz / Timeline.Z80.Divider, m.SampleRate, variant));
        Cpu = parts.BuildCpu(new SmsBus(this), bus => new Z80<SmsBus>(bus));
        Reset();
    }

    public void Reset()
    {
        Array.Clear(Ram);
        Cart.Reset();
        Vdp.PowerOn();
        Sound.Reset();
        Cpu.Reset();
        // the registers the BIOS leaves for the cartridge (as the oracle shows them at a cartridge's first instruction): the stack at $DFF0, interrupt mode 1, interrupts disabled
        Cpu.SP = 0xDFF0; Cpu.AF = 0xAB42; Cpu.BC = 0x0002; Cpu.DE = 0xA51B; Cpu.HL = 0x0000; Cpu.InterruptMode = 1; Cpu.R = 29;
        port3E = (byte)(IsGameGear ? 0xA8 : 0xAB);        // BIOS and expansion off, cartridge on (the state after the BIOS has run)
        port3F = 0xFF;
        ApplyMemoryControl();
        pad1 = pad2 = SmsButtons.None; pauseHeld = false;
        ggExtData = 0xFF; ggExtConfig = 0x7F; ggSend = 0; ggSerial = 0;
        lineCycle = 0; soundCycle = 0; Frames = 0; nextLine = StartLine; firstLineCycle = StartCycle;
        if (firstLineCycle < 0) { firstLineCycle += SegaTiming.MasterSystemCpuCyclesPerLine; nextLine = (nextLine + Vdp.TotalLines - 1) % Vdp.TotalLines; }
    }

    private void ApplyMemoryControl()
    {
        cartEnabled = (port3E & 0x40) == 0;
        ramEnabled = (port3E & 0x10) == 0;
        ioEnabled = (port3E & 0x04) == 0;
    }

    // =================================================================================== input

    /// <summary>Sets the buttons held on a pad. Pressing Pause is an NMI on the Z80 (a rising edge).</summary>
    public void SetPad(int player, SmsButtons buttons)
    {
        if (player == 0)
        {
            bool pause = (buttons & SmsButtons.Pause) != 0;
            if (pause && !pauseHeld) Cpu.RaiseNmi();
            pauseHeld = pause;
            pad1 = buttons;
        }
        else pad2 = buttons;
    }

    // =================================================================================== the frame

    private int nextLine;

    /// <summary>The line the machine powers on at (default 0). A tool setting: hardware starts at an arbitrary place in the frame, and a game that depends on it shows up when this is varied.</summary>
    public int StartLine { get; set; }
    /// <summary>The Z80 cycle within <see cref="StartLine"/> the machine powers on at (default 0; may be negative: that many cycles before the line). Tuning aid for the alignment of the picture to the Z80.</summary>
    public int StartCycle { get; set; }
    private int firstLineCycle;

    /// <summary>
    /// Runs one frame: every line, the Z80 a T-state at a time and the VDP at its event times. A frame ends where the picture is complete, as the first blanking line is about to start (line 192
    /// in the 192-line mode), not at the end of the vertical blanking period: the first frame runs lines 0-191, each later one lines 192-261 and 0-191 (192-312 and 0-191 on PAL). The state
    /// a caller sees between frames (RAM, VRAM, the picture) is then the one the oracle reports at the end of its frame, and the work the game does in the blanking period belongs to the frame it prepares.
    /// </summary>
    public void RunFrame()
    {
        int lines = Vdp.TotalLines;
        for (int guard = 0; guard < 2 * lines; guard++)
        {
            int n = nextLine;
            RunLine(n);
            nextLine = n + 1 == lines ? 0 : n + 1;
            if (nextLine == Vdp.ActiveHeight) break;
        }
        SyncSound();
        Frames++;
    }

    private void RunLine(int n)
    {
        Vdp.BeginLine(n);
        Cpu.Irq = Vdp.Irq;
        var cpu = Cpu;
        var vdp = Vdp;
        int first = firstLineCycle; firstLineCycle = 0;   // only the line the machine powers on in starts part-way
        if (InstructionHook == null)
        {
            for (lineCycle = first; lineCycle < SegaTiming.MasterSystemCpuCyclesPerLine; lineCycle++)
            {
                int mc = lineCycle * 3 + EventPhaseMc;
                if (mc >= vdp.NextEventMc) { vdp.RunTo(mc); cpu.Irq = vdp.Irq; }
                cpu.Tick();
            }
        }
        else
        {
            for (lineCycle = first; lineCycle < SegaTiming.MasterSystemCpuCyclesPerLine; lineCycle++)
            {
                int mc = lineCycle * 3 + EventPhaseMc;
                if (mc >= vdp.NextEventMc) { vdp.RunTo(mc); cpu.Irq = vdp.Irq; }
                if (cpu.AtInstructionBoundary) InstructionHook(cpu.PC);
                cpu.Tick();
            }
        }
        vdp.EndLine();
        cpu.Irq = vdp.Irq;
    }

    /// <summary>Replaces the sound unit mid-game (the APU menu); the new unit is brought up to the current time first.</summary>
    public void SwapSound(ISmsSound unit) { SyncSound(); Sound = unit; }

    private void SyncSound()
    {
        long now = Cpu.TotalCycles;
        if (now > soundCycle)
        {
            Sound.Run((int)(now - soundCycle));
            soundCycle = now;
        }
    }

    // =================================================================================== memory

    public byte ReadMemory(ushort address)
    {
        if (address >= 0xC000) return ramEnabled ? Ram[address & 0x1FFF] : (byte)0xFF;
        return cartEnabled ? Cart.Read(address) : (byte)0xFF;
    }

    /// <summary>A read without side effects, for tools.</summary>
    public byte Peek(ushort address) => ReadMemory(address);

    /// <summary>Called for every memory write: (address, value, frame line, Z80 cycle in the line). Tracing; a null hook costs one test per write.</summary>
    public Action<ushort, byte, int, int>? MemWriteHook;

    public void WriteMemory(ushort address, byte value)
    {
        MemWriteHook?.Invoke(address, value, Vdp.Line, lineCycle);
        if (address >= 0xC000 && ramEnabled) Ram[address & 0x1FFF] = value;
        Cart.Write(address, value);
    }

    // =================================================================================== I/O ports

    private int PortMc => lineCycle * 3 + IoPhaseMc;

    public byte ReadPort(ushort port)
    {
        byte v = ReadPortCore(port);
        PortHook?.Invoke(false, port, v, Vdp.Line, lineCycle);
        return v;
    }

    private byte ReadPortCore(ushort port)
    {
        int p = port & 0xFF;
        if (p == 0xF2 && Sound.HasFm) return Sound.ReadAudioControl();   // without the chip, $F2 is a mirror of $DC like any other port
        if (IsGameGear && p < 0x07) return ReadGgPort(p);
        switch (p & 0xC0)
        {
            case 0x00: return 0xFF;
            case 0x40: return (p & 1) == 0 ? Vdp.ReadVCounter(PortMc) : Vdp.ReadHCounter();
            case 0x80:
            {
                byte v;
                if ((p & 1) == 0) v = Vdp.ReadData(PortMc);
                else v = Vdp.ReadStatus(PortMc);
                Cpu.Irq = Vdp.Irq;
                return v;
            }
            default:
                if (!ioEnabled) return 0xFF;
                return (p & 1) == 0 ? ReadDC() : ReadDD();
        }
    }

    public void WritePort(ushort port, byte value)
    {
        PortHook?.Invoke(true, port, value, Vdp.Line, lineCycle);
        int p = port & 0xFF;
        if (p is 0xF0 or 0xF1 or 0xF2)
        {
            SyncSound();
            if (p == 0xF0) Sound.WriteFmAddress(value); else if (p == 0xF1) Sound.WriteFmData(value); else Sound.WriteAudioControl(value);
            return;
        }
        if (IsGameGear && p < 0x07)
        {
            switch (p)
            {
                case 1: ggExtData = value; break;
                case 2: ggExtConfig = value; break;
                case 3: ggSend = value; break;
                case 5: ggSerial = (byte)(value & 0xF8); break;
                case 6: SyncSound(); Sound.WriteStereo(value); break;
            }
            return;
        }
        switch (p & 0xC0)
        {
            case 0x00:
                if ((p & 1) == 0) { port3E = value; ApplyMemoryControl(); }
                else WriteIoControl(value);
                break;
            case 0x40:
                SyncSound();
                Sound.WritePsg(value);
                break;
            case 0x80:
                if ((p & 1) == 0) Vdp.WriteData(value, PortMc);
                else Vdp.WriteControl(value, PortMc);
                Cpu.Irq = Vdp.Irq;
                break;
        }
    }

    // ---- the controller ports ($DC, $DD) and the I/O control port ($3F) ----

    private bool ThA => (port3F & 0x02) == 0 ? (port3F & 0x20) != 0 : true;      // port A TH: the output level when it is an output, else pulled high
    private bool ThB => (port3F & 0x08) == 0 ? (port3F & 0x80) != 0 : true;

    private void WriteIoControl(byte value)
    {
        bool a0 = ThA, b0 = ThB;
        port3F = value;
        // the H counter is captured when a TH line rises
        if ((ThA && !a0) || (ThB && !b0)) Vdp.LatchHCounter(PortMc);
    }

    private static int Bit(SmsButtons b, SmsButtons flag, int bit) => (b & flag) != 0 ? 0 : 1 << bit;

    private byte ReadDC()
    {
        int v = Bit(pad1, SmsButtons.Up, 0) | Bit(pad1, SmsButtons.Down, 1) | Bit(pad1, SmsButtons.Left, 2) | Bit(pad1, SmsButtons.Right, 3)
              | Bit(pad1, SmsButtons.One, 4) | Bit(pad1, SmsButtons.Two, 5)
              | Bit(pad2, SmsButtons.Up, 6) | Bit(pad2, SmsButtons.Down, 7);
        // an output TR pin reads back its level
        if ((port3F & 0x01) == 0) v = (v & ~0x20) | ((port3F & 0x10) != 0 ? 0x20 : 0);
        return (byte)v;
    }

    private byte ReadDD()
    {
        int v = Bit(pad2, SmsButtons.Left, 0) | Bit(pad2, SmsButtons.Right, 1) | Bit(pad2, SmsButtons.One, 2) | Bit(pad2, SmsButtons.Two, 3)
              | Bit(pad1, SmsButtons.Reset, 4) | 0x20;
        if ((port3F & 0x04) == 0) v = (v & ~0x08) | ((port3F & 0x40) != 0 ? 0x08 : 0);
        v |= (ThA ? 0x40 : 0) | (ThB ? 0x80 : 0);
        return (byte)v;
    }

    private byte ReadGgPort(int p) => p switch
    {
        0 => (byte)(0x40 | ((pad1 & SmsButtons.Start) != 0 ? 0x00 : 0x80)),   // bit 7 Start (0 = pressed), bit 6 export console, bit 5 NTSC
        1 => ggExtData,
        2 => ggExtConfig,
        3 => ggSend,
        4 => 0xFF,
        5 => ggSerial,
        _ => 0xFF,
    };

    // =================================================================================== state

    public void SaveState(BinaryWriter w)
    {
        w.Write(Ram); w.Write(port3E); w.Write(port3F); w.Write((ushort)pad1); w.Write((ushort)pad2); w.Write(pauseHeld);
        w.Write(ggExtData); w.Write(ggExtConfig); w.Write(ggSend); w.Write(ggSerial);
        w.Write(soundCycle); w.Write(Frames);
        Cart.SaveState(w); Vdp.SaveState(w); Sound.SaveState(w); Cpu.SaveState(w);
    }

    public void LoadState(BinaryReader r)
    {
        r.Read(Ram, 0, Ram.Length); port3E = r.ReadByte(); port3F = r.ReadByte(); pad1 = (SmsButtons)r.ReadUInt16(); pad2 = (SmsButtons)r.ReadUInt16(); pauseHeld = r.ReadBoolean();
        ggExtData = r.ReadByte(); ggExtConfig = r.ReadByte(); ggSend = r.ReadByte(); ggSerial = r.ReadByte();
        soundCycle = r.ReadInt64(); Frames = r.ReadInt64();
        ApplyMemoryControl();
        Cart.LoadState(r); Vdp.LoadState(r); Sound.LoadState(r); Cpu.LoadState(r);
    }
}
