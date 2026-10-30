using System;
using System.Collections.Generic;
using System.Text;
using NesEmulator.Corruption;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace NesEmulator.Debugging;

// =====================================================================================================================================
//  The Sega consoles for the debugger (BrokenDev Timeflow T0): the Master System / Game Gear / SG-1000 Z80, and the Genesis's 68000 with
//  its sound Z80. The same engine as the NES, SNES and Game Boy: canonical keys per memory (mirrors folded), the watching taps on the
//  boards (null when off), the hooks at each instruction boundary.
// =====================================================================================================================================

/// <summary>The extra registers a trace record has no room for, kept beside the debugger's ring by a sequence number the record carries.</summary>
internal sealed class TraceSide
{
    private uint[]? data;
    private uint[]? seqs;
    private int size;
    private readonly int width;
    private uint seq;
    public TraceSide(int width) { this.width = width; }

    /// <summary>A new entry: returns its sequence number and the span to fill.</summary>
    public uint Next(int ringSize, out Span<uint> slot)
    {
        if (data == null) { size = Math.Max(1024, ringSize); data = new uint[size * width]; seqs = new uint[size]; }
        uint s = ++seq;
        int i = (int)(s % (uint)size);
        seqs![i] = s;
        slot = data.AsSpan(i * width, width);
        return s;
    }

    /// <summary>The entry of a sequence number (its low <paramref name="bits"/> bits as the record kept them), or false when it has been overwritten.</summary>
    public bool TryGet(uint recordSeq, int bits, out ReadOnlySpan<uint> slot)
    {
        slot = default;
        if (data == null || recordSeq == 0) return false;
        uint mask = bits >= 32 ? uint.MaxValue : (1u << bits) - 1;
        // the newest entry whose low bits match
        uint s = seq - ((seq - recordSeq) & mask);
        if (s == 0 || seq - s >= (uint)size) return false;
        int i = (int)(s % (uint)size);
        if (seqs![i] != s) return false;
        slot = data.AsSpan(i * width, width);
        return true;
    }
}

// ------------------------------------------------------------------------------------------------------------------------------------ Master System / Game Gear

/// <summary>The Master System, Game Gear and SG-1000 for the debugger: the Z80 with its work RAM mirror folded, ROM and cartridge RAM by offset through the mapper's
/// slots, the I/O ports by the ports the console decodes (a port mirror reaches the documented one), and the VDP's VRAM and colour RAM written through the data port.</summary>
public sealed class SmsDebugMachine : DebugMachine, ISegaInstructionHook, ISegaBusDebugTap
{
    private readonly SmsSession session;
    private readonly BOARD_SMS board;
    private readonly SmsZ80 cpu;
    private readonly DebugCpu[] cpus;

    public SmsDebugMachine(SmsSession session)
    {
        this.session = session;
        board = session.Board;
        cpu = new SmsZ80(this, board);
        cpus = new DebugCpu[] { cpu };
    }

    public BOARD_SMS Board => board;
    public override string Console => Consoles.Key(session.Console);
    public override IReadOnlyList<DebugCpu> Cpus => cpus;
    public override long Frame => board.Frames;
    public override int Scanline => board.Vdp.Line;
    /// <summary>The Z80 cycle within the line (228 a line).</summary>
    public override int Dot => board.LineCycle;
    /// <summary>Z80 T-states since power-on.</summary>
    public override long Cycle => board.Cpu.TotalCycles;

    public override void ApplyHooks(bool[] exec, bool[] memory)
    {
        board.DebugHook = exec[0] ? this : null;
        board.DebugTap = memory[0] ? this : null;
    }

    void ISegaInstructionHook.BeforeInstruction() => Debugger?.OnInstruction(cpu, board.Cpu.TotalCycles);

    void ISegaBusDebugTap.OnRead(uint address, byte value) => Debugger?.OnAccess(cpu, address, value, false, AccessSource.Cpu, -1);
    void ISegaBusDebugTap.OnWrite(uint address, byte value) => Debugger?.OnAccess(cpu, address, value, true, AccessSource.Cpu, -1);
    void ISegaBusDebugTap.OnWriteDone(uint address, byte value) => Debugger?.OnWriteDone(cpu, address, value);
    void ISegaBusDebugTap.OnDmaRead(uint address, byte value) { }

    void ISegaBusDebugTap.OnPort(ushort port, byte value, bool write)
    {
        var d = Debugger;
        if (d == null) return;
        int canon = cpu.FoldPort(port);
        d.OnPortAccess(cpu, new SpaceKey(SmsZ80.SpaceIo, (uint)canon), 0x10000u | (uint)canon, port, value, write);
        // the data port: the VRAM or colour RAM byte the write reaches (the VDP's address register before it moves on)
        if (write && canon == 0xBE && !(board.IsGameGear && (port & 0xFF) < 7))
        {
            var v = board.Vdp;
            if (v.DebugCode == 3) d.OnAccessKey(cpu, new SpaceKey(SmsZ80.SpaceCram, (uint)(v.DebugAddress & (board.IsGameGear ? 0x3F : 0x1F))), port, value, true, AccessSource.Cpu, -1);
            else d.OnAccessKey(cpu, new SpaceKey(SmsZ80.SpaceVram, (uint)(v.DebugAddress & 0x3FFF)), port, value, true, AccessSource.Cpu, -1);
        }
    }

    public override IReadOnlyList<IMemoryDomain> Domains()
    {
        var list = new List<IMemoryDomain>(MachineDomains.For(session));
        list.Add(new FuncDomain("BUS", "The Z80's address space ($0000-$FFFF), read without side effects", DomainKind.Registers, 0x10000, a => cpu.Peek((uint)a), (a, v) => cpu.Poke((uint)a, v)));
        return list;
    }

    public override IEnumerable<string> Limitations => new[]
    {
        "I/O breakpoints name the ports the console decodes ($3E $3F $7E $7F $BE $BF $DC $DD, the Game Gear's $00-$06, the FM unit's $F0-$F2): a port's mirrors reach them, a mirror typed in a breakpoint does not.",
        "VRAM and colour RAM watches see the data port's writes ($BE), not the VDP's own reads; the VDP registers are not a watch space.",
    };
}

