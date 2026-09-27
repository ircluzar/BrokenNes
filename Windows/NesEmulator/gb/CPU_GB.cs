using System;

namespace NesEmulator.Gb;

/// <summary>
/// What the SM83 sees of the machine. Every call is exactly one M-cycle (4 T-cycles at single speed):
/// the bus advances the rest of the system by that cycle, then performs the access. The interrupt
/// registers are exposed separately because the CPU samples them between cycles.
/// </summary>
public interface IGbCpuBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
    /// <summary>An internal M-cycle with no memory access.</summary>
    void Idle();
    /// <summary>IE &amp; IF &amp; 0x1F, sampled without spending time.</summary>
    byte PendingInterrupts { get; }
    /// <summary>Acknowledge (clear) interrupt <paramref name="bit"/> in IF.</summary>
    void AcknowledgeInterrupt(int bit);
    /// <summary>STOP executed. Returns true when the bus handled it as a CGB speed switch.</summary>
    bool Stop();
}

/// <summary>
/// Sharp SM83 (LR35902), the Game Boy / Game Boy Color CPU. Instruction-stepped, with every memory access
/// and internal cycle issued to the bus in hardware order, so the timer, PPU and DMA see the CPU's accesses at
/// the right M-cycle. Written from Pan Docs and the gbdev opcode tables.
/// </summary>
public sealed class CPU_GB : IGbCpu
{
    public string Id => "SM83";
    ushort IGbCpu.PC => PC;
    public const byte FZ = 0x80, FN = 0x40, FH = 0x20, FC = 0x10;

    public byte A, F, B, C, D, E, H, L;
    public ushort SP, PC;
    public bool Ime;
    public bool Halted { get; private set; }
    public bool Stopped { get; private set; }
    /// <summary>The CPU hit one of the 11 opcodes that lock the real chip up.</summary>
    public bool Locked { get; private set; }
    public long Instructions { get; private set; }

    private readonly IGbCpuBus bus;
    private bool imePending;      // EI: IME becomes 1 after the next instruction
    private bool haltBug;         // HALT with IME=0 and an interrupt pending: next opcode byte is read twice
    private bool eiBeforeThis;    // the instruction being executed directly follows EI

    public CPU_GB(IGbCpuBus bus) { this.bus = bus; }

    public ushort AF { get => (ushort)(A << 8 | F); set { A = (byte)(value >> 8); F = (byte)(value & 0xF0); } }
    public ushort BC { get => (ushort)(B << 8 | C); set { B = (byte)(value >> 8); C = (byte)value; } }
    public ushort DE { get => (ushort)(D << 8 | E); set { D = (byte)(value >> 8); E = (byte)value; } }
    public ushort HL { get => (ushort)(H << 8 | L); set { H = (byte)(value >> 8); L = (byte)value; } }

    /// <summary>Register file right after the boot ROM hands over (Pan Docs "Power Up Sequence").</summary>
    public void ResetPostBoot(GbModel model, bool cgbGame)
    {
        if (model == GbModel.Cgb)
        {
            // A=$11 is how software detects a Game Boy Color. DMG games on a CGB get the compatibility values.
            AF = cgbGame ? (ushort)0x1180 : (ushort)0x1100; BC = 0x0000;
            DE = cgbGame ? (ushort)0xFF56 : (ushort)0x0008; HL = cgbGame ? (ushort)0x000D : (ushort)0x007C;
        }
        else
        {
            AF = 0x01B0; BC = 0x0013; DE = 0x00D8; HL = 0x014D;
        }
        SP = 0xFFFE; PC = 0x0100;
        Ime = false; imePending = false; Halted = false; Stopped = false; Locked = false; haltBug = false;
    }

    // ------------------------------------------------------------------ bus helpers
    private byte Fetch()
    {
        byte v = bus.Read(PC);
        if (haltBug) haltBug = false; else PC++;
        return v;
    }
    private ushort Fetch16() { byte lo = Fetch(); return (ushort)(lo | Fetch() << 8); }
    private void Push(ushort v) { SP--; bus.Write(SP, (byte)(v >> 8)); SP--; bus.Write(SP, (byte)v); }
    private ushort Pop() { byte lo = bus.Read(SP++); return (ushort)(lo | bus.Read(SP++) << 8); }

