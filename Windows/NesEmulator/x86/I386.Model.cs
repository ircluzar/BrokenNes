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

    public static readonly string[] Ids = { "LEGACY", "386DX", "386DX+387", "486DX", "486DX2", "486DX4", "P55C" };

    /// <summary>"P55C", "P55C@200", "486DX2@50", "386DX@40"...: a preset, optionally at another clock (MHz). Null if unknown.</summary>
    public static CpuModel? Parse(string s)
    {
        string id = s, mhz = "";
        int at = s.IndexOf('@');
        if (at >= 0) { id = s[..at]; mhz = s[(at + 1)..]; }
        CpuModel? m = id.ToUpperInvariant() switch
        {
            "LEGACY" => LegacyModel(), "386DX" or "386" => I386DX, "386DX+387" => I386DXFpu, "486DX" or "486" => I486DX,
            "486DX2" => I486DX2, "486DX4" => I486DX4, "P55C" or "PMMX" or "P166MMX" or "PENTIUMMMX" => PentiumMmx, _ => null,
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
    private readonly bool _legacy, _hasFpu, _has486, _hasCpuid, _has586, _hasMmx;

    /// <summary>The clock RDTSC reads, in instructions (the machine passes its own: instructions + idle time); null = <see cref="Cycle"/>.</summary>
    public Func<long>? InstructionClock;
    /// <summary>FERR#: an unmasked x87 exception with CR0.NE clear (the PC routes it to IRQ 13).</summary>
    public Action? Ferr;

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
            case 0x0b: return false;                  // UD2 is P6
            case >= 0x60 and <= 0x7f: return _hasMmx && MmxOpcode(b1);
            case >= 0xd0 and <= 0xff: return _hasMmx && MmxOpcode(b1);
        }
        return true;
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

    private int Msr(bool write)
    {
        if (Cpl != 0) return K(Throw(EX_GP, 0));
        uint idx = R[1];
        ulong v = ((ulong)R[2] << 32) | R[0];
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
        if ((v & ~(CR4_TSD | CR4_DE | CR4_MCE)) != 0) return Throw(EX_GP, 0);
        Cr4 = v;
        return true;
    }

    private void CpuidModel()
    {
        switch (R[0])
        {
            case 0:
            {
                R[0] = 1;
                var v = System.Text.Encoding.ASCII.GetBytes(Model.Vendor.PadRight(12)[..12]);
                R[3] = BitConverter.ToUInt32(v, 0); R[2] = BitConverter.ToUInt32(v, 4); R[1] = BitConverter.ToUInt32(v, 8);
                break;
            }
            case 1: R[0] = Model.Signature; R[3] = 0; R[1] = 0; R[2] = Model.FeatureEdx; break;
            default: R[0] = 0; R[3] = 0; R[2] = 0; R[1] = 0; break;   // (a real P5 returns the leaf 1 data for higher leaves; harmless)
        }
    }

    // the model's extra state in a save state (never written for the legacy model, so its states keep their old layout)
    private void SaveModelState(System.IO.BinaryWriter w)
    {
        w.Write(Model.Id); w.Write(Model.ClockHz);
        w.Write(Cr4); w.Write(_tscOffset); w.Write(_msrCesr); w.Write(_msrCtr0Lo); w.Write(_msrCtr1Lo);
        SaveFpu(w);
    }

    private void LoadModelState(System.IO.BinaryReader r)
    {
        string id = r.ReadString(); long clk = r.ReadInt64();
        if (id != Model.Id || clk != Model.ClockHz)
            throw new System.IO.InvalidDataException($"the state is for CPU {id} at {clk / 1_000_000} MHz, this machine has {Model.Id} at {Model.ClockHz / 1_000_000} MHz");
        Cr4 = r.ReadUInt32(); _tscOffset = r.ReadInt64(); _msrCesr = r.ReadUInt32(); _msrCtr0Lo = r.ReadUInt32(); _msrCtr1Lo = r.ReadUInt32();
        LoadFpu(r);
    }
}
