using System.Collections.Generic;
using System.Linq;

namespace BrokenNes.Workshop;

/// <summary>One entry from AccuracyCoin's test table (its own comment: "table 'Name of test',
/// $FF, Address_To_Store_Test_Results, Address_To_Jump_To_In_Order_To_Run_The_Test").</summary>
internal sealed record AccuracyCoinTest(string Suite, string Name, ushort ResultAddress);

/// <summary>
/// The 141 real pass/fail tests in AccuracyCoin (100thCoin/AccuracyCoin, main branch,
/// commit 0e10453 "Fixed an edge case in $2004 Stress Test, Typos"), extracted programmatically
/// from AccuracyCoin.asm rather than hand-transcribed - every "table ..." macro invocation under
/// TestPages:, minus the 5 whose result address is result_DrawTest ($03FF). Those 5 (all under
/// "Power On State") are visual-only per DrawTEST's own logic: it checks whether the result
/// address's high byte is $03 and renders "DRAW" instead of PASS/FAIL/SKIP, so they carry no
/// pass/fail semantics and are intentionally excluded here.
///
/// Regenerate with Workshop's extract_tests.py-equivalent logic if AccuracyCoin.asm is updated -
/// the table/address layout is stable across the "125/129/140/141"-test versions noted during
/// phase-1 research, but re-verify the count if pinning a different commit.
/// </summary>
internal static class AccuracyCoinTests
{
    // Boot-time addresses (Windows/NesEmulator/board/... via NES.PeekSystemRam/PeekCpu):
    // menuCursorYPos=$16 (starts at $FF = "top of menu" per RESET:), RunningAllTests=$35
    // (1 while the automated run is in progress, 0 when idle/complete), PostAllTestTally=$37
    // (running count of tests executed so far during an automated run, resets to 0 at start).
    public const ushort MenuCursorYPos = 0x16;
    public const ushort RunningAllTests = 0x35;
    public const ushort PostAllTestTally = 0x37;

