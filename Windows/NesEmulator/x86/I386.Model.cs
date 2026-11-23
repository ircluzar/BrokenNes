// Intel 80386 / 80486 / Pentium MMX core: the CPU models (our own addition, not in tiny386).
//
// One core, many chips: a CpuModel says which instructions exist (#UD otherwise), whether the x87 FPU and MMX are there, what CPUID
// answers, which EFLAGS bits stick, CR4 / MSRs / RDTSC, and the timing model (instructions per second at the chosen clock). The flags
// are copied into readonly fields once, in the constructor, so the hot paths branch on a field, never on the descriptor.
//
// CpuModel.Legacy is the x86 console as it always was (tiny386's permissive 486: every 0F opcode tiny386 knew, "TINY386 CPU " CPUID,
// RDTSC = the instruction count, no FPU, no CR4): the default machine keeps it, so its runs and save states stay byte-identical.

using System;

namespace NesEmulator.X86;

/// <summary>A CPU model of the x86 PC. Build one with a preset (<see cref="I386DX"/>, <see cref="I486DX"/>, <see cref="I486DX2"/>,
/// <see cref="I486DX4"/>, <see cref="PentiumMmx"/>), optionally <see cref="WithClock"/>, or look one up with <see cref="Parse"/>.</summary>
public sealed record CpuModel
{
    /// <summary>Short id: LEGACY, 386DX, 486DX, 486DX2, 486DX4, P55C.</summary>
    public string Id { get; init; } = "LEGACY";
    public string Name { get; init; } = "tiny386 486-class";
    /// <summary>3 = 386, 4 = 486, 5 = Pentium.</summary>
    public int Gen { get; init; } = 4;
    /// <summary>The tiny386 behaviour of the original x86 console (no gating, no FPU). Every other flag is ignored when set.</summary>
    public bool Legacy { get; init; }
    public bool Fpu { get; init; }
    public bool Ops486 { get; init; }      // BSWAP, CMPXCHG, XADD, INVLPG, INVD, WBINVD, EFLAGS.AC
    public bool Cpuid { get; init; }       // CPUID and a writable EFLAGS.ID
    public bool Ops586 { get; init; }      // RDTSC, CMPXCHG8B, RDMSR / WRMSR, CR4
    public bool Mmx { get; init; }
    public bool Ops686 { get; init; }      // P6: CMOVcc, FCMOVcc, FCOMI / FUCOMI(P), RDPMC, SYSENTER / SYSEXIT, hint NOPs 0F 18-1F, MTRR / P6 MSRs, CR4.PCE
    public bool Sse { get; init; }         // P3: SSE, the MMX extensions, FXSAVE / FXRSTOR, CR4.OSFXSR / OSXMMEXCPT, #XM
    public uint Signature { get; init; }   // CPUID 1 EAX: family << 8 | model << 4 | stepping
    public uint FeatureEdx { get; init; }  // CPUID 1 EDX
    public string Vendor { get; init; } = "GenuineIntel";
    /// <summary>The core clock in Hz (RDTSC counts it).</summary>
    public long ClockHz { get; init; } = 20_000_000;
    /// <summary>Average core clocks per instruction of this generation (the timing model: instructions per second = clock / CPI).</summary>
    public double Cpi { get; init; } = 1.0;

    /// <summary>The emulated instruction rate: the machine's InstructionsPerSecond for this model at its clock.</summary>
    public long Ips => Legacy ? 20_000_000 : Math.Max(1, (long)Math.Round(ClockHz / Cpi));

    public uint FlagsMask => Legacy ? (Gen == 3 ? 0x37fd7u : Gen == 4 ? 0x77fd7u : 0x277fd7u)
        : Cpuid ? 0x277fd7u : Ops486 ? 0x77fd7u : 0x37fd7u;

    public CpuModel WithClock(int mhz) => this with { ClockHz = mhz * 1_000_000L, Name = $"{BaseName} {mhz} MHz" };
    private string BaseName => Name.Contains(" MHz") ? Name[..Name.LastIndexOf(' ', Name.Length - 5)] : Name;

    // ---- presets ----

    /// <summary>The original x86 console's CPU (gen 4 unless told otherwise).</summary>
    public static CpuModel LegacyModel(int gen = 4) => new() { Id = "LEGACY", Name = "tiny386 486-class", Gen = gen, Legacy = true };