    private byte GetR(int r) => r switch
    {
        0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L, 6 => bus.Read(HL), _ => A,
    };
    private void SetR(int r, byte v)
    {
        switch (r)
        {
            case 0: B = v; break; case 1: C = v; break; case 2: D = v; break; case 3: E = v; break;
            case 4: H = v; break; case 5: L = v; break; case 6: bus.Write(HL, v); break; default: A = v; break;
        }
    }
    private ushort GetRR(int p) => p switch { 0 => BC, 1 => DE, 2 => HL, _ => SP };
    private void SetRR(int p, ushort v)
    {
        switch (p) { case 0: BC = v; break; case 1: DE = v; break; case 2: HL = v; break; default: SP = v; break; }
    }
    private bool Cond(int cc) => cc switch
    {
        0 => (F & FZ) == 0, 1 => (F & FZ) != 0, 2 => (F & FC) == 0, _ => (F & FC) != 0,
    };

    // ------------------------------------------------------------------ ALU
    private void Alu(int op, byte v)
    {
        int a = A, r;
        switch (op)
        {
            case 0: // ADD
                r = a + v; F = (byte)(((r & 0xFF) == 0 ? FZ : 0) | ((a & 0xF) + (v & 0xF) > 0xF ? FH : 0) | (r > 0xFF ? FC : 0)); A = (byte)r; break;
            case 1: // ADC
            {
                int c = (F & FC) != 0 ? 1 : 0; r = a + v + c;
                F = (byte)(((r & 0xFF) == 0 ? FZ : 0) | ((a & 0xF) + (v & 0xF) + c > 0xF ? FH : 0) | (r > 0xFF ? FC : 0)); A = (byte)r; break;
            }
            case 2: // SUB
                r = a - v; F = (byte)(((r & 0xFF) == 0 ? FZ : 0) | FN | ((a & 0xF) < (v & 0xF) ? FH : 0) | (r < 0 ? FC : 0)); A = (byte)r; break;
            case 3: // SBC
            {
                int c = (F & FC) != 0 ? 1 : 0; r = a - v - c;
                F = (byte)(((r & 0xFF) == 0 ? FZ : 0) | FN | ((a & 0xF) - (v & 0xF) - c < 0 ? FH : 0) | (r < 0 ? FC : 0)); A = (byte)r; break;
            }
            case 4: A &= v; F = (byte)((A == 0 ? FZ : 0) | FH); break;
            case 5: A ^= v; F = (byte)(A == 0 ? FZ : 0); break;
            case 6: A |= v; F = (byte)(A == 0 ? FZ : 0); break;
            default: // CP
                r = a - v; F = (byte)(((r & 0xFF) == 0 ? FZ : 0) | FN | ((a & 0xF) < (v & 0xF) ? FH : 0) | (r < 0 ? FC : 0)); break;
        }
    }

    private byte Inc(byte v) { byte r = (byte)(v + 1); F = (byte)((F & FC) | (r == 0 ? FZ : 0) | ((v & 0xF) == 0xF ? FH : 0)); return r; }
    private byte Dec(byte v) { byte r = (byte)(v - 1); F = (byte)((F & FC) | FN | (r == 0 ? FZ : 0) | ((v & 0xF) == 0 ? FH : 0)); return r; }

    private void AddHL(ushort v)
    {
        int hl = HL, r = hl + v;
        F = (byte)((F & FZ) | ((hl & 0xFFF) + (v & 0xFFF) > 0xFFF ? FH : 0) | (r > 0xFFFF ? FC : 0));
        HL = (ushort)r;
    }

    /// <summary>SP + signed e8 with flags from the low byte (ADD SP,e and LD HL,SP+e).</summary>
    private ushort SpPlus(sbyte e)
    {
        int sp = SP, v = (byte)e;
        F = (byte)(((sp & 0xF) + (v & 0xF) > 0xF ? FH : 0) | ((sp & 0xFF) + v > 0xFF ? FC : 0));
        return (ushort)(sp + e);
    }

