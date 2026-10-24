using System;
using System.IO;
using System.Text;

namespace NesEmulator.Snes;

// WideMess: the souped-up SNES fantasy console (docs/projects/widemess-spec.md, docs/projects/widemess-plan.md). A superset of the SNES: the board, the
// 65816, the S-PPU and the sound are the SNES's, and the extensions stay invisible until a game unlocks them, so a SNES game on a WideMess is a SNES game.
//   WmsExtension  the unlock, the identity registers ($4220-$423F) and the B-bus window ($21A0-$21FF) the board hands to it
//   WmsFile       the .wms game file: its header, checked, and the ROM image inside
//   PPU_WMS       the picture chip: PPU_SFC (a subclass; the SNES modes draw on the stock pipeline) + 512 KB video RAM, 1,024 colours, the 8-layer
//                 256-colour mode and the 400-wide output

/// <summary>The WideMess's own registers and its unlock (spec section 1). The board consults it only for addresses the SNES leaves unused, and while it is locked every access falls through to the SNES's behaviour.</summary>
public sealed class WmsExtension
{
    public const byte SpecMajor = 1, SpecMinor = 0;
    /// <summary>The WMFEAT bits this console has: bit 0 = the picture (wide output and the 8-layer mode); bit 1 = the rest of the picture (the 256-sprite table,
    /// windows and colour math at 400, the SNES modes wide: milestone 2); bit 2 = the sound (32 voices, BRR + 16-bit PCM, 1 MB sound RAM: milestone 3); bits 3, 4, 5 =
    /// fast CPU, 1 MB work RAM, fast DMA (milestone 4). The other features arrive with their milestones.</summary>
    public const byte Features = 0x3F;
    public const byte IdValue = 0x57;   // 'W'

    private readonly PPU_WMS ppu;
    private byte unlockStep, speed, dma;

    public WmsExtension(PPU_WMS ppu) { this.ppu = ppu; }

    // ---- the sound unit's side (spec 4; WideMess.Sound.cs)
    private APU_WMS? sound;
    /// <summary>The WideMess sound chip when it is the sound unit playing (the session sets it, and again after an APU swap); null when a SNES sound chip plays, which has no sound RAM port and no voices 8-31.
    /// Set while unlocked, the unit is unlocked at once (the unlock line); an unlocked unit set while the console is locked (parked across a reset) goes back to its power-on WideMess part.</summary>
    public APU_WMS? Sound
    {
        get => sound;
        set
        {
            sound = value;
            if (value == null) return;
            if (Unlocked) value.Unlock();
            else if (value.Unlocked) value.PowerOnWide();
        }
    }
    /// <summary>The board's master clock (the session sets it): a sound RAM port access lands at that moment.</summary>
    public Func<long>? Clock { get; set; }
    private long SoundClock() => Clock?.Invoke() ?? throw new InvalidOperationException("the WideMess sound RAM port needs the board's clock (WmsExtension.Clock)");

    /// <summary>True once the game wrote $57, $4D to WMUNLOCK; until the next reset.</summary>
    public bool Unlocked { get; private set; }

    public void Reset() { Unlocked = false; unlockStep = 0; speed = 0; dma = 0; Changed?.Invoke(); }

    /// <summary>WMSPEED bit 0: fast mode (spec section 2).</summary>
    public bool FastCpu => (speed & 1) != 0;
    /// <summary>WMDMA bit 0: fast general DMA (spec section 2).</summary>
    public bool FastDma => (dma & 1) != 0;
    /// <summary>The board's hook: called when the unlock, WMSPEED or WMDMA changed (a write, a reset, a loaded state), so it applies the modes (BOARD_SFC.WideMess.cs).</summary>
    public Action? Changed;

    /// <summary>A CPU write to $4220-$423F. Returns true when the extension took it (unlocked); false leaves it to the SNES path (the unlock writes themselves are watched either way).</summary>
    public bool WriteCpu(uint offset, byte value)
    {
        bool was = Unlocked;
        if (offset == 0x4220)
        {
            if (value == IdValue) unlockStep = 1;
            else if (unlockStep == 1 && value == 0x4D) { Unlocked = true; unlockStep = 0; Changed?.Invoke(); sound?.Unlock(); }
            else unlockStep = 0;
        }
        else if (was)
        {
            if (offset == 0x4225) { speed = (byte)(value & 1); Changed?.Invoke(); }
            else if (offset == 0x4226) { dma = (byte)(value & 1); Changed?.Invoke(); }
        }
        return was;
    }

