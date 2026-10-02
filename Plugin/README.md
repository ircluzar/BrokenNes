# Bogue :: BrokenNes 2: the FL Studio plugin

BrokenNes runs four ways. All of them use the cores in `Windows/NesEmulator/`:

| Entrypoint | Project | Certified by |
|---|---|---|
| Desktop | `Windows/` | `UAT/entrypoints/desktop-smoke.ps1` |
| Web (Lite) | `WebLite/` | `UAT/entrypoints/web-smoke.ps1` |
| Plugin in a test host | `Plugin/BrokenNes2.Plugin` + `Plugin/BrokenNes.FruityHost` | `FruityHost selftest` (37 checks) |
| Plugin in FL Studio | the same DLL | `UAT/plugin/fl/fl-certify.ps1` |

`UAT/certify.ps1` builds and runs all of them and writes one report.

## Why a native plugin, not a VST

FL Studio only sends piano-roll **slides and per-note pitch** to its own native ("Fruity") plugins. A VST
instrument gets per-note velocity and nothing else. Pitch bends are the point of BrokenNes2, so it is a native
plugin: a DLL that exports `CreatePlugInstance` and hands FL an object shaped like a C++ `TFruityPlug`
(the layout Bendy uses, github.com/superjoebob/bendy_redo). It is written in C# and compiled with Native AOT: no
.NET runtime needs to be installed.

## The projects

- **`BrokenNes.Fruity`** (library): the FL SDK in C#: structures, the function table built from
  `[UnmanagedCallersOnly]` pointers, parameters / automation / saved state, a generic editor, GDI helpers, and
  `FruityPluginBase`, the class a plugin derives from.
- **`BrokenNes2.Plugin`** (Native AOT DLL, `BrokenNes2_x64.dll`): the instrument, on `Core/BrokenNes.Core`. FL shows it as **Bogue :: BrokenNes 2**; the DLL and its folder keep the file name `BrokenNes2` because FL finds a native plugin by its file name.
- **`BrokenNes.FruityHost`** (exe): the test bench, a stand-in for FL's host (see below).

## Consoles and sound chips

An emulator is of one **console** (the *Console* parameter and the three buttons in the editor): **NES**, **Game Boy** or **SNES**. A console
decides:
- **the sound chips it lists:** the 17 NES cores (FIX first), the Game Boy chips (DMG, DMGS: 8 channels) and the SNES chip (S-DSP). Picking a
  chip of another console switches the console, and switching the console picks its first chip. The MNES and WF cores (MIDI-style, not chips) are not offered.
- **the CPU and PPU of a running game:** not choices any more. Each console gets the best of its own family from BrokenNes's `CoreCatalog`
  (FIX for the NES; the real Game Boy and SNES cores otherwise).
- **what ROM mode runs:** a NES, Game Boy (also Game Boy Color) or SNES game, through BrokenNes's console sessions. The picture is scaled by a whole
  number to fill the screen (Game Boy 3x, NES and SNES 2x). A Game Boy or SNES game plays through the **Mix** channel; per-channel stems exist for NES games only.
  The Game list takes a `.nes`, `.gb`, `.gbc`, `.sfc`, `.smc` or `.zip` and switches the emulator to the file's console by itself.
- **Direct mode:** every console's chip takes the NES registers (the Game Boy and SNES chips are the register bridges DMG/DMGS/SNES), so the four
  tone channels exist on all of them.

## One instance = one channel of an emulator

Like any instrument, an instance of BrokenNes2 is one voice of its own: **one NES channel**, with its own piano roll, its own
mixer track, its own effects. The channels belong to an **emulator**, and instances coordinate so that they share one
(FL loads the DLL once per process, so a static `EmulatorHub` is all it takes).

| Channel | Direct mode | ROM mode |
|---|---|---|
| Pulse 1, Pulse 2, Triangle, Noise | FL's notes play that channel of a bare NES sound chip | the game's own sound for that channel (a *stem*) |
| Mix | not available | the whole game's sound |

