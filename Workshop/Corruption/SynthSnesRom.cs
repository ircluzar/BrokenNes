using System;
using System.Collections.Generic;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// A tiny SNES "game" the corruption tests build in memory (no ROM data of anyone's, nothing in git but this code): a 32 KB LoROM whose 65816 program
///   1. turns the sprite layer on, 2. uploads a sound program and a one-note instrument to the SPC700 through the boot loader (the real handshake), then
///   3. loops forever copying the controller to the SPC700's ports and counting in work RAM.
/// The SPC700 program sets up the S-DSP (one voice: a 2 kHz square-wave BRR sample, sample directory at $0400, sample at $0500), keys it on, and then loops counting in audio RAM
/// word $12 every pass in which a button is held. So the game has a visible sprite layer, a sound driver with an instrument in audio RAM, and a state that depends on the pad
/// frame by frame in work RAM ($10 $11 pad, $12 counter, $13-$14 sum) and in audio RAM ($12 $13).
/// </summary>
internal static class SynthSnesRom
{
    public const int AramProgram = 0x0200, AramDirectory = 0x0400, AramSample = 0x0500, AramCounter = 0x12;
    public const int PayloadLength = AramSample + 9 - AramProgram;     // $0200..$0508 uploaded as one block
    private const int PayloadRom = 0x100;                              // ROM offset of the payload ($8100 in bank 0)

    /// <summary>The one-note instrument's BRR block: range 9, filter 0, loop + end; sixteen samples (eight high, eight low).</summary>
    public static readonly byte[] SampleBlock = { 0x93, 0x77, 0x77, 0x77, 0x77, 0x99, 0x99, 0x99, 0x99 };

    /// <summary>The game. <paramref name="vibrato"/> builds the round-6 variant whose sound driver bends the voice's pitch back and forth (every 9 ms between $1000 and $1100) and fades its volume over about 1.4 s: a driver doing something with its instrument over time, for the Instrument Runaway recipe tests.</summary>
    public static byte[] Build(bool vibrato = false)
    {
        var rom = new byte[0x8000];
        BuildPayload(vibrato).CopyTo(rom, PayloadRom);
        var cpu = Cpu();
        if (cpu.Length > PayloadRom - 0x10) throw new InvalidOperationException("the 65816 program overlaps the payload");
        cpu.CopyTo(rom, 0);
        rom[0xF0] = 0x40;   // RTI: where the interrupt vectors point

        // header at $7FC0
        var title = "BN CORRUPTION SYNTH   "[..21];
        for (int i = 0; i < 21; i++) rom[0x7FC0 + i] = (byte)title[i];
        rom[0x7FD5] = 0x20; rom[0x7FD6] = 0x00; rom[0x7FD7] = 0x05; rom[0x7FD8] = 0x00; rom[0x7FD9] = 0x00; rom[0x7FDA] = 0x33; rom[0x7FDB] = 0x00;
        foreach (int v in new[] { 0x7FE4, 0x7FE6, 0x7FE8, 0x7FEA, 0x7FEE, 0x7FF4, 0x7FF8, 0x7FFA, 0x7FFE }) { rom[v] = 0xF0; rom[v + 1] = 0x80; }   // all but reset -> RTI
        rom[0x7FFC] = 0x00; rom[0x7FFD] = 0x80;                                                                                                       // reset -> $8000
        rom[0x7FDC] = 0xFF; rom[0x7FDD] = 0xFF; rom[0x7FDE] = 0x00; rom[0x7FDF] = 0x00;
        int sum = 0;
        foreach (byte b in rom) sum += b;
        sum &= 0xFFFF;
        rom[0x7FDE] = (byte)sum; rom[0x7FDF] = (byte)(sum >> 8);
        rom[0x7FDC] = (byte)~sum; rom[0x7FDD] = (byte)(~sum >> 8);
        return rom;
    }

    private static byte Lo(int v) => (byte)(v & 0xFF);
    private static byte Hi(int v) => (byte)(v >> 8 & 0xFF);

    // ---------------------------------------------------------------------------------------------------- the SPC700 side

