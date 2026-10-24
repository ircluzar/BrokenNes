using System;
using System.Collections.Generic;
using System.Text;
using NesEmulator.Corruption;

namespace NesEmulator.Debugging;

/// <summary>The NES for the debugger: the 6502 (any CPU core; CPU_FIX, the default, tells exactly when an interrupt is coming), its bus
/// with RAM mirrors folded, PRG ROM by offset (so a breakpoint on a NESFab label follows its bank), PRG RAM and OAM.</summary>
public sealed class NesDebugMachine : DebugMachine, INesInstructionHook, IBusDebugTap
{
    private readonly NES nes;
    private readonly NesCpu cpu;
    private readonly DebugCpu[] cpus;
    private long cycle;
    private bool execArmed, memArmed;
    private Bus? tappedBus;

    public NesDebugMachine(NES nes)
    {
        this.nes = nes;
        cpu = new NesCpu(this, nes);
        cpus = new DebugCpu[] { cpu };
    }

    public NES Nes => nes;
    public override string Console => "nes";
    public override IReadOnlyList<DebugCpu> Cpus => cpus;
    public override long Frame => Math.Max(0, nes.FramesExecuted - 1);
    public override int Scanline => nes.DebugBus?.ppu is IPpuFrameClock c ? c.ProbeScanline : -1;
    public override int Dot => nes.DebugBus?.ppu is IPpuProbe p ? p.ProbeDot : -1;
    public override long Cycle => cycle;

    public override void ApplyHooks(bool[] exec, bool[] memory)
    {
        execArmed = exec[0]; memArmed = memory[0];
        nes.DebugHook = execArmed ? this : null;
        SyncBus();
    }

    // The tap belongs to a bus; loading a ROM makes a new one, so the hook re-checks.
    private void SyncBus()
    {
        var bus = nes.DebugBus;
        if (tappedBus != null && (!memArmed || !ReferenceEquals(tappedBus, bus))) { tappedBus.DebugTap = null; tappedBus.DebugMaskRam(false); tappedBus = null; }
        if (memArmed && bus != null && !ReferenceEquals(tappedBus, bus)) { bus.DebugTap = this; bus.DebugMaskRam(true); tappedBus = bus; }
    }

    void INesInstructionHook.BeforeInstruction(long cpuCycle)
    {
        cycle = cpuCycle;
        if (memArmed && !ReferenceEquals(nes.DebugBus, tappedBus)) SyncBus();
        Debugger?.OnInstruction(cpu, cpuCycle);
    }

    void IBusDebugTap.OnRead(ushort address, byte value) => Debugger?.OnAccess(cpu, address, value, false, AccessSource.Cpu, -1);
    void IBusDebugTap.OnWrite(ushort address, byte value) => Debugger?.OnAccess(cpu, address, value, true, AccessSource.Cpu, -1);

    void IBusDebugTap.OnDmcDmaHalt(ushort address) => Debugger?.OnDmcHalt(cpu, address);

    void IBusDebugTap.OnOamDma(byte page)
    {
        var d = Debugger;
        if (d == null) return;
        for (int i = 0; i < 256; i++)
        {
            ushort a = (ushort)(page << 8 | i);
            d.OnAccess(cpu, a, cpu.Peek(a), false, AccessSource.OamDma, -1);
        }
        for (int i = 0; i < 256; i++) d.OnAccessKey(cpu, new SpaceKey(NesCpu.SpaceOam, (uint)i), 0x2004, cpu.Peek((ushort)(page << 8 | i)), true, AccessSource.OamDma, -1);
    }

    public override IReadOnlyList<IMemoryDomain> Domains()
    {
        var list = new List<IMemoryDomain>(MachineDomains.For(nes));
        list.Add(new FuncDomain("BUS", "The CPU's address space ($0000-$FFFF), read without side effects (registers read as the open bus)", DomainKind.Registers, 0x10000, a => cpu.Peek((uint)a), (a, v) => cpu.Poke((uint)a, v)));
        if (nes.DebugBus?.ppu is IPpuProbe p)
            list.Add(new FuncDomain("PPU", "The picture chip's address space ($0000-$3FFF: pattern tables, nametables, palette)", DomainKind.Video, 0x4000, a => p.ProbePpuBusRead((ushort)a), (a, v) => p.ProbePpuBusWrite((ushort)a, v)));
        return list;
    }

    public override IEnumerable<string> Limitations
    {
        get
        {
            string id = nes.GetCpuCoreId();
            if (id is "CPU_SPD" or "CPU_EIL" or "CPU_LW2" or "CPU_ULQ")
                yield return $"{id} reads and writes RAM without the bus (a speed shortcut), so RAM watches miss some of its accesses; CPU_FIX (the default) goes through the bus every time.";
            if (id != "CPU_FIX")
                yield return $"{id} does not say when an interrupt is about to be taken: the call stack finds interrupt entries afterwards, and an execute breakpoint on the instruction an NMI interrupts can be reported twice.";
            if (nes.DebugBus?.ppu is not IPpuProbe)
                yield return "this PPU core has no scanline / dot counter: V and H read -1.";
        }
    }
}