    /// <summary>A CPU read of $4220-$423F while unlocked.</summary>
    public byte ReadCpu(uint offset) => offset switch
    {
        0x4221 => IdValue,
        0x4222 => SpecMajor,
        0x4223 => SpecMinor,
        0x4224 => Features,
        0x4225 => speed,
        0x4226 => dma,
        _ => 0,
    };

    /// <summary>The first B-bus register of the WideMess window ($21A0-$21FF; the Satellaview's $2188-$219F stays below it).</summary>
    public const byte FirstBBus = 0xA0;

    /// <summary>A B-bus write to $21A0-$21FF (CPU or DMA); ignored while locked. $21A0-$21EF are the picture's (spec 3), $21F0-$21F7 the sound RAM port (spec 4), $21F8-$21FF reserved.</summary>
    public void WriteB(byte reg, byte value)
    {
        if (!Unlocked || reg < FirstBBus) return;
        if (reg < 0xF0) ppu.WriteWide(reg, value);
        else if (reg < 0xF8) sound?.WriteSoundPort(reg, value, SoundClock());
    }

    /// <summary>A B-bus read of $21A0-$21FF while unlocked: the picture's and the sound RAM's data ports, open bus for their write-only registers, 0 for the parts not built yet and the reserved ones.</summary>
    public byte ReadB(byte reg, byte openBus)
    {
        if (reg >= 0xF0) return reg < 0xF8 && sound != null ? sound.ReadSoundPort(reg, openBus, SoundClock()) : (byte)0;
        int v = ppu.ReadWide(reg);
        return v < 0 ? openBus : (byte)v;
    }

    public void SaveState(BinaryWriter w) { w.Write(Unlocked); w.Write(unlockStep); w.Write(speed); w.Write(dma); }
    public void LoadState(BinaryReader r) { Unlocked = r.ReadBoolean(); unlockStep = r.ReadByte(); speed = r.ReadByte(); dma = r.ReadByte(); Changed?.Invoke(); }
}

/// <summary>The .wms game file (spec section 5): a 64-byte header, then the ROM image as the 65816 sees it under the header's map.</summary>
public static partial class WmsFile
{
    public const int HeaderSize = 0x40;
    public static ReadOnlySpan<byte> Magic => "WIDEMESS"u8;

    public static bool HasMagic(byte[] data) => data.Length >= HeaderSize && data.AsSpan(0, 8).SequenceEqual(Magic);

    public sealed record Header(byte Major, byte Minor, byte Map, bool Battery, int RomSize, int SramSize, uint Required, byte Region, string Title);

    /// <summary>The header, checked: magic, sizes, CRC, a spec and features this console has. Throws <see cref="InvalidDataException"/> with a message for the user, never runs half a game.</summary>
    public static Header Read(byte[] data)
    {
        if (!HasMagic(data)) throw new InvalidDataException("not a WideMess game: the file does not start with WIDEMESS");
        byte major = data[8], minor = data[9], map = data[0x0A];
        int romSize = BitConverter.ToInt32(data, 0x0C), sram = BitConverter.ToInt32(data, 0x10);
        uint required = BitConverter.ToUInt32(data, 0x14), crc = BitConverter.ToUInt32(data, 0x1C);
        if (romSize <= 0 || HeaderSize + (long)romSize != data.Length) throw new InvalidDataException($"the WideMess header says {romSize:N0} bytes of ROM but the file holds {data.Length - HeaderSize:N0}");
        uint actual = Crc32(data.AsSpan(HeaderSize));
        if (actual != crc) throw new InvalidDataException($"the WideMess game is damaged: its CRC-32 is {actual:X8}, the header says {crc:X8}");
        if (major > WmsExtension.SpecMajor || (major == WmsExtension.SpecMajor && minor > WmsExtension.SpecMinor))
            throw new InvalidDataException($"the game needs WideMess spec {major}.{minor}; this BrokenNes has {WmsExtension.SpecMajor}.{WmsExtension.SpecMinor}");
        if ((required & ~(uint)WmsExtension.Features) != 0)
            throw new InvalidDataException($"the game needs WideMess features this BrokenNes does not have yet (WMFEAT bits {required & ~(uint)WmsExtension.Features:X2}): {Missing(required & ~(uint)WmsExtension.Features)}");
        if (map > 2) throw new InvalidDataException($"unknown WideMess map {map} (0 LoROM, 1 HiROM, 2 the WideMess map)");
        if (map == 2) CheckWideMessMap(romSize, sram, (data[0x0B] & 1) != 0);   // WideMess.Memory.cs
        string title = Encoding.ASCII.GetString(data, 0x20, 32).TrimEnd(' ', '\0');
        return new Header(major, minor, map, (data[0x0B] & 1) != 0, romSize, sram, required, data[0x18], title);
    }

