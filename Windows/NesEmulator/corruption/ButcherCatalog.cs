using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NesEmulator;

/// <summary>
/// Butchering mode's effect catalog: every effect's <b>purity</b> score, from 0 (forced: generated and pushed in from outside, such as an
/// injected waveform or a colour transform) to 100 (pure: a fault real consoles get on their own, such as a stuck or bridged pin, a dirty
/// contact, a worn button). docs/projects/butchering-purity-plan.md has the scale and the reasons. The scores do nothing yet: they are kept
/// here, one source for the panel, the HTTP API (<c>GET /api/butcher/catalog</c>) and later features.
/// <para>Every effect in <see cref="ButcherSettings"/> must have an entry (<see cref="Unscored"/> lists the ones that do not; the scenario
/// tests fail on any), so a new effect cannot be added without a score.</para>
/// </summary>
public static class ButcherCatalog
{
	public enum Tier { Forced, Steered, MechanismReal, Plausible, Pure }

	/// <param name="Path">The settings path, camelCase as the panel and the JSON have it (<c>unsafe.cpuPins</c>).</param>
	/// <param name="Purity">0-100.</param>
	/// <param name="ByConsole">A different score on a console where the part sits elsewhere (inside a chip instead of on the board), by console key.</param>
	public sealed record Entry(string Path, string Group, string Name, int Purity, string Why, IReadOnlyDictionary<string, int>? ByConsole = null)
	{
		public Tier Tier => TierOf(Purity);
		/// <summary>The score on one console (<see cref="ByConsole"/> when it has one, else <see cref="Purity"/>).</summary>
		public int PurityOn(string? console) => console != null && ByConsole != null && ByConsole.TryGetValue(console, out int p) ? p : Purity;
	}

	public static Tier TierOf(int purity) => purity >= 90 ? Tier.Pure : purity >= 70 ? Tier.Plausible : purity >= 40 ? Tier.MechanismReal : purity >= 15 ? Tier.Steered : Tier.Forced;

	private static IReadOnlyDictionary<string, int> On(params (string console, int purity)[] p) => p.ToDictionary(x => x.console, x => x.purity);