/// <summary>The Master System / Game Gear Z80 for the debugger.</summary>
internal sealed class SmsZ80 : DebugCpu
{
    public const int SpaceCpu = 0, SpaceRom = 1, SpaceRam = 2, SpaceSram = 3, SpaceIo = 4, SpaceVram = 5, SpaceCram = 6;
    private readonly BOARD_SMS board;
    private readonly AddressSpace[] spaces;
    private readonly TraceSide side = new(8);
    private Z80<SmsBus> Z => board.Cpu;

    public SmsZ80(DebugMachine m, BOARD_SMS board) : base(m)
    {
        this.board = board;
        spaces = new[]
        {
            new AddressSpace { Id = SpaceCpu, Name = "cpu", Size = 0x10000, Aliases = new[] { "bus" }, Description = "the Z80's addresses (the work RAM mirror $E000-$FFFF folded onto $C000-$DFFF)" },
            new AddressSpace { Id = SpaceRom, Name = "rom", Size = (uint)Math.Max(1, board.Cart.Rom.Length), Aliases = new[] { "prg" }, Description = "cartridge ROM by offset (whatever slot maps it)" },
            new AddressSpace { Id = SpaceRam, Name = "ram", Size = 0x2000, Aliases = new[] { "wram", "work" }, Description = "work RAM by offset ($C000-$DFFF and its mirror)" },
            new AddressSpace { Id = SpaceSram, Name = "sram", Size = (uint)Math.Max(1, board.Cart.Ram?.Length ?? 0), Aliases = new[] { "cartram" }, Description = "cartridge RAM by offset (mapped into slot 2)" },
            new AddressSpace { Id = SpaceIo, Name = "io", Size = 0x100, Aliases = new[] { "port", "ports" }, Description = "I/O ports by the port the console decodes ($BE the VDP data port, $BF control / status, $7E/$7F counters and PSG, $DC/$DD pads, $3E/$3F memory and I/O control)" },
            new AddressSpace { Id = SpaceVram, Name = "vram", Size = 0x4000, Description = "video RAM (16 KB), written through the data port $BE" },
            new AddressSpace { Id = SpaceCram, Name = "cram", Size = board.IsGameGear ? 64u : 32u, Description = board.IsGameGear ? "colour RAM (32 colours of 12 bits, byte pairs)" : "colour RAM (32 colours of 6 bits)" },
        };
    }

    public override string Id => "main";
    public override string Name => "Z80";
    public override IDebugDisassembler Disassembler => DisasmZ80.Instance;
    public override IReadOnlyList<AddressSpace> Spaces => spaces;
    public override uint PC => Z.PC;
    public override uint SP => Z.SP;

    public override bool WillExecute(out string? interrupt) => Z80Debug.WillExecute(Z.BoundaryKind, out interrupt);

    public override IReadOnlyList<string> RegisterNames => Z80Debug.Regs;
    public override bool TryGetRegister(string name, out long value) => Z80Debug.TryGet(Z, name, out value);
    public override bool TrySetRegister(string name, long value) => Z80Debug.TrySet(Z, name, value);

    /// <summary>The port the console decodes for a port address (the Master System looks at A7, A6 and A0; the Game Gear's $00-$06 and the FM unit's $F0-$F2 are their own).</summary>
    public int FoldPort(ushort port)
    {
        int p = port & 0xFF;
        if (board.IsGameGear && p < 7) return p;
        if (p is 0xF0 or 0xF1 or 0xF2) return p;
        return (p & 0xC0) switch { 0x00 => 0x3E, 0x40 => 0x7E, 0x80 => 0xBE, _ => 0xDC } | (p & 1);
    }

    private static uint Fold(uint a) { a &= 0xFFFF; return a >= 0xE000 ? a - 0x2000 : a; }

    private SpaceKey Phys(uint address)
    {
        uint a = Fold(address);
        if (a >= 0xC000) return new SpaceKey(SpaceRam, a - 0xC000);
        if (board.DebugBiosMapped) return new SpaceKey(SpaceCpu, a);
        int r = board.Cart.DebugRomIndex((ushort)a);
        if (r >= 0) return new SpaceKey(SpaceRom, (uint)r);
        int s = board.Cart.DebugRamIndex((ushort)a);
        if (s >= 0) return new SpaceKey(SpaceSram, (uint)s);
        return new SpaceKey(SpaceCpu, a);
    }

    public override byte Peek(uint address) => board.Peek((ushort)address);

    public override bool Poke(uint address, byte value)
    {
        var k = Phys(address);
        return k.Space != SpaceCpu && PokeSpace(k.Space, k.Offset, value);
    }

    public override byte PeekSpace(int space, uint o) => space switch
    {
        SpaceRom => o < (uint)board.Cart.Rom.Length ? board.Cart.Rom[o] : (byte)0,
        SpaceRam => o < 0x2000 ? board.Ram[o] : (byte)0,
        SpaceSram => board.Cart.Ram is { } r && o < (uint)r.Length ? r[o] : (byte)0,
        SpaceVram => o < 0x4000 ? board.Vdp.Vram[o] : (byte)0,
        SpaceCram => o < spaces[SpaceCram].Size ? board.Vdp.CramByte((int)o) : (byte)0,
        SpaceIo => 0,   // reading a port has effects
        _ => Peek(o),
    };

    public override bool PokeSpace(int space, uint o, byte v)
    {
        switch (space)
        {
            case SpaceRom: if (o >= (uint)board.Cart.Rom.Length) return false; board.Cart.Rom[o] = v; return true;
            case SpaceRam: if (o >= 0x2000) return false; board.Ram[o] = v; return true;
            case SpaceSram: if (board.Cart.Ram is not { } r || o >= (uint)r.Length) return false; r[o] = v; return true;
            case SpaceVram: if (o >= 0x4000) return false; board.Vdp.Vram[o] = v; return true;
            case SpaceCram: if (o >= spaces[SpaceCram].Size) return false; board.Vdp.SetCramByte((int)o, v); return true;
            case SpaceCpu: return Poke(o, v);
        }
        return false;
    }

