using System.Runtime.CompilerServices;
namespace NesEmulator
{
public sealed class CPU_LOW : ICPU {
	// Metadata defaults
	public string CoreName => "Low Power";
	public string Description => "Based on the Famiclone (FMC) core, this variant optimizes performance and power consumption.";
	public int Performance => 10;
	public int Rating => 4;
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

	// Precomputed flag bit masks (avoid shifts in hot paths)
	private const byte MASK_C = 1 << FLAG_C;
	private const byte MASK_Z = 1 << FLAG_Z;
	private const byte MASK_I = 1 << FLAG_I;
	private const byte MASK_D = 1 << FLAG_D;
	private const byte MASK_B = 1 << FLAG_B;
	private const byte MASK_U = 1 << FLAG_UNUSED;
	private const byte MASK_V = 1 << FLAG_V;
	private const byte MASK_N = 1 << FLAG_N;
	private const byte CLEAR_ZN = 0x7D; // ~(Z|N) (0x82) => 0x7D
	private const byte CLEAR_ZNV = 0x3D; // clear Z,N,V retaining others

	// Precomputed table for Z and N bits of every byte (bitwise OR of MASK_Z when zero and high bit for N)
	private static readonly byte[] ZNTable = CreateZNTable();
	private static byte[] CreateZNTable() {
		var t = new byte[256];
		for (int i=0;i<256;i++) t[i] = (byte)(((i==0)?MASK_Z:0) | (i & 0x80));
		return t;
	}

	private enum AddressMode { Implied, Accumulator, Immediate, ZeroPage, ZeroPageX, ZeroPageY, Absolute, AbsoluteX, AbsoluteY, Indirect, IndirectX, IndirectY, Relative }

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void SetZNFast(byte value) => status = (byte)((status & CLEAR_ZN) | ZNTable[value]);

	private Bus bus; // concrete type to avoid interface dispatch cost

	private bool irqRequested;
	private bool nmiRequested;
	// When false, ExecuteInstruction skips inline interrupt polling so scheduler can service at batch boundaries
	public bool InlineInterruptChecks { get; set; } = true;

	// When true, unknown opcodes are treated as 2-cycle NOPs instead of throwing CpuCrashException
	public bool IgnoreInvalidOpcodes { get; set; } = false;

