using System;
using System.Collections.Generic;
using System.Text;
using NesEmulator.Corruption;
using NesEmulator.Gb;
using NesEmulator.Systems;

namespace NesEmulator.Debugging;

/// <summary>The Game Boy / Game Boy Color for the debugger: the SM83 with echo RAM folded onto work RAM, ROM, cartridge RAM, work RAM and
/// VRAM by their banks, OAM, HRAM and the I/O registers; OAM DMA and the Color's VRAM DMA bytes as accesses.</summary>
public sealed class GbDebugMachine : DebugMachine, IGbInstructionHook, IGbBusDebugTap
{
    private readonly GbSession session;
    private readonly BOARD_GB board;
    private readonly Sm83 cpu;
    private readonly DebugCpu[] cpus;

    public GbDebugMachine(GbSession session)
    {
        this.session = session;
        board = session.Board;
        if (board.Core is not CPU_GB) throw new NotSupportedException($"the Game Boy debugger needs the SM83 (CPU_GB); this session runs {board.Core.Id}");
        cpu = new Sm83(this, board);
        cpus = new DebugCpu[] { cpu };
    }

    public BOARD_GB Board => board;
    public override string Console => session.Console == ConsoleKind.GameBoyColor ? "gbc" : "gb";
    public override IReadOnlyList<DebugCpu> Cpus => cpus;
    public override long Frame => board.Ppu.FrameCount;
    public override int Scanline => board.Ppu.ReadRegister(0x44);
    public override int Dot => board.Ppu.Dot;
    public override long Cycle => board.CycleCount;

    public override void ApplyHooks(bool[] exec, bool[] memory)
    {
        board.DebugHook = exec[0] ? this : null;
        board.DebugTap = memory[0] ? this : null;
    }

    void IGbInstructionHook.BeforeInstruction() => Debugger?.OnInstruction(cpu, board.CycleCount);

    void IGbBusDebugTap.OnRead(ushort address, byte value, GbBusSource source) =>
        Debugger?.OnAccess(cpu, address, value, false, source == GbBusSource.Cpu ? AccessSource.Cpu : source == GbBusSource.OamDma ? AccessSource.OamDma : AccessSource.VramDma, -1);

    void IGbBusDebugTap.OnWrite(ushort address, byte value, GbBusSource source) =>
        Debugger?.OnAccess(cpu, address, value, true, source == GbBusSource.Cpu ? AccessSource.Cpu : source == GbBusSource.OamDma ? AccessSource.OamDma : AccessSource.VramDma, -1);

    public override IReadOnlyList<IMemoryDomain> Domains()
    {
        var list = new List<IMemoryDomain>(MachineDomains.For(session));
        list.Add(new FuncDomain("IO", "I/O registers $FF00-$FF7F, then IE ($FFFF) at offset $80, as the CPU reads them", DomainKind.Registers, 0x81,
            a => a == 0x80 ? board.DebugIe : board.Peek((ushort)(0xFF00 + a)), (a, v) => { }, writable: false));
        list.Add(new FuncDomain("BUS", "The CPU's address space ($0000-$FFFF), read without side effects", DomainKind.Registers, 0x10000, a => cpu.Peek((uint)a), (a, v) => cpu.Poke((uint)a, v)));
        return list;
    }
}

/// <summary>The SM83 for the debugger.</summary>
internal sealed class Sm83 : DebugCpu
{
    public const int SpaceCpu = 0, SpaceRom = 1, SpaceWram = 2, SpaceVram = 3, SpaceSram = 4, SpaceOam = 5, SpaceHram = 6, SpaceIo = 7;
    private readonly BOARD_GB board;
    private readonly AddressSpace[] spaces;
    private CPU_GB Cpu => (CPU_GB)board.Core;

