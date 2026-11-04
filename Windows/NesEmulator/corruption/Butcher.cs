using System;
using System.Text.Json.Serialization;

namespace NesEmulator
{
// BUTCHERING MODE (docs/projects/butchering-mode-plan.md): the Melted Electronics Glitch Boy's corruption MECHANISMS
// (researched in a separate knowledge base; ideas reimplemented, no firmware code), exploded into one control per effect.
//
// Three places, all on the buses between chips rather than in the game's memory:
//   * Picture bus - pin faults. Each address / data line can be stuck low, stuck high or bridged to its neighbour (wired AND),
//     with a flicker that makes the fault intermittent per fetch (a resistor's partial contention). Two pin groups:
//       console pins   - the PPU's own bus, before the mapper's banking: pattern, nametable and attribute fetches and $2007
//                        (PPU_FIX / FIXS only: the hook sits in the core's Read / Write);
//       cartridge pins - the CHR chip's lines, after the mapper's banking (IMapper.TryPpuToChrIndex; every PPU core).
//     "Bend writes too" lets PPU writes land bent as well, so the damage stays until the entry state is reloaded.
//   * Sprites - bytes on their way into OAM (DMA and $2004): classic jitter / tile / attribute hits and organic events.
//   * Sound   - writes on their way into the APU registers: classic bit flips / detune / channel drop-outs and organic events.
//
// Off = Bus.Butcher / Cartridge.Butcher are null: one null test on paths that already branch. Determinism: one xorshift32 per
// effect group, reseeded when the settings change, so a replay with the same settings does the same damage.
public sealed class ButcherSettings
{
	public bool Enabled { get; set; }
	public PinGroupSettings ConsolePins { get; set; } = new();
	public PinGroupSettings CartPins { get; set; } = new();
	public SpriteSettings Sprites { get; set; } = new();
	public SoundSettings Sound { get; set; } = new();
	public BandSettings Bands { get; set; } = new();
	public RamSettings Ram { get; set; } = new();
	public PaletteSettings Palette { get; set; } = new();
	/// <summary>NES: the cartridge's CIRAM A10 (mirroring) line: 0 connected, 1 stuck low, 2 stuck high, 3 inverted; and its flicker.</summary>
	public int CiramA10 { get; set; }
	public double CiramFlicker { get; set; }
	/// <summary>The video DAC's 24 input lines (R0-R7, G0-G7, B0-B7): stuck / bridged bits on the final colours (display only).</summary>
	public PinGroupSettings VideoDac { get; set; } = new();
	/// <summary>The palette RAM's own bus (NES palette, SNES CGRAM): the renderer reads the wrong entry / bent colour bits.</summary>
	public PinGroupSettings PaletteBus { get; set; } = new();
	public UnsafeSettings Unsafe { get; set; } = new();
}

/// <summary>The unsafe round: damage the game itself can trip over - it can crash or stay broken until Reload State.</summary>
public sealed class UnsafeSettings
{
	/// <summary>NES: the cartridge's PRG pins on the CPU bus (A0-A14, D0-D7) - instruction fetches and data reads from the cart.</summary>
	public PinGroupSettings CpuPins { get; set; } = new();
	/// <summary>The sound CPU's own bus (Genesis Z80 over its 8 KB RAM, SNES SPC700 over its 64 KB ARAM): the music program gets bent.</summary>
	public PinGroupSettings SoundCpuPins { get; set; } = new();
	/// <summary>Interrupt lines: a ghost NMI (an extra vertical-blank interrupt), dropped NMIs, a stuck IRQ line.</summary>
	public EventEffect GhostNmi { get; set; } = new();
	public EventEffect DropNmi { get; set; } = new();
	public EventEffect StuckIrq { get; set; } = new();
	/// <summary>The bus from the CPU to the video chip's registers (NES $2000-$2007, GB $FF40-$FF4B, SMS VDP ports, Genesis
	/// VDP ports, SNES B-bus $21xx): writes land on the wrong register or with bent bits - a bent VRAM address can leave damage.</summary>
	public PinGroupSettings VideoBus { get; set; } = new();
	/// <summary>The DMA controller's source bus (SNES DMA + HDMA tables, Genesis VDP DMA): bytes copied from the wrong place, bent.</summary>
	public PinGroupSettings DmaPins { get; set; } = new();
	/// <summary>A coprocessor's own bus (SNES SA-1 CPU, Super FX; DSP-1's data port: data lines only).</summary>
	public PinGroupSettings CoproPins { get; set; } = new();
	/// <summary>Player 1's controller lines, one per button (indexed by the PadButtons bit): 0 connected, 1 stuck released,
	/// 2 stuck pressed, 3 bridged to the next listed button (pressing either presses both).</summary>
	public int[] PadLines { get; set; } = Array.Empty<int>();
	public double PadFlicker { get; set; }
	/// <summary>Bit rot in the sound chip's own RAM (SPC700 ARAM, Z80 RAM, Game Boy wave RAM): samples decay, the driver may lock up.</summary>
	public RotEffect SoundRamRot { get; set; } = new();
	/// <summary>SNES: echo pushed to its longest buffer and strongest feedback - the echo overwrites sample memory.</summary>
	public EventEffect EchoAbuse { get; set; } = new();
	/// <summary>Blind bit flips anywhere in work RAM (stack, pointers, flags included).</summary>
	public RotEffect RamRot { get; set; } = new();
	/// <summary>Blind bit flips anywhere in video RAM (tiles, maps, the sprite table on chips that keep it there).</summary>
	public RotEffect VramRot { get; set; } = new();
}

public sealed class RotEffect { public bool On { get; set; } public double Rate { get; set; } = 25; public int Bits { get; set; } = 1; }

/// <summary>Glitch Boy's palette glitch: the picture's colours through a transform, never the game's own palette.</summary>
public sealed class PaletteSettings
{
	public bool On { get; set; }
	/// <summary>0 hue +60, 1 hue +120, 2 complement (+180), 3 hue +240, 4 hue + brightness flip (the device's 5 non-identity modes).</summary>
	public int Mode { get; set; }
	public bool Invert { get; set; }
	public bool ChannelSwap { get; set; }
	/// <summary>0 = off, else levels per channel (2-8).</summary>
	public int Posterize { get; set; }
	/// <summary>Steps to the next mode every N frames (0 = hold): the device's MIDI-clock auto mode.</summary>
	public int CycleFrames { get; set; }
}

/// <summary>Glitch Boy's organic "Probe": learn which RAM bytes are live variables and nudge them within their own range.</summary>
public sealed class RamSettings
{
	public bool On { get; set; }
	/// <summary>How often a nudge starts.</summary>
	public double Rate { get; set; } = 25;
	/// <summary>Size of each nudge, 1-8 (Glitch Boy: 1).</summary>
	public int Step { get; set; } = 1;
	/// <summary>How many frames a nudge keeps pushing.</summary>
	public int Lifetime { get; set; } = 60;
	/// <summary>Nudges running at once, 1-8 (Glitch Boy: 3).</summary>
	public int MaxProbes { get; set; } = 3;
}

/// <summary>One kind of scanline band: how often one spawns, how many lines it covers, how hard it hits, how long it lives.</summary>
public sealed class BandEffect
{
	public bool On { get; set; }
	public double Rate { get; set; } = 25;
	public int Width { get; set; } = 30;
	public int Strength { get; set; } = 8;
	public int Lifetime { get; set; } = 30;
}

/// <summary>Glitch Boy's organic raster engine (its Glitch A in organic style), one toggle per band type. PPU FIX only.</summary>
public sealed class BandSettings
{
	public BandEffect Shift { get; set; } = new();
	public BandEffect Slip { get; set; } = new();
	public BandEffect PatternSwap { get; set; } = new();
	public BandEffect NametableSwap { get; set; } = new();
	public BandEffect BgOff { get; set; } = new();
	public BandEffect SpritesOff { get; set; } = new();
	public BandEffect SpriteHeight { get; set; } = new();
	public BandEffect Bend { get; set; } = new();
	/// <summary>Console-specific band types (the panel names them from the console's reach: Mode 7 wobble, window shift...).</summary>
	public BandEffect Extra1 { get; set; } = new();
	public BandEffect Extra2 { get; set; } = new();
	public BandEffect Extra3 { get; set; } = new();
}

/// <summary>One line's band overrides, for any console: each renderer maps them onto its own registers.</summary>
public struct BandOverride
{
	/// <summary>Layers off on this line: 1 background (BG1 / plane A), 2 sprites, 4 BG2 / plane B / window, 8 BG3, 16 BG4.</summary>
	public int LayersOff;
	public bool PatternSwap, MapSwap;
	/// <summary>Horizontal slide (pixels) and the vertical slip in force on this line (sum of slips that started above).</summary>
	public int Shift, YOffset;
	/// <summary>Console extras active on this line (bit 0 = Extra1 ...) and their summed strengths.</summary>
	public int Extras, Extra1, Extra2, Extra3;
	public bool Any => LayersOff != 0 || PatternSwap || MapSwap || Shift != 0 || YOffset != 0 || Extras != 0;
}

/// <summary>Fault per line: 0 none, 1 stuck low, 2 stuck high, 3 bridged to the next line up (the top line bridges down).</summary>
public sealed class PinGroupSettings
{
	public int[] Address { get; set; } = Array.Empty<int>();
	public int[] Data { get; set; } = Array.Empty<int>();
	/// <summary>0 = solid fault, 100 = the fault almost never makes contact.</summary>
	public double Flicker { get; set; }
	public bool BendWrites { get; set; }
}

public sealed class RateEffect { public bool On { get; set; } public double Rate { get; set; } = 25; }
public sealed class JitterEffect { public bool On { get; set; } public double Rate { get; set; } = 25; public int Range { get; set; } = 8; }
public sealed class EventEffect { public bool On { get; set; } public double Rate { get; set; } = 25; public int Duration { get; set; } = 30; }
public sealed class BitsEffect { public bool[] Bits { get; set; } = Array.Empty<bool>(); public double Rate { get; set; } = 25; }
public sealed class DropEffect { public bool[] Channels { get; set; } = Array.Empty<bool>(); public double Rate { get; set; } = 25; public int Duration { get; set; } = 30; }
public sealed class ForceBitEffect { public bool On { get; set; } public double Rate { get; set; } = 25; public int Duration { get; set; } = 30; public int Field { get; set; } = 2; public int Bit { get; set; } = 7; public bool High { get; set; } = true; }
public sealed class ValueEventEffect { public bool On { get; set; } public double Rate { get; set; } = 25; public int Duration { get; set; } = 30; public int Value { get; set; } = 1; }

public sealed class SpriteSettings
{
	public JitterEffect XJitter { get; set; } = new();
	public JitterEffect YJitter { get; set; } = new();
	public RateEffect Tile { get; set; } = new();
	/// <summary>V-flip, H-flip, behind background, palette bit 1, palette bit 0.</summary>
	public BitsEffect Attr { get; set; } = new();
	public EventEffect GroupLag { get; set; } = new();
	public EventEffect Clone { get; set; } = new();
	public EventEffect FieldSwap { get; set; } = new();
	public ForceBitEffect ForceBit { get; set; } = new();
}

public sealed class SoundSettings
{
	/// <summary>Bits 0-4 of the register written.</summary>
	public BitsEffect BitFlip { get; set; } = new();
	public RateEffect Nudge { get; set; } = new();
	/// <summary>Pulse 1, pulse 2, triangle, noise, DMC.</summary>
	public DropEffect Drops { get; set; } = new();
	public EventEffect Stutter { get; set; } = new();
	public EventEffect Drone { get; set; } = new();
	/// <summary>Value = duty 0-3.</summary>
	public ValueEventEffect Duty { get; set; } = new();
	/// <summary>Value = XOR applied to the period low byte (1-255).</summary>
	public ValueEventEffect Detune { get; set; } = new();
	/// <summary>The bus between the CPU and the sound chip's registers: a write lands on the wrong register / with bent bits.</summary>
	public PinGroupSettings BusPins { get; set; } = new();
	/// <summary>Where the sound chip fetches its own samples (NES DMC, SNES BRR, Game Boy wave RAM, Genesis DAC data).</summary>
	public PinGroupSettings SamplePins { get; set; } = new();
	/// <summary>The DAC's 16 input lines: the final sample with stuck / bridged bits (display-only, like the palette).</summary>
	public PinGroupSettings DacPins { get; set; } = new();
	/// <summary>A drifting crystal: the output's pitch wobbles (depth in cents, speed in tenths of a hertz).</summary>
	public ClockWobble Wobble { get; set; } = new();
}

public sealed class ClockWobble { public bool On { get; set; } public int Depth { get; set; } = 30; public int Speed { get; set; } = 8; }

/// <summary>One compiled pin group: what a fetch's address and data go through.</summary>
internal sealed class PinFaults
{
	public readonly bool Active, AddrActive, DataActive, BendWrites;
	private readonly int addrAnd, addrOr, dataAnd, dataOr;
	private readonly int[] addrBridges, dataBridges; // pairs of bit numbers
	private readonly uint flickerThreshold;          // fault absent on a fetch when rng < this
	private uint rng;

