// The x86 console's picture chips (the VGA harvest, docs/projects/vga-harvest-catalog.md): other emulators' old renderers drawing the stock VGA card's
// registers and memory, kept as art instruments with their flaws. Written for BrokenNes; the chips themselves are ports (see each file and
// x86/THIRD_PARTY_NOTICES.md).

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

/// <summary>A picture chip on the VGA card: it sees the card's port writes and memory accesses as they happen, polls the beam after every CPU batch,
/// and draws <see cref="Vga.Frame"/> in place of the stock picture. The card's registers and memory stay the card's (<see cref="Vga"/>).</summary>
public interface IVgaPicture
{
    /// <summary>The x86 PPU id ("BOCHS20", ...).</summary>
    string Id { get; }
    /// <summary>True for a CGA chip: the BIOS then also sets the CGA graphics modes 4-6.</summary>
    bool TakesCgaModes { get; }
    /// <summary>A port write to 3B0-3DF (before the card takes it).</summary>
    void NoteOut(int port, byte v);
    /// <summary>A CPU (or BIOS) write to A0000-BFFFF, before the card stores it.</summary>
    void NoteWrite(uint addr, byte v);
    /// <summary>A CPU read of A0000-BFFFF.</summary>
    void NoteRead(uint addr);
    /// <summary>The BIOS has set a video mode (INT 10h AH=0).</summary>
    void ModeSet(int mode);
    /// <summary>After every CPU batch of a frame: the chip's own timing.</summary>
    void Poll();
    /// <summary>Once per frame, at vertical retrace: fills the card's <see cref="Vga.Frame"/>.</summary>
    void Render();
    void Reset();
    void Save(BinaryWriter w);
    void Load(BinaryReader r);
}

/// <summary>The x86 PPU menu: the stock VGA picture and the harvest chips.</summary>
public static class VgaPictures
{
    public static readonly (string id, string label)[] Chips =
    {
        ("BOCHS20", "BOCHS20 (Bochs 2.0 vga.cc, 2002: 16x24 dirty tiles redrawn 33 times a second; no panning, split screen or blink; text from the DAC's first 16 colours)"),
        ("DOSBOX072", "DOSBOX072 (DOSBox 0.72 vga_draw.cpp, 2007: the frame drawn in four bands at four moments, 8-dot text, palette per frame)"),
        ("PCEM_CGA", "PCEM_CGA (PCem v10 vid_cga.c, 2015: an IBM CGA - 80-column snow, 8x8 font, RGBI colours; no VGA modes)"),
    };

    /// <summary>The chip for an x86 PPU id, null for the stock "VGA"; throws for an id it does not know.</summary>
    public static IVgaPicture? Create(Vga v, string id)
    {
        switch (id.ToUpperInvariant())
        {
            case "VGA": return null;
            case "BOCHS20": return new Bochs20Vga(v);
            case "DOSBOX072": return new Dosbox072Vga(v);
            case "PCEM_CGA": return new PcemCga(v);
            default: throw new ArgumentException($"the x86 PC has no picture chip '{id}'");
        }
    }

    public static string Describe(string id) => id.ToUpperInvariant() switch
    {
        "BOCHS20" => "VGA drawn by Bochs 2.0",
        "DOSBOX072" => "VGA drawn by DOSBox 0.72",
        "PCEM_CGA" => "CGA (PCem v10)",
        _ => "VGA",
    };

    /// <summary>A CPU byte offset into the B8000/A0000 window as the card stores it in odd/even (text) addressing: plane (b &amp; 1) at address b &amp; ~1.</summary>
    internal static int OddEven(int b) => ((b & ~1) << 1) | (b & 1);
}