    public Sm83(DebugMachine m, BOARD_GB board) : base(m)
    {
        this.board = board;
        spaces = new[]
        {
            new AddressSpace { Id = SpaceCpu, Name = "cpu", Size = 0x10000, Aliases = new[] { "bus" }, Description = "CPU addresses (echo RAM $E000-$FDFF folded onto $C000-$DDFF)" },
            new AddressSpace { Id = SpaceRom, Name = "rom", Size = (uint)Math.Max(1, board.Cart.Rom.Length), Aliases = new[] { "prg" }, Description = "ROM by offset (bank x $4000 + address - $4000 for $4000-$7FFF)" },
            new AddressSpace { Id = SpaceWram, Name = "wram", Size = (uint)board.Wram.Length, Aliases = new[] { "ram" }, Description = "work RAM by offset (bank x $1000 + address - $D000 for $D000-$DFFF)" },
            new AddressSpace { Id = SpaceVram, Name = "vram", Size = (uint)board.Ppu.Vram.Length, Description = "video RAM by offset (bank x $2000 + address - $8000)" },
            new AddressSpace { Id = SpaceSram, Name = "sram", Size = (uint)Math.Max(1, board.Cart.Ram.Length), Aliases = new[] { "cartram" }, Description = "cartridge RAM by offset" },
            new AddressSpace { Id = SpaceOam, Name = "oam", Size = 0xA0, Description = "sprite RAM ($FE00-$FE9F)" },
            new AddressSpace { Id = SpaceHram, Name = "hram", Size = 0x7F, Description = "high RAM ($FF80-$FFFE)" },
            new AddressSpace { Id = SpaceIo, Name = "io", Size = 0x81, Aliases = new[] { "regs" }, Description = "I/O registers ($FF00-$FF7F at 0-$7F, IE at $80)" },
        };
    }

    public override string Id => "main";
    public override string Name => "SM83";
    public override IDebugDisassembler Disassembler => DisasmSm83.Instance;
    public override IReadOnlyList<AddressSpace> Spaces => spaces;
    public override uint PC => Cpu.PC;
    public override uint SP => Cpu.SP;

    private int VramBank => board.Model == GbModel.Cgb ? board.Ppu.ReadRegister(0x4F) & 1 : 0;

    public override bool WillExecute(out string? interrupt)
    {
        interrupt = null;
        var c = Cpu;
        if (c.Halted || c.Stopped || c.Locked) return false;
        byte pending = board.PendingInterrupts;
        if (c.Ime && pending != 0)
        {
            int bit = System.Numerics.BitOperations.TrailingZeroCount(pending);
            interrupt = bit switch { 0 => "VBLANK", 1 => "STAT", 2 => "TIMER", 3 => "SERIAL", _ => "JOYPAD" };
            return false;
        }
        return true;
    }

    private static readonly string[] Regs = { "A", "F", "B", "C", "D", "E", "H", "L", "AF", "BC", "DE", "HL", "SP", "S", "PC", "IME" };
    public override IReadOnlyList<string> RegisterNames => Regs;

    public override bool TryGetRegister(string name, out long v)
    {
        var c = Cpu;
        v = name.ToUpperInvariant() switch
        {
            "A" => c.A, "F" => c.F, "B" => c.B, "C" => c.C, "D" => c.D, "E" => c.E, "H" => c.H, "L" => c.L,
            "AF" => c.AF, "BC" => c.BC, "DE" => c.DE, "HL" => c.HL, "SP" or "S" => c.SP, "PC" => c.PC, "IME" => c.Ime ? 1 : 0, _ => -1,
        };
        return v >= 0;
    }

    public override bool TrySetRegister(string name, long v)
    {
        var c = Cpu;
        switch (name.ToUpperInvariant())
        {
            case "A": c.A = (byte)v; return true;
            case "F": c.F = (byte)(v & 0xF0); return true;
            case "B": c.B = (byte)v; return true;
            case "C": c.C = (byte)v; return true;
            case "D": c.D = (byte)v; return true;
            case "E": c.E = (byte)v; return true;
            case "H": c.H = (byte)v; return true;
            case "L": c.L = (byte)v; return true;
            case "AF": c.AF = (ushort)v; return true;
            case "BC": c.BC = (ushort)v; return true;
            case "DE": c.DE = (ushort)v; return true;
            case "HL": c.HL = (ushort)v; return true;
            case "SP": case "S": c.SP = (ushort)v; return true;
            case "PC": c.PC = (ushort)v; return true;
        }
        return false;
    }

