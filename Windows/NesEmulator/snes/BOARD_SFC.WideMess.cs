using System;
using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The WideMess's CPU and memory (docs/projects/widemess-spec.md section 2, milestone 4), on the SNES board:
/// <list type="bullet">
/// <item><b>Fast mode</b> (WMSPEED bit 0): every CPU access and internal cycle takes 2 master clocks, except the I/O registers ($2100-$21FF, $4000-$43FF), which keep their SNES timing.
/// The page table's speeds follow (it is rebuilt when the mode changes, as for MEMSEL), the decoding path asks <see cref="AccessClocks"/>, and <see cref="Idle"/> charges <c>idleClocks</c>.</item>
/// <item><b>The 1 MB of work RAM</b> on a cartridge with the WideMess map: banks $70-$7D are <see cref="WramHigh"/> (work-RAM addresses $20000-$FFFFF; $7E-$7F are the SNES's 128 KB,
/// addresses $00000-$1FFFF), and once unlocked the WRAM port takes a 20-bit address. Other cartridges keep the SNES's 128 KB and its map.</item>
/// <item><b>Fast DMA</b> (WMDMA bit 0): general DMA moves a byte every 4 master clocks instead of 8.</item>
/// </list>
/// On the SNES console (no <see cref="WideMess"/>) and on a locked WideMess every one of these stays at its SNES value: <c>fastCpu</c> false, <c>idleClocks</c> 6, <c>dmaByteClocks</c> 8,
/// <c>wramPortMask</c> $1FFFF, <c>wramHigh</c> null unless the cartridge has the WideMess map (which only a .wms game can have).
/// </summary>
public sealed partial class BOARD_SFC
{
    /// <summary>Work RAM above the SNES's 128 KB on the WideMess map: work-RAM addresses $20000-$FFFFF, i.e. banks $70-$7D.</summary>
    public const int WramHighBytes = 0xE0000;

    private WmsExtension? wideMess;
    /// <summary>Banks $70-$7D when the cartridge has the WideMess map (index = (bank - $70) x 64 KB + offset); null otherwise.</summary>
    private readonly byte[]? wramHigh;

    // The modes the WideMess registers select (SNES values while locked or on a SNES).
    private bool fastCpu;
    private int idleClocks = 6, dmaByteClocks = 8;
    private uint wramPortMask = 0x1FFFF;

    /// <summary>The WideMess map's work RAM above the SNES's 128 KB (banks $70-$7D, 896 KB), or null when the cartridge does not have the WideMess map.</summary>
    public byte[]? WramHigh => wramHigh;
    /// <summary>True while WMSPEED's fast mode is on (2 master clocks per CPU cycle outside the I/O registers).</summary>
    public bool FastCpu => fastCpu;
    /// <summary>Master clocks per byte of general DMA: 8, or 4 with WMDMA's fast DMA.</summary>
    public int DmaByteClocks => dmaByteClocks;

    /// <summary>The WideMess's own registers (docs/projects/widemess-spec.md), on a WideMess console only; null on a SNES. It is consulted only for addresses the SNES leaves unused
    /// ($4220-$423F, B-bus $21A0-$21FF), and while it is locked those accesses go where they go on a SNES. Its modes (speed, DMA, the 20-bit WRAM port) reach the board through
    /// <see cref="WmsExtension.Changed"/>.</summary>
    public WmsExtension? WideMess
    {
        get => wideMess;
        set
        {
            if (wideMess != null) wideMess.Changed = null;
            wideMess = value;
            if (value != null) value.Changed = ApplyWideMess;
            ApplyWideMess();
        }
    }

    private bool consoleFastCpu;
    /// <summary>Milestone 6: the console's fast CPU for a locked SNES game (docs/projects/widemess-spec.md section 6.1): fast mode as WMSPEED gives it, with the game unaware (WMSPEED still reads
    /// what the game wrote). Only on a WideMess console; the session switches it off when the game unlocks the extensions.</summary>
    public bool ConsoleFastCpu
    {
        get => consoleFastCpu;
        set { consoleFastCpu = value && wideMess != null; ApplyWideMess(); }
    }

    /// <summary>The board's view of the WideMess registers, after any change (the unlock, WMSPEED, WMDMA, a reset, a loaded state).</summary>
    private void ApplyWideMess()
    {
        var wm = wideMess;
        bool fast = wm is { FastCpu: true } || (consoleFastCpu && wm != null);
        idleClocks = fast ? 2 : 6;
        dmaByteClocks = wm is { FastDma: true } ? 4 : 8;
        // the 20-bit WRAM port: the WideMess map's 1 MB, once unlocked; otherwise the SNES's 17 bits (and an address left from an unlocked time drops to them)
        wramPortMask = wramHigh != null && wm is { Unlocked: true } ? 0xFFFFFu : 0x1FFFFu;
        wramPortAddress &= wramPortMask;
        if (fast != fastCpu) { fastCpu = fast; BuildPageTable(); }
    }

    /// <summary>A CPU access's master clocks in fast mode: 2, except the I/O registers, which keep the SNES's (B-bus $2100-$21FF 6, the old joypad ports $4000-$41FF 12, $4200-$43FF 6).</summary>
    private static int FastAccessClocks(uint bank, uint offset)
    {
        if ((bank & 0x40) == 0 && offset >= 0x2100 && offset < 0x4400)
        {
            if (offset < 0x2200) return 6;
            if (offset >= 0x4000) return offset < 0x4200 ? 12 : 6;
        }
        return 2;
    }

    /// <summary>True for banks $70-$7D (the WideMess map's work RAM above the SNES's 128 KB).</summary>
    private static bool IsWramHighBank(uint bank) => bank - 0x70u < 0x0Eu;

    // ---- the WRAM port ($2180-$2183) over the whole work RAM: addresses $00000-$1FFFF are the SNES's 128 KB, $20000-$FFFFF the WideMess map's banks $70-$7D

    private byte ReadWramPort()
    {
        uint a = wramPortAddress;
        wramPortAddress = (a + 1) & wramPortMask;
        return a < 0x20000 ? Wram[a] : wramHigh![a - 0x20000];
    }

    private void WriteWramPort(byte value)
    {
        uint a = wramPortAddress;
        wramPortAddress = (a + 1) & wramPortMask;
        if (a < 0x20000) Wram[a] = value; else wramHigh![a - 0x20000] = value;
    }

    // ---- state: the WideMess part of a WideMess console's payload (the SNES part, the board's own, is unchanged; the modes are the extension's and come back through ApplyWideMess)

    private const byte WideMemoryStateVersion = 1;

    /// <summary>The WideMess map's work RAM (the WRAM port address is in the board's own state).</summary>
    public void SaveWideMemory(BinaryWriter w)
    {
        SnesStateIo.Begin(w, WideMemoryStateVersion);
        w.Write(wramHigh?.Length ?? 0);
        if (wramHigh != null) SnesStateIo.Put(w, wramHigh);
    }

    public void LoadWideMemory(BinaryReader r)
    {
        SnesStateIo.Expect(r, WideMemoryStateVersion, "WideMess work RAM");
        SnesStateIo.ExpectLength(r, wramHigh?.Length ?? 0, "WideMess work RAM");
        if (wramHigh != null) SnesStateIo.Get(r, wramHigh);
    }
}