	public static readonly IReadOnlyList<Entry> Effects = new Entry[]
	{
		// ---- picture
		new("consolePins", "picture", "Picture bus (VRAM pins)", 95, "The picture chip's external bus to its VRAM chips: an open or shorted trace, a cracked joint."),
		new("cartPins", "picture", "Cartridge pins (CHR bus)", 97, "Dirty or bent cartridge connector pins: the single most common NES fault."),
		new("ciramA10", "picture", "CIRAM A10 (mirroring pin)", 93, "A real cartridge pin; a bad contact gives exactly this."),
		new("videoDac", "palette", "Video DAC lines", 72, "Inside the picture chip on these consoles: a dead colour bit is a real failure, but of a damaged chip."),
		new("lcd", "picture", "LCD driver lines", 99, "A failed LCD column or row driver: the classic dead vertical lines of a Game Boy or Game Gear screen."),
		new("vramChip", "picture", "One VRAM chip failing", 93, "The SNES builds its word-wide VRAM from two byte-wide chips: one dying wrecks half of every word."),
		new("paletteBus", "palette", "Palette RAM bus", 70, "Palette / colour RAM is on the picture chip's die on every console here."),
		// ---- sprites
		new("sprites.xJitter", "sprites", "Sprite X jitter", 50, "Positions read wrong (a real result of a bad sprite-table path), chosen as an offset rather than a bit fault."),
		new("sprites.yJitter", "sprites", "Sprite Y jitter", 50, "As X jitter."),
		new("sprites.tile", "sprites", "Sprite tile", 52, "As jitter, on the tile index."),
		new("sprites.attr", "sprites", "Sprite attribute bits", 62, "A stuck flip / palette / priority bit: close to a real sprite-table cell failure."),
		new("sprites.groupLag", "sprites", "Sprite group lag", 45, "Eight sprites keep last frame's bytes: like a missed DMA, rarely this tidy."),
		new("sprites.clone", "sprites", "Sprite clone", 35, "One sprite copied over others: aimed."),
		new("sprites.fieldSwap", "sprites", "Sprite field swap", 40, "The fields of an entry shifted: mechanism-real, unlikely as a fault."),
		new("sprites.forceBit", "sprites", "Sprite force bit", 60, "A sprite-table bit held: close to a stuck cell, but chosen."),
		// ---- sound
		new("sound.busPins", "sound", "Sound register bus pins", 90, "The CPU-to-sound-chip bus is on the board on the Genesis, SMS FM unit and SNES; inside the CPU on the NES and Game Boy.", On(("nes", 80), ("gb", 80), ("gbc", 80), ("sms", 80), ("gg", 80))),
		new("sound.samplePins", "sound", "Sample fetch pins", 88, "The sample / DMC fetch path: on the board on the Genesis, inside the CPU on the NES.", On(("nes", 80))),
		new("sound.dacPins", "sound", "Sound DAC pins", 75, "The DAC is inside the sound chip on most consoles; the SNES DSP feeds an external DAC.", On(("snes", 88))),
		new("sound.bitFlip", "sound", "Sound bit flips", 65, "Noise on a register write: plausible, but random and per write."),
		new("sound.nudge", "sound", "Detune +-1", 30, "Aimed at the pitch registers."),
		new("sound.drops", "sound", "Channel drop-outs", 45, "A dead channel output is real (a failed mixer resistor); muting on a timer is not."),
		new("sound.stutter", "sound", "Stutter", 25, "Replays an old register write: needs the write history."),
		new("sound.drone", "sound", "Drone", 12, "Volume held at maximum: a value chosen to be loud."),
		new("sound.duty", "sound", "Duty force", 15, "A duty value chosen."),
		new("sound.detune", "sound", "Detune (XOR)", 30, "A bit pattern aimed at the period registers."),
		new("sound.wobble", "sound", "Clock wobble", 78, "A drifting or failing crystal / resonator."),
		new("sound.fmUnplug", "sound", "FM unit losing contact (Master System)", 85, "The FM unit's connector or socket losing contact: only the PSG is heard while it is out."),
		new("sound.caps", "sound", "Dried-out audio capacitors", 93, "Old electrolytic capacitors on the audio path: muffled sound, crackle, cut-outs. The most common repair on old consoles."),
		// ---- bands (a register changed for a few scanlines: what a CPU writing mid-frame does, not a physical fault)
		new("bands.shift", "bands", "Band: shift", 45, "Mid-frame scroll change."),
		new("bands.slip", "bands", "Band: slip", 45, "Mid-frame scroll change."),
		new("bands.patternSwap", "bands", "Band: pattern swap", 45, "Mid-frame pattern table change."),
		new("bands.nametableSwap", "bands", "Band: nametable swap", 45, "Mid-frame tile map change."),
		new("bands.bgOff", "bands", "Band: background off", 45, "Mid-frame layer disable."),
		new("bands.spritesOff", "bands", "Band: sprites off", 45, "Mid-frame layer disable."),
		new("bands.spriteHeight", "bands", "Band: sprite height", 45, "Mid-frame sprite size change."),
		new("bands.bend", "bands", "Band: bend", 45, "Mid-frame scroll curve."),
		new("bands.extra1", "bands", "Band: console extra 1", 45, "Mid-frame register change (Mode 7 wobble on the SNES is an aimed matrix push).", On(("snes", 35), ("wm", 35))),
		new("bands.extra2", "bands", "Band: console extra 2", 45, "Mid-frame register change."),
		new("bands.extra3", "bands", "Band: console extra 3", 45, "Mid-frame register change."),
		// ---- palette (display only)
		new("palette", "palette", "Palette hue modes", 5, "A colour transform: no console fault does this."),
		new("palette.invert", "palette", "Palette invert", 8, "A transform (a failed composite inverter stage is barely conceivable)."),
		new("palette.channelSwap", "palette", "Palette channel swap", 20, "A mis-wired RGB / SCART cable is real; a console fault is not."),
		new("palette.posterize", "palette", "Palette posterize", 5, "A transform."),
		new("palette.cycleFrames", "palette", "Palette cycle", 3, "A clock-driven show."),
		// ---- unsafe
		new("unsafe.cpuPins", "unsafe", "CPU pins (cartridge bus)", 97, "Dirty cartridge connectors: the classic."),
		new("unsafe.soundCpuPins", "unsafe", "Sound CPU pins", 88, "The SPC700 / Z80 bus to its RAM chips is on the board."),
		new("unsafe.ramBus", "unsafe", "Work-RAM bus", 93, "External work-RAM chips on every console here except the Game Boy Color (on the die).", On(("gbc", 75))),
		new("unsafe.dmaPins", "unsafe", "DMA pins", 70, "The DMA unit's address lines are inside the CPU on all of them."),
		new("unsafe.coproPins", "unsafe", "Coprocessor pins", 90, "SA-1 / Super FX / DSP-1 sit on the cartridge board."),
		new("unsafe.videoBus", "unsafe", "Video-chip register bus", 85, "On the board between CPU and picture chip on the NES and SNES; inside the Game Boy.", On(("gb", 70), ("gbc", 70))),
		new("unsafe.cicReset", "unsafe", "Lockout chip (CIC) reset", 97, "The CIC failing to agree with the cartridge's: the console resets about once a second (the famous blinking NES)."),
		new("unsafe.resetBounce", "unsafe", "Reset line bounce", 90, "A worn reset button or a noisy reset circuit pulsing the reset line."),
		new("unsafe.powerSag", "unsafe", "Power sag (brown-out)", 92, "A failing power supply dips: the console resets and RAM keeps only part of what it held."),
		new("unsafe.looseCart", "unsafe", "Loose cartridge", 96, "A badly seated or tilted cartridge: bursts of faults on the cartridge bus (the Game Boy's tilt sensitivity)."),
		new("unsafe.dramRot", "unsafe", "DRAM refresh rot", 82, "DRAM whose refresh fails loses whole rows to leakage (SNES work RAM, Genesis VRAM and work RAM are DRAM)."),
		new("unsafe.heatSoak", "unsafe", "Heat soak", 85, "Cracked joints and tired contacts open up as the board warms: intermittent faults get worse the longer it runs."),
		new("unsafe.esd", "unsafe", "Electrostatic discharge", 80, "A static zap from a finger or a cable: one moment of bit flips everywhere."),
		new("unsafe.linkNoise", "unsafe", "Link port noise (Game Boy)", 80, "A dirty or half-inserted link port picking up noise as data."),
		new("unsafe.padTh", "unsafe", "Pad TH line (Genesis)", 92, "The pad cable's select wire broken or shorted: the button multiplexing goes wrong (6-button pads especially)."),
		new("unsafe.ghostNmi", "unsafe", "Ghost NMI", 80, "Noise on an interrupt line."),
		new("unsafe.dropNmi", "unsafe", "Dropped NMI", 80, "A broken or weak interrupt trace."),
		new("unsafe.stuckIrq", "unsafe", "Stuck IRQ", 80, "A shared open-collector IRQ line held low."),
		new("unsafe.z80BusHold", "unsafe", "Z80 bus held (Genesis)", 82, "BUSREQ held: a board-level fault."),
		new("unsafe.z80ResetHold", "unsafe", "Z80 reset held (Genesis)", 82, "The Z80's reset line held: a board-level fault."),
		new("unsafe.clockStall", "unsafe", "Clock stall", 76, "A clock that stops: a cracked crystal, a bad joint on it."),
		new("unsafe.clockRush", "unsafe", "Clock rush", 55, "A clock at double rate: a wrong oscillator, unlikely as a fault."),
		new("unsafe.padLines", "unsafe", "Controller lines", 98, "Worn membranes, broken cable wires, stuck buttons: the most common fault of all."),
		new("unsafe.ramRot", "unsafe", "RAM rot", 88, "Failing RAM cells and soft errors."),
		new("unsafe.vramRot", "unsafe", "VRAM rot", 88, "As RAM rot."),
		new("unsafe.soundRamRot", "unsafe", "Sound RAM rot", 88, "As RAM rot."),
		new("unsafe.echoAbuse", "unsafe", "Echo abuse (SNES)", 20, "Echo pushed to its longest and loudest: a chosen register state (it reproduces the real echo-buffer overrun)."),
		new("ram", "unsafe", "RAM nudges", 18, "Steered: learns which bytes are game variables and nudges them within their range."),
	};