    // CPI: 386 ~4.4 clocks per instruction on DOS integer code, 486 ~2 (1-clock ALU ops, cache hits), Pentium ~1.1 (U/V pairing on
    // compiler output). Calibrated against published Dhrystone / TopBench scores, not per opcode (open issue: a per-opcode table).
    public static CpuModel I386DX => new() { Id = "386DX", Name = "Intel 386DX 33 MHz", Gen = 3, ClockHz = 33_000_000, Cpi = 4.4 };
    public static CpuModel I386DXFpu => I386DX with { Id = "386DX+387", Name = "Intel 386DX + 387 33 MHz", Fpu = true };
    public static CpuModel I486DX => new() { Id = "486DX", Name = "Intel 486DX 33 MHz", Gen = 4, Fpu = true, Ops486 = true, ClockHz = 33_000_000, Cpi = 2.0, Signature = 0x0410 };
    public static CpuModel I486DX2 => I486DX with { Id = "486DX2", Name = "Intel 486DX2 66 MHz", ClockHz = 66_000_000, Signature = 0x0435 };
    /// <summary>The 486DX4 has CPUID (family 4 model 8): FPU + VME bits.</summary>
    public static CpuModel I486DX4 => I486DX with { Id = "486DX4", Name = "Intel 486DX4 100 MHz", ClockHz = 100_000_000, Cpuid = true, Signature = 0x0480, FeatureEdx = 0x1 };
    /// <summary>Pentium MMX (P55C, family 5 model 4 stepping 3). CPUID EDX = FPU, DE, TSC, MSR, MCE, CX8, MMX. VME and PSE are NOT
    /// reported (not emulated; Windows 98 runs without them, as on a 486).</summary>
    public static CpuModel PentiumMmx => new()
    {
        Id = "P55C", Name = "Intel Pentium MMX 166 MHz", Gen = 5, Fpu = true, Ops486 = true, Cpuid = true, Ops586 = true, Mmx = true,
        Signature = 0x0543, FeatureEdx = 0x1 | 0x4 | 0x10 | 0x20 | 0x80 | 0x100 | 0x800000, ClockHz = 166_000_000, Cpi = 1.1,
    };

    /// <summary>Pentium II 400 (Deschutes, family 6 model 5 stepping 2, 512 KB half-speed L2). CPUID EDX = FPU, DE, TSC, MSR, MCE, CX8, SEP,
    /// MTRR, CMOV, MMX: only what this core implements. A real Deschutes also reports VME, PSE, PAE, PGE, MCA, PAT, PSE-36 and FXSR; those
    /// paging / FXSAVE features are not emulated, so they are left out (Windows 98 runs without them, as on our P55C).
    /// Timing (CPI 1.0 + the P6 column of the class tables): a P6 retires ~1 x86 instruction per clock on compiler output (3 decoders,
    /// out of order, loads hidden behind the window), so the average instruction is one clock and a memory operand one extra; the P6
    /// costs more than the P5 for segment loads, far transfers and INT / IRET (microcoded, serialising). Calibrated to SPECint95-class
    /// ratios (PII 400 ~ 2.7x a P166 MMX), not per opcode.</summary>
    public static CpuModel PentiumII => new CpuModel()
    {
        Id = "P2", Name = "Intel Pentium II 400 MHz", Gen = 6, Fpu = true, Ops486 = true, Cpuid = true, Ops586 = true, Mmx = true, Ops686 = true,
        Signature = 0x0652, FeatureEdx = 0x1 | 0x4 | 0x10 | 0x20 | 0x80 | 0x100 | 0x800 | 0x1000 | 0x8000 | 0x800000, ClockHz = 400_000_000, Cpi = P2Cpi,
    }.EnvOverride();