**Which emulator** (the *Emulator* parameter):
- **Auto** (the default) joins the first auto-created emulator that has the wanted channel free, and opens a new one only when none
  has: the fewest emulators that fit. With Channel = **Auto** too, a new instance takes the first free channel: four instances fill
  emulator #1 (pulse 1, pulse 2, triangle, noise), a fifth opens #2. A closed instance frees its channel for the next one.
- **#1 ... #8** names an emulator explicitly (created if it does not exist). That is how a Game Boy chip gets its own emulator next to
  the NES one: put an instance on #2 and pick the DMG sound chip there. An explicit emulator never fills up with Auto instances.
- A channel already used by another instance of the emulator is **refused** (the editor says so, the instance is silent) and handed
  over as soon as it frees up. In ROM mode, Auto channel means "Mix first" (so a lone instance plays the whole game). A Game Boy or SNES game
  has no per-channel stems, so an instance that sits on a tone channel when such a game loads moves to **Mix** (when Mix is free) instead of
  going silent: the first instance always hosts the game's whole sound.

**What the instances on an emulator share:** the console, mode (Direct/ROM), sound chip, the game and its picture.
Change one in any instance (or by FL automation) and every instance shows and uses it. Volume, pan, tuning, pulse duty and noise
mode belong to the instance. A saved project stores each instance's emulator and channel, so numbering and channels come back as saved.

**How it sounds in Direct mode:** each instance runs its own bare chip of the emulator's sound chip and plays only its channel. The
emulator is the shared configuration and clock, not a mixing bus: there is no latency between instances, and each channel has its own
audio output to route in FL. (Four instances cost four chips: about 6x real time for four sliding voices; a single one 20x+.)

**How it sounds in ROM mode:** the game runs once per emulator. Every frame produces the game's whole mix, and, for each claimed
channel, a **stem**: a bare chip that gets only that channel's register writes (plus the shared frame counter), replayed at the
clock the game made them (a small `ApuWriteTap` in the bus, off unless the plugin enables it). Summing the four stems matches the
mix (correlation 0.998 on VRUN). Instances read their own channel from a ring buffer at their own pace; the game is advanced by
whichever instance needs sound it does not have yet, so FL's mixer threads can all call in. An open editor keeps the picture moving
even when FL is not rendering audio.

FL's notes do **not** drive the game yet: that needs the VRUN live-driver ROM (spec: VRUN `docs/26-live-audio-bridge-spec.md`). Until
then ROM mode plays whatever the ROM plays by itself (VRUN's title music, an AudioPlayer ROM's first song...), split into channels.

## The editor

One dark, self-drawn window (no Win32 controls): the emulator's **screen** on the left, controls on the right.

- **Screen:** the game's picture in ROM mode; in Direct mode the message *Direct Mode: Bypassing the ROM*. A status line under it.
- **EMULATOR** (shared): *Emulator* (Auto / #1..#8, listing what each holds), *Console* (NES | Game Boy | SNES), *Mode* (Direct | ROM:
  switching to ROM loads the game at once), *Sound chip* (only the chips of the console), *Game* (the built-in VRUN, the loaded file, or
  "Load a game file..."). A sound-chip change takes effect on the running game.
- **Reset Console** (bottom right): starts this emulator over for every instance on it. Direct mode: held and jammed notes end (FL gets the voices back), each instance's chip is rebuilt. ROM mode: the game restarts from power-on (which also recovers a crashed one). Nothing else changes: instances keep their channels and settings.
- **About:** the button at the top right opens a window about how the plugin was made, what it uses and what it writes (see below).
- **Channels:** five cells, one per channel, each *free* / *in use* / *this*, with a level meter. Clicking a free cell moves this
  instance there.
- **THIS INSTANCE:** *Channel* dropdown, and sliders: Volume, Pan, Coarse (+-24 st), Fine (+-100 ct), plus Pulse duty on a pulse
  channel or Noise mode on the noise channel. Double-click a slider to reset it; the wheel nudges; right-click any control for FL's
  own parameter menu (link to a controller, automation).