	public PinFaults(PinGroupSettings? s, int addrLines, uint seed, int dataLines = 8)
	{
		rng = seed == 0 ? 0x6c8e9cf5u : seed;
		addrAnd = ~0; dataAnd = ~0;
		addrBridges = Compile(s?.Address, addrLines, ref addrAnd, ref addrOr);
		dataBridges = Compile(s?.Data, dataLines, ref dataAnd, ref dataOr);
		AddrActive = addrAnd != ~0 || addrOr != 0 || addrBridges.Length != 0;
		DataActive = dataAnd != ~0 || dataOr != 0 || dataBridges.Length != 0;
		Active = AddrActive || DataActive;
		BendWrites = s?.BendWrites == true;
		double f = Math.Clamp(s?.Flicker ?? 0, 0, 100) / 100.0;
		flickerThreshold = (uint)(Math.Min(f, 0.999) * uint.MaxValue);
	}

	private static int[] Compile(int[]? faults, int lines, ref int and, ref int or)
	{
		if (faults == null) return Array.Empty<int>();
		var bridges = new System.Collections.Generic.List<int>();
		for (int i = 0; i < Math.Min(faults.Length, lines); i++)
		{
			switch (faults[i])
			{
				case 1: and &= ~(1 << i); break;
				case 2: or |= 1 << i; break;
				case 3:
					int j = i + 1 < lines ? i + 1 : i - 1;
					if (j >= 0) { bridges.Add(i); bridges.Add(j); }
					break;
			}
		}
		return bridges.ToArray();
	}

	private uint Next() { uint x = rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; if (x == 0) x = 0x6c8e9cf5u; rng = x; return x; }

	/// <summary>True when the flicker leaves this fetch untouched.</summary>
	public bool Misses() => flickerThreshold != 0 && Next() < flickerThreshold;

	public int Addr(int a) => Apply(a, addrAnd, addrOr, addrBridges);
	public byte Data(byte v) => (byte)Apply(v, dataAnd, dataOr, dataBridges);
	public ushort Data16(ushort v) => (ushort)Apply(v, dataAnd, dataOr, dataBridges);
	public int Data24(int v) => Apply(v, dataAnd, dataOr, dataBridges) & 0xFFFFFF;

	private static int Apply(int v, int and, int or, int[] bridges)
	{
		v = (v & and) | or;
		for (int k = 0; k < bridges.Length; k += 2)
		{
			int a = bridges[k], b = bridges[k + 1];
			int both = (v >> a) & (v >> b) & 1; // wired AND: a low on either line pulls both low
			v = (v & ~((1 << a) | (1 << b))) | (both << a) | (both << b);
		}
		return v;
	}
}

/// <summary>What differs from console to console: the picture bus, the sprite record, the sound registers, the RAM window.
/// The panel builds itself from the same facts (NES.GetButcherReach / GbSession.GetButcherReach).</summary>
public sealed class ButcherProfile
{
	public string Console = "nes";
	public int ConsoleLines = 14;
	/// <summary>Sprite record: which raw byte (0-3) holds each canonical field 0 Y, 1 tile, 2 attributes, 3 X.</summary>
	public int[] RawOfField = { 0, 1, 2, 3 };
	/// <summary>Canonical field of each raw byte 0-3.</summary>
	public int[] FieldOfRaw = { 0, 1, 2, 3 };
	public int HiddenYMin = 0xEF, HiddenYZero = -1, YMin = 0, YMax = 0xEE, XMax = 0xFF;
	public int[] AttrBits = { 7, 6, 5, 1, 0 };
	public string[] AttrNames = { "V-flip", "H-flip", "Behind BG", "Palette 1", "Palette 0" };
	// sound: register r = address - SoundBase
	public int SoundBase = 0x4000;
	public string[] Channels = { "Pulse 1", "Pulse 2", "Triangle", "Noise", "DMC" };
	public Func<int, bool> SoundExcluded = r => r == 0x10 || r == 0x12 || r == 0x13 || r > 0x15;
	public Func<int, int> ChannelOf = r => r < 0x10 ? r >> 2 : -1;
	public int[] VolumeRegs = { 0x0, 0x4, 0xC }, DutyRegs = { 0x0, 0x4 }, PeriodLowRegs = { 0x2, 0x6, 0xA, 0xE };
	public Func<int, byte, byte> Drone = (r, v) => (byte)(v | 0x3F);
	public int DropReg = 0x15;
	public Func<byte, int, byte> DropApply = (v, mask) => (byte)(v & ~mask);
	/// <summary>The drop register's value before the game has written it: every channel on (NES $0F), or nothing keyed off (SNES KOF 0).</summary>
	public byte DropRegDefault = 0x0F;
	// RAM window the profiler watches, and the page it never touches (the stack)
	public int RamWindow = 0x800, StackPage = 1;

	public static readonly ButcherProfile Nes = new();
	public static readonly ButcherProfile Gb = new()
	{
		Console = "gb",
		ConsoleLines = 14,                                   // VRAM A0-A12 + the Color bank line
		RawOfField = new[] { 0, 2, 3, 1 }, FieldOfRaw = new[] { 0, 3, 1, 2 },   // Y, X, tile, flags
		HiddenYMin = 160, HiddenYZero = 0, YMin = 1, YMax = 159, XMax = 167,
		AttrBits = new[] { 6, 5, 7, 4, 0 },
		AttrNames = new[] { "V-flip", "H-flip", "Behind BG", "DMG palette", "Color palette 0" },
		SoundBase = 0,
		Channels = new[] { "Pulse 1", "Pulse 2", "Wave", "Noise" },
		SoundExcluded = r => r == 0x26 || r >= 0x30 || r < 0x10,                 // power and wave RAM are left alone
		ChannelOf = r => r >= 0x10 && r <= 0x14 ? 0 : r >= 0x15 && r <= 0x19 ? 1 : r >= 0x1A && r <= 0x1E ? 2 : r >= 0x1F && r <= 0x23 ? 3 : -1,
		VolumeRegs = new[] { 0x12, 0x17, 0x21, 0x1C }, DutyRegs = new[] { 0x11, 0x16 }, PeriodLowRegs = new[] { 0x13, 0x18, 0x1D, 0x22 },
		Drone = (r, v) => r == 0x1C ? (byte)((v & 0x9F) | 0x20) : (byte)0xF0,  // full volume, envelope held
		DropReg = 0x25,                                                         // NR51: a channel off on both sides
		DropApply = (v, mask) => (byte)(v & ~(mask | (mask << 4))),
		DropRegDefault = 0xFF,
		RamWindow = 0x2000, StackPage = 0x1F, VisibleLines = 144, CpuLines = 15, SoundBusLines = 6, SampleLines = 4, VideoBusLines = 4, DmaLines = 16,
	};
	/// <summary>Master System / Game Gear: the sprite table is in VRAM (no attribute byte), the PSG takes latch / data bytes.</summary>
	public static readonly ButcherProfile Sms = new()
	{
		Console = "sms",
		ConsoleLines = 14,                                   // VRAM A0-A13
		HiddenYMin = 0x100, HiddenYZero = 0xD0, YMin = 0, YMax = 0xCF, XMax = 0xFF,   // Y = $D0 ends the list
		AttrBits = Array.Empty<int>(), AttrNames = Array.Empty<string>(),
		Channels = new[] { "Tone 1", "Tone 2", "Tone 3", "Noise" },
		Psg = true,
		RamWindow = 0x2000, StackPage = 0x1F, VisibleLines = 192, CpuLines = 14, SoundBusLines = 0, SampleLines = 0, VideoBusLines = 1,
	};
	/// <summary>Master System with its FM unit (YM2413): the PSG's four channels, then FM 1-9.</summary>
	public static readonly ButcherProfile SmsFm = new()
	{
		Console = "sms",
		ConsoleLines = 14,
		HiddenYMin = 0x100, HiddenYZero = 0xD0, YMin = 0, YMax = 0xCF, XMax = 0xFF,
		AttrBits = Array.Empty<int>(), AttrNames = Array.Empty<string>(),
		Channels = new[] { "Tone 1", "Tone 2", "Tone 3", "Noise", "FM 1", "FM 2", "FM 3", "FM 4", "FM 5", "FM 6", "FM 7", "FM 8", "FM 9" },
		Psg = true,
		RamWindow = 0x2000, StackPage = 0x1F, VisibleLines = 192, CpuLines = 14, SoundBusLines = 0, SampleLines = 0, VideoBusLines = 1,
	};
	/// <summary>Genesis: 64 KB VRAM, PSG + YM2612, 64 KB work RAM.</summary>
	public static readonly ButcherProfile Md = new()
	{
		Console = "md",
		ConsoleLines = 16,
		HiddenYMin = 0x100, YMin = 0, YMax = 0xFF, XMax = 0xFF,
		AttrBits = new[] { 4, 3, 7, 6, 5 },
		AttrNames = new[] { "V-flip", "H-flip", "Priority", "Palette 1", "Palette 0" },
		Channels = new[] { "Tone 1", "Tone 2", "Tone 3", "Noise", "FM 1", "FM 2", "FM 3", "FM 4", "FM 5", "FM 6" },
		Psg = true,
		RamWindow = 0x10000, StackPage = 0xFF, VisibleLines = 224, CpuLines = 22, CpuDataLines = 16, SoundCpuLines = 13, DmaLines = 23, SoundBusLines = 2, SampleLines = 0, VideoBusLines = 4,
	};
	/// <summary>SNES: word VRAM (A0-A14), OAM records X, Y, tile, attributes; 128 KB work RAM; sound (SPC700 + DSP) not yet.</summary>
	public static readonly ButcherProfile Snes = new()
	{
		Console = "snes",
		ConsoleLines = 15, DataLines = 16,
		RawOfField = new[] { 1, 2, 3, 0 }, FieldOfRaw = new[] { 3, 0, 1, 2 },
		HiddenYMin = 0xE0, YMin = 0, YMax = 0xDF, XMax = 0xFF,
		AttrBits = new[] { 7, 6, 4, 3, 1 },
		AttrNames = new[] { "V-flip", "H-flip", "Priority", "Palette 2", "Palette 0" },
		// the S-DSP, reached at the SPC700's $F3 data port: voice n's registers at n*16 + 0-7, the globals at x8-xF
		SoundBase = 0,
		Channels = new[] { "Voice 1", "Voice 2", "Voice 3", "Voice 4", "Voice 5", "Voice 6", "Voice 7", "Voice 8" },
		SoundExcluded = r => (r & 0x0F) >= 8,          // KON, FLG, the echo buffer, the sample directory...: left alone
		ChannelOf = r => (r & 0x0F) < 8 ? r >> 4 : -1,
		VolumeRegs = new[] { 0x00, 0x01, 0x10, 0x11, 0x20, 0x21, 0x30, 0x31, 0x40, 0x41, 0x50, 0x51, 0x60, 0x61, 0x70, 0x71 },
		DutyRegs = Array.Empty<int>(),
		PeriodLowRegs = new[] { 0x02, 0x12, 0x22, 0x32, 0x42, 0x52, 0x62, 0x72 },
		Drone = (r, v) => 0x7F,
		DropReg = 0x5C,                                  // KOF: a dropped voice's key-off bit is held set
		DropApply = (v, mask) => (byte)(v | mask),
		DropRegDefault = 0x00,
		RamWindow = 0x20000, StackPage = 0x1F, VisibleLines = 224, CpuLines = 24, SoundCpuLines = 16, DmaLines = 24, SoundBusLines = 7, SampleLines = 16, VideoBusLines = 8,
	};
	public bool Psg;
	public int DataLines = 8;
	/// <summary>The CPU-bus pins the unsafe group bends (cartridge reads): address lines (A0..) and data lines.</summary>
	public int CpuLines = 15, CpuDataLines = 8;
	/// <summary>The sound CPU's address lines (0 = no separate sound CPU).</summary>
	public int SoundCpuLines;
	/// <summary>Sound-register bus address lines (0 = data only) and the sample-fetch address lines.</summary>
	public int SoundBusLines = 5, SampleLines = 15;
	/// <summary>The CPU-to-video register bus's address lines.</summary>
	public int VideoBusLines = 3;
	/// <summary>The DMA controller's source address lines (0 = not reached).</summary>
	public int DmaLines;
	public int VisibleLines = 240;
}

public sealed class Butcher
{
	public ButcherProfile Profile = ButcherProfile.Nes;
	/// <summary>The PPU's own address bus: A0-A13.</summary>
	public const int ConsoleAddressLines = 14;