    public static readonly IReadOnlyList<AccuracyCoinTest> All = new List<AccuracyCoinTest>
    {
        new("CPU Behavior", "ROM is not writable", 0x0405),
        new("CPU Behavior", "RAM Mirroring", 0x0403),
        new("CPU Behavior", "PC Wraparound", 0x044D),
        new("CPU Behavior", "The Decimal Flag", 0x0474),
        new("CPU Behavior", "The B Flag", 0x0475),
        new("CPU Behavior", "Dummy read cycles", 0x0406),
        new("CPU Behavior", "Dummy write cycles", 0x0407),
        new("CPU Behavior", "Open Bus", 0x0408),
        new("CPU Behavior", "All NOP instructions", 0x047D),
        new("Addressing mode wraparound", "Absolute Indexed", 0x046E),
        new("Addressing mode wraparound", "Zero Page Indexed", 0x046F),
        new("Addressing mode wraparound", "Indirect", 0x0470),
        new("Addressing mode wraparound", "Indirect, X", 0x0471),
        new("Addressing mode wraparound", "Indirect, Y", 0x0472),
        new("Addressing mode wraparound", "Relative", 0x0473),
        new("Unofficial Instructions: SLO", "$03   SLO indirect,X", 0x0409),
        new("Unofficial Instructions: SLO", "$07   SLO zeropage", 0x040A),
        new("Unofficial Instructions: SLO", "$0F   SLO absolute", 0x040B),
        new("Unofficial Instructions: SLO", "$13   SLO indirect,Y", 0x040C),
        new("Unofficial Instructions: SLO", "$17   SLO zeropage,X", 0x040D),
        new("Unofficial Instructions: SLO", "$1B   SLO absolute,Y", 0x040E),
        new("Unofficial Instructions: SLO", "$1F   SLO absolute,X", 0x040F),
        new("Unofficial Instructions: RLA", "$23   RLA indirect,X", 0x0419),
        new("Unofficial Instructions: RLA", "$27   RLA zeropage", 0x041A),
        new("Unofficial Instructions: RLA", "$2F   RLA absolute", 0x041B),
        new("Unofficial Instructions: RLA", "$33   RLA indirect,Y", 0x041C),
        new("Unofficial Instructions: RLA", "$37   RLA zeropage,X", 0x041D),
        new("Unofficial Instructions: RLA", "$3B   RLA absolute,Y", 0x041E),
        new("Unofficial Instructions: RLA", "$3F   RLA absolute,X", 0x041F),
        new("Unofficial Instructions: SRE", "$43   SRE indirect,X", 0x0420),
        new("Unofficial Instructions: SRE", "$47   SRE zeropage", 0x047F),
        new("Unofficial Instructions: SRE", "$4F   SRE absolute", 0x0422),
        new("Unofficial Instructions: SRE", "$53   SRE indirect,Y", 0x0423),
        new("Unofficial Instructions: SRE", "$57   SRE zeropage,X", 0x0424),
        new("Unofficial Instructions: SRE", "$5B   SRE absolute,Y", 0x0425),
        new("Unofficial Instructions: SRE", "$5F   SRE absolute,X", 0x0426),
        new("Unofficial Instructions: RRA", "$63   RRA indirect,X", 0x0427),
        new("Unofficial Instructions: RRA", "$67   RRA zeropage", 0x0428),
        new("Unofficial Instructions: RRA", "$6F   RRA absolute", 0x0429),
        new("Unofficial Instructions: RRA", "$73   RRA indirect,Y", 0x042A),
        new("Unofficial Instructions: RRA", "$77   RRA zeropage,X", 0x042B),
        new("Unofficial Instructions: RRA", "$7B   RRA absolute,Y", 0x042C),
        new("Unofficial Instructions: RRA", "$7F   RRA absolute,X", 0x042D),
        new("Unofficial Instructions: *AX", "$83   SAX indirect,X", 0x042E),
        new("Unofficial Instructions: *AX", "$87   SAX zeropage", 0x042F),
        new("Unofficial Instructions: *AX", "$8F   SAX absolute", 0x0430),
        new("Unofficial Instructions: *AX", "$97   SAX zeropage,Y", 0x0431),
        new("Unofficial Instructions: *AX", "$A3   LAX indirect,X", 0x0432),
        new("Unofficial Instructions: *AX", "$A7   LAX zeropage", 0x0433),
        new("Unofficial Instructions: *AX", "$AF   LAX absolute", 0x0434),
        new("Unofficial Instructions: *AX", "$B3   LAX indirect,Y", 0x0435),
        new("Unofficial Instructions: *AX", "$B7   LAX zeropage,Y", 0x0436),
        new("Unofficial Instructions: *AX", "$BF   LAX absolute,Y", 0x0437),
        new("Unofficial Instructions: DCP", "$C3   DCP indirect,X", 0x0438),
        new("Unofficial Instructions: DCP", "$C7   DCP zeropage", 0x0439),
        new("Unofficial Instructions: DCP", "$CF   DCP absolute", 0x043A),
        new("Unofficial Instructions: DCP", "$D3   DCP indirect,Y", 0x043B),
        new("Unofficial Instructions: DCP", "$D7   DCP zeropage,X", 0x043C),
        new("Unofficial Instructions: DCP", "$DB   DCP absolute,Y", 0x043D),
        new("Unofficial Instructions: DCP", "$DF   DCP absolute,X", 0x043E),
        new("Unofficial Instructions: ISC", "$E3   ISC indirect,X", 0x043F),
        new("Unofficial Instructions: ISC", "$E7   ISC zeropage", 0x0440),
        new("Unofficial Instructions: ISC", "$EF   ISC absolute", 0x0441),
        new("Unofficial Instructions: ISC", "$F3   ISC indirect,Y", 0x0442),
        new("Unofficial Instructions: ISC", "$F7   ISC zeropage,X", 0x0443),
        new("Unofficial Instructions: ISC", "$FB   ISC absolute,Y", 0x0444),
        new("Unofficial Instructions: ISC", "$FF   ISC absolute,X", 0x0445),
        new("Unofficial Instructions: SH*", "$93   SHA indirect,Y", 0x0446),
        new("Unofficial Instructions: SH*", "$9F   SHA absolute,Y", 0x0447),
        new("Unofficial Instructions: SH*", "$9B   SHS absolute,Y", 0x0448),
        new("Unofficial Instructions: SH*", "$9C   SHY absolute,X", 0x0449),
        new("Unofficial Instructions: SH*", "$9E   SHX absolute,Y", 0x044A),
        new("Unofficial Instructions: SH*", "$BB   LAE absolute,Y", 0x044B),
        new("Unofficial Immediates", "$0B   ANC Immediate", 0x0410),
        new("Unofficial Immediates", "$2B   ANC Immediate", 0x0411),
        new("Unofficial Immediates", "$4B   ASR Immediate", 0x0412),
        new("Unofficial Immediates", "$6B   ARR Immediate", 0x0413),
        new("Unofficial Immediates", "$8B   ANE Immediate", 0x0414),
        new("Unofficial Immediates", "$AB   LXA Immediate", 0x0415),
        new("Unofficial Immediates", "$CB   AXS Immediate", 0x0416),
        new("Unofficial Immediates", "$EB   SBC Immediate", 0x0417),
        new("CPU Interrupts", "Interrupt flag latency", 0x0461),
        new("CPU Interrupts", "NMI Overlap BRK", 0x0462),
        new("CPU Interrupts", "NMI Overlap IRQ", 0x0463),
        new("APU Registers and DMA tests", "DMA + Open Bus", 0x046C),
        new("APU Registers and DMA tests", "DMA + $2002 Read", 0x0488),
        new("APU Registers and DMA tests", "DMA + $2007 Read", 0x044C),
        new("APU Registers and DMA tests", "DMA + $2007 Write", 0x044F),
        new("APU Registers and DMA tests", "DMA + $4015 Read", 0x045D),
        new("APU Registers and DMA tests", "DMA + $4016 Read", 0x045E),
        new("APU Registers and DMA tests", "DMC DMA Bus Conflicts", 0x046B),
        new("APU Registers and DMA tests", "DMC DMA + OAM DMA", 0x0477),
        new("APU Registers and DMA tests", "Explicit DMA Abort", 0x0479),
        new("APU Registers and DMA tests", "Implicit DMA Abort", 0x0478),
        new("APU Tests", "Length Counter", 0x0465),
        new("APU Tests", "Length Table", 0x0466),
        new("APU Tests", "Frame Counter IRQ", 0x0467),
        new("APU Tests", "Frame Counter 4-step", 0x0468),
        new("APU Tests", "Frame Counter 5-step", 0x0469),
        new("APU Tests", "Delta Modulation Channel", 0x046A),
        new("APU Tests", "APU Register Activation", 0x045C),
        new("APU Tests", "Controller Strobing", 0x045F),
        new("APU Tests", "Controller Clocking", 0x047A),
        new("PPU Behavior", "CHR ROM is not writable", 0x0485),
        new("PPU Behavior", "PPU Register Mirroring", 0x0404),
        new("PPU Behavior", "PPU Register Open Bus", 0x044E),
        new("PPU Behavior", "PPU Read Buffer", 0x0476),
        new("PPU Behavior", "Palette RAM Quirks", 0x047E),
        new("PPU Behavior", "Rendering Flag Behavior", 0x0486),
        new("PPU Behavior", "$2007 read w/ rendering", 0x048A),
        new("PPU Behavior", "Attributes As Tiles", 0x0481),
        new("PPU VBlank Timing", "VBlank beginning", 0x0450),
        new("PPU VBlank Timing", "VBlank end", 0x0451),
        new("PPU VBlank Timing", "NMI Control", 0x0452),
        new("PPU VBlank Timing", "NMI Timing", 0x0453),
        new("PPU VBlank Timing", "NMI Suppression", 0x0454),
        new("PPU VBlank Timing", "NMI at VBlank end", 0x0455),
        new("PPU VBlank Timing", "NMI disabled at VBlank", 0x0456),
        new("Sprite Evaluation", "Sprite overflow behavior", 0x0459),
        new("Sprite Evaluation", "Sprite 0 Hit behavior", 0x0457),
        new("Sprite Evaluation", "$2002 flag timing", 0x048D),
        new("Sprite Evaluation", "Suddenly Resize Sprite", 0x0489),
        new("Sprite Evaluation", "Arbitrary Sprite zero", 0x0458),
        new("Sprite Evaluation", "Misaligned OAM behavior", 0x045A),
        new("Sprite Evaluation", "Address $2004 behavior", 0x045B),
        new("Sprite Evaluation", "OAM Corruption", 0x047B),
        new("Sprite Evaluation", "INC $4014", 0x0480),
        new("PPU Misc.", "t Register Quirks", 0x0482),
        new("PPU Misc.", "Stale BG Shift Registers", 0x0483),
        new("PPU Misc.", "Stale Sprite Shift Regs", 0x048F),
        new("PPU Misc.", "BG Serial In", 0x0487),
        new("PPU Misc.", "Sprites On Scanline 0", 0x0484),
        new("PPU Misc.", "$2004 Stress Test", 0x048C),
        new("PPU Misc.", "$2007 Stress Test", 0x048E),
        new("PPU Misc.", "ALE + Read", 0x0491),
        new("PPU Misc.", "Hybrid Addresses", 0x0492),
        new("CPU Behavior 2", "Instruction Timing", 0x0460),
        new("CPU Behavior 2", "Implied Dummy Reads", 0x046D),
        new("CPU Behavior 2", "Branch Dummy Reads", 0x048B),
        new("CPU Behavior 2", "JSR Edge Cases", 0x047C),
        new("CPU Behavior 2", "Internal Data Bus", 0x0490),
    };

