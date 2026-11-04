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
}

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
		RamWindow = 0x2000, StackPage = 0x1F,
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
		RamWindow = 0x2000, StackPage = 0x1F,
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
		RamWindow = 0x2000, StackPage = 0x1F,
	};
	/// <summary>Genesis: 64 KB VRAM, PSG + YM2612, 64 KB work RAM.</summary>
	public static readonly ButcherProfile Md = new()
	{
		Console = "md",
		ConsoleLines = 16,
		AttrBits = Array.Empty<int>(), AttrNames = Array.Empty<string>(),
		Channels = new[] { "Tone 1", "Tone 2", "Tone 3", "Noise", "FM 1", "FM 2", "FM 3", "FM 4", "FM 5", "FM 6" },
		Psg = true,
		RamWindow = 0x10000, StackPage = 0xFF,
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
		RamWindow = 0x20000, StackPage = 0x1F,
	};
	public bool Psg;
	public int DataLines = 8;
}

public sealed class Butcher
{
	public ButcherProfile Profile = ButcherProfile.Nes;
	/// <summary>The PPU's own address bus: A0-A13.</summary>
	public const int ConsoleAddressLines = 14;

	private ButcherSettings settings = new();
	internal PinFaults? Console;  // null = no console-pin fault
	internal PinFaults? Cart;     // null = no cartridge-pin fault
	private int cartLines;
	public bool SpritesActive { get; private set; }
	public bool SoundActive { get; private set; }
	public bool BandsActive { get; private set; }
	public bool RamActive { get; private set; }

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
		BandsActive = on && (bd.Shift.On || bd.Slip.On || bd.PatternSwap.On || bd.NametableSwap.On || bd.BgOff.On || bd.SpritesOff.On || bd.SpriteHeight.On || bd.Bend.On);
		if (!BandsActive) { Array.Clear(bandLife); LineBendMask = 0; TallXor = 0; }
		RamActive = on && settings.Ram.On;
		if (!RamActive) Array.Clear(probeLife);
		rngSprites = 0x1234567u; rngSound = 0x7654321u; rngBands = 0x2545F491u;
		if (!SpritesActive) { lagLeft = cloneLeft = swapLeft = forceLeft = 0; }
		if (!SoundActive || !Any(so.Drops.Channels)) pendingDropRelease = dropMask != 0;
		if (!SoundActive) { stutterLeft = droneLeft = dutyLeft = detuneLeft = 0; }
	}

	/// <summary>Nothing is on: the hooks can be detached.</summary>
	public bool Idle => Console == null && Cart == null && !SpritesActive && !SoundActive && !BandsActive && dropMask == 0 && !RamActive;

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
		if (f == null || f.Misses()) return mem[a];
		int b = f.AddrActive ? f.Addr(a) & (mem.Length - 1) : a;
		byte v = mem[b];
		return f.DataActive ? f.Data(v) : v;
	}

	/// <summary>The same for a word-wide VRAM (the SNES).</summary>
	public ushort PictureReadWord(ushort[] mem, int a)
	{
		var f = Console;
		if (f == null || f.Misses()) return mem[a];
		int b = f.AddrActive ? f.Addr(a) & (mem.Length - 1) : a;
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
			if ((RB() & 15) == 0 && bandStart[i] > 0 && bandEnd[i] < 240) { int s = (RB() & 1) != 0 ? 1 : -1; bandStart[i] += s; bandEnd[i] += s; }
			if (bandType[i] == 6) TallXor = 0x20;
		}
		var kinds = new[] { bd.Shift, bd.Slip, bd.PatternSwap, bd.NametableSwap, bd.BgOff, bd.SpritesOff, bd.SpriteHeight, bd.Bend };
		for (int t = 0; t < kinds.Length; t++)
		{
			var k = kinds[t];
			if (!k.On || RB() >= EventThreshold(k.Rate)) continue;
			int slot = Array.IndexOf(bandLife, 0);
			if (slot < 0) break;
			int width = Math.Clamp(k.Width, 1, 240), strength = Math.Clamp(k.Strength, 1, 64);
			int start = (int)(RB() % 240), end = Math.Min(240, start + 1 + (int)(RB() % (uint)width));
			int param = 1 + (int)(RB() % (uint)strength); if ((RB() & 1) != 0) param = -param;
			if (t == 1) end = 240;                                   // a vertical slip carries to the bottom
			if (t == 6) { start = 0; end = 240; TallXor = 0x20; }     // sprite height: the whole frame
			if (t == 7) { param = 0; int lines = 1 + strength / 16; for (int j = 0; j < lines; j++) param |= 1 << (int)(RB() % 9); } // A0-A8
			bandType[slot] = t; bandStart[slot] = start; bandEnd[slot] = end; bandParam[slot] = param;
			bandLife[slot] = Math.Clamp(k.Lifetime, 1, 3600);
		}
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

	/// <summary>A sprite-table byte by canonical field (0 Y, 1 tile, 2 attributes, 3 X), for chips whose table is not 4-byte records.</summary>
	public byte FilterSpriteField(int field, int sprite, byte b) => FilterField(field, b, settings.Sprites);

	/// <summary>A single OAM byte written through $2004.</summary>
	public byte FilterOamWrite(int index, byte b) => FilterByte(index & 0xFF, b, settings.Sprites, organicGroups: false);

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
	private readonly (ushort a, byte v)[][] history = { new (ushort, byte)[8], new (ushort, byte)[8], new (ushort, byte)[8], new (ushort, byte)[8] };
	private readonly int[] historyCount = new int[4];
	private int dropMask; private readonly int[] dropLeft = new int[16]; private bool pendingDropRelease;
	private int stutterLeft, droneLeft, dutyLeft, detuneLeft;

	/// <summary>Filters a game write to a sound register on its way into the chip (NES $4000-$4017, Game Boy NR10-NR52).</summary>
	public byte FilterApuWrite(ushort address, byte v)
	{
		var pf = Profile;
		int r = address - pf.SoundBase;
		if ((uint)r < (uint)lastGame.Length) lastGame[r] = v;
		int ch = pf.ChannelOf(r);
		if (ch >= 0 && ch < 4) { var h = history[ch]; Array.Copy(h, 0, h, 1, 7); h[0] = (address, v); if (historyCount[ch] < 8) historyCount[ch]++; }
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
		if (r < 0x30 || r > 0xB6) return v;                         // LFO, timers, DAC: left alone
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
		{ var h = history[ch]; Array.Copy(h, 0, h, 1, 7); h[0] = (0, v); if (historyCount[ch] < 8) historyCount[ch]++; }
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
				int ch = (int)(RA() % 4);
				if (historyCount[ch] > 1) inject(0, history[ch][1 + (int)(RA() % (uint)(historyCount[ch] - 1))].v);
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
	public void FrameStart(Action<ushort, byte> inject)
	{
		if (BandsActive) BandsFrame(); else { TallXor = 0; LineBendMask = 0; }
		if (RamActive) RamFrame();
		var sp = settings.Sprites;
		if (Profile.Psg)
		{
			if (SpritesActive)
			{
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
				int ch = (int)(RA() % 4);
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
}

/// <summary>A running system Butchering mode can drive (the NES, a Game Boy session).</summary>
public interface IButcherHost
{
	Butcher? Butcher { get; }
	void ApplyButcher(ButcherSettings settings);
	ButcherReach GetButcherReach();
}
}