	/// <summary>The pin fault codes (PinGroupSettings: 1 stuck low, 2 stuck high, 3 bridged). Flicker keeps the fault's score: it is what dirty contacts do.</summary>
	public static readonly IReadOnlyList<(int Code, string Name, int Purity, string Why)> Faults = new[]
	{
		(1, "stuck low", 96, "An open line pulled down, or a short to ground (corrosion does it)."),
		(2, "stuck high", 95, "An open line pulled up, or a short to the supply."),
		(3, "bridged", 92, "A solder bridge or a conductive whisker between neighbouring lines."),
		(4, "floating", 96, "An open line nothing drives: it keeps the charge of the last value put on it (NMOS bus capacitance)."),
	};

	public static Entry? Find(string path) => Effects.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

	// Settings properties that are not effects of their own: the containers, the master switch, and parameters of another effect.
	private static readonly HashSet<string> NotEffects = new(StringComparer.OrdinalIgnoreCase) { "enabled", "sprites", "sound", "bands", "unsafe", "ciramFlicker", "unsafe.padFlicker" };
	private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase) { "sprites", "sound", "bands", "unsafe" };

	/// <summary>Every effect path in <see cref="ButcherSettings"/>, camelCase (the containers' children, and the top-level effects).</summary>
	public static IReadOnlyList<string> SettingsPaths()
	{
		static string Camel(string n) => char.ToLowerInvariant(n[0]) + n[1..];
		var paths = new List<string>();
		foreach (var p in typeof(ButcherSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			string top = Camel(p.Name);
			if (Containers.Contains(top))
			{
				foreach (var c in p.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
				{
					string path = top + "." + Camel(c.Name);
					if (!NotEffects.Contains(path)) paths.Add(path);
				}
			}
			else if (!NotEffects.Contains(top)) paths.Add(top);
		}
		return paths;
	}

	/// <summary>The effects in <see cref="ButcherSettings"/> that have no catalog entry (empty = every effect is scored).</summary>
	public static IReadOnlyList<string> Unscored() => SettingsPaths().Where(p => Find(p) == null).ToList();
	/// <summary>Catalog entries whose path is no longer in <see cref="ButcherSettings"/> (a renamed or removed effect), sub-options aside.</summary>
	public static IReadOnlyList<string> Orphans()
	{
		var paths = new HashSet<string>(SettingsPaths(), StringComparer.OrdinalIgnoreCase);
		return Effects.Select(e => e.Path).Where(p => !paths.Contains(p) && !paths.Contains(p.Split('.')[0])).ToList();
	}
}