    public override int AccessKeys(uint address, bool write, Span<SpaceKey> keys)
    {
        keys[0] = new SpaceKey(SpaceCpu, Fold(address));
        var p = Phys(address);
        if (p.Space == SpaceCpu) return 1;
        keys[1] = p;
        return 2;
    }

    public override SpaceKey CpuSpecKey(uint address) => new(SpaceCpu, Fold(address));

    /// <summary>"bank:address": a 16 KB ROM bank as WLA-DX and devkitSMS write it (bank x $4000 + address mod $4000) for $0000-$BFFF; work RAM above.</summary>
    public override bool TryBankedSpec(uint bank, uint address, out SpaceKey key)
    {
        address &= 0xFFFF;
        if (address < 0xC000) { key = new SpaceKey(SpaceRom, bank * 0x4000 + (address & 0x3FFF)); return true; }
        key = new SpaceKey(SpaceRam, Fold(address) - 0xC000);
        return true;
    }

    public override bool BindSymbol(DebugSymbol sym, out SpaceKey key)
    {
        uint a = sym.Address;
        int bank = sym.Bank >= 0 ? sym.Bank : a > 0xFFFF ? (int)(a >> 16) : -1;
        a &= sym.Domain == "cpu" ? 0xFFFFu : uint.MaxValue;
        switch (sym.Domain)
        {
            case "cpu":
                if (bank >= 0) return TryBankedSpec((uint)bank, a, out key);
                // a variable in work RAM binds to the RAM (so a watch covers its size and every mirror); code and the rest to the CPU address
                key = Fold(a) >= 0xC000 ? new SpaceKey(SpaceRam, Fold(a) - 0xC000) : new SpaceKey(SpaceCpu, Fold(a));
                return true;
            case "rom": case "prg": key = new SpaceKey(SpaceRom, a); return true;
            case "wram": case "ram": key = new SpaceKey(SpaceRam, a >= 0xC000 ? Fold(a) - 0xC000 : a & 0x1FFF); return true;
            case "sram": key = new SpaceKey(SpaceSram, a); return true;
            case "io": key = new SpaceKey(SpaceIo, a & 0xFF); return true;
        }
        key = default;
        return false;
    }

    public override uint StackAfterPush(uint sp, int bytes) => (ushort)(sp - bytes);
    public override bool StackAbove(uint sp, uint reference) => (ushort)sp > (ushort)reference;

    public override void FillTrace(ref TraceRecord r) => Z80Debug.FillTrace(this, Z.AF, Z.BC, Z.DE, Z.HL, Z.IX, Z.IY, Z.SP, Z.I, Z.R, Z.IFF1, Z.IFF2, Z.InterruptMode, Z.AF2, Z.BC2, Z.DE2, Z.HL2, side, ref r);
    public override string FormatRegisters(in TraceRecord r) => Z80Debug.FormatRegisters(side, in r);
}

/// <summary>What the two Z80 adapters share: the registers by name, the boundary kinds, the trace columns.</summary>
internal static class Z80Debug
{
    public static readonly string[] Regs = { "A", "F", "B", "C", "D", "E", "H", "L", "AF", "BC", "DE", "HL", "IX", "IY", "IXH", "IXL", "IYH", "IYL", "SP", "S", "PC", "I", "R",
                                             "AF2", "BC2", "DE2", "HL2", "IFF1", "IFF2", "IM" };

    public static bool WillExecute(int kind, out string? interrupt)
    {
        interrupt = kind switch { 1 => "NMI", 2 => "IRQ", _ => null };
        return kind == 0;
    }

    public static bool TryGet<TBus>(Z80<TBus> z, string name, out long v) where TBus : struct, IZ80Bus
    {
        v = name.ToUpperInvariant() switch
        {
            "A" => z.A, "F" => z.F, "B" => z.B, "C" => z.C, "D" => z.D, "E" => z.E, "H" => z.H, "L" => z.L,
            "AF" => z.AF, "BC" => z.BC, "DE" => z.DE, "HL" => z.HL, "IX" => z.IX, "IY" => z.IY, "IXH" => z.IXH, "IXL" => z.IXL, "IYH" => z.IYH, "IYL" => z.IYL,
            "SP" or "S" => z.SP, "PC" => z.PC, "I" => z.I, "R" => z.R,
            "AF2" or "AF'" => z.AF2, "BC2" or "BC'" => z.BC2, "DE2" or "DE'" => z.DE2, "HL2" or "HL'" => z.HL2,
            "IFF1" => z.IFF1 ? 1 : 0, "IFF2" => z.IFF2 ? 1 : 0, "IM" => z.InterruptMode,
            _ => -1,
        };
        return v >= 0;
    }

    public static bool TrySet<TBus>(Z80<TBus> z, string name, long v) where TBus : struct, IZ80Bus
    {
        switch (name.ToUpperInvariant())
        {
            case "A": z.A = (byte)v; return true;
            case "F": z.F = (byte)v; return true;
            case "B": z.B = (byte)v; return true;
            case "C": z.C = (byte)v; return true;
            case "D": z.D = (byte)v; return true;
            case "E": z.E = (byte)v; return true;
            case "H": z.H = (byte)v; return true;
            case "L": z.L = (byte)v; return true;
            case "AF": z.AF = (ushort)v; return true;
            case "BC": z.BC = (ushort)v; return true;
            case "DE": z.DE = (ushort)v; return true;
            case "HL": z.HL = (ushort)v; return true;
            case "IX": z.IX = (ushort)v; return true;
            case "IY": z.IY = (ushort)v; return true;
            case "IXH": z.IXH = (byte)v; return true;
            case "IXL": z.IXL = (byte)v; return true;
            case "IYH": z.IYH = (byte)v; return true;
            case "IYL": z.IYL = (byte)v; return true;
            case "SP": case "S": z.SP = (ushort)v; return true;
            case "PC": z.PC = (ushort)v; return true;   // at an instruction boundary the fetch has not read PC yet
            case "I": z.I = (byte)v; return true;
            case "R": z.R = (byte)v; return true;
            case "AF2": case "AF'": z.AF2 = (ushort)v; return true;
            case "BC2": case "BC'": z.BC2 = (ushort)v; return true;
            case "DE2": case "DE'": z.DE2 = (ushort)v; return true;
            case "HL2": case "HL'": z.HL2 = (ushort)v; return true;
            case "IFF1": z.IFF1 = v != 0; return true;
            case "IFF2": z.IFF2 = v != 0; return true;
            case "IM": if (v is < 0 or > 2) return false; z.InterruptMode = (int)v; return true;
        }
        return false;
    }

