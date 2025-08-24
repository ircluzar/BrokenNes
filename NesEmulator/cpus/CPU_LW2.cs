using System;
using System.Runtime.CompilerServices;
namespace NesEmulator
{
public sealed class CPU_LW2 : ICPU {
	// Metadata defaults
	public string CoreName => "Exp. F-LW2";
	public string Description => "Based on the Famiclone (FMC) core, this failed variant optimizes performance but has some issues.";
	public int Performance => 5;
	public int Rating => 2;
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

	// When true, unknown opcodes are treated as 2-cycle NOPs instead of throwing CpuCrashException
	public bool IgnoreInvalidOpcodes { get; set; } = false;

	// === Opcode Dispatch Table ===
	// We replace the large per-instruction switch with a pre-built delegate table.
	// Each entry returns the cycle count for the executed opcode. This enables
	// future experimentation with function pointers or metadata-driven decoding.
	private Func<int>?[] opcodeExec = new Func<int>?[256];

	public CPU_LW2(Bus bus) {
		A = X = Y = 0;
		PC = 0x0000;
		SP = 0x0000;
		status = 0;

		this.bus = bus;

		irqRequested = false;
		nmiRequested = false;
		BuildOpcodeTable();
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

	// === Fast Bus Path ===
	// Inline fast-path for $0000-$1FFF (internal RAM mirrors) to bypass Bus.Read() call overhead.
	// Additional specialized PRG fetch fast path can be added later once mapper bank pointers exist.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private byte ReadFast(ushort address)
	{
		if ((address & 0xE000) == 0) // < $2000 (includes mirrors)
			return bus.ram[address & 0x07FF];
		return bus.Read(address);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void WriteFast(ushort address, byte value)
	{
		if ((address & 0xE000) == 0) { bus.ram[address & 0x07FF] = value; return; }
		bus.Write(address, value);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private byte Fetch() { return ReadFast(PC++); }

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
		var exec = opcodeExec[opcode];
		if (exec != null) return exec();
		if (IgnoreInvalidOpcodes) return 2; // treat unknown as NOP
		throw new CpuCrashException($"Bad opcode {opcode:X2} at {(PC-1):X4}");
	}

	private void BuildOpcodeTable()
	{
		var t = opcodeExec;
		// Local helper creators
		Func<int, Func<int>> C = cycles => () => cycles;
		Func<int> NopZp = () => { Fetch(); return 3; };
		Func<int> NopZpX = () => { Fetch(); return 4; };

		// Unofficial NOP sets
		foreach (var op in new byte[]{0x1A,0x3A,0x5A,0x7A,0xDA,0xFA}) t[op] = C(2);
		foreach (var op in new byte[]{0x04,0x44,0x64}) t[op] = NopZp;
		foreach (var op in new byte[]{0x14,0x34,0x54,0x74,0xD4,0xF4}) t[op] = NopZpX;
		t[0x0C] = () => { Fetch16Bits(); return 4; };
		foreach (var op in new byte[]{0x1C,0x3C,0x5C,0x7C,0xDC,0xFC}) t[op] = () => { var ar = ResolveAbsIndexed(X); return 4 + ar.extraCycles; };

		// BRK/NOP/RTI
		t[0x00]=()=>BRK(); t[0xEA]=()=>NOP(); t[0x40]=()=>RTI();

		// LDA
		t[0xA9]=()=>LDR(ref A,AddressMode.Immediate,2); t[0xA5]=()=>LDR(ref A,AddressMode.ZeroPage,3); t[0xB5]=()=>LDR(ref A,AddressMode.ZeroPageX,4); t[0xAD]=()=>LDR(ref A,AddressMode.Absolute,4); t[0xBD]=()=>LDR(ref A,AddressMode.AbsoluteX,4); t[0xB9]=()=>LDR(ref A,AddressMode.AbsoluteY,4); t[0xA1]=()=>LDR(ref A,AddressMode.IndirectX,6); t[0xB1]=()=>LDR(ref A,AddressMode.IndirectY,5);
		// LDX
		t[0xA2]=()=>LDR(ref X,AddressMode.Immediate,2); t[0xA6]=()=>LDR(ref X,AddressMode.ZeroPage,3); t[0xB6]=()=>LDR(ref X,AddressMode.ZeroPageY,4); t[0xAE]=()=>LDR(ref X,AddressMode.Absolute,4); t[0xBE]=()=>LDR(ref X,AddressMode.AbsoluteY,4);
		// LDY
		t[0xA0]=()=>LDR(ref Y,AddressMode.Immediate,2); t[0xA4]=()=>LDR(ref Y,AddressMode.ZeroPage,3); t[0xB4]=()=>LDR(ref Y,AddressMode.ZeroPageX,4); t[0xAC]=()=>LDR(ref Y,AddressMode.Absolute,4); t[0xBC]=()=>LDR(ref Y,AddressMode.AbsoluteX,4);

		// STA/STX/STY
		t[0x85]=()=>STR(ref A,AddressMode.ZeroPage,3); t[0x95]=()=>STR(ref A,AddressMode.ZeroPageX,4); t[0x8D]=()=>STR(ref A,AddressMode.Absolute,4); t[0x9D]=()=>STR(ref A,AddressMode.AbsoluteX,5); t[0x99]=()=>STR(ref A,AddressMode.AbsoluteY,5); t[0x81]=()=>STR(ref A,AddressMode.IndirectX,6); t[0x91]=()=>STR(ref A,AddressMode.IndirectY,6);
		t[0x86]=()=>STR(ref X,AddressMode.ZeroPage,3); t[0x96]=()=>STR(ref X,AddressMode.ZeroPageY,4); t[0x8E]=()=>STR(ref X,AddressMode.Absolute,4);
		t[0x84]=()=>STR(ref Y,AddressMode.ZeroPage,3); t[0x94]=()=>STR(ref Y,AddressMode.ZeroPageX,4); t[0x8C]=()=>STR(ref Y,AddressMode.Absolute,4);

		// TAX/TAY/TXA/TYA
		t[0xAA]=()=>TRR(ref X, ref A, AddressMode.Implied,2); t[0xA8]=()=>TRR(ref Y, ref A, AddressMode.Implied,2); t[0x8A]=()=>TRR(ref A, ref X, AddressMode.Implied,2); t[0x98]=()=>TRR(ref A, ref Y, AddressMode.Implied,2);

		// TSX,TXS,PHA,PHP,PLA,PLP
		t[0xBA]=()=>TSX(AddressMode.Implied,2); t[0x9A]=()=>TXS(AddressMode.Implied,2); t[0x48]=()=>PHA(AddressMode.Implied,3); t[0x08]=()=>PHP(AddressMode.Implied,3); t[0x68]=()=>PLA(AddressMode.Implied,4); t[0x28]=()=>PLP(AddressMode.Implied,4);

		// Logic (AND/EOR/ORA/BIT)
		t[0x29]=()=>AND(AddressMode.Immediate,2); t[0x25]=()=>AND(AddressMode.ZeroPage,3); t[0x35]=()=>AND(AddressMode.ZeroPageX,4); t[0x2D]=()=>AND(AddressMode.Absolute,4); t[0x3D]=()=>AND(AddressMode.AbsoluteX,4); t[0x39]=()=>AND(AddressMode.AbsoluteY,4); t[0x21]=()=>AND(AddressMode.IndirectX,6); t[0x31]=()=>AND(AddressMode.IndirectY,5);
		t[0x49]=()=>EOR(AddressMode.Immediate,2); t[0x45]=()=>EOR(AddressMode.ZeroPage,3); t[0x55]=()=>EOR(AddressMode.ZeroPageX,4); t[0x4D]=()=>EOR(AddressMode.Absolute,4); t[0x5D]=()=>EOR(AddressMode.AbsoluteX,4); t[0x59]=()=>EOR(AddressMode.AbsoluteY,4); t[0x41]=()=>EOR(AddressMode.IndirectX,6); t[0x51]=()=>EOR(AddressMode.IndirectY,5);
		t[0x09]=()=>ORA(AddressMode.Immediate,2); t[0x05]=()=>ORA(AddressMode.ZeroPage,3); t[0x15]=()=>ORA(AddressMode.ZeroPageX,4); t[0x0D]=()=>ORA(AddressMode.Absolute,4); t[0x1D]=()=>ORA(AddressMode.AbsoluteX,4); t[0x19]=()=>ORA(AddressMode.AbsoluteY,4); t[0x01]=()=>ORA(AddressMode.IndirectX,6); t[0x11]=()=>ORA(AddressMode.IndirectY,5);
		t[0x24]=()=>BIT(AddressMode.ZeroPage,3); t[0x2C]=()=>BIT(AddressMode.Absolute,4);

		// ADC / SBC
		t[0x69]=()=>ADC(AddressMode.Immediate,2); t[0x65]=()=>ADC(AddressMode.ZeroPage,3); t[0x75]=()=>ADC(AddressMode.ZeroPageX,4); t[0x6D]=()=>ADC(AddressMode.Absolute,4); t[0x7D]=()=>ADC(AddressMode.AbsoluteX,4); t[0x79]=()=>ADC(AddressMode.AbsoluteY,4); t[0x61]=()=>ADC(AddressMode.IndirectX,6); t[0x71]=()=>ADC(AddressMode.IndirectY,5);
		t[0xE9]=()=>SBC(AddressMode.Immediate,2); t[0xE5]=()=>SBC(AddressMode.ZeroPage,3); t[0xF5]=()=>SBC(AddressMode.ZeroPageX,4); t[0xED]=()=>SBC(AddressMode.Absolute,4); t[0xFD]=()=>SBC(AddressMode.AbsoluteX,4); t[0xF9]=()=>SBC(AddressMode.AbsoluteY,4); t[0xE1]=()=>SBC(AddressMode.IndirectX,6); t[0xF1]=()=>SBC(AddressMode.IndirectY,5);

		// CMP family (A/X/Y)
		t[0xC9]=()=>CPR(A,AddressMode.Immediate,2); t[0xC5]=()=>CPR(A,AddressMode.ZeroPage,3); t[0xD5]=()=>CPR(A,AddressMode.ZeroPageX,4); t[0xCD]=()=>CPR(A,AddressMode.Absolute,4); t[0xDD]=()=>CPR(A,AddressMode.AbsoluteX,4); t[0xD9]=()=>CPR(A,AddressMode.AbsoluteY,4); t[0xC1]=()=>CPR(A,AddressMode.IndirectX,6); t[0xD1]=()=>CPR(A,AddressMode.IndirectY,5);
		t[0xE0]=()=>CPR(X,AddressMode.Immediate,2); t[0xE4]=()=>CPR(X,AddressMode.ZeroPage,3); t[0xEC]=()=>CPR(X,AddressMode.Absolute,4);
		t[0xC0]=()=>CPR(Y,AddressMode.Immediate,2); t[0xC4]=()=>CPR(Y,AddressMode.ZeroPage,3); t[0xCC]=()=>CPR(Y,AddressMode.Absolute,4);

		// INC/DEC/INX/INY/DEX/DEY
		t[0xE6]=()=>INC(AddressMode.ZeroPage,5); t[0xF6]=()=>INC(AddressMode.ZeroPageX,6); t[0xEE]=()=>INC(AddressMode.Absolute,6); t[0xFE]=()=>INC(AddressMode.AbsoluteX,7);
		t[0xE8]=()=>INR(ref X,AddressMode.Implied,2); t[0xC8]=()=>INR(ref Y,AddressMode.Implied,2);
		t[0xC6]=()=>DEC(AddressMode.ZeroPage,5); t[0xD6]=()=>DEC(AddressMode.ZeroPageX,6); t[0xCE]=()=>DEC(AddressMode.Absolute,6); t[0xDE]=()=>DEC(AddressMode.AbsoluteX,7);
		t[0xCA]=()=>DER(ref X,AddressMode.Implied,2); t[0x88]=()=>DER(ref Y,AddressMode.Implied,2);

		// Shift / Rotate
		t[0x0A]=()=>ASL(AddressMode.Accumulator,2); t[0x06]=()=>ASL(AddressMode.ZeroPage,5); t[0x16]=()=>ASL(AddressMode.ZeroPageX,6); t[0x0E]=()=>ASL(AddressMode.Absolute,6); t[0x1E]=()=>ASL(AddressMode.AbsoluteX,7);
		t[0x4A]=()=>LSR(AddressMode.Accumulator,2); t[0x46]=()=>LSR(AddressMode.ZeroPage,5); t[0x56]=()=>LSR(AddressMode.ZeroPageX,6); t[0x4E]=()=>LSR(AddressMode.Absolute,6); t[0x5E]=()=>LSR(AddressMode.AbsoluteX,7);
		t[0x2A]=()=>ROL(AddressMode.Accumulator,2); t[0x26]=()=>ROL(AddressMode.ZeroPage,5); t[0x36]=()=>ROL(AddressMode.ZeroPageX,6); t[0x2E]=()=>ROL(AddressMode.Absolute,6); t[0x3E]=()=>ROL(AddressMode.AbsoluteX,7);
		t[0x6A]=()=>ROR(AddressMode.Accumulator,2); t[0x66]=()=>ROR(AddressMode.ZeroPage,5); t[0x76]=()=>ROR(AddressMode.ZeroPageX,6); t[0x6E]=()=>ROR(AddressMode.Absolute,6); t[0x7E]=()=>ROR(AddressMode.AbsoluteX,7);

		// Jumps / Calls
		t[0x4C]=()=>JMP(AddressMode.Absolute,3); t[0x6C]=()=>JMP(AddressMode.Indirect,5); t[0x20]=()=>JSR(); t[0x60]=()=>RTS();

		// Branches
		t[0x90]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_C)==0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };
		t[0xB0]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_C)!=0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };
		t[0xF0]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_Z)!=0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };
		t[0x30]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_N)!=0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };
		t[0xD0]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_Z)==0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };
		t[0x10]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_N)==0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };
		t[0x50]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_V)==0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };
		t[0x70]=()=>{ sbyte off=(sbyte)Fetch(); if((status & MASK_V)!=0){ ushort old=PC; PC=(ushort)(PC+off); return 2+1+(((old^PC)&0xFF00)!=0?1:0);} return 2; };

		// Flag set/clear
		t[0x18]=()=>FSC(FLAG_C,false,AddressMode.Implied,2); t[0xD8]=()=>FSC(FLAG_D,false,AddressMode.Implied,2); t[0x58]=()=>FSC(FLAG_I,false,AddressMode.Implied,2); t[0xB8]=()=>FSC(FLAG_V,false,AddressMode.Implied,2); t[0x38]=()=>FSC(FLAG_C,true,AddressMode.Implied,2); t[0xF8]=()=>FSC(FLAG_D,true,AddressMode.Implied,2); t[0x78]=()=>FSC(FLAG_I,true,AddressMode.Implied,2);
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
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveIndirectX() { byte zp=Fetch(); byte ptr=(byte)(zp+X); ushort a=(ushort)(ReadFast(ptr) | (ReadFast((byte)(ptr+1))<<8)); return new AddrResult(a,0);} 
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveIndirectY() { byte zp=Fetch(); ushort baseAddr=(ushort)(ReadFast(zp) | (ReadFast((byte)(zp+1))<<8)); ushort eff=(ushort)(baseAddr+Y); return new AddrResult(eff, HasPageCrossPenalty(baseAddr,eff)?1:0);} 
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveIndirect() { ushort ptr=Fetch16Bits(); byte lo=ReadFast(ptr); byte hi=(ptr & 0x00FF)==0x00FF ? ReadFast((ushort)(ptr & 0xFF00)) : ReadFast((ushort)(ptr+1)); ushort addr=(ushort)((hi<<8)|lo); return new AddrResult(addr,0);} 
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private AddrResult ResolveRelative() { sbyte off=(sbyte)Fetch(); ushort target=(ushort)(PC+off); return new AddrResult(target, HasPageCrossPenalty(PC,target)?1:0);} 

	//Load/Store Operations (enum-based)
	private int LDR(ref byte r, AddressMode mode, int baseCycles) { var ar = Resolve(mode); r = ReadFast(ar.address); SetZN(r); return baseCycles + ar.extraCycles; }
	private int STR(ref byte r, AddressMode mode, int baseCycles) { var ar = Resolve(mode); WriteFast(ar.address, r); return baseCycles; }

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
	private int AND(AddressMode mode, int baseCycles) { var ar = Resolve(mode); A = (byte)(A & ReadFast(ar.address)); SetZNFast(A); return baseCycles + ar.extraCycles; }
	private int EOR(AddressMode mode, int baseCycles) { var ar = Resolve(mode); A = (byte)(A ^ ReadFast(ar.address)); SetZNFast(A); return baseCycles + ar.extraCycles; }
	private int ORA(AddressMode mode, int baseCycles) { var ar = Resolve(mode); A = (byte)(A | ReadFast(ar.address)); SetZNFast(A); return baseCycles + ar.extraCycles; }

	private int BIT(AddressMode mode, int baseCycles) {
		var ar = Resolve(mode);
		byte value = ReadFast(ar.address);
		// Clear Z,N,V then set them from computed result (branchless)
		status &= CLEAR_ZNV;
		if ( (A & value) == 0) status |= MASK_Z;
		status |= (byte)(value & 0xC0); // copy bits 6 (V) & 7 (N)
		return baseCycles + ar.extraCycles;
	}

	//Arithmetic
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int ADC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte m = ReadFast(ar.address); int carryIn = (status & MASK_C)!=0 ? 1:0; int sum = A + m + carryIn; byte result=(byte)sum; status &= 0x3C; if (sum>0xFF) status|=MASK_C; if (result==0) status|=MASK_Z; if ((result & 0x80)!=0) status|=MASK_N; if ((((~(A ^ m)) & (A ^ result)) & 0x80)!=0) status|=MASK_V; A=result; return baseCycles + ar.extraCycles; }

	private int SBC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte m = ReadFast(ar.address); int carryIn=(status & MASK_C)!=0?1:0; int value = m ^ 0xFF; int sum = A + value + carryIn; byte result=(byte)sum; status &= 0x3C; if (sum>0xFF) status|=MASK_C; if (result==0) status|=MASK_Z; if ((result & 0x80)!=0) status|=MASK_N; if ((((A ^ result) & (value ^ result)) & 0x80)!=0) status|=MASK_V; A=result; return baseCycles + ar.extraCycles; }

	private int CPR(byte r, AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte m = ReadFast(ar.address); int diff = r - m; status &= 0x7C; if (r>=m) status|=MASK_C; if ((byte)diff==0) status|=MASK_Z; if ((diff & 0x80)!=0) status|=MASK_N; return baseCycles + ar.extraCycles; }

	//Increments and Decrements
	private int INC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte result = (byte)(ReadFast(ar.address)+1); WriteFast(ar.address,result); SetZNFast(result); return baseCycles; }

	private int DEC(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte result = (byte)(ReadFast(ar.address)-1); WriteFast(ar.address,result); SetZNFast(result); return baseCycles; }

	private int INR(ref byte r, AddressMode mode, int baseCycles) { r++; SetZNFast(r); return baseCycles; }

	private int DER(ref byte r, AddressMode mode, int baseCycles) { r--; SetZNFast(r); return baseCycles; }

	//Shifts
	private int ASL(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : ReadFast(ar.address); if ((value & 0x80)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)(value<<1); if (mode==AddressMode.Accumulator) A=result; else WriteFast(ar.address,result); SetZNFast(result); return baseCycles; }

	private int LSR(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : ReadFast(ar.address); if ((value & 0x01)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)(value>>1); if (mode==AddressMode.Accumulator) A=result; else WriteFast(ar.address,result); SetZNFast(result); return baseCycles; }

	private int ROL(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : ReadFast(ar.address); int oldCarry=(status & MASK_C)!=0 ? 1:0; if ((value & 0x80)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)((value<<1)|oldCarry); if (mode==AddressMode.Accumulator) A=result; else WriteFast(ar.address,result); SetZNFast(result); return baseCycles; }

	private int ROR(AddressMode mode, int baseCycles) { var ar = Resolve(mode); byte value = mode == AddressMode.Accumulator ? A : ReadFast(ar.address); int oldCarry=(status & MASK_C)!=0 ? 1:0; if ((value & 0x01)!=0) status|=MASK_C; else status &= unchecked((byte)~MASK_C); byte result=(byte)((value>>1) | (oldCarry!=0 ? 0x80:0)); if (mode==AddressMode.Accumulator) A=result; else WriteFast(ar.address,result); SetZNFast(result); return baseCycles; }

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