The game built into the DLL is VRUN's frozen build (`Resources/default.nes`, `builds/2026-10-01_tearfix`). There are no built-in Game Boy or SNES
games: a console with no game loaded says so on the screen.

## Inputs: playing the game

The **Inputs** button (bottom left) opens a window for Player 1's controls, so a game running in the emulator can be navigated (to reach a place
whose instruments you want, say). Three sources are added together and sent to the game of the emulator the instance sits on, for NES, Game Boy
and SNES games alike (the NES and Game Boy use A, B, Start, Select and the D-pad; the SNES uses X, Y, L and R too):
- the **keyboard**, read only while the plugin window is the active one (so typing elsewhere in FL is left alone). FL also plays notes from typed
  keys, so use a gamepad or switch off FL's typing keyboard to piano keys if they clash;
- an **XInput gamepad** (the left stick is a second D-pad);
- an **on-screen pad** in the window: hold a button with the mouse.

Click a binding, then press a key or a gamepad button to rebind it (Esc cancels, right-click clears). Mappings and the two on/off switches are
remembered for the Windows user (`HKCU\Software\Bogue\BrokenNes2`), not per project. Defaults: arrows, X Z S A Q W, Enter, right Shift;
gamepad with its right-hand button as the SNES A.

## Instrument Runaway

The **Instrument Runaway** button (bottom left, ROM mode with a game) freezes the game's main processing: its CPU and picture chip (and so its
sound driver's commands: music and sound effects) stop, the picture stays on screen, and the game is silent. Press it again to carry on exactly
where it stopped. Reset Console, loading another game, or leaving ROM mode also ends it.

On a **SNES game** the instruments stay usable. When Runaway starts, the plugin copies the game's sound memory (64 KB of audio RAM: its BRR samples
and sample directory, plus the 128 S-DSP registers). Every instance then becomes a **sampler** of that game, on its own private S-DSP over its own
copy (so instances never have to agree on timing, and each has its own audio output):
- FL notes play the game's own samples, **polyphonically** (eight DSP voices per instance, the oldest note gives way to a ninth); piano-roll slides,
  bends, velocity, Coarse/Fine all work, as in Direct mode;
- the **Instrument** picker (a dropdown) lists the samples the game has loaded, read from its sample directory: slot, address, length, loop or
  one-shot, and whether the game was playing it when it froze. It is a heuristic (a directory is only 4-byte entries, and other data can look like a
  sample): the list runs from the first slot that holds a well-formed BRR sample until the first slot that does not. A slot setting the game has
  nothing in (a new instance starts on slot 0) plays the instrument the game was sounding, else the first one found. The envelope is the one the game
  had set for that sample when one of its voices used it, else an organ-like one (instant attack, held until the key goes up); the game's echo and
  master volume settings are kept. $1000 plays a sample at its own rate at C5, so use Coarse/Fine to tune it;
- a project saved in Runaway keeps a copy of that sound memory (about 64 KB), so the instruments come back when it opens (the game itself starts
  frozen again).

On a **Game Boy game** every instance becomes a sampler of the game's sound unit too. When Runaway starts, the plugin copies the sound registers as
the game left them (duty, envelopes, the wave channel's volume code, the noise settings) and the 16 bytes of wave RAM (the wave channel's instrument).
Each instance owns a private real `APU_GB` core, set up from that copy and clocked by hand, playing **one Game Boy channel** chosen with the
**Channel** dropdown: Auto (pulse 1), Pulse 1, Pulse 2, Wave or Noise. A channel is monophonic like the real one:
- pulses use the game's duty and envelope shape, starting at the note's velocity; the **Duty** dropdown overrides the duty (the game's, 12.5, 25,
  50, 75 %); pitch follows slides and bends;
- Wave plays the game's own waveform; Noise uses the game's envelope and the **Noise mode** slider (long or short sequence), the pitch sets the
  noise clock;
