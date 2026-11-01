using System;
using NesEmulator.Mix;
using NesEmulator.Snes;

namespace NesEmulator;

/// <summary>
/// MIX LAB: a SNES CPU core (any <see cref="ISnesCpuCore"/>, picked by <see cref="MixConfig.SnesCpu"/>) running
/// as a NES CPU. The 65C816 powers up in emulation mode, which is 6502-compatible except that the 6502's
/// unofficial opcodes decode as real 65816 instructions and decimal mode works (the 2A03 has it disabled).
/// Cycle count = one per bus access or idle cycle, so the NES PPU/APU run at the rate this CPU really uses.
/// Discovered by CoreRegistry as CPU id "SNES".
/// </summary>
public sealed class CPU_SNES : ICPU
{
    internal sealed class NesBusView : ISnesBus
    {
        public readonly Bus Bus; public int Cycles;
        public NesBusView(Bus bus) { Bus = bus; }
        // The NES has a 16-bit address bus: bank bits are dropped, so a JML/long access lands in the same 64K.
        public byte Read(uint address) { Cycles++; return Bus.Read((ushort)address); }
        public void Write(uint address, byte value) { Cycles++; Bus.Write((ushort)address, value); }
        public void Idle() { Cycles++; }
    }

    private readonly NesBusView view;
    private readonly ISnesCpuCore cpu;
    private bool started;

    public CPU_SNES(Bus bus)
    {
        view = new NesBusView(bus);
        cpu = SnesCores.CreateCpu(MixConfig.SnesCpu, view);
    }

    public ISnesCpuCore Inner => cpu;
    public string CoreName => $"SNES:{cpu.Id}";
    public string Description => "MIX LAB: a SNES CPU core running NES code in 65C816 emulation mode";
    public int Performance => 0;
    public int Rating => 1;
    public string Category => "Experimental";
    public bool IgnoreInvalidOpcodes { get; set; }
    public long StoppedAtInstruction { get; private set; } = -1;
    public long Instructions { get; private set; }

    public void Reset() { view.Cycles = 0; cpu.Reset(); started = true; }

    public int ExecuteInstruction()
    {
        if (!started) Reset();
        view.Cycles = 0;
        if (cpu.Stopped && StoppedAtInstruction < 0) StoppedAtInstruction = Instructions;
        if (OpLog && oplogged < 12 && !nmiLatched)
        {
            ushort pc = cpu.PC; byte op = view.Bus.cartridge.mapper.CPURead(pc);
            if (!Official[op] && pc >= 0x8000) { oplogged++; Console.Error.WriteLine($"OPLOG instr {Instructions:N0}: ${op:X2} at ${pc:X4} (unofficial on the 6502; a real 65816 instruction here)"); }
        }
        // DIAGNOSIS ONLY (MIX_RESCUE_OPS=1; off by default - it substitutes CPU_FIX for the 65816, a fallback, not an
        // adaptation): an opcode the 6502 treats as unofficial is a different, real instruction on the
        // 65816. Hand that one instruction to a 6502 core, carrying the register file across and back.
        if (RescueOps && cpu.E && !nmiLatched && !(irqLatched && (cpu.P & 0x04) == 0))
        {
            ushort pc = cpu.PC;
            if (pc < 0x2000 || pc >= 0x6000)
            {
                byte op = view.Bus.Read(pc); view.Cycles = 0;
                if (!Official[op])
                {
                    helper ??= new CPU_FIX(view.Bus);
                    helper.SetState(GetState());
                    int cyc = helper.ExecuteInstruction();
                    var s = (CpuSharedState)helper.GetState();
                    cpu.A = s.A; cpu.X = s.X; cpu.Y = s.Y; cpu.P = (byte)(s.status | 0x30); cpu.PC = s.PC; cpu.S = (ushort)(0x0100 | (s.SP & 0xFF));
                    Rescued++; Instructions++;
                    return cyc;
                }
            }
        }
        bool willNmi = nmiLatched, willIrq = !willNmi && irqLatched && (cpu.P & 0x04) == 0 && !cpu.Stopped;
        cpu.Step();
        if (willNmi) nmiLatched = false;
        else if (willIrq && LatchIrq) { irqLatched = false; cpu.SetIrq(false); IrqsTaken++; }
        else if (willIrq) IrqsTaken++;
        Instructions++;
        return view.Cycles > 0 ? view.Cycles : 1;
    }

