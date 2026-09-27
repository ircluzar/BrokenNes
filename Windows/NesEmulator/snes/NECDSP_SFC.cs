using System;
using System.Text;

namespace NesEmulator.Snes;

/// <summary>
/// NEC µPD77C25 - the DSP-1 (and DSP-2/3/4) cartridge chip. It is low-level emulation: the chip
/// runs Nintendo's own program, supplied by the user as an 8KB firmware image (2048 24-bit program
/// words then 1024 16-bit data words, little-endian - the common "dsp1b.rom" layout). The firmware
/// is never bundled.
///
/// The SNES sees two ports: DR (data, 8 or 16 bits wide per SR.DRC) and SR (status, read-only,
/// high byte). The chip is driven lazily: every port access first runs the DSP up to the CPU's
/// master clock. A DSP spinning on "JRQM $" (waiting for the host) can do nothing until the host
/// touches DR, so the rest of that time slice is skipped instead of emulated.
/// </summary>
public sealed class NECDSP_SFC : ISnesCoprocessor
{
    /// <summary>DSP-1 crystal. One instruction per clock.</summary>
    public const double DefaultFrequency = 7_600_000;
    private const double MasterFrequency = 21_477_272;

    public enum Mapping
    {
        /// <summary>HiROM boards (Super Mario Kart): $00-1F/$80-9F, DR $6000-$6FFF, SR $7000-$7FFF.</summary>
        HiRom,
        /// <summary>LoROM up to 1MB (Pilotwings): $30-3F/$B0-BF, DR $8000-$BFFF, SR $C000-$FFFF.</summary>
        LoRom1MB,
        /// <summary>LoROM 2MB+: $60-6F/$E0-EF, DR $0000-$3FFF, SR $4000-$7FFF.</summary>
        LoRom2MB,
    }

    public string Name { get; }
    private readonly Mapping map;
    private readonly double dspPerMaster;

    private readonly uint[] program = new uint[2048];
    private readonly ushort[] dataRom = new ushort[1024];
    private readonly ushort[] dataRam = new ushort[256];
    private readonly ushort[] stack = new ushort[4];

    private int pc, sp, rp, dp;
    private ushort a, b, tr, trb, dr, sr, k, l, m, n, so;
    private Flags fa, fb;

    private long dspClock;          // instructions executed (or skipped) since reset
    private long lastMaster;        // master clock at the last access, for the report
    public long Instructions { get; private set; }

    private struct Flags { public bool Ov0, Ov1, Z, C, S0, S1; }

    // SR bits
    private const ushort Rqm = 0x8000, Drs = 0x1000, Drc = 0x0400;

    public NECDSP_SFC(byte[] firmware, Mapping mapping, string name = "DSP-1", double frequency = DefaultFrequency)
    {
        if (firmware.Length < 8192) throw new ArgumentException($"µPD77C25 firmware must be 8192 bytes (got {firmware.Length})");
        for (int i = 0; i < 2048; i++) program[i] = (uint)(firmware[i * 3] | firmware[i * 3 + 1] << 8 | firmware[i * 3 + 2] << 16);
        for (int i = 0; i < 1024; i++) dataRom[i] = (ushort)(firmware[6144 + i * 2] | firmware[6144 + i * 2 + 1] << 8);
        map = mapping;
        Name = name;
        dspPerMaster = frequency / MasterFrequency;
        Reset();
    }

    /// <summary>The usual mapping for a cartridge with this layout.</summary>
    public static Mapping MappingFor(SnesCartridge cart) =>
        cart.HiRom ? Mapping.HiRom : cart.Rom.Length > 0x100000 ? Mapping.LoRom2MB : Mapping.LoRom1MB;

    public void Reset()
    {
        pc = sp = rp = dp = 0;
        a = b = tr = trb = dr = k = l = m = n = so = 0;
        sr = 0;
        fa = fb = default;
        Array.Clear(dataRam);
        Array.Clear(stack);
        dspClock = 0;
        Instructions = 0;
    }

    // =====================================================================================
    //  Bus side
    // =====================================================================================

    public bool Owns(uint bank, uint offset)
    {
        switch (map)
        {
            case Mapping.HiRom: return (bank & 0x60) == 0 && offset >= 0x6000 && offset < 0x8000;
            case Mapping.LoRom1MB: return (bank & 0x70) == 0x30 && offset >= 0x8000;
            default: return (bank & 0x70) == 0x60 && offset < 0x8000;
        }
    }