- a project saved in Runaway keeps the copy (49 bytes, tag `GBS1`). The DMGS sound chip (a different core) has no Runaway sampler: only the
  frozen picture and silence.

On a **NES game**, Runaway freezes and silences the game only: there is no instrument playing for it yet (its Direct mode already plays the chips,
but not with the game's own settings). The game's music driver is not run while frozen: nothing here plays the game's music.

## The About window

It opens by itself **the first time the plugin is opened on a machine** (and again if `About.Revision` is raised because the text changed), and
the About button opens it any time. It exists for transparency and for one shoutout, and nothing else:
- **Read this first** (on top): made for FUN, no actual research, 100% written by AI, not accurate emulation; then how it was made (never compared
  with real hardware; its tests only check it does what the author asked; the emulation
  cores are the author's own experimental project, partly derived from open-source work);
- **Where to get the real deal: "Check out VSTs from Plogue"** (below it, in a plain panel): real, human-crafted plugins that are correctly accurate; if you make real work, buy their
  plugins; with a button that opens `https://www.plogue.com/` in the browser (only when clicked), and a one-line note that it is a personal
  endorsement, not an affiliation.
A one-line footer points to `THIRD_PARTY_NOTICES` and `LICENSE.txt` in the repository for credits, licences and what the plugin stores.
The "read first" text is `Resources/about.txt` (`# ` heading, `* ` big statement, `- ` bullet); the shoutout wording is the `Shout*` constants in `About.cs`. Edit and rebuild.

## Direct mode details

- **Pitch bends:** pitch, volume and pan are re-read from FL's live voice values every 64 samples (~1.3 ms) and written straight to the
  chip's registers. Slides and per-note pitch automation therefore bend smoothly, far finer than a ROM-driven NES (one change per 16.6 ms frame).
- **Monophonic channels:** last note wins, and the older note resumes when the newer one ends. The piano-roll note colour is ignored.
- **Any NES sound chip:** the *Sound chip* parameter lists all of BrokenNes's APU cores (FIX, QN, HI, ..., and DMG/DMGS = the Game Boy
  chip through the NES bridge). It can be switched while notes are held.

Parameters: Sound chip, Volume, Pan, Coarse, Fine, Pulse duty, Channel, Noise mode, Mode, Emulator, Console, Instrument. (Saved state is version 2; projects saved by
the earlier test builds, which had CPU and PPU parameters, load with defaults instead of misread values.)

**Known limitations**
- **Pulse waveform restart at a period high-byte change.** On the NES, writing a pulse channel's period high byte
  also restarts its waveform. A bend crossing a multiple of 256 (period 255 <-> 256 is concert A) does that: one
  audible click, as on hardware. Triangle and noise do not. The tests measure it, count it, and exclude those
  windows from the tight pitch limits. A phase-aware write (defer the high-byte change to the cycle boundary) is the
  planned fix.
- **Mono output.** The chip is mono; the *Pan* parameter pans the instance. A note's own pan is ignored.
- **ROM mode ignores FL's notes** until the VRUN live ROM exists. The DMC channel is only in the Mix (stems need the game's memory).
- **Game Boy and SNES games play through Mix only** (no per-channel stems yet), and there is no built-in game for them. Their chips in Direct mode are register bridges, not native trackers.
- **16 volume steps** (the chip's), so velocity and volume automation are stepped.
- **A late joiner in ROM mode** (an instance added while the game runs) starts within one frame (16.6 ms) of the others, not on the exact sample.
- **QLQ** (a deliberately unstable QuickNES variant) plays an octave sharp; the self-test reports it as a warning.
- **Emulators are per FL process** (an FL project can use up to eight).

## The test bench: `BrokenNes.FruityHost`

A faithful stand-in for FL's host. It loads a plugin DLL, hands it the object FL would (the C++-vtable-shaped
`TFruityPlugHost` that the plugin calls back into), and plays timelines of notes into a **rack** of instances (one per channel, as in
FL) the way FL's mixer does:
- `TriggerVoice` per note-on, `Voice_Release` per note-off;
- live voice levels (pitch in cents from C5, volume, pan) rewritten every block from slide chains and bend curves;
- `NewTick`, then `Gen_Render` for each instance, summed;
- the plugin hands finished voices back with `Voice_Kill`, which the host relays like FL does.

