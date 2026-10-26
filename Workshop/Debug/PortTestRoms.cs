using System;
using System.Text;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// The debug port's test ROMs (milestone D7), assembled here like the debugger's (<see cref="DebugTestRoms"/>): each sends every kind of
/// message once at start-up (print, print with values, a print by pointer, a marker, a timer's name, an unknown command), then every frame
/// times a fixed loop with a numbered timer and a named one, asserts on the third frame and marks the fifth. <c>port</c> is where it writes:
/// the console's port for the real ROM, a neighbouring address (nothing there) for the negative control.
/// </summary>
internal static class PortTestRoms
{
    // what the ROMs send, checked by PortTestCli
    public const string Hello = "hello from the port";
    public const string RoomText = "room %hu of %u, x=%02hx, s=%hd";
    public const string RoomExpected = "room 3 of 4660, x=0a, s=-2";
    public const string PointerText = "by pointer";
    public const string StartMark = "start";
    public const string AssertText = "frame counter < 3 (count=%hu)";
    public const string AssertExpected = "frame counter < 3 (count=3)";
    public const string FiveMark = "five frames";
    public const string WorkTimer = "work";
    public const string DrawTimer = "draw";
    /// <summary>NES: the cycles between the starts of STA $401E and STA $401F around the fixed loop (STA 4, LDX 2, 9 x (DEX 2 + BNE 3), DEX 2, BNE 2, LDA 2).</summary>
    public const int NesWorkCycles = 4 + 2 + 9 * 5 + 2 + 2 + 2;

    // ------------------------------------------------------------------------------------------------------------------ NES

