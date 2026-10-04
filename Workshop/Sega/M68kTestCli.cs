using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--m68ktest</c>: verifies the 68000 core (<c>NesEmulator.Sega.M68k</c>).
/// <list type="bullet">
/// <item><c>--m68ktest [--set 680x0|m68000|both] [--dir680 d] [--dirmame d] [--only substr] [--max-failures N] [--jobs N] [--json out.json] [--poison N] [--no-exclusions] [--no-tolerances]</c>:
/// SingleStepTests/680x0 (gzipped JSON, from a reference implementation written to the published documentation) and SingleStepTests/m68000 (binary, from MAME's microcoded core, which is
/// derived from the chip). Each test runs one instruction from a given register and memory state; the final registers, status register, prefetch queue, every written byte, the cycle count and
/// every bus transaction (kind, clocks, address, size, data, in order, idle runs merged) are compared. What is not compared is of two kinds, both counted and reported per file and listed
/// in <c>sega/THIRD_PARTY_NOTICES.md</c>: <b>exclusions</b> (a whole vector the other set contradicts, see <see cref="Exclusion"/>) and <b>tolerances</b> (one field of a vector not compared, see <see cref="Tolerance"/>).</item>
/// <item><c>--m68ktest --bench [--seconds s]</c>: throughput of the core on hand-assembled kernels.</item>
/// <item><c>--m68ktest --selftest</c>: hand-written checks of what the vectors cannot reach (interrupts, STOP, reset, privilege, TAS on the Genesis, address-error frames, halt).</item>
/// </list>
/// Exit code 0 = every vector that is held against the core passed.
/// </summary>
internal static class M68kTestCli
{
    public const string DefaultDir680 = @"C:\BrokenNes-tools\sega\test-sets\singlesteptests-680x0\68000\v1";
    public const string DefaultDirMame = @"C:\BrokenNes-tools\sega\test-sets\singlesteptests-m68000\v1";

    public static int Run(string[] args)
    {
        try
        {
            if (Flag(args, "--bench")) return M68kBench.Run(args);
            if (Flag(args, "--selftest")) return M68kSelfTest.Run(args);
            return M68kVectors.Run(args);
        }
        catch (Exception e)
        {
            Console.WriteLine("m68ktest: " + e);
            return 2;
        }
    }