    /// <summary>The P2's guest clocks per instruction. 1.0 = the faithful model (400 M instructions per guest second); higher = fewer
    /// instructions per guest second (less host work), while RDTSC still counts 400 MHz, so Windows and Sandra still see a PII 400.
    /// X86_P2CPI overrides (user 2026-11-21: "try a cheaper P2 cost model first").</summary>
    private static double P2Cpi => P2CpiOverride ?? 1.0;
    /// <summary>X86_P2CPI when set (>= 1.0): the developer override, above a package's own hardware.p2cpi.</summary>
    public static double? P2CpiOverride => double.TryParse(Environment.GetEnvironmentVariable("X86_P2CPI"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 1.0 ? v : null;

    /// <summary>Pentium III 866 (Coppermine, family 6 model 8 stepping 6 (cD0), 256 KB full-speed on-die L2, 133 MHz FSB). CPUID EDX = the
    /// P2's + FXSR + SSE (the processor serial number is not reported, as on most boards that switched it off: leaf 3 absent, max leaf 2).
    /// Timing: the P6 core of the P2 (CPI 1.0 faithful + the P6 column) plus the SSE class costs; the gen-6 cost lever (X86_P2CPI and a
    /// package's hardware.p2cpi) applies to it the same way, RDTSC still counting 866 MHz.</summary>
    public static CpuModel PentiumIII => new CpuModel()
    {
        Id = "P3", Name = "Intel Pentium III 866 MHz", Gen = 6, Fpu = true, Ops486 = true, Cpuid = true, Ops586 = true, Mmx = true, Ops686 = true, Sse = true,
        Signature = 0x0686, FeatureEdx = 0x1 | 0x4 | 0x10 | 0x20 | 0x80 | 0x100 | 0x800 | 0x1000 | 0x8000 | 0x800000 | 0x1000000 | 0x2000000, ClockHz = 866_000_000, Cpi = P2Cpi,
    }.EnvOverride();

    /// <summary>Bisect switches (measurement only): X86_CPUID_SIG / X86_CPUID_EDX (hex) replace CPUID 1 EAX / EDX.</summary>
    private CpuModel EnvOverride()
    {
        var m = this;
        string? s = Environment.GetEnvironmentVariable("X86_CPUID_SIG"), e = Environment.GetEnvironmentVariable("X86_CPUID_EDX");
        if (!string.IsNullOrEmpty(s)) m = m with { Signature = Convert.ToUInt32(s, 16) };
        if (!string.IsNullOrEmpty(e)) m = m with { FeatureEdx = Convert.ToUInt32(e, 16) };
        return m;
    }

    public static readonly string[] Ids = { "LEGACY", "386DX", "386DX+387", "486DX", "486DX2", "486DX4", "P55C", "P2", "P3" };

    /// <summary>"P55C", "P55C@200", "486DX2@50", "386DX@40"...: a preset, optionally at another clock (MHz). Null if unknown.</summary>
    public static CpuModel? Parse(string s)
    {
        string id = s, mhz = "";
        int at = s.IndexOf('@');
        if (at >= 0) { id = s[..at]; mhz = s[(at + 1)..]; }
        CpuModel? m = id.ToUpperInvariant() switch
        {
            "LEGACY" => LegacyModel(), "386DX" or "386" => I386DX, "386DX+387" => I386DXFpu, "486DX" or "486" => I486DX,
            "486DX2" => I486DX2, "486DX4" => I486DX4, "P55C" or "PMMX" or "P166MMX" or "PENTIUMMMX" => PentiumMmx,
            "P2" or "PII" or "P2-400" or "DESCHUTES" or "PENTIUMII" => PentiumII,
            "P3" or "PIII" or "P3-866" or "COPPERMINE" or "PENTIUMIII" => PentiumIII, _ => null,
        };
        if (m == null) return null;
        if (mhz.Length > 0) { if (m.Legacy || !int.TryParse(mhz, out int f) || f < 1 || f > 2000) return null; m = m.WithClock(f); }
        return m;
    }
}

public sealed partial class I386
{
    public readonly CpuModel Model;
    // the model's flags, read once (the interpreter and the compiled blocks test these fields only)
    private readonly bool _legacy, _hasFpu, _has486, _hasCpuid, _has586, _hasMmx, _has686, _hasSse;

    /// <summary>The clock RDTSC reads, in instructions (the machine passes its own: instructions + idle time); null = <see cref="Cycle"/>.</summary>
    public Func<long>? InstructionClock;
    /// <summary>FERR#: an unmasked x87 exception with CR0.NE clear (the PC routes it to IRQ 13).</summary>
    public Action? Ferr;

    // REP string timing: core clocks per iteration (after the first, which the instruction itself counts) of MOVS STOS LODS SCAS CMPS INS OUTS,
    // per generation (Intel's tables plus memory bandwidth past the cache: TopBench's memory test on real 486 / P5 machines), in instruction-clock units
    // (16.16: clocks / CPI). Legacy = null: a REP was one instruction, and the classic machine keeps that.
    private static readonly int[][] RepClocks = { new[] { 4, 5, 5, 8, 9, 8, 8 }, new[] { 4, 5, 4, 5, 7, 8, 8 }, new[] { 2, 2, 3, 4, 4, 8, 8 }, new[] { 2, 1, 2, 3, 3, 8, 8 } };
    private readonly long[]? _repCost16;
    public const int RepMovs = 0, RepStos = 1, RepLods = 2, RepScas = 3, RepCmps = 4, RepIns = 5, RepOuts = 6;

    private static long[]? RepCosts(CpuModel m)
    {
        if (m.Legacy) return null;
        var c = RepClocks[Math.Clamp(m.Gen, 3, 6) - 3];
        var r = new long[c.Length];
        for (int i = 0; i < c.Length; i++) r[i] = (long)Math.Round(c[i] / m.Cpi * 65536);
        return r;
    }

    /// <summary>Charges the iterations of a REP string instruction past its first (no state: the fraction is dropped per instruction).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void RepCharge(int kind, uint iterations)
    {
        if (_repCost16 != null && iterations > 1) Cycle += ((iterations - 1) * _repCost16[kind]) >> 16;
    }

    // Instruction classes slower than the average (the CPI covers the average instruction): extra clocks per generation (386 / 486 / P5,
    // Intel's tables, real-mode / typical forms), turned into extra instruction-clock units (clocks / CPI - 1, rounded). Charged once per
    // instruction at its start (Step1 by the first opcode byte, ExecTwo by the 0F byte, Grp3Body for MUL / DIV); the compiled code adds
    // the same constant for the forms it compiles (only F6 / F7 MUL / DIV among them), so JIT and interpreter count the same clock.
    private readonly byte[]? _opCost, _opCost0F;
    private readonly byte[]? _mulCost, _divCost;          // by operand size: 8, 16, 32

    private void BuildOpCosts(CpuModel m, out byte[]? op, out byte[]? op0f, out byte[]? mul, out byte[]? div)
    {
        op = op0f = mul = div = null;
        if (m.Legacy) return;
        int g = Math.Clamp(m.Gen, 3, 6) - 3;
        double cpi = m.Cpi;
        // c686 (P6 column): -1 = the P5 value (Intel's P6 optimization manual latencies / throughput where they differ)
        byte U(int c386, int c486, int c586, int c686 = -1) { int c = g == 0 ? c386 : g == 1 ? c486 : g == 2 || c686 < 0 ? c586 : c686; return (byte)Math.Clamp((int)Math.Round(c / cpi) - 1, 0, 255); }
        var t = new byte[256]; var t2 = new byte[256];
        void S(byte v, params int[] ops) { foreach (int o in ops) t[o] = v; }
        S(U(22, 26, 10, 4), 0x69, 0x6b);                         // IMUL r, rm, imm
        S(U(4, 3, 3, 8), 0x8e);                                  // MOV Sreg (real mode)
        S(U(7, 6, 4, 10), 0xc4, 0xc5);                            // LES / LDS
        S(U(17, 18, 4, 20), 0x9a); S(U(12, 17, 3, 18), 0xea);         // CALL / JMP far
        S(U(18, 13, 4, 20), 0xca, 0xcb); S(U(22, 15, 8, 25), 0xcf);   // RETF, IRET
        S(U(37, 30, 16, 30), 0xcc, 0xcd, 0xce);                   // INT
        S(U(18, 11, 5), 0x60); S(U(24, 9, 5), 0x61);          // PUSHA / POPA
        S(U(10, 14, 11), 0xc8); S(U(4, 5, 3), 0xc9);          // ENTER / LEAVE
        S(U(11, 7, 5), 0xe2); S(U(11, 9, 7), 0xe0, 0xe1); S(U(9, 8, 6), 0xe3);   // LOOP / LOOPcc / JCXZ
        S(U(12, 14, 7), 0xe4, 0xe5, 0xec, 0xed); S(U(10, 16, 12), 0xe6, 0xe7, 0xee, 0xef);   // IN / OUT
        S(U(4, 4, 4, 9), 0x9c); S(U(5, 9, 6, 17), 0x9d);             // PUSHF / POPF
        S(U(17, 15, 18), 0xd4); S(U(19, 14, 10), 0xd5); S(U(5, 4, 4), 0xd7);   // AAM / AAD / XLAT
        S(U(4, 2, 3), 0x27, 0x2f); S(U(4, 3, 3), 0x37, 0x3f);
        S(U(7, 7, 4), 0xa4, 0xa5); S(U(4, 5, 3), 0xaa, 0xab); S(U(5, 5, 2), 0xac, 0xad);   // string ops (one iteration)
        S(U(7, 6, 4), 0xae, 0xaf); S(U(10, 8, 5), 0xa6, 0xa7); S(U(15, 17, 10), 0x6c, 0x6d, 0x6e, 0x6f);
        if (m.Fpu) S(U(30, 12, 3, 2), 0xd8, 0xd9, 0xda, 0xdb, 0xdc, 0xdd, 0xde, 0xdf);   // x87: an average FP op
        void S2(byte v, params int[] ops) { foreach (int o in ops) t2[o] = v; }
        S2(U(22, 26, 10, 4), 0xaf);                              // IMUL r, rm
        S2(U(3, 3, 3, 1), 0xb6, 0xb7, 0xbe, 0xbf);               // MOVZX / MOVSX
        S2(U(20, 20, 20, 2), 0xbc, 0xbd);                        // BSF / BSR
        S2(U(6, 6, 7, 2), 0xa3, 0xab, 0xb3, 0xbb, 0xba);         // BT family
        S2(U(3, 3, 4), 0xa4, 0xa5, 0xac, 0xad);               // SHLD / SHRD
        S2(U(15, 15, 15), 0xa0, 0xa1, 0xa8, 0xa9, 0xb2, 0xb4, 0xb5);   // PUSH / POP FS GS, LSS / LFS / LGS
        if (m.Ops686) S2(U(1, 1, 1, 2), 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f);   // CMOVcc: 2 uops
        if (m.Sse)
        {   // P3 (Katmai / Coppermine): packed single ops are 2 uops on the 64-bit SIMD units (throughput 2 clocks), scalar 1; DIVPS / SQRTPS
            // ~36 / 56 clocks (DIVSS / SQRTSS 18 / 30: the 0F byte is shared, the packed-and-scalar mean is charged); FXSAVE / FXRSTOR ~ 60
            S2(U(2, 2, 2, 2), 0x10, 0x11, 0x14, 0x15, 0x28, 0x29, 0x2b, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5c, 0x5d, 0x5f, 0xc2, 0xc6);
            S2(U(3, 3, 3, 3), 0x2a, 0x2c, 0x2d, 0x2e, 0x2f);
            S2(U(27, 27, 27, 27), 0x5e); S2(U(43, 43, 43, 43), 0x51);
            S2(U(40, 40, 40, 40), 0xae);
        }
        op = t; op0f = t2;
        mul = new[] { U(12, 13, 11, 4), U(22, 26, 11, 5), U(38, 42, 10, 5) };
        div = new[] { U(14, 16, 17, 17), U(22, 24, 25, 25), U(38, 40, 41, 41) };
    }

    /// <summary>A memory operand (ModRM with mod != 3, LEA too): address generation and the cache / bus, extra clock units (0 = legacy).</summary>
    private readonly int _memCost;
    private static int MemCost(CpuModel m) => m.Legacy ? 0 : Math.Max(0, (int)Math.Round((m.Gen <= 3 ? 4 : m.Gen == 4 ? 6 : m.Gen == 5 ? 3 : 1) / m.Cpi));

    private static int SizeIdx(int bits) => bits == 8 ? 0 : bits == 16 ? 1 : 2;

    /// <summary>The extra clock the compiled code adds for a form it compiles (the interpreter charges the same in Grp3Body).</summary>
    /// <summary>The extra clock the compiled code adds for the instruction at <paramref name="pa"/>: what the interpreter charges for it (Step1's first
    /// opcode byte, ExecTwo's 0F byte, Grp3Body's MUL / DIV, DecodeModrm's memory operand), from the same tables.</summary>
    private int JitExtraCost(uint pa, byte kind, int reg, int bits, bool mem)
    {
        if (_opCost == null) return 0;
        int p = 0; uint b;
        do b = U8(pa + (uint)p++); while (IsPrefix[b] && p < 15);
        int c = (mem ? _memCost : 0) + _opCost[b];
        if (b == 0x0f) { uint b2 = U8(pa + (uint)p); if ((b2 & 0xf0) != 0x80) c += _opCost0F![b2]; }
        if (kind == DK_GRP3 && reg >= 4) c += reg < 6 ? _mulCost![SizeIdx(bits)] : _divCost![SizeIdx(bits)];
        return c;
    }

    public uint Cr4;
    private long _tscOffset;                         // TSC = clock * ClockHz / Ips + _tscOffset (WRMSR 10h moves it)
    private uint _msrCesr, _msrCtr0Lo, _msrCtr1Lo;   // P5 performance counter MSRs 11h-13h: kept, not counted

    public const int EX_MF = 16;
    private const uint CR4_TSD = 0x4, CR4_DE = 0x8, CR4_MCE = 0x40;

    private bool ModelAllows0F(uint b1)
    {
        // only called for a non-legacy model; true = the opcode exists on this chip
        switch (b1)
        {
            case 0x08: case 0x09: return _has486;
            case 0xb0: case 0xb1: case 0xc0: case 0xc1: return _has486;
            case >= 0xc8 and <= 0xcf: return _has486;
            case 0xa2: return _hasCpuid;
            case 0x30: case 0x31: case 0x32: case 0xc7: return _has586;
            case 0x24: case 0x26: return !_has586;    // MOV TRn: 386 / 486 only
            case 0x0b: return false;                  // UD2 (P6): #UD by definition either way
            case 0x33: case 0x34: case 0x35: return _has686;   // RDPMC, SYSENTER, SYSEXIT
            case >= 0x18 and <= 0x1f: return _has686;          // hint NOPs (0F 1F /0 = the long NOP)
            case >= 0x40 and <= 0x4f: return _has686;          // CMOVcc
            case >= 0x60 and <= 0x7f: return _hasMmx && MmxOpcode(b1) || _hasSse && SseMmxOp(b1);
            case >= 0xd0 and <= 0xff: return _hasMmx && MmxOpcode(b1) || _hasSse && SseMmxOp(b1);
            case 0xae: case 0xc2: case 0xc4: case 0xc5: case 0xc6: return _hasSse;
            case >= 0x10 and <= 0x17: case >= 0x28 and <= 0x2f: case >= 0x50 and <= 0x5f: return _hasSse && SseXmmOp(b1);
        }
        return true;
    }

    /// <summary>The machine's instruction rate changes from <paramref name="oldIps"/> (what the TSC counted with so far) to the model's:
    /// move the TSC offset so RDTSC goes on from its value at instruction clock <paramref name="clk"/> (PcMachine.SetIps).</summary>
    public void RebaseTsc(long clk, long oldIps, long newIps)
    {
        if (Model.Legacy || oldIps <= 0 || newIps != Model.Ips) return;
        _tscOffset += (long)((Int128)clk * Model.ClockHz / oldIps) - (long)((Int128)clk * Model.ClockHz / newIps);
    }

    private ulong ReadTsc()
    {
        long clk = InstructionClock?.Invoke() ?? Cycle;
        long ips = Model.Ips;
        return (ulong)((long)((Int128)clk * Model.ClockHz / ips) + _tscOffset);
    }

    private int Rdtsc()
    {
        if (_legacy) { R[0] = (uint)Cycle; R[2] = (uint)((ulong)Cycle >> 32); return OK; }
        if ((Cr4 & CR4_TSD) != 0 && Cpl != 0) return K(Throw(EX_GP, 0));
        ulong t = ReadTsc();
        R[0] = (uint)t; R[2] = (uint)(t >> 32);
        return OK;
    }

    // ---- P6 (Pentium II) MSRs: SYSENTER, MTRRs, performance counters, the BIOS / microcode identity. Kept, never acting on anything
    // (memory types do not change timing here). Saved only for a P6 model, so the P5 state layout is unchanged.
    private uint _sysCs, _sysEsp, _sysEip;
    private readonly ulong[] _mtrr = new ulong[MtrrSlots];   // 0-15 variable base/mask 200h-20Fh, 16-26 fixed 250h 258h 259h 268h-26Fh, 27 MTRRdefType 2FFh
    private ulong _pmcEvt0, _pmcEvt1, _pmc0, _pmc1, _biosSign;
    private const int MtrrSlots = 28;
    private const uint CR4_PCE = 0x100;
    private static int MtrrSlot(uint idx) => idx switch
    {
        >= 0x200 and <= 0x20f => (int)(idx - 0x200), 0x250 => 16, 0x258 => 17, 0x259 => 18, >= 0x268 and <= 0x26f => 19 + (int)(idx - 0x268), 0x2ff => 27, _ => -1,
    };

    private static readonly bool _msrLog = Environment.GetEnvironmentVariable("X86_MSRLOG") == "1";
    private static int _msrLogN;
    private void MsrLog(string what) { if (_msrLog && _msrLogN++ < 200) Console.WriteLine($"msrlog: {what} at {SegSel[1]:X4}:{Ip:X8} cpl {Cpl}"); }

    private int MsrP6(bool write, uint idx, ulong v)
    {
        if (_msrLog) MsrLog($"{(write ? "WRMSR" : "RDMSR")} {idx:X} v {v:X}");
        int ms = MtrrSlot(idx);
        if (ms >= 0)
        {   // variable MTRR masks / bases above 36 bits and the reserved defType bits are #GP on a real chip
            if (write) { if ((ms < 16 && (v >> 36) != 0) || (ms == 27 && (v & ~0xcffUL) != 0)) return K(Throw(EX_GP, 0)); _mtrr[ms] = v; }
            else { R[0] = (uint)_mtrr[ms]; R[2] = (uint)(_mtrr[ms] >> 32); }
            return OK;
        }
        ulong r;
        switch (idx)
        {
            case 0x00: case 0x01: r = 0; break;                       // P5_MC_ADDR / TYPE aliases
            case 0x10:
                if (write) { _tscOffset += (long)(v - ReadTsc()); return OK; }
                r = ReadTsc(); break;
            case 0x17: r = 1UL << 50; break;                          // IA32_PLATFORM_ID: platform 1 (Slot 1)
            case 0x1b: if (write) return OK; r = 0xFEE0_0100; break;  // APIC_BASE: every P6 has this MSR (Win98's VMM reads it on family 6
                                                                      // and shuts down on a #GP); BSP, base FEE00000, global enable OFF
            case 0x2a: r = 0x0C40_0000; break;                        // EBL_CR_POWERON: 4x bus ratio (100 MHz FSB -> 400), in-order queue 8
            case 0x79: return OK;                                     // BIOS_UPDT_TRIG: microcode loads are accepted and ignored
            case 0x8b: if (write) { _biosSign = v; return OK; } r = _biosSign; break;   // BIOS_SIGN_ID
            case 0xc1: if (write) { _pmc0 = v & 0xff_ffff_ffffUL; return OK; } r = _pmc0; break;
            case 0xc2: if (write) { _pmc1 = v & 0xff_ffff_ffffUL; return OK; } r = _pmc1; break;
            case 0xfe: r = 0x508; break;                              // MTRRcap: 8 variable ranges, fixed ranges, write combining
            case 0x174: if (write) { _sysCs = (uint)v; return OK; } r = _sysCs; break;
            case 0x175: if (write) { _sysEsp = (uint)v; return OK; } r = _sysEsp; break;
            case 0x176: if (write) { _sysEip = (uint)v; return OK; } r = _sysEip; break;
            case 0x179: case 0x17a: case 0x17b: if (write) return OK; r = 0; break;   // MCG_CAP / STATUS / CTL (MCA not reported: zero banks)
            case 0x186: if (write) { _pmcEvt0 = v; return OK; } r = _pmcEvt0; break;
            case 0x187: if (write) { _pmcEvt1 = v; return OK; } r = _pmcEvt1; break;
            case 0x1d9: if (write) return OK; r = 0; break;           // DEBUGCTLMSR
            default: return K(Throw(EX_GP, 0));
        }
        if (write) return K(Throw(EX_GP, 0));                         // the read-only ones
        R[0] = (uint)r; R[2] = (uint)(r >> 32);
        return OK;
    }

    /// <summary>RDPMC (P6): counter 0 / 1 (40 bits, kept as WRMSR left them: events are not counted). Ring 3 needs CR4.PCE.</summary>
    private int Rdpmc()
    {
        if ((Cr4 & CR4_PCE) == 0 && Cpl != 0 && (Cr0 & 1) != 0) return K(Throw(EX_GP, 0));
        if (R[1] > 1) return K(Throw(EX_GP, 0));
        ulong c = R[1] == 0 ? _pmc0 : _pmc1;
        R[0] = (uint)c; R[2] = (uint)(c >> 32) & 0xff;
        return OK;
    }

    /// <summary>SYSENTER / SYSEXIT (P6, CPUID SEP): flat ring 0 / ring 3 segments from SYSENTER_CS_MSR (CS, CS+8 SS, +16 CS3, +24 SS3).</summary>
    private int SysEnterExit(bool enter)
    {
        if ((Cr0 & 1) == 0 || (_sysCs & 0xfffc) == 0) return K(Throw(EX_GP, 0));
        if (!enter && (Cpl != 0 || (Flags & VM) != 0)) return K(Throw(EX_GP, 0));
        uint cs = _sysCs & 0xfffc;
        if (enter)
        {
            Flags &= ~(VM | IF | RF);
            SetSegDesc(SEG_CS, cs, 0xffff, 0x00cf9b00);
            SetSegDesc(SEG_SS, cs + 8, 0xffff, 0x00cf9300);
            R[4] = _sysEsp; NextIp = _sysEip;
        }
        else
        {
            SetSegDesc(SEG_CS, (cs + 16) | 3, 0xffff, 0x00cffb00);
            SetSegDesc(SEG_SS, (cs + 24) | 3, 0xffff, 0x00cff300);
            R[4] = R[1]; NextIp = R[2];
        }
        return OK;
    }

    private int Msr(bool write)
    {
        if (Cpl != 0) return K(Throw(EX_GP, 0));
        uint idx = R[1];
        ulong v = ((ulong)R[2] << 32) | R[0];
        if (_has686) return MsrP6(write, idx, v);
        switch (idx)
        {
            case 0x00: case 0x01: if (write) return OK; R[0] = R[2] = 0; return OK;   // P5_MC_ADDR / P5_MC_TYPE
            case 0x10:
                if (write) { _tscOffset += (long)(v - ReadTsc()); return OK; }
                { ulong t = ReadTsc(); R[0] = (uint)t; R[2] = (uint)(t >> 32); }
                return OK;
            case 0x11: if (write) _msrCesr = (uint)v; else { R[0] = _msrCesr; R[2] = 0; } return OK;
            case 0x12: if (write) _msrCtr0Lo = (uint)v; else { R[0] = _msrCtr0Lo; R[2] = 0; } return OK;
            case 0x13: if (write) _msrCtr1Lo = (uint)v; else { R[0] = _msrCtr1Lo; R[2] = 0; } return OK;
            case 0x0e: if (!write) { R[0] = R[2] = 0; } return OK;                    // TR12 (feature control)
        }
        return K(Throw(EX_GP, 0));
    }

    /// <summary>MOV CR4, r32 on a model with CR4: only the bits this core implements (TSD, DE, MCE; VME / PVI / PSE are #GP since
    /// CPUID does not report them).</summary>
    private bool WriteCr4(uint v)
    {
        if ((v & ~(CR4_TSD | CR4_DE | CR4_MCE | (_has686 ? CR4_PCE : 0) | (_hasSse ? CR4_OSFXSR | CR4_OSXMMEXCPT : 0))) != 0) { MsrLog($"CR4 write {v:X} #GP"); return Throw(EX_GP, 0); }
        if (_msrLog) MsrLog($"CR4 write {v:X}");
        Cr4 = v;
        return true;
    }

    private void CpuidModel()
    {
        switch (R[0])
        {
            case 0:
            {
                R[0] = _has686 ? 2u : 1u;
                var v = System.Text.Encoding.ASCII.GetBytes(Model.Vendor.PadRight(12)[..12]);
                R[3] = BitConverter.ToUInt32(v, 0); R[2] = BitConverter.ToUInt32(v, 4); R[1] = BitConverter.ToUInt32(v, 8);
                break;
            }
            case 1: R[0] = Model.Signature; R[3] = 0; R[1] = 0; R[2] = Model.FeatureEdx; break;
            // leaf 2 (P6): cache / TLB descriptors of a Deschutes with 512 KB L2: 01 ITLB 4K, 02 ITLB 4M, 03 DTLB 4K, 43 L2 512 KB, 08 L1I 16 KB,
            // 04 DTLB 4M, 0C L1D 16 KB (AL = 1: one call)
            case 2 when _has686: R[0] = 0x03020101; R[3] = 0; R[1] = 0; R[2] = _hasSse ? 0x0c040882u : 0x0c040843u; break;   // P3 Coppermine: 82 = L2 256 KB 8-way
            default: R[0] = 0; R[3] = 0; R[2] = 0; R[1] = 0; break;   // (a real P5 returns the leaf 1 data for higher leaves; harmless)
        }
    }

    // the model's extra state in a save state (never written for the legacy model, so its states keep their old layout)
    private void SaveModelState(System.IO.BinaryWriter w)
    {
        w.Write(Model.Id); w.Write(Model.ClockHz);
        w.Write(Cr4); w.Write(_tscOffset); w.Write(_msrCesr); w.Write(_msrCtr0Lo); w.Write(_msrCtr1Lo);
        SaveFpu(w);
        if (_has686)
        {
            w.Write(_sysCs); w.Write(_sysEsp); w.Write(_sysEip);
            foreach (ulong m in _mtrr) w.Write(m);
            w.Write(_pmcEvt0); w.Write(_pmcEvt1); w.Write(_pmc0); w.Write(_pmc1); w.Write(_biosSign);
        }
        if (_hasSse) SaveSse(w);   // after the P6 block: P2 / P55C states keep their layout
    }

    private void LoadModelState(System.IO.BinaryReader r)
    {
        string id = r.ReadString(); long clk = r.ReadInt64();
        if (id != Model.Id || clk != Model.ClockHz)
            throw new System.IO.InvalidDataException($"the state is for CPU {id} at {clk / 1_000_000} MHz, this machine has {Model.Id} at {Model.ClockHz / 1_000_000} MHz");
        Cr4 = r.ReadUInt32(); _tscOffset = r.ReadInt64(); _msrCesr = r.ReadUInt32(); _msrCtr0Lo = r.ReadUInt32(); _msrCtr1Lo = r.ReadUInt32();
        LoadFpu(r);
        if (_has686)
        {
            _sysCs = r.ReadUInt32(); _sysEsp = r.ReadUInt32(); _sysEip = r.ReadUInt32();
            for (int i = 0; i < _mtrr.Length; i++) _mtrr[i] = r.ReadUInt64();
            _pmcEvt0 = r.ReadUInt64(); _pmcEvt1 = r.ReadUInt64(); _pmc0 = r.ReadUInt64(); _pmc1 = r.ReadUInt64(); _biosSign = r.ReadUInt64();
        }
        if (_hasSse) LoadSse(r);
    }
}
