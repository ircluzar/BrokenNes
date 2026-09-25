using System.Runtime.CompilerServices;
namespace NesEmulator
{
// FIX family: the exclusive target for ongoing hardware-accuracy work (see
// project_fix_core_family memory). Started as an exact duplicate of CPU_FMC, the "standard
// control component" baseline - every other named core is either a speed/perf tradeoff or an
// intentional gimmick/personality core (see project_ppu_core_personalities memory) and is
// frozen going forward. Accuracy fixes land here, not on CPU_FMC or any other named core.
public class CPU_FIX : ICPU {
	// Metadata defaults
	public string CoreName => "Fix";
	public string Description => "Hardware-accuracy target core, forked from the Famiclone (FMC) baseline. All ongoing 6502 accuracy work lands here; the other named cores are frozen.";
	public int Performance => 0;
	public int Rating => 3;
	public string Category => "Accuracy";
	public byte A, X, Y;
	public ushort PC, SP;
	public byte status; //Flags (P)

	private const int FLAG_C = 0; //Carry
	private const int FLAG_Z = 1; //Zero
	private const int FLAG_I = 2; //Interrupt
	private const int FLAG_D = 3; //Decimal Mode (Unused in NES)
	private const int FLAG_B = 4; //Break Command
	private const int FLAG_UNUSED = 5; //Used bit 5 (always set)
	private const int FLAG_V = 6; //Overflow
	private const int FLAG_N = 7; //Negative

	private Bus bus; // concrete type to avoid interface dispatch cost

	private bool irqRequested;
	private bool nmiRequested;

	// Mirrors the real 6502's internal IRQ-gate poll latch. On hardware, the poll checkpoint
	// that decides whether a pending IRQ becomes a vectored interrupt sits at a fixed point
	// inside an instruction's cycle sequence. For CLI/SEI/PLP that checkpoint falls one cycle
	// *before* the instruction's own flag-store cycle, so the poll still sees the pre-instruction
	// I flag for one more instruction after the flag write - pollFlagI captures exactly that
	// stale-for-one-instruction value. Every other instruction (including RTI, whose flag pull
	// happens before its own checkpoint) syncs pollFlagI to the live status flag immediately.
	private bool pollFlagI;

	// When true, unknown opcodes are treated as 2-cycle NOPs instead of throwing CpuCrashException
	public bool IgnoreInvalidOpcodes { get; set; } = false;

	public CPU_FIX(Bus bus) {
		A = X = Y = 0;
		PC = 0x0000;
		SP = 0x0000;
		status = 0;

		this.bus = bus;

		irqRequested = false;
		nmiRequested = false;
		pollFlagI = GetFlag(FLAG_I);
	}

	public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => (PC, A, X, Y, status, SP);
	public void AddToPC(int delta) { PC = (ushort)(PC + delta); }