	private ButcherSettings settings = new();
	internal PinFaults? Console;  // null = no console-pin fault
	internal PinFaults? Cart;     // null = no cartridge-pin fault
	internal PinFaults? CpuPins;  // null = no CPU-bus (PRG) pin fault - unsafe
	internal PinFaults? SoundCpu; // null = no sound-CPU bus fault - unsafe
	internal PinFaults? Dma, Copro; // null = no DMA / coprocessor bus fault - unsafe
	internal PinFaults? SoundBus, SampleFaults, Dac; // the sound pins (safe)
	public bool WobbleActive { get; private set; }
	public bool DacActive => Dac != null || WobbleActive;
	private int cartLines;
	public bool SpritesActive { get; private set; }
	public bool SoundActive { get; private set; }
	public bool BandsActive { get; private set; }
	public bool RamActive { get; private set; }
	public bool PaletteActive { get; private set; }
	internal PinFaults? VideoDacFaults, PaletteBusFaults, VideoBusFaults;
	// interrupt lines: the host wires these to its CPU
	public Action? GhostNmiLine; public Action<bool>? HoldIrqLine;
	public bool IrqActive { get; private set; }
	public bool DropNmiNow => dropLeft2 > 0;
	private int ghostLeft, dropLeft2, irqLeft;
	private void InterruptFrame()
	{
		var u = settings.Unsafe;
		bool ghostWas = ghostLeft > 0;
		Tick(ref ghostLeft, u.GhostNmi.On, u.GhostNmi.Rate, 1, null);
		if (!ghostWas && ghostLeft > 0) GhostNmiLine?.Invoke();
		Tick(ref dropLeft2, u.DropNmi.On, u.DropNmi.Rate, u.DropNmi.Duration, null);
		bool irqWas = irqLeft > 0;
		Tick(ref irqLeft, u.StuckIrq.On, u.StuckIrq.Rate, u.StuckIrq.Duration, null);
		if (irqLeft > 0) HoldIrqLine?.Invoke(true); else if (irqWas) HoldIrqLine?.Invoke(false);
	}
	/// <summary>A video-chip register write through the bent CPU-to-video bus (register index lines, data lines).</summary>
	public (int reg, int v) VideoBusBend(int reg, int v, int lines)
	{
		var f = VideoBusFaults!; if (f.Misses()) return (reg, v);
		if (f.AddrActive && lines > 0) { int m = (1 << lines) - 1; reg = (f.Addr(reg & m) & m) | (reg & ~m); }
		return (reg, f.DataActive ? f.Data16((ushort)v) : v);
	}
	/// <summary>A palette RAM read by the renderer through the bent palette bus (index lines, data lines).</summary>
	public int PaletteRead(int index, int lines, Func<int, int> read)
	{
		var f = PaletteBusFaults!; if (f.Misses()) return read(index);
		int m = (1 << lines) - 1;
		int v = read(f.AddrActive ? (f.Addr(index & m) & m) | (index & ~m) : index);
		return f.DataActive ? f.Data16((ushort)v) : v;
	}
	public int CiramFault { get; private set; }
	private uint rngCiram = 0x3C6EF372u;
	/// <summary>A nametable RAM index (0-0x7FF) through the faulted A10 line.</summary>
	public ushort CiramA10(ushort i)
	{
		if (settings.CiramFlicker > 0 && Step(ref rngCiram) < (uint)(Math.Clamp(settings.CiramFlicker, 0, 99.9) / 100.0 * uint.MaxValue)) return i;
		return CiramFault switch { 1 => (ushort)(i & ~0x400), 2 => (ushort)(i | 0x400), 3 => (ushort)(i ^ 0x400), _ => i };
	}
	public bool RotActive { get; private set; }
	public bool PadActive { get; private set; }

	private uint rngSprites = 0x1234567u, rngSound = 0x7654321u;

	public ButcherSettings Settings => settings;
	public int CartAddressLines => cartLines;

	/// <summary>Takes a new set of controls. The CHR chip's size sets how many cartridge address lines exist.</summary>
	public void Apply(ButcherSettings s, int chrChipSize)
	{
		settings = s ?? new ButcherSettings();
		cartLines = AddressLinesFor(chrChipSize);
		var on = settings.Enabled;
		var c = new PinFaults(settings.ConsolePins, Profile.ConsoleLines, 0x9e3779b9u, Profile.DataLines);
		var k = new PinFaults(settings.CartPins, cartLines, 0x85ebca6bu);
		Console = on && c.Active ? c : null;
		Cart = on && k.Active ? k : null;
		var sp = settings.Sprites;
		SpritesActive = on && (sp.XJitter.On || sp.YJitter.On || sp.Tile.On || Any(sp.Attr.Bits)
			|| sp.GroupLag.On || sp.Clone.On || sp.FieldSwap.On || sp.ForceBit.On);
		var so = settings.Sound;
		SoundActive = on && (Any(so.BitFlip.Bits) || so.Nudge.On || Any(so.Drops.Channels)
			|| so.Stutter.On || so.Drone.On || so.Duty.On || so.Detune.On);
		var bd = settings.Bands;
		BandsActive = on && (bd.Shift.On || bd.Slip.On || bd.PatternSwap.On || bd.NametableSwap.On || bd.BgOff.On || bd.SpritesOff.On || bd.SpriteHeight.On || bd.Bend.On
			|| bd.Extra1.On || bd.Extra2.On || bd.Extra3.On);
		if (!BandsActive) { Array.Clear(bandLife); LineBendMask = 0; TallXor = 0; }
		RamActive = on && settings.Ram.On;
		var vd = new PinFaults(settings.VideoDac, 1, 0x1B873593u, 24);
		VideoDacFaults = on && vd.DataActive ? vd : null;
		PaletteActive = on && (settings.Palette.On || VideoDacFaults != null);
		CiramFault = on ? Math.Clamp(settings.CiramA10, 0, 3) : 0;
		var pb = new PinFaults(settings.PaletteBus, 8, 0x61C88647u, 16);
		PaletteBusFaults = on && pb.Active ? pb : null;
		var cp = new PinFaults(settings.Unsafe.CpuPins, Profile.CpuLines, 0xC2B2AE35u, Profile.CpuDataLines);
		CpuPins = on && cp.Active ? cp : null;
		var sc = new PinFaults(settings.Unsafe.SoundCpuPins, Math.Max(1, Profile.SoundCpuLines), 0x27D4EB2Fu);
		SoundCpu = on && Profile.SoundCpuLines > 0 && sc.Active ? sc : null;
		var so2 = settings.Sound;
		var sbp = new PinFaults(so2.BusPins, Math.Max(1, Profile.SoundBusLines), 0x7FEB352Du); SoundBus = on && sbp.Active ? sbp : null;
		var smp = new PinFaults(so2.SamplePins, Math.Max(1, Profile.SampleLines), 0x846CA68Bu); SampleFaults = on && smp.Active ? smp : null;
		var dac = new PinFaults(so2.DacPins, 1, 0x2C1B3C6Du, 16); Dac = on && dac.Active ? dac : null;
		WobbleActive = on && so2.Wobble.On && so2.Wobble.Depth > 0;
		var vb = new PinFaults(settings.Unsafe.VideoBus, Math.Max(1, Profile.VideoBusLines), 0x9E3779B1u, Profile.CpuDataLines);
		VideoBusFaults = on && vb.Active ? vb : null;
		var dm = new PinFaults(settings.Unsafe.DmaPins, Math.Max(1, Profile.DmaLines), 0x165667B1u, Profile.CpuDataLines);
		Dma = on && Profile.DmaLines > 0 && dm.Active ? dm : null;
		var co = new PinFaults(settings.Unsafe.CoproPins, 24, 0xD3A2646Cu);
		Copro = on && Profile.Console == "snes" && co.Active ? co : null;
		IrqActive = on && (settings.Unsafe.GhostNmi.On || settings.Unsafe.DropNmi.On || settings.Unsafe.StuckIrq.On);
		if (!IrqActive) { ghostLeft = dropLeft2 = 0; if (irqLeft > 0) { irqLeft = 0; HoldIrqLine?.Invoke(false); } }
		RotActive = on && (settings.Unsafe.RamRot.On || settings.Unsafe.VramRot.On || settings.Unsafe.SoundRamRot.On || settings.Unsafe.EchoAbuse.On);
		PadActive = on && settings.Unsafe.PadLines is { } pl && Array.Exists(pl, x => x != 0);
		paletteLut.Clear(); paletteMode = Math.Clamp(settings.Palette.Mode, 0, 4); paletteTick = 0;
		if (!RamActive) Array.Clear(probeLife);
		rngSprites = 0x1234567u; rngSound = 0x7654321u; rngBands = 0x2545F491u;
		if (!SpritesActive) { lagLeft = cloneLeft = swapLeft = forceLeft = 0; }
		if (!SoundActive || !Any(so.Drops.Channels)) pendingDropRelease = dropMask != 0;
		if (!SoundActive) { stutterLeft = droneLeft = dutyLeft = detuneLeft = 0; }
	}

	/// <summary>Nothing is on: the hooks can be detached.</summary>
	public bool Idle => !IrqActive && irqLeft == 0 && CiramFault == 0 && PaletteBusFaults == null && VideoBusFaults == null && !PadActive && SoundBus == null && SampleFaults == null && !DacActive && Dma == null && Copro == null && Console == null && Cart == null && CpuPins == null && SoundCpu == null && !SpritesActive && !SoundActive && !BandsActive && dropMask == 0 && !RamActive && !PaletteActive && !RotActive;

	public static int AddressLinesFor(int size)
	{
		int n = 0; while (n < 24 && (1 << n) < size) n++;
		return n;
	}

	private static bool Any(bool[]? b) { if (b == null) return false; foreach (var x in b) if (x) return true; return false; }

	private static uint Step(ref uint x) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; if (x == 0) x = 0x6c8e9cf5u; return x; }
	private uint RS() => Step(ref rngSprites);
	private uint RA() => Step(ref rngSound);
	/// <summary>Rate 0-100 to a per-event threshold, squared like the device's knob so the low end is fine-grained.</summary>
	private static uint Threshold(double rate) { double p = Math.Clamp(rate, 0, 100) / 100.0; return (uint)(p * p * uint.MaxValue); }
	/// <summary>Per-frame chance an event starts: at most one in four frames.</summary>
	private static uint EventThreshold(double rate) { double p = Math.Clamp(rate, 0, 100) / 100.0; return (uint)(p * p * 0.25 * uint.MaxValue); }

	// ===================================================================================== picture bus
	/// <summary>Console pins, on a PPU read: returns the address the bus actually carries, or -1 to read normally.</summary>
	/// <summary>True when the console pins or a bend band touch PPU fetches right now.</summary>
	public bool ConsoleBent => Console != null || LineBendMask != 0;

