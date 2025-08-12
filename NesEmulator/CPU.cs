using System.Runtime.CompilerServices;
namespace NesEmulator
{
public interface ICPU
{
	bool IgnoreInvalidOpcodes { get; set; }
	void Reset();
	int ExecuteInstruction();
	void RequestIRQ(bool line);
	void RequestNMI();
	object GetState();
	void SetState(object state);
	(ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters();
	void AddToPC(int delta);
}

public class CPU : ICPU {
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

	// When true, unknown opcodes are treated as 2-cycle NOPs instead of throwing CpuCrashException
	public bool IgnoreInvalidOpcodes { get; set; } = false;

	public CPU(Bus bus) {
		A = X = Y = 0;
		PC = 0x0000;
		SP = 0x0000;
		status = 0;

		this.bus = bus;

		irqRequested = false;
		nmiRequested = false;
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
	public void RequestIRQ(bool line) { irqRequested = line; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void RequestNMI() { nmiRequested = true; }

	public int ExecuteInstruction() {
		if (nmiRequested) {
			nmiRequested = false;
			return NMI();
		}

		if (GetFlag(FLAG_I) == false && irqRequested) {
			irqRequested = false;
			return IRQ();
		}

		byte opcode = Fetch();

			switch (opcode) {
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
			case 0x9D: return STR(ref A, AbsoluteX, 5);
			case 0x99: return STR(ref A, AbsoluteY, 5);
			case 0x81: return STR(ref A, IndirectX, 6);
			case 0x91: return STR(ref A, IndirectY, 6);
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
		byte result = (byte)(bus.Read(addr.address) + 1);
		bus.Write(addr.address, result);
		SetZN(result);

		return baseCycles; //No extra cycle to add
	}

	private int DEC(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		byte result = (byte)(bus.Read(addr.address) - 1);
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
			bus.Write(addr.address, result);
		}

		SetZN(result);

		return baseCycles;
	}

	//Jumps and Calls
	private int JMP(Func<AddrResult> mode, int baseCycles) {
		var addr = mode();
		PC = addr.address;
		return baseCycles;
	}

	private int JSR() {
		ushort targetLow = Fetch();
		ushort targetHigh = Fetch();

		ushort targetAddr = (ushort)((targetHigh << 8) | targetLow);

		ushort returnAddr = (ushort)(PC - 1);

		StackPush((byte)((returnAddr >> 8) & 0xFF));
		StackPush((byte)(returnAddr & 0xFF));

		PC = targetAddr;
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

	public int IRQ() {
		if (GetFlag(FLAG_I) == false) {
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

		return 0;
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

	private AddrResult ZeroPageX() {
		byte baseAddr = Fetch();
		byte addr = (byte)(baseAddr + X);
		return new AddrResult(addr, 0);
	}

	private AddrResult ZeroPageY() {
		byte baseAddr = Fetch();
		byte addr = (byte)(baseAddr + Y);
		return new AddrResult(addr, 0);
	}

	private AddrResult Absolute() {
		ushort addr = Fetch16Bits();
		return new AddrResult(addr, 0);
	}

	private AddrResult AbsoluteX() {
		ushort baseAddr = Fetch16Bits();
		ushort effective = (ushort)(baseAddr + X);
		int penalty = HasPageCrossPenalty(baseAddr, effective) ? 1 : 0;
		return new AddrResult(effective, penalty);
	}

	private AddrResult AbsoluteY() {
		ushort baseAddr = Fetch16Bits();
		ushort effective = (ushort)(baseAddr + Y);
		int penalty = HasPageCrossPenalty(baseAddr, effective) ? 1 : 0;
		return new AddrResult(effective, penalty);
	}

	private AddrResult IndirectX() {
		byte zp = Fetch();
		byte ptr = (byte)(zp + X);
		ushort addr = (ushort)(bus.Read(ptr) | (bus.Read((byte)(ptr + 1)) << 8));
		return new AddrResult(addr, 0);
	}

	private AddrResult IndirectY() {
		byte zp = Fetch();
		ushort baseAddr = (ushort)(bus.Read(zp) | (bus.Read((byte)(zp + 1)) << 8));
		ushort effective = (ushort)(baseAddr + Y);
		int penalty = HasPageCrossPenalty(baseAddr, effective) ? 1 : 0;
		return new AddrResult(effective, penalty);
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

	private class CpuState { public byte A,X,Y,status; public ushort PC,SP; public bool irqRequested,nmiRequested; }
	public object GetState() => new CpuState { A=A,X=X,Y=Y,status=status,PC=PC,SP=SP,irqRequested=irqRequested,nmiRequested=nmiRequested };
	public void SetState(object state) {
		if (state is CpuState s) { A=s.A;X=s.X;Y=s.Y;status=s.status;PC=s.PC;SP=s.SP;irqRequested=s.irqRequested;nmiRequested=s.nmiRequested; return; }
		if (state is System.Text.Json.JsonElement je) {
			if (je.TryGetProperty("A", out var pA)) A = (byte)pA.GetInt32();
			if (je.TryGetProperty("X", out var pX)) X = (byte)pX.GetInt32();
			if (je.TryGetProperty("Y", out var pY)) Y = (byte)pY.GetInt32();
			if (je.TryGetProperty("status", out var ps)) status = (byte)ps.GetInt32();
			if (je.TryGetProperty("PC", out var pPC)) PC = (ushort)pPC.GetInt32();
			if (je.TryGetProperty("SP", out var pSP)) SP = (ushort)pSP.GetInt32();
			if (je.TryGetProperty("irqRequested", out var pi)) irqRequested = pi.GetBoolean();
			if (je.TryGetProperty("nmiRequested", out var pn)) nmiRequested = pn.GetBoolean();
		}
	}
}
}