    internal static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    internal static bool Flag(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A whole vector the core is not held to, because the two vector sets contradict each other on it and the core follows the one derived from the chip (the MAME set), which passes.
/// Only the 680x0 set has exclusions. Each is a class, found and counted by rule, never by listing results.
/// </summary>
internal enum Exclusion
{
    None,
    /// <summary>RTE and RTR: the 680x0 vectors read the PC's high word before the status word; the MAME set reads SR (or CCR) first, then the PC.</summary>
    RteRtrPopOrder,
    /// <summary>LINK A7: the 680x0 vectors push the stack pointer after it has been decremented, the MAME set the value before.</summary>
    LinkA7,
    /// <summary>CHK: the sets differ in where the program fetch falls relative to the exception, in the branch conditions and in the flags left behind.</summary>
    ChkSequence,
    /// <summary>DIVS and DIVU that overflow: the 680x0 vectors end DIVS early for |quotient| of 32768 or more and leave N and Z alone; the MAME set runs DIVS for its full length unless the quotient is 65536 or more, and sets N, clears Z.</summary>
    DivOverflow,
    /// <summary>The one DIVU by zero: the 680x0 vector stacks the address of the instruction itself, where the manual (and every other exception vector of the set) stacks the next one.</summary>
    DivZero,
    /// <summary>ADDQ.L and SUBQ.L to an address register: the 680x0 vectors take 6 clocks, the MAME set (and the manual) 8.</summary>
    QuickLongToAn,
    /// <summary>ASR by a register count above the operand width on a negative value: the 680x0 vectors clear C and X, the MAME set (and the manual) leave the sign bit shifted out in both.</summary>
    AsrCountPastWidth,
    /// <summary>Two ASL.B vectors whose final D2 holds bits an ASL.B cannot reach.</summary>
    CorruptVector,
}

/// <summary>One field of a vector that is not compared (the rest of it is). Counted per file.</summary>
internal enum Tolerance
{
    /// <summary>MAME set, address errors: the PC the chip stacks follows microcode state (it is the instruction's address plus two to ten bytes by case); the core stacks the plain value the 680x0 vectors show.</summary>
    AddressErrorStackedPc,
    /// <summary>Both sets, address errors: the address registers' values after the aborted access (the post-increment or pre-decrement that has or has not happened) differ between the sets and from the core.</summary>
    AddressErrorAddressRegisters,
    /// <summary>680x0 set, address errors: the instruction word in the frame and the 11 bits of it the status word carries (the 680x0 vectors show the opcode where the chip shows the word a prefetch has moved in), and the function code of a PC-relative operand (data space there, program space on the chip).</summary>
    AddressErrorInstructionWordAndSpace,
    /// <summary>MAME set, MOVE.L address errors: the flag bits of the stacked SR (the chip's long move updates N and Z in a data dependent way when the destination is (An) or (An)+).</summary>
    AddressErrorMoveLFlags,
    /// <summary>680x0 set, DBcc address errors: the 680x0 vectors have the counter already decremented, the chip decrements it after the target fetch.</summary>
    DbccCounter,
    /// <summary>MAME set, address errors: eight idle clocks after the aborted access that the published figure (50 clocks), the 680x0 vectors and the core do not have.</summary>
    AddressErrorEightClockGap,
    /// <summary>680x0 set: bit 3 (I/N) of the address-error status word, which that set raises for program fetches and the chip does not.</summary>
    AddressErrorStatusInBit,
}

// ==================================================================================================================================================
// the vector model
// ==================================================================================================================================================

internal sealed class VecState
{
    public readonly uint[] D = new uint[8], A = new uint[7];
    public uint Usp, Ssp, Sr, Pc, Pf0, Pf1;
    public readonly List<(int addr, byte val)> Ram = new();
}

internal struct VecTx
{
    public char Kind;      // r w n t
    public int Cycles;
    public uint Addr;
    public bool Byte;
    public int Data;
    public bool Upper, Lower;
    public override string ToString() => Kind == 'n' ? $"n{Cycles}" : $"{Kind}{Cycles}@{Addr:X6}{(Byte ? ".b" : ".w")}={Data:X}";
}

internal sealed class VecTest
{
    public string Name = "";
    public VecState Init = new(), Final = new();
    public int Length;
    public readonly List<VecTx> Tx = new();
    public bool AddressError;          // the vector is an address-error exception (680x0: it reads vector 3; MAME: it holds an aborted access)
    public bool EightClockGap;         // MAME set: the eight idle clocks after the aborted access were removed from Tx and Length
    public bool StatusInBitCleared;    // 680x0 set: bit 3 of the status word was cleared in Tx for a program fetch
}

internal static class VecLoaders
{
    private static uint U(JsonElement e) => (uint)e.GetUInt64();

    private static void ReadState(JsonElement s, VecState st)
    {
        for (int i = 0; i < 8; i++) st.D[i] = U(s.GetProperty("d" + i));
        for (int i = 0; i < 7; i++) st.A[i] = U(s.GetProperty("a" + i));
        st.Usp = U(s.GetProperty("usp")); st.Ssp = U(s.GetProperty("ssp")); st.Sr = U(s.GetProperty("sr")); st.Pc = U(s.GetProperty("pc"));
        var pf = s.GetProperty("prefetch");
        st.Pf0 = U(pf[0]); st.Pf1 = U(pf[1]);
        foreach (var r in s.GetProperty("ram").EnumerateArray()) st.Ram.Add((r[0].GetInt32(), (byte)r[1].GetInt32()));
    }

    /// <summary>SingleStepTests/680x0: gzipped JSON.</summary>
    public static List<VecTest> Load680x0(string path)
    {
        byte[] json;
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            gz.CopyTo(ms);
            json = ms.ToArray();
        }
        var list = new List<VecTest>();
        using var doc = JsonDocument.Parse(json);
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var vt = new VecTest { Name = t.GetProperty("name").GetString()!, Length = t.GetProperty("length").GetInt32() };
            ReadState(t.GetProperty("initial"), vt.Init);
            ReadState(t.GetProperty("final"), vt.Final);
            foreach (var x in t.GetProperty("transactions").EnumerateArray())
            {
                string k = x[0].GetString()!;
                if (k == "n") vt.Tx.Add(new VecTx { Kind = 'n', Cycles = x[1].GetInt32() });
                else vt.Tx.Add(new VecTx { Kind = k[0], Cycles = x[1].GetInt32(), Addr = (uint)x[3].GetInt64(), Byte = x[4].GetString() == ".b", Data = x[5].GetInt32() });
            }
            // an address error shows as the read of vector 3 (the long at address 12)
            foreach (var x in vt.Tx) if (x.Kind == 'r' && !x.Byte && x.Addr == 0x0C) { vt.AddressError = true; break; }
            if (vt.AddressError) ClearStatusInBit(vt);
            list.Add(vt);
        }
        return list;
    }