	public int ConsoleReadAddr(int address, out bool bendData)
	{
		var f = Console;
		bendData = false;
		int a = address;
		if (f != null && !f.Misses())
		{
			bendData = f.DataActive;
			if (f.AddrActive) a = f.Addr(address);
		}
		a &= ~LineBendMask;
		if (a == address && !bendData) return -1;
		a &= 0x3FFF;
		// The palette is inside the PPU, decoded before the pins: a bent fetch that lands there reads the nametable mirror instead.
		if (a >= 0x3F00) a -= 0x1000;
		return a;
	}
	public byte ConsoleData(byte v) => Console != null ? Console.Data(v) : v;

	/// <summary>Console pins on a chip that reads a plain memory array (the Game Boy's VRAM): the byte the bent bus delivers.</summary>
	public byte PictureRead(byte[] mem, int a)
	{
		var f = Console;
		if (f == null || f.Misses()) return LineBendMask == 0 ? mem[a] : mem[a & ~LineBendMask];
		int b = (f.AddrActive ? f.Addr(a) : a) & ~LineBendMask & (mem.Length - 1);
		byte v = mem[b];
		return f.DataActive ? f.Data(v) : v;
	}

	/// <summary>The same for a word-wide VRAM (the SNES).</summary>
	public ushort PictureReadWord(ushort[] mem, int a)
	{
		var f = Console;
		if (f == null || f.Misses()) return LineBendMask == 0 ? mem[a] : mem[a & ~LineBendMask];
		int b = (f.AddrActive ? f.Addr(a) : a) & ~LineBendMask & (mem.Length - 1);
		ushort v = mem[b];
		return f.DataActive ? f.Data16(v) : v;
	}

	public void PictureWriteWord(ushort[] mem, int a, ushort v)
	{
		var f = Console;
		if (f is not { BendWrites: true } || f.Misses()) { mem[a] = v; return; }
		int b = f.AddrActive ? f.Addr(a) & (mem.Length - 1) : a;
		mem[b] = f.DataActive ? f.Data16(v) : v;
	}

	/// <summary>A write to that memory, with "bend writes too": it lands at the bent address with the bent value.</summary>
	public void PictureWrite(byte[] mem, int a, byte v)
	{
		var f = Console;
		if (f is not { BendWrites: true } || f.Misses()) { mem[a] = v; return; }
		int b = f.AddrActive ? f.Addr(a) & (mem.Length - 1) : a;
		mem[b] = f.DataActive ? f.Data(v) : v;
	}

	// ===================================================================================== RAM nudges
	// Profiler over CPU RAM $0000-$07FF minus the stack page and the sprite DMA page (Glitch Boy skips zero page too; SMB-era
	// games keep their moving objects there, so it is included and the filters below keep its pointers out): 32 random samples; any that changes becomes one of 16
	// watches; every 12 frames a watch that moved both ways, in balanced small steps (< 7), over a bounded range (16-224),
	// earns a point (2 = confirmed); one that fails 7 times is dropped. Nudges only push confirmed watches, one step at a
	// time, and turn back (or stop) at the edge of the range the game itself produced. Pointers, flags, counters that
	// jump and RNG state never pass the profiler, which is what keeps the game standing.
	private byte[]? ram;
	private int dmaPage = 2;
	public void AttachRam(byte[] r) => ram = r;
	public void NoteDmaPage(byte page) => dmaPage = page < 0x20 ? page & 7 : -1;
	/// <summary>The sprite DMA's source page inside the profiled RAM (-1 = not RAM).</summary>
	public void NoteDmaRamPage(int page) => dmaPage = page;
	private readonly int[] sampleOff = new int[32]; private readonly byte[] sampleLast = new byte[32]; private readonly bool[] sampleMoved = new bool[32];
	private readonly int[] wOff = new int[16]; private readonly byte[] wCur = new byte[16], wMin = new byte[16], wMax = new byte[16];
	private readonly int[] wIncs = new int[16], wDecs = new int[16], wMaxStep = new int[16], wCls = new int[16], wStrikes = new int[16];
	private readonly bool[] wUsed = new bool[16];
	private int profilerFrames; private bool profilerSeeded; private uint rngRam = 0x68E31DA4u;
	private readonly int[] probeWatch = new int[8], probeStep = new int[8], probeLife = new int[8], probePeriod = new int[8];
	private int frameNo;
	private uint RR() => Step(ref rngRam);

	private int RandomRamOffset()
	{
		int window = Math.Min(ram?.Length ?? 0, Profile.RamWindow);
		while (true) { int o = (int)(RR() % (uint)window); if ((o >> 8) != Profile.StackPage && (o >> 8) != dmaPage) return o; } // not the stack, not the sprite page
	}

	private void RamFrame()
	{
		var r = ram; if (r == null || r.Length < 0x200) return;
		frameNo++;
		if (!profilerSeeded) { for (int i = 0; i < 32; i++) { sampleOff[i] = RandomRamOffset(); sampleLast[i] = r[sampleOff[i]]; } profilerSeeded = true; }
		for (int i = 0; i < 32; i++)
		{
			int o = sampleOff[i];
			if (r[o] != sampleLast[i]) { Promote(o, r); sampleOff[i] = RandomRamOffset(); sampleMoved[i] = true; }
			sampleLast[i] = r[sampleOff[i]];
		}
		for (int w = 0; w < 16; w++)
		{
			if (!wUsed[w]) continue;
			if ((wOff[w] >> 8) == dmaPage) { wUsed[w] = false; continue; }
			int d = (sbyte)(byte)(r[wOff[w]] - wCur[w]);
			if (Math.Abs(d) >= 7) { wUsed[w] = false; continue; }  // pointers, flags, RNG jump
			byte v = r[wOff[w]];
			if (v < wMin[w]) wMin[w] = v; if (v > wMax[w]) wMax[w] = v;
			if (d > 0) wIncs[w]++; else if (d < 0) wDecs[w]++;
			wMaxStep[w] = Math.Max(wMaxStep[w], Math.Abs(d)); wCur[w] = v;
		}
		if (++profilerFrames >= 12)
		{
			profilerFrames = 0;
			// samples that sat on a quiet byte for the whole window move on (otherwise they settle on static bytes for good)
			for (int i = 0; i < 32; i++) if (!sampleMoved[i]) { sampleOff[i] = RandomRamOffset(); sampleLast[i] = r[sampleOff[i]]; }
			Array.Clear(sampleMoved);
			for (int w = 0; w < 16; w++)
			{
				if (!wUsed[w]) continue;
				int lo = Math.Min(wIncs[w], wDecs[w]), hi = Math.Max(wIncs[w], wDecs[w]), range = wMax[w] - wMin[w];
				bool looks = wMaxStep[w] < 7 && lo >= 2 && hi <= 8 * lo && range >= 16 && range <= 0xE0;
				if (looks) { if (wCls[w] < 2) wCls[w]++; }
				else if (wCls[w] < 2 && ++wStrikes[w] >= 7) wUsed[w] = false;
				if (wIncs[w] + wDecs[w] > 200) { wIncs[w] /= 2; wDecs[w] /= 2; } // counts span the watch's life, slowly forgetting
			}
		}
		// nudges
		var rs = settings.Ram;
		int max = Math.Clamp(rs.MaxProbes, 1, 8), running = 0;
		for (int p = 0; p < 8; p++)
		{
			if (probeLife[p] == 0) continue;
			if (p >= max || --probeLife[p] == 0 || !wUsed[probeWatch[p]] || wCls[probeWatch[p]] < 2) { probeLife[p] = 0; continue; }
			running++;
			if (frameNo % probePeriod[p] != 0) continue;
			int w = probeWatch[p], o = wOff[w];
			int v = r[o] + probeStep[p];
			if (v < wMin[w] || v > wMax[w]) { probeStep[p] = -probeStep[p]; v = r[o] + probeStep[p]; if (v < wMin[w] || v > wMax[w]) { probeLife[p] = 0; continue; } }
			r[o] = (byte)v; wCur[w] = (byte)v;
		}
		if (running < max && RR() < EventThreshold(rs.Rate))
		{
			int n = 0; Span<int> conf = stackalloc int[16];
			for (int w = 0; w < 16; w++) if (wUsed[w] && wCls[w] >= 2) conf[n++] = w;
			int slot = Array.IndexOf(probeLife, 0);
			if (n > 0 && slot >= 0 && slot < max)
			{
				int step = Math.Clamp(rs.Step, 1, 8);
				probeWatch[slot] = conf[(int)(RR() % (uint)n)];
				probeStep[slot] = (RR() & 1) != 0 ? step : -step;
				probeLife[slot] = Math.Clamp(rs.Lifetime, 1, 3600);
				probePeriod[slot] = 1 + (int)(RR() % 3);
			}
		}
	}

	private void Promote(int off, byte[] r)
	{
		for (int w = 0; w < 16; w++) if (wUsed[w] && wOff[w] == off) return;
		int slot = Array.IndexOf(wUsed, false);
		if (slot < 0) return;
		wUsed[slot] = true; wOff[slot] = off; wCur[slot] = wMin[slot] = wMax[slot] = r[off];
		wIncs[slot] = wDecs[slot] = wMaxStep[slot] = wCls[slot] = wStrikes[slot] = 0;
	}

	/// <summary>The profiler's view, for the panel: live watches and how many are confirmed variables.</summary>
	public (int watches, int confirmed, int nudging) RamStats()
	{
		int a = 0, c = 0, n = 0;
		for (int w = 0; w < 16; w++) if (wUsed[w]) { a++; if (wCls[w] >= 2) c++; }
		foreach (var l in probeLife) if (l > 0) n++;
		return (a, c, n);
	}

	// ===================================================================================== bands (PPU FIX)
	// Up to six bands at once, each a range of lines with one effect; they wobble a line now and then and expire.
	private const int BandSlots = 6;
	private readonly int[] bandType = new int[BandSlots], bandStart = new int[BandSlots], bandEnd = new int[BandSlots],
		bandParam = new int[BandSlots], bandLife = new int[BandSlots];
	private uint rngBands = 0x2545F491u;
	/// <summary>Address lines held low on the line being drawn (a bend band).</summary>
	public int LineBendMask { get; private set; }
	/// <summary>PPUCTRL bit 5 (sprite height) flipped for this frame by a sprite-height band.</summary>
	public int TallXor { get; private set; }
	private uint RB() => Step(ref rngBands);

	private void BandsFrame()
	{
		var bd = settings.Bands;
		TallXor = 0;
		for (int i = 0; i < BandSlots; i++)
		{
			if (bandLife[i] == 0) continue;
			if (--bandLife[i] == 0) continue;
			if ((RB() & 15) == 0 && bandStart[i] > 0 && bandEnd[i] < Profile.VisibleLines) { int s = (RB() & 1) != 0 ? 1 : -1; bandStart[i] += s; bandEnd[i] += s; }
			if (bandType[i] == 6) TallXor = 0x20;
		}
		var kinds = new[] { bd.Shift, bd.Slip, bd.PatternSwap, bd.NametableSwap, bd.BgOff, bd.SpritesOff, bd.SpriteHeight, bd.Bend, bd.Extra1, bd.Extra2, bd.Extra3 };
		for (int t = 0; t < kinds.Length; t++)
		{
			var k = kinds[t];
			if (!k.On || RB() >= EventThreshold(k.Rate)) continue;
			int slot = Array.IndexOf(bandLife, 0);
			if (slot < 0) break;
			int width = Math.Clamp(k.Width, 1, 240), strength = Math.Clamp(k.Strength, 1, 64);
			int lines = Profile.VisibleLines;
			int start = (int)(RB() % (uint)lines), end = Math.Min(lines, start + 1 + (int)(RB() % (uint)width));
			int param = 1 + (int)(RB() % (uint)strength); if ((RB() & 1) != 0) param = -param;
			if (t == 1) end = lines;                                 // a vertical slip carries to the bottom
			if (t == 6) { start = 0; end = lines; TallXor = 0x20; }   // sprite height: the whole frame
			if (t == 7) { param = 0; int bendLines = 1 + strength / 16; for (int j = 0; j < bendLines; j++) param |= 1 << (int)(RB() % 9); } // A0-A8
			bandType[slot] = t; bandStart[slot] = start; bandEnd[slot] = end; bandParam[slot] = param;
			bandLife[slot] = Math.Clamp(k.Lifetime, 1, 3600);
		}
	}