```
BrokenNes.FruityHost selftest <dll> [--rom game.nes] [--only id,id] [--json out.json] [--md out.md]
BrokenNes.FruityHost render   <dll> <fixture.json|name> out.wav [--rate 48000] [--block 256] [--chip QN]
BrokenNes.FruityHost track    <wav> <fromMs> <toMs> [--ref 440]   # print a pitch track
BrokenNes.FruityHost regtrace <dll> <fixture.json> <channel>      # print period-register changes
BrokenNes.FruityHost fixture  <name> out.json                     # the notes FL's project is generated from
BrokenNes.FruityHost state    <dll> out.bin [--channel 1-5]       # the plugin's saved state (for the FL project generator)
BrokenNes.FruityHost editor-shot <dll> out.bmp [--mode rom] [--console 0-2] [--rom file] [--about] [--instances N] [--open chip|game|channel|emulator]
BrokenNes.FruityHost romtrace <dll> <seconds> [--set <param> <value> --at <s>] [--seq param:value,...]   # ROM mode level / frames over time
BrokenNes.FruityHost makerom  gb|sfc <out>                        # the tiny do-nothing Game Boy / SNES ROMs the tests use
BrokenNes.FruityHost analyze  <wav> <fixture.json>                # certify audio rendered by FL
```

**`selftest` checks (each measures real audio or drives the real input path, not just that calls return):**