    /// <summary>
    /// The 66 tests (8 suites: SLO/RLA/SRE/RRA/*AX/DCP/ISC/SH*, each 6-10 opcodes, plus the
    /// 8-test "Unofficial Immediates" suite) whose own test routines execute an unofficial/
    /// illegal 6502 opcode as their subject under test. Verified empirically: every one of
    /// BrokenNes's 7 CPU cores throws a "Bad opcode" exception and crashes the whole automated
    /// run somewhere in this range (each at a different specific opcode/PC - CPU_ULQ fails
    /// earliest at $80, the since-retired joke core CPU_Z80 failed on frame 1 before the auto-run
    /// even started). Real 6502 silicon never "crashes" on an undefined opcode - it
    /// always does *something* (often NOP-like, sometimes genuinely useful, as this exact test
    /// suite checks for). Pre-skipping these (see AccuracyCoinRunner.RunSingleCombo's
    /// preSkipAddresses) is what makes it possible to reach the other 75 tests (PPU/APU/timing/
    /// interrupts/DMA) at all for any core.
    /// </summary>
    public static IReadOnlyList<ushort> UnofficialOpcodeTestAddresses { get; } =
        All.Where(t => t.Suite.StartsWith("Unofficial", System.StringComparison.Ordinal))
           .Select(t => t.ResultAddress)
           .ToList();
}