	/// <summary>The overrides for one visible line, for every console but the NES FIX path (which uses BandLine below).</summary>
	public BandOverride BandLineEx(int line)
	{
		var o = new BandOverride();
		int bend = 0;
		for (int i = 0; i < BandSlots; i++)
		{
			if (bandLife[i] == 0) continue;
			if (bandType[i] == 1) { if (line >= bandStart[i]) o.YOffset += bandParam[i]; continue; }   // a slip moves everything below its start
			if (line < bandStart[i] || line >= bandEnd[i]) continue;
			int p = bandParam[i];
			switch (bandType[i])
			{
				case 0: o.Shift += p; break;
				case 2: o.PatternSwap = true; break;
				case 3: o.MapSwap = true; break;
				case 4: o.LayersOff |= 1 | 4 | 8 | 16; break;   // every background layer
				case 5: o.LayersOff |= 2; break;
				case 7: bend |= p; break;
				case 8: o.Extras |= 1; o.Extra1 += p; break;
				case 9: o.Extras |= 2; o.Extra2 += p; break;
				case 10: o.Extras |= 4; o.Extra3 += p; break;
			}
		}
		LineBendMask = bend;
		return o;
	}

	/// <summary>Slides one finished row of a 32-bit picture by <paramref name="by"/> pixels (left when positive), wrapping.</summary>
	public static void ShiftRow(uint[] frame, int offset, int width, int by)
	{
		by = ((by % width) + width) % width; if (by == 0) return;
		var tmp = System.Buffers.ArrayPool<uint>.Shared.Rent(width);
		Array.Copy(frame, offset, tmp, 0, width);
		for (int x = 0; x < width; x++) frame[offset + x] = tmp[(x + by) % width];
		System.Buffers.ArrayPool<uint>.Shared.Return(tmp);
	}

	// The NES PPU cores other than FIX: bands through PPUCTRL / PPUMASK for the coming line (pattern table swap, sprite height,
	// background / sprites off) plus the bend lines on their PPU reads; put back at the next line unless the CPU rewrote them.
	private bool nesOverlayOn; private byte nesSavCtrl, nesSavMask, nesAppCtrl, nesAppMask;

	/// <summary>Called by a NES PPU core as it moves to <paramref name="newLine"/>.</summary>
	public void NesCoreLine(int newLine, ref byte ctrl, ref byte mask)
	{
		if (nesOverlayOn)
		{
			nesOverlayOn = false;
			if (ctrl == nesAppCtrl) ctrl = nesSavCtrl;
			if (mask == nesAppMask) mask = nesSavMask;
		}
		if (!BandsActive || newLine >= 240) { LineBendMask = 0; return; }
		var o = BandLineEx(newLine);
		if (!o.Any && TallXor == 0) return;
		nesSavCtrl = ctrl; nesSavMask = mask;
		if (o.PatternSwap) ctrl ^= 0x10;
		if (TallXor != 0) ctrl ^= 0x20;
		if ((o.LayersOff & 1) != 0) mask &= unchecked((byte)~0x08);
		if ((o.LayersOff & 2) != 0) mask &= unchecked((byte)~0x10);
		nesAppCtrl = ctrl; nesAppMask = mask;
		nesOverlayOn = true;
	}

	/// <summary>The overrides for one visible line (PPU FIX calls this at the end of the line before it).</summary>
	public void BandLine(int line, out int maskClear, out int ptXor, out int ntXor, out int shift, out int ySteps)
	{
		maskClear = ptXor = ntXor = shift = ySteps = 0;
		int bend = 0;
		for (int i = 0; i < BandSlots; i++)
		{
			if (bandLife[i] == 0 || line < bandStart[i] || line >= bandEnd[i]) continue;
			switch (bandType[i])
			{
				case 0: shift += bandParam[i]; break;
				case 1: if (line == bandStart[i]) ySteps += bandParam[i]; break;
				case 2: ptXor = 0x10; break;
				case 3: ntXor = 0x400; break;
				case 4: maskClear |= 0x08; break;
				case 5: maskClear |= 0x10; break;
				case 7: bend |= bandParam[i]; break;
			}
		}
		LineBendMask = bend;
	}

	// ===================================================================================== sprite-0 safety net
	// Every PPU core but FIX (which forces the hit itself, dot-exactly): when the game reads $2002 with the hit still clear,
	// rendering on and the PPU's line counter past sprite 0's last row, the read reports the hit. A game polling for the
	// split (SMB's status bar) then cannot spin forever inside its NMI when bent tiles or a jittered sprite 0 lost it.
	private byte ppuCtrl, ppuMask = 0x18;
	private int sprite0Y = 0xFF;
	public void NotePpuRegister(ushort reg, byte v) { if (reg == 0x2000) ppuCtrl = v; else ppuMask = v; }
	public void NoteSprite0(byte y) => sprite0Y = y;
	public byte Sprite0Guard(byte status, IPPU ppu)
	{
		if ((status & 0x40) != 0 || ppu is PPU_FIX || ppu is not IPpuFrameClock clock) return status;
		if ((ppuMask & 0x18) != 0x18 || sprite0Y >= 0xEF) return status;
		int line = clock.ProbeScanline, h = (ppuCtrl & 0x20) != 0 ? 16 : 8;
		return line < 240 && line > sprite0Y + h ? (byte)(status | 0x40) : status;
	}

	// ===================================================================================== sprites
	private readonly byte[] prevOam = new byte[256];
	private int lagLeft, lagGroup, cloneLeft, cloneSprite, swapLeft, swapBy, forceLeft;

	/// <summary>Called before a DMA overwrites OAM: remembers what was there (for group lag).</summary>
	public void BeforeOamDma(byte[] oam) { Buffer.BlockCopy(oam, 0, prevOam, 0, Math.Min(256, oam.Length)); }

	private bool Hidden(byte y) => y >= Profile.HiddenYMin || y == Profile.HiddenYZero;

	/// <summary>Filters all of OAM after a DMA landed.</summary>
	public void AfterOamDma(byte[] oam)
	{
		int len = Math.Min(256, oam.Length & ~3), sprites = len / 4;
		if (sprites < 4) return;
		var sp = settings.Sprites;
		int yRaw = Profile.RawOfField[0];
		// organic: whole-sprite effects first, from the bytes the game sent
		if (cloneLeft > 0) for (int s = cloneSprite; s < sprites; s += 7) for (int i = 0; i < 4; i++) oam[s * 4 + i] = oam[(s - 2) * 4 + i];
		if (swapLeft > 0)
			for (int s = 1; s < sprites; s++)
			{
				int b0 = s * 4; byte y = oam[b0 + yRaw];
				byte[] f = { oam[b0], oam[b0 + 1], oam[b0 + 2], oam[b0 + 3] };
				for (int i = 0; i < 4; i++) oam[b0 + i] = f[(i - swapBy + 4) & 3];
				if (Hidden(y)) oam[b0 + yRaw] = y; // a hidden sprite stays hidden
			}
		for (int i = 0; i < len; i++) oam[i] = FilterByte(i, oam[i], sp, organicGroups: true);
	}

	// Tables written a field at a time (Master System / Game Gear, Genesis, SNES, NES $2004): the organic events work on a
	// per-sprite field table - last frame's values (group lag), the latest ones (clones from two sprites back, field swap).
	// Y keeps its own value whenever either side is a hidden / end-of-list marker, so the list is never cut or revealed.
	private readonly byte[] fieldPrev = new byte[128 * 4], fieldCur = new byte[128 * 4];

	/// <summary>A sprite-table byte by canonical field (0 Y, 1 tile, 2 attributes, 3 X), for chips whose table is not 4-byte records.</summary>
	public byte FilterSpriteField(int field, int sprite, byte b)
	{
		var sp = settings.Sprites;
		int s = sprite & 127, k = s * 4 + (field & 3);
		byte orig = b;
		if (lagLeft > 0 && s > 0 && (s >> 3) % 8 == lagGroup) b = fieldPrev[k];
		else if (cloneLeft > 0 && s >= 2 && (s - cloneSprite) % 7 == 0) b = fieldCur[k - 8];
		else if (swapLeft > 0 && field != 0) b = fieldCur[s * 4 + ((field - swapBy) & 3)];
		if (field == 0 && (Hidden(orig) || Hidden(b))) b = orig;
		fieldCur[k] = orig;
		return FilterField(field, b, sp);
	}

	/// <summary>The SNES sprite high table (X bit 8 and the size bit of four sprites): the attribute-flip effect, one bit.</summary>
	public byte FilterSpriteHigh(byte v)
	{
		var sp = settings.Sprites;
		if (!Any(sp.Attr.Bits) || RS() >= Threshold(sp.Attr.Rate)) return v;
		return (byte)(v ^ (1 << (int)(RS() % 8)));
	}

	/// <summary>A single OAM byte written through $2004.</summary>
	public byte FilterOamWrite(int index, byte b) => FilterSpriteField(Profile.FieldOfRaw[index & 3], (index & 0x1FF) >> 2, b);

	private byte FilterByte(int idx, byte b, SpriteSettings sp, bool organicGroups)
	{
		int field = Profile.FieldOfRaw[idx & 3];
		if (organicGroups && lagLeft > 0 && idx >= 4 && (idx >> 5) == lagGroup) return prevOam[idx];
		return FilterField(field, b, sp);
	}

	private byte FilterField(int field, byte b, SpriteSettings sp)
	{
		if (forceLeft > 0 && field == (sp.ForceBit.Field & 3) && !(field == 0 && Hidden(b)))
		{
			int m = 1 << (sp.ForceBit.Bit & 7);
			b = sp.ForceBit.High ? (byte)(b | m) : (byte)(b & ~m);
		}
		switch (field)
		{
			case 0:
				if (Hidden(b)) break; // never reveal a hidden sprite
				if (sp.YJitter.On && RS() < Threshold(sp.YJitter.Rate)) b = Jitter(b, sp.YJitter.Range, Profile.YMin, Profile.YMax);
				break;
			case 3:
				if (sp.XJitter.On && RS() < Threshold(sp.XJitter.Rate)) b = Jitter(b, sp.XJitter.Range, 0, Profile.XMax);
				break;
			case 1:
				if (sp.Tile.On && RS() < Threshold(sp.Tile.Rate)) b = (byte)(b + ((RS() & 1) != 0 ? 1 : -1));
				break;
			case 2:
				if (Profile.AttrBits.Length > 0 && Any(sp.Attr.Bits) && RS() < Threshold(sp.Attr.Rate))
				{
					int[] bitOf = Profile.AttrBits;
					int n = 0; var picks = new int[5];
					for (int i = 0; i < Math.Min(bitOf.Length, sp.Attr.Bits.Length); i++) if (sp.Attr.Bits[i]) picks[n++] = bitOf[i];
					if (n > 0) b ^= (byte)(1 << picks[RS() % (uint)n]);
				}
				break;
		}
		return b;
	}

	private byte Jitter(byte b, int range, int min, int max)
	{
		range = Math.Clamp(range, 1, 64);
		int d = (int)(RS() % (uint)(range * 2 + 1)) - range;
		return (byte)Math.Clamp(b + d, min, max);
	}

	// ===================================================================================== sound
	private readonly byte[] lastGame = new byte[0x80];
	private bool dropRegKnown; // until the game writes its channel-enable register, every channel is taken to be on
	private readonly (ushort a, byte v)[][] history = Enumerable16();
	private readonly int[] historyCount = new int[16];
	private static (ushort a, byte v)[][] Enumerable16() { var h = new (ushort, byte)[16][]; for (int i = 0; i < 16; i++) h[i] = new (ushort, byte)[8]; return h; }
	private void Remember(int ch, ushort a, byte v)
	{
		if ((uint)ch >= 16u) return;
		var h = history[ch]; Array.Copy(h, 0, h, 1, 7); h[0] = (a, v); if (historyCount[ch] < 8) historyCount[ch]++;
	}
	private int dropMask; private readonly int[] dropLeft = new int[16]; private bool pendingDropRelease;
	private int stutterLeft, droneLeft, dutyLeft, detuneLeft;

