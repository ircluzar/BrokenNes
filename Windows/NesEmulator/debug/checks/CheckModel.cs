using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NesEmulator.Debugging.Checks;

/// <summary>One rule of the hardware-check catalogue (docs/projects/brokendev-debugging-plan.md, "D4 settled").</summary>
public sealed record RuleInfo(string Id, string Console, string Title, string Why, bool MemoryRule);

/// <summary>The catalogue: every rule the checks know, by console ("nes", "snes" (also the WideMess), "gb" (also the Color)).</summary>
public static class CheckCatalogue
{
    public static readonly RuleInfo[] All =
    {
        // ---- NES
        new("nes.ram-uninit", "nes", "work RAM read before it was written", "real RAM powers on with random contents", true),
        new("nes.ppu-render-write", "nes", "picture-chip access during rendering that corrupts ($2007, $2003, $2004)", "the PPU's address and OAM counters are in use while it renders", false),
        new("nes.oam-dma-render", "nes", "OAM DMA outside vblank (while rendering, or running into the pre-render line)", "the PPU reads OAM while it renders", false),
        new("nes.frame-irq", "nes", "the APU frame-counter IRQ taken and never acknowledged", "the IRQ stays raised and fires again at once (the SMB3 red clouds)", false),
        new("nes.dmc-conflict", "nes", "a controller or $2007 read while a DMC sample plays", "a DMC DMA landing on the read clocks the port twice", false),
        new("nes.unofficial-opcode", "nes", "an unofficial opcode executed", "unofficial opcodes differ between CPU revisions and clones", false),
        new("nes.open-bus", "nes", "a read of a write-only register or of an address nothing answers", "the value is whatever was last on the bus", true),
        new("nes.stack-wrap", "nes", "the stack pointer wrapped around page 1", "pushes overwrite the top of the stack", false),
        new("nes.stack-overflow", "nes", "the stack reached a page-1 byte the game uses as data", "the stack and the data overwrite each other", true),
        new("nes.lag-frame", "nes", "a lag frame: vblank arrived while the main program was still working", "the frame's work spills into the next one", false),
        new("nes.flash-write", "nes", "a mapper-30 flash write (a command to the self-flashing board)", "a stray flash command can erase or program the save sectors", false),
        // ---- SNES (and the WideMess)
        new("snes.wram-uninit", "snes", "work RAM read before it was written", "real RAM powers on with random contents", true),
        new("snes.vram-uninit", "snes", "VRAM read before it was written (a port, a DMA, or the picture)", "VRAM powers on with random contents", true),
        new("snes.cgram-uninit", "snes", "a palette colour read before it was written (a port, a DMA, or the picture)", "CGRAM powers on with random contents", true),
        new("snes.oam-uninit", "snes", "OAM read before it was written (a port, a DMA, or the picture's sprite scan)", "OAM powers on with random contents", true),
        new("snes.dma-hdma", "snes", "an HDMA transfer during a general DMA", "it freezes the first revision of the S-CPU (5A22 v1)", false),
        new("snes.hdma-midframe", "snes", "an HDMA channel switched on mid-picture", "the channel starts from stale table pointers", false),
        new("snes.ppu-write-active", "snes", "VRAM, CGRAM or OAM written during the picture without forced blank", "the write is dropped, or lands where the PPU is reading", false),
        new("snes.dma-overrun", "snes", "a DMA started in vblank ran into the picture", "the bytes written after vblank ends are dropped", false),
        new("snes.muldiv-early", "snes", "a multiply or divide result read too early", "the hardware needs 8 (multiply) or 16 (divide) CPU cycles", false),
        new("snes.spc-handshake", "snes", "an SPC700 handshake that never answered", "the game hangs waiting for the sound CPU", false),
        new("snes.echo-overrun", "snes", "code, data or samples inside the active echo buffer", "the DSP's echo writes overwrite them continuously", true),
        new("snes.stack-overflow", "snes", "the stack reached bytes the game uses as data", "the stack and the data overwrite each other", true),
        new("snes.open-bus", "snes", "a read of a write-only register or of an address nothing answers", "the value is whatever was last on the bus", true),
        new("snes.lag-frame", "snes", "a lag frame: vblank arrived while the main program was still working", "the frame's work spills into the next one", false),
        // ---- Game Boy (and the Color)
        new("gb.ram-uninit", "gb", "work RAM or HRAM read before it was written, or OAM unwritten when sprites show", "real RAM powers on with random contents", true),
        new("gb.vram-mode3", "gb", "VRAM written or read in mode 3", "the PPU owns VRAM while it draws: writes are dropped, reads give $FF", true),
        new("gb.oam-mode23", "gb", "OAM written or read in modes 2-3 or during OAM DMA", "the PPU owns OAM while it scans and draws", true),
        new("gb.oam-dma-hram", "gb", "memory outside HRAM used during OAM DMA", "the DMA owns the bus: the CPU reads the DMA's bytes", false),
        new("gb.lcd-off", "gb", "the LCD switched off outside vblank", "it can damage an original Game Boy's screen", false),
        new("gb.unusable", "gb", "the unusable area $FEA0-$FEFF used", "it behaves differently on every model (and corrupts OAM on some)", true),
        new("gb.cgb-palette-mode3", "gb", "Color palette data written or read in mode 3", "the write is dropped, the read gives $FF", false),
        new("gb.speed-switch", "gb", "a double-speed switch without its STOP sequence", "KEY1 only switches on STOP, which needs IE = $00 and JOYP = $30", false),
        new("gb.cart-ram-disabled", "gb", "cartridge RAM used while disabled (or absent)", "writes are dropped, reads give $FF", true),
        new("gb.halt-bug", "gb", "HALT with IME clear and an interrupt pending", "the next byte is read twice (the HALT bug)", false),
        new("gb.stack-overflow", "gb", "the stack reached bytes the game uses as data", "the stack and the data overwrite each other", true),
        new("gb.lag-frame", "gb", "a lag frame: vblank arrived while the main program was still working", "the frame's work spills into the next one", false),
    };