    /// <summary>NROM-128 at $C000 writing its messages to <paramref name="port"/> ($401C: the real port; $4018: the negative control).</summary>
    public static (byte[] rom, Asm asm, string mlb) Nes(ushort port = 0x401C)
    {
        byte lo(int v) => (byte)v; byte hi(int v) => (byte)(v >> 8);
        var a = new Asm(0xC000);
        void Sta(int reg) => a.B(0x8D, lo(port + reg), hi(port + reg));
        void Text(string s) { foreach (char c in s) { a.B(0xA9, (byte)c); Sta(0); } }
        void Bytes(params byte[] b) { foreach (var x in b) { a.B(0xA9, x); Sta(0); } }
        void Cmd(byte c) { a.B(0xA9, c); Sta(1); }

        a.L("reset").B(0x78, 0xD8, 0xA2, 0xFF, 0x9A)
         .B(0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20, 0x85, 0x10, 0x85, 0x20)   // PPU off; counter, frames = 0
         .B(0xA9, 0x80, 0x8D, 0x00, 0x20);                                          // NMI on
        a.L("say_hello"); Text(Hello); Cmd(0x01);
        a.L("say_room"); Text(RoomText); Bytes(0x00, 3, 0x34, 0x12, 0x0A, 0xFE); Cmd(0x01);
        a.L("say_pointer").B(0xA9).Lo("ptext"); Sta(0); a.B(0xA9).Hi("ptext"); Sta(0); Cmd(0x81);
        a.L("mark_start"); Text(StartMark); Cmd(0x03);
        a.L("name_timer"); Bytes(1); Text(WorkTimer); Cmd(0x07);
        a.L("bad_command"); Cmd(0x55);
        a.L("main_loop").B(0xA5, 0x20).L("wait").B(0xC5, 0x20, 0xF0).R("wait");     // LDA frames; CMP frames; BEQ wait
        a.L("timed").B(0xA9, 0x01); Sta(2);                                         // timer 1 start
        a.B(0xA2, 0x0A).L("delay").B(0xCA, 0xD0).R("delay");                       // LDX #10; DEX; BNE
        a.B(0xA9, 0x01); Sta(3);                                                    // timer 1 stop
        Text(DrawTimer); Cmd(0x04);
        a.B(0xA0, 0x05).L("delay2").B(0x88, 0xD0).R("delay2");                     // LDY #5; DEY; BNE
        Text(DrawTimer); Cmd(0x05);
        a.B(0xE6, 0x10, 0xA5, 0x10, 0xC9, 0x03, 0xF0, 0x03, 0x4C).A("no_assert");   // INC counter; LDA; CMP #3; BEQ +3; JMP              // INC counter; LDA; CMP #3; BNE
        a.L("do_assert"); Text(AssertText); Bytes(0x00); a.B(0xA5, 0x10); Sta(0); Cmd(0x02);
        a.L("no_assert").B(0xA5, 0x10, 0xC9, 0x05, 0xF0, 0x03, 0x4C).A("no_mark");
        a.L("do_mark"); Text(FiveMark); Cmd(0x03);
        a.L("no_mark").B(0x4C).A("main_loop");
        a.L("nmi").B(0xE6, 0x20, 0x40);                                             // INC frames; RTI
        a.L("irq").B(0x40);
        a.L("ptext").B(Encoding.ASCII.GetBytes(PointerText)).B(0x00);
        var code = a.Build();
        if (a["delay"] >> 8 != (a["delay"] + 3) >> 8) throw new InvalidOperationException("the timed loop crosses a page (its cycles would change)");
        var prg = new byte[0x4000];
        Array.Fill(prg, (byte)0xEA);
        code.CopyTo(prg, 0);
        void Vec(int at, string l) { prg[at] = (byte)a[l]; prg[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x3FFA, "nmi"); Vec(0x3FFC, "reset"); Vec(0x3FFE, "irq");
        var rom = new byte[16 + 0x4000 + 0x2000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 1;
        prg.CopyTo(rom, 16);
        var mlb = new StringBuilder();
        foreach (var l in new[] { "reset", "say_hello", "say_room", "say_pointer", "mark_start", "name_timer", "bad_command", "main_loop", "timed", "do_assert", "no_assert", "do_mark", "no_mark", "nmi" })
            mlb.Append($"NesPrgRom:{a[l] - 0xC000:X6}:{l}:\n");
        mlb.Append("R:0010:counter@0:\nR:0020:frames@0:\n");
        return (rom, a, mlb.ToString());
    }

    // ------------------------------------------------------------------------------------------------------------------ SNES / WideMess

    /// <summary>LoROM, 32 KB, writing to <paramref name="port"/> ($4230: the real port; $4240: the negative control): 8-bit stores, one 16-bit
    /// store (two DATA bytes at once), a long store of a command, a 3-byte pointer, a write to a reserved register.</summary>
    public static (byte[] rom, Asm asm, string sym) Snes(ushort port = 0x4230)
    {
        byte lo(int v) => (byte)v; byte hi(int v) => (byte)(v >> 8);
        var a = new Asm(0x8000);
        void Sta(int reg) => a.B(0x8D, lo(port + reg), hi(port + reg));
        void Text(string s) { foreach (char c in s) { a.B(0xA9, (byte)c); Sta(0); } }
        void Bytes(params byte[] b) { foreach (var x in b) { a.B(0xA9, x); Sta(0); } }
        void Cmd(byte c) { a.B(0xA9, c); Sta(2); }

        a.L("reset").B(0x78, 0x18, 0xFB, 0xE2, 0x30)                    // SEI CLC XCE SEP #$30
         .B(0xA9, 0x80, 0x8D, 0x00, 0x21)                                // forced blank
         .B(0x64, 0x10, 0x64, 0x20);                                     // STZ counter, frames
        a.L("say_hello"); Text(Hello[..^2]);
        a.L("wide_store").B(0xC2, 0x20, 0xA9, (byte)Hello[^2], (byte)Hello[^1]); Sta(0); a.B(0xE2, 0x20);   // REP #$20; LDA #"rt"; STA DATA (two bytes); SEP #$20
        a.L("long_cmd").B(0xA9, 0x01, 0x8F, lo(port + 2), hi(port + 2), 0x00);                               // LDA #1; STA $00:COMMAND (long)
        a.L("say_room"); Text(RoomText); Bytes(0x00, 3, 0x34, 0x12, 0x0A, 0xFE); Cmd(0x01);
        a.L("say_pointer").B(0xA9).Lo("ptext"); Sta(0); a.B(0xA9).Hi("ptext"); Sta(0); a.B(0xA9, 0x00); Sta(0); Cmd(0x81);
        a.L("mark_start"); Text(StartMark); Cmd(0x03);
        a.L("name_timer"); Bytes(1); Text(WorkTimer); Cmd(0x07);
        a.L("bad_command"); Cmd(0x55);
        a.L("reserved").B(0xA9, 0x00); Sta(0x0C);                       // $423C: reserved
        a.B(0xA9, 0x80, 0x8D, 0x00, 0x42);                              // NMI on
        a.L("main_loop").B(0xA5, 0x20).L("wait").B(0xC5, 0x20, 0xF0).R("wait");
        a.L("timed").B(0xA9, 0x01); Sta(4);
        a.B(0xA2, 0x0A).L("delay").B(0xCA, 0xD0).R("delay");
        a.B(0xA9, 0x01); Sta(6);
        Text(DrawTimer); Cmd(0x04);
        a.B(0xA0, 0x05).L("delay2").B(0x88, 0xD0).R("delay2");
        Text(DrawTimer); Cmd(0x05);
        a.B(0xE6, 0x10, 0xA5, 0x10, 0xC9, 0x03, 0xF0, 0x03, 0x4C).A("no_assert");   // INC counter; LDA; CMP #3; BEQ +3; JMP
        a.L("do_assert"); Text(AssertText); Bytes(0x00); a.B(0xA5, 0x10); Sta(0); Cmd(0x02);
        a.L("no_assert").B(0xA5, 0x10, 0xC9, 0x05, 0xF0, 0x03, 0x4C).A("no_mark");
        a.L("do_mark"); Text(FiveMark); Cmd(0x03);
        a.L("no_mark").B(0x4C).A("main_loop");
        a.L("nmi").B(0x48, 0xE6, 0x20, 0xAD, 0x10, 0x42, 0x68, 0x40);   // PHA INC frames LDA $4210 PLA RTI
        a.L("irq").B(0x40);
        a.L("ptext").B(Encoding.ASCII.GetBytes(PointerText)).B(0x00);
        var code = a.Build();
        var rom = new byte[0x8000];
        code.CopyTo(rom, 0);
        void Vec(int at, string l) { rom[at] = (byte)a[l]; rom[at + 1] = (byte)(a[l] >> 8); }
        Vec(0x7FEA, "nmi"); Vec(0x7FEE, "irq"); Vec(0x7FFC, "reset"); Vec(0x7FFA, "nmi"); Vec(0x7FFE, "irq");
        Array.Copy(Encoding.ASCII.GetBytes("BROKENNES PORT TEST   "), 0, rom, 0x7FC0, 21);
        rom[0x7FD5] = 0x20; rom[0x7FD6] = 0x00; rom[0x7FD7] = 0x05; rom[0x7FD8] = 0x00; rom[0x7FD9] = 0x01;
        rom[0x7FDC] = 0xFF; rom[0x7FDD] = 0xFF; rom[0x7FDE] = 0; rom[0x7FDF] = 0;
        int sum = 0; foreach (byte b in rom) sum += b;
        rom[0x7FDE] = (byte)sum; rom[0x7FDF] = (byte)(sum >> 8); rom[0x7FDC] = (byte)~sum; rom[0x7FDD] = (byte)(~sum >> 8);
        var sym = new StringBuilder("; wlalink no$snes symbols (hand-written for the debug port checks)\n");
        foreach (var l in new[] { "reset", "say_hello", "wide_store", "long_cmd", "say_room", "say_pointer", "mark_start", "name_timer", "bad_command", "reserved", "main_loop", "timed", "do_assert", "no_assert", "do_mark", "no_mark", "nmi" })
            sym.Append($"0000{a[l]:x4} {l}\n");
        sym.Append("[labels]\n00:0010 counter\n00:0020 frames\n");
        return (rom, a, sym.ToString());
    }

    // ------------------------------------------------------------------------------------------------------------------ Game Boy

    /// <summary>A 32 KB ROM-only cartridge sending bgb / Emulicious messages. <paramref name="real"/> false: the negative control, the same code with
    /// every message's $6464 signature changed to $6565 and <c>ld b,b</c> to <c>ld c,c</c> (what no emulator reads).</summary>
    public static (byte[] rom, Asm asm, string sym) GameBoy(bool real = true)
    {
        byte sig = real ? (byte)0x64 : (byte)0x65;
        byte brk = real ? (byte)0x40 : (byte)0x49;
        var a = new Asm(0x0150);
        int n = 0;
        void Msg(string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            string end = "m_end" + n++;
            a.B(0x52, 0x18).R(end).B(sig, sig, 0x00, 0x00).B(b).L(end);
        }
        a.L("start").B(0xF3, 0x31, 0xFE, 0xFF)                      // DI; LD SP,$FFFE
         .B(0xAF, 0xEA, 0x00, 0xC0, 0xEA, 0x01, 0xC0);              // XOR A; LD ($C000),A; LD ($C001),A
        a.L("say_hello"); Msg(Hello);
        a.L("say_regs").B(0x3E, 0x05, 0x21, 0x34, 0x12); Msg("A=%A% HL=%HL% sum=%3+4%");   // LD A,5; LD HL,$1234
        a.L("say_printf").B(0x21).A("fmt").B(0x11).A("args").B(0x52, 0x18, 0x04, sig, sig, 0x00, 0x02);   // LD HL,fmt; LD DE,args; the printf message
        a.L("mark_start"); Msg("@mark " + StartMark);
        a.L("plain_ldd").B(0x52, 0x00);                              // LD D,D; NOP: no message
        a.L("bare_break").B(brk);                                    // LD B,B: a break
        a.L("skipped").B(0x18).R("main_loop"); Msg("never shown");   // JR over a message: it never runs
        a.L("main_loop").L("wait_ly").B(0xF0, 0x44, 0xFE, 0x90, 0x20).R("wait_ly");    // LDH A,(LY); CP 144; JR NZ
        a.L("timed"); Msg("@timer+ " + WorkTimer + " %ZEROCLKS%");
        a.B(0x06, 0x0A).L("delay").B(0x05, 0x20).R("delay");        // LD B,10; DEC B; JR NZ
        Msg("@timer- " + WorkTimer + " %-8+LASTCLKS%");
        a.B(0xFA, 0x00, 0xC0, 0x3C, 0xEA, 0x00, 0xC0, 0xFE, 0x03, 0x20).R("no_assert");   // LD A,($C000); INC A; LD ($C000),A; CP 3; JR NZ
        a.L("do_assert"); Msg("@assert main.c:42 frame counter < 3"); a.B(brk);
        a.L("no_assert").B(0xFA, 0x00, 0xC0, 0xFE, 0x05, 0x20).R("no_mark");
        a.L("do_mark"); Msg("@mark " + FiveMark);
        a.L("no_mark").L("wait_ly2").B(0xF0, 0x44, 0xFE, 0x90, 0x28).R("wait_ly2");    // wait until LY leaves 144
        a.B(0xC3).A("main_loop");
        a.L("fmt").B(Encoding.ASCII.GetBytes("v=%u c=%hx s=%s")).B(0x00);
        a.L("args").B(0xD2, 0x04, 0xAB).B(0x00, 0x00).L("argstr").B(Encoding.ASCII.GetBytes("ok")).B(0x00);
        var code = a.Build();
        var rom = new byte[0x8000];
        rom[0x0100] = 0x00; rom[0x0101] = 0xC3; rom[0x0102] = 0x50; rom[0x0103] = 0x01;
        Encoding.ASCII.GetBytes("PORTTEST").CopyTo(rom, 0x134);
        rom[0x147] = 0x00; rom[0x148] = 0x00; rom[0x149] = 0x00;
        code.CopyTo(rom, 0x150);
        // the %s argument's pointer, patched now the address is known
        int argAt = (int)a["args"] - 0x150 + 0x150 + 3;
        rom[argAt] = (byte)a["argstr"]; rom[argAt + 1] = (byte)(a["argstr"] >> 8);
        byte chk = 0; for (int i = 0x134; i <= 0x14C; i++) chk = (byte)(chk - rom[i] - 1);
        rom[0x14D] = chk;
        var sym = new StringBuilder("; bgb symbols (hand-written for the debug port checks)\n");
        foreach (var l in new[] { "start", "say_hello", "say_regs", "say_printf", "mark_start", "plain_ldd", "bare_break", "skipped", "main_loop", "timed", "do_assert", "no_assert", "do_mark", "no_mark" })
            sym.Append($"00:{a[l]:X4} {l}\n");
        sym.Append("00:C000 counter\n");
        return (rom, a, sym.ToString());
    }

    public const string GbRegsExpected = "A=$05 HL=$1234 sum=7";
    public const string GbPrintfExpected = "v=1234 c=ab s=ok";
}
