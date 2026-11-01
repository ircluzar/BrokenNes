using System;
using System.Collections.Generic;
using System.Text;
using NesEmulator.Corruption;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace NesEmulator.Debugging;

/// <summary>
/// The SNES (and the WideMess, the same board) for the debugger: the 65816 with every address folded to the memory it reaches (work RAM
/// through $00-$3F/$80-$BF:0000-1FFF and the $2180 port, registers through every system bank, ROM and SRAM through every mirror), every
/// DMA / HDMA byte on both buses, the picture chip's VRAM / CGRAM / OAM through their ports; and the SPC700 (its own CPU in the list).
/// </summary>
public sealed class SnesDebugMachine : DebugMachine, ISnesInstructionHook, ISnesBusDebugTap
{
    private readonly SnesSession session;
    private readonly BOARD_SFC board;
    private readonly Snes65816 main;
    private readonly Spc700 spc;
    private readonly DebugCpu[] cpus;
    private bool spcArmed, spcMemArmed;
    private APU_SFC? hookedApu;
    private Action<ushort>? chainedSmpHook, smpHook;

    public SnesDebugMachine(SnesSession session)
    {
        this.session = session;
        board = session.Board;
        main = new Snes65816(this, board);
        spc = new Spc700(this);
        cpus = new DebugCpu[] { main, spc };
    }

    public BOARD_SFC Board => board;
    public SnesSession Session => session;
    internal APU_SFC? Apu => session.Spc;
    public override string Console => session.Console == ConsoleKind.WideMess ? "widemess" : "snes";
    public override IReadOnlyList<DebugCpu> Cpus => cpus;
    public override long Frame => board.VBlankCount;
    public override int Scanline => board.Scanline;
    public override int Dot => board.LineClock >> 2;
    public override long Cycle => board.MasterClock;

    public override void ApplyHooks(bool[] exec, bool[] memory)
    {
        board.DebugHook = exec[0] || exec[1] ? this : null;   // the SPC's hook is re-checked from the 65816's (the sound unit can be swapped)
        board.DebugTap = memory[0] ? this : null;              // DMA and HDMA bytes
        board.Cpu.DebugInterpose(memory[0] ? watchingBus ??= new WatchingBus(this) : null);   // the CPU's own accesses
        spcArmed = exec[1] || memory[1];
        spcMemArmed = memory[1];
        SyncSpcHook();
    }

    private void SyncSpcHook()
    {
        var apu = spcArmed ? session.Spc : null;
        if (ReferenceEquals(apu, hookedApu)) return;
        if (hookedApu != null && ReferenceEquals(hookedApu.SmpHook, smpHook)) hookedApu.SmpHook = chainedSmpHook;
        hookedApu = null; chainedSmpHook = null;
        if (apu != null)
        {
            chainedSmpHook = apu.SmpHook;
            var prev = chainedSmpHook;
            smpHook = prev == null ? OnSmp : pc => { prev(pc); OnSmp(pc); };
            apu.SmpHook = smpHook;
            hookedApu = apu;
            spc.Bind(apu);
        }
    }

    void ISnesInstructionHook.BeforeInstruction()
    {
        if (spcArmed && !ReferenceEquals(session.Spc, hookedApu)) SyncSpcHook();
        if (Debugger is { } d && d.WantsCpu(0)) d.OnInstruction(main, board.MasterClock);
    }

    private readonly (ushort addr, byte kind)[] spcAcc = new (ushort, byte)[4];

    private void OnSmp(ushort pc)
    {
        var d = Debugger;
        if (d == null) return;
        var st = spc.State;
        // the writes the previous SPC700 instruction made (predicted from its operands, seen now with their values)
        for (int i = 0; i < st.SpcPendingWrites; i++) d.OnAccess(spc, st.SpcAccess[i].addr, spc.Peek(st.SpcAccess[i].addr), true, AccessSource.Cpu, -1);
        st.SpcPendingWrites = 0;
        d.OnInstruction(spc, board.MasterClock);
        if (!spcMemArmed || hookedApu == null || hookedApu.Smp.Halted) return;
        var smp = hookedApu.Smp;
        uint bytes = spc.FetchBytes(smp.PC);
        Span<byte> b = stackalloc byte[3];
        b[0] = (byte)bytes; b[1] = (byte)(bytes >> 8); b[2] = (byte)(bytes >> 16);
        int n = DisasmSpc700.Accesses(b, smp.A, smp.X, smp.Y, smp.SP, (smp.PSW & 0x20) != 0, a => spc.Peek(a), spcAcc);
        for (int i = 0; i < n; i++)
        {
            var (addr, kind) = spcAcc[i];
            if ((kind & 2) != 0) d.OnAccess(spc, addr, spc.Peek(addr), false, AccessSource.Cpu, -1);
            if ((kind & 4) != 0 && st.SpcPendingWrites < st.SpcAccess.Length) st.SpcAccess[st.SpcPendingWrites++] = (addr, 4);
        }
    }

