// Public register access and save states. The register-dump and Serializer plumbing of BizHawk's Z80A (RegisterValue, IDictionary dumps, Serializer.Sync) is replaced by
// plain properties and a BinaryWriter/BinaryReader pair; nothing of it was kept.

using System;
using System.IO;

namespace NesEmulator.Sega;

public sealed partial class Z80<TBus>
{
    // ---- 8-bit registers
    public byte A { get => Regs[rA]; set => Regs[rA] = value; }
    public byte F { get => Regs[rF]; set => Regs[rF] = value; }
    public byte B { get => Regs[rB]; set => Regs[rB] = value; }
    public byte C { get => Regs[rC]; set => Regs[rC] = value; }
    public byte D { get => Regs[rD]; set => Regs[rD] = value; }
    public byte E { get => Regs[rE]; set => Regs[rE] = value; }
    public byte H { get => Regs[rH]; set => Regs[rH] = value; }
    public byte L { get => Regs[rL]; set => Regs[rL] = value; }
    public byte IXH { get => Regs[rIxh]; set => Regs[rIxh] = value; }
    public byte IXL { get => Regs[rIxl]; set => Regs[rIxl] = value; }
    public byte IYH { get => Regs[rIyh]; set => Regs[rIyh] = value; }
    public byte IYL { get => Regs[rIyl]; set => Regs[rIyl] = value; }
    /// <summary>The interrupt vector base register.</summary>
    public byte I { get => Regs[rI]; set => Regs[rI] = value; }
    /// <summary>The refresh counter (all eight bits; the CPU only counts the low seven).</summary>
    public byte R { get => Regs[rR]; set => Regs[rR] = value; }

    // ---- 16-bit registers and pairs
    public ushort AF { get => Pair(rF, rA); set => SetPair(rF, rA, value); }
    public ushort BC { get => Pair(rC, rB); set => SetPair(rC, rB, value); }
    public ushort DE { get => Pair(rE, rD); set => SetPair(rE, rD, value); }
    public ushort HL { get => Pair(rL, rH); set => SetPair(rL, rH, value); }
    public ushort IX { get => Pair(rIxl, rIxh); set => SetPair(rIxl, rIxh, value); }
    public ushort IY { get => Pair(rIyl, rIyh); set => SetPair(rIyl, rIyh, value); }
    public ushort SP { get => Pair(rSPl, rSPh); set => SetPair(rSPl, rSPh, value); }
    public ushort PC { get => Pair(rPCl, rPCh); set => SetPair(rPCl, rPCh, value); }
    /// <summary>The internal WZ register, which the documentation calls MEMPTR (it shows through BIT n,(HL) in flag bits 5 and 3).</summary>
    public ushort WZ { get => Pair(rZ, rW); set => SetPair(rZ, rW, value); }

    // ---- the shadow set
    public ushort AF2 { get => Pair(rF_s, rA_s); set => SetPair(rF_s, rA_s, value); }
    public ushort BC2 { get => Pair(rC_s, rB_s); set => SetPair(rC_s, rB_s, value); }
    public ushort DE2 { get => Pair(rE_s, rD_s); set => SetPair(rE_s, rD_s, value); }
    public ushort HL2 { get => Pair(rL_s, rH_s); set => SetPair(rL_s, rH_s, value); }

    private ushort Pair(ushort lo, ushort hi) => (ushort)(Regs[lo] | Regs[hi] << 8);
    private void SetPair(ushort lo, ushort hi, ushort v) { Regs[lo] = (byte)v; Regs[hi] = (byte)(v >> 8); }

    // ---- interrupt state
    public bool IFF1 { get => iff1; set => iff1 = value; }
    public bool IFF2 { get => iff2; set => iff2 = value; }
    /// <summary>The interrupt mode, 0 to 2.</summary>
    public int InterruptMode
    {
        get => interruptMode;
        set
        {
            if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value), value, "invalid interrupt mode");
            interruptMode = value;
        }
    }

    /// <summary>The Q latch the SCF and CCF instructions look at: F as the last instruction left it if it changed the flags, otherwise 0 (a DD or FD prefix also clears it).</summary>
    public byte Q { get => q; set => q = value; }

    /// <summary>True right after an EI: the interrupt is held off for one more instruction. (The test vectors' "ei".)</summary>
    public bool EiPending { get => ei; set => ei = value; }

    /// <summary>True right after LD A,I or LD A,R. (The test vectors' "p".)</summary>
    public bool LdAirJustRan { get => pFlag; set => pFlag = value; }

    // ---- save state

    private const int StateVersion = 1;

    /// <summary>Writes the CPU state (registers, the instruction in flight, latches, the cycle counter). The bus is not part of it.</summary>
    public void SaveState(BinaryWriter w)
    {
        w.Write(StateVersion);
        w.Write(Regs);
        for (int i = 0; i < cur_instr.Length; i++) w.Write(cur_instr[i]);
        w.Write(instr_pntr); w.Write(irq_pntr); w.Write(IRQS);
        w.Write(opcode); w.Write(pre); w.Write(nextPre);
        w.Write(I_skip); w.Write(boundary); w.Write(halted); w.Write(prefixRun);
        w.Write(iff1); w.Write(iff2); w.Write(interruptMode);
        w.Write(nmiLine); w.Write(nmiPending);
        w.Write(ei); w.Write(eiNew); w.Write(pFlag); w.Write(pNext);
        w.Write(q); w.Write(flagsWritten);
        w.Write(Irq); w.Write(Wait); w.Write(NmosInterruptBug);
        w.Write(TotalCycles);
    }

    /// <summary>Restores what <see cref="SaveState"/> wrote.</summary>
    public void LoadState(BinaryReader r)
    {
        int version = r.ReadInt32();
        if (version != StateVersion) throw new InvalidDataException($"Z80 state version {version} is not supported");
        r.Read(Regs, 0, Regs.Length);
        for (int i = 0; i < cur_instr.Length; i++) cur_instr[i] = r.ReadUInt16();
        instr_pntr = r.ReadInt32(); irq_pntr = r.ReadInt32(); IRQS = r.ReadInt32();
        opcode = r.ReadByte(); pre = r.ReadInt32(); nextPre = r.ReadInt32();
        I_skip = r.ReadBoolean(); boundary = r.ReadBoolean(); halted = r.ReadBoolean(); prefixRun = r.ReadInt32();
        iff1 = r.ReadBoolean(); iff2 = r.ReadBoolean(); interruptMode = r.ReadInt32();
        nmiLine = r.ReadBoolean(); nmiPending = r.ReadBoolean();
        ei = r.ReadBoolean(); eiNew = r.ReadBoolean(); pFlag = r.ReadBoolean(); pNext = r.ReadBoolean();
        q = r.ReadByte(); flagsWritten = r.ReadBoolean();
        Irq = r.ReadBoolean(); Wait = r.ReadBoolean(); NmosInterruptBug = r.ReadBoolean();
        TotalCycles = r.ReadInt64();
    }
}