/// <summary>The NES 6502 for the debugger.</summary>
internal sealed class NesCpu : DebugCpu
{
    public const int SpaceCpu = 0, SpacePrg = 1, SpacePrgRam = 2, SpaceOam = 3;
    private readonly NES nes;
    private readonly AddressSpace[] spaces;

    public NesCpu(DebugMachine m, NES nes) : base(m)
    {
        this.nes = nes;
        spaces = new[]
        {
            new AddressSpace { Id = SpaceCpu, Name = "cpu", Size = 0x10000, Aliases = new[] { "ram", "bus", "R", "G" }, Description = "the CPU's addresses ($0000-$07FF RAM with its mirrors to $1FFF folded; $2000-$2007 with their mirrors to $3FFF)" },
            new AddressSpace { Id = SpacePrg, Name = "prg", Size = (uint)Math.Max(1, nes.GetPrgRomSize()), Aliases = new[] { "P", "prgrom", "rom" }, Description = "PRG ROM by offset (whatever bank maps it)" },
            new AddressSpace { Id = SpacePrgRam, Name = "sram", Size = (uint)Math.Max(1, nes.GetPrgRamSize()), Aliases = new[] { "prgram", "S", "W", "wram" }, Description = "cartridge PRG RAM by offset ($6000-$7FFF)" },
            new AddressSpace { Id = SpaceOam, Name = "oam", Size = 256, Description = "sprite RAM (written through $2004 and OAM DMA)" },
        };
    }

    private Bus? Bus => nes.DebugBus;
    private CPU_FIX? Fix => Bus?.cpu as CPU_FIX;

    public override string Id => "main";
    public override string Name => $"6502 ({nes.GetCpuCoreId()})";
    public override IDebugDisassembler Disassembler => Disasm6502.Instance;
    public override IReadOnlyList<AddressSpace> Spaces => spaces;
    public override uint PC => nes.GetCpuRegs().PC;
    public override uint SP => 0x100u | (byte)nes.GetCpuRegs().SP;

    public override bool WillExecute(out string? interrupt)
    {
        interrupt = null;
        var fix = Fix;
        if (fix != null && fix.DebugInterruptPending) { interrupt = fix.DebugPendingInterruptKind; return false; }
        return true;
    }

    private static readonly string[] Regs = { "A", "X", "Y", "S", "SP", "P", "PC" };
    public override IReadOnlyList<string> RegisterNames => Regs;

    public override bool TryGetRegister(string name, out long value)
    {
        var r = nes.GetCpuRegs();
        value = name.ToUpperInvariant() switch
        {
            "A" => r.A, "X" => r.X, "Y" => r.Y, "S" or "SP" => (byte)r.SP, "P" => r.P, "PC" => r.PC, _ => -1,
        };
        return value >= 0;
    }

    public override bool TrySetRegister(string name, long v)
    {
        var f = Fix;
        if (f == null) return false;
        switch (name.ToUpperInvariant())
        {
            case "A": f.A = (byte)v; return true;
            case "X": f.X = (byte)v; return true;
            case "Y": f.Y = (byte)v; return true;
            case "S": case "SP": f.SP = (byte)v; return true;
            case "P": f.status = (byte)v; return true;
            case "PC": f.PC = (ushort)v; return true;
        }
        return false;
    }

    public override byte Peek(uint address)
    {
        var bus = Bus;
        if (bus == null) return 0;
        ushort a = (ushort)address;
        if (a < 0x2000) return bus.ram[a & 0x7FF];
        if (a < 0x6000) return bus.GetOpenBus();   // registers and expansion: reading them has effects, so the debugger does not
        if (a < 0x8000) { int n = nes.GetPrgRamSize(); return n > 0 ? nes.PeekPrgRam((a - 0x6000) % n) : bus.GetOpenBus(); }
        return nes.TryCpuToPrgIndex(a, out int i) ? nes.PeekPrg(i) : bus.GetOpenBus();
    }

    public override bool Poke(uint address, byte value)
    {
        var bus = Bus;
        if (bus == null) return false;
        ushort a = (ushort)address;
        if (a < 0x2000) { bus.ram[a & 0x7FF] = value; return true; }
        if (a >= 0x6000 && a < 0x8000) { int n = nes.GetPrgRamSize(); if (n == 0) return false; nes.PokePrgRam((a - 0x6000) % n, value); return true; }
        if (a >= 0x8000 && nes.TryCpuToPrgIndex(a, out int i)) { nes.PokePrg(i, value); return true; }
        return false;
    }

