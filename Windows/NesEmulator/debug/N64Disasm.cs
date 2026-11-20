// [brokennes_int2] BrokenNes's IDebugDisassembler over cor64's R4300Disassembler (VR4300) and RspDisassembler.
// Lands at Windows/NesEmulator/debug/N64Disasm.cs (MERGE_PLAN.md).
//
// The N64 is fixed-length (4 bytes, big-endian). IDebugDisassembler.Classify sees only the first two bytes (op, op1):
// that is enough for J/JAL, the branches, REGIMM (bltzal/bgezal are calls) and COP1 branches, but not for SPECIAL
// (jr / jalr / syscall / break live in the funct field, byte 3). ClassifyWord takes the whole word and is exact; the
// host should call it when it has the four bytes (the gap, and the proposed one-line interface change, are in CONTRACT.md).

using System;
using NesEmulator.Debugging;
using cor64.Debug;

namespace NesEmulator.N64;

public sealed class DisasmR4300 : IDebugDisassembler
{
    public static readonly DisasmR4300 Instance = new(rsp: false);
    /// <summary>The RSP (its own subset plus the vector unit; addresses are IMEM offsets).</summary>
    public static readonly DisasmR4300 Rsp = new(rsp: true);

    private readonly bool _rsp;
    private DisasmR4300(bool rsp) { _rsp = rsp; }

    /// <summary>Everything except SPECIAL and ERET: exact. Those two need the funct field (byte 3), so they come back as None; use ClassifyWord.</summary>
    public InstrClass Classify(byte op, byte op1, in DecodeContext ctx) =>
        Of(op >> 2, ((op & 3) << 3) | (op1 >> 5), op1 & 31, fn: -1);

    /// <summary>Exact length and flow of the instruction word (big-endian as stored).</summary>
    public InstrClass ClassifyWord(uint w) => Of((int)(w >> 26), (int)(w >> 21) & 31, (int)(w >> 16) & 31, (int)w & 63);

    /// <param name="fn">The funct field, or -1 when it is not known (Classify sees two bytes only).</param>
    private static InstrClass Of(int opcode, int rs, int rt, int fn)
    {
        FlowKind f = opcode switch
        {
            0 => fn switch
            {
                8 => rs == 31 ? FlowKind.Return : FlowKind.Jump,           // jr ra / jr rX
                9 => FlowKind.Call,                                         // jalr
                12 or 13 => FlowKind.Software,                              // syscall, break
                48 or 49 or 50 or 51 or 52 or 54 => FlowKind.Software,      // traps
                _ => FlowKind.None,                                         // also fn == -1 (unknown)
            },
            1 => rt switch
            {
                16 or 17 or 18 or 19 => FlowKind.Call,                      // bltzal, bgezal(l): bgezal r0 is the "bal" call
                0 or 1 or 2 or 3 => FlowKind.Branch,
                8 or 9 or 10 or 11 or 12 or 14 => FlowKind.Software,        // trap immediates
                _ => FlowKind.None,
            },
            2 => FlowKind.Jump,                                             // j
            3 => FlowKind.Call,                                             // jal
            4 or 5 or 6 or 7 or 20 or 21 or 22 or 23 => FlowKind.Branch,   // beq bne blez bgtz and the likely forms
            16 when rs == 16 && fn == 24 => FlowKind.Return,                // eret
            17 when rs == 8 => FlowKind.Branch,                             // bc1f / bc1t (l)
            _ => FlowKind.None,
        };
        return new InstrClass(4, f, 0);   // MIPS calls push nothing: the return address goes in ra (the call stack follows jal / jr ra)
    }

    public string Format(uint pc, ReadOnlySpan<byte> bytes, in DecodeContext ctx, Func<uint, string?>? label)
    {
        if (bytes.Length < 4) return "(short read)";
        uint w = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        AddressLabeler? l = label == null ? null : a => label((uint)a) ?? null!;
        if (_rsp) return RspDisassembler.Disassemble(w, pc & 0xFFC, l);
        return R4300Disassembler.Disassemble(w, (ulong)(long)(int)pc, l);   // KSEG addresses are sign-extended 64-bit on the VR4300
    }
}
