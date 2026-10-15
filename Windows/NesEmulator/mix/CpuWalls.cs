using System;
using NesEmulator.Sega;

namespace NesEmulator
{
    /// <summary>
    /// MIX: the Master System's real Z80 (<see cref="Z80{TBus}"/>, the same core the Master System, Game Gear and Genesis run) as a NES CPU (CoreRegistry id "Z80"). It fetches the NES game's 6502 machine code on the NES bus and
    /// decodes it as Z80 instructions: the instruction-set wall, the same one <c>CPU_SM83</c> is on the other side of the Game Boy. Nothing is translated and nothing is handed to another core: the chip really runs the bytes.
    /// <para>Wiring, as an adapter board between the Z80 and the NES would do it: the Z80 starts at the NES reset vector with its stack in the NES stack page; it runs at its stock speed against the NES clock (the Master System's
    /// Z80 is clocked at 3,579,545 Hz, exactly twice the NES CPU, so two T-states are one NES cycle); the NES NMI is the Z80's NMI and the NES IRQ its INT, and where the Z80 would fetch from <c>$0066</c> (NMI) or <c>$0038</c>
    /// (INT, taking <c>RST 38h</c> from the data bus) the board shows a <c>JP</c> to the NES vector (<c>$FFFA</c> / <c>$FFFE</c>), so the interrupt reaches the handler the cartridge holds; the I/O space does not exist on a NES
    /// (reads give $FF, writes are dropped and counted).</para>
    /// <para><b>What it is, honestly:</b> no NES game is Z80 code, so every NES game stops being itself at its first instruction (a few start by accident and draw noise). A program written for the Z80 does run (the verifier
    /// <c>--cpu-wall-test</c> drives the NES screen from one): the core is real, the games are the wall.</para>
    /// </summary>
    public sealed class CPU_Z80 : ICPU
    {
        private readonly Bus bus;
        private readonly Z80<NesZ80Bus> z80;
        private int carry;                 // a T-state left over from the last instruction (two make a NES cycle)
        private int overlayAddr = -1, overlayPos;   // the JP the board shows at an interrupt vector
        private ushort overlayTarget;

        public long InstructionsRun { get; private set; }
        public long Interrupts { get; private set; }
        /// <summary>I/O reads and writes the Z80 made (the NES has no I/O space: reads give $FF, writes are dropped).</summary>
        public long PortReads { get; private set; }
        public long PortWrites { get; private set; }

        public CPU_Z80(Bus bus) { this.bus = bus; z80 = new Z80<NesZ80Bus>(new NesZ80Bus(this)); }

        public string CoreName => "Z80 (Master System CPU)";
        public string Description => "MIX: the Master System's Z80 running NES 6502 code (an instruction-set wall: no NES game is Z80 code)";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";
        public bool IgnoreInvalidOpcodes { get; set; }
        /// <summary>The Z80 itself (tools and the verifier read its registers).</summary>
        public Z80<NesZ80Bus> Inner => z80;

        /// <summary>The adapter's <c>JP nn</c> at an interrupt vector: its three bytes come back in order (the opcode as an M1 fetch, the address as two memory reads); after the third the board stops showing it.</summary>
        private bool Overlay(ushort a, out byte b)
        {
            b = 0;
            if (overlayAddr < 0 || a != overlayAddr + overlayPos) return false;
            b = overlayPos == 0 ? (byte)0xC3 : overlayPos == 1 ? (byte)overlayTarget : (byte)(overlayTarget >> 8);
            if (++overlayPos == 3) overlayAddr = -1;
            return true;
        }
        internal byte Fetch(ushort a) => Overlay(a, out byte b) ? b : bus.Read(a);
        internal byte ReadByte(ushort a) => Overlay(a, out byte b) ? b : bus.Read(a);
        internal void WriteByte(ushort a, byte v) => bus.Write(a, v);
        internal byte In() { PortReads++; return 0xFF; }
        internal void Out() { PortWrites++; }
        // IM 0: the data bus gives RST 38h. NES cores raise an IRQ as a request that stays until the CPU takes it (they rarely drop the line; the APU's $4015 read clears its flag without telling the CPU), a Z80's INT is a level:
        // the adapter holds the request until the acknowledge and then lets go, as the other bridges (CPU_SNES with LatchIrq) do.
        internal byte AckVector() { Arm(0x0038, 0xFFFE); z80.Irq = false; return 0xFF; }

        private void Arm(int vector, ushort nesVector)
        {
            overlayAddr = vector; overlayPos = 0;
            overlayTarget = (ushort)(bus.Read(nesVector) | bus.Read((ushort)(nesVector + 1)) << 8);
            Interrupts++;
        }