	public CPU_LOW(Bus bus) {
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

	public void SetZN(byte value) { SetZNFast(value); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	private byte Fetch() { return bus.Read(PC++); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ushort Fetch16Bits() { byte low = Fetch(); byte high = Fetch(); return (ushort)((high << 8) | low); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void RequestIRQ(bool line) { irqRequested = line; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void RequestNMI() { nmiRequested = true; }

	public int ExecuteInstruction() {
		if (InlineInterruptChecks) {
			if (nmiRequested) { nmiRequested = false; return NMI(); }
			if (!GetFlag(FLAG_I) && irqRequested) { irqRequested = false; return IRQ(); }
		}

		byte opcode = Fetch();

			switch (opcode) {
			// === Unofficial NOP family (multi-byte safe no-ops) ===
			// Implied 2-cycle NOPs
			case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: return 2; // single byte implied
			// ZeroPage variants (consume operand, 3 cycles)
			case 0x04: case 0x44: case 0x64: { Fetch(); return 3; }
			// ZeroPage,X variants (operand + X indexing discard, 4 cycles)
			case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4: { Fetch(); return 4; }
			// Absolute (16-bit operand)
			case 0x0C: { Fetch16Bits(); return 4; }
			// Absolute,X (adds page cross penalty like regular AbsoluteX addressing)
			case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: { var ar = ResolveAbsIndexed(X); return 4 + ar.extraCycles; }
			//BRK, NOP, RTI
			case 0x00: return BRK();
			case 0xEA: return NOP();
			case 0x40: return RTI();
			
			//LDA, LDX, LDY, STA, STX, STY
			case 0xA9: return LDR(ref A, AddressMode.Immediate, 2);
			case 0xA5: return LDR(ref A, AddressMode.ZeroPage, 3);
			case 0xB5: return LDR(ref A, AddressMode.ZeroPageX, 4);
			case 0xAD: return LDR(ref A, AddressMode.Absolute, 4);
			case 0xBD: return LDR(ref A, AddressMode.AbsoluteX, 4);
			case 0xB9: return LDR(ref A, AddressMode.AbsoluteY, 4);
			case 0xA1: return LDR(ref A, AddressMode.IndirectX, 6);
			case 0xB1: return LDR(ref A, AddressMode.IndirectY, 5);
			case 0xA2: return LDR(ref X, AddressMode.Immediate, 2);
			case 0xA6: return LDR(ref X, AddressMode.ZeroPage, 3);      
			case 0xB6: return LDR(ref X, AddressMode.ZeroPageY, 4);
			case 0xAE: return LDR(ref X, AddressMode.Absolute, 4);
			case 0xBE: return LDR(ref X, AddressMode.AbsoluteY, 4);
			case 0xA0: return LDR(ref Y, AddressMode.Immediate, 2);
			case 0xA4: return LDR(ref Y, AddressMode.ZeroPage, 3);      
			case 0xB4: return LDR(ref Y, AddressMode.ZeroPageX, 4);
			case 0xAC: return LDR(ref Y, AddressMode.Absolute, 4);
			case 0xBC: return LDR(ref Y, AddressMode.AbsoluteX, 4);
			case 0x85: return STR(ref A, AddressMode.ZeroPage, 3);
			case 0x95: return STR(ref A, AddressMode.ZeroPageX, 4);
			case 0x8D: return STR(ref A, AddressMode.Absolute, 4);
			case 0x9D: return STR(ref A, AddressMode.AbsoluteX, 5);
			case 0x99: return STR(ref A, AddressMode.AbsoluteY, 5);
			case 0x81: return STR(ref A, AddressMode.IndirectX, 6);
			case 0x91: return STR(ref A, AddressMode.IndirectY, 6);
			case 0x86: return STR(ref X, AddressMode.ZeroPage, 3);
			case 0x96: return STR(ref X, AddressMode.ZeroPageY, 4);
			case 0x8E: return STR(ref X, AddressMode.Absolute, 4);
			case 0x84: return STR(ref Y, AddressMode.ZeroPage, 3);
			case 0x94: return STR(ref Y, AddressMode.ZeroPageX, 4);
			case 0x8C: return STR(ref Y, AddressMode.Absolute, 4);
			
			//TAX, TAY, TXA, TYA
			case 0xAA: return TRR(ref X, ref A, AddressMode.Implied, 2);
			case 0xA8: return TRR(ref Y, ref A, AddressMode.Implied, 2);
			case 0x8A: return TRR(ref A, ref X, AddressMode.Implied, 2);
			case 0x98: return TRR(ref A, ref Y, AddressMode.Implied, 2);

			//TSX, TXS, PHA, PHP, PLA, PLP
			case 0xBA: return TSX(AddressMode.Implied, 2);
			case 0x9A: return TXS(AddressMode.Implied, 2);
			case 0x48: return PHA(AddressMode.Implied, 3);
			case 0x08: return PHP(AddressMode.Implied, 3);
			case 0x68: return PLA(AddressMode.Implied, 4);
			case 0x28: return PLP(AddressMode.Implied, 4);

			//AND, EOR, ORA, BIT
			case 0x29: return AND(AddressMode.Immediate, 2);
			case 0x25: return AND(AddressMode.ZeroPage, 3);
			case 0x35: return AND(AddressMode.ZeroPageX, 4);
			case 0x2D: return AND(AddressMode.Absolute, 4);
			case 0x3D: return AND(AddressMode.AbsoluteX, 4);
			case 0x39: return AND(AddressMode.AbsoluteY, 4);
			case 0x21: return AND(AddressMode.IndirectX, 6);
			case 0x31: return AND(AddressMode.IndirectY, 5);
			case 0x49: return EOR(AddressMode.Immediate, 2);
			case 0x45: return EOR(AddressMode.ZeroPage, 3);
			case 0x55: return EOR(AddressMode.ZeroPageX, 4);
			case 0x4D: return EOR(AddressMode.Absolute, 4);
			case 0x5D: return EOR(AddressMode.AbsoluteX, 4);
			case 0x59: return EOR(AddressMode.AbsoluteY, 4);
			case 0x41: return EOR(AddressMode.IndirectX, 6);
			case 0x51: return EOR(AddressMode.IndirectY, 5);
			case 0x09: return ORA(AddressMode.Immediate, 2);
			case 0x05: return ORA(AddressMode.ZeroPage, 3);
			case 0x15: return ORA(AddressMode.ZeroPageX, 4);
			case 0x0D: return ORA(AddressMode.Absolute, 4);
			case 0x1D: return ORA(AddressMode.AbsoluteX, 4);
			case 0x19: return ORA(AddressMode.AbsoluteY, 4);
			case 0x01: return ORA(AddressMode.IndirectX, 6);
			case 0x11: return ORA(AddressMode.IndirectY, 5);
			case 0x24: return BIT(AddressMode.ZeroPage, 3);
			case 0x2C: return BIT(AddressMode.Absolute, 4);

			//ADC, SBC, CMP, CPX, CPY
			case 0x69: return ADC(AddressMode.Immediate, 2);
			case 0x65: return ADC(AddressMode.ZeroPage, 3);
			case 0x75: return ADC(AddressMode.ZeroPageX, 4);
			case 0x6D: return ADC(AddressMode.Absolute, 4);
			case 0x7D: return ADC(AddressMode.AbsoluteX, 4);
			case 0x79: return ADC(AddressMode.AbsoluteY, 4);
			case 0x61: return ADC(AddressMode.IndirectX, 6);
			case 0x71: return ADC(AddressMode.IndirectY, 5);
			case 0xE9: return SBC(AddressMode.Immediate, 2);
			case 0xE5: return SBC(AddressMode.ZeroPage, 3);
			case 0xF5: return SBC(AddressMode.ZeroPageX, 4);
			case 0xED: return SBC(AddressMode.Absolute, 4);
			case 0xFD: return SBC(AddressMode.AbsoluteX, 4);
			case 0xF9: return SBC(AddressMode.AbsoluteY, 4);
			case 0xE1: return SBC(AddressMode.IndirectX, 6);
			case 0xF1: return SBC(AddressMode.IndirectY, 5);
			case 0xC9: return CPR(A, AddressMode.Immediate, 2);
			case 0xC5: return CPR(A, AddressMode.ZeroPage, 3);
			case 0xD5: return CPR(A, AddressMode.ZeroPageX, 4);
			case 0xCD: return CPR(A, AddressMode.Absolute, 4);
			case 0xDD: return CPR(A, AddressMode.AbsoluteX, 4);
			case 0xD9: return CPR(A, AddressMode.AbsoluteY, 4);
			case 0xC1: return CPR(A, AddressMode.IndirectX, 6);
			case 0xD1: return CPR(A, AddressMode.IndirectY, 5);
			case 0xE0: return CPR(X, AddressMode.Immediate, 2);
			case 0xE4: return CPR(X, AddressMode.ZeroPage, 3);
			case 0xEC: return CPR(X, AddressMode.Absolute, 4);
			case 0xC0: return CPR(Y, AddressMode.Immediate, 2);
			case 0xC4: return CPR(Y, AddressMode.ZeroPage, 3);
			case 0xCC: return CPR(Y, AddressMode.Absolute, 4);

			//INC, INX, INY, DEC, DEX, DEY
			case 0xE6: return INC(AddressMode.ZeroPage, 5);
			case 0xF6: return INC(AddressMode.ZeroPageX, 6);
			case 0xEE: return INC(AddressMode.Absolute, 6);
			case 0xFE: return INC(AddressMode.AbsoluteX, 7);
			case 0xE8: return INR(ref X, AddressMode.Implied, 2);
			case 0xC8: return INR(ref Y, AddressMode.Implied, 2);
			case 0xC6: return DEC(AddressMode.ZeroPage, 5);
			case 0xD6: return DEC(AddressMode.ZeroPageX, 6);
			case 0xCE: return DEC(AddressMode.Absolute, 6);
			case 0xDE: return DEC(AddressMode.AbsoluteX, 7);
			case 0xCA: return DER(ref X, AddressMode.Implied, 2);
			case 0x88: return DER(ref Y, AddressMode.Implied, 2);

			//ASL, LSR, ROL, ROR
			case 0x0A: return ASL(AddressMode.Accumulator, 2);
			case 0x06: return ASL(AddressMode.ZeroPage, 5);
			case 0x16: return ASL(AddressMode.ZeroPageX, 6);
			case 0x0E: return ASL(AddressMode.Absolute, 6);
			case 0x1E: return ASL(AddressMode.AbsoluteX, 7);
			case 0x4A: return LSR(AddressMode.Accumulator, 2);
			case 0x46: return LSR(AddressMode.ZeroPage, 5);
			case 0x56: return LSR(AddressMode.ZeroPageX, 6);
			case 0x4E: return LSR(AddressMode.Absolute, 6);
			case 0x5E: return LSR(AddressMode.AbsoluteX, 7);
			case 0x2A: return ROL(AddressMode.Accumulator, 2);
			case 0x26: return ROL(AddressMode.ZeroPage, 5);
			case 0x36: return ROL(AddressMode.ZeroPageX, 6);
			case 0x2E: return ROL(AddressMode.Absolute, 6);
			case 0x3E: return ROL(AddressMode.AbsoluteX, 7);
			case 0x6A: return ROR(AddressMode.Accumulator, 2);
			case 0x66: return ROR(AddressMode.ZeroPage, 5);
			case 0x76: return ROR(AddressMode.ZeroPageX, 6);
			case 0x6E: return ROR(AddressMode.Absolute, 6);
			case 0x7E: return ROR(AddressMode.AbsoluteX, 7);

			//JMP, JSR, RTS
			case 0x4C: return JMP(AddressMode.Absolute, 3);
			case 0x6C: return JMP(AddressMode.Indirect, 5);
			case 0x20: return JSR();
			case 0x60: return RTS();
			
			//BCC, BCS, BEQ, BMI, BNE, BPL, BVC, BVS
			// --- Optimized branches (inline relative addressing & penalty) ---
			case 0x90: { sbyte off = (sbyte)Fetch(); if ((status & MASK_C)==0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BCC
			case 0xB0: { sbyte off = (sbyte)Fetch(); if ((status & MASK_C)!=0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BCS
			case 0xF0: { sbyte off = (sbyte)Fetch(); if ((status & MASK_Z)!=0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BEQ
			case 0x30: { sbyte off = (sbyte)Fetch(); if ((status & MASK_N)!=0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BMI
			case 0xD0: { sbyte off = (sbyte)Fetch(); if ((status & MASK_Z)==0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BNE
			case 0x10: { sbyte off = (sbyte)Fetch(); if ((status & MASK_N)==0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BPL
			case 0x50: { sbyte off = (sbyte)Fetch(); if ((status & MASK_V)==0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BVC
			case 0x70: { sbyte off = (sbyte)Fetch(); if ((status & MASK_V)!=0) { ushort old=PC; PC=(ushort)(PC+off); return 2 + 1 + (((old ^ PC)&0xFF00)!=0 ? 1:0); } return 2; } // BVS

			//CLC, CLD, CLI, CLV, SEC, SED, SEI
			case 0x18: return FSC(FLAG_C, false, AddressMode.Implied, 2);
			case 0xD8: return FSC(FLAG_D, false, AddressMode.Implied, 2);
			case 0x58: return FSC(FLAG_I, false, AddressMode.Implied, 2);
			case 0xB8: return FSC(FLAG_V, false, AddressMode.Implied, 2);
			case 0x38: return FSC(FLAG_C, true, AddressMode.Implied, 2);
			case 0xF8: return FSC(FLAG_D, true, AddressMode.Implied, 2);
			case 0x78: return FSC(FLAG_I, true, AddressMode.Implied, 2);
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

	// Address resolution without delegate allocations
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private AddrResult Resolve(AddressMode mode) => mode switch {
		AddressMode.Implied => new AddrResult(0,0),
		AddressMode.Accumulator => new AddrResult(0,0),
		AddressMode.Immediate => new AddrResult(PC++,0),
		AddressMode.ZeroPage => new AddrResult(Fetch(),0),
		AddressMode.ZeroPageX => new AddrResult((byte)(Fetch()+X),0),
		AddressMode.ZeroPageY => new AddrResult((byte)(Fetch()+Y),0),
		AddressMode.Absolute => new AddrResult(Fetch16Bits(),0),
		AddressMode.AbsoluteX => ResolveAbsIndexed(X),
		AddressMode.AbsoluteY => ResolveAbsIndexed(Y),
		AddressMode.IndirectX => ResolveIndirectX(),
		AddressMode.IndirectY => ResolveIndirectY(),
		AddressMode.Indirect => ResolveIndirect(),
		AddressMode.Relative => ResolveRelative(),
		_ => new AddrResult(0,0)
	};

	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveAbsIndexed(byte idx) { ushort baseAddr = Fetch16Bits(); ushort eff=(ushort)(baseAddr+idx); return new AddrResult(eff, HasPageCrossPenalty(baseAddr,eff)?1:0); }
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveIndirectX() { byte zp=Fetch(); byte ptr=(byte)(zp+X); ushort a=(ushort)(bus.Read(ptr) | (bus.Read((byte)(ptr+1))<<8)); return new AddrResult(a,0);} 
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveIndirectY() { byte zp=Fetch(); ushort baseAddr=(ushort)(bus.Read(zp) | (bus.Read((byte)(zp+1))<<8)); ushort eff=(ushort)(baseAddr+Y); return new AddrResult(eff, HasPageCrossPenalty(baseAddr,eff)?1:0);} 
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveIndirect() { ushort ptr=Fetch16Bits(); byte lo=bus.Read(ptr); byte hi=(ptr & 0x00FF)==0x00FF ? bus.Read((ushort)(ptr & 0xFF00)) : bus.Read((ushort)(ptr+1)); ushort addr=(ushort)((hi<<8)|lo); return new AddrResult(addr,0);} 
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveRelative() { sbyte off=(sbyte)Fetch(); ushort target=(ushort)(PC+off); return new AddrResult(target, HasPageCrossPenalty(PC,target)?1:0);} 

	//Load/Store Operations (enum-based)
	private int LDR(ref byte r, AddressMode mode, int baseCycles) { var ar = Resolve(mode); r = bus.Read(ar.address); SetZN(r); return baseCycles + ar.extraCycles; }
	private int STR(ref byte r, AddressMode mode, int baseCycles) { var ar = Resolve(mode); bus.Write(ar.address, r); return baseCycles; }

	//Register Transfer
	private int TRR(ref byte r1, ref byte r2, AddressMode mode, int baseCycles) { r1 = r2; SetZN(r1); return baseCycles; }

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

	private int TSX(AddressMode mode, int baseCycles) {
		X = (byte)SP;
		SetZN(X);
		return baseCycles;
	}

	private int TXS(AddressMode mode, int baseCycles) {
		SP = X;
		return baseCycles;
	}

	private int PHA(AddressMode mode, int baseCycles) {
		StackPush(A);
		return baseCycles;
	}

	private int PHP(AddressMode mode, int baseCycles) {
		StackPush((byte)(status | (1 << FLAG_B) | (1 << FLAG_UNUSED)));
		return baseCycles;
	}

	private int PLA(AddressMode mode, int baseCycles) {
		A = StackPop();
		SetZN(A);
		return baseCycles;
	}

	private int PLP(AddressMode mode, int baseCycles) {
		status = StackPop();
		SetFlag(FLAG_UNUSED, true);
		SetFlag(FLAG_B, false);
		return baseCycles;
	}

	//Logical
	private int AND(AddressMode mode, int baseCycles) { var ar = Resolve(mode); A = (byte)(A & bus.Read(ar.address)); SetZNFast(A); return baseCycles + ar.extraCycles; }
	private int EOR(AddressMode mode, int baseCycles) { var ar = Resolve(mode); A = (byte)(A ^ bus.Read(ar.address)); SetZNFast(A); return baseCycles + ar.extraCycles; }
	private int ORA(AddressMode mode, int baseCycles) { var ar = Resolve(mode); A = (byte)(A | bus.Read(ar.address)); SetZNFast(A); return baseCycles + ar.extraCycles; }

	private int BIT(AddressMode mode, int baseCycles) {
		var ar = Resolve(mode);
		byte value = bus.Read(ar.address);
		// Clear Z,N,V then set them from computed result (branchless)
		status &= CLEAR_ZNV;
		if ( (A & value) == 0) status |= MASK_Z;
		status |= (byte)(value & 0xC0); // copy bits 6 (V) & 7 (N)
		return baseCycles + ar.extraCycles;
	}

	//Arithmetic
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int ADC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte m = bus.Read(ar.address); int carryIn = (status & MASK_C)!=0 ? 1:0; int sum = A + m + carryIn; byte result=(byte)sum; status &= 0x3C; if (sum>0xFF) status|=MASK_C; if (result==0) status|=MASK_Z; if ((result & 0x80)!=0) status|=MASK_N; if ((((~(A ^ m)) & (A ^ result)) & 0x80)!=0) status|=MASK_V; A=result; return baseCycles + ar.extraCycles; }

	private int SBC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte m = bus.Read(ar.address); int carryIn=(status & MASK_C)!=0?1:0; int value = m ^ 0xFF; int sum = A + value + carryIn; byte result=(byte)sum; status &= 0x3C; if (sum>0xFF) status|=MASK_C; if (result==0) status|=MASK_Z; if ((result & 0x80)!=0) status|=MASK_N; if ((((A ^ result) & (value ^ result)) & 0x80)!=0) status|=MASK_V; A=result; return baseCycles + ar.extraCycles; }

	private int CPR(byte r, AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte m = bus.Read(ar.address); int diff = r - m; status &= 0x7C; if (r>=m) status|=MASK_C; if ((byte)diff==0) status|=MASK_Z; if ((diff & 0x80)!=0) status|=MASK_N; return baseCycles + ar.extraCycles; }

	//Increments and Decrements
	private int INC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte result = (byte)(bus.Read(ar.address)+1); bus.Write(ar.address,result); SetZNFast(result); return baseCycles; }

	private int DEC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte result = (byte)(bus.Read(ar.address)-1); bus.Write(ar.address,result); SetZNFast(result); return baseCycles; }

	private int INR(ref byte r, AddressMode mode, int baseCycles) { r++; SetZNFast(r); return baseCycles; }

	private int DER(ref byte r, AddressMode mode, int baseCycles) { r--; SetZNFast(r); return baseCycles; }

	//Shifts
	private int ASL(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : bus.Read(ar.address); if ((value & 0x80)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)(value<<1); if (mode==AddressMode.Accumulator) A=result; else bus.Write(ar.address,result); SetZNFast(result); return baseCycles; }

	private int LSR(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : bus.Read(ar.address); if ((value & 0x01)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)(value>>1); if (mode==AddressMode.Accumulator) A=result; else bus.Write(ar.address,result); SetZNFast(result); return baseCycles; }

	private int ROL(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : bus.Read(ar.address); int oldCarry=(status & MASK_C)!=0 ? 1:0; if ((value & 0x80)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)((value<<1)|oldCarry); if (mode==AddressMode.Accumulator) A=result; else bus.Write(ar.address,result); SetZNFast(result); return baseCycles; }

	private int ROR(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : bus.Read(ar.address); int oldCarry=(status & MASK_C)!=0 ? 1:0; if ((value & 0x01)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)((value>>1) | (oldCarry!=0 ? 0x80:0)); if (mode==AddressMode.Accumulator) A=result; else bus.Write(ar.address,result); SetZNFast(result); return baseCycles; }

	//Jumps and Calls
	private int JMP(AddressMode mode, int baseCycles) {
		var ar = Resolve(mode);
		PC = ar.address;
		return baseCycles; // no page-cross penalty for these addressing modes
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

	//Branches handled inline in switch (old BIF helper removed)

	//Status Flag Changes
	private int FSC(int bit, bool state, AddressMode mode, int baseCycles) {
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

	// Scheduler-driven interrupt servicing (used when InlineInterruptChecks=false)
	public int ServicePendingInterrupts() {
		int cycles = 0;
		// NMI has priority over IRQ
		if (nmiRequested) { nmiRequested = false; cycles += NMI(); }
		// After NMI, IRQ may still be pending and allowed
		if (irqRequested && !GetFlag(FLAG_I)) { irqRequested = false; cycles += IRQ(); }
		return cycles;
	}

	private struct AddrResult {
		public ushort address;
		public int extraCycles;

		public AddrResult(ushort addr, int extra) {
			address = addr;
			extraCycles = extra;
		}
	}

	// Old delegate-based addressing helpers removed (now using Resolve(AddressMode))

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool HasPageCrossPenalty(ushort baseAddr, ushort effectiveAddr) => (baseAddr & 0xFF00) != (effectiveAddr & 0xFF00);

	public object GetState() => new CpuSharedState { A=A,X=X,Y=Y,status=status,PC=PC,SP=SP,irqRequested=irqRequested,nmiRequested=nmiRequested };
	public void SetState(object state) {
		if (state is CpuSharedState s) { A=s.A;X=s.X;Y=s.Y;status=s.status;PC=s.PC;SP=s.SP;irqRequested=s.irqRequested;nmiRequested=s.nmiRequested; return; }
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