    private WatchingBus? watchingBus;

    /// <summary>Sits between the 65816 and its bus while a watch is set: every access the CPU makes, with the value, then (or, for a
    /// write, first) the real bus. Reads are reported after they happen (the value), writes before they land (the picture chip's port
    /// addresses still say where they go).</summary>
    private sealed class WatchingBus : ISnesBus
    {
        private readonly SnesDebugMachine m;
        public WatchingBus(SnesDebugMachine machine) { m = machine; }
        public byte Read(uint address)
        {
            byte v = m.board.Cpu.DebugRealBus.Read(address);
            m.Debugger?.OnAccess(m.main, address & 0xFFFFFF, v, false, AccessSource.Cpu, -1);
            return v;
        }
        public void Write(uint address, byte value)
        {
            var d = m.Debugger;
            d?.OnAccess(m.main, address & 0xFFFFFF, value, true, AccessSource.Cpu, -1);
            m.board.Cpu.DebugRealBus.Write(address, value);
            d?.OnWriteDone(m.main, address & 0xFFFFFF, value);
        }
        public void Idle() { m.Debugger?.OnIdleCycle(m.main); m.board.Cpu.DebugRealBus.Idle(); }
    }

    void ISnesBusDebugTap.OnRead(uint address, byte value, SnesBusSource source, int channel) =>
        Debugger?.OnAccess(main, address, value, false, (AccessSource)source, channel);

    void ISnesBusDebugTap.OnWrite(uint address, byte value, SnesBusSource source, int channel) =>
        Debugger?.OnAccess(main, address, value, true, (AccessSource)source, channel);

    public override IReadOnlyList<IMemoryDomain> Domains()
    {
        var list = new List<IMemoryDomain>(MachineDomains.For(session));
        if (board.WramHigh is { } hi) list.Insert(1, new ArrayDomain("WRAMHI", $"WideMess work RAM ({hi.Length / 1024} KB, banks $70-$7D)", DomainKind.Work, hi));
        list.Add(new FuncDomain("REGS", "CPU I/O and DMA registers: $4200-$421F as they read without side effects, $4300-$437F (offset = address - $4200)", DomainKind.Registers, 0x180,
            a => a >= 0x100 ? board.DebugDmaRegister(a - 0x100) : main.Peek((uint)(0x4200 + a)), (a, v) => { }, writable: false));
        list.Add(new FuncDomain("BUS", "The 65816's 24-bit address space, read without side effects (registers read as the open bus)", DomainKind.Registers, 0x1000000, a => main.Peek((uint)a), (a, v) => main.Poke((uint)a, v)));
        return list;
    }

    public override IEnumerable<string> Limitations
    {
        get
        {
            yield return "SPC700 watches are predicted from each instruction's operands before it runs (its accesses are not tapped, so the plugin's sound path stays untouched); stores' dummy reads are not listed.";
            yield return "VRAM reads through $2139/$213A are seen as register reads, not VRAM reads.";
            yield return "while a read or write watch is set the 65816 reaches its bus through the debugger's watching bus (its interface path, as on a bridge): the machine is the same, it only runs slower.";
            if (session.Spc == null) yield return "the sound unit is the HLE stand-in: there is no SPC700 to debug.";
        }
    }
}

/// <summary>The 65816 for the debugger.</summary>
internal sealed class Snes65816 : DebugCpu
{
    public const int SpaceCpu = 0, SpaceRom = 1, SpaceWram = 2, SpaceSram = 3, SpaceIo = 4, SpaceVram = 5, SpaceCgram = 6, SpaceOam = 7, SpaceWramHi = 8;
    private readonly BOARD_SFC board;
    private readonly AddressSpace[] spaces;
    private CPU_SFC Cpu => board.Cpu;