        public void Reset()
        {
            z80.Reset();
            z80.PC = (ushort)(bus.Read(0xFFFC) | bus.Read(0xFFFD) << 8);
            z80.SP = 0x01FD;
            carry = 0; overlayAddr = -1;
        }

        public int ExecuteInstruction()
        {
            int t = z80.ExecuteInstruction() + carry;
            InstructionsRun++;
            carry = t & 1;
            return Math.Max(1, t >> 1);
        }

        public void RequestIRQ(bool line) => z80.Irq = line;
        public void RequestNMI() { Arm(0x0066, 0xFFFA); z80.RaiseNmi(); }
        public object GetState() => new CpuSharedState { A = z80.A, X = z80.B, Y = z80.C, status = z80.F, PC = z80.PC, SP = (ushort)(z80.SP & 0xFF) };
        public void SetState(object state) { if (state is CpuSharedState s) { z80.A = s.A; z80.B = s.X; z80.C = s.Y; z80.PC = s.PC; z80.SP = (ushort)(0x0100 | s.SP); } }
        public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => (z80.PC, z80.A, z80.B, z80.C, z80.F, z80.SP);
        public void AddToPC(int delta) => z80.PC = (ushort)(z80.PC + delta);
    }

    /// <summary>The Z80's view of the NES: the 16-bit address bus is the NES bus, the I/O space is empty.</summary>
    public struct NesZ80Bus : IZ80Bus
    {
        private readonly CPU_Z80 owner;
        internal NesZ80Bus(CPU_Z80 owner) { this.owner = owner; }
        public byte FetchOpcode(ushort address) => owner.Fetch(address);
        public byte Read(ushort address) => owner.ReadByte(address);
        public void Write(ushort address, byte value) => owner.WriteByte(address, value);
        public byte In(ushort port) => owner.In();
        public void Out(ushort port, byte value) => owner.Out();
        public byte InterruptVector() => owner.AckVector();
    }

    /// <summary>
    /// MIX: the Genesis's 68000 (<see cref="M68k{TBus}"/>, the core the Genesis runs) as a NES CPU (CoreRegistry id "M68K"). The NES bus is 8 bits and 16 address lines; the board splits each word cycle into two byte cycles
    /// (a byte access is one), keeps the low 16 address bits, and the 68000 starts at the NES reset vector with its supervisor stack in the NES stack page. It runs at its stock speed against the NES clock (the Genesis's 68000 is
    /// clocked at 7,670,454 Hz, 30/7 of the NES CPU's rate). The NES NMI is the 68000's level 7 and the NES IRQ its level 1; the vectors the CPU reads at reset and for an interrupt come from the NES vectors
    /// (<c>$FFFC</c>, <c>$FFFA</c>, <c>$FFFE</c>) in the low word of the 32-bit vector; every other exception vector (address error, illegal instruction, line A, ...) reads the NES RAM at 0 to 255, which a NES game never fills.
    /// <para>An instruction-set wall like <see cref="CPU_Z80"/>: no NES game is 68000 code, and the 68000 needs even addresses for words (an odd one is an address error, which with no handler runs from zero). A 68000 program
    /// does run (<c>--cpu-wall-test</c>).</para>
    /// </summary>
    public sealed class CPU_M68K : ICPU
    {
        private readonly Bus bus;
        private readonly M68k<NesM68kBus> cpu;
        private int carry7;                // 68000 clocks times 7 not yet converted to NES cycles (30 of them make one)
        private bool nmiPending, irqLine;
        private int vecAddr = -1;          // an armed vector overlay: the 8 bytes at vecAddr..vecAddr+3 (two words) read as 0 and the NES vector
        private ushort vecNes;
        private int resetPhase = -1;
        private ushort resetPc, resetSp;

        public long InstructionsRun { get; private set; }
        public long Interrupts { get; private set; }
        public long AddressErrors => cpu.AddressErrorCount;

        public CPU_M68K(Bus bus) { this.bus = bus; cpu = new M68k<NesM68kBus>(new NesM68kBus(this)); cpu.Power(); }

        public string CoreName => "M68K (Genesis CPU)";
        public string Description => "MIX: the Genesis's 68000 running NES 6502 code (an instruction-set wall: no NES game is 68000 code)";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";
        public bool IgnoreInvalidOpcodes { get; set; }
        public M68k<NesM68kBus> Inner => cpu;