    private bool IsStatus(uint offset) => map switch
    {
        Mapping.HiRom => (offset & 0x1000) != 0,
        Mapping.LoRom1MB => (offset & 0x4000) != 0,
        _ => (offset & 0x4000) != 0,
    };

    public byte Read(uint bank, uint offset, long masterClock)
    {
        CatchUp(masterClock);
        if (IsStatus(offset)) return (byte)(sr >> 8);
        return ReadDr();
    }

    public void Write(uint bank, uint offset, byte value, long masterClock)
    {
        CatchUp(masterClock);
        if (!IsStatus(offset)) WriteDr(value);   // SR is read-only from the SNES side
    }

    /// <summary>
    /// Debug: the SNES side of the conversation. 'c' = 8-bit write (a command byte), 'r' = 8-bit read,
    /// 'W' / 'R' = completed 16-bit word written / read. Used to record reference traces for the
    /// homemade DSP-1 (<see cref="DSP1_SFC"/>).
    /// </summary>
    public Action<char, ushort>? PortLog;

    private byte ReadDr()
    {
        if ((sr & Drc) != 0) { sr &= unchecked((ushort)~Rqm); PortLog?.Invoke('r', (byte)dr); return (byte)dr; }   // 8-bit transfers
        if ((sr & Drs) == 0) { sr |= Drs; return (byte)dr; }
        sr &= unchecked((ushort)~(Rqm | Drs));
        PortLog?.Invoke('R', dr);
        return (byte)(dr >> 8);
    }

    private void WriteDr(byte value)
    {
        if ((sr & Drc) != 0) { sr &= unchecked((ushort)~Rqm); dr = (ushort)((dr & 0xFF00) | value); PortLog?.Invoke('c', value); return; }
        if ((sr & Drs) == 0) { sr |= Drs; dr = (ushort)((dr & 0xFF00) | value); return; }
        sr &= unchecked((ushort)~(Rqm | Drs));
        dr = (ushort)((dr & 0x00FF) | value << 8);
        PortLog?.Invoke('W', dr);
    }

    private void CatchUp(long masterClock)
    {
        lastMaster = masterClock;
        long target = (long)(masterClock * dspPerMaster);
        while (dspClock < target)
        {
            uint op = program[pc];
            // "JRQM $" / "JNRQM $": the program is waiting for the SNES, and only a DR access by
            // the SNES can end the wait - so the rest of this slice would all be the same loop.
            if (op >> 22 == 2 && ((op >> 2) & 0x7FF) == pc)
            {
                uint brch = (op >> 13) & 0x1FF;
                if ((brch == 0x0BE && (sr & Rqm) != 0) || (brch == 0x0BC && (sr & Rqm) == 0)) { dspClock = target; break; }
            }
            Step();
            dspClock++;
        }
    }

    // =====================================================================================
    //  Core
    // =====================================================================================

    /// <summary>Executes one instruction.</summary>
    public void Step()
    {
        uint op = program[pc];
        pc = (pc + 1) & 0x7FF;
        switch (op >> 22)
        {
            case 0: ExecOp(op); break;
            case 1: ExecOp(op); sp = (sp - 1) & 3; pc = stack[sp]; break;   // RT: an OP, then return
            case 2: ExecJp(op); break;
            case 3: Load((ushort)(op >> 6), (int)(op & 15)); break;
        }
        // The multiplier runs every cycle on whatever K and L hold: M = high word, N = low word
        // of the 31-bit signed product (shifted up one so both halves are Q15).
        int product = (short)k * (short)l;
        m = (ushort)(product >> 15);
        n = (ushort)(product << 1);
        Instructions++;
    }

