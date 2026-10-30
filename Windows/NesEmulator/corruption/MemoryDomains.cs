using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Mix;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace NesEmulator.Corruption;

public enum DomainKind { Work, Video, Palette, Audio, Registers, CartRam, Rom }

/// <summary>
/// A named, byte-addressed, bounded window onto one memory of a running machine. Peek and Poke are plain memory access (no bus cycle, so no read side effects and no timing), valid only between frames; a
/// domain whose chip keeps derived state does that chip's own bookkeeping in <see cref="Poke"/> (the SNES OAM invalidates the sprite cache, the S-DSP registers go through the DSP's register write).
/// Sixteen-bit memories are addressed by byte, little-endian.
/// </summary>
public interface IMemoryDomain
{
    /// <summary>The id a stockpile names it by: ARAM, DSP, WRAM, VRAM, ... (the NES keeps its older PRG, PRGRAM, CHR, RAM).</summary>
    string Id { get; }
    string Label { get; }
    /// <summary>The true size in bytes (0 when <see cref="Unavailable"/>).</summary>
    int Size { get; }
    DomainKind Kind { get; }
    /// <summary>False when the machine has this memory but writing it is not defined here.</summary>
    bool Writable { get; }
    /// <summary>Why this machine has no such memory right now ("the sound unit is the HLE stand-in"), or null.</summary>
    string? Unavailable { get; }
    byte Peek(int address);
    void Poke(int address, byte value);
}

/// <summary>A byte array as a domain.</summary>
public sealed class ArrayDomain : IMemoryDomain
{
    private readonly byte[] data;
    private readonly Action? afterPoke;
    public ArrayDomain(string id, string label, DomainKind kind, byte[] data, Action? afterPoke = null, bool writable = true)
    { Id = id; Label = label; Kind = kind; this.data = data; this.afterPoke = afterPoke; Writable = writable; }
    public string Id { get; }
    public string Label { get; }
    public DomainKind Kind { get; }
    public bool Writable { get; }
    public string? Unavailable => null;
    public int Size => data.Length;
    /// <summary>The whole memory at once (BrokenDev Timeflow compares it frame to frame).</summary>
    public void CopyTo(Span<byte> into) => data.AsSpan(0, Math.Min(data.Length, into.Length)).CopyTo(into);
    public byte Peek(int address) => (uint)address < (uint)data.Length ? data[address] : (byte)0;
    public void Poke(int address, byte value)
    {
        if ((uint)address >= (uint)data.Length || !Writable) return;
        data[address] = value;
        afterPoke?.Invoke();
    }
}

/// <summary>A 16-bit-word array (the SNES VRAM and CGRAM, the Genesis CRAM) as a domain of bytes, little-endian. <paramref name="highMask"/> clears bits of a word's high byte that the hardware does not store.</summary>
public sealed class WordDomain : IMemoryDomain
{
    private readonly ushort[] data;
    private readonly byte highMask;
    public WordDomain(string id, string label, DomainKind kind, ushort[] data, byte highMask = 0xFF)
    { Id = id; Label = label; Kind = kind; this.data = data; this.highMask = highMask; }
    public string Id { get; }
    public string Label { get; }
    public DomainKind Kind { get; }
    public bool Writable => true;
    public string? Unavailable => null;
    public int Size => data.Length * 2;
    /// <summary>The whole memory at once, little-endian as <see cref="Peek"/> gives it (BrokenDev Timeflow compares it frame to frame).</summary>
    public void CopyTo(Span<byte> into)
    {
        int n = Math.Min(data.Length, into.Length / 2);
        for (int i = 0; i < n; i++) { ushort w = data[i]; into[2 * i] = (byte)w; into[2 * i + 1] = (byte)(w >> 8); }
    }
    public byte Peek(int address)
    {
        if ((uint)address >= (uint)Size) return 0;
        ushort w = data[address >> 1];
        return (address & 1) == 0 ? (byte)w : (byte)(w >> 8);
    }

