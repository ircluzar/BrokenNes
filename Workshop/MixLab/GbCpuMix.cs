using System;
using System.IO;
using BrokenNes.Workshop.MixLab;
using NesEmulator.Gb;
using NesEmulator.Snes;

namespace BrokenNes.Workshop.MixLab
{
    /// <summary>
    /// MIX LAB: the SNES 65C816 (CPU_SFC, emulation mode = 6502 view) as the CPU of a Game Boy. It fetches the Game
    /// Boy's SM83 machine code and decodes it as 65xx instructions - the "instruction-set wall": expect it to crash
    /// within a handful of instructions. Game Boy interrupts drive its IRQ line; it starts at the cartridge entry $0100.
    /// </summary>
    internal sealed class Cpu65816OnGb : IGbCpu, ISnesBus
    {
        private readonly IGbCpuBus bus;
        private readonly CPU_SFC cpu;
        public long Instructions { get; private set; }
        public string Id => "65816";
        public Cpu65816OnGb(IGbCpuBus bus) { this.bus = bus; cpu = new CPU_SFC(this); }

        // ISnesBus -> Game Boy bus: the bank byte is dropped (the Game Boy has a 16-bit bus).
        public byte Read(uint address) => bus.Read((ushort)address);
        public void Write(uint address, byte value) => bus.Write((ushort)address, value);
        public void Idle() => bus.Idle();

        public void ResetPostBoot(GbModel model, bool cgbGame)
        {
            cpu.Reset();
            cpu.E = true; cpu.PC = 0x0100; cpu.S = 0x01FF; cpu.P = 0x34;
        }
        public void Step()
        {
            cpu.SetIrq(bus.PendingInterrupts != 0);
            cpu.Step(); Instructions++;
        }
        public ushort PC => cpu.PC;
        public bool Halted => false;
        public bool Stopped => cpu.Stopped;
        public bool Locked => cpu.Stopped;
        public void Wake() { }
        public void SaveState(BinaryWriter w) { }
        public void LoadState(BinaryReader r) { }
    }
}

namespace NesEmulator
{
    /// <summary>
    /// MIX LAB: the Game Boy's SM83 as a NES CPU (CoreRegistry id "SM83"). It runs a NES game's 6502 machine code as SM83
    /// instructions on the NES bus - the other side of the instruction-set wall. Cycles are counted per bus access
    /// (one SM83 M-cycle = one NES CPU cycle here, a 1.7x "overclock" of the SM83). NMI -> the SM83's VBlank interrupt
    /// (vector $0040 - NES RAM, where no handler lives), IRQ -> its STAT interrupt. It starts at the NES reset vector.
    /// </summary>
    public sealed class CPU_SM83 : ICPU, IGbCpuBus
    {
        private readonly Bus bus;
        private readonly CPU_GB cpu;
        private int cycles;
        private byte pending;
        public long InstructionsRun { get; private set; }

        public CPU_SM83(Bus bus) { this.bus = bus; cpu = new CPU_GB(this); }

        public string CoreName => "SM83 (Game Boy CPU)";
        public string Description => "MIX LAB: the Game Boy's Sharp SM83 running NES 6502 code";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";
        public bool IgnoreInvalidOpcodes { get; set; }
        public CPU_GB Inner => cpu;

        // IGbCpuBus over the NES bus
        public byte Read(ushort address) { cycles++; return bus.Read(address); }
        public void Write(ushort address, byte value) { cycles++; bus.Write(address, value); }
        public void Idle() { cycles++; }
        public byte PendingInterrupts => pending;
        public void AcknowledgeInterrupt(int bit) => pending &= (byte)~(1 << bit);
        public bool Stop() => false;

        public void Reset()
        {
            cpu.ResetPostBoot(GbModel.Dmg, false);
            cpu.PC = (ushort)(bus.Read(0xFFFC) | bus.Read(0xFFFD) << 8);
            cpu.SP = 0x01FF; cpu.Ime = true;
        }
        public int ExecuteInstruction()
        {
            cycles = 0;
            cpu.Step(); InstructionsRun++;
            return Math.Max(1, cycles);
        }
        public void RequestIRQ(bool line) { if (line) pending |= 0x02; else pending &= 0xFD; }
        public void RequestNMI() => pending |= 0x01;
        public object GetState() => new CpuSharedState { A = cpu.A, X = cpu.B, Y = cpu.C, status = cpu.F, PC = cpu.PC, SP = (ushort)(cpu.SP & 0xFF) };
        public void SetState(object state) { if (state is CpuSharedState s) { cpu.A = s.A; cpu.B = s.X; cpu.C = s.Y; cpu.PC = s.PC; cpu.SP = (ushort)(0x0100 | s.SP); } }
        public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => (cpu.PC, cpu.A, cpu.B, cpu.C, cpu.F, cpu.SP);
        public void AddToPC(int delta) => cpu.PC = (ushort)(cpu.PC + delta);
    }
}
