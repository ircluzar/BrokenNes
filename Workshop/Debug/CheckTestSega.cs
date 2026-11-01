using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Debugging.Checks;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--check-test --only sega</c>: the Sega rules (Timeflow T0), each with a trigger and a negative control: small Master System and Genesis programs (the Genesis's 68000
/// with, for the Z80 rules, a Z80 program it loads and starts).
/// </summary>
internal static partial class CheckTestCli
{
    // ---------------------------------------------------------------- Master System / Game Gear
    private static byte[] SmsRom(params byte[] main)
    {
        // $0000: DI; LD SP,$DFF0; then the program
        var rom = new byte[0x8000];
        var code = new byte[] { 0xF3, 0x31, 0xF0, 0xDF }.Concat(main).ToArray();
        code.CopyTo(rom, 0);
        System.Text.Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(rom, 0x7FF0);
        rom[0x7FFF] = 0x4C;
        return rom;
    }

    private static (List<Finding>, ConsoleChecks) SmsRun(byte[] rom, string name, ConsoleKind kind = ConsoleKind.MasterSystem, int frames = 10) =>
        RunRom(rom, name + (kind == ConsoleKind.GameGear ? ".gg" : ".sms"), kind, "[labels]\n", frames);

    private static readonly byte[] SmsSpin = { 0x18, 0xFE };                                     // JR $
    private static readonly byte[] SmsWork = { 0x32, 0x00, 0xC0, 0x3C, 0x18, 0xFA };             // loop: LD ($C000),A; INC A; JR loop

    private static void Sega()
    {
        var (clean, _) = SmsRun(SmsRom(SmsSpin), "sms_clean", frames: 30);
        Check("SMS: the clean template breaks no rule", clean.Count == 0, clean.Count == 0 ? "no finding in 30 frames" : string.Join("; ", clean));
        foreach (var kind in new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear })
        {
            string t = kind == ConsoleKind.GameGear ? "gg" : "sms";
            Pair("sms.ram-uninit", () => SmsRun(SmsRom(0x3A, 0x45, 0xC3, 0x18, 0xFE), t + "_uninit", kind),                         // LD A,($C345); JR $
                () => SmsRun(SmsRom(0x3E, 0x00, 0x32, 0x45, 0xE3, 0x3A, 0x45, 0xC3, 0x18, 0xFE), t + "_uninit_neg", kind),           // LD A,0; LD ($E345),A (the mirror); LD A,($C345); JR $
                place: "ram:$0345", also: f => f.FirstFrame == 0, what: kind == ConsoleKind.GameGear ? "Game Gear" : "Master System; the negative writes it through the mirror");
        }
        Pair("sms.lag-frame", () => SmsRun(SmsRom(SmsWork), "sms_lag", frames: 10), () => SmsRun(SmsRom(SmsSpin), "sms_lag_neg", frames: 10), fromFrame: 1);

        // ---------------------------------------------------------------- Genesis
        var (mclean, _) = MdRun(MdRom(MdSpin), "md_clean", 30);
        Check("Genesis: the clean template breaks no rule", mclean.Count == 0, mclean.Count == 0 ? "no finding in 30 frames" : string.Join("; ", mclean));
        Pair("md.ram-uninit", () => MdRun(MdRom(W(0x1039), L(0xFF0345), MdSpin), "md_uninit"),                                         // MOVE.B $FF0345,D0; BRA *
            () => MdRun(MdRom(W(0x13FC, 0x0000), L(0xE00345), W(0x1039), L(0xFF0345), MdSpin), "md_uninit_neg"),                       // MOVE.B #0,$E00345 (the mirror); MOVE.B $FF0345,D0; BRA *
            place: "ram:$0345", also: f => f.FirstFrame == 0, what: "the negative writes it through the mirror");
        // the Z80's RAM: a Z80 program that reads $1345 (never written) against one that writes it first
        Pair("md.zram-uninit", () => MdRun(MdRom(LoadZ80(0x3A, 0x45, 0x13, 0x18, 0xFE)), "md_zuninit"),                                 // LD A,($1345); JR $
            () => MdRun(MdRom(LoadZ80(0x3E, 0x07, 0x32, 0x45, 0x13, 0x3A, 0x45, 0x13, 0x18, 0xFE)), "md_zuninit_neg"),                   // LD A,7; LD ($1345),A; LD A,($1345); JR $
            place: "zram:$1345");
        // the 68000 writing the Z80's RAM after releasing its reset, without requesting its bus; the negative requests it first
        Pair("md.z80-bus", () => MdRun(MdRom(W(0x33FC, 0x0100), L(0xA11200), W(0x13FC, 0x0001), L(0xA01000), MdSpin), "md_zbus"),
            () => MdRun(MdRom(W(0x33FC, 0x0100), L(0xA11100), W(0x33FC, 0x0100), L(0xA11200), W(0x0839, 0x0000), L(0xA11100), W(0x66F6), W(0x13FC, 0x0001), L(0xA01000), MdSpin), "md_zbus_neg"),
            also: f => f.Address == "$A01000");
        Pair("md.lag-frame", () => MdRun(MdRom(W(0x5279), L(0xFF0000), W(0x60F8)), "md_lag"), () => MdRun(MdRom(MdSpin), "md_lag_neg"), fromFrame: 1);   // loop: ADDQ.W #1,$FF0000; BRA.S loop
    }

    private static int[] W(params int[] w) => w;
    private static int[] L(uint v) => new[] { (int)(v >> 16), (int)(v & 0xFFFF) };
    private static readonly int[] MdSpin = { 0x60FE };   // BRA.S *

    private static byte[] MdRom(params int[][] code)
    {
        var m = new M68kAsm();
        m.Long(0x00FFFE00).Long(0x200);
        for (int v = 2; v < 64; v++) m.Long(0x300);
        m.Org(0x100).Text("SEGA MEGA DRIVE ");
        m.Org(0x200).W(0x46FC, 0x2700);   // MOVE #$2700,SR
        foreach (var part in code) m.W(part);
        m.Org(0x300).W(0x4E73);           // an exception: RTE
        return m.Build(0x8000);
    }

    /// <summary>68000 code that loads <paramref name="z80"/> into the Z80's RAM and starts it (bus request, reset released, the copy, the bus given back), then spins.</summary>
    private static int[] LoadZ80(params byte[] z80)
    {
        var w = new List<int>();
        w.AddRange(W(0x33FC, 0x0100)); w.AddRange(L(0xA11100));       // request the bus
        w.AddRange(W(0x33FC, 0x0100)); w.AddRange(L(0xA11200));       // release reset
        w.AddRange(W(0x0839, 0x0000)); w.AddRange(L(0xA11100)); w.Add(0x66F6);   // wait for the grant
        for (int i = 0; i < z80.Length; i++) { w.AddRange(W(0x13FC, z80[i])); w.AddRange(L(0xA00000 + (uint)i)); }   // MOVE.B #b,$A0000i
        w.AddRange(W(0x33FC, 0x0000)); w.AddRange(L(0xA11200));       // reset
        w.AddRange(W(0x33FC, 0x0000)); w.AddRange(L(0xA11100));       // give the bus back
        w.AddRange(W(0x33FC, 0x0100)); w.AddRange(L(0xA11200));       // and let it run
        w.AddRange(MdSpin);
        return w.ToArray();
    }

    private static (List<Finding>, ConsoleChecks) MdRun(byte[] rom, string name, int frames = 10) => RunRom(rom, name + ".md", ConsoleKind.Genesis, "", frames);
}