    /// <summary>R0-R4 AF BC DE HL SP; P and R6 the low 16 bits of the side entry's number (IX, IY, I, R, the flip-flops, the shadow set); the effective address and its value.</summary>
    public static void FillTrace(DebugCpu c, ushort af, ushort bc, ushort de, ushort hl, ushort ix, ushort iy, ushort sp, byte i, byte rr, bool iff1, bool iff2, int im,
                                 ushort af2, ushort bc2, ushort de2, ushort hl2, TraceSide side, ref TraceRecord r)
    {
        r.R0 = af; r.R1 = bc; r.R2 = de; r.R3 = hl; r.R4 = sp;
        r.R5 = (byte)((iff1 ? 1 : 0) | (iff2 ? 2 : 0) | im << 2);
        uint seq = side.Next(c.MachineDebuggerRingSize, out var slot);
        slot[0] = ix; slot[1] = iy; slot[2] = i; slot[3] = rr; slot[4] = af2; slot[5] = bc2; slot[6] = de2; slot[7] = hl2;
        r.P = (byte)seq; r.R6 = (byte)(seq >> 8);
        uint bytes = c.FetchBytes(r.Pc);
        r.Bytes = bytes;
        r.Length = (byte)c.Classify(bytes).Length;
        if (r.Kind == 0)
        {
            Span<byte> b = stackalloc byte[4];
            for (int k = 0; k < 4; k++) b[k] = (byte)(bytes >> (8 * k));
            int ea = DisasmZ80.EffectiveAddress(b[..Math.Min(4, Math.Max(1, (int)r.Length))], bc, de, hl, ix, iy, sp);
            r.Ea = ea;
            if (ea >= 0) r.EaValue = c.Peek((uint)ea);
        }
    }

    /// <summary>Mesen's Z80 trace columns: A F B C D E H L IX IY SP, then the flags as letters.</summary>
    public static string FormatRegisters(TraceSide side, in TraceRecord r)
    {
        var sb = new StringBuilder(96);
        void X2(string n, int v) => sb.Append(n).Append(':').Append((v & 0xFF).ToString("X2")).Append(' ');
        X2("A", r.R0 >> 8); X2("F", r.R0); X2("B", r.R1 >> 8); X2("C", r.R1); X2("D", r.R2 >> 8); X2("E", r.R2); X2("H", r.R3 >> 8); X2("L", r.R3);
        if (side.TryGet((uint)(r.P | r.R6 << 8), 16, out var s)) sb.Append("IX:").Append(s[0].ToString("X4")).Append(" IY:").Append(s[1].ToString("X4")).Append(' ');
        sb.Append("SP:").Append(r.R4.ToString("X4"));
        int f = r.R0 & 0xFF;
        sb.Append(' ');
        const string names = "SZYHXPNC";
        for (int k = 0; k < 8; k++) sb.Append((f & (0x80 >> k)) != 0 ? names[k] : char.ToLowerInvariant(names[k]));
        if ((r.R5 & 1) != 0) sb.Append(" IFF");
        return sb.ToString();
    }
}

// ------------------------------------------------------------------------------------------------------------------------------------ Genesis

/// <summary>
/// The Genesis / Mega Drive for the debugger: the 68000 ("main") and, when the sound unit is the Genesis's own, its Z80 ("z80"). The 68000's work RAM is folded through its
/// mirrors ($E00000-$FFFFFF onto $FF0000), the cartridge by ROM / battery RAM offset (the SSF2 banks as mapped), the Z80's RAM through the $A00000 window; the Z80's bank window
/// reaches the 68000's space by its 68000 address. Each CPU's watches see the other CPU's accesses into its memory (source "OtherCpu"), and the 68000's watches see the VDP's DMA
/// reads of ROM and RAM (source "Dma").
/// </summary>
public sealed class MdDebugMachine : DebugMachine
{
    private readonly MdSession session;
    private readonly BOARD_MD board;
    internal readonly MdM68k M68;
    internal readonly MdZ80? Z;
    private readonly DebugCpu[] cpus;
    private readonly MainHooks main;
    private readonly Z80Hooks z80;
    private MdSoundUnit? hookedUnit;

    public MdDebugMachine(MdSession session)
    {
        this.session = session;
        board = session.Board;
        M68 = new MdM68k(this, board);
        if (board.Sound is MdSoundUnit)
        {
            Z = new MdZ80(this, board);
            cpus = new DebugCpu[] { M68, Z };
        }
        else cpus = new DebugCpu[] { M68 };
        main = new MainHooks(this);
        z80 = new Z80Hooks(this);
    }

    public BOARD_MD Board => board;
    public override string Console => "md";
    public override IReadOnlyList<DebugCpu> Cpus => cpus;
    public override long Frame => board.Vdp.Frames;
    public override int Scanline => board.Vdp.Line;
    /// <summary>Master clocks into the line (3420 a line).</summary>
    public override int Dot => board.Vdp.LineOffset;
    /// <summary>The master clock (53.69 MHz NTSC).</summary>
    public override long Cycle => board.MasterClock;

    internal MdSoundUnit? Unit => board.Sound as MdSoundUnit;

    public override void ApplyHooks(bool[] exec, bool[] memory)
    {
        bool zExec = Z != null && exec[1], zMem = Z != null && memory[1];
        board.DebugHook = exec[0] ? main : null;
        // each CPU's tap also reports its accesses into the other's memory, so either CPU's watches arm both taps
        board.DebugTap = memory[0] || zMem ? main : null;
        var unit = Unit;
        if (hookedUnit != null && !ReferenceEquals(hookedUnit, unit)) { hookedUnit.DebugHook = null; hookedUnit.DebugTap = null; hookedUnit = null; }
        if (unit != null && Z != null)
        {
            unit.DebugHook = zExec ? z80 : null;
            unit.DebugTap = zMem || memory[0] ? z80 : null;
            hookedUnit = zExec || zMem || memory[0] ? unit : null;
        }
    }