    public static RuleInfo? Find(string id) => All.FirstOrDefault(r => r.Id == id);
    public static IEnumerable<RuleInfo> For(string console) => All.Where(r => r.Console == console);

    /// <summary>The rule family a debug machine's console uses ("widemess" -> "snes", "gbc" -> "gb").</summary>
    public static string FamilyOf(string machineConsole) => machineConsole switch { "widemess" => "snes", "gbc" => "gb", _ => machineConsole };
}

/// <summary>
/// One finding: a rule broken at one place (an address for memory rules, the function the PC was in for code rules), with the first frame,
/// PC and symbol, and how many times it happened. Findings with the same rule and place are one finding.
/// </summary>
public sealed class Finding
{
    public string Rule { get; init; } = "";
    /// <summary>Where: the symbol (a variable for memory rules, a function for code rules) or the address when no symbol names it.</summary>
    public string Place { get; init; } = "";
    /// <summary>The address in the memory it reached ("wram:$1F02"), for memory rules; "" otherwise.</summary>
    public string Address { get; init; } = "";
    public long FirstFrame { get; init; }
    public int FirstScanline { get; init; }
    public uint Pc { get; init; }
    public string? PcSymbol { get; init; }
    /// <summary>The call stack at the first occurrence, outermost first (function names).</summary>
    public string CallStack { get; init; } = "";
    public long Count { get; internal set; }
    public string Detail { get; internal set; } = "";
    /// <summary>The verify scenario it was found in (set by whoever runs the checks).</summary>
    public string Scenario { get; set; } = "";
    /// <summary>The accepted entry that covers it (its reason), or null: a new finding.</summary>
    public string? AcceptedReason { get; set; }
    public string Key => Rule + "|" + Place;
    public override string ToString() =>
        $"{Rule} at {Place}{(Address.Length > 0 && Address != Place ? $" ({Address})" : "")}: {Count}x, first frame {FirstFrame} line {FirstScanline}, pc ${Pc:X4}{(PcSymbol != null ? $" ({PcSymbol})" : "")}{(Detail.Length > 0 ? " - " + Detail : "")}";
}

/// <summary>A finding a game accepts (its manifest's checks.accepted): the rule, where (a glob over the place or the address), and why.</summary>
public sealed class AcceptedFinding
{
    /// <summary>The rule a failed debug-port assert is reported as (D7). Not in the catalogue and never acceptable.</summary>
    public const string PortAssertRule = "port.assert";

    public string Rule { get; init; } = "";
    public string At { get; init; } = "*";
    public string Reason { get; init; } = "";
    public long Matched { get; set; }

    private Regex? re;
    public bool Matches(Finding f)
    {
        if (f.Rule.StartsWith(PortAssertRule, StringComparison.Ordinal)) return false;   // a debug-port assert is the game's own test: never accepted
        if (!Glob(Rule).IsMatch(f.Rule)) return false;
        re ??= Glob(At);
        return re.IsMatch(f.Place) || (f.Address.Length > 0 && re.IsMatch(f.Address)) || (f.PcSymbol != null && re.IsMatch(BaseName(f.PcSymbol)));
    }

    public static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"name+$12" -> "name".</summary>
    public static string BaseName(string symbol) { int p = symbol.IndexOf("+$", StringComparison.Ordinal); return p > 0 ? symbol[..p] : symbol; }
}

/// <summary>What a check run is told (from the manifest's checks section).</summary>
public sealed class CheckOptions
{
    /// <summary>Rules switched off for this game (an opt-out, with its reason in the manifest).</summary>
    public HashSet<string> Off { get; } = new(StringComparer.Ordinal);
    /// <summary>Symbols (functions) where the main program waits for the next frame, in addition to the spin / WAI / HALT detection.</summary>
    public List<string> FrameWait { get; } = new();
    /// <summary>Frames a SNES handshake may go unanswered before it is a finding (60 = one second).</summary>
    public int HandshakeFrames { get; set; } = 60;
    /// <summary>Collect the profile (cycles per frame and per function); the checks always run.</summary>
    public bool Profile { get; set; } = true;
    public bool IsOn(string rule) => !Off.Contains(rule);
    /// <summary>Listen to the debug port (D7) during the run: its asserts become <c>port.assert</c> findings, its timers go into the profile.</summary>
    public bool DebugPort { get; set; } = true;
}