    private byte Cb(int op, int bit, byte v)
    {
        int c = (F & FC) != 0 ? 1 : 0; byte r;
        switch (op)
        {
            case 0: r = (byte)(v << 1 | v >> 7); F = (byte)(v >> 7 != 0 ? FC : 0); break;             // RLC
            case 1: r = (byte)(v >> 1 | v << 7); F = (byte)((v & 1) != 0 ? FC : 0); break;            // RRC
            case 2: r = (byte)(v << 1 | c); F = (byte)(v >> 7 != 0 ? FC : 0); break;                  // RL
            case 3: r = (byte)(v >> 1 | c << 7); F = (byte)((v & 1) != 0 ? FC : 0); break;            // RR
            case 4: r = (byte)(v << 1); F = (byte)(v >> 7 != 0 ? FC : 0); break;                      // SLA
            case 5: r = (byte)(v >> 1 | v & 0x80); F = (byte)((v & 1) != 0 ? FC : 0); break;          // SRA
            case 6: r = (byte)(v << 4 | v >> 4); F = 0; break;                                        // SWAP
            default: r = (byte)(v >> 1); F = (byte)((v & 1) != 0 ? FC : 0); break;                    // SRL
        }
        if (r == 0) F |= FZ;
        return r;
    }

    private void Daa()
    {
        int a = A;
        if ((F & FN) == 0)
        {
            if ((F & FC) != 0 || a > 0x99) { a += 0x60; F |= FC; }
            if ((F & FH) != 0 || (a & 0x0F) > 0x09) a += 0x06;
        }
        else
        {
            if ((F & FC) != 0) a -= 0x60;
            if ((F & FH) != 0) a -= 0x06;
        }
        A = (byte)a;
        F = (byte)((F & (FN | FC)) | (A == 0 ? FZ : 0));
    }

    // ------------------------------------------------------------------ execution
    /// <summary>Run one instruction, one interrupt dispatch, or one halted M-cycle.</summary>
    public void Step()
    {
        if (Locked) { bus.Idle(); return; }
        if (Halted)
        {
            bus.Idle();
            if (bus.PendingInterrupts != 0) Halted = false;
            return;
        }

        bool enableAfter = imePending;
        imePending = false;

        if (Ime && bus.PendingInterrupts != 0)
        {
            Dispatch();
            if (enableAfter) Ime = true;
            return;
        }

        eiBeforeThis = enableAfter;
        byte op = Fetch();
        Execute(op);
        Instructions++;
        if (enableAfter && op != 0xF3) Ime = true;
    }

    private void Dispatch()
    {
        Ime = false;
        bus.Idle();
        bus.Idle();
        SP--; bus.Write(SP, (byte)(PC >> 8));
        // The vector is chosen after the high byte is pushed: if that push overwrote IE (SP was $0000),
        // the interrupt can be cancelled and the CPU jumps to $0000 (Mooneye ie_push).
        byte pending = bus.PendingInterrupts;
        SP--; bus.Write(SP, (byte)PC);
        if (pending == 0) { PC = 0x0000; bus.Idle(); return; }
        int bit = System.Numerics.BitOperations.TrailingZeroCount(pending);
        bus.AcknowledgeInterrupt(bit);
        PC = (ushort)(0x40 + bit * 8);
        bus.Idle();
    }