    public void Poke(int address, byte value)
    {
        if ((uint)address >= (uint)Size) return;
        ref ushort w = ref data[address >> 1];
        w = (address & 1) == 0 ? (ushort)((w & 0xFF00) | value) : (ushort)((w & 0x00FF) | (value & highMask) << 8);
    }
}

/// <summary>A memory reached through two functions (a register file, a NES cartridge array behind its accessors).</summary>
public sealed class FuncDomain : IMemoryDomain
{
    private readonly Func<int, byte> peek;
    private readonly Action<int, byte> poke;
    private readonly int size;
    public FuncDomain(string id, string label, DomainKind kind, int size, Func<int, byte> peek, Action<int, byte> poke, bool writable = true)
    { Id = id; Label = label; Kind = kind; this.size = size; this.peek = peek; this.poke = poke; Writable = writable; }
    public string Id { get; }
    public string Label { get; }
    public DomainKind Kind { get; }
    public bool Writable { get; }
    public string? Unavailable => null;
    public int Size => size;
    public byte Peek(int address) => (uint)address < (uint)size ? peek(address) : (byte)0;
    public void Poke(int address, byte value) { if ((uint)address < (uint)size && Writable) poke(address, value); }
}

/// <summary>A memory the machine has in general but not now; it is listed so the user (and a stockpile's replay) can be told why it is missing.</summary>
public sealed class MissingDomain : IMemoryDomain
{
    public MissingDomain(string id, string label, DomainKind kind, string why) { Id = id; Label = label; Kind = kind; Unavailable = why; }
    public string Id { get; }
    public string Label { get; }
    public DomainKind Kind { get; }
    public int Size => 0;
    public bool Writable => false;
    public string? Unavailable { get; }
    public byte Peek(int address) => 0;
    public void Poke(int address, byte value) { }
}

/// <summary>A session (or a test double) that lists its own memory domains; <see cref="MachineDomains.For(IConsoleSession)"/> asks it first.</summary>
public interface IMemoryDomainSource
{
    IReadOnlyList<IMemoryDomain> MemoryDomains { get; }
}

/// <summary>The memory domains of a running machine, by console.</summary>
public static class MachineDomains
{
    public static IMemoryDomain? Find(IReadOnlyList<IMemoryDomain> domains, string id) =>
        domains.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The domains of a console session, true sizes, in a fixed order. A session of a console this build cannot yet corrupt gives an empty list and <see cref="Why"/> says so
    /// (the caller shows it; nothing quietly applies to the wrong memory).
    /// </summary>
    public static IReadOnlyList<IMemoryDomain> For(IConsoleSession session)
    {
        if (session is IMemoryDomainSource own) return own.MemoryDomains;
        var inner = session is SegaPictureSession w ? w.Inner : session;
        switch (inner)
        {
            case SnesSession snes: return Snes(snes);
            case GbSession gb: return GameBoy(gb);
            case NesEmulator.Sega.SmsSession sms: return Sms(sms);
            case NesEmulator.Sega.MdSession md: return Genesis(md);
            default: return Array.Empty<IMemoryDomain>();
        }
    }

    /// <summary>Why <see cref="For(IConsoleSession)"/> gave nothing for this session.</summary>
    public static string Why(IConsoleSession session) =>
        $"corrupting the {Consoles.DisplayName(session.Console)}'s memory is not available in this build";

    /// <summary>The NES's domains. <c>PRG</c>, <c>PRGRAM</c>, <c>CHR</c> and <c>RAM</c> are the ids the Glitch Harvester has always used; the sizes here are the cartridge's true ones.</summary>
    public static IReadOnlyList<IMemoryDomain> For(NES nes) => new IMemoryDomain[]
    {
        new FuncDomain("RAM", "System RAM (2 KB)", DomainKind.Work, 2048, nes.PeekSystemRam, nes.PokeSystemRam),
        new FuncDomain("PRGRAM", "Cartridge PRG RAM", DomainKind.CartRam, Safe(nes.GetPrgRamSize), nes.PeekPrgRam, nes.PokePrgRam),
        new FuncDomain("CHR", "Cartridge CHR (ROM or RAM)", DomainKind.Video, Safe(nes.GetChrSize), nes.PeekChr, nes.PokeChr),
        new FuncDomain("PRG", "Cartridge PRG ROM", DomainKind.Rom, Safe(nes.GetPrgRomSize), nes.PeekPrg, nes.PokePrg),
    };