    private sealed class MainHooks : ISegaInstructionHook, ISegaBusDebugTap
    {
        private readonly MdDebugMachine m;
        public MainHooks(MdDebugMachine m) { this.m = m; }
        public void BeforeInstruction() => m.Debugger?.OnInstruction(m.M68, m.board.MasterClock);
        public void OnRead(uint address, byte value) => Access(address, value, false);
        public void OnWrite(uint address, byte value) => Access(address, value, true);
        public void OnWriteDone(uint address, byte value) => m.Debugger?.OnWriteDone(m.M68, address, value);
        public void OnPort(ushort port, byte value, bool write) { }
        public void OnDmaRead(uint address, byte value) => m.Debugger?.OnAccess(m.M68, address, value, false, AccessSource.Dma, -1);

        private void Access(uint address, byte value, bool write)
        {
            var d = m.Debugger;
            if (d == null) return;
            d.OnAccess(m.M68, address, value, write, AccessSource.Cpu, -1);
            // the 68000 reaching the Z80's RAM through its window: the Z80's watches see it too
            if (m.Z != null && address >= 0xA00000 && address < 0xA04000)
                d.OnAccessKey(m.Z, new SpaceKey(MdZ80.SpaceZram, address & 0x1FFF), address & 0x1FFF, value, write, AccessSource.OtherCpu, -1);
        }
    }

    private sealed class Z80Hooks : ISegaInstructionHook, ISegaBusDebugTap
    {
        private readonly MdDebugMachine m;
        public Z80Hooks(MdDebugMachine m) { this.m = m; }
        public void BeforeInstruction() { var u = m.Unit; if (u != null && m.Z != null) m.Debugger?.OnInstruction(m.Z, u.Z80MasterClock); }
        public void OnRead(uint address, byte value) => Access(address, value, false);
        public void OnWrite(uint address, byte value) => Access(address, value, true);
        public void OnWriteDone(uint address, byte value) { if (m.Z != null) m.Debugger?.OnWriteDone(m.Z, address, value); }
        public void OnDmaRead(uint address, byte value) { }
        public void OnPort(ushort port, byte value, bool write)
        {
            if (m.Z != null) m.Debugger?.OnPortAccess(m.Z, new SpaceKey(MdZ80.SpaceIo, (uint)(port & 0xFF)), 0x10000u | (uint)(port & 0xFF), port, value, write);
        }

        private void Access(uint address, byte value, bool write)
        {
            var d = m.Debugger;
            if (d == null || m.Z == null) return;
            d.OnAccess(m.Z, address, value, write, AccessSource.Cpu, -1);
            // the bank window: the 68000's watches see the Z80's access at its 68000 address
            if (address >= 0x8000 && m.Unit is { } u)
                d.OnAccess(m.M68, (uint)(u.BankRegister << 15 | (int)(address & 0x7FFF)), value, write, AccessSource.OtherCpu, -1);
        }
    }

    public override IReadOnlyList<IMemoryDomain> Domains()
    {
        var list = new List<IMemoryDomain>(MachineDomains.For(session));
        list.Add(new FuncDomain("BUS", "The 68000's address space ($000000-$FFFFFF), read without side effects (the VDP, I/O and bus registers read as $FF)", DomainKind.Registers, 0x1000000,
            a => board.DebugPeek((uint)a), (a, v) => M68.Poke((uint)a, v)));
        if (Z != null)
            list.Add(new FuncDomain("Z80BUS", "The Z80's address space ($0000-$FFFF), read without side effects (the bank window shows the 68000's space)", DomainKind.Registers, 0x10000,
                a => Z.Peek((uint)a), (a, v) => Z.Poke((uint)a, v)));
        return list;
    }

    public override IEnumerable<string> Limitations
    {
        get
        {
            yield return "VRAM, colour RAM and VSRAM are spaces to read and write, not watch spaces: the VDP's FIFO and its DMA writes are not tapped (the DMA's reads of ROM and work RAM are).";
            yield return "a 68000 word read at the program counter is taken as a program fetch (the prefetch and the extension words are not data reads).";
            if (Z != null) yield return "while the Z80's hook is armed its polling loops are run, not fast-forwarded (the output is the same, the speed lower).";
            else yield return $"the sound unit ({board.Sound.CoreName}) is not the Genesis's own: there is no Z80 to debug.";
        }
    }
}

/// <summary>The Genesis 68000 for the debugger.</summary>
internal sealed class MdM68k : DebugCpu
{
    public const int SpaceCpu = 0, SpaceRom = 1, SpaceRam = 2, SpaceSram = 3, SpaceZram = 4, SpaceVram = 5, SpaceCram = 6, SpaceVsram = 7;
    private readonly BOARD_MD board;
    private readonly AddressSpace[] spaces;
    private readonly TraceSide side = new(16);
    private readonly DisasmM68k disasm;
    private M68k<MdBus> C => board.Cpu;