    private void ExecOp(uint op)
    {
        int pselect = (int)(op >> 20) & 3;
        int alu = (int)(op >> 16) & 15;
        bool accB = ((op >> 15) & 1) != 0;
        int dpl = (int)(op >> 13) & 3;
        int dphm = (int)(op >> 9) & 15;
        bool rpdcr = ((op >> 8) & 1) != 0;
        int src = (int)(op >> 4) & 15;
        int dst = (int)op & 15;

        ushort idb = src switch
        {
            0 => trb,
            1 => a,
            2 => b,
            3 => tr,
            4 => (ushort)dp,
            5 => (ushort)rp,
            6 => dataRom[rp],
            7 => (ushort)(0x8000 - (fa.S1 ? 1 : 0)),   // SGN: saturation value from ACCA's S1
            8 => ReadDrInternal(),                      // DR, and request the next transfer
            9 => dr,                                    // DR without flag change
            10 => sr,
            11 or 12 => 0,                              // serial input: not wired on SNES boards
            13 => k,
            14 => l,
            _ => dataRam[dp],
        };

        if (alu != 0)
        {
            int p = pselect switch { 0 => dataRam[dp], 1 => idb, 2 => m, _ => n };
            int q = accB ? b : a;
            ref Flags f = ref accB ? ref fb : ref fa;
            // ADC/SBB and SHL1 take their carry from the OTHER accumulator: low word in one,
            // high word in the other makes 32-bit arithmetic two instructions.
            int c = (accB ? fa.C : fb.C) ? 1 : 0;
            int r;
            bool arith = false, add = false;
            switch (alu)
            {
                case 1: r = q | p; break;
                case 2: r = q & p; break;
                case 3: r = q ^ p; break;
                case 4: r = q - p; arith = true; break;
                case 5: r = q + p; arith = add = true; break;
                case 6: r = q - p - c; arith = true; break;
                case 7: r = q + p + c; arith = add = true; break;
                case 8: p = 1; r = q - 1; arith = true; break;
                case 9: p = 1; r = q + 1; arith = add = true; break;
                case 10: r = ~q; break;
                case 11: r = (q >> 1) | (q & 0x8000); break;   // SHR1: arithmetic
                case 12: r = (q << 1) | c; break;              // SHL1
                case 13: r = (q << 2) | 3; break;              // SHL2 shifts ones in
                case 14: r = (q << 4) | 15; break;             // SHL4 shifts ones in
                default: r = (q << 8) | (q >> 8); break;       // XCHG
            }
            int r16 = r & 0xFFFF;
            if (arith)
            {
                f.C = add ? r > 0xFFFF : r < 0;
                f.Ov0 = ((add ? (q ^ r16) & ~(q ^ p) : (q ^ r16) & (q ^ p)) & 0x8000) != 0;
                if (f.Ov0) { f.S1 = f.Ov1 ^ (r16 & 0x8000) == 0; f.Ov1 = !f.Ov1; }
            }
            else
            {
                f.C = alu switch { 11 => (q & 1) != 0, 12 => (q & 0x8000) != 0, _ => false };
                f.Ov0 = f.Ov1 = false;
            }
            f.S0 = (r16 & 0x8000) != 0;
            f.Z = r16 == 0;
            if (!f.Ov1) f.S1 = f.S0;
            if (accB) b = (ushort)r16; else a = (ushort)r16;
        }

        Load(idb, dst);

        if (dst != 4)
        {
            switch (dpl)
            {
                case 1: dp = (dp & 0xF0) | ((dp + 1) & 0x0F); break;
                case 2: dp = (dp & 0xF0) | ((dp - 1) & 0x0F); break;
                case 3: dp &= 0xF0; break;
            }
            dp ^= dphm << 4;
        }
        if (dst != 5 && rpdcr) rp = (rp - 1) & 0x3FF;
    }

    private ushort ReadDrInternal() { sr |= Rqm; return dr; }

    private void Load(ushort id, int dst)
    {
        switch (dst)
        {
            case 0: break;
            case 1: a = id; break;
            case 2: b = id; break;
            case 3: tr = id; break;
            case 4: dp = id & 0xFF; break;
            case 5: rp = id & 0x3FF; break;
            case 6: dr = id; sr |= Rqm; break;                                  // hand a result to the SNES
            case 7: sr = (ushort)((sr & 0x907C) | (id & ~0x907C)); break;        // RQM/DRS/unused bits are not writable
            case 8: case 9: so = id; break;                                      // serial out: unwired
            case 10: k = id; break;
            case 11: k = id; l = dataRom[rp]; break;                             // KLR
            case 12: l = id; k = dataRam[dp | 0x40]; break;                      // KLM
            case 13: l = id; break;
            case 14: trb = id; break;
            default: dataRam[dp] = id; break;
        }
    }