    public Snes65816(DebugMachine m, BOARD_SFC board) : base(m)
    {
        this.board = board;
        var s = new List<AddressSpace>
        {
            new() { Id = SpaceCpu, Name = "cpu", Size = 0x1000000, Aliases = new[] { "bus" }, Description = "24-bit CPU addresses no memory answers (coprocessor ports, open bus)" },
            new() { Id = SpaceRom, Name = "rom", Size = (uint)Math.Max(1, board.Cart.Rom.Length), Aliases = new[] { "prg" }, Description = "ROM by offset, through every mirror" },
            new() { Id = SpaceWram, Name = "wram", Size = 0x20000, Aliases = new[] { "ram" }, Description = "work RAM ($7E:0000-$7F:FFFF; $0000-$1FFF of every system bank; the $2180 port)" },
            new() { Id = SpaceSram, Name = "sram", Size = (uint)Math.Max(1, board.Cart.Sram.Length), Description = "cartridge RAM by offset" },
            new() { Id = SpaceIo, Name = "io", Size = 0x8000, Aliases = new[] { "regs", "registers" }, Description = "registers $2000-$7FFF of every system bank ($00-$3F, $80-$BF)" },
            new() { Id = SpaceVram, Name = "vram", Size = 0x10000, Description = "video RAM by byte (word address x 2), through $2118/$2119 and DMA" },
            new() { Id = SpaceCgram, Name = "cgram", Size = 0x200, Aliases = new[] { "palette" }, Description = "palette RAM by byte, through $2122 and DMA" },
            new() { Id = SpaceOam, Name = "oam", Size = 0x220, Description = "sprite RAM by byte, through $2104 and DMA" },
        };
        if (board.WramHigh is { } hi) s.Add(new() { Id = SpaceWramHi, Name = "wramhi", Size = (uint)hi.Length, Description = "the WideMess work RAM (banks $70-$7D)" });
        spaces = s.ToArray();
    }

    public override string Id => "main";
    public override string Name => "65816";
    public override IDebugDisassembler Disassembler => Disasm65816.Instance;
    public override IReadOnlyList<AddressSpace> Spaces => spaces;
    public override int AddressDigits => 6;
    public override uint PC => (uint)Cpu.PBR << 16 | Cpu.PC;
    public override uint SP => Cpu.S;
    public override DecodeContext Context => new(Cpu.E || (Cpu.P & 0x20) != 0, Cpu.E || (Cpu.P & 0x10) != 0, Cpu.E);
    public override uint NextAddress(uint pc, int i) => (pc & 0xFF0000) | (uint)((pc + i) & 0xFFFF);

    public override bool WillExecute(out string? interrupt)
    {
        var c = Cpu;
        interrupt = c.DebugInterruptPending ? c.DebugPendingInterruptKind : null;
        return c.DebugWillExecute;
    }

    private static readonly string[] Regs = { "A", "C", "X", "Y", "S", "SP", "D", "DB", "DBR", "PB", "PBR", "K", "P", "PC", "E" };
    public override IReadOnlyList<string> RegisterNames => Regs;

    public override bool TryGetRegister(string name, out long v)
    {
        var c = Cpu;
        v = name.ToUpperInvariant() switch
        {
            "A" or "C" => c.A, "X" => c.X, "Y" => c.Y, "S" or "SP" => c.S, "D" => c.D, "DB" or "DBR" => c.DBR, "PB" or "PBR" or "K" => c.PBR,
            "P" => c.P, "PC" => c.PC, "E" => c.E ? 1 : 0, _ => -1,
        };
        return v >= 0;
    }

    public override bool TrySetRegister(string name, long v)
    {
        var c = Cpu;
        switch (name.ToUpperInvariant())
        {
            case "A": case "C": c.A = (ushort)v; return true;
            case "X": c.X = (ushort)v; return true;
            case "Y": c.Y = (ushort)v; return true;
            case "S": case "SP": c.S = (ushort)v; return true;
            case "D": c.D = (ushort)v; return true;
            case "DB": case "DBR": c.DBR = (byte)v; return true;
            case "PB": case "PBR": case "K": c.PBR = (byte)v; return true;
            case "P": c.P = (byte)v; return true;
            case "PC": c.PC = (ushort)v; return true;
        }
        return false;
    }