	public void Reset() {
		A = X = Y = 0;
		SP = 0xFD;
		status = 0x24;

		byte low = bus.Read(0xFFFC);
		byte high = bus.Read(0xFFFD);
		PC = (ushort)((high << 8) | low);

		pollFlagI = GetFlag(FLAG_I);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void SetFlag(int bit, bool value) {
		byte mask = (byte)(1 << bit);
		if (value) status |= mask; else status &= (byte)~mask;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool GetFlag(int bit) { return (status & (1 << bit)) != 0; }

	public void SetZN(byte value) {
		SetFlag(FLAG_Z, value == 0); //Zero
		SetFlag(FLAG_N, (value & 0x80) != 0); //Negative
	}

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	private byte Fetch() { return bus.Read(PC++); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ushort Fetch16Bits() { byte low = Fetch(); byte high = Fetch(); return (ushort)((high << 8) | low); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	// The 6502 polls for an interrupt at the end of an instruction's SECOND-TO-LAST cycle, so an IRQ
	// asserted during the last cycle is only taken after one more instruction. Modelled wherever the
	// bus knows which cycle of the instruction the PPU/APU are on (precise stepping - always, for
	// PPU_FIX; see Bus.PreciseInterruptPhase), otherwise IRQs are taken at the next instruction as
	// before. Measured on Mega Man 3's MMC3-timed stage-select split: with the counter clocked at dot
	// 261 (the A12 rise of the first sprite-pattern fetch) this puts all five mid-line writes on Mesen
	// 2.1.1's exact dots; without it they land 6 dots (2 CPU cycles) early. Deliberately NOT applied
	// to NMI: tried and measured, it took Lifeforce's NMI landing points from 40/57 matching Mesen to
	// 2/57 - the NMI edge is sampled differently.
	public void RequestIRQ(bool line)
	{
		if (line && !irqRequested)
		{
			int ph = bus.PreciseInterruptPhase; // -1: not precise; MaxValue: raised after the instruction's last access
			if (ph == int.MaxValue) irqDeferOne = true; else irqRaisedAtAccess = ph;
		}
		irqRequested = line;
	}
	private int irqRaisedAtAccess = -1;
	private bool irqDeferOne;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void RequestNMI()
	{
		nmiRequested = true;
	}

	public int ExecuteInstruction() {
		if (nmiRequested) {
			nmiRequested = false;
			int nmiCycles = NMI();
			// NMI's own flag-set (like IRQ's) is immediately visible to the poll checkpoint -
			// only CLI/SEI/PLP get a one-instruction-delayed poll, per AccuracyCoin's model.
			pollFlagI = GetFlag(FLAG_I);
			InstructionTracer.OnInterrupt(1, (byte)nmiCycles);
			return nmiCycles;
		}

		// A deferral covers exactly one instruction boundary - this one - whether or not the IRQ is
		// unmasked here, so a masked IRQ cannot leave it armed against a later one.
		bool irqDeferred = irqDeferOne; irqDeferOne = false;
		if (!pollFlagI && irqRequested && !irqDeferred) {
			irqRequested = false;
			int irqCycles = IRQ();
			pollFlagI = GetFlag(FLAG_I);
			InstructionTracer.OnInterrupt(2, (byte)irqCycles);
			return irqCycles;
		}

		irqRaisedAtAccess = -1;
		byte opcode = Fetch();
		ushort instructionPC = (ushort)(PC - 1);
		byte preA = A, preX = X, preY = Y, preSP = (byte)SP, preStatus = status;
		bool iBefore = GetFlag(FLAG_I);

		int cycles = Dispatch(opcode);
		if (irqRequested && irqRaisedAtAccess >= 0 && irqRaisedAtAccess >= cycles - 1) irqDeferOne = true;

		InstructionTracer.OnInstruction(instructionPC, opcode, preA, preX, preY, preSP, preStatus, (byte)cycles);

		// CLI (0x58), SEI (0x78) and PLP (0x28) write the I flag one cycle *after* the poll
		// checkpoint that gates the next IRQ, so the poll keeps seeing the pre-instruction value
		// for exactly one more instruction. Every other instruction (RTI included) syncs
		// immediately.
		pollFlagI = (opcode == 0x58 || opcode == 0x78 || opcode == 0x28) ? iBefore : GetFlag(FLAG_I);

		return cycles;
	}

	private int Dispatch(byte opcode) {
			switch (opcode) {
			// === Unofficial NOP family (multi-byte safe no-ops) ===
			// Implied 2-cycle NOPs
			case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: return 2; // single byte implied
			// Immediate 2-byte NOPs: the operand byte is genuinely fetched off the bus (that's
			// the real cycle-2 access), just discarded rather than used.
			case 0x80: case 0x82: case 0x89: case 0xC2: case 0xE2: { Fetch(); return 2; }
			// ZeroPage variants: real hardware actually reads the target address (that's what
			// lets e.g. "NOP $2002" clear PPUSTATUS's VBlank bit) - it's a genuine dummy read,
			// not just a cycle to burn.
			case 0x04: case 0x44: case 0x64: { var ar = ZeroPage(); bus.Read(ar.address); return 3; }
			// ZeroPage,X variants (ZeroPageX() already performs the un-indexed dummy read; this
			// is the real read of the indexed target address).
			case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4: { var ar = ZeroPageX(); bus.Read(ar.address); return 4; }
			// Absolute (16-bit operand) - genuine read of the target address.
			case 0x0C: { var ar = Absolute(); bus.Read(ar.address); return 4; }
			// Absolute,X (adds page cross penalty like regular AbsoluteX addressing; AbsoluteX()
			// already performs the wrong-page dummy read on an actual cross, this is the real
			// read of the final target address).
			case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: { var ar = AbsoluteX(); bus.Read(ar.address); return 4 + ar.extraCycles; }
			//BRK, NOP, RTI
			case 0x00: return BRK();
			case 0xEA: return NOP();
			case 0x40: return RTI();
			
			//LDA, LDX, LDY, STA, STX, STY
			case 0xA9: return LDR(ref A, Immediate, 2);
			case 0xA5: return LDR(ref A, ZeroPage, 3);
			case 0xB5: return LDR(ref A, ZeroPageX, 4);
			case 0xAD: return LDR(ref A, Absolute, 4);
			case 0xBD: return LDR(ref A, AbsoluteX, 4);
			case 0xB9: return LDR(ref A, AbsoluteY, 4);
			case 0xA1: return LDR(ref A, IndirectX, 6);
			case 0xB1: return LDR(ref A, IndirectY, 5);
			case 0xA2: return LDR(ref X, Immediate, 2);
			case 0xA6: return LDR(ref X, ZeroPage, 3);      
			case 0xB6: return LDR(ref X, ZeroPageY, 4);
			case 0xAE: return LDR(ref X, Absolute, 4);
			case 0xBE: return LDR(ref X, AbsoluteY, 4);
			case 0xA0: return LDR(ref Y, Immediate, 2);
			case 0xA4: return LDR(ref Y, ZeroPage, 3);      
			case 0xB4: return LDR(ref Y, ZeroPageX, 4);
			case 0xAC: return LDR(ref Y, Absolute, 4);
			case 0xBC: return LDR(ref Y, AbsoluteX, 4);
			case 0x85: return STR(ref A, ZeroPage, 3);
			case 0x95: return STR(ref A, ZeroPageX, 4);
			case 0x8D: return STR(ref A, Absolute, 4);
			case 0x9D: return STR(ref A, AbsoluteXStore, 5);
			case 0x99: return STR(ref A, AbsoluteYStore, 5);
			case 0x81: return STR(ref A, IndirectX, 6);
			case 0x91: return STR(ref A, IndirectYStore, 6);
			case 0x86: return STR(ref X, ZeroPage, 3);
			case 0x96: return STR(ref X, ZeroPageY, 4);
			case 0x8E: return STR(ref X, Absolute, 4);
			case 0x84: return STR(ref Y, ZeroPage, 3);
			case 0x94: return STR(ref Y, ZeroPageX, 4);
			case 0x8C: return STR(ref Y, Absolute, 4);
			
			//TAX, TAY, TXA, TYA
			case 0xAA: return TRR(ref X, ref A, Implied, 2);
			case 0xA8: return TRR(ref Y, ref A, Implied, 2);
			case 0x8A: return TRR(ref A, ref X, Implied, 2);
			case 0x98: return TRR(ref A, ref Y, Implied, 2);

			//TSX, TXS, PHA, PHP, PLA, PLP
			case 0xBA: return TSX(Implied, 2);
			case 0x9A: return TXS(Implied, 2);
			case 0x48: return PHA(Implied, 3);
			case 0x08: return PHP(Implied, 3);
			case 0x68: return PLA(Implied, 4);
			case 0x28: return PLP(Implied, 4);

			//AND, EOR, ORA, BIT
			case 0x29: return AND(Immediate, 2);
			case 0x25: return AND(ZeroPage, 3);
			case 0x35: return AND(ZeroPageX, 4);
			case 0x2D: return AND(Absolute, 4);
			case 0x3D: return AND(AbsoluteX, 4);
			case 0x39: return AND(AbsoluteY, 4);
			case 0x21: return AND(IndirectX, 6);
			case 0x31: return AND(IndirectY, 5);
			case 0x49: return EOR(Immediate, 2);
			case 0x45: return EOR(ZeroPage, 3);
			case 0x55: return EOR(ZeroPageX, 4);
			case 0x4D: return EOR(Absolute, 4);
			case 0x5D: return EOR(AbsoluteX, 4);
			case 0x59: return EOR(AbsoluteY, 4);
			case 0x41: return EOR(IndirectX, 6);
			case 0x51: return EOR(IndirectY, 5);
			case 0x09: return ORA(Immediate, 2);
			case 0x05: return ORA(ZeroPage, 3);
			case 0x15: return ORA(ZeroPageX, 4);
			case 0x0D: return ORA(Absolute, 4);
			case 0x1D: return ORA(AbsoluteX, 4);
			case 0x19: return ORA(AbsoluteY, 4);
			case 0x01: return ORA(IndirectX, 6);
			case 0x11: return ORA(IndirectY, 5);
			case 0x24: return BIT(ZeroPage, 3);
			case 0x2C: return BIT(Absolute, 4);

			//ADC, SBC, CMP, CPX, CPY
			case 0x69: return ADC(Immediate, 2);
			case 0x65: return ADC(ZeroPage, 3);
			case 0x75: return ADC(ZeroPageX, 4);
			case 0x6D: return ADC(Absolute, 4);
			case 0x7D: return ADC(AbsoluteX, 4);
			case 0x79: return ADC(AbsoluteY, 4);
			case 0x61: return ADC(IndirectX, 6);
			case 0x71: return ADC(IndirectY, 5);
			case 0xE9: return SBC(Immediate, 2);
			case 0xE5: return SBC(ZeroPage, 3);
			case 0xF5: return SBC(ZeroPageX, 4);
			case 0xED: return SBC(Absolute, 4);
			case 0xFD: return SBC(AbsoluteX, 4);
			case 0xF9: return SBC(AbsoluteY, 4);
			case 0xE1: return SBC(IndirectX, 6);
			case 0xF1: return SBC(IndirectY, 5);
			case 0xC9: return CPR(A, Immediate, 2);
			case 0xC5: return CPR(A, ZeroPage, 3);
			case 0xD5: return CPR(A, ZeroPageX, 4);
			case 0xCD: return CPR(A, Absolute, 4);
			case 0xDD: return CPR(A, AbsoluteX, 4);
			case 0xD9: return CPR(A, AbsoluteY, 4);
			case 0xC1: return CPR(A, IndirectX, 6);
			case 0xD1: return CPR(A, IndirectY, 5);
			case 0xE0: return CPR(X, Immediate, 2);
			case 0xE4: return CPR(X, ZeroPage, 3);
			case 0xEC: return CPR(X, Absolute, 4);
			case 0xC0: return CPR(Y, Immediate, 2);
			case 0xC4: return CPR(Y, ZeroPage, 3);
			case 0xCC: return CPR(Y, Absolute, 4);

			//INC, INX, INY, DEC, DEX, DEY
			case 0xE6: return INC(ZeroPage, 5);
			case 0xF6: return INC(ZeroPageX, 6);
			case 0xEE: return INC(Absolute, 6);
			case 0xFE: return INC(AbsoluteX, 7);
			case 0xE8: return INR(ref X, Implied, 2);
			case 0xC8: return INR(ref Y, Implied, 2);
			case 0xC6: return DEC(ZeroPage, 5);
			case 0xD6: return DEC(ZeroPageX, 6);
			case 0xCE: return DEC(Absolute, 6);
			case 0xDE: return DEC(AbsoluteX, 7);
			case 0xCA: return DER(ref X, Implied, 2);
			case 0x88: return DER(ref Y, Implied, 2);

			//ASL, LSR, ROL, ROR
			case 0x0A: return ASL(Accumulator, 2);
			case 0x06: return ASL(ZeroPage, 5);
			case 0x16: return ASL(ZeroPageX, 6);
			case 0x0E: return ASL(Absolute, 6);
			case 0x1E: return ASL(AbsoluteX, 7);
			case 0x4A: return LSR(Accumulator, 2);
			case 0x46: return LSR(ZeroPage, 5);
			case 0x56: return LSR(ZeroPageX, 6);
			case 0x4E: return LSR(Absolute, 6);
			case 0x5E: return LSR(AbsoluteX, 7);
			case 0x2A: return ROL(Accumulator, 2);
			case 0x26: return ROL(ZeroPage, 5);
			case 0x36: return ROL(ZeroPageX, 6);
			case 0x2E: return ROL(Absolute, 6);
			case 0x3E: return ROL(AbsoluteX, 7);
			case 0x6A: return ROR(Accumulator, 2);
			case 0x66: return ROR(ZeroPage, 5);
			case 0x76: return ROR(ZeroPageX, 6);
			case 0x6E: return ROR(Absolute, 6);
			case 0x7E: return ROR(AbsoluteX, 7);

			//JMP, JSR, RTS
			case 0x4C: return JMP(Absolute, 3);
			case 0x6C: return JMP(Indirect, 5);
			case 0x20: return JSR();
			case 0x60: return RTS();
			
			//BCC, BCS, BEQ, BMI, BNE, BPL, BVC, BVS
			case 0x90: return BIF(!GetFlag(FLAG_C), Relative, 2);
			case 0xB0: return BIF(GetFlag(FLAG_C), Relative, 2);
			case 0xF0: return BIF(GetFlag(FLAG_Z), Relative, 2);
			case 0x30: return BIF(GetFlag(FLAG_N), Relative, 2);
			case 0xD0: return BIF(!GetFlag(FLAG_Z), Relative, 2);
			case 0x10: return BIF(!GetFlag(FLAG_N), Relative, 2);
			case 0x50: return BIF(!GetFlag(FLAG_V), Relative, 2);
			case 0x70: return BIF(GetFlag(FLAG_V), Relative, 2);

			//CLC, CLD, CLI, CLV, SEC, SED, SEI
			case 0x18: return FSC(FLAG_C, false, Implied, 2);
			case 0xD8: return FSC(FLAG_D, false, Implied, 2);
			case 0x58: return FSC(FLAG_I, false, Implied, 2);
			case 0xB8: return FSC(FLAG_V, false, Implied, 2);
			case 0x38: return FSC(FLAG_C, true, Implied, 2);
			case 0xF8: return FSC(FLAG_D, true, Implied, 2);
			case 0x78: return FSC(FLAG_I, true, Implied, 2);

			// === Unofficial "combined" opcodes (stable, well-documented, seen in real compiled
			// and hand-written code - e.g. cc65-generated NESmaker games use LAX). The genuinely
			// unstable ones (ANE/XAA, SHA/SHX/SHY/TAS, LAS) vary between real 2A03 revisions and
			// are deliberately NOT implemented here - real games don't rely on those. LXA (0xAB)
			// is the one exception: it is unstable too, but real compiled code does emit it, so it
			// is approximated below rather than left to crash.
			case 0xA7: return LAX(ZeroPage, 3);
			case 0xB7: return LAX(ZeroPageY, 4);
			case 0xAF: return LAX(Absolute, 4);
			case 0xBF: return LAX(AbsoluteY, 4);
			case 0xA3: return LAX(IndirectX, 6);
			case 0xB3: return LAX(IndirectY, 5);
			case 0x87: return SAX(ZeroPage, 3);
			case 0x97: return SAX(ZeroPageY, 4);
			case 0x8F: return SAX(Absolute, 4);
			case 0x83: return SAX(IndirectX, 6);
			case 0xC7: return DCP(ZeroPage, 5);
			case 0xD7: return DCP(ZeroPageX, 6);
			case 0xCF: return DCP(Absolute, 6);
			case 0xDF: return DCP(AbsoluteX, 7);
			case 0xDB: return DCP(AbsoluteY, 7);
			case 0xC3: return DCP(IndirectX, 8);
			case 0xD3: return DCP(IndirectY, 8);
			case 0xE7: return ISC(ZeroPage, 5);
			case 0xF7: return ISC(ZeroPageX, 6);
			case 0xEF: return ISC(Absolute, 6);
			case 0xFF: return ISC(AbsoluteX, 7);
			case 0xFB: return ISC(AbsoluteY, 7);
			case 0xE3: return ISC(IndirectX, 8);
			case 0xF3: return ISC(IndirectY, 8);
			case 0x07: return SLO(ZeroPage, 5);
			case 0x17: return SLO(ZeroPageX, 6);
			case 0x0F: return SLO(Absolute, 6);
			case 0x1F: return SLO(AbsoluteX, 7);
			case 0x1B: return SLO(AbsoluteY, 7);
			case 0x03: return SLO(IndirectX, 8);
			case 0x13: return SLO(IndirectY, 8);
			case 0x27: return RLA(ZeroPage, 5);
			case 0x37: return RLA(ZeroPageX, 6);
			case 0x2F: return RLA(Absolute, 6);
			case 0x3F: return RLA(AbsoluteX, 7);
			case 0x3B: return RLA(AbsoluteY, 7);
			case 0x23: return RLA(IndirectX, 8);
			case 0x33: return RLA(IndirectY, 8);
			case 0x47: return SRE(ZeroPage, 5);
			case 0x57: return SRE(ZeroPageX, 6);
			case 0x4F: return SRE(Absolute, 6);
			case 0x5F: return SRE(AbsoluteX, 7);
			case 0x5B: return SRE(AbsoluteY, 7);
			case 0x43: return SRE(IndirectX, 8);
			case 0x53: return SRE(IndirectY, 8);
			case 0x67: return RRA(ZeroPage, 5);
			case 0x77: return RRA(ZeroPageX, 6);
			case 0x6F: return RRA(Absolute, 6);
			case 0x7F: return RRA(AbsoluteX, 7);
			case 0x7B: return RRA(AbsoluteY, 7);
			case 0x63: return RRA(IndirectX, 8);
			case 0x73: return RRA(IndirectY, 8);
			// ANC (AAC): AND immediate, then copy the result's bit 7 into carry (as if followed by an implicit ASL/ROL). Stable, seen in real code.
			case 0x0B: case 0x2B: return ANC(Immediate, 2);
			// LXA (ATX/OAL): genuinely unstable on real silicon (depends on bus capacitance decay,
			// varies per chip) - unlike ANE/XAA this one IS occasionally emitted by real compiled
			// code, so rather than crash we approximate with the "magic=0xFF" behavior most NES
			// 2A03 units and most accurate emulators (Mesen included) settle on: the OR-with-A term
			// becomes a no-op, so it reduces to A=X=value.
			case 0xAB: return LXA(Immediate, 2);
			// SBX (AXS): X = (A&X) - immediate, no borrow-in (unlike SBC), carry set on no-borrow. Stable, seen in real code.
			case 0xCB: return SBX(Immediate, 2);
			// ALR (ASR): AND immediate then LSR A. Carry comes from bit 0 of the AND result *before* the
			// shift (i.e. the bit the LSR shifts out), not from the shifted value. Stable, seen in real code.
			case 0x4B: return ALR(Immediate, 2);
			// ARR: AND immediate then ROR A, but with its own famously odd flag behavior - the ALU's adder
			// is involved, so carry comes from bit 6 of the result and overflow from bit 6 XOR bit 5,
			// instead of the usual ROR "shifted-out bit 0" carry. Stable, seen in real code.
			// (ARR's decimal-mode fixup quirk is moot here: the 2A03 has decimal mode disabled.)
			case 0x6B: return ARR(Immediate, 2);
			// USBC (SBC immediate, unofficial encoding): an exact alias of the official 0xE9 - same
			// operation, same flags, same 2 cycles. Compilers/assemblers do emit it. Stable.
			case 0xEB: return SBC(Immediate, 2);
			default:
				if (IgnoreInvalidOpcodes)
				{
					// Silently treat as NOP; optionally could log or count, but keep minimal for performance
					return 2; // typical NOP cycle cost
				}
				throw new CpuCrashException($"Bad opcode {opcode:X2} at {(PC-1):X4}");
		}
	}

	public class CpuCrashException : System.Exception {
		public CpuCrashException(string msg) : base(msg) {}
	}

	//Load/Store Operations
	private int LDR(ref byte r, Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		r = bus.Read(addr.address);
		SetZN(r);

		return baseCycles + addr.extraCycles;
	}

	private int STR(ref byte r, Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		bus.Write(addr.address, r);

		return baseCycles; //No extra cycle to add
	}

	//Register Transfer
	private int TRR(ref byte r1, ref byte r2, Func<AddrResult> mode, int baseCycles) {
		r1 = r2;
		SetZN(r1);

		return baseCycles;
	}

	//Stack Operations
	private void StackPush(byte value) {
		bus.Write((ushort)(0x0100 + SP), value);
		SP--;
		SP &= 0x00FF;
	}

	private byte StackPop() {
		SP++;
		SP &= 0x00FF;
		return bus.Read((ushort)(0x0100 + SP));
	}

	private int TSX(Func<AddrResult> mode, int baseCycles) {
		X = (byte)SP;
		SetZN(X);
		return baseCycles;
	}

	private int TXS(Func<AddrResult> mode, int baseCycles) {
		SP = X;
		return baseCycles;
	}

	private int PHA(Func<AddrResult> mode, int baseCycles) {
		StackPush(A);
		return baseCycles;
	}

	private int PHP(Func<AddrResult> mode, int baseCycles) {
		StackPush((byte)(status | (1 << FLAG_B) | (1 << FLAG_UNUSED)));
		return baseCycles;
	}

	private int PLA(Func<AddrResult> mode, int baseCycles) {
		A = StackPop();
		SetZN(A);
		return baseCycles;
	}

	private int PLP(Func<AddrResult> mode, int baseCycles) {
		status = StackPop();
		SetFlag(FLAG_UNUSED, true);
		SetFlag(FLAG_B, false);
		return baseCycles;
	}

	//Logical
	private int AND(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		A = (byte)(A & bus.Read(addr.address));
		SetZN(A);

		return baseCycles + addr.extraCycles;
	}

	private int EOR(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		A = (byte)(A ^ bus.Read(addr.address));
		SetZN(A);

		return baseCycles + addr.extraCycles;
	}

	private int ORA(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		A = (byte)(A | bus.Read(addr.address));
		SetZN(A);
		return baseCycles + addr.extraCycles;
	}

	private int BIT(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = bus.Read(addr.address);

		SetFlag(FLAG_Z, (A & value) == 0);
		SetFlag(FLAG_N, (value & 0x80) != 0);
		SetFlag(FLAG_V, (value & 0x40) != 0);

		return baseCycles + addr.extraCycles;
	}

	//Arithmetic
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int ADC(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte m = bus.Read(addr.address);
		ushort sum = (ushort)(A + m + (GetFlag(FLAG_C) ? 1 : 0));
		SetFlag(FLAG_C, sum > 0xFF);
		SetFlag(FLAG_Z, (sum & 0xFF) == 0);
		SetFlag(FLAG_N, (sum & 0x80) != 0);
		SetFlag(FLAG_V, (~(A ^ m) & (A ^ sum) & 0x80) != 0);
		A = (byte)sum;
		return baseCycles + addr.extraCycles;
	}

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int SBC(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte m = bus.Read(addr.address);
		ushort value = (ushort)(m ^ 0xFF);
		ushort sum = (ushort)(A + value + (GetFlag(FLAG_C) ? 1 : 0));
		SetFlag(FLAG_C, sum > 0xFF);
		SetFlag(FLAG_Z, (sum & 0xFF) == 0);
		SetFlag(FLAG_N, (sum & 0x80) != 0);
		SetFlag(FLAG_V, ((A ^ sum) & (value ^ sum) & 0x80) != 0);
		A = (byte)sum;
		return baseCycles + addr.extraCycles;
	}

	private int CPR(byte r, Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte M = bus.Read(addr.address);
		ushort temp = (ushort)(r - M);

		SetFlag(FLAG_C, r >= M);
		SetFlag(FLAG_Z, (temp & 0xFF) == 0);
		SetFlag(FLAG_N, (temp & 0x80) != 0);

		return baseCycles + addr.extraCycles;
	}

	//Increments and Decrements
	private int INC(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte original = bus.Read(addr.address);
		byte result = (byte)(original + 1);
		bus.Write(addr.address, original); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		SetZN(result);

		return baseCycles; //No extra cycle to add
	}

	private int DEC(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte original = bus.Read(addr.address);
		byte result = (byte)(original - 1);
		bus.Write(addr.address, original); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		SetZN(result);

		return baseCycles; //No extra cycle to add
	}

	private int INR(ref byte r, Func<AddrResult> mode, int baseCycles) {
		r++;
		SetZN(r);
		return baseCycles;
	}

	private int DER(ref byte r, Func<AddrResult> mode, int baseCycles) {
		r--;
		SetZN(r);
		return baseCycles;
	}

	//Shifts
	private int ASL(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = mode == Accumulator ? A : bus.Read(addr.address);
		SetFlag(FLAG_C, (value & 0x80) != 0);
		byte result = (byte)(value << 1);

		if (mode == Accumulator) {
			A = result;
		} else {
			bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
			bus.Write(addr.address, result);
		}

		SetZN(result);

		return baseCycles;
	}

	private int LSR(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = mode == Accumulator ? A : bus.Read(addr.address);
		SetFlag(FLAG_C, (value & 0x01) != 0);
		byte result = (byte)(value >> 1);

		if (mode == Accumulator) {
			A = result;
		} else {
			bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
			bus.Write(addr.address, result);
		}

		SetZN(result);

		return baseCycles;
	}

	private int ROL(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = mode == Accumulator ? A : bus.Read(addr.address);
		bool oldCarry = GetFlag(FLAG_C);
		SetFlag(FLAG_C, (value & 0x80) != 0);
		byte result = (byte)((value << 1) | (oldCarry ? 1 : 0));

		if (mode == Accumulator) {
			A = result;
		} else {
			bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
			bus.Write(addr.address, result);
		}

		SetZN(result);

		return baseCycles;
	}

	private int ROR(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = mode == Accumulator ? A : bus.Read(addr.address);
		bool oldCarry = GetFlag(FLAG_C);
		SetFlag(FLAG_C, (value & 0x01) != 0);
		byte result = (byte)((value >> 1) | (oldCarry ? 0x80 : 0));

		if (mode == Accumulator) {
			A = result;
		} else {
			bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
			bus.Write(addr.address, result);
		}

		SetZN(result);

		return baseCycles;
	}

	// === Unofficial "combined" opcodes ===
	// LAX: load into both A and X (a real, pure load - conditional page-cross cycle like LDA/LDX).
	private int LAX(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		A = bus.Read(addr.address);
		X = A;
		SetZN(A);
		return baseCycles + addr.extraCycles;
	}

	// SAX (AAX): store A&X with no flags affected. Store-only - no indexed-absolute or (zp),Y forms exist on real hardware.
	private int SAX(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		bus.Write(addr.address, (byte)(A & X));
		return baseCycles;
	}

	// DCP (DCM): DEC then CMP against A. Fixed cycle cost, matching this file's existing INC/DEC/ASL-family convention for indexed RMW addressing.
	private int DCP(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte original = bus.Read(addr.address);
		byte result = (byte)(original - 1);
		bus.Write(addr.address, original); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		ushort temp = (ushort)(A - result);
		SetFlag(FLAG_C, A >= result);
		SetFlag(FLAG_Z, (temp & 0xFF) == 0);
		SetFlag(FLAG_N, (temp & 0x80) != 0);
		return baseCycles;
	}

	// ISC (ISB/INS): INC then SBC.
	private int ISC(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte original = bus.Read(addr.address);
		byte result = (byte)(original + 1);
		bus.Write(addr.address, original); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		ushort value = (ushort)(result ^ 0xFF);
		ushort sum = (ushort)(A + value + (GetFlag(FLAG_C) ? 1 : 0));
		SetFlag(FLAG_C, sum > 0xFF);
		SetFlag(FLAG_Z, (sum & 0xFF) == 0);
		SetFlag(FLAG_N, (sum & 0x80) != 0);
		SetFlag(FLAG_V, ((A ^ sum) & (value ^ sum) & 0x80) != 0);
		A = (byte)sum;
		return baseCycles;
	}

	// SLO (ASO): ASL then ORA.
	private int SLO(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = bus.Read(addr.address);
		SetFlag(FLAG_C, (value & 0x80) != 0);
		byte result = (byte)(value << 1);
		bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		A = (byte)(A | result);
		SetZN(A);
		return baseCycles;
	}

	// RLA: ROL then AND.
	private int RLA(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = bus.Read(addr.address);
		bool oldCarry = GetFlag(FLAG_C);
		SetFlag(FLAG_C, (value & 0x80) != 0);
		byte result = (byte)((value << 1) | (oldCarry ? 1 : 0));
		bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		A = (byte)(A & result);
		SetZN(A);
		return baseCycles;
	}

	// SRE (LSE): LSR then EOR.
	private int SRE(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = bus.Read(addr.address);
		SetFlag(FLAG_C, (value & 0x01) != 0);
		byte result = (byte)(value >> 1);
		bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		A = (byte)(A ^ result);
		SetZN(A);
		return baseCycles;
	}

	// RRA: ROR then ADC (the ADC's carry-in is the carry ROR just produced, matching real hardware).
	private int RRA(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = bus.Read(addr.address);
		bool oldCarry = GetFlag(FLAG_C);
		SetFlag(FLAG_C, (value & 0x01) != 0);
		byte result = (byte)((value >> 1) | (oldCarry ? 0x80 : 0));
		bus.Write(addr.address, value); // dummy write-back of the unmodified value (real RMW hardware behavior)
		bus.Write(addr.address, result);
		ushort sum = (ushort)(A + result + (GetFlag(FLAG_C) ? 1 : 0));
		SetFlag(FLAG_C, sum > 0xFF);
		SetFlag(FLAG_Z, (sum & 0xFF) == 0);
		SetFlag(FLAG_N, (sum & 0x80) != 0);
		SetFlag(FLAG_V, (~(A ^ result) & (A ^ sum) & 0x80) != 0);
		A = (byte)sum;
		return baseCycles;
	}

	// ANC (AAC): AND immediate, then copy bit 7 of the result into carry.
	private int ANC(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		A = (byte)(A & bus.Read(addr.address));
		SetZN(A);
		SetFlag(FLAG_C, (A & 0x80) != 0);
		return baseCycles;
	}

	// LXA (ATX/OAL): see call-site comment - approximated as A=X=value ("magic=0xFF").
	private int LXA(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = bus.Read(addr.address);
		A = value;
		X = value;
		SetZN(A);
		return baseCycles;
	}

	// SBX (AXS): X = (A&X) - immediate, unsigned subtraction with no borrow-in (unlike SBC).
	private int SBX(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte value = bus.Read(addr.address);
		int anded = A & X;
		int result = anded - value;
		SetFlag(FLAG_C, anded >= value);
		X = (byte)result;
		SetZN(X);
		return baseCycles;
	}

	// ALR (ASR): AND immediate then LSR A. Carry is bit 0 of the AND result *before* the shift
	// (the bit LSR shifts out), so it must be captured before A is overwritten; Z/N come from the
	// shifted result, which always has bit 7 clear - so N is always cleared here.
	private int ALR(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte anded = (byte)(A & bus.Read(addr.address));
		SetFlag(FLAG_C, (anded & 0x01) != 0);
		A = (byte)(anded >> 1);
		SetZN(A);
		return baseCycles;
	}

	// ARR: AND immediate then ROR A (the old carry still rotates into bit 7, as a real ROR would),
	// but the flags do NOT follow ROR. On real silicon the AND result also passes through the adder,
	// so carry is bit 6 of the *result* and overflow is bit 6 XOR bit 5 of the result - the classic
	// ARR trap. Z/N are normal, taken from the result.
	private int ARR(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte anded = (byte)(A & bus.Read(addr.address));
		byte result = (byte)((anded >> 1) | (GetFlag(FLAG_C) ? 0x80 : 0));
		A = result;
		SetFlag(FLAG_C, (result & 0x40) != 0);
		SetFlag(FLAG_V, (((result >> 6) ^ (result >> 5)) & 0x01) != 0);
		SetZN(A);
		return baseCycles;
	}

	//Jumps and Calls
	private int JMP(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		PC = addr.address;
		return baseCycles;
	}

	private int JSR() {
		// Real 6502 JSR order: fetch low byte -> dummy read from stack -> push PCH -> push PCL ->
		// fetch high byte. Fetching the high byte *after* both pushes means a JSR run from a
		// stack-page-overlapping RAM address observes the just-pushed return-address bytes if they
		// land on the not-yet-read high-byte operand, matching hardware exactly.
		byte low = Fetch();
		bus.Read((ushort)(0x0100 + SP)); // cycle-3 dummy read of the current stack location

		ushort returnAddr = PC; // address of the (not yet fetched) high byte operand

		StackPush((byte)((returnAddr >> 8) & 0xFF));
		StackPush((byte)(returnAddr & 0xFF));

		byte high = Fetch();
		PC = (ushort)((high << 8) | low);
		return 6;
	}

	private int RTS() {
		byte low = StackPop();
		byte high = StackPop();
		PC = (ushort)(((high << 8) | low) + 1);
		return 6;
	}

	//Branches
	private int BIF(bool condition, Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		int extra = 0;

		if (condition) {
			// Real hardware performs 1-2 extra bus reads on a taken branch instead of a pure
			// arithmetic PC update: a dummy read at the not-taken continuation address (cycle 3),
			// and, only when the branch crosses a page, a second dummy read at the wrong-page
			// temporary address (cycle 4) before PCH is corrected. These are externally observable
			// (e.g. a branch placed at a PPU register address clears vblank via its dummy read).
			ushort pcAfterOperand = PC; // address of the fall-through instruction
			bus.Read(pcAfterOperand);

			ushort noCarryPC = (ushort)((pcAfterOperand & 0xFF00) | (addr.address & 0x00FF));
			if ((noCarryPC & 0xFF00) != (addr.address & 0xFF00)) {
				bus.Read(noCarryPC);
			}

			PC = addr.address;
			extra = 1 + addr.extraCycles;
		}

		return baseCycles + extra;
	}

	//Status Flag Changes
	private int FSC(int bit, bool state, Func<AddrResult> mode, int baseCycles) {
		SetFlag(bit, state);
		return baseCycles;
	}
	
	//System Functions
	private int NOP() {
		return 2;
	}

	private int BRK() {
		PC++;
	
		StackPush((byte)((PC >> 8) & 0xFF));
		StackPush((byte)(PC & 0xFF));
		
		byte pushedStatus = (byte)(status | (1 << FLAG_B) | (1 << FLAG_UNUSED));
		StackPush(pushedStatus);

		SetFlag(FLAG_B, false);

		SetFlag(FLAG_I, true);

		byte lo = bus.Read(0xFFFE);
		byte hi = bus.Read(0xFFFF);
		PC = (ushort)((hi << 8) | lo);

		return 7;
	}

	private int RTI() {
		status = StackPop();
		SetFlag(FLAG_UNUSED, true);
		SetFlag(FLAG_B, false);

		byte low = StackPop();
		byte high = StackPop();
		PC = (ushort)((high << 8) | low);

		return 6;
	}

	// The decision to service a pending IRQ is made entirely by the caller (ExecuteInstruction's
	// `!pollFlagI && irqRequested` check), which already accounts for the one-instruction-delayed
	// visibility of CLI/SEI/PLP's effect on the I flag. Re-checking the *live* status.I flag here
	// is not just redundant - it's wrong whenever an intervening SEI/PLP has already flipped the
	// live flag by the time this runs (e.g. CLI immediately followed by SEI): the caller correctly
	// decided to service based on the pre-SEI state, but this stale re-check would then silently
	// veto that decision, dropping the interrupt instead of servicing it. Real hardware has no such
	// second gate - once polling latches the decision, the interrupt proceeds. NMI() below never
	// had this guard, which is the correct model.
	public int IRQ() {
		StackPush((byte)((PC >> 8) & 0xFF));
		StackPush((byte)(PC & 0xFF));

		SetFlag(FLAG_B, false);
		SetFlag(FLAG_UNUSED, true);
		StackPush(status);

		SetFlag(FLAG_I, true);

		byte low = bus.Read(0xFFFE);
		byte high = bus.Read(0xFFFF);
		PC = (ushort)((high << 8) | low);

		return 7;
	}

	public int NMI() {
		StackPush((byte)((PC >> 8) & 0xFF));
		StackPush((byte)(PC & 0xFF));

		SetFlag(FLAG_B, false);
		SetFlag(FLAG_UNUSED, true);
		StackPush(status);

		SetFlag(FLAG_I, true);

		byte low = bus.Read(0xFFFA);
		byte high = bus.Read(0xFFFB);
		PC = (ushort)((high << 8) | low);

		return 7;
	}

	private struct AddrResult {
		public ushort address;
		public int extraCycles;

		public AddrResult(ushort addr, int extra) {
			address = addr;
			extraCycles = extra;
		}
	}

	private AddrResult Implied() {
		return new AddrResult(0, 0);
	}

	private AddrResult Accumulator() {
		return new AddrResult(0, 0);
	}

	private AddrResult Immediate() {
		return new AddrResult(PC++, 0);
	}

	private AddrResult ZeroPage() {
		byte addr = Fetch();
		return new AddrResult(addr, 0);
	}

	// Zero-page indexed modes always take their fixed cycle count (no page-cross variability -
	// the zero-page pointer wraps within page 0), and real hardware always performs a genuine
	// dummy read of the un-indexed zero-page address before adding the index. This is true
	// regardless of whether the instruction using this mode is a load, a store, or a
	// read-modify-write - so the dummy read lives here, unconditionally, rather than being
	// gated per-caller.
	private AddrResult ZeroPageX() {
		byte baseAddr = Fetch();
		bus.Read(baseAddr); // dummy read of the un-indexed zero-page address
		byte addr = (byte)(baseAddr + X);
		return new AddrResult(addr, 0);
	}

	private AddrResult ZeroPageY() {
		byte baseAddr = Fetch();
		bus.Read(baseAddr); // dummy read of the un-indexed zero-page address
		byte addr = (byte)(baseAddr + Y);
		return new AddrResult(addr, 0);
	}

	private AddrResult Absolute() {
		ushort addr = Fetch16Bits();
		return new AddrResult(addr, 0);
	}

	// Load-style absolute,X: real hardware only performs the extra bus cycle - a dummy read at
	// the not-yet-fixed-up address (old high byte, new/wrapped low byte) - when the index
	// addition actually crosses a page. When it doesn't cross, the "uncorrected" address is
	// identical to the effective address anyway, so skipping the read there costs nothing
	// observable and keeps the common (non-crossing) case to a single bus access, matching
	// hardware's variable cycle count for loads. See AbsoluteXStore for the store-only
	// unconditional variant.
	private AddrResult AbsoluteX() {
		ushort baseAddr = Fetch16Bits();
		ushort effective = (ushort)(baseAddr + X);
		bool crossed = HasPageCrossPenalty(baseAddr, effective);
		if (crossed) {
			ushort uncorrected = (ushort)((baseAddr & 0xFF00) | (effective & 0x00FF));
			bus.Read(uncorrected); // dummy read at the wrong-page address
		}
		return new AddrResult(effective, crossed ? 1 : 0);
	}

	private AddrResult AbsoluteY() {
		ushort baseAddr = Fetch16Bits();
		ushort effective = (ushort)(baseAddr + Y);
		bool crossed = HasPageCrossPenalty(baseAddr, effective);
		if (crossed) {
			ushort uncorrected = (ushort)((baseAddr & 0xFF00) | (effective & 0x00FF));
			bus.Read(uncorrected); // dummy read at the wrong-page address
		}
		return new AddrResult(effective, crossed ? 1 : 0);
	}

	// Indexed absolute STORES always take the fixed max cycle count (5), unlike loads: the
	// dummy read at the not-yet-fixed-up address always happens, cross or not (when there's no
	// cross it just happens to read the same address the write will target next). Used only by
	// STR for STA abs,X/Y - LDR/AND/EOR/ORA/ADC/SBC/CPR keep using the conditional AbsoluteX/Y
	// above, since only STORES are fixed-cycle here.
	private AddrResult AbsoluteXStore() {
		ushort baseAddr = Fetch16Bits();
		ushort effective = (ushort)(baseAddr + X);
		ushort uncorrected = (ushort)((baseAddr & 0xFF00) | (effective & 0x00FF));
		bus.Read(uncorrected); // unconditional dummy read (store semantics)
		return new AddrResult(effective, 0);
	}

	private AddrResult AbsoluteYStore() {
		ushort baseAddr = Fetch16Bits();
		ushort effective = (ushort)(baseAddr + Y);
		ushort uncorrected = (ushort)((baseAddr & 0xFF00) | (effective & 0x00FF));
		bus.Read(uncorrected); // unconditional dummy read (store semantics)
		return new AddrResult(effective, 0);
	}

	// (zp,X): the zero-page pointer is always dummy-read before X is added to it (fixed 6-cycle
	// timing regardless of load/store/RMW), same rationale as ZeroPageX/Y above.
	private AddrResult IndirectX() {
		byte zp = Fetch();
		bus.Read(zp); // dummy read of the pointer before X is added
		byte ptr = (byte)(zp + X);
		ushort addr = (ushort)(bus.Read(ptr) | (bus.Read((byte)(ptr + 1)) << 8));
		return new AddrResult(addr, 0);
	}

	// Load-style (zp),Y: same conditional-on-cross dummy read as AbsoluteX/Y above.
	private AddrResult IndirectY() {
		byte zp = Fetch();
		ushort baseAddr = (ushort)(bus.Read(zp) | (bus.Read((byte)(zp + 1)) << 8));
		ushort effective = (ushort)(baseAddr + Y);
		bool crossed = HasPageCrossPenalty(baseAddr, effective);
		if (crossed) {
			ushort uncorrected = (ushort)((baseAddr & 0xFF00) | (effective & 0x00FF));
			bus.Read(uncorrected); // dummy read at the wrong-page address
		}
		return new AddrResult(effective, crossed ? 1 : 0);
	}

	// STA (zp),Y: always 6 cycles, unconditional dummy read (store semantics) - see
	// AbsoluteXStore for the same rationale.
	private AddrResult IndirectYStore() {
		byte zp = Fetch();
		ushort baseAddr = (ushort)(bus.Read(zp) | (bus.Read((byte)(zp + 1)) << 8));
		ushort effective = (ushort)(baseAddr + Y);
		ushort uncorrected = (ushort)((baseAddr & 0xFF00) | (effective & 0x00FF));
		bus.Read(uncorrected); // unconditional dummy read (store semantics)
		return new AddrResult(effective, 0);
	}

	private AddrResult Indirect() {
		ushort ptr = Fetch16Bits();
		byte lo = bus.Read(ptr);
		byte hi = (ptr & 0x00FF) == 0x00FF ? bus.Read((ushort)(ptr & 0xFF00)) : bus.Read((ushort)(ptr + 1));
		ushort addr = (ushort)((hi << 8) | lo);
		return new AddrResult(addr, 0);
	}

	private AddrResult Relative() {
		sbyte offset = (sbyte)Fetch();
		ushort target = (ushort)(PC + offset);
		int penalty = HasPageCrossPenalty(PC, target) ? 1 : 0;
		return new AddrResult(target, penalty);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool HasPageCrossPenalty(ushort baseAddr, ushort effectiveAddr) => (baseAddr & 0xFF00) != (effectiveAddr & 0xFF00);

	public object GetState() => new CpuSharedState { A=A,X=X,Y=Y,status=status,PC=PC,SP=SP,irqRequested=irqRequested,nmiRequested=nmiRequested,irqDeferOne=irqDeferOne };
	public void SetState(object state) {
		if (state is CpuSharedState s) { A=s.A;X=s.X;Y=s.Y;status=s.status;PC=s.PC;SP=s.SP;irqRequested=s.irqRequested;nmiRequested=s.nmiRequested;irqDeferOne=s.irqDeferOne; pollFlagI = GetFlag(FLAG_I); return; }
		if (state is System.Text.Json.JsonElement je) {
			if (je.TryGetProperty("A", out var pA)) A = (byte)pA.GetInt32();
			if (je.TryGetProperty("X", out var pX)) X = (byte)pX.GetInt32();
			if (je.TryGetProperty("Y", out var pY)) Y = (byte)pY.GetInt32();
			if (je.TryGetProperty("status", out var ps)) status = (byte)ps.GetInt32();
			if (je.TryGetProperty("PC", out var pPC)) PC = (ushort)pPC.GetInt32();
			if (je.TryGetProperty("SP", out var pSP)) SP = (ushort)pSP.GetInt32();
			if (je.TryGetProperty("irqRequested", out var pi)) irqRequested = pi.GetBoolean();
			irqDeferOne = je.TryGetProperty("irqDeferOne", out var pdo) && pdo.GetBoolean();
			if (je.TryGetProperty("nmiRequested", out var pn)) nmiRequested = pn.GetBoolean();
			// pollFlagI has no cross-core representation in shared state; resync to the live I
			// flag on load/hot-swap. This loses a mid-flight one-instruction delay across a
			// savestate/hot-swap boundary, an acceptable edge case for a non-serialized latch.
			pollFlagI = GetFlag(FLAG_I);
		}
	}
}
}