    private static string Missing(uint bits)
    {
        var names = new System.Collections.Generic.List<string>();
        string[] all = { "picture", "sprites+", "sound+", "fast CPU", "1 MB work RAM", "fast DMA", "bit 6", "debug port" };
        for (int i = 0; i < 8; i++) if ((bits & (1u << i)) != 0) names.Add(all[i]);
        return string.Join(", ", names);
    }

    /// <summary>The ROM image of a .wms file, or the data unchanged when it is a SNES ROM.</summary>
    public static byte[] Payload(byte[] data, out Header? header)
    {
        header = null;
        if (!HasMagic(data)) return data;
        header = Read(data);
        return data.AsSpan(HeaderSize).ToArray();
    }

    /// <summary>A .wms file around a SNES image (BrokenDev's wms-pack and the test ROMs use it).</summary>
    public static byte[] Pack(byte[] image, string title, byte map, uint required, int sramSize = 0, bool battery = false)
    {
        var f = new byte[HeaderSize + image.Length];
        Magic.CopyTo(f);
        f[8] = WmsExtension.SpecMajor; f[9] = WmsExtension.SpecMinor; f[0x0A] = map; f[0x0B] = (byte)(battery ? 1 : 0);
        BitConverter.TryWriteBytes(f.AsSpan(0x0C), image.Length);
        BitConverter.TryWriteBytes(f.AsSpan(0x10), sramSize);
        BitConverter.TryWriteBytes(f.AsSpan(0x14), required);
        BitConverter.TryWriteBytes(f.AsSpan(0x1C), Crc32(image));
        var t = Encoding.ASCII.GetBytes(title.PadRight(32)[..32]);
        t.CopyTo(f, 0x20);
        image.CopyTo(f, HeaderSize);
        return f;
    }

    private static uint[]? crcTable;
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var table = crcTable ??= BuildCrc();
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }
}

/// <summary>
/// The WideMess picture chip (spec section 3): the SNES's S-PPU (a subclass; the SNES modes draw on its pipeline untouched) plus 512 KB of video RAM (the SNES's 64 KB
/// is its first 64 KB), 1,024 colours (the SNES's 256 first), the WideMess mode (8 layers of 8-bpp tiles, each with its own map, tiles, colour bank and scroll) and the
/// 400-wide output; and (milestone 2, WideMess.Picture.cs) the sprites, windows and colour math of the WideMess mode and the SNES modes drawn 400 wide. Its registers ($21A0-$21EF)
/// are reached only through <see cref="WmsExtension"/> once a game unlocked them, so until then it is the SNES chip.
/// </summary>
public sealed partial class PPU_WMS : PPU_SFC
{
    public const int WideWidth = 400, Layers = 8;
    public const int VramBytes = 512 * 1024, Colours = 1024;

    /// <summary>Video RAM above the first 64 KB (which is <see cref="PPU_SFC.Vram"/>, so the SNES modes see what they always saw).</summary>
    public readonly byte[] VramHigh = new byte[VramBytes - 0x10000];
    /// <summary>Colours 256-1023 (0-255 are <see cref="PPU_SFC.Cgram"/>).</summary>
    public readonly ushort[] CgramHigh = new ushort[Colours - 256];
    /// <summary>The last frame at 400 pixels per line (when <see cref="FrameIsWide"/>).</summary>
    public readonly uint[] WideBuffer = new uint[WideWidth * MaxHeight];
    private readonly bool[] rowWide = new bool[MaxHeight];
    private bool anyWide;