    public MdM68k(DebugMachine m, BOARD_MD board) : base(m)
    {
        this.board = board;
        disasm = DisasmM68k.WithMemory(a => board.DebugPeek(a));
        spaces = new[]
        {
            new AddressSpace { Id = SpaceCpu, Name = "cpu", Size = 0x1000000, Aliases = new[] { "bus" }, Description = "the 68000's 24-bit addresses (work RAM's mirrors $E00000-$FFFFFF folded onto $FF0000-$FFFFFF, the Z80 RAM window $A00000-$A03FFF onto $A00000-$A01FFF)" },
            new AddressSpace { Id = SpaceRom, Name = "rom", Size = (uint)Math.Max(1, board.Cart.Rom.Length), Aliases = new[] { "prg" }, Description = "cartridge ROM by offset (the SSF2 banks as mapped)" },
            new AddressSpace { Id = SpaceRam, Name = "ram", Size = 0x10000, Aliases = new[] { "wram", "work" }, Description = "the 68000's work RAM by offset ($FF0000-$FFFFFF and its mirrors)" },
            new AddressSpace { Id = SpaceSram, Name = "sram", Size = (uint)Math.Max(1, board.Cart.Sram.Length), Aliases = new[] { "cartram" }, Description = "cartridge battery RAM by offset" },
            new AddressSpace { Id = SpaceZram, Name = "zram", Size = 0x2000, Aliases = new[] { "z80ram" }, Description = "the Z80's 8 KB of RAM (the 68000 reaches it at $A00000)" },
            new AddressSpace { Id = SpaceVram, Name = "vram", Size = 0x10000, Description = "video RAM (64 KB; read and written here, not watched)" },
            new AddressSpace { Id = SpaceCram, Name = "cram", Size = 128, Description = "colour RAM (64 words, each little-endian as the CRAM domain shows it)" },
            new AddressSpace { Id = SpaceVsram, Name = "vsram", Size = 128, Description = "vertical scroll RAM (words, little-endian)" },
        };
    }

    public override string Id => "main";
    public override string Name => "68000";
    public override IDebugDisassembler Disassembler => disasm;
    public override IReadOnlyList<AddressSpace> Spaces => spaces;
    public override uint PC => (C.Pc - 4) & 0xFFFFFF;
    public override uint SP => C.A[7];
    public override int AddressDigits => 6;
    public override uint NextAddress(uint pc, int i) => (uint)((pc + i) & 0xFFFFFF);

    public override bool WillExecute(out string? interrupt)
    {
        interrupt = null;
        var c = C;
        if (c.Halted) return false;
        if (c.TraceDue) { interrupt = "TRACE"; return false; }
        int level = board.InterruptSample;
        if (level > c.IntMask || level == 7)
        {
            interrupt = level switch { 6 => "VINT", 4 => "HINT", 2 => "EXTINT", _ => "IRQ" + level };
            return false;
        }
        return !c.Stopped;
    }