    /// <summary>The 680x0 vectors set bit 3 (I/N) of the status word for a faulting program fetch; the chip does not. Cleared in the expected frame (bus log and final memory).</summary>
    private static void ClearStatusInBit(VecTest t)
    {
        uint at = t.Final.Ssp;     // the status word is the lowest word of the frame, which is where the stack ends up
        for (int i = 0; i < t.Tx.Count; i++)
        {
            var x = t.Tx[i];
            if (x.Kind != 'w' || x.Addr != at || (x.Data & 7) is not (2 or 6) || (x.Data & 8) == 0) continue;
            x.Data &= ~8;
            t.Tx[i] = x;
            for (int k = 0; k < t.Final.Ram.Count; k++)
                if (t.Final.Ram[k].addr == (int)at + 1) t.Final.Ram[k] = (t.Final.Ram[k].addr, (byte)(t.Final.Ram[k].val & ~8));
            t.StatusInBitCleared = true;
            return;
        }
    }

    /// <summary>SingleStepTests/m68000: the binary layout documented by that repository's decode.py.</summary>
    public static List<VecTest> LoadMame(string path)
    {
        var b = File.ReadAllBytes(path);
        int p = 0;
        uint U32() { uint v = BitConverter.ToUInt32(b, p); p += 4; return v; }
        ushort U16() { ushort v = BitConverter.ToUInt16(b, p); p += 2; return v; }
        byte U8() => b[p++];

        void State(VecState st)
        {
            U32(); if (U32() != 0x01234567) throw new InvalidDataException("bad state magic");
            for (int i = 0; i < 8; i++) st.D[i] = U32();
            for (int i = 0; i < 7; i++) st.A[i] = U32();
            st.Usp = U32(); st.Ssp = U32(); st.Sr = U32(); st.Pc = U32();
            st.Pf0 = U32(); st.Pf1 = U32();
            int n = (int)U32();
            for (int i = 0; i < n; i++)
            {
                int addr = (int)U32(); ushort data = U16();
                st.Ram.Add((addr, (byte)(data >> 8)));
                st.Ram.Add((addr | 1, (byte)data));
            }
        }

        if (U32() != 0x1A3F5D71) throw new InvalidDataException("bad file magic");
        int tests = (int)U32();
        var list = new List<VecTest>(tests);
        for (int t = 0; t < tests; t++)
        {
            var vt = new VecTest();
            U32(); if (U32() != 0xABC12367) throw new InvalidDataException("bad test magic");
            U32(); if (U32() != 0x89ABCDEF) throw new InvalidDataException("bad name magic");
            int len = (int)U32();
            vt.Name = Encoding.UTF8.GetString(b, p, len); p += len;
            State(vt.Init);
            State(vt.Final);
            U32(); if (U32() != 0x456789AB) throw new InvalidDataException("bad transactions magic");
            vt.Length = (int)U32();
            int count = (int)U32();
            for (int i = 0; i < count; i++)
            {
                int tw = U8(); int cycles = (int)U32();
                if (tw == 0) { vt.Tx.Add(new VecTx { Kind = 'n', Cycles = cycles }); continue; }
                U32();  // function code
                uint addr = U32(); int data = (int)U32(); uint uds = U32(), lds = U32();
                char kind = tw switch { 1 => 'w', 2 => 'r', 3 => 't', 4 => 'R', 5 => 'W', _ => throw new InvalidDataException("bad transaction kind") };
                if (tw >= 4)
                {
                    // an aborted cycle (the address-error access: AS never asserted) is plain idle time as far as the bus is concerned
                    vt.AddressError = true;
                    vt.Tx.Add(new VecTx { Kind = 'n', Cycles = cycles });
                    continue;
                }
                bool byteAccess = uds + lds != 2;
                vt.Tx.Add(new VecTx { Kind = kind, Cycles = cycles, Addr = addr, Byte = byteAccess, Data = data, Upper = uds != 0, Lower = lds != 0 });
            }
            if (vt.AddressError) DropEightClockGap(vt);
            list.Add(vt);
        }
        return list;
    }