    private byte ctrl, mainLayers;
    private int vaddr, cgaddr;
    private bool cgPhase; private byte cgLow;
    private readonly byte[] mapBase = new byte[Layers], config = new byte[Layers];
    private readonly ushort[] scrollH = new ushort[Layers], scrollV = new ushort[Layers];
    private readonly bool[] scrollPhase = new bool[2 * Layers];
    private readonly byte[] scrollLow = new byte[2 * Layers];

    /// <summary>True when the last frame has at least one line drawn 400 wide: the frame is <see cref="GetWideFrame"/>.</summary>
    public bool FrameIsWide => anyWide;
    /// <summary>WMCTRL bit 0 (400-pixel output) and bit 1 (the WideMess mode), as last written.</summary>
    public bool WideOutput => (ctrl & 1) != 0;
    public bool LayersMode => (ctrl & 2) != 0;

    public override void Reset()
    {
        base.Reset();
        ctrl = 0; mainLayers = 0; vaddr = 0; cgaddr = 0; cgPhase = false; cgLow = 0;
        Array.Clear(mapBase); Array.Clear(config); Array.Clear(scrollH); Array.Clear(scrollV); Array.Clear(scrollPhase); Array.Clear(scrollLow);
        ResetPicture();
    }

    public override void BeginFrame()
    {
        base.BeginFrame();
        if (anyWide) { Array.Clear(rowWide); anyWide = false; }
        BeginFramePicture();
    }

    // ---------------------------------------------------------------- registers ($21A0-$21EF; $21A0-$21BF in WideMess.Picture.cs)

    public void WriteWide(byte reg, byte v)
    {
        if (reg < 0xC0) { WritePicture(reg, v); return; }
        switch (reg)
        {
            case 0xC0: ctrl = (byte)(v & 3); Array.Clear(scrollPhase); return;
            case 0xC1: mainLayers = v; return;
            case 0xC2: vaddr = (vaddr & 0x7FF00) | v; return;
            case 0xC3: vaddr = (vaddr & 0x700FF) | v << 8; return;
            case 0xC4: vaddr = (vaddr & 0x0FFFF) | (v & 7) << 16; return;
            case 0xC5: if (VideoPortsOpen) SetVramByte(vaddr, v); vaddr = (vaddr + 1) & (VramBytes - 1); return;
            case 0xC6: cgaddr = (cgaddr & 0x300) | v; cgPhase = false; return;
            case 0xC7: cgaddr = (cgaddr & 0x0FF) | (v & 3) << 8; cgPhase = false; return;
            case 0xC8:
                if (!cgPhase) { cgLow = v; cgPhase = true; return; }
                if (VideoPortsOpen) SetColour(cgaddr, (ushort)(cgLow | (v & 0x7F) << 8));
                cgaddr = (cgaddr + 1) & (Colours - 1); cgPhase = false;
                return;
        }
        if (reg >= 0xD0 && reg < 0xD8) { mapBase[reg - 0xD0] = v; return; }
        if (reg >= 0xD8 && reg < 0xE0) { config[reg - 0xD8] = v; return; }
        if (reg >= 0xE0 && reg < 0xF0)
        {
            int i = reg - 0xE0, layer = i >> 1;
            if (!scrollPhase[i]) { scrollLow[i] = v; scrollPhase[i] = true; return; }
            ushort value = (ushort)((scrollLow[i] | v << 8) & 0x3FF);
            if ((i & 1) == 0) scrollH[layer] = value; else scrollV[layer] = value;
            scrollPhase[i] = false;
        }
    }