    private static readonly string[] Regs = { "D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "A0", "A1", "A2", "A3", "A4", "A5", "A6", "A7", "SP", "USP", "SSP", "PC", "SR", "CCR" };
    public override IReadOnlyList<string> RegisterNames => Regs;

    public override bool TryGetRegister(string name, out long v)
    {
        var c = C;
        string n = name.ToUpperInvariant();
        if (n.Length == 2 && (n[0] == 'D' || n[0] == 'A') && n[1] is >= '0' and <= '7') { v = n[0] == 'D' ? c.D[n[1] - '0'] : c.A[n[1] - '0']; return true; }
        v = n switch { "SP" => c.A[7], "USP" => c.Usp, "SSP" => c.Ssp, "PC" => PC, "SR" => c.ReadSr(), "CCR" => c.ReadCcr(), _ => -1 };
        return v >= 0;
    }

    public override bool TrySetRegister(string name, long v)
    {
        var c = C;
        string n = name.ToUpperInvariant();
        if (n.Length == 2 && (n[0] == 'D' || n[0] == 'A') && n[1] is >= '0' and <= '7') { if (n[0] == 'D') c.D[n[1] - '0'] = (uint)v; else c.A[n[1] - '0'] = (uint)v; return true; }
        switch (n)
        {
            case "SP": c.A[7] = (uint)v; return true;
            case "USP": c.Usp = (uint)v; return true;
            case "SSP": c.Ssp = (uint)v; return true;
            case "SR": c.WriteSr((uint)v); return true;
            case "CCR": c.WriteCcr((uint)v); return true;
            case "PC":
            {
                // the prefetch queue is refilled from the new address (without bus cycles): the next instruction is the one there
                uint a = (uint)v & 0xFFFFFE;
                c.Ir = Word(a); c.Irc = Word(a + 2); c.Pc = a + 4;
                return true;
            }
        }
        return false;
    }

    private ushort Word(uint a) => (ushort)(board.DebugPeek(a) << 8 | board.DebugPeek(a + 1));

    private static uint Fold(uint a)
    {
        a &= 0xFFFFFF;
        if (a >= 0xE00000) return 0xFF0000 | (a & 0xFFFF);
        if (a >= 0xA00000 && a < 0xA04000) return 0xA00000 | (a & 0x1FFF);
        return a;
    }

    private SpaceKey Phys(uint address)
    {
        uint a = address & 0xFFFFFF;
        if (a < 0x400000)
        {
            int s = board.Cart.DebugSramIndex(a);
            if (s >= 0) return new SpaceKey(SpaceSram, (uint)s);
            int r = board.Cart.DebugRomIndex(a);
            return r >= 0 ? new SpaceKey(SpaceRom, (uint)r) : new SpaceKey(SpaceCpu, a);
        }
        if (a >= 0xE00000) return new SpaceKey(SpaceRam, a & 0xFFFF);
        if (a >= 0xA00000 && a < 0xA04000) return new SpaceKey(SpaceZram, a & 0x1FFF);
        return new SpaceKey(SpaceCpu, Fold(a));
    }

    public override byte Peek(uint address) => board.DebugPeek(address);

    public override bool Poke(uint address, byte value)
    {
        var k = Phys(address);
        return k.Space != SpaceCpu && PokeSpace(k.Space, k.Offset, value);
    }

    private byte[]? Zram => (board.Sound as MdSoundUnit)?.Z80Ram;

    public override byte PeekSpace(int space, uint o) => space switch
    {
        SpaceRom => o < (uint)board.Cart.Rom.Length ? board.Cart.Rom[o] : (byte)0,
        SpaceRam => o < 0x10000 ? board.Ram[o] : (byte)0,
        SpaceSram => o < (uint)board.Cart.Sram.Length ? board.Cart.Sram[o] : (byte)0,
        SpaceZram => Zram is { } z && o < 0x2000 ? z[o] : (byte)0,
        SpaceVram => o < 0x10000 ? board.Vdp.Vram[o] : (byte)0,
        SpaceCram => o < 128 ? (byte)(board.Vdp.Cram[o >> 1] >> ((o & 1) != 0 ? 8 : 0)) : (byte)0,
        SpaceVsram => o < 128 ? (byte)(board.Vdp.Vsram[o >> 1] >> ((o & 1) != 0 ? 8 : 0)) : (byte)0,
        _ => Peek(o),
    };

    public override bool PokeSpace(int space, uint o, byte v)
    {
        static void SetWord(ushort[] w, uint o, byte v) { int i = (int)(o >> 1); w[i] = (o & 1) != 0 ? (ushort)(w[i] & 0x00FF | v << 8) : (ushort)(w[i] & 0xFF00 | v); }
        switch (space)
        {
            case SpaceRom: if (o >= (uint)board.Cart.Rom.Length) return false; board.Cart.Rom[o] = v; return true;
            case SpaceRam: if (o >= 0x10000) return false; board.Ram[o] = v; return true;
            case SpaceSram: if (o >= (uint)board.Cart.Sram.Length) return false; board.Cart.Sram[o] = v; return true;
            case SpaceZram: if (Zram is not { } z || o >= 0x2000) return false; z[o] = v; return true;
            case SpaceVram: if (o >= 0x10000) return false; board.Vdp.Vram[o] = v; return true;
            case SpaceCram: if (o >= 128) return false; SetWord(board.Vdp.Cram, o, v); return true;
            case SpaceVsram: if (o >= 128) return false; SetWord(board.Vdp.Vsram, o, v); return true;
            case SpaceCpu: return Poke(o, v);
        }
        return false;
    }

    public override int AccessKeys(uint address, bool write, Span<SpaceKey> keys)
    {
        keys[0] = new SpaceKey(SpaceCpu, Fold(address));
        var p = Phys(address);
        if (p.Space == SpaceCpu) return 1;
        keys[1] = p;
        return 2;
    }

    public override SpaceKey CpuSpecKey(uint address) => new(SpaceCpu, Fold(address));

    public override bool BindSymbol(DebugSymbol sym, out SpaceKey key)
    {
        uint a = sym.Address;
        switch (sym.Domain)
        {
            case "cpu":
                // a variable in work RAM, battery RAM or the Z80's RAM binds to that memory (a watch then covers its size and every mirror); code and the rest to the CPU address
                key = Phys(a);
                if (key.Space is SpaceRom or SpaceCpu) key = new SpaceKey(SpaceCpu, Fold(a));
                return true;
            case "rom": case "prg": key = new SpaceKey(SpaceRom, a); return true;
            case "wram": case "ram": key = new SpaceKey(SpaceRam, a & 0xFFFF); return true;
            case "sram": key = new SpaceKey(SpaceSram, a); return true;
            case "zram": key = new SpaceKey(SpaceZram, a & 0x1FFF); return true;
        }
        key = default;
        return false;
    }

    public override uint StackAfterPush(uint sp, int bytes) => sp - (uint)bytes;
    public override bool StackAbove(uint sp, uint reference) => (sp & 0xFFFFFF) > (reference & 0xFFFFFF);

    /// <summary>R0 / R1 the side entry's number (D0-D7, A0-A7), R2 the status register, R3 / R4 the stack pointer.</summary>
    public override void FillTrace(ref TraceRecord r)
    {
        var c = C;
        uint seq = side.Next(MachineDebuggerRingSize, out var slot);
        for (int i = 0; i < 8; i++) { slot[i] = c.D[i]; slot[8 + i] = c.A[i]; }
        r.R0 = (ushort)seq; r.R1 = (ushort)(seq >> 16);
        r.R2 = c.ReadSr();
        r.R3 = (ushort)c.A[7]; r.R4 = (ushort)(c.A[7] >> 16);
        uint bytes = FetchBytes(r.Pc);
        r.Bytes = bytes;
        r.Length = (byte)Classify(bytes).Length;
    }

    public override string FormatRegisters(in TraceRecord r)
    {
        var sb = new StringBuilder(200);
        if (side.TryGet((uint)(r.R0 | r.R1 << 16), 32, out var s))
        {
            for (int i = 0; i < 8; i++) sb.Append('D').Append(i).Append(':').Append(s[i].ToString("X8")).Append(' ');
            for (int i = 0; i < 7; i++) sb.Append('A').Append(i).Append(':').Append(s[8 + i].ToString("X8")).Append(' ');
        }
        uint sp = (uint)(r.R3 | r.R4 << 16);
        sb.Append("SP:").Append(sp.ToString("X8")).Append(" SR:").Append(r.R2.ToString("X4")).Append(' ');
        int sr = r.R2;
        sb.Append((sr & 0x8000) != 0 ? 'T' : 't').Append((sr & 0x2000) != 0 ? 'S' : 's').Append((sr >> 8) & 7).Append(' ');
        const string f = "XNZVC";
        for (int k = 0; k < 5; k++) sb.Append((sr & (0x10 >> k)) != 0 ? f[k] : char.ToLowerInvariant(f[k]));
        return sb.ToString();
    }
}

/// <summary>The Genesis's sound Z80 for the debugger: its 8 KB of RAM (mirrored at $2000), the YM2612 and the bank register, the window at $8000 into the 68000's space.</summary>
internal sealed class MdZ80 : DebugCpu
{
    public const int SpaceCpu = 0, SpaceZram = 1, SpaceM68k = 2, SpaceRom = 3, SpaceRam = 4, SpaceIo = 5;
    private readonly BOARD_MD board;
    private readonly AddressSpace[] spaces;
    private readonly TraceSide side = new(8);

