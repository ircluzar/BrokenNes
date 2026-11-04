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

	public PinFaults(PinGroupSettings? s, int addrLines, uint seed)
	{
		rng = seed == 0 ? 0x6c8e9cf5u : seed;
		addrAnd = ~0; dataAnd = ~0;
		addrBridges = Compile(s?.Address, addrLines, ref addrAnd, ref addrOr);
		dataBridges = Compile(s?.Data, 8, ref dataAnd, ref dataOr);
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

public sealed class Butcher
{
	/// <summary>The PPU's own address bus: A0-A13.</summary>
	public const int ConsoleAddressLines = 14;

	private ButcherSettings settings = new();
	internal PinFaults? Console;  // null = no console-pin fault
	internal PinFaults? Cart;     // null = no cartridge-pin fault
	private int cartLines;
	public bool SpritesActive { get; private set; }
	public bool SoundActive { get; private set; }

	private uint rngSprites = 0x1234567u, rngSound = 0x7654321u;

	public ButcherSettings Settings => settings;
	public int CartAddressLines => cartLines;

	/// <summary>Takes a new set of controls. The CHR chip's size sets how many cartridge address lines exist.</summary>
	public void Apply(ButcherSettings s, int chrChipSize)
	{
		settings = s ?? new ButcherSettings();
		cartLines = AddressLinesFor(chrChipSize);
		var on = settings.Enabled;
		var c = new PinFaults(settings.ConsolePins, ConsoleAddressLines, 0x9e3779b9u);
		var k = new PinFaults(settings.CartPins, cartLines, 0x85ebca6bu);
		Console = on && c.Active ? c : null;
		Cart = on && k.Active ? k : null;
		var sp = settings.Sprites;
		SpritesActive = on && (sp.XJitter.On || sp.YJitter.On || sp.Tile.On || Any(sp.Attr.Bits)
			|| sp.GroupLag.On || sp.Clone.On || sp.FieldSwap.On || sp.ForceBit.On);
		var so = settings.Sound;
		SoundActive = on && (Any(so.BitFlip.Bits) || so.Nudge.On || Any(so.Drops.Channels)
			|| so.Stutter.On || so.Drone.On || so.Duty.On || so.Detune.On);
		rngSprites = 0x1234567u; rngSound = 0x7654321u;
		if (!SpritesActive) { lagLeft = cloneLeft = swapLeft = forceLeft = 0; }
		if (!SoundActive || !Any(so.Drops.Channels)) pendingDropRelease = dropMask != 0;
		if (!SoundActive) { stutterLeft = droneLeft = dutyLeft = detuneLeft = 0; }
	}

	/// <summary>Nothing is on: the hooks can be detached.</summary>
	public bool Idle => Console == null && Cart == null && !SpritesActive && !SoundActive && dropMask == 0;

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
	public int ConsoleReadAddr(int address, out bool bendData)
	{
		var f = Console!;
		bendData = false;
		if (f.Misses()) return -1;
		bendData = f.DataActive;
		if (!f.AddrActive) return address;
		int a = f.Addr(address) & 0x3FFF;
		// The palette is inside the PPU, decoded before the pins: a bent fetch that lands there reads the nametable mirror instead.
		if (a >= 0x3F00) a -= 0x1000;
		return a;
	}
	public byte ConsoleData(byte v) => Console!.Data(v);

	// ===================================================================================== sprites
	private readonly byte[] prevOam = new byte[256];
	private int lagLeft, lagGroup, cloneLeft, cloneSprite, swapLeft, swapBy, forceLeft;

	/// <summary>Called before a DMA overwrites OAM: remembers what was there (for group lag).</summary>
	public void BeforeOamDma(byte[] oam) { Buffer.BlockCopy(oam, 0, prevOam, 0, Math.Min(256, oam.Length)); }

	/// <summary>Filters all of OAM after a DMA landed.</summary>
	public void AfterOamDma(byte[] oam)
	{
		if (oam.Length < 256) return;
		var sp = settings.Sprites;
		// organic: whole-sprite effects first, from the bytes the game sent
		if (cloneLeft > 0) for (int s = cloneSprite; s < 64; s += 7) for (int i = 0; i < 4; i++) oam[s * 4 + i] = oam[(s - 2) * 4 + i];
		if (swapLeft > 0)
			for (int s = 1; s < 64; s++)
			{
				int b0 = s * 4; byte y = oam[b0], t = oam[b0 + 1], a = oam[b0 + 2], x = oam[b0 + 3];
				byte[] f = { y, t, a, x };
				for (int i = 0; i < 4; i++) oam[b0 + i] = f[(i - swapBy + 4) & 3];
				if (y > 0xEE) oam[b0] = y; // a hidden sprite stays hidden
			}
		for (int i = 0; i < 256; i++) oam[i] = FilterByte(i, oam[i], sp, organicGroups: true);
	}

	/// <summary>A single OAM byte written through $2004.</summary>
	public byte FilterOamWrite(int index, byte b) => FilterByte(index & 0xFF, b, settings.Sprites, organicGroups: false);

	private byte FilterByte(int idx, byte b, SpriteSettings sp, bool organicGroups)
	{
		int field = idx & 3;
		if (organicGroups && lagLeft > 0 && idx >= 4 && (idx >> 5) == lagGroup) return prevOam[idx];
		if (forceLeft > 0 && field == (sp.ForceBit.Field & 3) && !(field == 0 && b > 0xEE))
		{
			int m = 1 << (sp.ForceBit.Bit & 7);
			b = sp.ForceBit.High ? (byte)(b | m) : (byte)(b & ~m);
		}
		switch (field)
		{
			case 0:
				if (b > 0xEE) break; // never reveal a hidden sprite
				if (sp.YJitter.On && RS() < Threshold(sp.YJitter.Rate)) b = Jitter(b, sp.YJitter.Range, 0xEE);
				break;
			case 3:
				if (sp.XJitter.On && RS() < Threshold(sp.XJitter.Rate)) b = Jitter(b, sp.XJitter.Range, 0xFF);
				break;
			case 1:
				if (sp.Tile.On && RS() < Threshold(sp.Tile.Rate)) b = (byte)(b + ((RS() & 1) != 0 ? 1 : -1));
				break;
			case 2:
				if (Any(sp.Attr.Bits) && RS() < Threshold(sp.Attr.Rate))
				{
					int[] bitOf = { 7, 6, 5, 1, 0 };
					int n = 0; var picks = new int[5];
					for (int i = 0; i < Math.Min(5, sp.Attr.Bits.Length); i++) if (sp.Attr.Bits[i]) picks[n++] = bitOf[i];
					b ^= (byte)(1 << picks[RS() % (uint)n]);
				}
				break;
		}
		return b;
	}

	private byte Jitter(byte b, int range, int max)
	{
		range = Math.Clamp(range, 1, 64);
		int d = (int)(RS() % (uint)(range * 2 + 1)) - range;
		return (byte)Math.Clamp(b + d, 0, max);
	}

	// ===================================================================================== sound
	private readonly byte[] lastGame = new byte[0x18];
	private readonly (ushort a, byte v)[][] history = { new (ushort, byte)[8], new (ushort, byte)[8], new (ushort, byte)[8], new (ushort, byte)[8] };
	private readonly int[] historyCount = new int[4];
	private int dropMask; private readonly int[] dropLeft = new int[5]; private bool pendingDropRelease;
	private int stutterLeft, droneLeft, dutyLeft, detuneLeft;

	/// <summary>Filters a game write to $4000-$4017 on its way into the APU.</summary>
	public byte FilterApuWrite(ushort address, byte v)
	{
		int r = address - 0x4000;
		if ((uint)r < (uint)lastGame.Length) lastGame[r] = v;
		if (r < 0x10) { int ch = r >> 2; var h = history[ch]; Array.Copy(h, 0, h, 1, 7); h[0] = (address, v); if (historyCount[ch] < 8) historyCount[ch]++; }
		if (address == 0x4015) return (byte)(v & ~dropMask);
		if (address == 0x4010 || address == 0x4012 || address == 0x4013 || address > 0x4015) return v; // DMC samples and the frame counter are left alone
		return Bend(address, v);
	}

	private byte Bend(ushort address, byte v)
	{
		var so = settings.Sound;
		int r = address - 0x4000;
		if (droneLeft > 0 && (r == 0x0 || r == 0x4 || r == 0xC)) v |= 0x3F;           // constant volume, max
		if (dutyLeft > 0 && (r == 0x0 || r == 0x4)) v = (byte)((v & 0x3F) | ((so.Duty.Value & 3) << 6));
		bool periodLow = r == 0x2 || r == 0x6 || r == 0xA || r == 0xE;
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

	// ===================================================================================== per frame
	/// <summary>Once per frame, before it runs: starts and ends events. <paramref name="inject"/> writes straight to the APU.</summary>
	public void FrameStart(Action<ushort, byte> inject)
	{
		var sp = settings.Sprites;
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
			for (int ch = 0; ch < 5; ch++)
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
			if (droneStart && droneLeft > 0) { inject(0x4000, Bend(0x4000, lastGame[0x0])); inject(0x4004, Bend(0x4004, lastGame[0x4])); inject(0x400C, Bend(0x400C, lastGame[0xC])); }
			if (dutyStart && dutyLeft > 0) { inject(0x4000, Bend(0x4000, lastGame[0x0])); inject(0x4004, Bend(0x4004, lastGame[0x4])); }
			if (detuneStart && detuneLeft > 0) { inject(0x4002, Bend(0x4002, lastGame[0x2])); inject(0x4006, Bend(0x4006, lastGame[0x6])); inject(0x400A, Bend(0x400A, lastGame[0xA])); }
			if (stutterLeft > 0)
			{
				int ch = (int)(RA() % 4);
				if (historyCount[ch] > 1) { var w = history[ch][1 + (int)(RA() % (uint)(historyCount[ch] - 1))]; inject(w.a, w.v); }
			}
		}
		else if (pendingDropRelease || dropMask != 0) { dropMask = 0; Array.Clear(dropLeft); }
		pendingDropRelease = false;
		if (dropMask != oldDrop) inject(0x4015, (byte)(lastGame[0x15] & ~dropMask));
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
public sealed record ButcherReach(string PpuCore, bool ConsolePins, bool CartPins, int CartAddressLines, bool ChrIsRam, int Mapper,
	bool SpritesDma, bool Sprites2004, bool Sound);
}
