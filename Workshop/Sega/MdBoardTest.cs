using System;
using System.IO;
using System.Text;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-test</c>: the Genesis board, its VDP, pads, cartridge and sound-unit handshake, checked against what the documentation and the hardware tests say (not against this code's own habits).
/// Expectations that come from hardware tests carry their source: <b>Nemesis' VDP port access test ROM</b> (the FIFO ring and the unused bits of CRAM, VSRAM and 8-bit reads, the byte-swapping of odd
/// VRAM addresses, the 8-bit read target, writes to a read target), the memory-test ROM of Charles MacDonald (open bus), the 6-button pad protocol of Sega's documentation. The VDP is driven
/// standalone (explicit master-clock times) where the 68000 would only be in the way, and through the board's bus where the map itself is under test. <c>--md-test --list</c> prints the checks.
/// </summary>
internal static class MdBoardTest
{
    private static int pass, fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  (" + detail + ")")}");
    }

    private static void Group(string title) => Console.WriteLine($"-- {title}");

    // =====================================================================================================================
    //  Rigs
    // =====================================================================================================================

    /// <summary>A ROM image: the vectors (stack at $FFFE00, entry at $200), "SEGA" at $100, a header with the region letters, and <paramref name="code"/> at $200.</summary>
    private static byte[] MakeRom(string regions, params ushort[] code)
    {
        var rom = new byte[0x20000];
        void L(int at, uint v) { rom[at] = (byte)(v >> 24); rom[at + 1] = (byte)(v >> 16); rom[at + 2] = (byte)(v >> 8); rom[at + 3] = (byte)v; }
        L(0, 0xFFFE00); L(4, 0x200);
        for (int v = 2; v < 64; v++) L(v * 4, 0x400);
        Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100);
        Encoding.ASCII.GetBytes("(C)TEST 2026.OCT").CopyTo(rom, 0x110);
        Encoding.ASCII.GetBytes("BOARD TEST ROM".PadRight(48)).CopyTo(rom, 0x120);
        Encoding.ASCII.GetBytes("BOARD TEST ROM".PadRight(48)).CopyTo(rom, 0x150);
        Encoding.ASCII.GetBytes("GM 00000000-00").CopyTo(rom, 0x180);
        Encoding.ASCII.GetBytes(regions.PadRight(16)).CopyTo(rom, 0x1F0);
        for (int i = 0; i < code.Length; i++) { rom[0x200 + i * 2] = (byte)(code[i] >> 8); rom[0x201 + i * 2] = (byte)code[i]; }
        rom[0x400] = 0x60; rom[0x401] = 0xFE;           // the exception handler spins
        rom[0x1000] = 0x12; rom[0x1001] = 0x34;          // a recognisable word
        rom[0x1002] = 0x56; rom[0x1003] = 0x78;
        return rom;
    }

    private static BOARD_MD Board(byte[]? rom = null, SegaRegion region = SegaRegion.Ntsc, GenesisModel model = GenesisModel.Model1)
        => new(new MdCartridge(rom ?? MakeRom("JUE", 0x60FE)), region, model, 44100);

    private static ushort R16(BOARD_MD b, uint a) => b.BusRead(a, true, true, 4);
    private static byte R8(BOARD_MD b, uint a) { ushort w = b.BusRead(a & ~1u, (a & 1) == 0, (a & 1) != 0, 4); return (byte)((a & 1) == 0 ? w >> 8 : w); }
    private static void W16(BOARD_MD b, uint a, ushort v) => b.BusWrite(a, true, true, v, 4);
    private static void W8(BOARD_MD b, uint a, byte v) => b.BusWrite(a & ~1u, (a & 1) == 0, (a & 1) != 0, (ushort)(v << 8 | v), 4);
    private static void Idle(BOARD_MD b, int cycles) { b.BusIdle(cycles); b.Vdp.RunTo(b.MasterClock); }

    /// <summary>A VDP on its own with a flat 68000 space behind it, driven at times the test chooses (every port access costs the 68000's 4 cycles = 28 master clocks).</summary>
    private sealed class Rig : IMdVdpHost
    {
        public readonly MdVdp V;
        public long T;
        public readonly byte[] Mem = new byte[0x20000];
        public int Z80IntEdges; public bool Z80Int;

        public Rig(SegaRegion region = SegaRegion.Ntsc) { V = new MdVdp(SegaTimeline.Genesis(region)); V.Attach(this); }
        public ushort ReadBusWord(uint address) => (ushort)(Mem[address & 0x1FFFF] << 8 | Mem[address + 1 & 0x1FFFF]);
        public void SetZ80Interrupt(bool asserted, long masterClock) { Z80Int = asserted; if (asserted) Z80IntEdges++; }

        public void Tick(int cycles = 4) { T += cycles * 7L; }
        public void Ctl(ushort v) { Tick(); V.WriteControl(v, T); if (V.Time > T) T = V.Time; }
        public void Data(ushort v) { Tick(); V.WriteData(v, T); if (V.Time > T) T = V.Time; }
        public ushort ReadData() { Tick(); var v = V.ReadData(T); if (V.Time > T) T = V.Time; return v; }
        public ushort Status() { Tick(); var v = V.ReadStatus(T); if (V.Time > T) T = V.Time; return v; }
        public void Reg(int r, int val) => Ctl((ushort)(0x8000 | r << 8 | val));
        public void Cmd(int code, int addr) { Ctl((ushort)((code & 3) << 14 | addr & 0x3FFF)); Ctl((ushort)((code >> 2 & 0xF) << 4 | addr >> 14 & 3)); }
        public void RunTo(long t) { if (t > T) T = t; V.RunTo(T); }
        public void Wait(int cycles) { T += cycles * 7L; V.RunTo(T); }
        public long LineStart(int line) => (long)line * MdVdp.ClocksPerLine;
        public void Settle() { for (int i = 0; i < 2000 && (V.DmaBusy || !FifoEmpty()); i++) { T += 70; V.RunTo(T); } }
        private bool FifoEmpty() => (V.ReadStatus(T) & 0x200) != 0;
    }

    // =====================================================================================================================

    public static int Run(string[] args)
    {
        Console.WriteLine("== Genesis board, VDP, pads, cartridge and handshake self-test");
        // proof that the checks can fail: --slot-set "nextslotfix=0" (or any experiment name) runs the same checks on the old rule
        if (M68kTestCli.Arg(args, "--slot-set") is { } slotSet) { MdRunCli.ApplySlotSet(slotSet); Console.WriteLine($"   (experiment settings: {slotSet})"); }
        MapAndOpenBus();
        Pads();
        Z80Handshake();
        VdpPorts();
        VdpFifoAndUnusedBits();
        VdpCountersAndInterrupts();
        VdpDma();
        Timing();
        InterruptSamplePoints();
        VdpPortLoops();
        Cartridge();
        MdSoundTest.Run(Check, Group);
        Console.WriteLine($"== {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // =====================================================================================================================
    //  The 68000's map
    // =====================================================================================================================

    private static void MapAndOpenBus()
    {
        Group("the 68000's map and the open bus");
        var b = Board();
        Check("ROM reads: the word at $1000", R16(b, 0x1000) == 0x1234 && R16(b, 0x1002) == 0x5678);
        Check("a 128 KB ROM mirrors through the cartridge area", R16(b, 0x21000) == 0x1234 && R16(b, 0x3E1000) == 0x1234);
        W16(b, 0xFF0010, 0xBEEF);
        Check("work RAM at $FF0010 reads back", R16(b, 0xFF0010) == 0xBEEF);
        Check("work RAM mirrors every 64 KB through $E00000-$FFFFFF", R16(b, 0xE00010) == 0xBEEF && R16(b, 0xF30010) == 0xBEEF);
        W8(b, 0xFF0011, 0x55);
        Check("a byte write to an odd address changes only the low byte", R16(b, 0xFF0010) == 0xBE55, $"{R16(b, 0xFF0010):X4}");
        W8(b, 0xFF0010, 0x66);
        Check("a byte write to an even address changes only the high byte", R16(b, 0xFF0010) == 0x6655);

        // open bus: an unmapped read returns what the data bus held, which is the word of the last bus cycle (MacDonald's memory test: $400000-$7FFFFF read as the next instruction word)
        R16(b, 0x1000);
        Check("an unmapped read ($500000) returns the last bus word", R16(b, 0x500000) == 0x1234, $"{R16(b, 0x500000):X4}");
        R16(b, 0x1002);
        Check("... and follows the bus ($C00018, an unused VDP mirror)", R16(b, 0xC00018) == 0x5678, $"{R16(b, 0xC00018):X4}");
        R16(b, 0x1000);
        ushort status = R16(b, 0xC00004);
        Check("the VDP status keeps the bus word in its six unused top bits (BlastEm and the memory test agree)", (status & 0xFC00) == 0x1000 && (status & 0x200) != 0, $"{status:X4}");
        Check("a TMSS write ($A14000) is ignored", Try(() => { W16(b, 0xA14000, 0x5345); W16(b, 0xA14002, 0x4741); }));

        // version register: bit 7 overseas, bit 6 PAL, bit 5 no expansion unit
        Check("version register, NTSC overseas ROM: $A0", R8(Board(MakeRom("JUE")), 0xA10001) == 0xA0, $"{R8(Board(MakeRom("JUE")), 0xA10001):X2}");
        Check("version register, NTSC Japan-only ROM: $20", R8(Board(MakeRom("J")), 0xA10001) == 0x20);
        Check("version register, PAL: $E0", R8(Board(MakeRom("E"), SegaRegion.Pal), 0xA10001) == 0xE0);
    }

    private static bool Try(Action a) { try { a(); return true; } catch { return false; } }

    // =====================================================================================================================
    //  Pads
    // =====================================================================================================================

    private static void Pads()
    {
        Group("pads (3-button lines, the 6-button TH sequence, the timeout)");
        var b = Board();
        W8(b, 0xA10003, 0x40); W8(b, 0xA10005, 0x40);       // the data latches first (TH high), then TH becomes an output on both ports: no edge for the pad
        W8(b, 0xA10009, 0x40); W8(b, 0xA1000B, 0x40);
        Check("TH high, nothing pressed: $7F", R8(b, 0xA10003) == 0x7F, $"{R8(b, 0xA10003):X2}");
        b.SetPad(0, PadButtons.Up | PadButtons.C);
        Check("TH high: Up and C pressed read as 0 at bit 0 and bit 5", R8(b, 0xA10003) == 0x5E, $"{R8(b, 0xA10003):X2}");
        W8(b, 0xA10003, 0x00);
        b.SetPad(0, PadButtons.Start | PadButtons.Down);
        Check("TH low: Start is bit 5, A bit 4, bits 3-2 are 0, Down bit 1: Start+Down = $11", R8(b, 0xA10003) == 0x11, $"{R8(b, 0xA10003):X2}");
        byte p2 = R8(b, 0xA10005); b.SetPad(1, PadButtons.Left); byte p2l = R8(b, 0xA10005);
        Check("port 2's pad is independent of port 1 (TH high there: $7F; Left pressed: $7B)", p2 == 0x7F && p2l == 0x7B, $"{p2:X2} {p2l:X2}");
        Check("the control register reads back what was written", R8(b, 0xA10009) == 0x40);

        // the six-button pad: TH high/low four times from the timeout; the sixth phase (third low) is the ID, the seventh (fourth high) the extra buttons, the eighth (fourth low) the ones
        var s = Board();
        W8(s, 0xA10003, 0x40); W8(s, 0xA10009, 0x40);
        s.SetPad(0, PadButtons.X | PadButtons.Mode | PadButtons.Start | PadButtons.Right);
        byte[] seq = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            W8(s, 0xA10003, (byte)((i & 1) == 0 ? 0x40 : 0x00));
            seq[i] = R8(s, 0xA10003);
        }
        // phases 1-5: normal; Right pressed clears bit 3 of the TH-high reads, Start clears bit 5 of the TH-low ones
        Check("six-button, phase 1 (TH high): the normal lines, Right pressed", seq[0] == 0x77, $"{seq[0]:X2}");
        Check("six-button, phase 2 (TH low): Start pressed, the 0 0 bits", seq[1] == (0x10 | 0x03), $"{seq[1]:X2}");
        Check("six-button, phases 3-5 are normal", seq[2] == 0x77 && seq[3] == 0x13 && seq[4] == 0x77, $"{seq[2]:X2} {seq[3]:X2} {seq[4]:X2}");
        Check("six-button, phase 6 (third TH low): Start, A, four zero bits = the 6-button ID", seq[5] == 0x10, $"{seq[5]:X2}");
        Check("six-button, phase 7 (fourth TH high): C B Mode X Y Z with Mode and X pressed = $73 ^ ...", seq[6] == (0x40 | 0x20 | 0x10 | 0x00 | 0x00 | 0x02 | 0x01), $"{seq[6]:X2}");
        Check("six-button, phase 8 (fourth TH low): Start, A, four one bits", seq[7] == 0x1F, $"{seq[7]:X2}");
        // the timeout: 8192 68000 cycles without a TH edge restarts the count
        W8(s, 0xA10003, 0x40); W8(s, 0xA10003, 0x00); W8(s, 0xA10003, 0x40); W8(s, 0xA10003, 0x00); W8(s, 0xA10003, 0x40); W8(s, 0xA10003, 0x00);
        byte beforeTimeout = R8(s, 0xA10003);
        Idle(s, 8300);
        W8(s, 0xA10003, 0x40);
        byte afterTimeout = R8(s, 0xA10003);
        Check("after the third TH low the ID shows; after 8300 idle cycles TH high is a normal read again", beforeTimeout == 0x10 && afterTimeout == 0x77, $"{beforeTimeout:X2} {afterTimeout:X2}");
        var three = Board();
        three.Pads.SetKind(0, MdPadKind.ThreeButton);
        W8(three, 0xA10003, 0x40); W8(three, 0xA10009, 0x40);
        byte last = 0;
        for (int i = 0; i < 6; i++) { W8(three, 0xA10003, (byte)((i & 1) == 0 ? 0x40 : 0x00)); last = R8(three, 0xA10003); }
        Check("a 3-button pad never shows the ID (TH low phase 6 = Start, A, 0 0, D, U)", last == 0x33, $"{last:X2}");
    }

    // =====================================================================================================================
    //  Z80, BUSREQ and RESET
    // =====================================================================================================================

    private static void Z80Handshake()
    {
        Group("the Z80's bus handshake, RAM window and bank window");
        var b = Board();
        Check("at power-on the Z80 is in reset and the bus is not requested ($A11100 bit 8 set)", (R16(b, 0xA11100) & 0x100) != 0 && b.Sound.IsInReset);
        W16(b, 0xA11100, 0x0100);
        Idle(b, 20);
        Check("BUSREQ while the Z80 is held in reset is granted at once ($A11100 bit 8 clear)", (R16(b, 0xA11100) & 0x100) == 0);
        W8(b, 0xA00010, 0x5A); W8(b, 0xA00011, 0xA5);
        Check("with the bus granted the 68000 writes and reads Z80 RAM ($A00010)", R8(b, 0xA00010) == 0x5A && R8(b, 0xA00011) == 0xA5);
        Check("Z80 RAM mirrors at +$2000", R8(b, 0xA02010) == 0x5A);
        Check("the RESET register reads back released/asserted in bit 8 (oracle-derived)", (R16(b, 0xA11200) & 0x100) == 0);

        // a Z80 program: LD A,$42 / LD ($0100),A / JR $ ; released from reset it runs by itself
        byte[] prog = { 0x3E, 0x42, 0x32, 0x00, 0x01, 0x18, 0xFE };
        for (int i = 0; i < prog.Length; i++) W8(b, (uint)(0xA00000 + i), prog[i]);
        W16(b, 0xA11200, 0x0100);       // release reset
        W16(b, 0xA11100, 0x0000);       // give the bus back
        Idle(b, 400);
        Check("after RESET release and BUSREQ release the Z80 runs on its own", b.Sound.IsInReset == false && !b.Sound.IsBusGranted(b.MasterClock));
        Check("while the Z80 runs, the 68000's reads of $A00000 get no RAM ($FF)", R8(b, 0xA00100) == 0xFF);
        W16(b, 0xA11100, 0x0100);
        Idle(b, 60);
        bool granted = (R16(b, 0xA11100) & 0x100) == 0;
        Check("BUSREQ with the Z80 running is granted within a few Z80 instructions", granted);
        Check("the program ran: the Z80 stored $42 at $0100", R8(b, 0xA00100) == 0x42, $"{R8(b, 0xA00100):X2}");

        // the grant comes at the end of the machine cycle the Z80 is in, not at the end of its instruction: a Z80 looping on EX (SP),HL (19 T-states) is asked for the bus at 60 different phases of the loop
        // and the 68000 looks 20 of its cycles (140 master clocks, 9 T-states) later; a grant at the instruction boundary is up to 19 T-states (285 clocks) late and misses some of them. Games start their
        // sound drivers with "request, release RESET, test BUSACK once" and branch into the middle of the test if the first answer is "not yet" (Bugs Bunny in Double Trouble).
        var slow = Board();
        W16(slow, 0xA11100, 0x0100);
        byte[] longLoop = { 0x31, 0x00, 0x01, 0xE3, 0x18, 0xFD };     // LD SP,$0100 ; loop: EX (SP),HL ; JR loop
        for (int i = 0; i < longLoop.Length; i++) W8(slow, (uint)(0xA00000 + i), longLoop[i]);
        W16(slow, 0xA11200, 0x0100); W16(slow, 0xA11100, 0x0000);
        int notYet = 0;
        for (int phase = 0; phase < 60; phase++)
        {
            Idle(slow, 300 + phase * 3);
            W16(slow, 0xA11100, 0x0100);
            Idle(slow, 20);
            if ((R16(slow, 0xA11100) & 0x100) != 0) notYet++;
            W16(slow, 0xA11100, 0x0000);
        }
        Check("BUSREQ is granted within 20 68000 cycles wherever a Z80 in a 19-T-state instruction loop is asked (60 phases)", notYet == 0, $"{notYet} of 60 not granted yet");

        // the bank window: ROM at the 68000 address (bank << 15) | (z80 address & $7FFF)
        var c = Board();
        W16(c, 0xA11100, 0x0100);
        // LD A,($9000) ; LD ($0101),A ; JR $   with bank 0: address $1000 in ROM = $12
        byte[] prog2 = { 0x3A, 0x00, 0x90, 0x32, 0x01, 0x01, 0x18, 0xFE };
        for (int i = 0; i < prog2.Length; i++) W8(c, (uint)(0xA00000 + i), prog2[i]);
        for (int bit = 0; bit < 9; bit++) W8(c, 0xA06000, 0);       // the bank register is nine serial writes; zeros give bank 0
        W16(c, 0xA11200, 0x0100); W16(c, 0xA11100, 0x0000);
        Idle(c, 600);
        W16(c, 0xA11100, 0x0100); Idle(c, 60);
        Check("the Z80 reads the ROM through its bank window ($9000 = ROM $1000)", R8(c, 0xA00101) == 0x12, $"{R8(c, 0xA00101):X2}");
        Check("the bank-window access cost the Z80 and the 68000 time (counted by the unit)", ((MdSoundUnit)c.Sound).BankReads > 0);
        // the YM2612 busy flag and timers are the sound unit's; a status read of $A04000 is reachable through the 68000 when granted
        Check("the YM2612's status at $A04000 reads (busy flag clear at power-on)", (R8(c, 0xA04000) & 0x80) == 0);
    }

    // =====================================================================================================================
    //  VDP ports
    // =====================================================================================================================

    private static void VdpPorts()
    {
        Group("VDP: registers, commands, the pending flag, memories");
        var r = new Rig();
        r.Reg(0, 0x04); r.Reg(1, 0x44); r.Reg(15, 2); r.Reg(12, 0x81);
        Check("register writes land (R1 = display on, mode 5; R15 = 2; R12 = H40)", r.V.Reg[1] == 0x44 && r.V.Reg[15] == 2 && r.V.H40 && r.V.Mode5 && r.V.DisplayEnabled);
        r.Reg(1, 0x04);
        r.Ctl(0x4100);
        Check("the first word of a command sets the pending flag", r.V.CommandPending);
        r.Status();
        Check("a status read clears the pending flag", !r.V.CommandPending);
        r.Ctl(0x4100); r.Ctl(0x0000);
        Check("the second word completes the command: VRAM write at $0100", !r.V.CommandPending && r.V.Code == 1 && r.V.Address == 0x100);
        r.Data(0xABCD); r.Data(0x1234);
        r.Settle();
        Check("data port writes land in VRAM with the auto-increment (2)", r.V.Vram[0x100] == 0xAB && r.V.Vram[0x101] == 0xCD && r.V.Vram[0x102] == 0x12 && r.V.Vram[0x103] == 0x34);
        r.Cmd(0, 0x100);
        ushort a = r.ReadData(), b2 = r.ReadData();
        Check("VRAM reads return the words in order", a == 0xABCD && b2 == 0x1234, $"{a:X4} {b2:X4}");
        r.Reg(15, 1);
        r.Cmd(1, 0x200); r.Data(0x1122); r.Settle();
        Check("auto-increment 1 moves the address by one byte", r.V.Address == 0x201 && r.V.Vram[0x200] == 0x11 && r.V.Vram[0x201] == 0x22);
        r.Reg(15, 2);

        r.Cmd(3, 0x04); r.Data(0x0EEE); r.Data(0xFFFF); r.Settle();
        Check("CRAM writes keep only the 9 colour bits ($0EEE mask)", r.V.Cram[2] == 0x0EEE && r.V.Cram[3] == 0x0EEE);
        r.Cmd(5, 0x02); r.Data(0xFFFF); r.Settle();
        Check("VSRAM writes keep 11 bits", r.V.Vsram[1] == 0x07FF, $"{r.V.Vsram[1]:X4}");
        r.Cmd(5, 0x00); r.Data(0x0123); r.Cmd(5, 0x50); r.Data(0x0456); r.Settle();
        r.Cmd(4, 0x50);
        ushort vs = r.ReadData();
        Check("VSRAM holds 40 words: a write to entry 40 is lost and a read there returns entry 0 (Nemesis' DMA fill to VSRAM tests)", (vs & 0x7FF) == 0x123 && r.V.Vsram[40] == 0, $"{vs:X4}");
        r.Cmd(1, 0xFFFE); r.Data(0xAAAA); r.Data(0xBBBB); r.Settle();
        Check("the VRAM address wraps at 64 KB", r.V.Vram[0xFFFE] == 0xAA && r.V.Vram[0] == 0xBB && r.V.Vram[1] == 0xBB);

        // MacDonald/Nemesis: a word written at an odd VRAM address lands byte-swapped at the even address below it, and reads at an odd address return the even word
        r.Cmd(1, 0x8001); r.Data(0x1122); r.Data(0x3344); r.Settle();
        Check("a word written at an odd VRAM address is stored byte-swapped (Nemesis: VRAM Byteswapping)", r.V.Vram[0x8000] == 0x22 && r.V.Vram[0x8001] == 0x11 && r.V.Vram[0x8002] == 0x44 && r.V.Vram[0x8003] == 0x33);
        r.Cmd(0, 0x8001);
        ushort o1 = r.ReadData(), o2 = r.ReadData();
        Check("a read at an odd VRAM address returns the even word's own bytes", o1 == 0x2211 && o2 == 0x4433, $"{o1:X4} {o2:X4}");

        // Nemesis (Partial CP Writes): a first word alone changes CD1:0 and A13:0 and leaves the rest; the next control word is then the second half of a command even if it looks like a register write
        var pc = new Rig();
        pc.Reg(1, 0x64); pc.Reg(12, 0x81); pc.Reg(15, 2);
        pc.Cmd(1, 0x8000); pc.Data(0xFFFF); pc.Cmd(1, 0x8002); pc.Data(0xFFFF); pc.Settle();
        pc.Reg(15, 1);
        pc.Cmd(1, 0x8000); pc.Ctl(0x4000); pc.Ctl(0x8F02);
        Check("a register-write-looking word after a lone first word completes the command (R15 stays 1)", pc.V.Reg[15] == 1 && !pc.V.CommandPending);
        pc.Data(0x0123); pc.Data(0x4567); pc.Settle();
        pc.Reg(15, 2);
        pc.Cmd(0, 0x8000);
        ushort p1 = pc.ReadData(), p2 = pc.ReadData();
        Check("... so the two words went in at increment 1: $0123 at $8000, $4567 byte-swapped over it ($6745), $FFFF at $8002", p1 == 0x6745 && p2 == 0xFFFF, $"{p1:X4} {p2:X4}");

        // Nemesis (Register Writes and Code Reg): a register write puts 10 in CD1:0, so data written after it goes nowhere
        var rw = new Rig();
        rw.Reg(1, 0x64); rw.Reg(12, 0x81); rw.Reg(15, 2);
        rw.Cmd(1, 0x8000); rw.Data(0xFFFF); rw.Settle();
        rw.Cmd(1, 0x8000); rw.Reg(15, 2); rw.Data(0xBEEF); rw.Settle();
        Check("data written after a register write is lost (CD1:0 became 10)", rw.V.Vram[0x8000] == 0xFF && rw.V.Vram[0x8001] == 0xFF);
        // a read right behind queued writes sees them (the pre-cache waits for the FIFO)
        var rb = new Rig();
        rb.Reg(1, 0x64); rb.Reg(12, 0x81); rb.Reg(15, 2);
        rb.Cmd(1, 0x4000); rb.Data(0x1111); rb.Data(0x2222); rb.Data(0x3333); rb.Data(0x4444);
        rb.Cmd(0, 0x4004);
        ushort rd = rb.ReadData();
        Check("a read command right behind queued writes returns the written word ($3333 at $4004)", rd == 0x3333, $"{rd:X4}");

        // the status register
        r.Settle();
        ushort s = r.Status();
        Check("status: FIFO empty (bit 9), not full (bit 8), no DMA (bit 1), NTSC (bit 0 clear)", (s & 0x303) == 0x200, $"{s:X4}");
        var pal = new Rig(SegaRegion.Pal);
        Check("status on PAL: bit 0 set", (pal.Status() & 1) == 1);
        Check("a register write to a mode 5 register while in mode 4 is ignored (R11 with R1 bit 2 clear)", Mode4Ignored());
    }

    private static bool Mode4Ignored()
    {
        var r = new Rig();
        r.Reg(1, 0x00);
        r.Reg(11, 0x07);
        return r.V.Reg[11] == 0;
    }

    // =====================================================================================================================
    //  The FIFO and the unused bits of narrow reads
    // =====================================================================================================================

    private static void VdpFifoAndUnusedBits()
    {
        Group("VDP: the write FIFO and what narrow reads show of it (Nemesis' VDP port access test ROM, tests 1-9)");

        // capacity: four writes at one instant fill it, a fifth waits for a slot
        var r = new Rig();
        r.Reg(1, 0x64); r.Reg(12, 0x81); r.Reg(15, 2);
        r.Cmd(1, 0);
        r.Tick(); long t0 = r.T;
        for (int i = 0; i < 4; i++) r.V.WriteData((ushort)(0x1000 + i), t0);
        ushort full = r.V.ReadStatus(t0);
        Check("four writes at one instant: FIFO full (bit 8), not empty", (full & 0x300) == 0x100, $"{full:X4}");
        long before = t0;
        r.V.WriteData(0x1004, t0);
        Check("a fifth write waits for a free slot: the VDP's clock moved past the 68000's", r.V.Time > before && r.V.FifoStalls >= 1, $"time {r.V.Time - before}");
        r.T = Math.Max(r.T, r.V.Time);
        r.Settle();
        Check("all five words reached VRAM in order", r.V.Vram[0] == 0x10 && r.V.Vram[1] == 0x00 && r.V.Vram[8] == 0x10 && r.V.Vram[9] == 0x04);

        // Test 1: six writes, then CRAM and VSRAM reads show the FIFO's stale/next entry in their unused bits
        r = new Rig();
        r.Reg(1, 0x64); r.Reg(12, 0x81); r.Reg(15, 2);
        r.Cmd(1, 0x8000);
        foreach (ushort v in new ushort[] { 0x1111, 0x2222, 0x3333, 0x4444, 0x5555, 0x6666 }) r.Data(v);
        var got = new ushort[16];
        int k = 0;
        for (int round = 0; round < 8; round++)
        {
            r.Cmd(4, 0); got[k++] = r.ReadData(); r.Wait(10);
            r.Cmd(8, 0); got[k++] = r.ReadData(); r.Wait(10);
            r.Cmd(3, 0x20); r.Data(0xFFFF);
            r.Wait(10);
        }
        // the ROM's expected row (hardware-verified): 3000 3111 4000 4000 5000 5111 6000 6000 F800 F111 F800 F111 F800 F111 F800 F111
        ushort[] want = { 0x3000, 0x3111, 0x4000, 0x4000, 0x5000, 0x5111, 0x6000, 0x6000, 0xF800, 0xF111, 0xF800, 0xF111, 0xF800, 0xF111, 0xF800, 0xF111 };
        Check("FIFO Buffer Size (test 1): VSRAM reads show the next FIFO entry's top 5 bits, CRAM reads its bits outside $0EEE", Same(got, want), Hex(got));

        // Test 6: the undocumented 8-bit VRAM read target (code 01100): the byte at address^1 in the low byte, the FIFO entry's high byte above it
        r = new Rig();
        r.Reg(1, 0x64); r.Reg(12, 0x81); r.Reg(15, 2);
        r.Cmd(1, 0x8000);
        foreach (ushort v in new ushort[] { 0x1122, 0x3344, 0x5566, 0x7788, 0x99AA, 0xBBCC, 0xDDEE, 0x1234 }) r.Data(v);
        r.Settle();
        r.Cmd(0xC, 0x8000);
        ushort e1 = r.ReadData(), e2 = r.ReadData();
        Check("8-bit read target (test 6): $9922 $9944 at $8000 and $8002", e1 == 0x9922 && e2 == 0x9944, $"{e1:X4} {e2:X4}");
        r.Cmd(3, 0x20); r.Data(0xFFFF); r.Wait(10);
        r.Cmd(0xC, 0x8004);
        ushort e3 = r.ReadData(), e4 = r.ReadData();
        Check("... after the FIFO advances one entry: $BB66 $BB88", e3 == 0xBB66 && e4 == 0xBB88, $"{e3:X4} {e4:X4}");

        // Test 5: a data port write while a read target is set takes a FIFO entry and is lost; the row of the ROM (hardware-verified) is
        // 1000 1000 2000 2000 3000 3000 4000 4000 F800 F800 E800 E800 D800 D800 C800 C800
        r = new Rig();
        r.Reg(1, 0x64); r.Reg(12, 0x81); r.Reg(15, 2);
        r.Cmd(1, 0x8000);
        foreach (ushort v in new ushort[] { 0x1111, 0x2222, 0x3333, 0x4444 }) r.Data(v);
        r.Settle();
        var inv = new ushort[16];
        ushort[] invalids = { 0xFFFF, 0xEEEE, 0xDDDD, 0xCCCC, 0xBBBB, 0xAAAA, 0x9999 };
        for (int i = 0; i < 8; i++)
        {
            r.Cmd(4, 0); inv[2 * i] = r.ReadData(); inv[2 * i + 1] = r.ReadData(); r.Wait(10);
            if (i < invalids.Length) { r.Data(invalids[i]); r.Wait(10); }
        }
        ushort[] want5 = { 0x1000, 0x1000, 0x2000, 0x2000, 0x3000, 0x3000, 0x4000, 0x4000, 0xF800, 0xF800, 0xE800, 0xE800, 0xD800, 0xD800, 0xC800, 0xC800 };
        Check("write to an invalid target (test 5): each such write takes the next FIFO entry, which the next narrow read shows", Same(inv, want5), Hex(inv));
        Check("... and the lost words are written nowhere: VRAM at $8000 holds the first four real writes", r.V.Vram[0x8000] == 0x11 && r.V.Vram[0x8002] == 0x22 && r.V.Vram[0x8004] == 0x33 && r.V.Vram[0x8006] == 0x44);

        // Tests 8 and 9: CRAM/VSRAM addresses are word addresses: writes at an odd byte address land in the same entry as the even one
        r = new Rig();
        r.Reg(1, 0x64); r.Reg(12, 0x81); r.Reg(15, 2);
        r.Cmd(3, 0x21); r.Data(0x0246); r.Settle();
        Check("CRAM written at the odd byte address $21 lands in entry 16 (test 8)", r.V.Cram[16] == 0x0246 && r.V.Cram[17] == 0, $"{r.V.Cram[16]:X4}");
    }

    private static bool Same(ushort[] a, ushort[] b) { for (int i = 0; i < b.Length; i++) if (a[i] != b[i]) return false; return true; }
    private static string Hex(ushort[] a) { var sb = new StringBuilder(); foreach (var v in a) sb.Append($"{v:X4} "); return sb.ToString().Trim(); }

    // =====================================================================================================================
    //  Counters, flags and interrupts
    // =====================================================================================================================

    private static void VdpCountersAndInterrupts()
    {
        Group("VDP: V and H counters, blanking flags, VINT and HINT");
        var r = new Rig();
        r.Reg(1, 0x64); r.Reg(12, 0x81);
        // the V counter at the start of lines (read mid-line, before the step at VCounterStepOffset)
        bool ok = true; string why = "";
        foreach (var (line, want) in new[] { (0, 0), (100, 100), (0xEA, 0xEA), (0xEB, 0xE5), (261, 0xFF) })
        {
            r.RunTo(r.LineStart(line) + 1000);
            int v = r.V.ReadHvCounter(r.T) >> 8;
            if (v != want) { ok = false; why += $" line {line}: {v:X2}!={want:X2}"; }
        }
        Check("V counter, NTSC V28: counts $00-$EA then jumps back to $E5 and ends at $FF on line 261", ok, why);
        r.RunTo(r.LineStart(262 + 100) + 2900);
        Check("the V counter steps to the next line inside the horizontal blank (before the line ends)", r.V.ReadHvCounter(r.T) >> 8 == 101);
        r.RunTo(r.LineStart(524 + 120) + 8 * 100);
        int h = r.V.ReadHvCounter(r.T) & 0xFF;
        Check("H counter in H40 at the line start's pixel 100 (offset 800 from counter 0) reads $32 (the counter is offset/16)", h == 0x32, $"{h:X2}");
        r.RunTo(r.LineStart(524 + 121) + r.V.PixelOrigin + 8 * 100);
        h = r.V.ReadHvCounter(r.T) & 0xFF;
        Check("... and the picture's pixel 100 reads $3E: the first picture pixel is 12 counter ticks after the counter's 0 (BlastEm: a register write made at a given counter reading shows 24 pixels further left)", h == 0x3E, $"{h:X2}");

        var pal = new Rig(SegaRegion.Pal);
        pal.Reg(1, 0x64);
        bool okp = true;
        foreach (var (line, want) in new[] { (0, 0), (0x102, 0x102 & 0xFF), (0x103, 0xCA), (312, 0xFF) })
        {
            pal.RunTo(pal.LineStart(line) + 1000);
            if (pal.V.ReadHvCounter(pal.T) >> 8 != want) okp = false;
        }
        Check("V counter, PAL V28 (313 lines): $00-$02 wrapped through $102, then $CA-$FF", okp);

        // vertical blank, VINT
        r = new Rig();
        r.Reg(1, 0x64); r.Reg(12, 0x81);
        r.RunTo(r.LineStart(223) + 1000);
        bool beforeVbl = (r.V.ReadStatus(r.T) & 0x88) == 0;
        // the V counter steps to 224 at line 223's H counter $14A (clock 2640); the VBlank flag follows it by 33 clocks (oracle: status reads against HV reads, Track B4a)
        r.RunTo(r.LineStart(223) + 2640 + 10);
        ushort sv1 = r.V.ReadStatus(r.T); int v1 = r.V.ReadHvCounter(r.T) >> 8;
        r.RunTo(r.LineStart(223) + 2640 + 45);
        ushort sv2 = r.V.ReadStatus(r.T);
        Check("line 223: not in vertical blank; the V counter steps to 224 at clock 2640 with the VBlank flag still low (it rises 33 clocks later) and VINT not yet", beforeVbl && v1 == 224 && (sv1 & 0x88) == 0 && (sv2 & 0x88) == 0x08, $"{sv1:X4} {v1} {sv2:X4}");
        // VINT: 788 clocks (H40) after the step, which is 8 clocks into line 224
        r.RunTo(r.LineStart(224) + 4);
        ushort sv3 = r.V.ReadStatus(r.T); bool early = (sv3 & 0x80) == 0 && r.V.IplAt(r.T) == 0 && !r.Z80Int;
        r.RunTo(r.LineStart(224) + 20);
        Check("VINT arrives 788 master clocks after the V counter's step (8 clocks into line 224; the oracle's handler entry and flag agree to a few clocks): status bit 7, IPL 6, Z80 interrupt line up", early && (r.V.ReadStatus(r.T) & 0x80) != 0 && r.V.IplAt(r.T) == 6 && r.Z80Int, $"{sv3:X4}");
        r.RunTo(r.LineStart(224) + 8 + 2560);
        bool z80Up = r.Z80Int;
        r.RunTo(r.LineStart(224) + 8 + 2590);
        Check("the Z80 interrupt line stays up 2573 master clocks (171.5 Z80 cycles, as the oracle's re-entry counts show) from the VINT and then drops, within line 224", z80Up && !r.Z80Int, $"{z80Up} {r.Z80Int}");
        r.RunTo(r.LineStart(225) + 100);
        r.V.AcknowledgeInterrupt(6);
        Check("the acknowledge clears the pending VINT (status bit 7 and the IPL)", (r.V.ReadStatus(r.T) & 0x80) == 0 && r.V.IplAt(r.T) == 0);
        r.RunTo(r.LineStart(262) + 100);
        Check("the vertical blank flag clears at the start of the next frame", (r.V.ReadStatus(r.T) & 0x08) == 0);
        r = new Rig();
        r.Reg(1, 0x6C); r.Reg(12, 0x81);        // V30
        r.RunTo(r.LineStart(230) + 100);
        Check("V30 (R1 bit 3): the vertical blank begins at line 240, not 224", (r.V.ReadStatus(r.T) & 0x08) == 0);
        r.RunTo(r.LineStart(240) + 100);
        Check("... line 240 is blanking", (r.V.ReadStatus(r.T) & 0x08) != 0);

        // HINT: the counter counts lines; with R10 = 3 the interrupt comes every fourth line
        r = new Rig();
        r.Reg(0, 0x14); r.Reg(10, 3); r.Reg(1, 0x64); r.Reg(12, 0x81);
        var lines = new System.Collections.Generic.List<int>();
        for (int line = 0; line < 20; line++)
        {
            r.RunTo(r.LineStart(line) + 3000);
            if (r.V.IplAt(r.T) == 4) { lines.Add(line); r.V.AcknowledgeInterrupt(4); }
        }
        Check("HINT with R10 = 3 fires every fourth line", lines.Count >= 4 && lines[1] - lines[0] == 4 && lines[2] - lines[1] == 4, string.Join(",", lines));
        // the lines the horizontal interrupt fires on, measured on BlastEm (handlers reading the V counter, R10 = 0, 1, 3, 224, 225): the counter counts the pre-render line and lines 0 to 223; R10 = n first fires at the
        // end of line n - 1, then every n + 1 lines; 0 is every line from the pre-render line on; above 224 never
        System.Collections.Generic.List<int> HintLines(int r10)
        {
            var q = new Rig();
            q.Reg(1, 0x44); q.Reg(0, 0x14); q.Reg(10, r10); q.Reg(12, 0x81);
            var got = new System.Collections.Generic.List<int>();
            for (int line = 0; line < 262 * 2; line++)
            {
                q.RunTo(q.LineStart(line) + 3000);
                if (line >= 262 && q.V.IplAt(q.T) == 4) got.Add(line - 262);
                if (q.V.IplAt(q.T) == 4) q.V.AcknowledgeInterrupt(4);
            }
            return got;
        }
        var h0 = HintLines(0); var h1 = HintLines(1); var h3 = HintLines(3); var h224 = HintLines(224); var h225 = HintLines(225);
        Check("HINT, R10 = 0: every line from the pre-render line (261) through 223 (225 interrupts), none on 224-260", h0.Count == 225 && h0.Contains(261) && h0.Contains(0) && h0.Contains(223) && !h0.Contains(224) && !h0.Contains(260), $"{h0.Count}");
        Check("HINT, R10 = 1: lines 0, 2, 4 ... 222 (the first at the end of line n - 1 = 0; the pre-render line only starts the count)", h1.Count == 112 && h1[0] == 0 && h1[1] == 2 && h1[^1] == 222, $"{h1.Count} {string.Join(",", h1.Take(3))}");
        Check("HINT, R10 = 3: the first at the end of line 2, then every fourth line", h3.Count == 56 && h3[0] == 2 && h3[1] == 6 && h3[^1] == 222, $"{h3.Count} {string.Join(",", h3.Take(3))}");
        Check("HINT, R10 = 224: once, at the end of line 223; R10 = 225: never (the counter never gets there)", h224.Count == 1 && h224[0] == 223 && h225.Count == 0, $"{h224.Count} {h225.Count}");

        // the flags, as the oracle's status reads show them (H40: V step 2640, VBlank flag 33 later, HBlank flag from 2855 to the end of the line; H32: 2660, 49, 2975)
        var fl = new Rig(); fl.Reg(1, 0x44); fl.Reg(12, 0x81);
        fl.RunTo(fl.LineStart(100) + 2850); bool hb1 = (fl.V.ReadStatus(fl.T) & 4) != 0;
        fl.RunTo(fl.LineStart(100) + 2865); bool hb2 = (fl.V.ReadStatus(fl.T) & 4) != 0;
        fl.RunTo(fl.LineStart(101) + 5); bool hb3 = (fl.V.ReadStatus(fl.T) & 4) != 0;
        Check("the horizontal blank flag (H40) is set from clock 2855 of a line and falls at the next line's start", !hb1 && hb2 && !hb3, $"{hb1} {hb2} {hb3}");
        var f32 = new Rig(); f32.Reg(1, 0x44); f32.Reg(12, 0x00);
        f32.RunTo(f32.LineStart(100) + 2650); int vs1 = f32.V.ReadHvCounter(f32.T) >> 8;
        f32.RunTo(f32.LineStart(100) + 2670); int vs2 = f32.V.ReadHvCounter(f32.T) >> 8;
        f32.RunTo(f32.LineStart(100) + 2970); bool h32a = (f32.V.ReadStatus(f32.T) & 4) != 0;
        f32.RunTo(f32.LineStart(100) + 2985); bool h32b = (f32.V.ReadStatus(f32.T) & 4) != 0;
        Check("H32: the V counter steps at clock 2660 (H counter $10A) and the horizontal blank flag is set from 2975", vs1 == 100 && vs2 == 101 && !h32a && h32b, $"{vs1} {vs2} {h32a} {h32b}");
        var fc = new Rig();
        fc.Reg(0, 0x14); fc.Reg(10, 9); fc.Reg(1, 0x64);
        int fired = 0;
        for (int line = 0; line < 262; line++)
        {
            fc.RunTo(fc.LineStart(line) + 3000);
            if (fc.V.IplAt(fc.T) == 4) { fired++; fc.V.AcknowledgeInterrupt(4); }
        }
        Check("HINT with R10 = 9: 22 or 23 interrupts across the 225 counted lines of a frame", fired is 22 or 23, $"{fired}");
        var off = new Rig();
        off.Reg(0, 0x04); off.Reg(10, 0); off.Reg(1, 0x64);
        off.RunTo(off.LineStart(10));
        Check("HINT disabled (R0 bit 4 clear): IPL stays 0", off.V.IplAt(off.T) == 0);
        // whether a line's HINT is latched is decided by R0 bit 4 as it was at about the previous line's HINT time (clock 2642 of the line before): B4b's probe run_irqdelay.py H, and OutRunners and Super Hang-On on the oracle
        var hk = new Rig();
        hk.Reg(0, 0x04); hk.Reg(10, 0); hk.Reg(1, 0x64);
        hk.RunTo(hk.LineStart(100) + 1000);
        hk.Reg(0, 0x14);
        hk.RunTo(hk.LineStart(100) + 3000);
        Check("HINT enabled in the display period of line 100 after a disabled stretch: line 100's own HINT (decided in line 99, disabled) is lost", hk.V.IplAt(hk.T) == 0);
        hk.RunTo(hk.LineStart(101) + 3000);
        Check("... line 101's (decided in line 100 with HINT enabled) is taken", hk.V.IplAt(hk.T) == 4);
        var ho = new Rig();
        ho.Reg(0, 0x14); ho.Reg(10, 0); ho.Reg(1, 0x64);
        ho.RunTo(ho.LineStart(119) + 3380);
        ho.Reg(0, 0x04);
        ho.RunTo(ho.LineStart(125));
        bool quiet = ho.V.IplAt(ho.T) == 0;
        ho.Reg(0, 0x14);
        ho.RunTo(ho.T + 600);
        Check("HINT disabled at the very end of line 119 (Super Hang-On: clock 3363 to 3417 of line 222): line 120's HINT, decided before, is kept and taken when HINT is enabled again", quiet && ho.V.IplAt(ho.T) == 4, $"{quiet} {ho.V.IplAt(ho.T)}");
        var hp = new Rig();
        hp.Reg(0, 0x14); hp.Reg(10, 0); hp.Reg(1, 0x64);
        hp.RunTo(hp.LineStart(119) + 2000);
        hp.Reg(0, 0x04);
        hp.RunTo(hp.LineStart(125));
        hp.V.AcknowledgeInterrupt(4);                      // the one decided before the write (line 119's) was kept; take it away
        hp.Reg(0, 0x14);
        hp.RunTo(hp.T + 600);
        Check("HINT disabled in the middle of line 119 (before its HINT time): the HINTs of lines 120 to 124 are lost, nothing waits when HINT is enabled again (only line 119's, decided before, did)", hp.V.IplAt(hp.T) == 0);
        var hd = new Rig();
        hd.Reg(0, 0x14); hd.Reg(10, 0); hd.Reg(1, 0x64);
        hd.RunTo(hd.LineStart(120) + 290);
        hd.Reg(0, 0x04);
        hd.RunTo(hd.LineStart(120) + 3000);
        bool quiet2 = hd.V.IplAt(hd.T) == 0;
        hd.Reg(0, 0x14);
        hd.RunTo(hd.T + 600);
        Check("HINT disabled early in line 120 (OutRunners: clock 290 of line 223): its HINT, decided in line 119, is kept and taken when HINT is enabled again", quiet2 && hd.V.IplAt(hd.T) == 4, $"{quiet2} {hd.V.IplAt(hd.T)}");

    }

    // =====================================================================================================================
    //  DMA
    // =====================================================================================================================

    private static void VdpDma()
    {
        Group("VDP: DMA (68000 to VDP, fill, copy) and its timing");
        var r = new Rig();
        r.Reg(1, 0x74); r.Reg(12, 0x81); r.Reg(15, 2);          // display on, DMA enabled
        for (int i = 0; i < 16; i++) { r.Mem[0x1000 + i] = (byte)(0xA0 + i); }
        r.Reg(1, 0x34);        // display off for the fast case: every slot but refresh is free
        r.Reg(19, 8); r.Reg(20, 0); r.Reg(21, (0x1000 >> 1) & 0xFF); r.Reg(22, (0x1000 >> 1) >> 8 & 0xFF); r.Reg(23, 0);
        r.Cmd(0x21, 0x0400);
        Check("a bus DMA command starts: DMA busy (status bit 1), the 68000 held", r.V.DmaHaltsCpu && (r.V.ReadStatus(r.T) & 2) != 0);
        r.Settle();
        bool same = true;
        for (int i = 0; i < 16; i++) if (r.V.Vram[0x400 + i] != 0xA0 + i) same = false;
        Check("68000-to-VRAM DMA moved 8 words to $0400 in order", same);
        Check("the length registers read 0 and the source registers point past the data", r.V.Reg[19] == 0 && r.V.Reg[20] == 0 && r.V.Reg[21] == (((0x1000 + 16) >> 1) & 0xFF), $"R21 {r.V.Reg[21]:X2}");
        Check("DMA busy is clear again", (r.V.ReadStatus(r.T) & 2) == 0 && !r.V.DmaHaltsCpu);
        Check("the DMA's words went through the FIFO ring: the next entry the VDP would write holds data from the transfer (CRAM read shows its top bits)", DmaWordsInRing());

        // timing: during the display a VRAM word needs two of the 18 free slots per line; in vertical blank 205 slots are free
        var act = new Rig();
        act.Reg(1, 0x74); act.Reg(12, 0x81); act.Reg(15, 2);
        act.Reg(19, 90); act.Reg(20, 0); act.Reg(21, 0); act.Reg(22, 0x08); act.Reg(23, 0);
        act.RunTo(act.LineStart(10));
        long startT = act.T;
        act.Cmd(0x21, 0);
        act.Settle();
        double linesTaken = (act.T - startT) / (double)MdVdp.ClocksPerLine;
        Check("DMA of 90 words to VRAM during the display in H40 takes about 10 lines (9 words a line)", linesTaken is > 8.5 and < 12.5, $"{linesTaken:F1} lines");
        var blank = new Rig();
        blank.Reg(1, 0x74); blank.Reg(12, 0x81); blank.Reg(15, 2);
        blank.Reg(19, 90); blank.Reg(20, 0); blank.Reg(21, 0); blank.Reg(22, 0x08); blank.Reg(23, 0);
        blank.RunTo(blank.LineStart(230));
        long startB = blank.T;
        blank.Cmd(0x21, 0);
        blank.Settle();
        double linesBlank = (blank.T - startB) / (double)MdVdp.ClocksPerLine;
        Check("... and under one line during vertical blank (about 100 words a line)", linesBlank < 1.2, $"{linesBlank:F2} lines");

        // one word a slot into CRAM in a blanked H40 line: 198 words a line, not the 204 free slots (the oracle: cram_rate.py; the direct colour demo is pixel-identical only with exactly 198)
        var cr = new Rig();
        cr.Reg(1, 0x34); cr.Reg(12, 0x81); cr.Reg(15, 2);            // display off: every line is blanked
        cr.Reg(19, 0xA0); cr.Reg(20, 0x0F); cr.Reg(21, 0); cr.Reg(22, 0x08); cr.Reg(23, 0);       // 4000 words from $1000
        cr.RunTo(cr.LineStart(30));
        long startC = cr.T;
        cr.Cmd(0x23, 0);
        cr.Settle();
        double linesCram = (cr.T - startC) / (double)MdVdp.ClocksPerLine;
        Check("DMA of 4000 words into CRAM in a blanked H40 line writes 198 words a line (about 20.3 lines; 204 a line would be 19.7)", linesCram is > 20.0 and < 20.6, $"{linesCram:F2} lines");
        var vr = new Rig();
        vr.Reg(1, 0x34); vr.Reg(12, 0x81); vr.Reg(15, 2);
        vr.Reg(19, 0xA0); vr.Reg(20, 0x0F); vr.Reg(21, 0); vr.Reg(22, 0x08); vr.Reg(23, 0);
        vr.RunTo(vr.LineStart(30));
        long startV = vr.T;
        vr.Cmd(0x21, 0);
        vr.Settle();
        double linesVram = (vr.T - startV) / (double)MdVdp.ClocksPerLine;
        Check("... while the same transfer into VRAM (two slots a word) is not slowed by it: the 68000 is let go after about 38.5 lines (it is let go at the last fetch, a FIFO ahead of the last write)", linesVram is > 38.2 and < 38.9, $"{linesVram:F2} lines");

        // fill: the data word that starts it is an ordinary write, the rest repeat its high byte at address^1 (Nemesis: DMA Fill FIFO Usage reads 1234 then 12 12 ...)
        var f = new Rig();
        f.Reg(1, 0x74); f.Reg(12, 0x81); f.Reg(15, 1);
        f.Reg(19, 6); f.Reg(20, 0); f.Reg(23, 0x80);
        f.Cmd(0x21, 0x0800);
        f.Data(0x1234);
        f.Settle();
        string fill = "";
        for (int i = 0; i < 8; i++) fill += f.V.Vram[0x800 + i].ToString("X2");
        // the word $1234 goes to $0800 and the address moves to $0801; the six fill bytes (the high byte, $12) go to address^1: $0800, $0803, $0802, $0805, $0804, $0807 (Nemesis' fill test shows the same shape)
        Check("VRAM fill with autoincrement 1 and length 6: bytes 12 34 12 12 12 12 00 12", fill == "1234121212120012", fill);
        Check("the fill's length register reads 0 afterwards", f.V.Reg[19] == 0 && f.V.Reg[20] == 0);

        // after a fill the source address registers have moved on by the length (Nemesis: DMA Fill Source Reg Update)
        var fs = new Rig();
        fs.Reg(1, 0x74); fs.Reg(12, 0x81); fs.Reg(15, 1);
        fs.Reg(19, 4); fs.Reg(20, 0); fs.Reg(21, 0xFA); fs.Reg(22, 0x00); fs.Reg(23, 0x80);
        fs.Cmd(0x21, 0x0800); fs.Data(0x1234); fs.Settle();
        Check("a fill of 4 moves the source registers from $00FA to $00FE", fs.V.Reg[21] == 0xFE && fs.V.Reg[22] == 0x00, $"{fs.V.Reg[22]:X2}{fs.V.Reg[21]:X2}");
        fs.Reg(19, 4); fs.Reg(21, 0xFE); fs.Reg(22, 0x00);
        fs.Cmd(0x21, 0x0900); fs.Data(0x1234); fs.Settle();
        Check("... with the carry into the middle byte ($00FE + 4 = $0102)", fs.V.Reg[21] == 0x02 && fs.V.Reg[22] == 0x01, $"{fs.V.Reg[22]:X2}{fs.V.Reg[21]:X2}");
        // CRAM fills repeat the data left in the next FIFO entry, not the word that started them
        var cf = new Rig();
        cf.Reg(1, 0x74); cf.Reg(12, 0x81); cf.Reg(15, 2);
        cf.Cmd(3, 0x00); foreach (ushort v in new ushort[] { 0x0A06, 0x0C04, 0x0E02, 0x0200 }) cf.Data(v);
        cf.Settle();
        cf.Reg(19, 3); cf.Reg(20, 0); cf.Reg(23, 0x80);
        cf.Cmd(0x23, 0x20); cf.Data(0x08AC); cf.Settle();
        Check("a CRAM fill writes the first word as given and then the stale next FIFO entry ($0C04)", cf.V.Cram[16] == 0x08AC && cf.V.Cram[17] == 0x0C04 && cf.V.Cram[18] == 0x0C04, $"{cf.V.Cram[16]:X4} {cf.V.Cram[17]:X4} {cf.V.Cram[18]:X4}");
        // a data port write during a fill changes the byte it repeats; the 68000 is not held
        var fd = new Rig();
        fd.Reg(1, 0x74); fd.Reg(12, 0x81); fd.Reg(15, 1);
        fd.Reg(19, 40); fd.Reg(20, 0); fd.Reg(23, 0x80);
        fd.Cmd(0x21, 0x0800); fd.Data(0x1234);
        long before2 = fd.T;
        fd.Tick(8);
        fd.V.WriteData(0x5678, fd.T);
        Check("a fill does not hold the 68000: the port write returned at once (the clock did not jump)", fd.V.Time - before2 < 200 * 7 && fd.V.DmaBusy);
        fd.Settle();
        Check("... and the bytes after it are $56 (the new high byte)", fd.V.Vram[0x800 + 40] == 0x56 || fd.V.Vram[0x800 + 39] == 0x56);
        // the HV counter latch (R0 bit 1) freezes the counter
        var hv = new Rig();
        hv.Reg(0, 0x06); hv.Reg(1, 0x64); hv.Reg(12, 0x81);
        hv.RunTo(hv.LineStart(50) + 1000);
        ushort h1 = hv.V.ReadHvCounter(hv.T);
        hv.RunTo(hv.LineStart(60) + 1500);
        ushort h2 = hv.V.ReadHvCounter(hv.T);
        hv.Reg(0, 0x04);
        hv.RunTo(hv.LineStart(61) + 2000);
        ushort h3 = hv.V.ReadHvCounter(hv.T);
        Check("with R0 bit 1 set the HV counter stops; with it clear it runs again (Nemesis: HV Counter Latch)", h1 == h2 && h3 != h2, $"{h1:X4} {h2:X4} {h3:X4}");

        // copy: VRAM to VRAM bytes
        var c = new Rig();
        c.Reg(1, 0x74); c.Reg(12, 0x81); c.Reg(15, 2);
        c.Cmd(1, 0x1000); c.Data(0x0102); c.Data(0x0304); c.Settle();
        c.Reg(15, 1); c.Reg(19, 4); c.Reg(20, 0); c.Reg(21, 0x00); c.Reg(22, 0x10); c.Reg(23, 0xC0);
        c.Cmd(0x31, 0x2000);
        c.Settle();
        // a copy moves bytes in the VDP's byte order: source and destination addresses with their low bit flipped (Nemesis: DMA Copy Length Reg Update: an odd length ends on the odd byte)
        var oc = new Rig();
        oc.Reg(1, 0x34); oc.Reg(12, 0x81); oc.Reg(15, 2);       // display off: every slot but refresh is free, so the copy is quick
        oc.Cmd(1, 0x9200); oc.Data(0x0123); oc.Settle();
        oc.Reg(15, 1); oc.Reg(19, 0x01); oc.Reg(20, 0x02); oc.Reg(21, 0x00); oc.Reg(22, 0x90); oc.Reg(23, 0xC0);
        oc.Cmd(0x30, 0x8000); oc.Settle();
        Check("a copy of $201 bytes: the 513th lands on $8201 with the byte from $9201", oc.V.Vram[0x8201] == 0x23 && oc.V.Vram[0x8200] == 0x00, $"{oc.V.Vram[0x8200]:X2}{oc.V.Vram[0x8201]:X2}");
        // a DMA copy that starts in the last 56 clocks of a line (its engine starts 56 clocks later, in the next line): the search for the next free slot went back to the start of that line and found a slot
        // before the engine's start over and over, so the VDP never got on (D&D - Warriors of the Eternal Sun froze at its frame 541). Run in a thread so that a failure is a failed check, not a hung test.
        string hangAt = "";
        for (int d = 0; d < 70 && hangAt == ""; d++)
        {
            var hs = new Rig(); hs.T = hs.LineStart(210) - 600; hs.V.RunTo(hs.T);
            hs.Reg(1, 0x34); hs.Reg(12, 0x81); hs.Reg(15, 1); hs.Reg(19, 0x50); hs.Reg(20, 0x00); hs.Reg(21, 0x00); hs.Reg(22, 0xFC); hs.Reg(23, 0xC0);
            hs.Ctl((ushort)(0x4000 | 0xC380 & 0x3FFF));              // the first control word of the command: the address and the VRAM write code
            hs.T = hs.LineStart(211) - 70 + d;                        // the second word, which holds the DMA bit, arrives (70 - d) clocks before the end of line 210 (28 clocks more for its own bus cycle)
            bool ok = false;
            var th = new System.Threading.Thread(() => { hs.Ctl((ushort)(0x0080 | 0xC380 >> 14 & 3)); for (int k = 212; k < 220; k++) hs.RunTo(hs.LineStart(k)); ok = true; }) { IsBackground = true };
            th.Start();
            if (!th.Join(1500) || !ok) hangAt = $"{d - 70} clocks from the line end";
        }
        Check("a DMA copy started in the last 56 clocks of a blanked line goes on in the next line (the slot search used to freeze there: D&D - Warriors of the Eternal Sun)", hangAt == "", hangAt);        Check("VRAM copy: four bytes from $1000 to $2000", c.V.Vram[0x2000] == 0x01 && c.V.Vram[0x2001] == 0x02 && c.V.Vram[0x2002] == 0x03 && c.V.Vram[0x2003] == 0x04, $"{c.V.Vram[0x2000]:X2}{c.V.Vram[0x2001]:X2}{c.V.Vram[0x2002]:X2}{c.V.Vram[0x2003]:X2}");
    }

    private static bool DmaWordsInRing()
    {
        var r = new Rig();
        r.Reg(1, 0x34); r.Reg(12, 0x81); r.Reg(15, 2);
        for (int i = 0; i < 12; i += 2) { r.Mem[0x1000 + i] = (byte)(0x10 * (i / 2 + 1)); r.Mem[0x1001 + i] = 0x0F; }
        r.Reg(19, 6); r.Reg(20, 0); r.Reg(21, 0x00); r.Reg(22, 0x08); r.Reg(23, 0);
        r.Cmd(0x21, 0x0400);
        r.Settle();
        // six words 100F 200F 300F 400F 500F 600F: the next entry holds the third (300F): a CRAM read shows $3000 | ($300F & $F111 = $3001) -- the unused bits only
        r.Cmd(8, 0);
        ushort v = r.ReadData();
        return (v & ~0x0EEE) == (0x300F & ~0x0EEE & 0xFFFF);
    }

    // =====================================================================================================================
    //  Timing on the master-clock timeline
    // =====================================================================================================================

    private static void Timing()
    {
        Group("timing: the master clock, the 68000 and Z80 dividers, frame length");
        var b = Board(MakeRom("JUE", 0x60FE));
        long t0 = b.MasterClock;
        for (int i = 0; i < 1000; i++) R16(b, 0x1000);      // 1000 four-cycle bus reads
        long dt = b.MasterClock - t0;
        long refresh = 1000L * 4 / BOARD_MD.RefreshPeriodCycles * BOARD_MD.RefreshStallCycles * BOARD_MD.ClocksPer68k;
        long expect = 1000L * 4 * 7 + refresh;
        Check("1000 four-cycle reads take 28 master clocks each plus the DRAM refresh stalls", Math.Abs(dt - expect) <= 3 * 14 * BOARD_MD.ClocksPer68k, $"{dt} vs {expect}");

        for (int f = 0; f < 3; f++) b.RunFrame();
        long frameClocks = b.Timeline.MasterClocksPerFrame;
        Check("three frames of an NTSC board: 3 x 262 x 3420 master clocks", Math.Abs(b.MasterClock - t0 - 3 * frameClocks) < 2 * MdVdp.ClocksPerLine && frameClocks == 262 * 3420L, $"{b.MasterClock - t0} vs {3 * frameClocks}");
        Check("the VDP counted the frames", b.Vdp.Frames == 3);
        var pal = Board(MakeRom("E"), SegaRegion.Pal);
        pal.RunFrame();
        Check("a PAL board's frame is 313 lines", pal.Timeline.MasterClocksPerFrame == 313 * 3420L && pal.Vdp.Frames == 1);

        // the Z80 runs at master/15 while it is released: one frame
        var z = Board();
        W16(z, 0xA11200, 0x0100);       // release reset, bus never requested
        z.RunFrame();
        var unit = (MdSoundUnit)z.Sound;
        long expected = 262L * 3420 / 15;
        Check("the Z80 ticks once per 15 master clocks while it runs (one NTSC frame)", Math.Abs(unit.Z80Ticks - expected) < 200, $"{unit.Z80Ticks} vs {expected}");
    }

    // =====================================================================================================================
    //  Where an interrupt is taken (Track B4b: histograms of the oracle, BlastEm 1.0.0)
    // =====================================================================================================================

    /// <summary>
    /// A horizontal interrupt every line while the 68000 runs a loop of <paramref name="body"/> and a BRA.S back. The handler logs the address it was given to return to (the first instruction NOT executed). Returns the share
    /// of the interrupts that return to the BRA (they were taken after the body) and how many were taken.
    /// </summary>
    private static (double afterBody, int count) InterruptAfterBody(ushort[] body, int bodyBytes)
    {
        var code = new System.Collections.Generic.List<ushort>
        {
            0x41F9, 0x00FF, 0x0000,                     // lea ($FF0000).l,a0     (the log)
            0x4DF9, 0x00FF, 0x8000,                     // lea ($FF8000).l,a6     (a place for MOVEM to write)
            0x33FC, 0x8014, 0x00C0, 0x0004,             // move.w #$8014,($C00004).l   R0: horizontal interrupt on
            0x33FC, 0x8144, 0x00C0, 0x0004,             // R1: display on, no vertical interrupt
            0x33FC, 0x8A00, 0x00C0, 0x0004,             // R10 = 0: every line
            0x46FC, 0x2000,                             // move.w #$2000,sr
        };
        int loop = 0x200 + code.Count * 2;
        code.AddRange(body);
        int disp = -(bodyBytes + 2);
        code.Add((ushort)(0x6000 | (disp & 0xFF)));      // bra.s loop
        var rom = MakeRom("JUE", code.ToArray());
        const int handler = 0x500;
        rom[0x70] = 0; rom[0x71] = 0; rom[0x72] = (byte)(handler >> 8 & 0xFF); rom[0x73] = (byte)(handler & 0xFF);       // level 4 autovector
        ushort[] h = { 0x20EF, 0x0002, 0x4E73 };         // move.l 2(sp),(a0)+ ; rte
        for (int i = 0; i < h.Length; i++) { rom[handler + i * 2] = (byte)(h[i] >> 8); rom[handler + i * 2 + 1] = (byte)h[i]; }
        var b = Board(rom);
        for (int f = 0; f < 24; f++) b.RunFrame();
        int n = (int)((b.Cpu.A[0] - 0xFF0000) / 4), afterBody = 0;
        for (int i = 0; i < n; i++)
        {
            uint ret = (uint)(b.Ram[i * 4] << 24 | b.Ram[i * 4 + 1] << 16 | b.Ram[i * 4 + 2] << 8 | b.Ram[i * 4 + 3]);
            ret &= 0xFFFFFF;
            if (ret == loop + bodyBytes) afterBody++;
        }
        return (n == 0 ? 0 : afterBody / (double)n, n);
    }

    private static void InterruptSamplePoints()
    {
        Group("68000: where a horizontal interrupt that comes every line is taken in a small loop (the shares BlastEm 1.0.0 gives: tools run_rethist.py)");
        int savedMask = M68k<MdBus>.BranchIplMask; bool savedMovem = M68k<MdBus>.MovemFreezesIpl;
        try
        {
            // NOP + BRA.S: BlastEm takes 78.9 % of the interrupts after the NOP (the branch samples about where it starts, so the NOP's share is the 10 of the loop's 14 cycles that follow the branch's sample, plus about one more)
            var (nop, nopCount) = InterruptAfterBody(new ushort[] { 0x4E71 }, 2);
            Check("NOP + BRA.S loop: about three quarters of the interrupts are taken after the NOP (BlastEm 0.789)", nopCount > 4000 && nop is > 0.66 and < 0.84, $"{nop:F3} of {nopCount}");
            // MOVEM.L d0-d7,(a6) + BRA.S: BlastEm takes 12.2 % after the MOVEM (the lines are sampled where it starts, not at the end of its 72 cycles of writes)
            var (movem, movemCount) = InterruptAfterBody(new ushort[] { 0x48D6, 0x00FF }, 4);
            Check("MOVEM to memory + BRA.S loop: about an eighth of the interrupts are taken after the MOVEM (BlastEm 0.122)", movemCount > 4000 && movem is > 0.08 and < 0.18, $"{movem:F3} of {movemCount}");
            // the same loops with the old rules (the sample at the start of the last bus cycle, a taken branch included): the checks above must fail
            M68k<MdBus>.BranchIplMask = 0; M68k<MdBus>.MovemFreezesIpl = false;
            var (nopOld, _) = InterruptAfterBody(new ushort[] { 0x4E71 }, 2);
            var (movemOld, _) = InterruptAfterBody(new ushort[] { 0x48D6, 0x00FF }, 4);
            Check("... and the old sampling (last bus cycle of every instruction) misses both windows, so the checks can fail", !(nopOld is > 0.66 and < 0.84) && !(movemOld is > 0.08 and < 0.18), $"nop {nopOld:F3}, movem {movemOld:F3}");
        }
        finally { M68k<MdBus>.BranchIplMask = savedMask; M68k<MdBus>.MovemFreezesIpl = savedMovem; }
    }


    // =====================================================================================================================
    //  Loops of VDP port reads against the oracle's laps (Track B4b, wave 5)
    // =====================================================================================================================

    /// <summary>Laps a second frame of loop <c>per x move.w (a2),d1 ; addq.l #1,(a0) ; bra.w</c> (a2 = the VDP's control port, display off, H40, no interrupts) averaged over 300 frames after 100.</summary>
    private static double VdpReadLoopLapsPerFrame(int per)
    {
        var code = new System.Collections.Generic.List<ushort>
        {
            0x41F9, 0x00FF, 0x0000,                     // lea ($FF0000).l,a0
            0x45F9, 0x00C0, 0x0004,                     // lea ($C00004).l,a2
            0x33FC, 0x8134, 0x00C0, 0x0004,             // R1: display off, DMA on
            0x33FC, 0x8C81, 0x00C0, 0x0004,             // R12: H40
            0x20BC, 0x0000, 0x0000,                     // move.l #0,(a0)
        };
        for (int i = 0; i < per; i++) code.Add(0x3212);        // move.w (a2),d1
        code.Add(0x5290);                                       // addq.l #1,(a0)
        code.Add(0x6000); code.Add((ushort)(-(per * 2 + 4) & 0xFFFF));       // bra.w loop
        var b = Board(MakeRom("JUE", code.ToArray()));
        for (int f = 0; f < 100; f++) b.RunFrame();
        long c1 = (long)(uint)(b.Ram[0] << 24 | b.Ram[1] << 16 | b.Ram[2] << 8 | b.Ram[3]);
        for (int f = 0; f < 300; f++) b.RunFrame();
        long c2 = (long)(uint)(b.Ram[0] << 24 | b.Ram[1] << 16 | b.Ram[2] << 8 | b.Ram[3]);
        return (c2 - c1) / 300.0;
    }

    private static void VdpPortLoops()
    {
        Group("68000: loops of VDP port reads run at the oracle's rate (laps a frame, BlastEm 1.0.0: tools rate.py, probes_b4b2.py)");
        // the oracle's laps a frame with its vertical interrupt handler running (1, 2 and 16 reads a lap: 3,329.29, 2,746.79, 799.07), without the handler's share of the lap (about 0.3, 0.36 and 1.25 master clocks):
        // 3,333.0, 2,749.8 and 800.0. The cycle counts alone give 3,328.9 less 0.3 %: the oracle's loops are faster than that and the 4-clock grid of the VDP cycles (BOARD_MD.VdpQuantum) is what makes them equal.
        int[] per = { 1, 2, 16 }; double[] oracle = { 3333.0, 2749.8, 800.0 };
        int saved = BOARD_MD.VdpQuantum;
        try
        {
            for (int i = 0; i < per.Length; i++)
            {
                double laps = VdpReadLoopLapsPerFrame(per[i]);
                Check($"{per[i]} VDP status read{(per[i] > 1 ? "s" : "")} + addq + bra: {oracle[i]:F1} laps a frame as on the oracle (within 0.15 %)", Math.Abs(laps / oracle[i] - 1) < 0.0015, $"{laps:F2}");
            }
            BOARD_MD.VdpQuantum = 0;
            double off = VdpReadLoopLapsPerFrame(16);
            Check("... and without the grid the 16-read loop is more than 0.15 % off, so the check can fail", Math.Abs(off / 800.0 - 1) > 0.0015, $"{off:F2}");
        }
        finally { BOARD_MD.VdpQuantum = saved; }
    }

    // =====================================================================================================================
    //  The cartridge
    // =====================================================================================================================

    private static void Cartridge()
    {
        Group("the cartridge: header, battery SRAM, the $A130F1 toggle");
        var rom = MakeRom("JUE");
        // SRAM header: "RA", $F8 $20 (battery, odd bytes), start $200001, end $203FFF
        rom[0x1B0] = (byte)'R'; rom[0x1B1] = (byte)'A'; rom[0x1B2] = 0xF8; rom[0x1B3] = 0x20;
        rom[0x1B4] = 0x00; rom[0x1B5] = 0x20; rom[0x1B6] = 0x00; rom[0x1B7] = 0x01;
        rom[0x1B8] = 0x00; rom[0x1B9] = 0x20; rom[0x1BA] = 0x3F; rom[0x1BB] = 0xFF;
        var b = Board(rom);
        var cart = b.Cart;
        Check("header: titles, serial, regions and the SRAM description are read", cart.OverseasTitle == "BOARD TEST ROM" && cart.Serial.StartsWith("GM 00000000") && cart.Regions == "JUE" && cart.HasSramHeader && cart.HasBattery, $"{cart.OverseasTitle}|{cart.Serial}|{cart.Regions}|{cart.Sram.Length}");
        Check("SRAM of an odd-byte cartridge holds one byte per word ($2000 bytes for $4000 of address space)", cart.Sram.Length == 0x2000, $"{cart.Sram.Length}");
        Check("a ROM of 128 KB has its SRAM mapped at power-on", cart.SramMapped);
        W8(b, 0x200001, 0x77); W8(b, 0x200003, 0x88);
        Check("SRAM bytes written at odd addresses read back", R8(b, 0x200001) == 0x77 && R8(b, 0x200003) == 0x88);
        Check("... and the even bytes of the window read as $FF-ish open lines, not RAM", R8(b, 0x200000) != 0x77);
        W8(b, 0xA130F1, 0x00);
        Check("$A130F1 = 0 maps the ROM back over the window (the SRAM bytes disappear)", R8(b, 0x200001) != 0x77);
        W8(b, 0xA130F1, 0x01);
        Check("$A130F1 = 1 maps the SRAM again with its contents", R8(b, 0x200001) == 0x77);
        W8(b, 0xA130F1, 0x03);
        W8(b, 0x200001, 0x11);
        Check("$A130F1 bit 1 write-protects the SRAM", R8(b, 0x200001) == 0x77);
        Check("the SRAM image is the battery file's content", cart.Sram[0] == 0x77 && cart.Sram[1] == 0x88);

        // a round trip through the state
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) b.SaveState(w);
        var b2 = Board(rom);
        ms.Position = 0;
        using (var r = new BinaryReader(ms, Encoding.UTF8, true)) b2.LoadState(r);
        Check("a saved state restores the SRAM, the work RAM and the clock", b2.Cart.Sram[0] == 0x77 && b2.MasterClock == b.MasterClock);
    }
}