        // ---- the bus (called by NesM68kBus)
        internal ushort Read(uint address, bool upper, bool lower)
        {
            ushort a = (ushort)address;
            if (resetPhase >= 0 && a < 8)
            {
                // the RESET exception reads the supervisor stack pointer from vector 0 and the program counter from vector 1; the board answers with the stack page and the NES reset vector
                ushort value = a switch { 0 => 0, 2 => resetSp, 4 => 0, _ => resetPc };
                if (a == 6) resetPhase = -1;
                return value;
            }
            if (vecAddr >= 0 && (a == vecAddr || a == vecAddr + 2))
            {
                ushort value = a == vecAddr ? (ushort)0 : vecNes;
                if (a == vecAddr + 2) vecAddr = -1;
                return value;
            }
            ushort w = 0;
            if (upper) w |= (ushort)(bus.Read(a) << 8);
            if (lower) w |= bus.Read((ushort)(a + 1));
            return w;
        }

        internal void Write(uint address, bool upper, bool lower, ushort data)
        {
            ushort a = (ushort)address;
            if (upper && lower) { bus.Write(a, (byte)(data >> 8)); bus.Write((ushort)(a + 1), (byte)data); }
            else if (upper) bus.Write(a, (byte)(data >> 8));
            else if (lower) bus.Write((ushort)(a + 1), (byte)data);
        }

        internal int Level => nmiPending ? 7 : irqLine ? 1 : 0;

        internal int Acknowledge(int level)
        {
            int vec = 24 + level;
            if (level == 7) nmiPending = false; else irqLine = false;   // the NES IRQ is a latched request (see CPU_Z80.AckVector): let go once taken
            vecAddr = vec * 4;
            ushort at = level == 7 ? (ushort)0xFFFA : (ushort)0xFFFE;
            vecNes = (ushort)(bus.Read(at) | bus.Read((ushort)(at + 1)) << 8);
            Interrupts++;
            return vec;
        }

        public void Reset() => ResetTo((ushort)(bus.Read(0xFFFC) | bus.Read(0xFFFD) << 8), 0x0200);

        /// <summary>The RESET exception with the board answering the two vector reads: the stack pointer and the first instruction's address (also how a CPU swapped in mid-game takes over: the 68000's prefetch queue has to be refilled from the new address).</summary>
        private void ResetTo(ushort pc, ushort sp)
        {
            resetPc = pc; resetSp = sp;
            resetPhase = 0; vecAddr = -1; nmiPending = false; carry7 = 0;
            cpu.Power();
            cpu.Reset();
            resetPhase = -1;
        }

        public int ExecuteInstruction()
        {
            carry7 += cpu.Step() * 7;
            InstructionsRun++;
            int nes = carry7 / 30;
            carry7 %= 30;
            return Math.Max(1, nes);
        }

        public void RequestIRQ(bool line) => irqLine = line;
        public void RequestNMI() => nmiPending = true;
        public object GetState() => new CpuSharedState { A = (byte)cpu.D[0], X = (byte)cpu.D[1], Y = (byte)cpu.D[2], status = (byte)cpu.ReadSr(), PC = (ushort)cpu.Pc, SP = (ushort)(cpu.A[7] & 0xFF) };
        public void SetState(object state)
        {
            if (state is not CpuSharedState s) return;
            ResetTo((ushort)(s.PC & 0xFFFE), (ushort)((0x0100 | s.SP) & 0xFFFE));   // words need even addresses
            cpu.D[0] = s.A; cpu.D[1] = s.X; cpu.D[2] = s.Y;
        }
        public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => ((ushort)cpu.Pc, (byte)cpu.D[0], (byte)cpu.D[1], (byte)cpu.D[2], (byte)cpu.ReadSr(), (ushort)cpu.A[7]);
        public void AddToPC(int delta) => cpu.Pc = (uint)(cpu.Pc + delta);
    }

    /// <summary>The 68000's view of the NES: word cycles become byte cycles on the 8-bit bus, time is counted from the CPU's own step.</summary>
    public struct NesM68kBus : IM68kBus
    {
        private readonly CPU_M68K owner;
        internal NesM68kBus(CPU_M68K owner) { this.owner = owner; }
        public ushort Read(uint address, bool upper, bool lower, int cycles) => owner.Read(address, upper, lower);
        public void Write(uint address, bool upper, bool lower, ushort data, int cycles) => owner.Write(address, upper, lower, data);
        public void Idle(int cycles) { }
        public int InterruptLevel => owner.Level;
        public int Acknowledge(int level, int cycles) => owner.Acknowledge(level);
        public void ResetLine(int cycles) { }
    }
}