	/// <summary>Filters a game write to a sound register on its way into the chip (NES $4000-$4017, Game Boy NR10-NR52).</summary>
	public byte FilterApuWrite(ushort address, byte v)
	{
		var pf = Profile;
		int r = address - pf.SoundBase;
		if ((uint)r < (uint)lastGame.Length) lastGame[r] = v;
		int ch = pf.ChannelOf(r);
		if (ch >= 0) Remember(ch, address, v);
		if (r == pf.DropReg) { dropRegKnown = true; return pf.DropApply(v, dropMask); }
		if (pf.SoundExcluded(r)) return v; // NES: DMC samples and the frame counter; Game Boy: power and wave RAM
		return Bend(address, v);
	}

	private byte Bend(ushort address, byte v)
	{
		var so = settings.Sound; var pf = Profile;
		int r = address - pf.SoundBase;
		if (droneLeft > 0 && Array.IndexOf(pf.VolumeRegs, r) >= 0) v = pf.Drone(r, v);          // constant volume, max
		if (dutyLeft > 0 && Array.IndexOf(pf.DutyRegs, r) >= 0) v = (byte)((v & 0x3F) | ((so.Duty.Value & 3) << 6));
		bool periodLow = Array.IndexOf(pf.PeriodLowRegs, r) >= 0;
		if (detuneLeft > 0 && periodLow) v ^= (byte)Math.Clamp(so.Detune.Value, 1, 255);
		if (Any(so.BitFlip.Bits) && RA() < Threshold(so.BitFlip.Rate))
		{
			int n = 0; var picks = new int[5];
			for (int i = 0; i < Math.Min(5, so.BitFlip.Bits.Length); i++) if (so.BitFlip.Bits[i]) picks[n++] = i;
			v ^= (byte)(1 << picks[RA() % (uint)n]);
			if ((RA() & 1) != 0) v ^= (byte)(1 << picks[RA() % (uint)n]);
		}
		if (so.Nudge.On && periodLow && RA() < Threshold(so.Nudge.Rate)) v = (byte)(v + ((RA() & 1) != 0 ? 1 : -1));
		return v;
	}

	// ===================================================================================== FM chips
	// Channels after the PSG's four in the panel's drop list: Genesis FM 1-6 (YM2612), Master System FM 1-9 (YM2413).
	// Both chips take an address latch then data. Drop-outs key a channel off (the game's next key-on brings it back),
	// drone holds total level / volume at loudest, detune XORs the frequency low byte, nudges +-1 it, bit flips hit the
	// operator and channel parameters. Key-on registers are only ever cleared, never set: no note the game did not play.
	/// <summary>Writes straight to the FM chip (port, value): set by the host that owns one.</summary>
	public Action<int, byte>? FmInject;
	private readonly int[] fmAddr = new int[2];
	private int FmDropBit(int fmCh) => 1 << (4 + fmCh);

	/// <summary>MMC5's expansion sound ($5000-$5007: two pulse channels laid out like the APU's $4000-$4007): the same bends.
	/// The PCM ($5010-$5011) and the enable register are left alone. (MMC5 is the only cartridge sound chip BrokenNes emulates.)</summary>
	public byte FilterMmc5Write(ushort address, byte v)
	{
		if (Profile.Console != "nes" || address > 0x5007) return v;
		return Bend((ushort)(0x4000 + (address - 0x5000)), v);
	}

	/// <summary>YM2612 port write (0 / 2 = address of part 1 / 2, 1 / 3 = data).</summary>
	public byte FilterYmWrite(int port, byte v)
	{
		int part = (port >> 1) & 1;
		if ((port & 1) == 0) { fmAddr[part] = v; return v; }
		int r = fmAddr[part];
		var so = settings.Sound;
		if (r == 0x28 && part == 0)                                   // key on / off: ops in bits 4-7, channel code in 0-2
		{
			int code = v & 7, ch = (code & 3) + ((code & 4) != 0 ? 3 : 0);
			if ((code & 3) != 3 && (dropMask & FmDropBit(ch)) != 0) return (byte)(v & 0x0F);
			return v;
		}
		if (r == 0x2A && part == 0 && SampleFaults != null) return SampleData(v);   // the DAC's sample data: the sample pins
		if (r < 0x30 || r > 0xB6) return v;                         // LFO, timers, DAC: left alone
		if (r >= 0xA0) { int fc = (r & 3) == 3 ? -1 : (r & 3) + part * 3; if (fc >= 0) Remember(4 + fc, (ushort)(part << 8 | r), v); }   // stutter replays a channel's frequency writes
		if (droneLeft > 0 && r >= 0x40 && r <= 0x4F) return 0;        // total level 0 = loudest
		bool freqLow = r >= 0xA0 && r <= 0xA2;
		if (detuneLeft > 0 && freqLow) v ^= (byte)Math.Clamp(so.Detune.Value, 1, 255);
		if (so.Nudge.On && freqLow && RA() < Threshold(so.Nudge.Rate)) v = (byte)(v + ((RA() & 1) != 0 ? 1 : -1));
		return FlipBits(v);
	}

	/// <summary>YM2413 (Master System FM unit): address port then data port.</summary>
	public byte FilterOpllWrite(bool data, byte v)
	{
		if (!data) { fmAddr[0] = v; return v; }
		int r = fmAddr[0];
		var so = settings.Sound;
		int ch = r & 0x0F;
		if (r >= 0x10 && r <= 0x28 && ch < 9) Remember(4 + ch, (ushort)r, v);
		if (r >= 0x20 && r <= 0x28 && (dropMask & FmDropBit(ch)) != 0) return (byte)(v & ~0x10);   // key off
		if (r >= 0x30 && r <= 0x38 && droneLeft > 0) return (byte)(v & 0xF0);                       // volume 0 = loudest
		bool freqLow = r >= 0x10 && r <= 0x18;
		if (detuneLeft > 0 && freqLow) v ^= (byte)Math.Clamp(so.Detune.Value, 1, 255);
		if (so.Nudge.On && freqLow && RA() < Threshold(so.Nudge.Rate)) v = (byte)(v + ((RA() & 1) != 0 ? 1 : -1));
		if (r >= 0x20 && r <= 0x28) return v;                      // key / sustain / block: no flips (no new notes)
		return r <= 0x38 ? FlipBits(v) : v;
	}

	private byte FlipBits(byte v)
	{
		var so = settings.Sound;
		if (!Any(so.BitFlip.Bits) || RA() >= Threshold(so.BitFlip.Rate)) return v;
		int n = 0; Span<int> picks = stackalloc int[5];
		for (int i = 0; i < Math.Min(5, so.BitFlip.Bits.Length); i++) if (so.BitFlip.Bits[i]) picks[n++] = i;
		return n == 0 ? v : (byte)(v ^ (1 << picks[(int)(RA() % (uint)n)]));
	}

	/// <summary>A newly dropped FM channel is keyed off at once (the next key-on the game writes is let through once the drop ends).</summary>
	private void FmDrops(int oldDrop)
	{
		var inj = FmInject; if (inj == null) return;
		int fmChannels = Profile.Channels.Length - 4;
		for (int ch = 0; ch < fmChannels; ch++)
		{
			int bit = FmDropBit(ch);
			if ((dropMask & bit) == 0 || (oldDrop & bit) != 0) continue;
			if (Profile.Console == "md") { inj(0, 0x28); inj(1, (byte)(ch < 3 ? ch : ch + 1)); }
			else { inj(-1, (byte)(0x20 + ch)); inj(-2, 0); }       // YM2413: address then data (key off, block 0)
		}
	}

	// ===================================================================================== unsafe: CPU pins, RAM / VRAM rot
	/// <summary>NES cartridge PRG pins: the byte a CPU read of $8000-$FFFF gets through the bent bus (A15 stays: the cart is still selected).</summary>
	public byte CpuPrgRead(ushort address, Func<ushort, byte> read)
	{
		var f = CpuPins!;
		if (f.Misses()) return read(address);
		int a = f.AddrActive ? (f.Addr(address & 0x7FFF) & 0x7FFF) | 0x8000 : address;
		byte v = read((ushort)a);
		return f.DataActive ? f.Data(v) : v;
	}
	/// <summary>A CPU write to the cart (mapper registers) through the same pins, with "bend writes too".</summary>
	public bool CpuPrgWrite(ushort address, byte value, Action<ushort, byte> write)
	{
		var f = CpuPins!;
		if (!f.BendWrites || f.Misses()) return false;
		int a = f.AddrActive ? (f.Addr(address & 0x7FFF) & 0x7FFF) | 0x8000 : address;
		write((ushort)a, f.DataActive ? f.Data(value) : value);
		return true;
	}

	/// <summary>Any console's cartridge read through the bent CPU pins: the low <paramref name="lines"/> address bits are bent,
	/// the rest of the address is kept (the same slot / bank stays selected).</summary>
	public byte CpuRead8(uint address, int lines, Func<uint, byte> read)
	{
		var f = CpuPins!;
		if (f.Misses()) return read(address);
		uint mask = lines >= 32 ? uint.MaxValue : (1u << lines) - 1;
		uint a = f.AddrActive ? ((uint)f.Addr((int)(address & mask)) & mask) | (address & ~mask) : address;
		byte v = read(a);
		return f.DataActive ? f.Data(v) : v;
	}
	public ushort CpuRead16(uint address, int lines, Func<uint, ushort> read)
	{
		var f = CpuPins!;
		if (f.Misses()) return read(address);
		uint mask = (1u << lines) - 1;
		uint a = f.AddrActive ? ((uint)f.Addr((int)(address & mask)) & mask) | (address & ~mask) : address;
		ushort v = read(a);
		return f.DataActive ? f.Data16(v) : v;
	}
	private static uint BendWith(PinFaults f, uint a, int lines) { uint m = lines >= 32 ? uint.MaxValue : (1u << lines) - 1; return ((uint)f.Addr((int)(a & m)) & m) | (a & ~m); }
	/// <summary>A DMA controller's source read through its bent bus (8-bit: the SNES).</summary>
	public byte DmaRead8(uint a, Func<uint, byte> read)
	{
		var f = Dma!; if (f.Misses()) return read(a);
		byte v = read(f.AddrActive ? BendWith(f, a, Profile.DmaLines) : a);
		return f.DataActive ? f.Data(v) : v;
	}
	/// <summary>The same for a 16-bit transfer (the Genesis VDP's DMA).</summary>
	public ushort DmaRead16(uint a, Func<uint, ushort> read)
	{
		var f = Dma!; if (f.Misses()) return read(a);
		ushort v = read(f.AddrActive ? BendWith(f, a, Profile.DmaLines) & 0xFFFFFE : a);
		return f.DataActive ? f.Data16(v) : v;
	}
	/// <summary>A coprocessor's read of its own bus (SA-1, Super FX) through its bent pins.</summary>
	public byte CoproRead(uint a, Func<uint, byte> read)
	{
		var f = Copro!; if (f.Misses()) return read(a);
		byte v = read(f.AddrActive ? BendWith(f, a, 24) : a);
		return f.DataActive ? f.Data(v) : v;
	}
	/// <summary>DSP-1's data port: only its data lines.</summary>
	public byte CoproData(byte v) { var f = Copro; return f != null && f.DataActive && !f.Misses() ? f.Data(v) : v; }

	// ---------------------------------------------------------------- sound pins
	/// <summary>A register write on the CPU-to-sound-chip bus: the register index (low <paramref name="lines"/> bits) and the value, bent.</summary>
	public (int reg, byte v) SoundBusBend(int reg, byte v, int lines)
	{
		var f = SoundBus!; if (f.Misses()) return (reg, v);
		if (f.AddrActive && lines > 0) { int m = (1 << lines) - 1; reg = (f.Addr(reg & m) & m) | (reg & ~m); }
		return (reg, f.DataActive ? f.Data(v) : v);
	}
	/// <summary>A sample byte the chip fetches, through its bent sample bus.</summary>
	public byte SampleRead(int a, int lines, Func<int, byte> read)
	{
		var f = SampleFaults!; if (f.Misses()) return read(a);
		int m = (1 << lines) - 1;
		byte v = read(f.AddrActive && lines > 0 ? (f.Addr(a & m) & m) | (a & ~m) : a);
		return f.DataActive ? f.Data(v) : v;
	}
	public byte SampleData(byte v) { var f = SampleFaults; return f != null && f.DataActive && !f.Misses() ? f.Data(v) : v; }