    /// <summary>The readable registers (the data ports); -1 for the write-only ones (open bus).</summary>
    public int ReadWide(byte reg)
    {
        switch (reg)
        {
            case 0xC5: { byte b = VramByte(vaddr); vaddr = (vaddr + 1) & (VramBytes - 1); return b; }
            case 0xC8:
            {
                ushort c = Colour(cgaddr);
                if (!cgPhase) { cgPhase = true; return c & 0xFF; }
                cgPhase = false; cgaddr = (cgaddr + 1) & (Colours - 1);
                return c >> 8;
            }
            default: return reg < 0xC0 ? ReadPicture(reg) : -1;
        }
    }

    // ---------------------------------------------------------------- memories

    public byte VramByte(int a) => a < 0x10000 ? (byte)(Vram[a >> 1] >> ((a & 1) << 3)) : VramHigh[a - 0x10000];

    public void SetVramByte(int a, byte v)
    {
        if (a >= 0x10000) { VramHigh[a - 0x10000] = v; return; }
        ref ushort w = ref Vram[a >> 1];
        w = (a & 1) == 0 ? (ushort)((w & 0xFF00) | v) : (ushort)((w & 0x00FF) | v << 8);
    }

    public ushort Colour(int i) => i < 256 ? Cgram[i] : CgramHigh[i - 256];
    public void SetColour(int i, ushort c) { if (i < 256) Cgram[i] = c; else CgramHigh[i - 256] = c; }

    // ---------------------------------------------------------------- the picture

    /// <summary>WMCTRL = 0: the SNES chip's own line, or with a console enhancement on (milestone 6, WideMess.Enhance.cs) the wide compositor for the locked SNES game. Otherwise the wide
    /// compositor (WideMess.Picture.cs): the WideMess mode, or a SNES mode drawn 400 wide.</summary>
    public override void RenderLine(int line)
    {
        if (ctrl == 0)
        {
            if (enhOn) EnhancedLine(line); else base.RenderLine(line);
            return;
        }
        RenderWideLine(line, (ctrl & 1) != 0, (ctrl & 2) != 0);
        if (CompareWithSnes && ctrl == 1) CompareLine(line);
    }

    /// <summary>The last frame at 400 wide: lines drawn in a SNES mode are centred in it (72 columns of black either side), as a SNES picture inside a wide screen.</summary>
    public uint[] GetWideFrame()
    {
        for (int row = 0; row < VisibleHeight; row++)
        {
            if (rowWide[row]) continue;
            var d = WideBuffer.AsSpan(row * WideWidth, WideWidth);
            d.Fill(0xFF000000u);
            FrameBuffer.AsSpan(row * Width, Width).CopyTo(d.Slice((WideWidth - Width) / 2, Width));
        }
        return WideBuffer;
    }

    // ---------------------------------------------------------------- state (the WideMess part; the SNES part is PPU_SFC's own)

    public void SaveWide(BinaryWriter w)
    {
        w.Write(ctrl); w.Write(mainLayers); w.Write(vaddr); w.Write(cgaddr); w.Write(cgPhase); w.Write(cgLow);
        w.Write(mapBase); w.Write(config);
        foreach (var s in scrollH) w.Write(s);
        foreach (var s in scrollV) w.Write(s);
        foreach (var p in scrollPhase) w.Write(p);
        w.Write(scrollLow);
        w.Write(VramHigh);
        foreach (var c in CgramHigh) w.Write(c);
    }

    public void LoadWide(BinaryReader r)
    {
        ctrl = r.ReadByte(); mainLayers = r.ReadByte(); vaddr = r.ReadInt32() & (VramBytes - 1); cgaddr = r.ReadInt32() & (Colours - 1); cgPhase = r.ReadBoolean(); cgLow = r.ReadByte();
        r.Read(mapBase); r.Read(config);
        for (int i = 0; i < Layers; i++) scrollH[i] = r.ReadUInt16();
        for (int i = 0; i < Layers; i++) scrollV[i] = r.ReadUInt16();
        for (int i = 0; i < scrollPhase.Length; i++) scrollPhase[i] = r.ReadBoolean();
        r.Read(scrollLow);
        if (r.Read(VramHigh) != VramHigh.Length) throw new EndOfStreamException("the WideMess video RAM is cut short");
        for (int i = 0; i < CgramHigh.Length; i++) CgramHigh[i] = r.ReadUInt16();
    }
}