    private void ExecJp(uint op)
    {
        int brch = (int)(op >> 13) & 0x1FF;
        int na = (int)(op >> 2) & 0x7FF;
        bool take;
        switch (brch)
        {
            case 0x100: take = true; break;                                           // JMP
            case 0x140: stack[sp] = (ushort)pc; sp = (sp + 1) & 3; take = true; break; // CALL
            case 0x080: take = !fa.C; break;
            case 0x082: take = fa.C; break;
            case 0x084: take = !fb.C; break;
            case 0x086: take = fb.C; break;
            case 0x088: take = !fa.Z; break;
            case 0x08A: take = fa.Z; break;
            case 0x08C: take = !fb.Z; break;
            case 0x08E: take = fb.Z; break;
            case 0x090: take = !fa.Ov0; break;
            case 0x092: take = fa.Ov0; break;
            case 0x094: take = !fb.Ov0; break;
            case 0x096: take = fb.Ov0; break;
            case 0x098: take = !fa.Ov1; break;
            case 0x09A: take = fa.Ov1; break;
            case 0x09C: take = !fb.Ov1; break;
            case 0x09E: take = fb.Ov1; break;
            case 0x0A0: take = !fa.S0; break;
            case 0x0A2: take = fa.S0; break;
            case 0x0A4: take = !fb.S0; break;
            case 0x0A6: take = fb.S0; break;
            case 0x0A8: take = !fa.S1; break;
            case 0x0AA: take = fa.S1; break;
            case 0x0AC: take = !fb.S1; break;
            case 0x0AE: take = fb.S1; break;
            case 0x0B0: take = (dp & 0x0F) == 0x00; break;
            case 0x0B1: take = (dp & 0x0F) != 0x00; break;
            case 0x0B2: take = (dp & 0x0F) == 0x0F; break;
            case 0x0B3: take = (dp & 0x0F) != 0x0F; break;
            case 0x0B4: take = true; break;    // JNSIAK: serial input never acknowledges... treated as idle
            case 0x0B6: take = false; break;   // JSIAK
            case 0x0B8: take = true; break;    // JNSOAK
            case 0x0BA: take = false; break;   // JSOAK
            case 0x0BC: take = (sr & Rqm) == 0; break;
            case 0x0BE: take = (sr & Rqm) != 0; break;
            default: take = false; break;
        }
        if (take) pc = na;
    }

    public string Describe() =>
        $"{Name} pc=${pc:X3} sp={sp} dp=${dp:X2} rp=${rp:X3} a=${a:X4} b=${b:X4} dr=${dr:X4} sr=${sr:X4} k=${k:X4} l=${l:X4} " +
        $"instr={Instructions:N0} dspClock={dspClock:N0} (master {lastMaster:N0})";

    // =====================================================================================
    //  Disassembler (debug tooling)
    // =====================================================================================

    private static readonly string[] SrcNames = { "TRB", "A", "B", "TR", "DP", "RP", "ROM", "SGN", "DR", "DRNF", "SR", "SIM", "SIL", "K", "L", "MEM" };
    private static readonly string[] DstNames = { "NON", "A", "B", "TR", "DP", "RP", "DR", "SR", "SOL", "SOM", "K", "KLR", "KLM", "L", "TRB", "MEM" };
    private static readonly string[] AluNames = { "NOP", "OR", "AND", "XOR", "SUB", "ADD", "SBB", "ADC", "DEC", "INC", "CMP", "SHR1", "SHL1", "SHL2", "SHL4", "XCHG" };
    private static readonly string[] PNames = { "RAM", "IDB", "M", "N" };

    public string Disassemble(int address)
    {
        uint op = program[address & 0x7FF];
        var sb = new StringBuilder($"{address:X3}: {op:X6}  ");
        switch (op >> 22)
        {
            case 2:
                sb.Append($"JP  brch={(op >> 13) & 0x1FF:X3} -> {(op >> 2) & 0x7FF:X3}");
                break;
            case 3:
                sb.Append($"LD  #{(op >> 6) & 0xFFFF:X4} -> {DstNames[op & 15]}");
                break;
            default:
                sb.Append(op >> 22 == 1 ? "RT  " : "OP  ");
                int alu = (int)(op >> 16) & 15;
                if (alu != 0) sb.Append($"{AluNames[alu]} {(((op >> 15) & 1) != 0 ? "B" : "A")},{PNames[(op >> 20) & 3]}  ");
                sb.Append($"mov {SrcNames[(op >> 4) & 15]}->{DstNames[op & 15]}");
                int dpl = (int)(op >> 13) & 3, dphm = (int)(op >> 9) & 15;
                if (dpl != 0) sb.Append(dpl switch { 1 => " DP++", 2 => " DP--", _ => " DPCLR" });
                if (dphm != 0) sb.Append($" DP^={dphm << 4:X2}");
                if (((op >> 8) & 1) != 0) sb.Append(" RP--");
                break;
        }
        return sb.ToString();
    }
}