    private static byte[] BuildPayload(bool vibrato = false)
    {
        var p = new byte[PayloadLength];
        var code = new List<byte>();
        // S-DSP set-up: MOV $F2,#reg / MOV $F3,#value pairs (8F imm dp)
        (byte reg, byte val)[] dsp =
        {
            (0x0C, 0x7F), (0x1C, 0x7F),     // main volume
            (0x5D, 0x04),                   // sample directory at $0400
            (0x6C, 0x20),                   // FLG: unmuted, echo writes off
            (0x00, 0x50), (0x01, 0x50),     // voice 0 volume
            (0x02, 0x00), (0x03, 0x10),     // pitch $1000: the sample at its own rate (32 kHz / 16 samples = 2 kHz)
            (0x04, 0x00),                   // sample number 0
            (0x05, 0xFF), (0x06, 0xE0),     // ADSR on, fastest attack; sustain level 7 and no decay of it
            (0x4C, 0x01),                   // key on voice 0
        };
        foreach (var (reg, val) in dsp) { code.AddRange(new byte[] { 0x8F, reg, 0xF2, 0x8F, val, 0xF3 }); }
        if (vibrato)
        {
            // MOV $30,#$50 ; loop: pitch high byte $10, delay, $11, delay ; fade the volume one step if it is not zero ; BRA loop ; delay: Y x (256 x DEC A), about 9 ms
            code.AddRange(new byte[] { 0x8F, 0x50, 0x30 });
            int vloop = code.Count;
            int delayAt = AramProgram + 160;                                // the delay routine sits at a fixed place after the loop (the loop is under 160 bytes)
            byte[] step(byte hi) => new byte[] { 0x8F, 0x03, 0xF2, 0x8F, hi, 0xF3, 0x3F, Lo(delayAt), Hi(delayAt) };
            code.AddRange(step(0x10)); code.AddRange(step(0x11));
            // MOV A,$30 / BEQ skip (13 bytes) / DEC A / MOV $30,A / voice 0 volume left and right = A
            code.AddRange(new byte[] { 0xE4, 0x30, 0xF0, 0x0D, 0x9C, 0xC4, 0x30, 0x8F, 0x00, 0xF2, 0xC4, 0xF3, 0x8F, 0x01, 0xF2, 0xC4, 0xF3 });
            int back2 = vloop - (code.Count + 2);
            code.AddRange(new byte[] { 0x2F, (byte)back2 });
            if (code.Count > 160) throw new InvalidOperationException("the vibrato loop overlaps its delay routine");
            while (code.Count < 160) code.Add(0x00);
            code.AddRange(new byte[] { 0x8D, 0x06, 0xE8, 0x00, 0x9C, 0xD0, 0xFD, 0xDC, 0xD0, 0xF8, 0x6F });   // delay: MOV Y,#6 ; MOV A,#0 ; DEC A ; BNE -3 ; DEC Y ; BNE -8 ; RET
        }
        else
        {
            // loop: MOV A,$F4 / OR A,$F5 / BEQ +2 / INCW $12 / BRA loop
            int loop = code.Count;
            code.AddRange(new byte[] { 0xE4, 0xF4, 0x04, 0xF5, 0xF0, 0x02, 0x3A, (byte)AramCounter });
            int back = loop - (code.Count + 2);
            code.AddRange(new byte[] { 0x2F, (byte)back });
        }
        code.CopyTo(p, 0);
        // sample directory entry 0: start and loop at $0500
        p[AramDirectory - AramProgram + 0] = Lo(AramSample); p[AramDirectory - AramProgram + 1] = Hi(AramSample);
        p[AramDirectory - AramProgram + 2] = Lo(AramSample); p[AramDirectory - AramProgram + 3] = Hi(AramSample);
        SampleBlock.CopyTo(p, AramSample - AramProgram);
        return p;
    }

    // ---------------------------------------------------------------------------------------------------- the 65816 side

    private sealed class Asm
    {
        public readonly List<byte> B = new();
        private readonly Dictionary<string, int> labels = new();
        private readonly List<(int at, string label)> fixups = new();
        public int Pc => 0x8000 + B.Count;
        public void Label(string n) => labels[n] = B.Count;
        public void Emit(params byte[] b) => B.AddRange(b);
        public void Branch(byte op, string label) { Emit(op, 0); fixups.Add((B.Count - 1, label)); }
        private readonly List<(int at, string label)> jumps = new();
        /// <summary>JMP absolute to a label.</summary>
        public void Jmp(string label) { Emit(0x4C, 0, 0); jumps.Add((B.Count - 2, label)); }
        public byte[] Done()
        {
            foreach (var (at, label) in jumps) { int addr = 0x8000 + labels[label]; B[at] = (byte)addr; B[at + 1] = (byte)(addr >> 8); }
            foreach (var (at, label) in fixups)
            {
                int off = labels[label] - (at + 1);
                if (off is < -128 or > 127) throw new InvalidOperationException($"branch to {label} is out of range");
                B[at] = (byte)off;
            }
            return B.ToArray();
        }
    }

