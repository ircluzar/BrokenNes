using System;
using System.Collections.Generic;
using System.Linq;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// Random real-mode instruction streams for the differential trace. Each case: a random register / flag state, random data in the
/// segments, ~20 well-formed random instructions (with 0x66 / 0x67 / segment / REP prefixes), then HLT; every interrupt vector points at a HLT.
/// Left out on purpose: far / indirect transfers and returns (they would run off into random memory), IRET (tiny386 aborts on a
/// real-mode IRETD), writes to CR0 (LMSW, MOV CR0), LIDT / LGDT / LLDT / LTR, RDTSC and CMOV (the port differs on purpose),
/// BOUND, MOV Sreg with reg 6/7, and a REP string instruction under 0x67 (ECX could make it run for billions of iterations).
/// A conditional jump or LOOP always skips exactly the next generated instruction, so both outcomes stay on instruction boundaries.
/// </summary>
internal static class Fuzz
{
    private static readonly byte[] Ivt = BuildIvt();
    // vector n -> 0000:0500+n, a HLT each, so the trace shows which interrupt or exception was taken
    private static byte[] BuildIvt() { var b = new byte[1024]; for (int i = 0; i < 256; i++) { b[i * 4] = (byte)i; b[i * 4 + 1] = 0x05; } return b; }
    private static readonly byte[] Halts = Enumerable.Repeat((byte)0xf4, 256).ToArray();

    public static IEnumerable<X86Case> Generate(int n, int seed, int firstId)
    {
        var rng = new Random(seed);
        for (int i = 0; i < n; i++)
        {
            var c = new X86Case { Name = $"fuzz{i}" };
            for (int r = 0; r < 8; r++) c.Regs[r] = rng.Next(3) == 0 ? (uint)rng.Next(0x10000) : (uint)rng.NextInt64(0x100000000);
            c.Regs[4] = 0x8000;
            c.Regs[8] = ((uint)rng.Next(0x10000) & 0x0cd5) | 2;   // CF PF AF ZF SF DF OF
            c.Segs = new uint[] { 0x0800, 0x1000, rng.Next(2) == 0 ? 0x1000u : 0x2000u, 0x3000, 0x4000, 0x5000 };
            c.Ip = 0;
            var code = new List<byte>();
            int count = 12 + rng.Next(16);
            for (int k = 0; k < count; k++) EmitOne(rng, code, k < count - 1);
            code.Add(0xf4);
            c.Code = code.ToArray();
            c.Clear(0, 0x400000).Bytes(0, Ivt).Bytes(0x500, Halts).Fill(0x10000, 0x60000, (uint)rng.Next(1, int.MaxValue)).Bytes(0x8000, c.Code);
            c.Steps = count + 8;
            c.HashAddr = 0; c.HashLen = 0x400000;
            yield return c;
        }
    }

    private static void EmitOne(Random rng, List<byte> o, bool canSkip)
    {
        var ins = new List<byte>();
        Instr(rng, ins, out bool isSkip);
        if (isSkip)
        {
            if (!canSkip) { o.Add(0x90); return; }
            var next = new List<byte>();
            Instr(rng, next, out bool nestedSkip);
            if (nestedSkip) next = new List<byte> { 0x90 };
            int len = next.Count;
            // ins ends with its displacement placeholder: 1 byte (rel8) or 2/4 bytes (rel16/32)
            int dsz = ins[^1];
            ins.RemoveAt(ins.Count - 1);
            for (int i = 0; i < dsz; i++) ins.Add((byte)(len >> (8 * i)));
            o.AddRange(ins); o.AddRange(next);
            return;
        }
        o.AddRange(ins);
    }