    private void Execute(byte op)
    {
        // Regular blocks first: LD r,r' and ALU A,r.
        if (op >= 0x40 && op < 0x80)
        {
            if (op == 0x76) { Halt(); return; }
            SetR(op >> 3 & 7, GetR(op & 7));
            return;
        }
        if (op >= 0x80 && op < 0xC0) { Alu(op >> 3 & 7, GetR(op & 7)); return; }

        switch (op)
        {
            case 0x00: return;                                                        // NOP
            case 0x01: case 0x11: case 0x21: case 0x31: SetRR(op >> 4, Fetch16()); return;
            case 0x02: bus.Write(BC, A); return;
            case 0x12: bus.Write(DE, A); return;
            case 0x22: bus.Write(HL, A); HL++; return;
            case 0x32: bus.Write(HL, A); HL--; return;
            case 0x0A: A = bus.Read(BC); return;
            case 0x1A: A = bus.Read(DE); return;
            case 0x2A: A = bus.Read(HL); HL++; return;
            case 0x3A: A = bus.Read(HL); HL--; return;
            case 0x03: case 0x13: case 0x23: case 0x33: SetRR(op >> 4, (ushort)(GetRR(op >> 4) + 1)); bus.Idle(); return;
            case 0x0B: case 0x1B: case 0x2B: case 0x3B: SetRR(op >> 4, (ushort)(GetRR(op >> 4) - 1)); bus.Idle(); return;
            case 0x04: case 0x0C: case 0x14: case 0x1C: case 0x24: case 0x2C: case 0x34: case 0x3C:
            {
                int r = op >> 3 & 7; SetR(r, Inc(GetR(r))); return;
            }
            case 0x05: case 0x0D: case 0x15: case 0x1D: case 0x25: case 0x2D: case 0x35: case 0x3D:
            {
                int r = op >> 3 & 7; SetR(r, Dec(GetR(r))); return;
            }
            case 0x06: case 0x0E: case 0x16: case 0x1E: case 0x26: case 0x2E: case 0x36: case 0x3E:
            {
                byte v = Fetch(); SetR(op >> 3 & 7, v); return;
            }
            case 0x07: A = (byte)(A << 1 | A >> 7); F = (byte)((A & 1) != 0 ? FC : 0); return;                         // RLCA
            case 0x0F: F = (byte)((A & 1) != 0 ? FC : 0); A = (byte)(A >> 1 | A << 7); return;                         // RRCA
            case 0x17: { int c = (F & FC) != 0 ? 1 : 0; F = (byte)(A >> 7 != 0 ? FC : 0); A = (byte)(A << 1 | c); return; }        // RLA
            case 0x1F: { int c = (F & FC) != 0 ? 1 : 0; F = (byte)((A & 1) != 0 ? FC : 0); A = (byte)(A >> 1 | c << 7); return; }  // RRA
            case 0x08: { ushort a = Fetch16(); bus.Write(a, (byte)SP); bus.Write((ushort)(a + 1), (byte)(SP >> 8)); return; }
            case 0x09: case 0x19: case 0x29: case 0x39: AddHL(GetRR(op >> 4)); bus.Idle(); return;
            case 0x10: Fetch(); if (!bus.Stop()) Stopped = true; return;                                                // STOP
            case 0x18: { sbyte e = (sbyte)Fetch(); PC = (ushort)(PC + e); bus.Idle(); return; }
            case 0x20: case 0x28: case 0x30: case 0x38:
            {
                sbyte e = (sbyte)Fetch();
                if (Cond(op >> 3 & 3)) { PC = (ushort)(PC + e); bus.Idle(); }
                return;
            }
            case 0x27: Daa(); return;
            case 0x2F: A = (byte)~A; F |= FN | FH; return;
            case 0x37: F = (byte)((F & FZ) | FC); return;
            case 0x3F: F = (byte)((F & FZ) | ((F & FC) ^ FC)); return;

            case 0xC0: case 0xC8: case 0xD0: case 0xD8:
                bus.Idle();
                if (Cond(op >> 3 & 3)) { PC = Pop(); bus.Idle(); }
                return;
            case 0xC9: PC = Pop(); bus.Idle(); return;
            case 0xD9: PC = Pop(); bus.Idle(); Ime = true; return;                                                        // RETI
            case 0xC1: case 0xD1: case 0xE1: BC_DE_HL_Set(op >> 4 & 3, Pop()); return;
            case 0xF1: AF = Pop(); return;
            case 0xC5: case 0xD5: case 0xE5: bus.Idle(); Push(GetRR(op >> 4 & 3)); return;
            case 0xF5: bus.Idle(); Push(AF); return;
            case 0xC2: case 0xCA: case 0xD2: case 0xDA:
            {
                ushort a = Fetch16();
                if (Cond(op >> 3 & 3)) { PC = a; bus.Idle(); }
                return;
            }
            case 0xC3: PC = Fetch16(); bus.Idle(); return;
            case 0xE9: PC = HL; return;
            case 0xC4: case 0xCC: case 0xD4: case 0xDC:
            {
                ushort a = Fetch16();
                if (Cond(op >> 3 & 3)) { bus.Idle(); Push(PC); PC = a; }
                return;
            }
            case 0xCD: { ushort a = Fetch16(); bus.Idle(); Push(PC); PC = a; return; }
            case 0xC7: case 0xCF: case 0xD7: case 0xDF: case 0xE7: case 0xEF: case 0xF7: case 0xFF:
                bus.Idle(); Push(PC); PC = (ushort)(op & 0x38); return;
            case 0xC6: case 0xCE: case 0xD6: case 0xDE: case 0xE6: case 0xEE: case 0xF6: case 0xFE:
                Alu(op >> 3 & 7, Fetch()); return;
            case 0xCB: ExecuteCb(Fetch()); return;
            case 0xE0: { byte n = Fetch(); bus.Write((ushort)(0xFF00 | n), A); return; }
            case 0xF0: { byte n = Fetch(); A = bus.Read((ushort)(0xFF00 | n)); return; }
            case 0xE2: bus.Write((ushort)(0xFF00 | C), A); return;
            case 0xF2: A = bus.Read((ushort)(0xFF00 | C)); return;
            case 0xEA: bus.Write(Fetch16(), A); return;
            case 0xFA: A = bus.Read(Fetch16()); return;
            case 0xE8: { sbyte e = (sbyte)Fetch(); SP = SpPlus(e); bus.Idle(); bus.Idle(); return; }
            case 0xF8: { sbyte e = (sbyte)Fetch(); HL = SpPlus(e); bus.Idle(); return; }
            case 0xF9: SP = HL; bus.Idle(); return;
            case 0xF3: Ime = false; imePending = false; return;                                                           // DI
            case 0xFB: imePending = true; return;                                                                         // EI
            default:
                // $D3 $DB $DD $E3 $E4 $EB $EC $ED $F4 $FC $FD: the real CPU locks up until power-off.
                Locked = true; return;
        }
    }