	// The DAC: the final samples with stuck / bridged bits, then the wobble (a fractional delay swept by a slow LFO).
	private readonly float[] wobBuf = new float[4096]; private int wobPos; private double wobPhase;
	public void DacShorts(Span<short> s, int channels)
	{
		var f = Dac;
		if (f != null) for (int i = 0; i < s.Length; i++) if (!f.Misses()) s[i] = (short)f.Data16((ushort)s[i]);
		if (!WobbleActive) return;
		for (int i = 0; i + channels - 1 < s.Length; i += channels)
			for (int c = 0; c < channels; c++) s[i + c] = (short)Math.Clamp(Wob(s[i + c], c, channels), -32768, 32767);
	}
	public void DacFloats(float[] s)
	{
		var f = Dac;
		if (f != null) for (int i = 0; i < s.Length; i++) if (!f.Misses()) s[i] = (short)f.Data16((ushort)(short)Math.Clamp(s[i] * 32767, -32768, 32767)) / 32768f;
		if (!WobbleActive) return;
		for (int i = 0; i < s.Length; i++) s[i] = (float)(Wob(s[i] * 32768, 0, 1) / 32768);
	}
	private double Wob(double x, int c, int channels)
	{
		var w = settings.Sound.Wobble;
		int n = wobBuf.Length / channels;
		if (c == 0) { wobPos = (wobPos + 1) % n; wobPhase += 2 * Math.PI * Math.Max(1, w.Speed) / 10.0 / 44100.0; }
		wobBuf[wobPos * channels + c] = (float)x;
		double delay = 60 + Math.Clamp(w.Depth, 0, 100) * 4 * (1 + Math.Sin(wobPhase));   // samples: a swept delay is a swept pitch
		double rp = wobPos - delay; while (rp < 0) rp += n;
		int i0 = (int)rp; double fr = rp - i0; int i1 = (i0 + 1) % n;
		return wobBuf[i0 * channels + c] * (1 - fr) + wobBuf[i1 * channels + c] * fr;
	}

	// ---------------------------------------------------------------- sound RAM rot, SNES echo abuse
	private byte[]? soundRam;
	public void AttachSoundRam(byte[]? r) => soundRam = r;
	public Action<int, byte>? EchoInject; public Func<int, byte>? DspReadReg;
	private int echoLeft;
	private void SoundRot()
	{
		var u = settings.Unsafe;
		if (u.SoundRamRot.On && soundRam is { Length: > 0 } r && RT() < EventThreshold(u.SoundRamRot.Rate) * 4u)
			for (int i = 0; i < Math.Clamp(u.SoundRamRot.Bits, 1, 64); i++) r[(int)(RT() % (uint)r.Length)] ^= (byte)(1 << (int)(RT() % 8));
		if (u.EchoAbuse.On && EchoInject is { } inj)
		{
			if (echoLeft > 0) { if (--echoLeft == 0 && DspReadReg is { } rd) inj(0x6C, (byte)(rd(0x6C) | 0x20)); }   // echo writes off again
			else if (RT() < EventThreshold(u.EchoAbuse.Rate))
			{
				echoLeft = Math.Clamp(u.EchoAbuse.Duration, 1, 3600);
				inj(0x7D, 0x0F); inj(0x0D, 0x7F); inj(0x4D, 0xFF); inj(0x2C, 0x7F); inj(0x3C, 0x7F);   // longest delay, full feedback, every voice, echo volume up
				if (DspReadReg is { } rd) inj(0x6C, (byte)(rd(0x6C) & ~0x20));                         // echo writes on: the buffer runs over sample memory
			}
		}
	}

	/// <summary>A sound CPU's read of its own memory through its bent bus.</summary>
	public byte SoundCpuRead(int address, int lines, Func<int, byte> read)
	{
		var f = SoundCpu!;
		if (f.Misses()) return read(address);
		int mask = (1 << lines) - 1;
		int a = f.AddrActive ? (f.Addr(address & mask) & mask) | (address & ~mask) : address;
		byte v = read(a);
		return f.DataActive ? f.Data(v) : v;
	}

	public void CpuWrite8(uint address, byte v, int lines, Action<uint, byte> write)
	{
		var f = CpuPins!;
		if (!f.BendWrites || f.Misses()) { write(address, v); return; }
		uint mask = (1u << lines) - 1;
		uint a = f.AddrActive ? ((uint)f.Addr((int)(address & mask)) & mask) | (address & ~mask) : address;
		write(a, f.DataActive ? f.Data(v) : v);
	}

	/// <summary>Player 1's buttons through the faulted controller lines (bits as PadButtons: Up 0 ... Mode 14).
	/// <paramref name="order"/> is the console's button order, which "bridged to the next" follows.</summary>
	public ushort FilterPad(ushort bits, int[] order)
	{
		if (!PadActive) return bits;
		var lines = settings.Unsafe.PadLines;
		uint thr = (uint)(Math.Clamp(settings.Unsafe.PadFlicker, 0, 99.9) / 100.0 * uint.MaxValue);
		ushort input = bits;
		for (int i = 0; i < order.Length; i++)
		{
			int b = order[i];
			if (b >= lines.Length || lines[b] == 0) continue;
			if (thr != 0 && RT() < thr) continue;   // the line made no contact this time
			switch (lines[b])
			{
				case 1: bits &= (ushort)~(1 << b); break;
				case 2: bits |= (ushort)(1 << b); break;
				case 3:
					int n = order[(i + 1) % order.Length];
					if ((input & (1 << b | 1 << n)) != 0) bits |= (ushort)(1 << b | 1 << n);
					break;
			}
		}
		return bits;
	}
	/// <summary>Button orders the panel lists (PadButtons bits).</summary>
	public static readonly int[] PadNes = { 4, 5, 11, 10, 0, 1, 2, 3 }, PadGb = { 4, 5, 11, 10, 0, 1, 2, 3 }, PadSms = { 0, 1, 2, 3, 4, 5, 10 },
		PadMd = { 0, 1, 2, 3, 4, 5, 12, 10, 6, 7, 13, 14 }, PadSnes = { 0, 1, 2, 3, 5, 7, 11, 10, 4, 6, 8, 9 };

	private byte[]? vram; private ushort[]? vramWords;
	public void AttachVram(byte[]? v) { vram = v; vramWords = null; }
	public void AttachVramWords(ushort[]? v) { vramWords = v; vram = null; }
	public bool HasVram => vram != null || vramWords != null;
	private uint rngRot = 0x5BD1E995u;
	private uint RT() => Step(ref rngRot);

	private void RotFrame()
	{
		var u = settings.Unsafe;
		if (u.RamRot.On && ram is { Length: > 0 } r && RT() < EventThreshold(u.RamRot.Rate) * 4u)
			for (int i = 0; i < Math.Clamp(u.RamRot.Bits, 1, 64); i++) r[(int)(RT() % (uint)r.Length)] ^= (byte)(1 << (int)(RT() % 8));
		if (u.VramRot.On && RT() < EventThreshold(u.VramRot.Rate) * 4u)
			for (int i = 0; i < Math.Clamp(u.VramRot.Bits, 1, 64); i++)
			{
				if (vram is { Length: > 0 } v) v[(int)(RT() % (uint)v.Length)] ^= (byte)(1 << (int)(RT() % 8));
				else if (vramWords is { Length: > 0 } w) w[(int)(RT() % (uint)w.Length)] ^= (ushort)(1 << (int)(RT() % 16));
			}
	}

	// ===================================================================================== palette (display only)
	// The finished picture's colours go through a cached table (a game shows few colours), so fades and flashes keep working:
	// the transform follows whatever the game draws. Nothing the game can read changes.
	private readonly System.Collections.Generic.Dictionary<uint, uint> paletteLut = new();
	private int paletteMode, paletteTick;

	private void PaletteFrame()
	{
		var ps = settings.Palette;
		if (ps.CycleFrames > 0 && ++paletteTick >= ps.CycleFrames) { paletteTick = 0; paletteMode = (paletteMode + 1) % 5; paletteLut.Clear(); }
	}

	/// <summary>0xAARRGGBB pictures (the console sessions).</summary>
	public void ApplyPalette(uint[] argb, int count)
	{
		if (!PaletteActive) return;
		count = Math.Min(count, argb.Length);
		for (int i = 0; i < count; i++) argb[i] = MapColour(argb[i]);
	}

	/// <summary>R, G, B, A byte pictures (the NES).</summary>
	public void ApplyPaletteRgba(byte[] rgba)
	{
		if (!PaletteActive) return;
		for (int i = 0; i + 3 < rgba.Length; i += 4)
		{
			uint c = 0xFF000000u | (uint)rgba[i] << 16 | (uint)rgba[i + 1] << 8 | rgba[i + 2];
			uint m = MapColour(c);
			rgba[i] = (byte)(m >> 16); rgba[i + 1] = (byte)(m >> 8); rgba[i + 2] = (byte)m;
		}
	}

	private uint MapColour(uint c)
	{
		if (paletteLut.TryGetValue(c, out var m)) return m;
		var ps = settings.Palette;
		if (!ps.On) { m = VideoDac(c); if (paletteLut.Count < 65536) paletteLut[c] = m; return m; }
		double r = (c >> 16 & 255) / 255.0, g = (c >> 8 & 255) / 255.0, b = (c & 255) / 255.0;
		double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, h = 0, sat = max == 0 ? 0 : d / max, v = max;
		if (d > 0) h = max == r ? ((g - b) / d + 6) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
		h = (h + new[] { 1.0, 2.0, 3.0, 4.0, 1.0 }[paletteMode]) % 6;      // 60-degree steps; greys keep no hue
		if (paletteMode == 4) v = (v + 0.5) % 1.0;                           // the device's luma shift
		int k = (int)h; double f = h - k, p = v * (1 - sat), q = v * (1 - sat * f), t = v * (1 - sat * (1 - f));
		(r, g, b) = k switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
		if (ps.ChannelSwap) (r, g, b) = (g, b, r);
		if (ps.Invert) (r, g, b) = (1 - r, 1 - g, 1 - b);
		if (ps.Posterize >= 2) { int n = Math.Clamp(ps.Posterize, 2, 8) - 1; r = Math.Round(r * n) / n; g = Math.Round(g * n) / n; b = Math.Round(b * n) / n; }
		m = (c & 0xFF000000u) | (uint)Math.Clamp((int)(r * 255 + 0.5), 0, 255) << 16 | (uint)Math.Clamp((int)(g * 255 + 0.5), 0, 255) << 8 | (uint)Math.Clamp((int)(b * 255 + 0.5), 0, 255);
		m = VideoDac(m);
		if (paletteLut.Count < 65536) paletteLut[c] = m;
		return m;
	}

	/// <summary>The video DAC's lines on one colour (bits 0-7 R, 8-15 G, 16-23 B).</summary>
	private uint VideoDac(uint c)
	{
		var f = VideoDacFaults; if (f == null) return c;
		uint rgb = (c >> 16 & 255) | (c >> 8 & 255) << 8 | (c & 255) << 16;
		uint o = (uint)f.Data24((int)rgb);
		return (c & 0xFF000000u) | (o & 255) << 16 | (o >> 8 & 255) << 8 | (o >> 16 & 255);
	}

	// ===================================================================================== the SN76489 PSG (Master System / Game Gear)
	// One port: a latch byte (1 cc t dddd: channel, type 1 = volume, low data) or a data byte (0 x dddddd: the rest of the latched
	// tone). Volume 0 is loudest, 15 is silent. Drop-outs hold a channel's volume at 15, drone at 0; tone bytes take the bit flips,
	// nudges and detune; stutter replays an older byte of a channel.
	private int psgCh, psgType; private readonly int[] psgVol = { -1, -1, -1, -1 };

