using System;
using System.Collections.Generic;
using NesEmulator;
using NesEmulator.Gb;

namespace BrokenNes.Workshop.MixLab;

/// <summary>
/// MIX LAB: a Game Boy inside a NES cartridge - the RetroVision idea. The NES runs a tiny 6502 program (below) that
/// sets a 4-green palette, turns the background on and, every NMI, reads controller 1 and writes it to the cartridge
/// ($5000 = buttons, $5001 = "frame"). On $5001 the cartridge runs its Game Boy for one frame. The cartridge then
/// answers the NES PPU's fetches itself: it supplies the nametable (through IMapper.TryPpuNametableRead - one cell per
/// Game Boy tile, 20x18 of them centred on the screen) and, for each pattern fetch, the 8x8 block of the Game Boy's
/// current picture that the preceding nametable fetch pointed at - so the 360 tiles of a Game Boy frame need no bank
/// switching (a PPU that fetches out of order, like the gimmick cores, draws what it asked for).
/// Sound: "2a03" re-voices the Game Boy APU on this NES's own APU (it becomes the NES's sound); "exp" keeps the Game Boy
/// APU and hands its samples out as cartridge expansion audio (for the WAV).
/// </summary>
internal sealed class GbOnNesCart : IMapper
{
    private readonly Cartridge cart;
    public readonly BOARD_GB Gb;
    private byte buttons;
    private int lastCell = -1;
    private long frames;
    private readonly byte[] shades = new byte[160 * 144];
    public readonly List<short> Expansion = new();
    private readonly short[] pull = new short[16384];
    public bool ExpansionAudio;

    public GbOnNesCart(Cartridge cart, BOARD_GB gb) { this.cart = cart; Gb = gb; }

    // ------------------------------------------------------------------ the NES program
    /// <summary>NROM, 16K PRG, mapper 0 header (the mapper object is then swapped for this class).</summary>
    public static byte[] Rom()
    {
        var rom = new byte[16 + 0x4000];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 0; rom[6] = 0x00;
        byte[] code =
        {
            // $C000 reset
            0x78, 0xD8, 0xA2, 0xFF, 0x9A,                   // SEI, CLD, LDX #$FF, TXS
            0xA9, 0x00, 0x8D, 0x00, 0x20, 0x8D, 0x01, 0x20, // LDA #0, STA $2000, STA $2001
            0x2C, 0x02, 0x20, 0x10, 0xFB,                   // wait vblank
            0x2C, 0x02, 0x20, 0x10, 0xFB,                   // wait vblank
            0xA9, 0x3F, 0x8D, 0x06, 0x20, 0xA9, 0x00, 0x8D, 0x06, 0x20, // $2006 = $3F00
            0xA2, 0x00, 0xBD, 0x00, 0xC1, 0x8D, 0x07, 0x20, 0xE8, 0xE0, 0x20, 0xD0, 0xF5, // copy 32 palette bytes from $C100
            0xA9, 0x00, 0x8D, 0x05, 0x20, 0x8D, 0x05, 0x20, // scroll 0,0
            0xA9, 0x80, 0x8D, 0x00, 0x20,                   // NMI on, BG from $0000
            0xA9, 0x0A, 0x8D, 0x01, 0x20,                   // background on (and its left 8 pixels)
            0xAD, 0x02, 0x50, 0x4C, 0x40, 0xC0,             // $C040: LDA $5002 (clocks the Game Boy 7 cycles), JMP $C040
        };
        byte[] nmi =
        {
            // $C050 NMI: read the pad into $00 (A first -> bit 7), hand it to the cartridge, strobe a frame
            0x48, 0x8A, 0x48,                               // PHA, TXA, PHA
            0xA9, 0x01, 0x8D, 0x16, 0x40, 0xA9, 0x00, 0x8D, 0x16, 0x40, // strobe $4016
            0xA2, 0x08, 0xAD, 0x16, 0x40, 0x4A, 0x26, 0x00, 0xCA, 0xD0, 0xF7, // 8 x (LDA $4016, LSR, ROL $00)
            0xA5, 0x00, 0x8D, 0x00, 0x50, 0x8D, 0x01, 0x50, // LDA $00, STA $5000, STA $5001 (frame marker)
            0xA9, 0x00, 0x8D, 0x05, 0x20, 0x8D, 0x05, 0x20, 0xA9, 0x80, 0x8D, 0x00, 0x20, // scroll 0,0 / $2000 = $80
            0x68, 0xAA, 0x68, 0x40,                         // PLA, TAX, PLA, RTI ($C080 = RTI for IRQ)
        };
        int p = 16;
        Array.Copy(code, 0, rom, p + 0x0000, code.Length);
        Array.Copy(nmi, 0, rom, p + 0x0050, nmi.Length);
        // Palette: universal = the lightest Game Boy green; each palette = light, dark, darkest.
        var g = GbShades.Green;
        byte N(int i) => NesPalette.Nearest(g[i].r, g[i].g, g[i].b);
        for (int k = 0; k < 8; k++) { rom[p + 0x100 + k * 4] = N(0); rom[p + 0x101 + k * 4] = N(1); rom[p + 0x102 + k * 4] = N(2); rom[p + 0x103 + k * 4] = N(3); }
        void Vec(int at, int addr) { rom[p + at] = (byte)addr; rom[p + at + 1] = (byte)(addr >> 8); }
        Vec(0x3FFA, 0xC050); Vec(0x3FFC, 0xC000); Vec(0x3FFE, 0xC080);
        return rom;
    }