    private static int Safe(Func<int> size) { try { return size(); } catch { return 0; } }

    private static IReadOnlyList<IMemoryDomain> GameBoy(GbSession s)
    {
        var b = s.Board;
        var ppu = b.Ppu;
        var list = new List<IMemoryDomain>
        {
            new ArrayDomain("WRAM", $"Work RAM ({b.Wram.Length / 1024} KB)", DomainKind.Work, b.Wram),
            new ArrayDomain("HRAM", "High RAM (127 bytes)", DomainKind.Work, b.Hram),
            new ArrayDomain("VRAM", $"Video RAM ({ppu.Vram.Length / 1024} KB; a Color has two banks)", DomainKind.Video, ppu.Vram),
            new ArrayDomain("OAM", "Sprite attribute RAM (160 bytes)", DomainKind.Video, ppu.Oam),
        };
        if (b.Model == NesEmulator.Gb.GbModel.Cgb)
            list.Add(new FuncDomain("CRAM", "Colour palette RAM (64 bytes of background, 64 of sprites)", DomainKind.Palette, 128,
                a => a < 64 ? ppu.BgPalRam[a] : ppu.ObjPalRam[a - 64], (a, v) => { if (a < 64) ppu.BgPalRam[a] = v; else ppu.ObjPalRam[a - 64] = v; }));
        else list.Add(new MissingDomain("CRAM", "Colour palette RAM", DomainKind.Palette, "this game runs on the monochrome Game Boy, which has no colour RAM"));
        list.Add(b.Cart.Ram.Length > 0 ? new ArrayDomain("SRAM", $"Cartridge RAM ({b.Cart.Ram.Length / 1024} KB)", DomainKind.CartRam, b.Cart.Ram) : new MissingDomain("SRAM", "Cartridge RAM", DomainKind.CartRam, "this cartridge has no RAM of its own"));
        list.Add(new ArrayDomain("ROM", $"Cartridge ROM ({b.Cart.Rom.Length / 1024} KB)", DomainKind.Rom, b.Cart.Rom));
        list.Add(b.Apu is NesEmulator.Gb.APU_GB apu ? new ArrayDomain("WAVE", "Wave channel RAM (16 bytes: the Game Boy's one custom instrument)", DomainKind.Audio, apu.WaveRam)
                                                      : new MissingDomain("WAVE", "Wave channel RAM", DomainKind.Audio, "the sound unit is not the Game Boy's own"));
        return list;
    }

    private static IReadOnlyList<IMemoryDomain> Sms(NesEmulator.Sega.SmsSession s)
    {
        var b = s.Board;
        bool gg = s.Console == ConsoleKind.GameGear;
        var list = new List<IMemoryDomain>
        {
            new ArrayDomain("RAM", "Work RAM (8 KB)", DomainKind.Work, b.Ram),
            new ArrayDomain("VRAM", "Video RAM (16 KB)", DomainKind.Video, b.Vdp.Vram),
            new FuncDomain("CRAM", gg ? "Colour RAM (32 colours of 12 bits)" : "Colour RAM (32 colours of 6 bits)", DomainKind.Palette, gg ? 64 : 32, a => b.Vdp.CramByte(a), (a, v) => b.Vdp.SetCramByte(a, v)),
        };
        list.Add(b.Cart.Ram is { Length: > 0 } ram ? new ArrayDomain("SRAM", $"Cartridge RAM ({ram.Length / 1024} KB)", DomainKind.CartRam, ram) : new MissingDomain("SRAM", "Cartridge RAM", DomainKind.CartRam, "this cartridge has no RAM of its own"));
        list.Add(new ArrayDomain("ROM", $"Cartridge ROM ({b.Cart.Rom.Length / 1024} KB)", DomainKind.Rom, b.Cart.Rom));
        return list;
    }