    private static uint Fold(uint a) { a &= 0xFFFF; return a >= 0xE000 && a < 0xFE00 ? a - 0x2000 : a; }

    /// <summary>The memory a CPU address reaches now (banks as they are mapped), or (cpu, address) for the unusable area.</summary>
    private SpaceKey Phys(uint address)
    {
        uint a = Fold(address);
        if (a < 0x8000) { int i = board.Cart.DebugRomIndex((ushort)a); return i >= 0 ? new SpaceKey(SpaceRom, (uint)i) : new SpaceKey(SpaceCpu, a); }
        if (a < 0xA000) return new SpaceKey(SpaceVram, (uint)(VramBank << 13) | (a - 0x8000));
        if (a < 0xC000) { int i = board.Cart.DebugRamIndex((ushort)a); return i >= 0 ? new SpaceKey(SpaceSram, (uint)i) : new SpaceKey(SpaceCpu, a); }
        if (a < 0xD000) return new SpaceKey(SpaceWram, a - 0xC000);
        if (a < 0xE000) return new SpaceKey(SpaceWram, (uint)(board.DebugWramBank << 12) | (a - 0xD000));
        if (a >= 0xFE00 && a < 0xFEA0) return new SpaceKey(SpaceOam, a - 0xFE00);
        if (a >= 0xFF80 && a < 0xFFFF) return new SpaceKey(SpaceHram, a - 0xFF80);
        if (a >= 0xFF00 && a < 0xFF80) return new SpaceKey(SpaceIo, a - 0xFF00);
        if (a == 0xFFFF) return new SpaceKey(SpaceIo, 0x80);
        return new SpaceKey(SpaceCpu, a);
    }

    public override byte Peek(uint address) => board.Peek((ushort)address);

    public override bool Poke(uint address, byte value)
    {
        var k = Phys(address);
        return k.Space != SpaceCpu && k.Space != SpaceIo && PokeSpace(k.Space, k.Offset, value);
    }

    public override byte PeekSpace(int space, uint o) => space switch
    {
        SpaceRom => o < (uint)board.Cart.Rom.Length ? board.Cart.Rom[o] : (byte)0,
        SpaceWram => o < (uint)board.Wram.Length ? board.Wram[o] : (byte)0,
        SpaceVram => o < (uint)board.Ppu.Vram.Length ? board.Ppu.Vram[o] : (byte)0,
        SpaceSram => o < (uint)board.Cart.Ram.Length ? board.Cart.Ram[o] : (byte)0,
        SpaceOam => o < 0xA0 ? board.Ppu.Oam[o] : (byte)0,
        SpaceHram => o < 0x7F ? board.Hram[o] : (byte)0,
        SpaceIo => o == 0x80 ? board.DebugIe : board.Peek((ushort)(0xFF00 + (o & 0x7F))),
        _ => Peek(o),
    };