    public MdZ80(DebugMachine m, BOARD_MD board) : base(m)
    {
        this.board = board;
        spaces = new[]
        {
            new AddressSpace { Id = SpaceCpu, Name = "cpu", Size = 0x10000, Aliases = new[] { "bus" }, Description = "the Z80's addresses (its RAM's mirror $2000-$3FFF folded onto $0000-$1FFF)" },
            new AddressSpace { Id = SpaceZram, Name = "zram", Size = 0x2000, Aliases = new[] { "ram", "z80ram" }, Description = "the Z80's 8 KB of RAM by offset" },
            new AddressSpace { Id = SpaceM68k, Name = "m68k", Size = 0x1000000, Aliases = new[] { "68k", "bank" }, Description = "the 68000's space by its address (what the bank window at $8000-$FFFF reaches)" },
            new AddressSpace { Id = SpaceRom, Name = "rom", Size = (uint)Math.Max(1, board.Cart.Rom.Length), Aliases = new[] { "prg" }, Description = "cartridge ROM by offset, through the bank window" },
            new AddressSpace { Id = SpaceRam, Name = "wram", Size = 0x10000, Aliases = new[] { "68kram" }, Description = "the 68000's work RAM by offset, through the bank window" },
            new AddressSpace { Id = SpaceIo, Name = "io", Size = 0x100, Aliases = new[] { "port", "ports" }, Description = "the Z80's I/O ports (nothing answers them on the Genesis)" },
        };
    }

    private MdSoundUnit Unit => (MdSoundUnit)board.Sound;
    private Z80<MdSoundUnit.ZBus> Zc => Unit.Cpu;

    public override string Id => "z80";
    public override string Name => "Z80 (sound)";
    public override IDebugDisassembler Disassembler => DisasmZ80.Instance;
    public override IReadOnlyList<AddressSpace> Spaces => spaces;
    public override uint PC => board.Sound is MdSoundUnit ? Zc.PC : 0u;
    public override uint SP => board.Sound is MdSoundUnit ? Zc.SP : 0u;

    public override bool WillExecute(out string? interrupt) => Z80Debug.WillExecute(Zc.BoundaryKind, out interrupt);

    public override IReadOnlyList<string> RegisterNames => Z80Debug.Regs;
    public override bool TryGetRegister(string name, out long value) { value = -1; return board.Sound is MdSoundUnit && Z80Debug.TryGet(Zc, name, out value); }
    public override bool TrySetRegister(string name, long value) => board.Sound is MdSoundUnit && Z80Debug.TrySet(Zc, name, value);

    private static uint Fold(uint a) { a &= 0xFFFF; return a < 0x4000 ? a & 0x1FFF : a; }
    private uint Window(uint a) => (uint)(Unit.BankRegister << 15 | (int)(a & 0x7FFF));

    public override byte Peek(uint address)
    {
        uint a = address & 0xFFFF;
        if (board.Sound is not MdSoundUnit u) return 0xFF;
        if (a < 0x4000) return u.Z80Ram[a & 0x1FFF];
        if (a >= 0x8000) return board.DebugPeek(Window(a));
        return 0xFF;   // the YM2612, the bank register and the VDP window: reading them has effects (or nothing to read)
    }

    public override bool Poke(uint address, byte value)
    {
        uint a = address & 0xFFFF;
        if (a < 0x4000) return PokeSpace(SpaceZram, a & 0x1FFF, value);
        if (a >= 0x8000) return ((MdDebugMachine)Machine).M68.Poke(Window(a), value);
        return false;
    }

    public override byte PeekSpace(int space, uint o) => space switch
    {
        SpaceZram => board.Sound is MdSoundUnit u && o < 0x2000 ? u.Z80Ram[o] : (byte)0,
        SpaceM68k => board.DebugPeek(o),
        SpaceRom => o < (uint)board.Cart.Rom.Length ? board.Cart.Rom[o] : (byte)0,
        SpaceRam => o < 0x10000 ? board.Ram[o] : (byte)0,
        SpaceIo => 0,
        _ => Peek(o),
    };

    public override bool PokeSpace(int space, uint o, byte v)
    {
        switch (space)
        {
            case SpaceZram: if (board.Sound is not MdSoundUnit u || o >= 0x2000) return false; u.Z80Ram[o] = v; return true;
            case SpaceM68k: return ((MdDebugMachine)Machine).M68.Poke(o, v);
            case SpaceRom: if (o >= (uint)board.Cart.Rom.Length) return false; board.Cart.Rom[o] = v; return true;
            case SpaceRam: if (o >= 0x10000) return false; board.Ram[o] = v; return true;
            case SpaceCpu: return Poke(o, v);
        }
        return false;
    }

    public override int AccessKeys(uint address, bool write, Span<SpaceKey> keys)
    {
        uint a = address & 0xFFFF;
        keys[0] = new SpaceKey(SpaceCpu, Fold(a));
        if (a < 0x4000) { keys[1] = new SpaceKey(SpaceZram, a & 0x1FFF); return 2; }
        if (a < 0x8000 || board.Sound is not MdSoundUnit) return 1;
        uint m = Window(a);
        keys[1] = new SpaceKey(SpaceM68k, m);
        if (m < 0x400000) { int r = board.Cart.DebugRomIndex(m); if (r >= 0) { keys[2] = new SpaceKey(SpaceRom, (uint)r); return 3; } }
        else if (m >= 0xE00000) { keys[2] = new SpaceKey(SpaceRam, m & 0xFFFF); return 3; }
        return 2;
    }

    public override SpaceKey CpuSpecKey(uint address) => new(SpaceCpu, Fold(address));

    public override bool BindSymbol(DebugSymbol sym, out SpaceKey key)
    {
        uint a = sym.Address & 0xFFFF;
        switch (sym.Domain)
        {
            case "cpu": key = a < 0x4000 ? new SpaceKey(SpaceZram, a & 0x1FFF) : new SpaceKey(SpaceCpu, Fold(a)); return true;
            case "zram": case "ram": key = new SpaceKey(SpaceZram, a & 0x1FFF); return true;
        }
        key = default;
        return false;
    }

    public override uint StackAfterPush(uint sp, int bytes) => (ushort)(sp - bytes);
    public override bool StackAbove(uint sp, uint reference) => (ushort)sp > (ushort)reference;

    public override void FillTrace(ref TraceRecord r)
    {
        var z = Zc;
        Z80Debug.FillTrace(this, z.AF, z.BC, z.DE, z.HL, z.IX, z.IY, z.SP, z.I, z.R, z.IFF1, z.IFF2, z.InterruptMode, z.AF2, z.BC2, z.DE2, z.HL2, side, ref r);
    }

    public override string FormatRegisters(in TraceRecord r) => Z80Debug.FormatRegisters(side, in r);
}