    private static IReadOnlyList<IMemoryDomain> Genesis(NesEmulator.Sega.MdSession s)
    {
        var b = s.Board;
        var list = new List<IMemoryDomain>
        {
            new ArrayDomain("RAM", "68000 work RAM (64 KB)", DomainKind.Work, b.Ram),
            new ArrayDomain("VRAM", "Video RAM (64 KB)", DomainKind.Video, b.Vdp.Vram),
            new WordDomain("CRAM", "Colour RAM (64 colours of 9 bits)", DomainKind.Palette, b.Vdp.Cram),
            new WordDomain("VSRAM", "Vertical scroll RAM (40 entries)", DomainKind.Video, b.Vdp.Vsram),
        };
        list.Add(b.Sound is NesEmulator.Sega.MdSoundUnit unit ? new ArrayDomain("ZRAM", "Z80 sound RAM (8 KB): the sound driver's code and data", DomainKind.Audio, unit.Z80Ram)
                                                              : new MissingDomain("ZRAM", "Z80 sound RAM", DomainKind.Audio, "the sound unit is not the Genesis's own"));
        list.Add(b.Cart.Sram.Length > 0 ? new ArrayDomain("SRAM", $"Cartridge RAM ({b.Cart.Sram.Length / 1024} KB)", DomainKind.CartRam, b.Cart.Sram) : new MissingDomain("SRAM", "Cartridge RAM", DomainKind.CartRam, "this cartridge has no battery RAM"));
        list.Add(new ArrayDomain("ROM", $"Cartridge ROM ({b.Cart.Rom.Length / 1024} KB)", DomainKind.Rom, b.Cart.Rom));
        return list;
    }

    private static IReadOnlyList<IMemoryDomain> Snes(SnesSession s)
    {
        var board = s.Board;
        var ppu = board.Ppu;
        var list = new List<IMemoryDomain>
        {
            new ArrayDomain("WRAM", "Work RAM (128 KB)", DomainKind.Work, board.Wram),
            new WordDomain("VRAM", "Video RAM (64 KB)", DomainKind.Video, ppu.Vram),
            new WordDomain("CGRAM", "Palette RAM (512 bytes; the top bit of a colour is not stored)", DomainKind.Palette, ppu.Cgram, highMask: 0x7F),
            new ArrayDomain("OAM", "Sprite attribute RAM (544 bytes)", DomainKind.Video, ppu.Oam, afterPoke: ppu.InvalidateCaches),
        };
        if (board.Cart.Sram.Length > 0) list.Add(new ArrayDomain("SRAM", $"Cartridge RAM ({board.Cart.Sram.Length / 1024} KB)", DomainKind.CartRam, board.Cart.Sram));
        else list.Add(new MissingDomain("SRAM", "Cartridge RAM", DomainKind.CartRam, "this cartridge has no battery or work RAM of its own"));
        list.Add(new ArrayDomain("ROM", $"Cartridge ROM ({board.Cart.Rom.Length / 1024} KB)", DomainKind.Rom, board.Cart.Rom));
        var spc = s.Spc;
        if (spc != null)
        {
            list.Add(new ArrayDomain("ARAM", "SPC700 audio RAM (64 KB): the sound driver, its sequences, the BRR samples and the sample directory", DomainKind.Audio, spc.Aram));
            list.Add(new FuncDomain("DSP", "S-DSP registers (128)", DomainKind.Registers, 128, spc.Dsp.Read, spc.Dsp.Write));
        }
        else
        {
            const string why = "the SNES sound unit is the silent HLE stand-in: it has no SPC700 or S-DSP";
            list.Add(new MissingDomain("ARAM", "SPC700 audio RAM", DomainKind.Audio, why));
            list.Add(new MissingDomain("DSP", "S-DSP registers", DomainKind.Registers, why));
        }
        return list;
    }
}