    public override byte PeekSpace(int space, uint offset) => space switch
    {
        SpacePrg => nes.PeekPrg((int)offset),
        SpacePrgRam => nes.PeekPrgRam((int)offset),
        SpaceOam => 0,
        _ => Peek(offset),
    };

    public override bool PokeSpace(int space, uint offset, byte value)
    {
        switch (space)
        {
            case SpacePrg: nes.PokePrg((int)offset, value); return true;
            case SpacePrgRam: nes.PokePrgRam((int)offset, value); return true;
            case SpaceOam: return false;
            default: return Poke(offset, value);
        }
    }

    private static uint Fold(uint a)
    {
        a &= 0xFFFF;
        if (a < 0x2000) return a & 0x7FF;
        if (a < 0x4000) return 0x2000 | (a & 7);
        return a;
    }

    public override int AccessKeys(uint address, bool write, Span<SpaceKey> keys)
    {
        ushort a = (ushort)address;
        keys[0] = new SpaceKey(SpaceCpu, Fold(a));
        if (a >= 0x8000) { if (nes.TryCpuToPrgIndex(a, out int i)) { keys[1] = new SpaceKey(SpacePrg, (uint)i); return 2; } }
        else if (a >= 0x6000) { int n = nes.GetPrgRamSize(); if (n > 0) { keys[1] = new SpaceKey(SpacePrgRam, (uint)((a - 0x6000) % n)); return 2; } }
        else if (a == 0x2004 && write) { /* OAM data port: the OAM address is the PPU's, not exposed; the $2004 key above covers it */ }
        return 1;
    }

    public override SpaceKey CpuSpecKey(uint address) => new(SpaceCpu, Fold(address));

    public override bool BindSymbol(DebugSymbol sym, out SpaceKey key)
    {
        switch (sym.Domain)
        {
            case "prg": key = new SpaceKey(SpacePrg, sym.Address); return true;
            case "ram": key = new SpaceKey(SpaceCpu, sym.Address & 0x7FF); return true;
            case "sram": key = new SpaceKey(SpacePrgRam, sym.Address); return true;
            case "cpu": case "io": key = new SpaceKey(SpaceCpu, Fold(sym.Address)); return true;
        }
        key = default;
        return false;
    }

    public override uint StackAfterPush(uint sp, int bytes) => 0x100u | (byte)(sp - bytes);
    public override bool StackAbove(uint sp, uint reference) => (byte)sp > (byte)reference;

    public override bool DetectInterruptEntry(uint prevPc, uint prevSp, uint pc, uint sp, out string kind)
    {
        kind = "";
        if (Fix != null) return false;   // CPU_FIX says so exactly
        if ((byte)sp != (byte)(prevSp - 3)) return false;
        ushort nmi = (ushort)(Peek(0xFFFA) | Peek(0xFFFB) << 8), irq = (ushort)(Peek(0xFFFE) | Peek(0xFFFF) << 8);
        if (pc == nmi) { kind = "NMI"; return true; }
        if (pc == irq) { kind = "IRQ"; return true; }
        return false;
    }

    public override void FillTrace(ref TraceRecord r)
    {
        var g = nes.GetCpuRegs();
        r.R0 = g.A; r.R1 = g.X; r.R2 = g.Y; r.R3 = (byte)g.SP; r.P = g.P;
        uint bytes = FetchBytes(r.Pc);
        r.Bytes = bytes;
        r.Length = (byte)Classify(bytes).Length;
        if (r.Kind == 0)
        {
            Span<byte> b = stackalloc byte[3];
            b[0] = (byte)bytes; b[1] = (byte)(bytes >> 8); b[2] = (byte)(bytes >> 16);
            int ea = Disasm6502.EffectiveAddress(b[..r.Length], g.X, g.Y, a => Peek(a));
            r.Ea = ea;
            if (ea >= 0) r.EaValue = Peek((uint)ea);
        }
    }

    public override string FormatRegisters(in TraceRecord r)
    {
        var sb = new StringBuilder(40);
        sb.Append("A:").Append(r.R0.ToString("X2")).Append(" X:").Append(r.R1.ToString("X2")).Append(" Y:").Append(r.R2.ToString("X2"))
          .Append(" S:").Append(r.R3.ToString("X2")).Append(" P:").Append(Flags(r.P, "NVUBDIZC"));
        return sb.ToString();
    }

    /// <summary>Mesen's text status flags: a letter per bit from bit 7, upper case when set.</summary>
    internal static string Flags(byte p, string letters)
    {
        Span<char> c = stackalloc char[8];
        for (int i = 0; i < 8; i++) c[i] = (p & (0x80 >> i)) != 0 ? char.ToUpperInvariant(letters[i]) : char.ToLowerInvariant(letters[i]);
        return new string(c);
    }
}