    // one instruction; for a skip jump the last byte is the displacement size (patched by the caller)
    private static void Instr(Random rng, List<byte> o, out bool skip)
    {
        skip = false;
        int start = o.Count;
        bool op16 = true, ad16 = true;
        int np = rng.Next(4) == 0 ? 1 + rng.Next(2) : 0;
        bool rep = false;
        var pre = new List<byte>();
        for (int i = 0; i < np; i++)
        {
            byte p = (byte)(rng.Next(6) switch { 0 => 0x66, 1 => 0x67, 2 => 0x26, 3 => 0x2e, 4 => 0x36, _ => 0x64 + rng.Next(2) });
            if (p == 0x66) op16 = false;   // a prefix sets the size (tiny386: opsz16 = !code16), a repeat does not toggle it
            if (p == 0x67) ad16 = false;
            pre.Add(p);
        }
        o.AddRange(pre);
        int v = op16 ? 2 : 4;

        void Modrm(int regField = -1, bool memOnly = false)
        {
            int mod = memOnly ? rng.Next(3) : rng.Next(4);
            int reg = regField >= 0 ? regField : rng.Next(8);
            int rm = rng.Next(8);
            o.Add((byte)((mod << 6) | (reg << 3) | rm));
            if (mod == 3) return;
            if (ad16)
            {
                if (mod == 0 && rm == 6) Imm(2);
                else if (mod == 1) Imm(1);
                else if (mod == 2) Imm(2);
            }
            else
            {
                if (rm == 4)
                {
                    int sib = rng.Next(256);
                    o.Add((byte)sib);
                    if ((sib & 7) == 5 && mod == 0) Imm(4);
                }
                else if (mod == 0 && rm == 5) Imm(4);
                if (mod == 1) Imm(1);
                else if (mod == 2) Imm(4);
            }
        }
        void Imm(int n) { for (int i = 0; i < n; i++) o.Add((byte)rng.Next(256)); }

        int pick = rng.Next(100);
        if (pick < 30)
        {
            // ALU group 0x00-0x3d
            int op = rng.Next(8), form = rng.Next(6);
            o.Add((byte)(op * 8 + form));
            if (form < 4) Modrm(); else Imm(form == 4 ? 1 : v);
            return;
        }
        if (pick < 38)
        {
            int g = rng.Next(4);   // 80 81 82 83
            o.Add((byte)(0x80 + g)); Modrm(); Imm(g == 1 ? v : 1);
            return;
        }
        if (pick < 46)
        {
            // shifts / rotates
            int f = rng.Next(6);
            byte opc = (byte)(f switch { 0 => 0xc0, 1 => 0xc1, 2 => 0xd0, 3 => 0xd1, 4 => 0xd2, _ => 0xd3 });
            o.Add(opc); Modrm();
            if (opc <= 0xc1) Imm(1);
            return;
        }
        if (pick < 52)
        {
            // unary group 3 / 4 / 5 (inc dec push)
            int f = rng.Next(4);
            if (f < 2) { o.Add((byte)(0xf6 + f)); int r = rng.Next(8); Modrm(r); if (r < 2) Imm(f == 0 ? 1 : v); }
            else if (f == 2) { o.Add(0xfe); Modrm(rng.Next(2)); }
            else { o.Add(0xff); Modrm(new[] { 0, 1, 6 }[rng.Next(3)]); }
            return;
        }
        if (pick < 58)
        {
            // string instructions
            int s = new[] { 0xa4, 0xa5, 0xa6, 0xa7, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf, 0x6c, 0x6d, 0x6e, 0x6f }[rng.Next(14)];
            if (ad16 && rng.Next(2) == 0)
            {
                // REP: a short count and SI / DI clear of the 64 KB wrap first (tiny386's REP fast path runs on in physical memory
                // past a segment wrap; the port wraps like the 386, so a wrapping REP is a known, deliberate difference)
                o.Add((byte)(rng.Next(2) == 0 ? 0xf3 : 0xf2)); rep = true;
                o.InsertRange(start, new byte[] { 0xb9, (byte)rng.Next(65), 0, 0xbe, 0, (byte)(0x10 + rng.Next(0xd0)), 0xbf, 0, (byte)(0x10 + rng.Next(0xd0)) });
            }
            o.Add((byte)s);
            _ = rep;
            return;
        }
        if (pick < 64)
        {
            // jumps that skip the next instruction
            skip = true;
            int f = rng.Next(5);
            if (f == 0) { o.Add((byte)(0x70 + rng.Next(16))); o.Add(1); }
            else if (f == 1) { o.Add((byte)(0xe0 + rng.Next(4))); o.Add(1); }
            else if (f == 2) { o.Add(0xeb); o.Add(1); }
            else if (f == 3) { o.Add(0x0f); o.Add((byte)(0x80 + rng.Next(16))); o.Add((byte)v); }
            else
            {
                // CMP / TEST then JZ / JNZ: the try_jcc8 fold
                o.Add((byte)(new[] { 0x38, 0x39, 0x3a, 0x3b, 0x84, 0x85 }[rng.Next(6)])); Modrm();
                o.Add((byte)(0x74 + rng.Next(2))); o.Add(1);
            }
            return;
        }
        if (pick < 80)
        {
            // one-byte assortment
            int[] ops = { 0x06, 0x07, 0x0e, 0x16, 0x17, 0x1e, 0x1f, 0x27, 0x2f, 0x37, 0x3f, 0x40, 0x43, 0x46, 0x48, 0x4b, 0x4d, 0x50, 0x53, 0x56, 0x58, 0x5a, 0x5d,
                          0x60, 0x61, 0x63, 0x68, 0x69, 0x6a, 0x6b, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8c, 0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x95, 0x97,
                          0x98, 0x99, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f, 0xa0, 0xa1, 0xa2, 0xa3, 0xa8, 0xa9, 0xb0, 0xb4, 0xb7, 0xb8, 0xbb, 0xbe, 0xc4, 0xc5, 0xc6, 0xc7,
                          0xc8, 0xc9, 0xcc, 0xcd, 0xce, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xdb, 0xdd, 0xdf, 0xe4, 0xe5, 0xe6, 0xe7, 0xec, 0xed, 0xee, 0xef,
                          0xf5, 0xf8, 0xf9, 0xfa, 0xfb, 0xfc, 0xfd };
            int x = ops[rng.Next(ops.Length)];
            o.Add((byte)x);
            switch (x)
            {
                case 0x68: Imm(v); break;
                case 0x69: Modrm(); Imm(v); break;
                case 0x6a: case 0xa8: case 0xcd: case 0xd4: case 0xd5: case 0xe4: case 0xe5: case 0xe6: case 0xe7: Imm(1); break;
                case 0x6b: Modrm(); Imm(1); break;
                case 0x63: case 0x84: case 0x85: case 0x86: case 0x87: case 0x88: case 0x89: case 0x8a: case 0x8b: Modrm(); break;
                case 0xd8: case 0xdb: case 0xdd: case 0xdf: Modrm(); break;
                case 0x8c: Modrm(rng.Next(6)); break;
                case 0x8e: Modrm(new[] { 0, 2, 3, 4, 5 }[rng.Next(5)]); break;
                case 0x8d: case 0xc4: case 0xc5: Modrm(-1, true); break;
                case 0x8f: Modrm(0); break;
                case 0xa0: case 0xa1: case 0xa2: case 0xa3: Imm(ad16 ? 2 : 4); break;
                case 0xa9: Imm(v); break;
                case 0xb0: case 0xb4: case 0xb7: Imm(1); break;
                case 0xb8: case 0xbb: case 0xbe: Imm(v); break;
                case 0xc6: Modrm(0); Imm(1); break;
                case 0xc7: Modrm(0); Imm(v); break;
                case 0xc8: Imm(2); o.Add((byte)rng.Next(4)); break;
            }
            return;
        }
        {
            // 0F xx
            int[] ops = { 0x02, 0x03, 0x06, 0x09, 0x20, 0x21, 0x23, 0x90, 0x93, 0x95, 0x9c, 0x9f, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa8, 0xa9,
                          0xab, 0xac, 0xad, 0xaf, 0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xba, 0xbb, 0xbc, 0xbd, 0xbe, 0xbf, 0xc0, 0xc1, 0xc7, 0xc8, 0xcb, 0xcf, 0x01 };
            int x = ops[rng.Next(ops.Length)];
            o.Add(0x0f); o.Add((byte)x);
            switch (x)
            {
                case 0x06: case 0x09: case 0xa0: case 0xa1: case 0xa2: case 0xa8: case 0xa9: case 0xc8: case 0xcb: case 0xcf: break;
                case 0x20: o.Add((byte)(0xc0 | (new[] { 0, 2, 3 }[rng.Next(3)] << 3) | rng.Next(8))); break;
                case 0x21: case 0x23: o.Add((byte)(0xc0 | rng.Next(64))); break;
                case 0xa4: case 0xac: Modrm(); Imm(1); break;
                case 0xb2: case 0xb4: case 0xb5: Modrm(-1, true); break;
                case 0xba: Modrm(4 + rng.Next(4)); Imm(1); break;
                case 0xc7: Modrm(1, true); break;
                case 0x01: Modrm(rng.Next(2), true); break;       // SGDT / SIDT
                default: Modrm(); break;
            }
        }
    }
}