    /// <summary>The memory a 24-bit address reaches (the board's own decoding, in the same order).</summary>
    internal SpaceKey Map(uint address)
    {
        address &= 0xFFFFFF;
        uint bank = address >> 16, off = address & 0xFFFF;
        if (bank is 0x7E or 0x7F) return new SpaceKey(SpaceWram, (bank - 0x7E) << 16 | off);
        if (board.DebugIsWramHighBank(bank)) return new SpaceKey(SpaceWramHi, (bank - 0x70) << 16 | off);
        if ((bank & 0x40) == 0 && off < 0x8000)
        {
            if (off < 0x2000) return new SpaceKey(SpaceWram, off);
            // $6000-$7FFF of the system banks is the cartridge's (HiROM SRAM) when it answers there
            if (off >= 0x6000 && board.DebugCartIndex(bank, off, out bool ss) is int si && si >= 0 && ss && (board.Coprocessor == null || !board.Coprocessor.Owns(bank, off)))
                return new SpaceKey(SpaceSram, (uint)si);
            return new SpaceKey(SpaceIo, off);
        }
        int i = board.DebugCartIndex(bank, off, out bool sram);
        if (i >= 0 && (board.Coprocessor == null || !board.Coprocessor.Owns(bank, off))) return new SpaceKey(sram ? SpaceSram : SpaceRom, (uint)i);
        return new SpaceKey(SpaceCpu, address);
    }

    public override byte Peek(uint address)
    {
        var k = Map(address);
        return k.Space == SpaceCpu ? board.DebugOpenBus : PeekSpace(k.Space, k.Offset);
    }

    public override bool Poke(uint address, byte value)
    {
        var k = Map(address);
        return k.Space != SpaceCpu && k.Space != SpaceIo && PokeSpace(k.Space, k.Offset, value);
    }

    public override byte PeekSpace(int space, uint o)
    {
        var ppu = board.Ppu;
        switch (space)
        {
            case SpaceRom: return o < (uint)board.Cart.Rom.Length ? board.Cart.Rom[o] : (byte)0;
            case SpaceWram: return board.Wram[o & 0x1FFFF];
            case SpaceWramHi: return board.WramHigh is { } h && o < (uint)h.Length ? h[o] : (byte)0;
            case SpaceSram: return o < (uint)board.Cart.Sram.Length ? board.Cart.Sram[o] : (byte)0;
            case SpaceVram: { ushort w = ppu.Vram[(o >> 1) & 0x7FFF]; return (o & 1) == 0 ? (byte)w : (byte)(w >> 8); }
            case SpaceCgram: { ushort w = ppu.Cgram[(o >> 1) & 0xFF]; return (o & 1) == 0 ? (byte)w : (byte)(w >> 8); }
            case SpaceOam: return ppu.Oam[o % 544];
            case SpaceIo: return o >= 0x4300 && o < 0x4380 ? board.DebugDmaRegister((int)(o - 0x4300)) : board.DebugOpenBus;
            default: return Peek(o);
        }
    }

    public override bool PokeSpace(int space, uint o, byte v)
    {
        var ppu = board.Ppu;
        switch (space)
        {
            case SpaceRom: if (o >= (uint)board.Cart.Rom.Length) return false; board.Cart.Rom[o] = v; return true;
            case SpaceWram: board.Wram[o & 0x1FFFF] = v; return true;
            case SpaceWramHi: if (board.WramHigh is not { } h || o >= (uint)h.Length) return false; h[o] = v; return true;
            case SpaceSram: if (o >= (uint)board.Cart.Sram.Length) return false; board.Cart.Sram[o] = v; return true;
            case SpaceVram: { ref ushort w = ref ppu.Vram[(o >> 1) & 0x7FFF]; w = (o & 1) == 0 ? (ushort)((w & 0xFF00) | v) : (ushort)((w & 0x00FF) | v << 8); return true; }
            case SpaceCgram: { ref ushort w = ref ppu.Cgram[(o >> 1) & 0xFF]; w = (o & 1) == 0 ? (ushort)((w & 0xFF00) | v) : (ushort)((w & 0x00FF) | (v & 0x7F) << 8); return true; }
            case SpaceOam: ppu.Oam[o % 544] = v; ppu.InvalidateCaches(); return true;
            default: return false;
        }
    }