    private static byte[] Cpu()
    {
        var a = new Asm();
        a.Emit(0x78);                       // SEI
        a.Emit(0x18, 0xFB);                 // CLC XCE: native mode
        a.Emit(0xC2, 0x10);                 // REP #$10: 16-bit index, 8-bit accumulator
        // sprites on: force blank, OBSEL 0, main screen = OBJ, then full brightness
        a.Emit(0xA9, 0x8F, 0x8D, 0x00, 0x21);       // LDA #$8F : STA $2100
        a.Emit(0x9C, 0x01, 0x21);                   // STZ $2101
        a.Emit(0xA9, 0x10, 0x8D, 0x2C, 0x21);       // LDA #$10 : STA $212C
        a.Emit(0xA9, 0x0F, 0x8D, 0x00, 0x21);       // LDA #$0F : STA $2100
        a.Emit(0xA9, 0x01, 0x8D, 0x00, 0x42);       // LDA #$01 : STA $4200 (auto joypad read)
        // the boot loader's handshake: wait for $AA $BB
        a.Emit(0xA9, 0xAA); a.Label("w1"); a.Emit(0xCD, 0x40, 0x21); a.Branch(0xD0, "w1");
        a.Emit(0xA9, 0xBB); a.Label("w2"); a.Emit(0xCD, 0x41, 0x21); a.Branch(0xD0, "w2");
        a.Emit(0xA2, Lo(AramProgram), Hi(AramProgram), 0x8E, 0x42, 0x21);   // LDX #$0200 : STX $2142 (destination)
        a.Emit(0xA9, 0x01, 0x8D, 0x41, 0x21);                                          // LDA #$01 : STA $2141 (more than zero: a transfer)
        a.Emit(0xA9, 0xCC, 0x8D, 0x40, 0x21);                                          // LDA #$CC : STA $2140 (the kick)
        a.Label("w3"); a.Emit(0xCD, 0x40, 0x21); a.Branch(0xD0, "w3");                 // wait for the echo
        a.Emit(0xA2, 0x00, 0x00);                                                      // LDX #0
        a.Label("send");
        a.Emit(0xBF, Lo(PayloadRom), (byte)(0x80 | Hi(PayloadRom)), 0x00);          // LDA $008100,X
        a.Emit(0x8D, 0x41, 0x21);                                                      // STA $2141 (the byte)
        a.Emit(0x8A, 0x8D, 0x40, 0x21);                                                // TXA : STA $2140 (the index)
        a.Label("w4"); a.Emit(0xCD, 0x40, 0x21); a.Branch(0xD0, "w4");                 // wait for the index to come back
        a.Emit(0xE8);                                                                  // INX
        a.Emit(0xE0, Lo(PayloadLength), Hi(PayloadLength));                 // CPX #length
        a.Branch(0xD0, "send");
        a.Emit(0xA2, Lo(AramProgram), Hi(AramProgram), 0x8E, 0x42, 0x21);   // LDX #$0200 : STX $2142 (where to start)
        a.Emit(0x9C, 0x41, 0x21);                                                      // STZ $2141 (zero: execute)
        a.Emit(0xA9, (byte)(Lo(PayloadLength) + 2), 0x8D, 0x40, 0x21);            // LDA #(next index + 2) : STA $2140
        a.Label("w5"); a.Emit(0xCD, 0x40, 0x21); a.Branch(0xD0, "w5");                 // wait for the loader to acknowledge the command (it echoes the value), before the pad overwrites the port
        // the main loop: the pad to the SPC700's ports and into work RAM
        a.Label("main");
        a.Emit(0xAD, 0x18, 0x42, 0x8D, 0x40, 0x21, 0x85, 0x10);                        // LDA $4218 : STA $2140 : STA $10
        a.Emit(0xAD, 0x19, 0x42, 0x8D, 0x41, 0x21, 0x85, 0x11);                        // LDA $4219 : STA $2141 : STA $11
        a.Emit(0xE6, 0x12);                                                            // INC $12
        a.Emit(0xA5, 0x13, 0x18, 0x6D, 0x18, 0x42, 0x85, 0x13);                        // LDA $13 : CLC : ADC $4218 : STA $13
        a.Emit(0xA5, 0x14, 0x69, 0x00, 0x85, 0x14);                                    // LDA $14 : ADC #0 : STA $14 (the carry: a 16-bit sum of the pad over time)
        a.Jmp("main");
        return a.Done();
    }
}
