using System;
using System.IO;
using NesEmulator.Mix;
using NesEmulator.Gb;
using NesEmulator.Snes;

namespace NesEmulator.Mix
{
    /// <summary>
    /// MIX LAB: the SNES 65C816 (CPU_SFC, emulation mode = 6502 view) as the CPU of a Game Boy. It fetches the Game
    /// Boy's SM83 machine code and decodes it as 65xx instructions - the "instruction-set wall".
    /// Wiring, as an adapter board would do it: it starts at the cartridge entry $0100 in emulation mode (stack page
    /// $01xx - on a Game Boy that is ROM, so stray pushes become harmless MBC writes; a native-mode stack in HRAM
    /// runs down through the I/O registers and switches the LCD off). Game Boy interrupts drive its IRQ line, and the
    /// vector pull (the 65816's VPB pin) is answered with the Game Boy's own handler address $0040 + 8n,
    /// acknowledging that IF bit, instead of $FFFE/$FFFF (HRAM and IE). Truthful result: most games start with $00 at
    /// $0100 - BRK on a 65816 - so the boot logo is as far as it gets.
    /// </summary>
    internal sealed class Cpu65816OnGb : IGbCpu, ISnesBus
    {
        private readonly IGbCpuBus bus;
        private readonly CPU_SFC cpu;
        public long Instructions { get; private set; }
        public string Id => "65816";
        public Cpu65816OnGb(IGbCpuBus bus) { this.bus = bus; cpu = new CPU_SFC(this); }

        // ISnesBus -> Game Boy bus: the bank byte is dropped (the Game Boy has a 16-bit bus).
        public byte Read(uint address)
        {
            if (vector >= 0)
            {
                ushort a = (ushort)address;   // in a dispatch step the only reads are the vector pull
                if (a == 0xFFEE || a == 0xFFFE) { bus.Idle(); return (byte)vector; }
                if (a == 0xFFEF || a == 0xFFFF) { bus.Idle(); return (byte)(vector >> 8); }
            }
            return bus.Read((ushort)address);
        }
        private int vector = -1;
        public long Interrupts { get; private set; }
        public void Write(uint address, byte value)
        {
            if (Trace && traced < 40 && (ushort)address >= 0xFF00 && (ushort)address < 0xFF80) { traced++; Console.Error.WriteLine($"65816 W ${(ushort)address:X4}={value:X2} PC=${cpu.PC:X4} E={cpu.E} S=${cpu.S:X4} instr={Instructions}"); }
            bus.Write((ushort)address, value);
        }
        private static readonly bool Trace = Environment.GetEnvironmentVariable("MIX_GB65_TRACE") == "1";
        private int traced;
        public void Idle() => bus.Idle();

        public void ResetPostBoot(GbModel model, bool cgbGame)
        {
            cpu.Reset();
            cpu.E = true; cpu.PC = 0x0100; cpu.S = 0x01FF; cpu.P = 0x34;
        }
        public void Step()
        {
            byte pending = bus.PendingInterrupts;
            cpu.SetIrq(pending != 0);
            if (pending != 0 && (cpu.P & 0x04) == 0 && !cpu.Stopped)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(pending);
                bus.AcknowledgeInterrupt(bit);
                vector = 0x40 + 8 * bit; Interrupts++;
            }
            try { cpu.Step(); } finally { vector = -1; }
            Instructions++;
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
    /// (vector $0040), IRQ -> its STAT interrupt ($0048). It starts at the NES reset vector.
    /// Wiring, as an adapter board would do it: a dispatch continues through the NES's own vectors ($FFFA for NMI,
    /// $FFFE for IRQ - a JP (vector) the adapter shows at $0040/$0048). STOP wakes on the adapter's interrupt lines
    /// (a Game Boy wakes on its joypad lines; a NES has nothing wired there). The SM83's eleven lock-up opcodes -
    /// CMP/CPX/SBC on a 6502, so they come up constantly - act as one-byte NOPs instead of freezing the CPU.
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
        public void AcknowledgeInterrupt(int bit) { pending &= (byte)~(1 << bit); taken = bit; }
        private int taken = -1;
        public long Interrupts { get; private set; }
        public long Unlocks { get; private set; }
        public bool Stop() => false;