    public override int AccessKeys(uint address, bool write, Span<SpaceKey> keys)
    {
        var k = Map(address);
        keys[0] = k;
        if (k.Space != SpaceIo || k.Offset < 0x2100 || k.Offset > 0x21FF) return 1;
        var ppu = board.Ppu;
        switch (k.Offset)
        {
            case 0x2118: if (write) { keys[1] = new SpaceKey(SpaceVram, (uint)(ppu.DebugVramWordAddress * 2)); return 2; } break;
            case 0x2119: if (write) { keys[1] = new SpaceKey(SpaceVram, (uint)(ppu.DebugVramWordAddress * 2 + 1)); return 2; } break;
            case 0x2122: if (write) { keys[1] = new SpaceKey(SpaceCgram, (uint)ppu.DebugCgramByteAddress); return 2; } break;
            case 0x2104: if (write) { keys[1] = new SpaceKey(SpaceOam, (uint)ppu.DebugOamByteAddress); return 2; } break;
            case 0x2180:
            {
                // the WRAM port: a write is seen before it lands; a read after, when the port address has already moved on
                uint a = write ? board.DebugWramPortAddress : (board.DebugWramPortAddress - 1) & board.DebugWramPortMask;
                keys[1] = a < 0x20000 ? new SpaceKey(SpaceWram, a) : new SpaceKey(SpaceWramHi, a - 0x20000);
                return 2;
            }
        }
        return 1;
    }

    public override SpaceKey CpuSpecKey(uint address) => Map(address);

    public override bool TryBankedSpec(uint bank, uint address, out SpaceKey key) { key = Map(bank << 16 | (address & 0xFFFF)); return true; }

    public override bool BindSymbol(DebugSymbol sym, out SpaceKey key)
    {
        switch (sym.Domain)
        {
            case "cpu": key = Map(sym.Address); return true;
            case "wram": key = new SpaceKey(SpaceWram, sym.Address & 0x1FFFF); return true;
            case "rom": key = new SpaceKey(SpaceRom, sym.Address); return true;
            case "sram": key = new SpaceKey(SpaceSram, sym.Address); return true;
            case "io": key = new SpaceKey(SpaceIo, sym.Address & 0xFFFF); return true;
        }
        key = default;
        return false;
    }

    public override uint StackAfterPush(uint sp, int bytes) => Cpu.E ? 0x100u | (byte)(sp - bytes) : (ushort)(sp - bytes);
    public override bool StackAbove(uint sp, uint reference) => (ushort)sp > (ushort)reference;

    public override void FillTrace(ref TraceRecord r)
    {
        var c = Cpu;
        r.R0 = c.A; r.R1 = c.X; r.R2 = c.Y; r.R3 = c.S; r.R4 = c.D; r.R5 = c.DBR; r.R6 = (byte)(c.E ? 1 : 0); r.P = c.P;
        uint bytes = FetchBytes(r.Pc);
        r.Bytes = bytes;
        var ctx = Context;
        r.Length = (byte)Disassembler.Classify((byte)bytes, 0, ctx).Length;
        if (r.Kind == 0)
        {
            Span<byte> b = stackalloc byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(bytes >> (8 * i));
            long ea = Disasm65816.EffectiveAddress(b[..r.Length], c.X, c.Y, c.D, c.S, c.DBR, c.E, a => Peek(a));
            r.Ea = (int)ea;
            if (ea >= 0) r.EaValue = Peek((uint)ea);
        }
    }

    public override DecodeContext ContextOf(in TraceRecord r) => new((r.R6 & 1) != 0 || (r.P & 0x20) != 0, (r.R6 & 1) != 0 || (r.P & 0x10) != 0, (r.R6 & 1) != 0);

    public override string FormatRegisters(in TraceRecord r)
    {
        var sb = new StringBuilder(64);
        sb.Append("A:").Append(r.R0.ToString("X4")).Append(" X:").Append(r.R1.ToString("X4")).Append(" Y:").Append(r.R2.ToString("X4"))
          .Append(" S:").Append(r.R3.ToString("X4")).Append(" D:").Append(r.R4.ToString("X4")).Append(" DB:").Append(r.R5.ToString("X2"))
          .Append(" P:").Append(NesCpu.Flags(r.P, "NVMXDIZC")).Append((r.R6 & 1) != 0 ? " E" : "");
        return sb.ToString();
    }
}

/// <summary>The SPC700 (the SNES sound CPU) for the debugger. Its own 64 KB is its only space; reading $F0-$FF shows the RAM under the I/O block
/// (the timers are read-to-clear, so the debugger never reads them).</summary>
internal sealed class Spc700 : DebugCpu
{
    private APU_SFC? apu;
    private readonly AddressSpace[] spaces = { new() { Id = 0, Name = "spc", Size = 0x10000, Aliases = new[] { "cpu", "aram" }, Description = "the SPC700's 64 KB (audio RAM; the boot ROM at $FFC0 while it is mapped)" } };