    private void BC_DE_HL_Set(int p, ushort v) => SetRR(p, v);

    private void Halt()
    {
        if (Ime || bus.PendingInterrupts == 0) { Halted = true; return; }
        // EI; HALT with an interrupt already pending: the interrupt is taken right away and returns to the HALT,
        // which then halts normally (Tail Gator and Quarth do this; treating it as the HALT bug crashes them).
        if (eiBeforeThis) { PC--; return; }
        // IME=0 with an interrupt already pending: HALT exits at once and the next opcode byte is read twice.
        haltBug = true;
    }

    private void ExecuteCb(byte op)
    {
        int r = op & 7, bit = op >> 3 & 7;
        switch (op >> 6)
        {
            case 0: SetR(r, Cb(bit, 0, GetR(r))); return;
            case 1: F = (byte)((F & FC) | FH | ((GetR(r) >> bit & 1) == 0 ? FZ : 0)); return;       // BIT
            case 2: SetR(r, (byte)(GetR(r) & ~(1 << bit))); return;                                  // RES
            default: SetR(r, (byte)(GetR(r) | 1 << bit)); return;                                    // SET
        }
    }

    /// <summary>Leave STOP (a joypad press does this on hardware).</summary>
    public void Wake() { Stopped = false; Halted = false; }

    // ------------------------------------------------------------------ savestate
    public void SaveState(System.IO.BinaryWriter w)
    {
        w.Write(A); w.Write(F); w.Write(B); w.Write(C); w.Write(D); w.Write(E); w.Write(H); w.Write(L);
        w.Write(SP); w.Write(PC); w.Write(Ime); w.Write(imePending); w.Write(Halted); w.Write(Stopped); w.Write(Locked); w.Write(haltBug);
    }
    public void LoadState(System.IO.BinaryReader r)
    {
        A = r.ReadByte(); F = r.ReadByte(); B = r.ReadByte(); C = r.ReadByte(); D = r.ReadByte(); E = r.ReadByte(); H = r.ReadByte(); L = r.ReadByte();
        SP = r.ReadUInt16(); PC = r.ReadUInt16(); Ime = r.ReadBoolean(); imePending = r.ReadBoolean(); Halted = r.ReadBoolean();
        Stopped = r.ReadBoolean(); Locked = r.ReadBoolean(); haltBug = r.ReadBoolean();
    }
}