        public void Reset()
        {
            cpu.ResetPostBoot(GbModel.Dmg, false);
            cpu.PC = (ushort)(bus.Read(0xFFFC) | bus.Read(0xFFFD) << 8);
            cpu.SP = 0x01FF; cpu.Ime = true;
        }
        public int ExecuteInstruction()
        {
            cycles = 0; taken = -1;
            if (cpu.Stopped && pending != 0) cpu.Wake();
            cpu.Step(); InstructionsRun++;
            if (cpu.Locked) { cpu.Unlock(); Unlocks++; }
            if (taken >= 0)
            {
                ushort v = taken == 0 ? (ushort)0xFFFA : (ushort)0xFFFE;
                Idle();   // JP (vector): opcode fetch, two vector bytes, jump
                cpu.PC = (ushort)(Read(v) | Read((ushort)(v + 1)) << 8);
                Idle();
                Interrupts++;
            }
            return Math.Max(1, cycles);
        }
        public void RequestIRQ(bool line) { if (line) pending |= 0x02; else pending &= 0xFD; }
        public void RequestNMI() => pending |= 0x01;
        // Hot-swap with the NES cores: A/B/C carry the 6502's A/X/Y. A savestate also carries the SM83's whole state (F, D, E, H, L, IME,
        // EI delay, HALT/STOP, the HALT bug) and the pending NES interrupt lines, so a state saved on this core reloads exactly
        // (NES.LoadState hands SetState a JsonElement, which used to be ignored: the registers stayed the pre-load ones, and after a
        // Reset IME stayed on).
        public sealed class Sm83NesState : CpuSharedState
        {
            public string? sm83 { get; set; }
            public byte pendingLines { get; set; }
        }
        public object GetState()
        {
            using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms)) cpu.SaveState(w);
            return new Sm83NesState { A = cpu.A, X = cpu.B, Y = cpu.C, status = cpu.F, PC = cpu.PC, SP = (ushort)(cpu.SP & 0xFF),
                irqRequested = (pending & 0x02) != 0, nmiRequested = (pending & 0x01) != 0, sm83 = Convert.ToBase64String(ms.ToArray()), pendingLines = pending };
        }
        public void SetState(object state)
        {
            if (state is Sm83NesState own && own.sm83 != null) { LoadOwn(own.sm83, own.pendingLines); return; }
            if (state is System.Text.Json.JsonElement je)
            {
                if (je.TryGetProperty("sm83", out var jb) && jb.ValueKind == System.Text.Json.JsonValueKind.String)
                { LoadOwn(jb.GetString()!, je.TryGetProperty("pendingLines", out var jp) && jp.ValueKind == System.Text.Json.JsonValueKind.Number ? (byte)jp.GetInt32() : (byte)0); return; }
                // another core's state (a 6502 save loaded on this core): its register file
                int I(string n, int d) => je.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : d;
                bool B(string n) => je.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
                state = new CpuSharedState { A = (byte)I("A", 0), X = (byte)I("X", 0), Y = (byte)I("Y", 0), status = (byte)I("status", 0), PC = (ushort)I("PC", 0), SP = (ushort)I("SP", 0xFD), irqRequested = B("irqRequested"), nmiRequested = B("nmiRequested") };
            }
            if (state is CpuSharedState s)
            {
                cpu.A = s.A; cpu.B = s.X; cpu.C = s.Y; cpu.PC = s.PC; cpu.SP = (ushort)(0x0100 | s.SP);
                if (s.irqRequested) pending |= 0x02; else pending &= 0xFD;
                if (s.nmiRequested) pending |= 0x01;
            }
        }
        private void LoadOwn(string b64, byte lines)
        {
            using var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(b64)));
            cpu.LoadState(r); pending = lines;
        }
        public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => (cpu.PC, cpu.A, cpu.B, cpu.C, cpu.F, cpu.SP);
        public void AddToPC(int delta) => cpu.PC = (ushort)(cpu.PC + delta);
    }
}