    private readonly (ushort, byte)[] traceAcc = new (ushort, byte)[4];
    public Spc700(SnesDebugMachine m) : base(m) { apu = m.Apu; }
    internal void Bind(APU_SFC a) => apu = a;

    private SMP_SFC? Smp => apu?.Smp;
    public override string Id => "spc";
    public override string Name => "SPC700";
    public override IDebugDisassembler Disassembler => DisasmSpc700.Instance;
    public override IReadOnlyList<AddressSpace> Spaces => spaces;
    public override uint PC => Smp?.PC ?? 0;
    public override uint SP => 0x100u | (Smp?.SP ?? 0);
    public override bool WillExecute(out string? interrupt) { interrupt = null; return Smp is { Halted: false }; }

    private static readonly string[] Regs = { "A", "X", "Y", "S", "SP", "P", "PSW", "PC", "YA" };
    public override IReadOnlyList<string> RegisterNames => Regs;

    public override bool TryGetRegister(string name, out long v)
    {
        var s = Smp;
        if (s == null) { v = 0; return false; }
        v = name.ToUpperInvariant() switch
        {
            "A" => s.A, "X" => s.X, "Y" => s.Y, "S" or "SP" => s.SP, "P" or "PSW" => s.PSW, "PC" => s.PC, "YA" => s.Y << 8 | s.A, _ => -1,
        };
        return v >= 0;
    }

    public override bool TrySetRegister(string name, long v)
    {
        var s = Smp;
        if (s == null) return false;
        switch (name.ToUpperInvariant())
        {
            case "A": s.A = (byte)v; return true;
            case "X": s.X = (byte)v; return true;
            case "Y": s.Y = (byte)v; return true;
            case "S": case "SP": s.SP = (byte)v; return true;
            case "P": case "PSW": s.PSW = (byte)v; return true;
            case "PC": s.PC = (ushort)v; return true;
        }
        return false;
    }

    public override byte Peek(uint address)
    {
        var a = apu;
        if (a == null) return 0;
        ushort x = (ushort)address;
        return x >= 0xF0 && x <= 0xFF ? a.Aram[x] : a.PeekCode(x);
    }

    public override bool Poke(uint address, byte value) { if (apu == null) return false; apu.Aram[(ushort)address] = value; return true; }

    public override int AccessKeys(uint address, bool write, Span<SpaceKey> keys) { keys[0] = new SpaceKey(0, address & 0xFFFF); return 1; }
    public override SpaceKey CpuSpecKey(uint address) => new(0, address & 0xFFFF);

    public override bool BindSymbol(DebugSymbol sym, out SpaceKey key)
    {
        if (sym.Domain == "spc") { key = new SpaceKey(0, sym.Address & 0xFFFF); return true; }
        key = default;
        return false;
    }

    public override uint StackAfterPush(uint sp, int bytes) => 0x100u | (byte)(sp - bytes);
    public override bool StackAbove(uint sp, uint reference) => (byte)sp > (byte)reference;

    public override void FillTrace(ref TraceRecord r)
    {
        var s = Smp;
        if (s == null) return;
        r.R0 = s.A; r.R1 = s.X; r.R2 = s.Y; r.R3 = s.SP; r.P = s.PSW;
        uint bytes = FetchBytes(r.Pc);
        r.Bytes = bytes;
        r.Length = (byte)DisasmSpc700.Length((byte)bytes);
        if (r.Kind == 0)
        {
            Span<byte> b = stackalloc byte[3];
            b[0] = (byte)bytes; b[1] = (byte)(bytes >> 8); b[2] = (byte)(bytes >> 16);
            var acc = traceAcc;
            int n = DisasmSpc700.Accesses(b, s.A, s.X, s.Y, s.SP, (s.PSW & 0x20) != 0, a => Peek(a), acc);
            if (n > 0) { r.Ea = acc[n - 1].Item1; r.EaValue = Peek(acc[n - 1].Item1); }
        }
    }

    public override string FormatRegisters(in TraceRecord r) =>
        $"A:{r.R0:X2} X:{r.R1:X2} Y:{r.R2:X2} S:{r.R3:X2} P:{NesCpu.Flags(r.P, "NVPBHIZC")}";
}