    /// <summary>MAME's address-error flow idles eight clocks between the aborted access and the stack frame; the published figure (50 clocks, Motorola), the 680x0 vectors and the core have none.</summary>
    private static void DropEightClockGap(VecTest t)
    {
        for (int i = 0; i + 2 < t.Tx.Count; i++)
        {
            if (t.Tx[i].Kind == 'n' && t.Tx[i + 1].Kind == 'n' && t.Tx[i + 1].Cycles == 8 && t.Tx[i + 2].Kind == 'w')
            {
                t.Tx.RemoveAt(i + 1);
                t.Length -= 8;
                t.EightClockGap = true;
                return;
            }
        }
    }
}

// ==================================================================================================================================================
// the machine a vector runs on
// ==================================================================================================================================================

internal sealed class VecMachine
{
    public readonly byte[] Ram = new byte[1 << 24];
    public readonly List<int> Touched = new();
    public readonly List<VecTx> Log = new();
    public int IrqLevel;

    public void Clear()
    {
        foreach (int a in Touched) Ram[a] = 0;
        Touched.Clear();
        Log.Clear();
    }
}

internal struct VecBus : IM68kBus
{
    public VecMachine M;

    public ushort Read(uint address, bool upper, bool lower, int cycles)
    {
        if (cycles > 4) M.Log.Add(new VecTx { Kind = 'n', Cycles = cycles - 4 });
        int a = (int)(address & 0xFFFFFE);
        ushort w = (ushort)(M.Ram[a] << 8 | M.Ram[a + 1 & 0xFFFFFF]);
        bool b = !(upper && lower);
        M.Log.Add(new VecTx { Kind = 'r', Cycles = 4, Addr = b && lower ? address | 1 : address, Byte = b, Data = b ? (lower ? w & 0xFF : w >> 8) : w, Upper = upper, Lower = lower });
        return w;
    }

    public void Write(uint address, bool upper, bool lower, ushort data, int cycles)
    {
        if (cycles > 4) M.Log.Add(new VecTx { Kind = 'n', Cycles = cycles - 4 });
        int a = (int)(address & 0xFFFFFE);
        if (upper) { M.Ram[a] = (byte)(data >> 8); M.Touched.Add(a); }
        if (lower) { int a1 = a + 1 & 0xFFFFFF; M.Ram[a1] = (byte)data; M.Touched.Add(a1); }
        bool b = !(upper && lower);
        M.Log.Add(new VecTx { Kind = 'w', Cycles = 4, Addr = b && lower ? address | 1 : address, Byte = b, Data = b ? (lower ? data & 0xFF : data >> 8) : data, Upper = upper, Lower = lower });
    }

    public void Idle(int cycles) { if (cycles > 0) M.Log.Add(new VecTx { Kind = 'n', Cycles = cycles }); }
    public int InterruptLevel => M.IrqLevel;
    public int Acknowledge(int level, int cycles) { if (cycles > 0) M.Log.Add(new VecTx { Kind = 'n', Cycles = cycles }); return 24 + level; }
    public void ResetLine(int cycles) { if (cycles > 0) M.Log.Add(new VecTx { Kind = 'n', Cycles = cycles }); }
}