	public byte FilterPsgWrite(byte v)
	{
		var so = settings.Sound;
		if ((v & 0x80) != 0) { psgCh = (v >> 5) & 3; psgType = (v >> 4) & 1; }
		int ch = psgCh;
		Remember(ch, 0, v);
		if (psgType == 1)
		{
			int vol = v & 0x0F;
			if ((v & 0x80) != 0) psgVol[ch] = vol;
			if ((dropMask & (1 << ch)) != 0) vol = 15;
			else if (droneLeft > 0) vol = 0;
			return (byte)((v & 0xF0) | vol);
		}
		int bits = (v & 0x80) != 0 ? 0x0F : 0x3F;   // the data a latch / data byte carries
		int d = v & bits;
		if (detuneLeft > 0) d ^= Math.Clamp(so.Detune.Value, 1, 255) & bits;
		if (Any(so.BitFlip.Bits) && RA() < Threshold(so.BitFlip.Rate))
		{
			int n = 0; var picks = new int[5];
			for (int i = 0; i < Math.Min(5, so.BitFlip.Bits.Length); i++) if (so.BitFlip.Bits[i] && ((1 << i) & bits) != 0) picks[n++] = i;
			if (n > 0) d ^= 1 << picks[RA() % (uint)n];
		}
		if (so.Nudge.On && RA() < Threshold(so.Nudge.Rate)) d = (d + ((RA() & 1) != 0 ? 1 : -1)) & bits;
		return (byte)((v & ~bits) | (d & bits));
	}

	private void PsgFrame(Action<ushort, byte> inject)
	{
		var so = settings.Sound;
		int oldDrop = dropMask; bool droneWas = droneLeft > 0;
		if (SoundActive)
		{
			for (int ch = 0; ch < Math.Min(Profile.Channels.Length, dropLeft.Length); ch++)
			{
				if (dropLeft[ch] > 0) { if (--dropLeft[ch] == 0) dropMask &= ~(1 << ch); continue; }
				bool on = so.Drops.Channels != null && ch < so.Drops.Channels.Length && so.Drops.Channels[ch];
				if (on && RA() < EventThreshold(so.Drops.Rate)) { dropMask |= 1 << ch; dropLeft[ch] = Math.Max(1, so.Drops.Duration); }
			}
			Tick(ref stutterLeft, so.Stutter.On, so.Stutter.Rate, so.Stutter.Duration, null);
			Tick(ref droneLeft, so.Drone.On, so.Drone.Rate, so.Drone.Duration, null);
			Tick(ref detuneLeft, so.Detune.On, so.Detune.Rate, so.Detune.Duration, null);
			if (stutterLeft > 0)
			{
				int ch = (int)(RA() % (uint)Math.Min(16, Profile.Channels.Length));
				if (historyCount[ch] > 1)
				{
					var w = history[ch][1 + (int)(RA() % (uint)(historyCount[ch] - 1))];
					if (ch < 4) inject(0, w.v);
					else if (FmInject is { } fi)
					{
						if (Profile.Console == "md") { int part = w.a >> 8; fi(part * 2, (byte)w.a); fi(part * 2 + 1, w.v); }
						else { fi(-1, (byte)w.a); fi(-2, w.v); }
					}
				}
			}
		}
		else { dropMask = 0; Array.Clear(dropLeft); droneLeft = 0; }
		bool drone = droneLeft > 0;
		FmDrops(oldDrop);
		for (int ch = 0; ch < 4; ch++)
		{
			bool was = (oldDrop & (1 << ch)) != 0, now = (dropMask & (1 << ch)) != 0;
			if (was == now && droneWas == drone) continue;
			int vol = now ? 15 : drone ? 0 : psgVol[ch];
			if (vol >= 0) inject(0, (byte)(0x90 | (ch << 5) | vol));   // a volume latch; the game's own volume comes back when the effect ends
		}
	}

	// ===================================================================================== per frame
	/// <summary>Once per frame, before it runs: starts and ends events. <paramref name="inject"/> writes straight to the APU.</summary>
	// The panel's freeze watchdog: frames in a row in which the game's work RAM did not change at all (a crashed or hung game
	// stops writing; a paused one usually still ticks a counter). Read by /api/butcher/stats.
	public int StillFrames { get; private set; }
	private ulong lastRamHash;
	private void WatchRam()
	{
		var r = ram; if (r == null) return;
		ulong h = 1469598103934665603UL; int step = r.Length > 0x10000 ? 2 : 1;
		for (int i = 0; i < r.Length; i += step) { h ^= r[i]; h *= 1099511628211UL; }
		StillFrames = h == lastRamHash ? StillFrames + 1 : 0; lastRamHash = h;
	}

	public void FrameStart(Action<ushort, byte> inject)
	{
		WatchRam();
		if (PaletteActive) PaletteFrame();
		if (RotActive) { RotFrame(); SoundRot(); }
		if (IrqActive || irqLeft > 0) InterruptFrame();
		if (BandsActive) BandsFrame(); else { TallXor = 0; LineBendMask = 0; }
		if (RamActive) RamFrame();
		var sp = settings.Sprites;
		if (SpritesActive) Buffer.BlockCopy(fieldCur, 0, fieldPrev, 0, fieldCur.Length);
		if (Profile.Psg)
		{
			if (SpritesActive)
			{
				Tick(ref lagLeft, sp.GroupLag.On, sp.GroupLag.Rate, sp.GroupLag.Duration, () => lagGroup = (int)(RS() % 8));
				Tick(ref cloneLeft, sp.Clone.On, sp.Clone.Rate, sp.Clone.Duration, () => cloneSprite = 2 + (int)(RS() % 7));
				Tick(ref swapLeft, sp.FieldSwap.On, sp.FieldSwap.Rate, sp.FieldSwap.Duration, () => swapBy = 1 + (int)(RS() % 3));
				Tick(ref forceLeft, sp.ForceBit.On, sp.ForceBit.Rate, sp.ForceBit.Duration, null);
			}
			PsgFrame(inject);
			return;
		}
		if (SpritesActive)
		{
			Tick(ref lagLeft, sp.GroupLag.On, sp.GroupLag.Rate, sp.GroupLag.Duration, () => lagGroup = (int)(RS() % 8));
			Tick(ref cloneLeft, sp.Clone.On, sp.Clone.Rate, sp.Clone.Duration, () => cloneSprite = 2 + (int)(RS() % 7));
			Tick(ref swapLeft, sp.FieldSwap.On, sp.FieldSwap.Rate, sp.FieldSwap.Duration, () => swapBy = 1 + (int)(RS() % 3));
			Tick(ref forceLeft, sp.ForceBit.On, sp.ForceBit.Rate, sp.ForceBit.Duration, null);
		}
		var so = settings.Sound;
		int oldDrop = dropMask;
		if (SoundActive)
		{
			for (int ch = 0; ch < Profile.Channels.Length; ch++)
			{
				if (dropLeft[ch] > 0) { if (--dropLeft[ch] == 0) dropMask &= ~(1 << ch); continue; }
				bool on = so.Drops.Channels != null && ch < so.Drops.Channels.Length && so.Drops.Channels[ch];
				if (on && RA() < EventThreshold(so.Drops.Rate)) { dropMask |= 1 << ch; dropLeft[ch] = Math.Max(1, so.Drops.Duration); }
			}
			bool droneStart = droneLeft == 0, dutyStart = dutyLeft == 0, detuneStart = detuneLeft == 0;
			Tick(ref stutterLeft, so.Stutter.On, so.Stutter.Rate, so.Stutter.Duration, null);
			Tick(ref droneLeft, so.Drone.On, so.Drone.Rate, so.Drone.Duration, null);
			Tick(ref dutyLeft, so.Duty.On, so.Duty.Rate, so.Duty.Duration, null);
			Tick(ref detuneLeft, so.Detune.On, so.Detune.Rate, so.Detune.Duration, null);
			// an effect that just started applies to the registers the game already set, not only to its next write
			var pf = Profile; ushort A(int r) => (ushort)(pf.SoundBase + r);
			if (droneStart && droneLeft > 0) foreach (var r in pf.VolumeRegs) inject(A(r), Bend(A(r), lastGame[r]));
			if (dutyStart && dutyLeft > 0) foreach (var r in pf.DutyRegs) inject(A(r), Bend(A(r), lastGame[r]));
			if (detuneStart && detuneLeft > 0) foreach (var r in pf.PeriodLowRegs) inject(A(r), Bend(A(r), lastGame[r]));
			if (stutterLeft > 0)
			{
				int ch = (int)(RA() % (uint)Math.Min(16, Profile.Channels.Length));
				if (historyCount[ch] > 1) { var w = history[ch][1 + (int)(RA() % (uint)(historyCount[ch] - 1))]; inject(w.a, w.v); }
			}
		}
		else if (pendingDropRelease || dropMask != 0) { dropMask = 0; Array.Clear(dropLeft); }
		pendingDropRelease = false;
		if (dropMask != oldDrop)
		{
			byte en = dropRegKnown ? lastGame[Profile.DropReg] : Profile.DropRegDefault;
			inject((ushort)(Profile.SoundBase + Profile.DropReg), Profile.DropApply(en, dropMask));
		}
	}

	private void Tick(ref int left, bool on, double rate, int duration, Action? onStart)
	{
		if (left > 0) { left--; return; }
		if (on && Step(ref rngSound) < EventThreshold(rate)) { left = Math.Clamp(duration, 1, 3600); onStart?.Invoke(); }
	}
}
}

namespace NesEmulator
{
/// <summary>What Butchering mode can reach on the running game (NES.GetButcherReach); the panel greys out the rest.</summary>
public sealed record ButcherReach(string PpuCore, bool ConsolePins, bool ConsolePinsPartial, bool CartPins, int CartAddressLines, bool ChrIsRam, int Mapper,
	bool SpritesDma, bool Sprites2004, bool Sound, bool Bands, bool Ram)
{
	/// <summary>The panel's labels for this console (NES defaults).</summary>
	public string Console { get; init; } = "nes";
	public int ConsoleAddressLines { get; init; } = 14;
	public int ConsoleDataLines { get; init; } = 8;
	public string[] AttrNames { get; init; } = ButcherProfile.Nes.AttrNames;
	public string[] Channels { get; init; } = ButcherProfile.Nes.Channels;
	public string PinsTitle { get; init; } = "Picture bus · console pins";
	public string PinsWhy { get; init; } = "The PPU's own bus, before the cartridge's banking: tiles, nametables and attributes.";
	/// <summary>A limit of this console's sound reach, shown in the panel (empty = none).</summary>
	public string SoundNote { get; init; } = "";
	/// <summary>The shared band types this console's chip can do (ids: shift, slip, patternSwap, nametableSwap, bgOff, spritesOff, spriteHeight, bend).</summary>
	public string[] BandKinds { get; init; } = { "shift", "slip", "patternSwap", "nametableSwap", "bgOff", "spritesOff", "spriteHeight", "bend" };
	/// <summary>Names of this console's own extra band types (Extra1-3), with what each does.</summary>
	public string[] ExtraBands { get; init; } = Array.Empty<string>();
	/// <summary>Unsafe reach: the CPU-bus pins (NES cartridge PRG pins) and VRAM rot.</summary>
	public bool CpuPins { get; init; }
	public int CpuAddressLines { get; init; } = 15;
	public int CpuDataLines { get; init; } = 8;
	public string CpuPinsTitle { get; init; } = "CPU bus · cartridge PRG pins";
	public int SoundCpuLines { get; init; }
	/// <summary>The console's buttons in panel order (PadButtons bits) and their names.</summary>
	public int[] PadOrder { get; init; } = Butcher.PadNes;
	public string[] PadNames { get; init; } = { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" };
	public string SoundCpuTitle { get; init; } = "";
	public int DmaLines { get; init; }
	public int DmaDataLines { get; init; } = 8;
	public string CoproName { get; init; } = "";
	public int SoundBusLines { get; init; } = 5;
	/// <summary>The sound chip has RAM of its own (SPC700 ARAM, Z80 RAM, Game Boy wave RAM) for the sound RAM rot.</summary>
	public bool SoundRam { get; init; }
	public int VideoBusLines { get; init; } = 3;
	public int SampleLines { get; init; } = 15;
	public string SamplePinsTitle { get; init; } = "Sample fetch · DMC (A0-A14, D0-D7)";
	public bool VramRot { get; init; } = true;
}

/// <summary>A running system Butchering mode can drive (the NES, a Game Boy session).</summary>
public interface IButcherHost
{
	Butcher? Butcher { get; }
	void ApplyButcher(ButcherSettings settings);
	ButcherReach GetButcherReach();
}
}