    // NES cores treat RequestIRQ(true) as a request latched until the CPU takes it: MMC3/MMC5 raise it once and
    // clear their own flag, never calling RequestIRQ(false). A SNES CPU's IRQ input is a level. Raw (default),
    // the line stays high after the first MMC3 IRQ (an IRQ storm). The bridge translates to the NES semantics by
    // default; MIX_IRQ_LATCH=0 restores the raw level.
    public static bool RescueOps = Environment.GetEnvironmentVariable("MIX_RESCUE_OPS") == "1";
    private CPU_FIX? helper;
    public long Rescued { get; private set; }
    public static bool OpLog =Environment.GetEnvironmentVariable("MIX_OPLOG") == "1";
    private int oplogged;
    private static readonly bool[] Official = BuildOfficial();
    private static bool[] BuildOfficial()
    {
        var ok = new bool[256];
        foreach (var o in new byte[] {
            0x00,0x01,0x05,0x06,0x08,0x09,0x0A,0x0D,0x0E,0x10,0x11,0x15,0x16,0x18,0x19,0x1D,0x1E,0x20,0x21,0x24,0x25,0x26,0x28,0x29,0x2A,0x2C,0x2D,0x2E,
            0x30,0x31,0x35,0x36,0x38,0x39,0x3D,0x3E,0x40,0x41,0x45,0x46,0x48,0x49,0x4A,0x4C,0x4D,0x4E,0x50,0x51,0x55,0x56,0x58,0x59,0x5D,0x5E,
            0x60,0x61,0x65,0x66,0x68,0x69,0x6A,0x6C,0x6D,0x6E,0x70,0x71,0x75,0x76,0x78,0x79,0x7D,0x7E,0x81,0x84,0x85,0x86,0x88,0x8A,0x8C,0x8D,0x8E,
            0x90,0x91,0x94,0x95,0x96,0x98,0x99,0x9A,0x9D,0xA0,0xA1,0xA2,0xA4,0xA5,0xA6,0xA8,0xA9,0xAA,0xAC,0xAD,0xAE,0xB0,0xB1,0xB4,0xB5,0xB6,0xB8,0xB9,0xBA,0xBC,0xBD,0xBE,
            0xC0,0xC1,0xC4,0xC5,0xC6,0xC8,0xC9,0xCA,0xCC,0xCD,0xCE,0xD0,0xD1,0xD5,0xD6,0xD8,0xD9,0xDD,0xDE,0xE0,0xE1,0xE4,0xE5,0xE6,0xE8,0xE9,0xEA,0xEC,0xED,0xEE,
            0xF0,0xF1,0xF5,0xF6,0xF8,0xF9,0xFD,0xFE }) ok[o] = true;
        return ok;
    }
    public static bool LatchIrq = Environment.GetEnvironmentVariable("MIX_IRQ_LATCH") != "0";
    private bool irqLatched, nmiLatched;
    public long IrqsTaken { get; private set; }
    public void RequestIRQ(bool line) { irqLatched = line; cpu.SetIrq(line); }
    public void RequestNMI() { nmiLatched = true; cpu.RaiseNmi(); }

    // Hot-swap with the NES cores through their shared state: the 6502 register file maps onto emulation mode.
    public object GetState() => new CpuSharedState
    {
        A = (byte)cpu.A, X = (byte)cpu.X, Y = (byte)cpu.Y, status = cpu.P, PC = cpu.PC, SP = (ushort)(cpu.S & 0xFF),
    };

    public void SetState(object state)
    {
        if (state is not CpuSharedState s) return;
        if (!started) { cpu.Reset(); started = true; }
        cpu.E = true;
        cpu.A = s.A; cpu.X = s.X; cpu.Y = s.Y; cpu.P = (byte)(s.status | 0x30); cpu.PC = s.PC; cpu.S = (ushort)(0x0100 | (s.SP & 0xFF));
    }

    public void AddToPC(int delta) => cpu.PC = (ushort)(cpu.PC + delta);
    public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => (cpu.PC, (byte)cpu.A, (byte)cpu.X, (byte)cpu.Y, cpu.P, (ushort)(cpu.S & 0xFF));
}