    public override bool PokeSpace(int space, uint o, byte v)
    {
        switch (space)
        {
            case SpaceRom: if (o >= (uint)board.Cart.Rom.Length) return false; board.Cart.Rom[o] = v; return true;
            case SpaceWram: if (o >= (uint)board.Wram.Length) return false; board.Wram[o] = v; return true;
            case SpaceVram: if (o >= (uint)board.Ppu.Vram.Length) return false; board.Ppu.Vram[o] = v; return true;
            case SpaceSram: if (o >= (uint)board.Cart.Ram.Length) return false; board.Cart.Ram[o] = v; return true;
            case SpaceOam: if (o >= 0xA0) return false; board.Ppu.Oam[o] = v; return true;
            case SpaceHram: if (o >= 0x7F) return false; board.Hram[o] = v; return true;
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

    public override bool TryBankedSpec(uint bank, uint address, out SpaceKey key)
    {
        address &= 0xFFFF;
        if (address < 0x4000) { key = new SpaceKey(SpaceRom, address); return true; }
        if (address < 0x8000) { key = new SpaceKey(SpaceRom, bank * 0x4000 + address - 0x4000); return true; }
        if (address < 0xA000) { key = new SpaceKey(SpaceVram, (bank & 1) * 0x2000 + address - 0x8000); return true; }
        if (address < 0xC000) { key = new SpaceKey(SpaceSram, bank * 0x2000 + address - 0xA000); return true; }
        if (address >= 0xD000 && address < 0xE000) { key = new SpaceKey(SpaceWram, Math.Max(1, bank) * 0x1000 + address - 0xD000); return true; }
        key = Phys(address);
        if (key.Space == SpaceCpu) key = new SpaceKey(SpaceCpu, Fold(address));
        return true;
    }

    public override bool BindSymbol(DebugSymbol sym, out SpaceKey key)
    {
        uint a = sym.Address;
        switch (sym.Domain)
        {
            case "cpu":
                // a bank names ROM, VRAM, cartridge RAM or work RAM banks; bank 0 is the fixed ROM below $4000, and unbanked elsewhere
                if (sym.Bank > 0 || (a < 0x4000)) return TryBankedSpec((uint)Math.Max(0, sym.Bank), a, out key);
                key = new SpaceKey(SpaceCpu, Fold(a));
                return true;
            case "rom": key = new SpaceKey(SpaceRom, a); return true;
            case "wram": key = new SpaceKey(SpaceWram, a); return true;
            case "sram": key = new SpaceKey(SpaceSram, a); return true;
            case "hram": key = new SpaceKey(SpaceHram, a >= 0xFF80 ? a - 0xFF80 : a); return true;
            case "io": key = new SpaceKey(SpaceIo, a >= 0xFF00 ? (a == 0xFFFF ? 0x80u : a - 0xFF00) : a); return true;
        }
        key = default;
        return false;
    }

    public override uint StackAfterPush(uint sp, int bytes) => (ushort)(sp - bytes);
    public override bool StackAbove(uint sp, uint reference) => (ushort)sp > (ushort)reference;

    public override void FillTrace(ref TraceRecord r)
    {
        var c = Cpu;
        r.R0 = c.AF; r.R1 = c.BC; r.R2 = c.DE; r.R3 = c.HL; r.R4 = c.SP; r.P = c.F; r.R6 = (byte)(c.Ime ? 1 : 0);
        uint bytes = FetchBytes(r.Pc);
        r.Bytes = bytes;
        r.Length = (byte)Disassembler.Classify((byte)bytes, (byte)(bytes >> 8), default).Length;
        if ((byte)bytes == 0xCB) r.Length = 2;
        if (r.Kind == 0)
        {
            Span<byte> b = stackalloc byte[3];
            b[0] = (byte)bytes; b[1] = (byte)(bytes >> 8); b[2] = (byte)(bytes >> 16);
            int ea = DisasmSm83.EffectiveAddress(b[..Math.Min(3, (int)r.Length)], c.B, c.C, c.D, c.E, c.H, c.L, c.SP);
            r.Ea = ea;
            if (ea >= 0) r.EaValue = Peek((uint)ea);
        }
    }

    public override string FormatRegisters(in TraceRecord r)
    {
        var sb = new StringBuilder(64);
        sb.Append("A:").Append((r.R0 >> 8).ToString("X2")).Append(" B:").Append((r.R1 >> 8).ToString("X2")).Append(" C:").Append((r.R1 & 0xFF).ToString("X2"))
          .Append(" D:").Append((r.R2 >> 8).ToString("X2")).Append(" E:").Append((r.R2 & 0xFF).ToString("X2")).Append(" F:").Append(NesCpu.Flags(r.P, "ZNHC----")[..4])
          .Append(" H:").Append((r.R3 >> 8).ToString("X2")).Append(" L:").Append((r.R3 & 0xFF).ToString("X2")).Append(" S:").Append(r.R4.ToString("X4"))
          .Append((r.R6 & 1) != 0 ? " IME" : "");
        return sb.ToString();
    }
}