| Test | What is proven |
|---|---|
| load | the DLL exports `CreatePlugInstance`; the info block FL reads (SDK version, names, flags, parameter count) is right |
| params | names and value text, info flags, set/get/clamp, FL's 0..2^30 automation scaling |
| state | state round-trips and is stable; foreign, truncated, absurd and future-version streams are survived |
| silence | no voices means exact silence and finite samples |
| pitch | pulse 1/2 and triangle at 7 keys each: period register exact, audio within 2.5 cents (worst measured 0.1) |
| range | pitches outside the chip's range clamp; the pulse mutes below period 8; absurd bends stay finite |
| coarse-fine | tuning shifts the pitch by exactly that much |
| slide | slides up and down on all three tonal channels follow the glide (RMS ~1.2 cents) |
| bend | +-60 cent vibrato is followed (RMS ~1 cent) |
| volume, pan | level falls with velocity, zero is silent, master volume scales; pan places the sound |
| routing | each Channel value plays that channel only, the note colour is ignored, four instances share one emulator |
| hub-share | Auto fills channel by channel, a fifth instance opens emulator #2, freed channels are reused, an emulator closes with its last instance, chip and mode changes reach every instance |
| hub-explicit | an explicit emulator #2 with the Game Boy chip next to the NES one, both at the right pitch; Auto never fills an explicit emulator |
| hub-conflict | a channel used by another instance is refused (silent, with a reason), Mix needs ROM mode, the waiting instance gets the channel when it frees up |
| hub-state | saved projects give back each instance's emulator, channel, volume and the emulator's chip, in any restore order |
| consoles | 20 chips, MNES/WF hidden; each console starts on its own chip; picking a chip or a console keeps both in step and reaches every instance; every console sounds in Direct mode; switching console under a held note keeps it sounding |
| mono | newest note wins and the older one resumes, back-to-back notes retrigger |
| lifecycle | every voice is handed back with `Voice_Kill`; bad handles are harmless; 64 simultaneous voices |
| cores | each of the 22 sound chips makes the note, accurate ones on pitch |
| swap | switching the chip mid-note keeps it sounding at pitch |
| rates | 22.05-96 kHz and block sizes 1-4096 keep pitch and continuity |
| realtime | well above real time with four instances sliding, **0 bytes allocated** while playing (inside the AOT DLL); ROM mode likewise (and a test Game Boy / SNES game at 2.5x or better: measured about 16x and 23x) |
| rom-mode | the built-in game (or `--rom`) runs inside the plugin: ~60 fps of frames, sound, picture; the project remembers it; a missing or junk ROM is harmless; ROM mode loads the built-in game by itself |
| rom-chips | the sound chip switches five times while the built-in game runs: it keeps sounding, drawing and running |
| rom-stems | five instances on one game from several threads: the mix and each channel on its own, the four stems sum to the mix (correlation > 0.9), sound chip switch keeps all of them sounding |
| editor | through real mouse messages: every control is drawn, dropdowns open/scroll/pick and tell the host, a click outside closes, emulator and channel picks move the instance, console buttons list only their chips, channel cells, mode switch, slider drag / double-click reset |
| rom-consoles | Game Boy and SNES games (tiny ROMs made by the test) open from a saved project, run, draw at their own size, sit on Mix, switch sound chip; loading a file of another console switches the emulator to it; a file that is not a ROM is refused; the NES comes back with its built-in game |
| reset | Reset Console clears held notes and rebuilds chips for every instance on the emulator (Voice_Kill, silent, registers fresh, nobody moves), and restarts a ROM-mode game from power-on for every listener |
| inputs | pressing Start on VRUN's title screen changes the game against an identical run (the pad reaches it); Game Boy and SNES games take pad input; the Inputs window is modal, shows all 12 rows, the on-screen pad presses and releases, switches and cleared bindings are remembered, defaults come back |
| runaway | a frozen NES game stops (frames, picture) and is silent, releasing resumes it, Reset Console ends it, Direct mode refuses it; on a SNES game the sound memory is captured and notes play a known sample at exact pitches (2000/4000/1000 Hz within a few cents), polyphonically, an unused slot plays the game's instrument, a saved project comes back in Runaway and plays; the buttons work |
| rom-audio | a Game Boy game's own 512 Hz tone and a SNES game's sample reach the first instance's output (Mix) whatever channel the instance was on, and from a Direct-mode instance that loaded the file |
| runaway-gb | Runaway on a Game Boy game: the frozen game is silent; pulse 1 and the wave channel (the game's own wave RAM) play FL notes at the right pitch (within 25 cents), pulse 2 and noise sound; the Channel and Duty dropdowns work through real mouse input (Duty only on the pulses); a saved project comes back in Runaway and plays; releasing gives the whole game back on Mix |
| runaway-snes | Runaway on a SNES game: the instrument picker replaces the slider, lists the game's instruments, picking one plays that sample at the right pitch |
| about | the About window opens by itself the first time only (not for a second instance, not on a machine that has seen it), is modal, shows real text, closes, and reopens from the button |
| rom-editor | switching to ROM loads the game and the picture replaces the message; the picture keeps moving with no audio running; core picks reach the game; Direct brings the message back |
| robust | 60 create/destroy cycles, three emulators concurrently on three threads, garbage calls, destroy with a voice playing |
| log | `BROKENNES_PLUGIN_LOG=<file>` records the host's calls (for diagnosing a host with no output) |

## Building, installing

```bash
dotnet publish Plugin/BrokenNes2.Plugin -c Release     # Native AOT; needs the MSVC tools (vswhere on PATH)
```
Output `BrokenNes2_x64.dll` (about 7 MB: it carries the built-in game). FL loads it from
`<FL>\Plugins\Fruity\Generators\BrokenNes2\`. That is under Program Files, so copying needs administrator rights:
`UAT/plugin/fl/install-to-fl.cmd` does it (one UAC prompt) and refuses while FL is open.

If FL crashes or shows nothing, set `BROKENNES_PLUGIN_LOG` to a file path and start FL: the last line is what FL was
asking the plugin when it stopped.