    // ------------------------------------------------------------------ CPU side
    public void Reset() { }
    public byte CPURead(ushort address)
    {
        if (address == 0x5002) { Advance(7); return 0; }   // the main loop: LDA $5002 + JMP = 7 NES cycles per read
        return address >= 0x8000 ? cart.prgROM[address & 0x3FFF] : address >= 0x6000 ? cart.prgRAM[address - 0x6000] : (byte)0;
    }

    /// <summary>Game Boy M-cycles per NES CPU cycle (1.048576 MHz / 1.789773 MHz).</summary>
    private const double GbPerNes = 1048576.0 / 1789773.0;
    private double gbTarget;
    private long shownFrame = -1;

    /// <summary>Run the Game Boy for the time <paramref name="nesCycles"/> NES cycles take; publish each finished frame.</summary>
    private void Advance(int nesCycles)
    {
        gbTarget += nesCycles * GbPerNes * (Gb.DoubleSpeed ? 2 : 1);   // double speed: twice the M-cycles in the same time
        while (Gb.CycleCount < gbTarget) Gb.StepInstruction();
        if (Gb.FrameCount != shownFrame) { shownFrame = Gb.FrameCount; Gb.RunFrameEndHousekeeping(); Publish(); }
    }
    public bool IsCpuReadOpenBus(ushort address) => address < 0x6000 && address != 0x5002;
    public void CPUWrite(ushort address, byte value)
    {
        if (address == 0x5000) buttons = value;
        else if (address == 0x5001) SetButtons();
        else if (address >= 0x6000 && address < 0x8000) cart.prgRAM[address - 0x6000] = value;
    }
    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        if (address >= 0x8000) { prgIndex = address & 0x3FFF; return true; }
        prgIndex = -1; return false;
    }

    private void SetButtons()
    {
        // NES pad byte: bit7 A, 6 B, 5 Select, 4 Start, 3 Up, 2 Down, 1 Left, 0 Right.
        GbButtons b = 0;
        if ((buttons & 0x80) != 0) b |= GbButtons.A; if ((buttons & 0x40) != 0) b |= GbButtons.B;
        if ((buttons & 0x20) != 0) b |= GbButtons.Select; if ((buttons & 0x10) != 0) b |= GbButtons.Start;
        if ((buttons & 0x08) != 0) b |= GbButtons.Up; if ((buttons & 0x04) != 0) b |= GbButtons.Down;
        if ((buttons & 0x02) != 0) b |= GbButtons.Left; if ((buttons & 0x01) != 0) b |= GbButtons.Right;
        if (b != Gb.Buttons) { Gb.Buttons = b; Gb.UpdateJoypadIrq(); }
    }

    private void Publish()
    {
        frames++;
        int n;
        while ((n = Gb.Apu.ReadSamples(pull)) > 0) if (ExpansionAudio) for (int i = 0; i + 1 < n; i += 2) Expansion.Add((short)((pull[i] + pull[i + 1]) / 2));
        // The picture as 4 shades: DMG shades directly, Game Boy Color colours by brightness.
        var fb = Gb.Ppu.FrameBuffer;
        for (int i = 0; i < shades.Length; i++)
        {
            if (!Gb.CgbMode) { shades[i] = Gb.Ppu.ShadeBuffer[i]; continue; }
            uint c = fb[i]; int y = (int)(((c >> 16) & 255) * 3 + ((c >> 8) & 255) * 6 + (c & 255)) / 10;
            shades[i] = (byte)(y > 190 ? 0 : y > 120 ? 1 : y > 60 ? 2 : 3);
        }
    }

    // ------------------------------------------------------------------ PPU side
    private const int Col0 = 6, Row0 = 6;   // Game Boy screen at NES (48, 48)

    public bool TryPpuNametableRead(ushort address, out byte value)
    {
        int off = address & 0x3FF;
        value = 0;
        if (off >= 0x3C0) { return true; }                // attributes: palette 0 everywhere
        int row = off >> 5, col = off & 31;
        int gr = row - Row0, gc = col - Col0;
        if (gr >= 0 && gr < 18 && gc >= 0 && gc < 20) { lastCell = gr * 20 + gc; value = (byte)(1 + lastCell % 255); }
        else lastCell = -1;
        return true;
    }
    public bool TryPpuNametableWrite(ushort address, byte value) => true;

    public byte PPURead(ushort address)
    {
        if (address >= 0x2000 || lastCell < 0) return 0;
        int plane = (address >> 3) & 1, r = address & 7;
        int gy = (lastCell / 20) * 8 + r, gx = (lastCell % 20) * 8;
        int bits = 0;
        for (int x = 0; x < 8; x++) bits |= ((shades[gy * 160 + gx + x] >> plane) & 1) << (7 - x);
        return (byte)bits;
    }
    public void PPUWrite(ushort address, byte value) { }

    public object GetMapperState() => frames;
    public void SetMapperState(object state) { }
    public uint GetChrBankSignature() => (uint)frames;   // the "CHR" changes every frame
}
