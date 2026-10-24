# BrokenNes2: the FL Studio plugin

BrokenNes runs four ways. All of them use the cores in `Windows/NesEmulator/`:

| Entrypoint | Project | Certified by |
|---|---|---|
| Desktop | `Windows/` | `UAT/entrypoints/desktop-smoke.ps1` |
| Web (Lite) | `WebLite/` | `UAT/entrypoints/web-smoke.ps1` |
| Plugin in a test host | `Plugin/BrokenNes2.Plugin` + `Plugin/BrokenNes.FruityHost` | `FruityHost selftest` (64 results since round 9: all pass but the expected QLQ warning; `realtime`'s 5x floor needs a quiet machine) |
| Plugin in FL Studio | the same DLL | `UAT/plugin/fl/fl-certify.ps1` |

`UAT/certify.ps1` builds and runs all of them and writes one report.

The desktop app is also how the plugin gets to a user: **Config > Synthesizer Mode** installs it into FL Studio and restarts the app as a
standalone synthesizer (see "Installing, and the standalone synth" below). Those two are certified by `UAT/plugin/install-smoke.ps1` and
`UAT/plugin/synth-smoke.ps1`.

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
- **`BrokenNes2.Plugin`** (Native AOT DLL, `BrokenNes2_x64.dll`): the instrument, on `Core/BrokenNes.Core`. FL lists it as **BrokenNes2**, the name it reports; the DLL and its folder have the same name, because FL finds a native plugin by its file name.
- **`BrokenNes.FruityHost`** (exe): the test bench, a stand-in for FL's host (see below).
- **`BrokenNes.SynthHost`** (library): hosting the plugin outside FL. `HostSim` / `PluginInstance` / `PluginLibrary` (the host side of the SDK and the
  calls into a loaded DLL; shared with the test bench) plus `SynthRack` (four instances played live) and `MidiRouter`. No audio device and no window of
  its own, so it is tested headless; the desktop app (`Windows/Synth`) adds the device, the MIDI port and the window.

## Consoles and sound chips

An emulator is of one **console** (the *Console* parameter and the five buttons in the editor): **NES**, **Game Boy** (GB), **SNES**, **SMS** (the Master System and the Game Gear) or **MD** (the Genesis / Mega Drive). A console
decides:
- **the sound chips it lists:** the 17 NES cores (FIX first), the Game Boy chips (DMG, DMGS: 8 channels), the SNES chip (S-DSP), the Master System's two chips (**SMS**: the SN76489 PSG; **SMSFM**: the Japanese console's YM2413 FM chip with the PSG, appended last as chip 22) the Genesis chip (**MD**: the YM2612 FM chip and the PSG, see below) and, on the SNES console, the **WideMess** sound chip (**WMS**, appended last as chip 29: see "The WideMess chip"). Picking a
  chip of another console switches the console, and switching the console picks its first chip. The MNES and WF cores (MIDI-style, not chips) are not offered.
- **the CPU and PPU of a running game:** not choices any more. Each console gets the best of its own family from BrokenNes's `CoreCatalog`
  (FIX for the NES; the real Game Boy, SNES and Master System / Game Gear cores otherwise).
- **what ROM mode runs:** a NES, Game Boy (also Game Boy Color), SNES, Master System (also SG-1000 and SC-3000), Game Gear or **Genesis** game, through BrokenNes's console sessions. The picture is scaled by a whole
  number to fill the screen (Game Boy and Game Gear 3x, NES, SNES and Master System 2x, Genesis 1x, or 2x in its 256-wide mode). A NES game has stems for its four tone channels; a **Sega game has one stem per voice of its sound unit** (PSG tones and noise, the Genesis' FM 1-6 and DAC,
  the Master System FM chip's FM 1-9 and rhythm voices: see "The channel model"); a Game Boy or SNES game plays through the **Mix** channel only.
  A **WideMess** game (`.wms`) runs on the SNES console's WMS chip, 400 x 224 at 1x (see "The WideMess chip").
  The Game list takes a `.nes`, `.gb`, `.gbc`, `.sfc`, `.smc`, `.wms`, `.sms`, `.gg`, `.sg`, `.sc`, `.md`, `.gen`, `.smd` or `.zip` and switches the emulator to the file's console by itself (a Master System and a Game Gear game share the SMS console;
  which one it is, is read from the file).
- **Direct mode:** every console's chip takes the NES registers (the Game Boy, SNES, Master System and Genesis chips are the register bridges DMG/DMGS/SNES/SMS/SMSFM/MD), so the four
  tone channels exist on all of them. The WMS chip is the exception: it is the WideMess sound chip itself, played polyphonically (below).

### The Master System console (the SMS chip)

The chip is the Sega **SN76489 PSG** (three square-wave tones and a noise channel, 4-bit attenuators in 2 dB steps), reached the way the Game Boy and SNES chips are: through
the bridge core `APU_SMS` (`Windows/NesEmulator/mix/SegaSoundBridges.cs`), which takes the NES registers the plugin writes and replays them on a real `Sn76489`. The five channels map onto the PSG's four voices one to one
(the Mix channel exists only in ROM mode):

| Plugin channel | PSG voice | What is kept, what is not |
|---|---|---|
| Pulse 1, Pulse 2 | tone 1, tone 2 | Pitch exactly (the PSG runs from twice the NES clock, so a NES timer value t is PSG period t + 1: measured error 0.01 cent). The PSG has one waveform, a 50% square: **Pulse duty has no effect on the tone** (the slider is dimmed); a thinner duty is played quieter so the loudness matches what the NES would play. |
| Triangle | tone 3 | Pitch exactly (period 2 (t + 1)). It is a square wave, not a triangle, at the loudness of a NES triangle. |
| Noise | the noise voice | *Noise mode* long = white noise, short = periodic noise (the short mode keeps its buzz pitch). The NES's 16 noise rates become the PSG's three fixed rates, or exactly any rate while tone 3 is not playing the triangle (then the nearest fixed rate is used). |
| Mix | (none) | ROM mode only. |

Limits, all of them the chip's and none hidden: **notes below 109 Hz (A2) are silent** on every tone voice (the PSG's 10-bit period; a NES pulse reaches 54 Hz, a NES triangle 27 Hz), so
a keyboard note is the pitch asked for or nothing, and the chip counts them (`APU_SMS.LowNotesDropped`; a NES *game* on this core moves such a note whole octaves up instead, see
`LowNotePolicy`); the 15 NES volume steps become 9 or more PSG levels (2 dB steps; the loud ones merge); there is no DMC. No allocation on the audio thread, and the chip does not step
its front NES core (nothing reads it back here), so four PSG instances run about 21x real time against about 7x for the NES chip.

**Master System and Game Gear games in ROM mode:** the Game list, the file dialog and a saved project open `.sms`, `.gg`, `.sg` and `.sc` files; the game runs through the session path (`SessionGame`: Z80, the Master System VDP, the sound unit of the
chosen chip, the Game Gear's stereo mixed to mono) and plays through **Mix** (the 256 x 192 picture, or the Game Gear's 160 x 144). **Per-voice stems:** the PSG's four voices (PSG tone 1, 2, 3 and noise: the channels *Pulse 1, Pulse 2, Triangle, Noise*) on every such
game, and on the **SMSFM** chip the YM2413's nine channels (FM 1-9) and five rhythm voices (Bass drum, Hi-hat, Snare, Tom, Cymbal) too. A stem is the sound unit's own per-voice run of the chips, in step with the mix, so the stems add up to the mix to its 16-bit rounding
(`--sega-stems-test`, and the plugin's `stems-sega`). The stock **SMS** unit is the Western console's, which has no YM2413: a game's YM2413 writes make no sound on it and an instance that asks for an FM voice is refused with a message; start the game on the **SMSFM** chip to
hear them (the chip may be swapped under a running game, but the YM2413 writes the game already made to the unit that ignored them are gone until the game starts again). A Game Gear game has no FM, whatever the chip. Instrument Runaway: see below.

### The Master System FM chip (the SMSFM chip)

The Japanese Master System's sound pair: the **YM2413** (nine FM channels of two operators, 15 built-in instruments and one custom one, volume in 3 dB steps) and the **SN76489 PSG**, reached through the bridge core `APU_SMSFM` (`Windows/NesEmulator/mix/SmsFmBridge.cs`, the bank in
`mix/OpllPatchBank.cs`) the way the Genesis chip is. Direct and ROM mode. It is appended last, as chip 22 on console 3 (nothing before it moved). How the channels map:

| Plugin channel | Voice | What is kept, what is not |
|---|---|---|
| Pulse 1, Pulse 2 | FM channels 1, 2 | Pitch to the chip's 9-bit F-number and block by nearest: **at most 3.4 cents off from 49 Hz up** (the F-number's step; 6 cents at the bottom of the NES triangle's range), measured 2.4 cents worst over 21 notes. The timbre is the **instrument** the *Instrument* parameter picks. Loudness is the NES channel's own in the chip's 3 dB steps (a thinner *Pulse duty* is played quieter), within 2.3 dB of the NES mixer's formula at every volume and duty cycle for the sine, organ and horn. |
| Triangle | FM channel 3 | A sine on the chip's custom instrument at the NES triangle's fixed loudness. The chip has **one** custom instrument: a ROM instrument on the pulses leaves it to the triangle, a designed instrument is loaded into it and so plays the triangle too. |
| Noise | the PSG's noise voice | As on the SMS chip. |
| Mix | (none) | ROM mode only. |

**The Instrument parameter is the instrument picker** (22 entries, a dropdown on the pulses):

| # | Instruments |
|---|---|
| 0-14 | the chip's own: Violin, Guitar, Piano, Flute, Clarinet, Oboe, Trumpet, Organ, Horn, Synthesizer, Harpsichord, Vibraphone, Synth bass, Acoustic bass, Electric guitar |
| 15-20 | designed here: Sine, Rectified (the chip's half-sine wave), Fed-back lead, Soft organ, Bell, Pluck |
| 21 | PSG square (no FM: the pulses are the PSG's square tones, 109 Hz and up) |

The designed instruments were written for this project (see `sega/THIRD_PARTY_NOTICES.md`) and **nobody has listened to them**: what is measured is pitch, level, harmonic content and that an instrument can change under a held note. Limits, none hidden: the volume register has 16 steps of 3 dB, so velocity is
stepped coarser than on the PSG (2 dB) or the YM2612 (0.75 dB); the percussive instruments (Guitar, Piano, Harpsichord, Vibraphone, Acoustic bass, Electric guitar, Bell, Pluck) fade by themselves whether or not the note is held, and the Violin and Trumpet have the chip's vibrato (+-14 cents at 6.4 Hz);
the Synth bass has its carrier at half the base frequency, so the bridge sets the base an octave up and the instrument reaches half as high; the top note is about 6.2 kHz (a higher note is silent and counted: `APU_SMSFM.NotesAboveFmRange`); some quiet instruments cannot reach the loudest NES level (counted: `TooQuietSyncs`); there is no DMC.

### The Genesis console (the MD chip)

The chip is the Genesis / Mega Drive's sound pair: the **YM2612** (six FM voices of four operators each, eight algorithms) and the **SN76489 PSG**, reached through the bridge core `APU_MD`
(`Windows/NesEmulator/mix/GenesisSoundBridges.cs`), which takes the NES registers the plugin writes and plays them on a real `Ym2612` (the ymfm port the Genesis uses) and a real `Sn76489`. **Direct mode, and the sound unit of Genesis games in ROM mode** (see below). It is appended as chip 21 (the
*Console* parameter's value 4, `Cx.Genesis`; nothing before it moved) and the 12 parameters are unchanged in meaning. How the five channels map:

| Plugin channel | Genesis voice | What is kept, what is not |
|---|---|---|
| Pulse 1, Pulse 2 | FM voice 1, FM voice 2 | Pitch to the chip's frequency registers (block and fnum chosen by nearest: **at most 0.8 cent off from 54 Hz up, 1.6 cent at the bottom of the triangle's range**; the NES's own timer steps are coarser than that above 250 Hz). The timbre is the **FM patch** the *Instrument* parameter picks (below). Loudness is the NES channel's own (a thinner *Pulse duty* is played quieter; the duty does not change the timbre), set in 0.75 dB steps through the carriers' total level, within 0.6 dB of the NES mixer's formula at every volume and duty cycle (measured on the sine, brass and additive-square patches; all 16 patches are within 0.7 dB of the NES chip at its loudest step). |
| Triangle | FM voice 3 | A pure sine (algorithm 7, one carrier) at the NES triangle's fixed loudness; it does not follow the patch. |
| Noise | the PSG's noise voice | *Noise mode* long = white, short = periodic. Any NES noise rate is exact (the PSG's tone 3 is free to set it). |
| Mix | (none) | ROM mode only. |

**The Instrument parameter is the FM patch picker** for this chip (a dropdown in the editor listing the bank, shown on the pulse channels; per instance, saved with the project like any parameter; a value past the end is the last patch):

| # | Patch | What it is |
|---|---|---|
| 0 | Brass | two 1:1 pairs a few cents apart whose modulators swell and settle (algorithm 4) |
| 1 | Buzz lead | a fed-back operator bending a 1:1 carrier: bright, strong odd harmonics |
| 2 | Square | additive carriers at 1x, 3x, 5x, 7x at 1/n: a band-limited square |
| 3 | Pulse 25% | additive carriers with the spectrum of a 25% pulse |
| 4 | Organ | four drawbars at 1x, 2x, 4x, 6x |
| 5 | Strings | one 1:1 modulator over three carriers, the outer two a few cents either side, slow attack |
| 6 | Clarinet | a modulator at twice the carrier's frequency: odd harmonics only |
| 7 | Flute | a whisper of 1:1 modulation on a sine, soft attack |
| 8 | E. piano | a bright 14x tine that fades over a mellowing body |
| 9 | Pluck | a hard 1:1 modulator that fades: bright, then mellow |
| 10 | Bell | a 7x modulator that fades over a fundamental and an octave partial |
| 11 | Marimba | a 3x modulator that fades fast: a knock, then a round tone |
| 12 | Bass punch | a fed-back 1:1 modulator that fades quickly |
| 13 | Bass growl | a four-operator chain, every ratio 1:1: harsh |
| 14 | Sine | one carrier and nothing else (also the triangle's voice) |
| 15 | PSG square | no FM: the pulses are the PSG's square tones (50% duty, **109 Hz and up**: a note below that is silent and counted) |

**These patches were written for this project** from FM theory and the YM2612 register map (see `sega/THIRD_PARTY_NOTICES.md`); none is copied from a game, a driver or an emulator, and **nobody has listened to them yet**:
what is measured is each patch's pitch (the fundamental to 0.7 cent), harmonic series, loudness against the NES chip, and that a patch can be changed under a held note. The carriers hold their level, so the NES's own
envelope and volume writes shape the note; a modulator may fade (a pluck, a bell). `--md-patches` prints each patch's measured spectrum and `--md-patches --wav-dir <dir>` writes a short phrase per patch.

Other limits, none hidden: the chip counts the notes it cannot play (`APU_MD.NotesAboveFmRange`: above about 6.6 kHz; the FM chip reaches the NES's lowest notes, 54.6 Hz for a pulse and 27.3 Hz for the triangle); there is no DMC;
the YM2612 is the discrete Model 1 chip (its ladder effect included, compensated in the loudness maths). Each instance owns its own chip, so four instances are four YM2612s: **8.3x real time** with four sliding voices on a shared machine (the
floor is 5x; the NES chip measures 7x and the PSG chip 21x in the same test), **0 bytes allocated** on the audio thread, and a patch can change every second under them.

**Genesis games in ROM mode** (`.md`, `.gen`, `.smd`; the Game list, the file dialog and a saved project): the game runs through the session path (`SessionGame` over `MdSession`: the 68000, the Z80 sound CPU, the VDP, the YM2612 with its DAC and the PSG) and plays through
**Mix**; the picture is 256 or 320 wide and 224 or 240 high, shown at its own size. **Per-voice stems:** PSG tone 1, 2, 3 and noise (the channels *Pulse 1, Pulse 2, Triangle, Noise*), **FM 1-6** and the **DAC** (the *Voice* parameter): eleven in all, from the sound unit's own run of the chips, in step with the
mix. They add up to it within 3 sixteen-bit steps (`--sega-stems-test`: the discrete chip's DAC offsets and ladder effect are in the stems exactly as in the mix). An instance that asks for FM 7-9 or a rhythm voice is refused with a message and the game does not run ahead for it. Instrument Runaway: see below.
**The Genesis is the heaviest console here**: the 68000, the Z80 and the VDP are emulated whole, so a real Genesis game takes a good part of a core (see "What it costs").

### The WideMess chip (the WMS chip, WideMess milestone 7a)

WideMess is BrokenNes's souped-up SNES fantasy console (`docs/projects/widemess-spec.md`; section 8 says how it appears in each host). In the plugin it is the **SNES console's second sound
chip, WMS**, appended last as **chip 29** (after the six Presets chips; nothing before it moved), the way the Japanese Master System is the Master System console with its FM chip. There is no
sixth console button: a WideMess is a SNES until a game unlocks it.

**Direct mode: the chip itself, many notes at once.** Not a NES-register bridge like the other chips: each instance owns a private WideMess sound chip (the S-DSP with its voices 8-31 attached and
unlocked, `WmsSampler`) and FL's notes play on its new voices, **24 notes at once on one channel** (a 25th takes the oldest note's voice; that note stays silent until FL releases it). The
**Instrument** parameter picks one of 14 built-in **16-bit PCM** instruments (a dropdown on every channel; out of range is the last):

| # | Instrument | What it is |
|---|---|---|
| 0 | Auto | the channel's own: Pulse 1 / 2 a pulse at the *Pulse duty* (12.5, 25, 50, 75 %), Triangle a triangle, Noise a noise in the *Noise mode* (long = white, short = periodic) |
| 1-3 | Square, Pulse 25%, Pulse 12.5% | the NES pulses' shapes without their steps |
| 4-6 | Triangle, Sawtooth, Sine | |
| 7 | Organ | drawbars 1, 2, 3, 4, 6, 8, held |
| 8 | Strings | every harmonic at 1/n^1.4, a 0.2 s attack |
| 9-11 | Pluck, E. piano, Synth bass | with decaying S-DSP envelopes |
| 12-13 | Noise, Noise (periodic) | a 16,384-sample white noise; the NES short noise's 93-step sequence; the note sets the rate (C5 = 32 kHz) |

Every waveform is eleven one-cycle loops (4,096 samples down to 4), one per octave, each band-limited so that nothing above 16 kHz is in it at twice its own rate (`WmsBank`). A note starts on the
loop whose rate puts the pitch register between $1000 and $1FFF: the pitch is **exact to a fifth of a cent** from A0 to C8 (measured), and a held note bends an octave or more up (and down as far as
FL goes) on the loop it started on. The instruments were designed here, from the additive formulas of the waveforms named; nobody has listened to them: what is measured is pitch, harmonic content,
level and polyphony. Level: a 25% pulse at full velocity is as loud as the NES chip's (0.0 dB); each note's voice is kept at a third of the chip's range, so eight loud notes fit the S-DSP's 16-bit
sum, as on the console (the spec: the voices add with saturation). Cost: four instances with six sliding notes each, 9x real time; nothing allocated on the audio thread (the chip is made on the
UI thread when the instance is placed; Reset Console makes it again there).

**WideMess games in ROM mode.** The Game list and the file dialog open `.wms` files: the emulator goes to the SNES console, takes the WMS chip and the game runs on the WideMess console (the
picture 400 x 224, shown at 1x: the editor's 512 x 480 screen takes whole-number scaling only), its whole sound on **Mix** (no per-voice stems, like a SNES game). A SNES game on the WMS chip
runs on the WideMess console too, locked: frame for frame and sample for sample the SNES chip's game. A project keeps the chip it was saved with: a `.wms` game in a project on the SNES chip
runs on the WideMess console with the SNES's own sound unit (its new voices are not heard, and the status line says so). The chip can be swapped under a running game (WMS and SNES): the game goes
on. A project keeps the whole machine as for every console (`GAM1`).

**Instrument Runaway on a WideMess game** whose sound unit is unlocked: every instance becomes a sampler of the game's WideMess sound chip (`WmsSnapshot`: the 1 MB of sound RAM, the S-DSP's
registers, the second register page and the new voices' directory). The **Instrument** picker lists the game's instruments: every well-formed entry of the new voices' directory (16-bit PCM and
BRR, anywhere in the 1 MB; they play on the new voices, 24 at once), then the S-DSP's own directory's samples (8 at once). $1000 plays a sample at its own rate at C5; the envelope is the one the
game had on a voice that played it. A project saved in Runaway keeps the copy in section **`WMS1`** (deflated, CRC-32; a damaged or newer section is refused and the Instruments tab says so). A
locked WideMess game (or a SNES game) is the SNES sampler's, as before. Not built: editing WideMess instruments in the Instruments tab (it says so), presets of them, pushing them into the game,
recording the new voices (the Record window records the S-DSP's 8).

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
A whole FL song does get in, as a MIDI export: see "Test MIDI export" below.

## The channel model

The channel ids (`Ch` in `Bn2Params.cs`) grew append-only, so a saved project keeps its channel: **0-4** Pulse 1, Pulse 2, Triangle, Noise, Mix; **5** the Sampler (not a channel: what every instance sits on in Instrument Runaway); **6-14** FM 1-9; **15** the DAC; **16-20** the Master System FM chip's rhythm voices (Bass drum, Hi-hat,
Snare, Tom, Cymbal). In a Sega game's ROM mode the first four are the PSG's tone 1, 2, 3 and noise. The thirteenth parameter, **Voice**, names a voice beyond them (0 = use the *Channel* parameter, 1-9 FM 1-9, 10 DAC, 11-15 Bass drum ... Cymbal); *Channel* keeps its meaning. The fourteenth, **Recipe** (round 6), picks one of the *game voices* Instrument Runaway learned from a SNES or Genesis game's sound driver (0 = a plain instrument; see "Game voices" below). What exists:

| Emulator in ROM mode | Channels |
|---|---|
| NES | Pulse 1, Pulse 2, Triangle, Noise, Mix |
| Game Boy, SNES | Mix |
| Master System / Game Gear, SMS chip | PSG tone 1-3, PSG noise, Mix |
| Master System, SMSFM chip | PSG tone 1-3, PSG noise, FM 1-9, Bass drum, Hi-hat, Snare, Tom, Cymbal, Mix |
| Genesis | PSG tone 1-3, PSG noise, FM 1-6, DAC, Mix |

Direct mode has the four NES-register channels on every chip. An instance that asks for a voice its emulator's console or chip does not have stays on that emulator with a message and no channel (silent, and the game does not run ahead looking for sound for it) and is placed again when the chip or console changes. The editor's channel strip shows the voices the emulator
offers as compact cells (T1 T2 T3 Nz F1-F9 DAC BD HH SD TM CY, then Mix); the Channel dropdown lists them by name.

## The editor

One dark, self-drawn window (no Win32 controls): the emulator's **screen** on the left, controls on the right.

- **Screen:** the game's picture in ROM mode; in Direct mode the message *Direct Mode: Bypassing the ROM*. A status line under it.
- **EMULATOR** (shared): *Emulator* (Auto / #1..#8, listing what each holds), *Console* (NES | Game Boy | SNES | SMS | MD), *Mode* (Direct | ROM:
  switching to ROM loads the game at once), *Sound chip* (only the chips of the console), *Game* (the built-in VRUN, the loaded file, or
  "Load a game file..."). A sound-chip change takes effect on the running game.
- **Inputs / Corruption / Instrument Runaway** (bottom left): the Inputs window, the Corruption window (see "Corruption" below) and the Runaway switch.
- **Reset Console** (bottom right): starts this emulator over for every instance on it. Direct mode: held and jammed notes end (FL gets the voices back), each instance's chip is rebuilt. ROM mode: the game restarts from power-on (which also recovers a crashed one). Nothing else changes: instances keep their channels and settings.
- **About:** the button at the top right opens a window about how the plugin was made, what it uses and what it writes (see below).
- **Channels:** one cell per voice the emulator offers (five for a NES game, up to twenty-one for a Master System FM game), each *free* / *in use* / *this*, with a level meter. Clicking a free cell moves this
  instance there.
- **THIS INSTANCE:** *Channel* dropdown, and sliders: Volume, Pan, Coarse (+-24 st), Fine (+-100 ct), plus Pulse duty on a pulse
  channel or Noise mode on the noise channel. Double-click a slider to reset it; the wheel nudges; right-click any control for FL's
  own parameter menu (link to a controller, automation).

The game built into the DLL is VRUN's frozen build (`Resources/default.nes`, `builds/2026-10-01_tearfix`). There are no built-in Game Boy or SNES
games: a console with no game loaded says so on the screen.

### Tabs: Emulator, Instruments, Presets (round 6)

A strip of three tabs sits on the header rule above the right column (left edge at the column's). Everything listed above is the **Emulator** tab and is unchanged: same window (904 x 664),
same controls in the same places, same ids, same FL parameters (the tabs add none); the only pixels that differ from the editor before the tabs existed are the strip itself
(`FruityHost bmpdiff` of a capture before and after, strip masked: 0 pixels differ). **Instruments** and **Presets** make the window **420 pixels wider** (`UiLayout.WideExtra`, 1324 x 664: the picture, the
status line and the Inputs / Corruption / Instrument Runaway buttons stay where they are; Reset Console belongs to the Emulator tab) and going back restores 904.
The editor resizes its own window, then calls the host's `FHD_EditorResized` (id 2 in FL's public `fp_plugclass.h`, `Fhd.EditorResized` here): FL fits its frame around the editor window again.
The test bench's host does what FL does (its frame follows) and the standalone synth's panel follows too (`SynthRack.EditorResized`).

- **Instruments** works with Instrument Runaway: with Runaway off it says *Switch Instrument Runaway on to see the game's instruments* (no game loaded: *Load a game in ROM mode, then switch Instrument Runaway on to see its instruments*; no model
  connected: *The instrument model is not connected in this build of BrokenNes2.*). With the game frozen: the **instrument list** (scrolling list), the **fields** of the selected instrument (a row each: name, a slider with - and + for
  ranges and signed values, a stepper for enums, toggles for bit fields, the value, a lock; hovering a row explains the field; Shift on a stepper steps by ten), a **waveform view** with loop markers and a **hex field** for the
  selected blob (click a byte, type two hex digits), and the **corruption tools**: Strength, Randomise, Nudge - / + (on the field whose name was clicked, else the model picks), Lock all / Unlock all (and a lock per field and for the sample data),
  Undo / Redo with the step and the seed shown, **Send to Presets** and **Push into the game**. Every action's answer (done, or refused with the reason) appears on the status line under the tools.
- **Push into the game / Remove from game** (round 6, `inject`): an edit normally changes only Runaway's private copy (what FL's notes play). **Push into the game** also puts the edited instrument into the *running game's own* sound memory and sound chip, so
  that when you switch Instrument Runaway off the game's own music plays it. The status line says exactly what stuck; nothing is silent. Pushing the same instrument again replaces the earlier push; **Remove from game** puts the original back and the list marks a pushed
  instrument "in the game". What a push can hold depends on the console and the game's sound driver (each line measured on a real game, see `docs/projects/round6-inject.md`):
  - **SNES**: the sample's bytes and its loop pointer are written into the game's audio RAM (they stay until the game loads other sounds over them). The envelope (ADSR/GAIN), the left/right volume weights, the tuning and the echo / noise / pitch-modulation flags live in registers the game's
    sound driver rewrites, so they are *forced* instead: wherever a voice plays that sample, the S-DSP uses your values in place of the driver's (the driver's registers are never changed). Noise is only audible if the game itself set the DSP's noise clock.
  - **Genesis**: an FM patch is loaded at every key-on of a channel that holds the game's original patch (every channel the game plays it on; the driver's volume is kept; pan, AMS and FMS only where you changed them), so the edit holds even when the driver reloads its patch. A DAC sound is recognised
    by its first 12 samples in the stream the driver writes and handed to the chip as the edited sound (an edited rate plays it faster or slower inside the driver's own pace; the first 11 samples are heard as the game has them, and the sound ends where the driver ends it).
  - **Game Boy**: the wave channel's wave RAM, written now and again whenever the wave channel is triggered while the RAM holds the game's original wave. **Pulse and noise cannot be pushed** (refused with the reason).
  - **Master System / Game Gear with the FM unit**: the YM2413's user instrument (instrument 0), loaded at every key-on of a channel that plays it. The chip's fifteen ROM instruments are read-only; **the PSG cannot be pushed**. **The NES cannot be pushed** (it has no instrument memory and no editable instruments).
  A project saved with pushed instruments keeps them (section `INJ1`: the table of overrides and the original bytes; the pushed bytes themselves are in the saved machine). A damaged or newer section is refused and the tab says so; a replaced game (reload, Reset Console, a corruption started from a stockpile) takes its pushes with it and the tab says so.
- **Presets**: the library list with a search box (name, tag, console or game) and a console filter, the details of the selected preset, and **Load, Rename, Delete (two clicks), Export, Import, Audition**. No presets are provided: an empty library says
  how to get some. Everything about them is in "Presets: keep a ripped instrument, play it with no ROM" below.
- **All of it is wired to two interfaces, not to a model:** `Plugin/BrokenNes2.Plugin/Ui/Contracts.cs` (`IInstrumentModelProvider`, `IPresetLibrary`, `UiServices`). The real models plug in from their own files
  (`Ui/UiBoot.cs` shows how); until they do the tabs say "not connected". `Ui/StubModels.cs` holds demonstration models (a 30-instrument SNES-like model with every field kind and a seeded corruptor with history, a 14-preset
  library) that **only the self-test installs** (hook 403); nothing in normal use does.
- **Widgets** (`Ui/`): `ScrollList` and `VScrollBar` (wheel, draggable thumb, page by clicking the track), `Slider`, `Stepper`, `BitToggle`, `TextInput` (caret, Home/End/Delete, Ctrl+A, Enter, Esc; no clipboard), `HexField`, `WaveView`,
  on a small drawing surface `UiCanvas` with the editor's palette. Typing reaches the focused field through `WM_CHAR` / `WM_KEYDOWN`; while a text or hex field has the keyboard the Inputs window's keyboard-to-pad polling is off
  (typing a name does not press the game's buttons), and the keyboard goes back to the host's frame when the field lets go.
- **Control ids:** 1000-1999 belong to the tabs (1000-1002 the strip, 1100-1599 Instruments, 1600-1799 Presets; the exact map is in `Ui/UiIds.cs`); everything below 1000 is the Emulator tab's and the Corruption / Inputs / About windows' and
  is never reused. Test hooks for the tabs: `Fpd.TestBase + 400..499` through the editor (listed in `Ui/Bn2Editor.Tabs.cs`).
- **Not verified in real FL:** whether FL's frame follows `FHD_EditorResized` the way the test host's does, whether FL passes typed characters and the wheel to a plugin window (FL also plays notes from typed keys), and the wide window on a
  small screen. The tabs are wired to the real models (`imodel`, `presets`); the stub models only exist for the self-test of the tabs themselves.

## Inputs: playing the game

The **Inputs** button (bottom left) opens a window for Player 1's controls, so a game running in the emulator can be navigated (to reach a place
whose instruments you want, say). Three sources are added together and sent to the game of the emulator the instance sits on, for NES, Game Boy
and SNES games alike (the NES and Game Boy use A, B, Start, Select and the D-pad; the SNES uses X, Y, L and R too):
- the **keyboard**, read only while the plugin window is the active one (so typing elsewhere in FL is left alone). FL also plays notes from typed
  keys, so use a gamepad or switch off FL's typing keyboard to piano keys if they clash;
- an **XInput gamepad** (the left stick is a second D-pad);
- an **on-screen pad** in the window: hold a button with the mouse.

Click a binding, then press a key or a gamepad button to rebind it (Esc cancels, right-click clears). Mappings, the two on/off switches and the
d-pad setting below are remembered for the Windows user (`HKCU\Software\BrokenNes\BrokenNes2`), not per project. Defaults: arrows, X Z S A Q W,
Enter, right Shift; gamepad with its right-hand button as the SNES A.

**D-pad protection.** A real d-pad cannot press Left and Right, or Up and Down, together, but a keyboard can. So when the three sources together hold
two opposite directions, **neither is sent** (the pad is neutral on that axis; Up+Down+Right sends Right). The tick box **Allow opposite d-pad directions**
at the bottom of the Inputs window is **off by default** (remembered as `InputAllowOpposite`; an install or project from before the setting is protected).
The rule lives in one pure function, `DpadGuard` (`Windows/NesEmulator/systems/DpadGuard.cs`, shared with the desktop app), and is applied in one place
(`Inputs.Human`, called by the editor's poll). It is for people only: a pad set from outside (the test hooks, a project) is not touched.

**Settings migration.** The remembered settings used to live under an older registry path (named in `Prefs.cs`, `OldRegistryPath`). The first time a
settings store is used with an empty new key, the old values are copied over once; the old key is never changed or deleted. The `prefs` check proves it
on a fake old store and on real (test) registry keys.

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

On a **Master System, Game Gear or Genesis game** every instance becomes a sampler of the game's sound chips. When Runaway starts, the plugin copies what the chips were doing: the PSG's registers; the YM2413's 64 registers (Master System FM); the YM2612's six channel patches (the
28 operator registers, algorithm and feedback, pan and LFO sensitivity) and which channels were sounding; and, for a Genesis game, **the sounds found in the last 65,536 writes the game made to the DAC register**. A Genesis game plays a sample by writing it to the DAC one value at a time at a steady rate: the plugin cuts that stream at its gaps, trims the silence,
lists each distinct sound once (with how often it was played), and, when the end of a sound repeats (a looped instrument), finds the loop and its pitch. Each instance owns private copies of the chips, set up from the copy, and the **Instrument** picker lists what the game had (the parameter is 0 for "Auto", the game's choice, and the instrument number + 1 otherwise):

| Chip | The picker lists | Polyphony and notes |
|---|---|---|
| PSG | square tones, white noise, periodic noise | three tones at once (below 109 Hz silent and counted); the noise's pitch is its shift rate, so periodic noise buzzes near the note |
| YM2413 | the game's user instrument, the chip's 15 instruments, then the PSG's three | nine notes at once; Auto = the user instrument if a channel was keyed with it, else the first instrument being played |
| YM2612 | the six channels' patches as the game left them ("never set up" is flagged), the DAC sounds, then the PSG's three | six FM notes at once; eight DAC voices; Auto = the first channel that was sounding |

FM notes are set at the note's pitch (the carriers' common multiple is the note) and level (YM2413: the 3 dB volume steps; YM2612: the carriers' total level, 0.75 dB steps; velocity 1 plays the level the game had). A **looped** DAC sound plays at the note's pitch whatever rate the game streamed it at (its cycle is the note); a **one-shot** plays at its own speed on C5
and through to its end (a short note does not cut a drum hit). Slides and bends follow FL's live pitch; more notes than voices: the oldest note gives way and stays silent until it ends. All sound goes to both sides (a Game Gear's stereo mask is not kept). A project saved in Runaway keeps the copy (tag `PSG1`, `OPL1` or `OPN1`: about 16 KB for a Genesis game with one DAC sound, never more than 300,000 bytes) and a damaged, truncated or foreign section is dropped, not trusted.

### Game voices: Instrument Runaway listens to the sound driver (SNES and Genesis, round 6)

**Why.** A copy of the sound memory and of the chip registers at one instant holds a game's *samples* and *patches*, but a corrupted game keeps most of its character in what its sound program **does** with the chip over time: which registers it writes at each
note, how it bends the pitch, sweeps the volume, switches the envelope. In a heavily corrupted game that is most of what you hear. Instrument Runaway used to ignore it, so the same instruments sounded clean in the plugin. (Measured: `docs/projects/round6-runaway.md`.)

**What the plugin does now.** From the moment a game starts, the plugin keeps a log of every write the game's sound program makes to the chip: the SPC700's writes to the S-DSP (SNES), the Z80's and 68000's writes to the YM2612's registers and the PSG (Genesis; the DAC's sample stream has its own recorder,
as before). A ring of 262,144 writes holds minutes of play, and nothing allocates on the audio thread. When Instrument Runaway starts, the last **30 seconds** of the log become **game voices** (*recipes*): for each distinct way the driver sounded an instrument, the settings at the key-on (SNES: sample,
envelope, volume, pitch, noise and echo flags; Genesis FM: the whole channel patch, frequency, LFO; PSG: tone or noise, period, attenuation) and a **macro**, the timed changes the driver made to that voice while the note sounded (pitch as cents from the key-on pitch, volume, envelope, any FM register such as the total levels that fade a note,
the LFO, the PSG's attenuation and noise control). A voice that was already sounding when the listening began and was never keyed on again (a stuck noise: the driver had died or stopped playing notes) becomes a **held voice**, "Sounding voice n", with the macro of what it did.

**In FL.** The **Instrument** picker lists the game's samples or patches as before and, below them, the game voices ("Game voice 3: sample #5, ADSR 8FE0", "Game voice 2: FM algorithm 4, feedback 3", "Game voice 5: PSG tone"), each with how often the game played it and what moves in it. Pick one and the FL notes play it: **FL's C5 plays the game's own pitch**, other notes move it
from there (FL's pitch is added to the macro's), velocity scales the volume so the loudest moment of the game's note is as loud as a plain instrument at the same velocity, and the SNES's eight voices / the Genesis' six FM channels, three PSG tones and one noise are shared by the notes as before. The Instrument row's text says what the voice does ("played 63x, 1.20 s, 40 pitch changes, 12 volume changes");
the banner above says what the listening found ("Listened for 10 s: 187 key-ons, 13 game voices"), or that it heard nothing and the game should play first.

**With a corruption.** The Corruption window's old tick, *freeze the game and switch Instrument Runaway on as soon as it starts*, froze the corrupted game before its sound program had run a single frame: the memory was the corrupted game's, but nothing of what the corrupted driver does existed yet. It now reads **Switch Instrument Runaway on by itself after the corrupted game has
played for [10 s]**: Start builds the corrupted game, it plays (you hear it, screech included), and when the time is up Instrument Runaway switches on with the game voices of those seconds. The button cycles 0 (at once: the old behaviour; the result line says nothing of the driver was learned), 5, 10, 20 and 30 s. You can also leave the tick off, let the game play as long as you like and press
**Instrument Runaway** yourself: the last 30 seconds are what counts. A corruption applied to a game that was already running restarts the listening from the corruption.

**A project keeps them** in two new sections after the sound memory: `SPR1` (SNES: a versioned list of recipes, varint-coded, about 1.8 KB for one note with 430 changes) and `GNR1` (Genesis). A damaged, cut or absurd section is dropped and the instruments still come back; an older project loads exactly as before and a newer one loses nothing it knew. The Recipe parameter is saved with the other parameters.

**What a recipe cannot be, so you know what to expect.** It is what one note of the driver did, played again when *you* play a note. It is **not** the song: the game's sequencer (which notes at which times) does not run in the plugin, so a corrupted song, a melody that has gone wrong, the order and timing of the game's notes never carry; you play the notes. It cannot reproduce behaviour that is random or depends
on state that is different every note (a driver that is writing garbage, a pitch that drifts because a variable was corrupted: the recipe holds the one note the plugin chose as typical, the one with the most going on), anything that needs other voices (SNES pitch modulation: the neighbouring voice's output; the YM2612's channel 3 modes), the echo buffer's history, or
global effects that are not one voice's (the SNES's echo feedback or FIR coefficients are kept as the game left them at the end, not replayed; the noise rate is one setting for all voices: the newest noise note's). A macro that is longer than your FL note is cut by your note-off; a note longer than the macro holds its last values. The Master System, Game Gear and Game Boy have no game voices yet
(their Runaway is the copy of the registers as before). What a recipe does carry is measured against the game itself in the record: on a clean and on corrupted SNES games (Super Mario World, Donkey Kong Country) the game's own notes played again from their recorded macros come out at 0.00 to 0.05 of spectral distance from the game's sound (silence is 5 to 11 for a game that is playing), grouped into one recipe per instrument 0.00 to 0.6 (the old plain instruments: 0.3 to 6); on a Genesis game's FM notes 1.1 against 1.5 for the plain instrument (the game's own FM pipeline sets the floor), PSG 0.85 against 1.5. The exception found: a loud, harsh corruption of Donkey Kong Country (471 notes in 10 s, a quarter of the energy above 4 kHz) came out at 1.7, no closer to the game than silence (2.0): see the record, "Screech hunt".

On a **NES game**, Runaway freezes and silences the game only: there is no instrument playing for it yet (its Direct mode already plays the chips,
but not with the game's own settings). The game's music driver is not run while frozen: nothing here plays the game's music.

## Corruption: farm in BrokenNes, play it in FL

The plugin can take corrupted instruments out of a game the BrokenNes desktop app corrupted. The path is: **farm** a corruption in BrokenNes until a game has instruments that sound good broken, **keep** it in a
stockpile (a file), **open** that file in the plugin, **Start** the corruption on the loaded game, then take its instruments over with **Instrument Runaway**. The design is `docs/projects/round5-corr-design.md`, the record
(what was built and every number) `docs/projects/round5-corr.md`.

**What the user does**
1. In the BrokenNes desktop app, run the game (SNES, Game Boy / Color, Master System, Game Gear, Genesis; the NES keeps its own path there), corrupt it (RTC blast, Glitch Harvester "Corrupt and stash"), keep the results you like
   in the stockpile and **Save file...** in the Glitch Harvester. The file (`*.stockpile.json`) holds, per corruption, a name and description, which game it was made on (console and the ROM's SHA-1: never the ROM), the machine it
   was made on (CPU and sound unit), **where the game was** (a saved machine, or the pads from power-on) and **the writes**. It never contains a ROM.
2. In FL, put the plugin in **ROM mode** with **the same game** loaded and the same Sound chip setting.
3. **Corruption** (bottom left, next to Inputs): **Open stockpile...**, pick the file. Each corruption is listed with a green dot when it fits this game and the reason in orange when it does not (another game, another console, made
   with another sound unit, switch to ROM mode first, "it points to a shared base the file does not contain"). The line under the list says what Start will do (loads its saved machine, or replays N frames from power-on).
4. Pick one, **Start**. A new machine is built on a worker thread (a replay shows its progress and **Cancel** stops it; the longest replay is 36,000 frames, 10 minutes of game, anything longer is refused with the number) while the
   game that is loaded plays on; then it replaces it between frames. Tick **Switch Instrument Runaway on by itself after the corrupted game has played for [10 s]** to have it played and then captured: the corrupted game runs for that long (you hear it), then Instrument Runaway switches on with
   the corrupted memory **and** the game voices learned from what its sound program did meanwhile (0 s freezes at once, with only the memory; see "Game voices" above for why that misses most of what a corrupted driver does).
5. **Instrument Runaway** as always: on a SNES game the picker lists the *corrupted* instruments (the corruption of the audio RAM and S-DSP registers is in the copy Runaway takes) and the game voices of the corrupted driver, on a Genesis game the corrupted driver's FM and PSG notes, on a Game Boy game the wave channel plays the corrupted
   wave RAM, and so on. **Remove corruption** restarts the game at the place the corruption was made, without its writes.
6. Save the FL project. It keeps the corrupted game, and a corrupted instrument taken in Runaway keeps its sound.

**What a project keeps.** Instrument Runaway's copy of the sound memory (`SPC1`, `GBS1`, `PSG1`...) already carried the corrupted instruments. The game itself comes back one of two ways, written against what the console's session can do:
- every console now keeps its state (the SNES too, since the `snes-state` work): the **game-state section** (`GAM1`, see `Plugin/CLAUDE.md`) holds the whole machine, corruption included, and the new section **`CRP1`** (`Corruption.cs`; a CRC-32 and UTF-8 JSON) only says where the corruption came from
  (stockpile path, entry id / name / description / summary / content hash, frozen, the frame it started at). Its size is about 0.6 KB;
- a game that cannot keep its state (a console without save states, or a project that could not hold the machine: it was too big or the game refused) is described by **its recorded history** in `CRP1`: every pad the game was handed from power-on (run-length text) and every corruption write with the
  frame it landed at (the plugin records them allocation-free in the audio thread's frame loop, `ConsoleFarm.OnFrame`). Reopened, the game is **rebuilt by replaying that history** on the loading thread (progress shown as the game's status)
  and is the same machine (the check compares memory hashes against a game that never left). About 1.2 KB for the test game; refused over 8 MB (the project then says the history was too long). The self-test keeps this path alive by making the plugin report no state (hook 121).
A section that is damaged (a checksum covers it), cut short or absurd is not trusted and never silent: the editor shows "The project's corruption record is damaged..." over the picture for as long as the game runs.

**What it will not do, and says so on screen:** start a corruption made on another game / console / CPU / sound unit; start one in Direct mode, on a NES game (the plugin starts corruptions on the console games; the NES's corruption lives in
the desktop app for now), or with nothing selected; replay past the cap; open a file that is not a stockpile, is cut off, is a newer version, or is empty. A result stays over the picture for 12 seconds; a project's problem stays.
A game that was **reset**, whose sound chip was swapped, or whose state was put back from a project is no longer a replay from power-on, and the plugin knows it (`ConsoleFarm` poisons its recording with the reason).

Where the code is: `Corruption.cs` (the window's data, `CRP1`), `Emulator.Corruption.cs` (open, which entries fit, start, the history rebuild), `Bn2Editor.cs` (the window, ids 500-525, test hooks 140-144), `Game.cs` (`SessionGame` records the farm),
the shared library `Windows/NesEmulator/corruption/` (stockpile file, memory domains, anchors, `ConsoleFarm`). Test hooks 100-120 in `Bn2Plugin.TestDispatcher`.

## Presets: keep a ripped instrument, play it with no ROM

A **preset** is one instrument ripped from a game (and, if you corrupted it, the corruption: every step with its seed) kept in a file of its own, `*.bn2preset`. Anyone can then play it from FL with **no ROM at all**: the file holds the instrument, never the game (only the game's name and SHA-1, for reference).

**Rip it, corrupt it, keep it:**
1. Put BrokenNes2 in ROM mode with a game (SNES, Game Boy, Master System, Game Gear, Genesis; the NES has instruments too) and press **Instrument Runaway** to freeze it.
2. Open the **Instruments** tab, pick an instrument, corrupt it (Randomise, Nudge, the steppers, the hex field; Undo / Redo as you like).
3. Press **Send to Presets**. A name field opens on the status line, offered with the instrument's own name (` (corrupted)` added after a corruption); **Enter** adds it, **Esc** cancels. The line says what happened.

**Where they are:** `%APPDATA%\BrokenNes\Presets` (the environment variable `BROKENNES_PRESETS_DIR` names another folder). A subfolder is a category; a new preset goes in a folder named after its game. Copy the folder to share your library; nothing ships with the product. Do not commit a preset to a repository: it holds a game's samples (git ignores the extension, and a check fails if one is ever tracked).

**Play it with no ROM:** add another BrokenNes2 and pick a **Presets chip** under *Sound chip* (Direct mode): **PRESETS** (presets of any console) or **PRE-NES / PRE-GB / PRE-SNES / PRE-SMS / PRE-MD** (that console's presets only). The channel strip gives way to a banner and the **Instrument row becomes the preset picker**; or open the **Presets** tab, select a preset and press **Load** (the instance moves to the matching Presets chip by itself). FL's notes play it, with pitch bend, slides, velocity and the Volume / Pan / Coarse / Fine controls; every instance holds its own preset, so several instances play several presets at once. **Audition** plays a middle C for a second and a half through the instance's output without changing anything.
- A game cannot run on a Presets chip (ROM mode puts the console's own chip back); **Load is refused, with the reason, in an instance that runs a game**, and nothing changes.
- The **NES DMC** (sampled sounds) cannot be played yet and says so; the NES pulse, triangle and noise play.
- A preset ripped from a SNES game that was listened to (see "Game voices") also keeps **the way the game's driver played that sample**: on the Emulator tab the *Channel* row becomes **Voice**: *Plain sample, as sent* (what you get on loading) or *Game voice n*.

**In the project:** the preset an instance holds is saved *inside the FL project* (section `PRS1`), so the project plays on another computer, or after the library file is deleted, moved or renamed. The Presets tab lists such a preset as "This project" until the library has the same file. A damaged or too-new copy in a project is refused and a note over the picture says why.

**What it sounds like:** the same as Instrument Runaway playing that instrument from its game (measured: identical audio for every instrument tested, see `docs/projects/round6-presets.md`). Nothing allocates while it plays. **Not verified:** nobody has listened to it (no sound card on the build machine), and real FL has not been tried.

## Test MIDI export: hear an FL song in VRUN's sound engine (round 7)

The point: a song composed in FL Studio has to go back into a game. VRUN (the user's NES game) has its own sound engine and a sound-test player (the AudioPlayer, `src/audioplayer` in the VRUN project). **Test MIDI export** plays an FL song through exactly that engine, inside the plugin, so you can hear whether it replays the way it sounds in FL before it goes into the game.

**What you do:** in FL, **File > Export > MIDI file...**; in BrokenNes2, the **Test MIDI export** button (in the header, next to the plugin's name, on every tab) opens a window: **Open MIDI...** (the standard file dialog), and the song plays at once. Change the song in FL, export again over the same file, press **Reload**: the new export plays from its start. **Play / Restart / Stop** are in the window; with **Follow FL's transport** ticked (the default) FL's Play restarts the song from its start and FL's Stop stops it, so you can start both together and compare by ear. Whether it replays well is your call: the plugin measures, it does not listen.

**What happens:** the MIDI file is converted by **VRUN's own converter** (a byte-for-byte copy of `engine/audio/converter` in `Plugin/BrokenNes2.Plugin/Music/Vrun`; a test keeps it identical to VRUN's whenever the VRUN project is on the machine), on a worker thread. Its four opcode streams are written into a **template**: VRUN's AudioPlayer built once with one song whose four stream arrays are padded to the converter's maximum (`Resources/musictest.nes` and its manifest `musictest.txt`, made by `BrokenDev/tools/music/build-musictest.ps1` from your VRUN tree), together with the song's name (the player's screen shows it) and its loop length (the player's position and loop counter use it). That ROM becomes the emulator's **NES game in ROM mode**: the player screen is in the picture, the four NES channels arrive as the plugin's per-channel stems (Pulse 1, Pulse 2, Triangle, Noise; Mix plays all), any NES sound chip can be picked (VRUN itself uses FIX; the window says so when another one is picked).
If the emulator was elsewhere (Direct mode, another console, another NES game, Instrument Runaway), it is switched to NES and ROM mode and a note over the picture says what was replaced and how to go back; the replaced game stays in the **Game** list (and the song is listed there too, "MIDI test: <file>").

**The facts the window shows** are the things that make the replay differ from FL, nothing else: which MIDI channel each NES channel plays (the converter picks the densest channels; drums on channel 10 go to the noise), notes on MIDI channels the NES has no room for (four at most: dropped), notes thinned because a channel was denser than one change per frame, the loop (the song starts again at the end of the MIDI: its length is the loop), bytes per channel and **truncation** (over 1,280 bytes a channel goes silent before the loop), the tempo (only the first counts: later changes are ignored), pitch bends (round 9: played, see below; the line says where, what they cost, what was thinned and what was not played), notes below A1 (played at the lowest pitch). For example: *"3 channels: square 1 <- MIDI 1, triangle <- MIDI 2, noise <- MIDI 10; square 2 silent"*, *"55 of 132 notes dropped: MIDI channels 4, 5, 6, 7, 8 have no NES channel"*, *"truncated (over 1,280 bytes): square 1 1,923, triangle 3,843; they go silent before the loop"*, *"bends played on square 1, square 2, triangle: 43 slides + 2 pitch steps (+174 bytes), held exact, moving within 12 cents; 1 bend frame thinned (no free frame); not played on noise (no pitch)"*.

**Pitch bends (round 9):** FL's slides and bends, exported as MIDI pitch bends (with the bend range of RPN 0; 2 semitones when a channel does not set it), **play** on square 1, square 2 and the triangle, through VRUN's pitch-bend engine (`engine/audio/README.md` in VRUN, "Pitch bends"): every song is converted with bends on, which changes nothing for a song without bends. A bend held still lands exactly on the period the game's note table gives that pitch (a bend held at +2 sounds exactly like the note two semitones up); a moving one is followed by slopes within 10 cents (or half a frame of its own motion on a fast glide). The engine reads one opcode per channel per frame, and notes never move for a bend: a bend that needs a frame a note already takes is thinned, and the facts count those frames (what was lost comes first in the bends line, so the window never cuts it off). A channel whose bends would make it longer than 1,280 bytes gets a coarser fit, then its held bends only, then none (said in the line: *"at 40 cents: room"*, *"held only: no room for slides"*, *"bends not played on square 2 (no room)"*); the noise has no pitch (*"bends not played on noise (no pitch)"*). **In the game:** VRUN's own build plays a song's bends only when its roster entry says `"bends": true` (the VRUN project's choice, song by song).
**Refused, with the reason on screen** (and the song that was playing goes on): a file that is not a MIDI file or ends in the middle of an event, a MIDI with no notes, a MIDI with **only drums** (the converter's own rule: with no channel besides channel 10 it keeps its old fixed map, MIDI channels 1-4, and the drums are left out, so nothing would play; add a melodic channel), a file over 4 MB, a template this build does not understand (another format or version of the manifest, another opcode format, a ROM that is not the one the manifest describes; round 7's version-1 template, whose plain decoders cannot play bends).

**In the FL project:** the song is kept as the MIDI file's own bytes and its path (section `SNG1`: version, flags, CRC-32), so a reopened project converts the same MIDI into the same ROM and the game-state section (`GAM1`) puts the machine back where it was. A damaged or newer section is refused and the window says so; a project without one opens as before; an older BrokenNes2 skips it.

**How it is checked** (no sound card on the build machine: every claim is a measured register trace, hash or PCM; see `docs/projects/round7-midi.md`): `FruityHost midi-test` (no DLL: the converter against VRUN's own on every roster song, the template against a fresh build, **the patched ROM plays the converter's streams frame by frame** on the plugin's machine (VRUN's decoders modelled, three negative controls per song), and a full rebuild of each song by VRUN's toolchain writes the same registers frame for frame), and `selftest --only midi` (the DLL: button and window with real mouse messages, stems, Play / Stop / Restart, FL's transport, Reload, the project, refusals, 0 bytes allocated on the audio thread; a deliberate bug per scenario).

Round 9 adds the check `bends` (the ROM's own pitch against the MIDI's on songs with glides, octave slides, vibrato, bent notes, bends on every channel and on the drums; no note moves; songs without bends convert byte for byte as before) and compares the registers as each frame's NMI left them: the bend decoders make a busy frame's NMI write on past the frame's end (a few frames a song), which a read at the frame's end would show a frame late (`docs/projects/round9-bends.md`).

**Limits, none hidden:** the template has the AudioPlayer's decoders (the bend decoders on square 1, square 2 and the triangle since round 9; VRUN's game uses the larger SFX-resync variant for `level2` only; in the player no sound effect borrows a channel, so it plays the same); FL's own MIDI starts at once, the song's first note sounds about one NES frame (16.7 ms measured) after FL's Play; FL's song position is not followed (a start always restarts the song from its beginning); the song is the emulator's, not an instance's (one song per emulator); real FL was not tried (that FL sends `FPD_SetPlaying` on Play and Stop is the SDK's word, not a measurement here).

### Capture from FL: the song and its pitch bends, straight from your BrokenNes2 channels (round 9)

The user's report: FL's MIDI export does not carry the piano roll's pitch bends (not even through FL's own MIDI Out channels), and exporting means replacing every BrokenNes2 channel by a MIDI Out channel (by hand or with the "Prepare for MIDI export" macro). BrokenNes2 itself receives the bends (FL hands a native plugin the slides and per-note pitch), so it captures them: **Capture from FL** in the Test MIDI export window.

**What you do:** compose with BrokenNes2 channels as they are; press **Test MIDI export**, then **Capture from FL** (ARMED); press **Play** in FL from the start of the song and **Stop** at its end. Every BrokenNes2 instance of the FL process records what FL made it play; a moment later the song is a MIDI file, converted by VRUN's converter and playing in VRUN's sound-test player in that emulator, with the facts. **Save MIDI...** keeps the file (also in `%TEMP%\BrokenNes2\Captures\`). Pressing Capture from FL again while that emulator plays the capture puts it back in Direct mode first (said). FL's **Export > WAV** with a capture armed works too: the capture follows the samples FL renders, not the clock.

**What is recorded, per instance** (`Plugin/BrokenNes2.Plugin/Capture/`, the instance's side in `Bn2Plugin.Capture.cs`): every note FL starts (TriggerVoice: its InitLevels and FinalLevels pitch as FL hands them, the instance's Coarse / Fine, FL's velocity from `FPV_GetVelocity`), every change of the pitch FL wants it to play while a note sounds (FinalLevels, read at the start of every block: slides, per-note pitch, fine pitch, the channel's pitch, Coarse / Fine automation), every release; on a **clock of its own**: the samples FL rendered on it since FL's `FPD_SetPlaying` 1, at the rate they were rendered at (FL gave no song position in the user's log). Preallocated lanes filled without allocating; anything lost is counted and said.

**The file** (SMF type 1, 960 ticks per quarter): a conductor (FL's tempo, FL's time signature from `FPD_SetTimeSig`, the facts as text) and one track per instance that got a note, named after its role (Pulse 1, Pulse 2, Triangle, Noise, DMC, or FL's name for the channel when FL gives one) on the **MIDI channel VRUN's converter turns into that NES channel**: Pulse 1 -> 1, Pulse 2 -> 2, Triangle -> 3, Noise -> 10 (the converter takes channel 10 for the noise when it has more notes than channel 4, which a capture never uses; the three busiest other channels play the squares and the triangle, the lowest on average the triangle: the window says when that does not give each part its own NES channel). Notes at their real times (the tempo sets only where the beats fall); keys = FL's key + the instance's whole semitones; velocity = FL's value x 128 (FL's 0.5 is 64); **pitch bends** = what the instance plays minus its newest sounding note's key (a BrokenNes2 channel plays its newest note), written whenever the 14-bit value changes, before the note-on they belong to, at a **range per track** (RPN 0) that is the smallest of 2 to 48 semitones holding every move (beyond 48: clamped, counted). Everything ends at the bar line after the last note: VRUN loops the song there. Notes still sounding at Stop end there (said).

**Which key FL means:** FL hands a voice's pitch in cents above the **channel's** root note (`Record/FlNotes.cs`, shared with the recording code): C5 on a channel made in FL (your compositions), MIDI note 0 on a channel FL made by importing a MIDI file. FL tells a plugin no root note: the capture reads C5, unless a channel's notes cannot be read that way (a key above 127): then MIDI note 0, said on screen; channels that fit both follow the sure ones when those all said MIDI note 0. A channel whose root note was moved elsewhere in FL lands in another octave (nothing tells).

**The tempo:** FL's `FPE_Tempo` when FL sends it; else sample rate x 60 / (samples per tick x PPQ) from `FPD_SetSamplesPerTick` (a float in Value: the user's log's 1129579756 = 212.0192 = 44,100 x 60 / (130 BPM x 96)) and the PPQ in `FPD_SetTimeSig`'s struct; else 120 BPM, said as an assumption. The file says which and whether the two agree.

**Facts on screen** (first the summary, then what changes the replay most): events lost, guesses, how many notes slide or bend (**VRUN's engine plays the bends on its squares and triangle since round 9; the converter's bends line in the song box says what it kept**), the converter's own choice of channels, the base key when not C5, notes cut at Stop, overlaps resolved (**one note at a time per channel, as BrokenNes2 and the NES play: an earlier note still sounding when the next one starts ends there and the newer keeps its whole length**; a note struck at the same moment as a newer one is left out; slides inside a note are untouched; the fact counts them, and says when FL still held an earlier note after the newer ended: BrokenNes2 goes back to it there, the file does not), a channel FL did not start (it joins at the others' clock), a channel FL rendered less than the others (Smart disable?), FL's song position jumping (its loop), tempo changes during the take (the first is kept: what VRUN plays), and, when FL answers `FHD_GetMixingTime` asked with offset 0, whether FL's song position agrees with the capture's clock. Everything also goes to the host log, which runs by itself while a capture is armed (with what FL said about time).

**Send my notes and pitch bends as MIDI out** (a tick box in the window, off by default, remembered): each instance forwards the notes FL plays on it and the pitch it plays as pitch bends through `MIDIOut_Delayed`, port 1, on the capture's channels, bend range 48 (RPN 0 first). What FL does with them is not known: another channel whose MIDI input port is 1 (pitch bend range 48) should play them; the SDK's words for the plugin's `FPF_MIDIOut` flag ("only those will be enabled when rendering to a MIDI file") suggest FL's own MIDI export may record them: try it.

**How it is checked** (`docs/projects/round9-capture.md`): `FruityHost capture-test` (no DLL) and `selftest --only capture` (the DLL, played by an **FL-faithful host**: the Dispatcher calls of the user's real log in their order, notes in FL ticks rendered tick by tick, the pitch above the channel's root, slides inside their parent note, per-note pitch automation, velocity / 128, no song position): every note within a tick, the played pitch at every tick within the 14-bit bend step, an independent Python reader, the converter's streams equal to those of the ideal file written from the script, the plugin's machine playing them, FL's Export at another rate, the MIDI out, 0 bytes on the audio thread, a deliberate bug per check.

**Limits, none hidden:** real FL was not tried (the stand-in host follows the log); FL's own MIDI import is reported to read bends at 2 semitones whatever the file says (so a wide slide comes back narrower if you drag the file into FL; other programs read the range); a channel FL stops rendering (Smart disable) drifts (said); a pause in FL ends the take; the take starts where FL's Play starts it (press Play from the song's start); the game itself plays a song's bends only with `"bends": true` in its roster entry (Test MIDI export always plays them; see "Pitch bends").

## Record the music: a SNES or NES game's song into FL Studio (round 8)

(The NES works through the same window, files, offer, Play here, project and live MIDI out: see **"Recording a NES game"** at the end of this section. What follows is written for the SNES.)

The point (the user's words, summarised): freeze a game, take its instruments, then **record the game playing them** and get it into FL Studio as notes on the piano roll, one track per instrument, each replayed by a BrokenNes2 instance on the Presets chip with **no game running**. A corrupted game's music is half instruments and half the way the sound program drives the chip; the recording keeps the second half. SNES games first (the other consoles are the next phase).

**What you do:** the **Record** button (header, right of Test MIDI export, every tab) opens a window. Load a SNES game in ROM mode, press **Record** (it starts at once; or when Instrument Runaway is switched off if the game is frozen; or with FL's Play if **Start with FL's Play** is ticked), let the music play, press **Stop** (or FL's Stop, ticked by default). In a moment the window lists what it found and the **tracks**:
- **Drag the MIDI file into FL** (the big handle: the whole song, one track per instrument, the tempo map) or **drag a track's row** (that instrument's notes alone, a one-track file) onto a channel or a piano roll; **Save MIDI...** writes the song where you choose.
- One **preset per instrument**, named like its track, is already in the Presets library (`Presets\<game>\Recordings\<recording>`); **Add presets** puts them there again (a reopened project does not add them by itself).
- **The tracks are on offer** (from the moment the recording is transcribed, and when a project with a recording is reopened, until you press *On offer (...): stop* or the emulator that made it closes). **The whole workflow: drag the MIDI file onto FL's channel rack, Replace each imported channel by BrokenNes2, press Play.** Every BrokenNes2 made while the tracks are on offer (a replacing one or a new one) **joins them at once** (the window's **CHANNELS** line counts it, its editor says *Linked to the recording*; it stays silent until its first note). When FL plays, each joined channel knows its track from the **first note** FL gives it: the note's **colour**, which FL's import sets to the note's MIDI channel (every track has its own), and its **key** (FL's pitch, read from the channel's own base: see "FL's conventions" below); it plays that track's preset **from that very note** (the right sample on the SNES, the right pulse and duty, triangle or noise on the NES), then moves to the tracks' emulator (Direct mode, PRE-SNES / PRE-NES chip). Each of its notes is then matched to **the recorded note it is** (order, key, the time since the last note, velocity) and plays that note's own bends, volume and velocity from the note's start: **FL's song position is not needed** (FL 2026 gives none). The order you replace the channels in does not matter, nor how FL creates the instance. Details and limits below ("Which track a channel plays").
- **Link to recording** (a big button over the screen of every BrokenNes2 that is not linked, while tracks are on offer) and **Link all BrokenNes2 channels** (the window's CHANNELS line): the anchors when a channel did not join by itself (made before the recording, given a project state with a game or preset, or changed in its editor). A linked channel finds its track from its first note like the others; **Unlink** (its banner) takes it back.
- **Play here** (each row): this channel plays that track, picked by hand (any channel, linked or not). The line above the CHANNELS says what this channel plays, or how it will find its track.
- **Lanes** (off by default): notes of one instrument that sound at the same time get tracks of their own, so each has its own pitch bend and volume (exact, more tracks). Without it, such notes share their instrument's channel bend and the facts say by how much they are off.

**What the MIDI file holds** (SMF type 1, 960 ticks per quarter): a conductor track with the tempo map and the facts as text; per instrument (= the sample number the game keyed; a sample loaded under a number already used is a new instrument) a track on its own MIDI channel (1 to 16 without 10: FL's import keeps it as the notes' colour, which is how a replaced channel knows its track; past 15 tracks two tracks share a channel, paired so that their first notes are different keys: the colour names two and the first note tells them apart), its pitch-bend range (RPN 0, the smallest of 2 to 48 semitones that holds every move), notes whose key is the sample's **root note** (estimated from the sample: the piano roll shows real notes), **pitch bend** for the driver's slides and vibrato, **velocity** = the key-on level and **controller 7** = later volume changes relative to it (100 = as keyed). The presets carry the tuning that makes key + bend give back **the game's own pitch register exactly**. The tempo: when the note onsets sit on a steady grid (85 % or more of them, or 3 of 4 stretches of 6 s agreeing) the file has that tempo (folded into 80-160 BPM) with its changes and the first downbeat on a bar; when not, it says so and the notes are at their real times at FL's tempo.

**The facts the window shows** are everything that keeps the replay from being the game: notes that shared a channel with another note of the same instrument (and the largest pitch and volume error that caused), values shorter than 2 ms merged (below FL's control rate), notes with pitch modulation, a sample switched under a sounding note, an envelope rewritten while sounding or another envelope than their preset's (not replayed), voices already sounding when Record began (not transcribed), writes lost (none, unless the capture ran out of memory blocks), the memory cap (10,485,760 writes, about 160 MB), notes past 200,000.

**Live MIDI out** (the window's last row): while the game plays, the same notes go out as MIDI as they happen, one MIDI channel per instrument, to the **MIDI port** shown (the - and + buttons). In FL, set the MIDI input port of the channels that should play them to that number (or record them into a piano roll). Pitch bend range 12 semitones (announced with RPN 0; set the receiving channels' pitch bend range to 12 if FL does not take it), velocity and controller 7 as in the file. The roots of the samples already loaded are read before it starts; a sample loaded later is keyed as if its rate were C5 until its root is known (a moment) and counted. Switching it off, freezing the game (Instrument Runaway) or replacing the game ends every sounding note. Not done live (the file does): the per-instrument 12-TET offset of the driver, the per-track bend range, lanes, the tempo.

**FL's conventions** (measured in the user's FL 2026 session of 2026-10-18 through the host log, `docs/projects/round8-record.md` section 14; one place in the code: `Record/FlNotes.cs`). A voice's pitch is in cents **above the channel's base key**: on a channel FL made by importing a MIDI file and you replaced by BrokenNes2 it is from **MIDI note 0** (7100 for key 71), on a channel made in FL from **C5 = MIDI 60** (the SDK's reading, and what the FL render certification measured). Nothing in the SDK tells a plugin which: a channel finds its base from its track's keys (and keeps it: `TRK1`). Until reload both the synth and the matcher read every channel from C5, so a replaced channel played 5 octaves up (a NES pulse muted) and never found its track. FL's velocity (`FPV_GetVelocity`) is the MIDI velocity / 128; the note's colour (`FPV_GetColor`) is the imported note's MIDI channel - 1; `FHD_GetMixingTime` takes an offset in samples in the `TFPTime` it fills (the plugin passed NaN until reload: FL answered NaN); FL's import kept no channel name the plugin could read (`CI_TrackName` answered 0).

**Which track a channel plays (import, then Replace).** FL does not tell a plugin which channel it replaced; the notes do (or the channel's name, when FL gives one). Only a **linked** BrokenNes2 looks: it **joins by itself** when it is made while tracks are on offer, or is still pristine (nothing but defaults) when they are offered; FL's parameter pushes and states without a game, preset or track do not change that (they are logged). It is **the user's own** (it leaves, and never joins by itself) when a project state it restores holds a game, a preset or a track, when you change something in its editor, load a preset or a game into it, or record from it. **Link to recording** / **Link all** link any channel by hand; **Play here** gives one its track. A linked channel waiting for its first note is **silent** (FL's Auto emulator may put it on the game's emulator, whose channel it would otherwise play). The matcher (`Record/RecTracks.cs`) keeps hypotheses (track, base key, which recorded note): the first note seeds them with every note of the colour's track whose key the pitch names at base 0 or 60; each next note keeps those whose track has a note of that key at the time that passed since (the instance's own clock at FL's tempo), and FL's velocity narrows them further. **Played from the first note** when the colour (1-15) names the track; with colour 0 (FL's default colour: a note you draw has it too) when the first note is where the song's start puts it (the time since FL's Play), else from the second consistent note. **Sure** (it moves to the tracks' emulator, the matcher keeps following the notes) after 1 note with a colour and the song's start, 2 with colour 0 and the song's start, 3 with a colour, 4 without. A jump (Play from the middle, FL looping a pattern, a moved note) is a note no hypothesis explains: they are seeded again from it and the next notes decide (the first one or two notes after a jump can take another note of the same key); a note the recording does not have plays as FL gives it (its key, its velocity); a channel of other notes is never made sure. Past 15 tracks a colour names two tracks whose first notes differ. The tracks' engines are built when they are offered, so taking one allocates nothing on the audio thread. The project keeps which track a channel plays, how it was found and its base (section `TRK1` version 3).
**Bends and volume.** FL's manual says controller data is not imported with a MIDI file's notes, and FL users report that the import reads pitch bends at 2 semitones whatever range the file sets. So a channel that plays a track plays **each recorded note's own** pitch bends, controller 7 and velocity **from the note's start** (the note it was matched to), and the replay stays exact whatever FL kept, without FL's song position: the window's *Bends + volume from the recording* (on by default, remembered) switches it; off, the channel follows FL's own pitch (at its base) and volume (which you can edit in FL). A note FL bends itself is not bent twice: with the switch on FL's pitch is not used during the note.
**The host log** (diagnostics): `Documents\BrokenNes2 host log.txt` gets what FL does to each instance in its first moments (creation with FL's host version, the instances alive when tracks were offered, states with their sections, parameters FL sets, Dispatcher calls by name, the channel's name FL gave, joining / leaving / linking, the first notes with FL's song positions, colours and lengths, how each found its track, how far FL moved each note's pitch). It runs **by itself for every instance while tracks are on offer**, and whenever **Log FL's calls** is ticked (for the whole FL process, remembered across FL restarts until unticked). Kept under 1 MB (the older part becomes `BrokenNes2 host log.old.txt`). Send it when a channel does not take its track. (The test bench never writes it: its runs log into a scratch file and its `hostlog` check fails if the real file changed.)

**In the FL project:** the recording itself (every chip write and the copies of the samples, compressed; section `REC1`, before `GAM1`, CRC-32) is kept by the emulator's first instance, so a reopened project transcribes it again to the same MIDI file. A damaged or newer section is refused with the reason in the window (a newer one is written back as it came); a recording too big for a project (over 24 MB compressed) is a marker that says so.

**Known limits, none hidden:** effects between voices (pitch modulation, the shared echo buffer, the noise clock) cannot be replayed by separate instances; a recording is that one take (random or state-dependent behaviour as it happened); playing from the middle of the song or after FL loops a pattern, the first note or two can take another recorded note of the same key and velocity (the same sound unless its bends differ) until the notes place it; a note you add or move plays as FL gives it; nothing here could run FL itself: why FL's base differs between channels (its root note is the likely reason), whether FL applies the imported pitch bends during a note (the log now records it for every note), the routing of live MIDI. The host log answers them (see `docs/projects/round8-record.md`, sections 13 and 14). A recording made before FL was restarted without its project can be offered again from its MIDI file and presets (`Emulator.OfferFromFiles`; tests only so far, no button yet).

**How it is checked** (no sound card: every claim is a register comparison): `FruityHost record-test` (no DLL: roots of samples of known pitch, tempo of known onsets, the capture, the synthetic driver's music transcribed and **replayed from the MIDI file through the presets' own pitch function: every value exact unless the transcription reports it**, an independent Python SMF parser, `REC1` damage, live MIDI out, which track a channel plays from its notes, and Super Mario World, Donkey Kong Country and a corrupted Super Mario World when the ROMs are at `X:\EMULATION\...`; 17 deliberate bugs) and `selftest --only record` (the DLL: see the table below).

### Recording a NES game (round 8, record-nes)

Load a NES game in ROM mode (VRUN, a file, a Test MIDI export song) and use the Record window exactly as above. What differs:
- **What a note is.** A NES game's driver can let the chip shape its notes itself (the decay envelope, the length counter, the triangle's linear counter, the frequency sweep), so the plugin does not read notes from the register writes alone: a model of the chip (the FIX chip's rules, held equal to the chip itself) follows the game's writes and says, at every write and every quarter / half frame, whether each channel sounds, its period, its volume and its duty. A note is a stretch during which a channel sounds, cut where the driver strikes it again.
- **Tracks.** One per **channel and its sound**: "Pulse 1, 50%", "Pulse 2, 12.5%", "Triangle", "Noise", "Noise (short)", and one per **DMC sample** (the bytes are copied when the game starts it). A note whose duty changes while it sounds is on the track of **each** duty it uses, struck at the same moment, and silent (controller 7 at 0) on a track while the other duty sounds: the instances' waves run in step, so the duty changes when the game changed it and the wave does not restart. Each copy has its own velocity, so channels recognising their tracks tell such tracks apart by FL's velocity.
- **What the file carries.** The key is the musical note of the period the note holds longest; every period it has (the driver's vibrato and slides, and the hardware sweep's steps) is the key plus a pitch bend found with the plugin's own arithmetic, so the replay writes the game's period registers; the volume (constant, or the decay envelope's level at each quarter frame) is velocity x controller 7, solved exactly. The triangle's notes start at the driver's strike (the chip sounds it at the next quarter frame, the replay's chip does the same).
- **What plays it.** Each track's preset is a **NES preset with the recorded-track behaviour** (its note block, version 3): it plays FL's notes with Direct mode's own register code (one code path, `Record/NesDirect.cs`), the preset's duty or noise mode, the volume register from the notes, no sweep, on the plugin's FIX chip. The tracks' instances settle on a **PRE-NES** emulator of their own. A DMC track's preset is refused with "cannot play on this console yet" (no engine plays the NES DMC).
- **Facts you may see:** notes shaped by the hardware envelope / moved by the sweep / ended by the length or linear counter (replayed as values); duty changes (two tracks); DMC samples (not playable yet); raw sound written straight to the DMC's level, $4011 (not transcribed); notes shorter than 2 ms (left out); a game on another chip than FIX (the presets play on FIX).
- **The project:** the recording is `REC1` **version 2** (a NES session); an older BrokenNes2 refuses it as "made by a newer BrokenNes2" and writes it back as it came. SNES recordings stay version 1.
- **Live MIDI out:** one MIDI channel per channel and duty; a duty change ends the note on one channel and strikes it on the other (the file keeps the wave running instead).
- **Checked by** `FruityHost nesrec-test` (no DLL: the chip model against APU_FIX, NesDirect against Direct mode's old arithmetic, the capture, real games transcribed and replayed from the MIDI bytes value by value, VRUN-converted songs against VRUN's decoder model, the Python SMF parser, REC1 version 2, live MIDI out, the matching of duty tracks) and `selftest --only record-nes` (the DLL). Numbers: `docs/projects/round8-record.md`, section 12.

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

Parameters (13, append-only): Sound chip, Volume, Pan, Coarse, Fine, Pulse duty, Channel, Noise mode, Mode, Emulator, Console, Instrument (the FM chips' patch or instrument in Direct mode; the sample on a frozen SNES game; the duty on a frozen Game Boy game; the instrument on a frozen Sega game), Voice (a Sega voice beyond the four tone channels, see
"The channel model"). Saved state is version 2; after the parameters come the ROM path, `EMU2` (emulator and channel) and then, only in Instrument Runaway, one of `SPC1`, `GBS1`, `PSG1`, `OPL1`, `OPN1` (with `SPR1` / `GNR1`, the game voices, right after `SPC1` / `OPN1`), then (ROM mode) `GAM1`: the whole machine of the game, so a reopened project continues where the game was (see "Project game state" in `Plugin/CLAUDE.md`); the Test MIDI export's song (`SNG1`, round 7) comes right before `GAM1`. Projects saved by the earlier test builds, which had CPU and PPU parameters, load with defaults instead of misread values, and a project saved before the Voice parameter existed loads it as 0.

**Known limitations**
- **Pulse waveform restart at a period high-byte change.** On the NES, writing a pulse channel's period high byte
  also restarts its waveform. A bend crossing a multiple of 256 (period 255 <-> 256 is concert A) does that: one
  audible click, as on hardware. Triangle and noise do not. The tests measure it, count it, and exclude those
  windows from the tight pitch limits. A phase-aware write (defer the high-byte change to the cycle boundary) is the
  planned fix.
- **Mono output.** The chip is mono; the *Pan* parameter pans the instance. A note's own pan is ignored.
- **ROM mode ignores FL's notes** until the VRUN live ROM exists. The DMC channel is only in the Mix (stems need the game's memory).
- **Game Boy and SNES games play through Mix only** (no per-channel stems), and there is no built-in game for any console but the NES. Their chips in Direct mode are register bridges, not native trackers.
- **The Sega chips' limits:** the SMS chip cannot play below 109 Hz; the SMSFM chip's volume is in 3 dB steps and its pitch within 3.4 cents; the YM2413's custom instrument is one for the whole chip (a designed instrument on the pulses also plays the triangle). A NES game cannot be heard through the PSG, the YM2413 or the YM2612 here (the chip selects the console, as for DMG): that trip exists in the desktop app and Lite, where the NES APU menu lists `SMS`, `SMSFM` and `MD`.
- **Sega Runaway reads what the chips were doing at one moment.** A Genesis DAC sound is found in a stream of writes: a driver that plays a drum in pieces, or mixes its samples in software before the DAC, gives pieces or one long stretch; the twelve most played and longest are kept. A YM2612 channel keeps the total levels the game had set (a quiet note's level is baked in); the YM2413's
  rhythm voices (Bass drum ... Cymbal) are not instruments here (the sampler plays the nine melodic channels with the chip's instruments).
- **After a chip swap, a game load or a Runaway release, each instance restarts its reading from "now" on its own**, so a stem can end up a few milliseconds (at most one frame) off the Mix and the other stems, as a late joiner does; the stems add up to the mix to the sample only while they read in step.
- **16 volume steps** (the chip's), so velocity and volume automation are stepped.
- **A late joiner in ROM mode** (an instance added while the game runs) starts within one frame (16.6 ms) of the others, not on the exact sample.
- **QLQ** (a deliberately unstable QuickNES variant) plays an octave sharp; the self-test reports it as a warning.
- **Emulators are per FL process** (an FL project can use up to eight).

## Installing, and the standalone synth

Both live in the desktop app (`Windows/Synth`), under **Config > Synthesizer Mode**. The desktop build carries the plugin as `Plugin\BrokenNes2_x64.dll`
beside the exe (a loose file, not bundled into the single-file exe: FL needs a real file to copy and the synth loads it from disk).

**Install to FL Studio...** (or `BrokenNes.Windows.exe --install-vst`, which does the same without starting the emulator) lists every FL Studio found under
`Program Files\Image-Line` (FL's registry keys hold no install path, so the folders are the evidence), says what each holds now (not installed / up to
date / another build), pre-ticks the newest certified one, labels every FL other than 2026 "not certified", and has Browse... for an install somewhere
unusual. Several can be ticked. The copy is tried as the current user; what needs administrator rights is done by one elevated copy of the program
(one UAC prompt), and its outcome is judged by hashing the files. FL running from a target is detected per installation and offers Retry. The DLL is
copied beside the target and renamed over it, so an interrupted copy never leaves a truncated plugin. Command line:
`--install-vst [--fl <FL Studio folder>]... [--quiet] [--result <file>] [--dll <plugin>] [--list]`: with `--fl` there is no picker (scripts, tests),
`--quiet` shows no windows, `--result` writes one line per target, `--list` writes what the picker would offer. Exit 0 ok, 1 failed, 2 backed out.

**Restart as Standalone Synth** (or `BrokenNes.Windows.exe --synth`) starts the program again as a synthesizer and closes the emulator; **Synth > Restart
as Emulator** is the way back. The mode belongs to the process: nothing is remembered, a plain launch is always the emulator. The window hosts the
plugin itself (the same DLL FL gets, loaded like FL loads it): four instances, **Pulse 1, Pulse 2, Triangle, Noise**, one tab each with the plugin's own
editor, sharing one emulator. A tab's settings are the plugin's (sound chip, mode, game, volume...); they and the devices are remembered in
`%AppData%\BrokenNes\synth.json` and `synth-state.bin` (**Synth > Reset synth settings and restart** forgets them; `BROKENNES_SYNTH_DIR` relocates them).
- **MIDI input:** any WinMM device (the first one found is opened unless you chose another or none). *MIDI routing* is either "channel 1-4 plays Pulse 1,
  Pulse 2, Triangle, Noise" or "all MIDI plays the selected tab" (for a keyboard that only knows channel 1). Note on/off (velocity 0 = off), pitch bend
  (+-2 semitones), CC 120/123 (all notes off).
- **Computer keyboard:** `A W S E D F T G Y H U J K O L P ;` is a piano from C4 (note 60), `Z` / `X` move it an octave, played on the selected tab. It can be
  switched off. (In ROM mode the same keys are the game's pad if the plugin's Inputs are on: the notes do nothing there anyway.)
- **Audio:** WASAPI shared mode, at the device's own rate, 40 ms buffer; pick the output in the bar. With no playback device the status line says
  "no audio output" and nothing renders (events are dropped, not queued). `BROKENNES_SYNTH_AUDIO=null` is a *test switch*: it renders in real time into
  nothing, says so on the status line, and exists so the whole path can be certified on a machine with no sound card. It is never a fallback.
- **Level readout** on the status line and a meter; if the engine throws on the audio thread the status line says so.

What it is not: not a VST (only FL, or a host that behaves like FL, can load a native Fruity plugin; the other DAWs get nothing from this), not
verified by ear (see below), and one console of four voices: the Mix channel and per-channel game stems are for ROM mode as in FL.

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
                                  [--demo] [--freeze] [--tab instruments|presets] [--click id,id,...] [--chars text]   # the tabs: demo models, Runaway on, open a tab, click controls by id, type
BrokenNes.FruityHost bmpdiff  <a.bmp> <b.bmp> [--mask x,y,w,h]...   # pixels that differ between two captures outside the masks (how the Emulator tab is shown unchanged)
BrokenNes.FruityHost editor-golden <dll>                          # the rectangle of every control the Emulator tab draws (the golden table of the ui test)
BrokenNes.FruityHost romtrace <dll> <seconds> [--set <param> <value> --at <s>] [--seq param:value,...]   # ROM mode level / frames over time
BrokenNes.FruityHost makerom  gb|sfc <out>                        # the tiny do-nothing Game Boy / SNES ROMs the tests use
BrokenNes.FruityHost analyze  <wav> <fixture.json>                # certify audio rendered by FL
BrokenNes.FruityHost midi-test [--only id,id] [--no-mutants]      # Test MIDI export without the DLL: converter vs VRUN's, template vs a fresh build, engine, full-rebuild reference, bends (round 9), facts, SNG1
BrokenNes.FruityHost midi-trace <song> [from] [to]                # the APU fields of a song in the patched template, frame by frame (what the engine check compares)
BrokenNes.FruityHost editor-shot <dll> out.bmp --mode rom --midi song.mid [--midi-close]   # the Test MIDI export window with a song (or the editor behind it)
BrokenNes.FruityHost capture-test [--only id,id] [--no-mutants]   # Capture from FL without the DLL: FL's pitch base, the tempo, the file read back independently, VRUN's converter, the lanes
BrokenNes.FruityHost editor-shot <dll> out.bmp --capture [--capture-root 0]   # the Test MIDI export window after a take played by the FL-faithful host (root 0: imported channels)
BrokenNes.FruityHost record-test [--only id,id] [--no-mutants] [--seconds n]   # Record the music without the DLL: root, tempo, capture, transcribe, smf, section, live, match, games (SMW, DKC, a corrupted SMW)
BrokenNes.FruityHost record-dump smw|dkc|synth [seconds] [track] [park] [--phases] [--aram]   # a recording's tempo map, tracks and one track's notes (a development aid)
BrokenNes.FruityHost editor-shot <dll> out.bmp --mode rom --console 2 --rom game.sfc --seconds 40 --record 10 [--live]   # the Record window after a recording (live MIDI out on first)
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
| consoles | 23 chips, MNES/WF hidden; each console starts on its own chip; picking a chip or a console keeps both in step and reaches every instance; every console sounds in Direct mode; switching console under a held note keeps it sounding |
| psg | the Master System PSG chip (SMS, console 3): appended as chip 20 with every earlier chip index unchanged, a saved project keeps it; pitch of pulse 1/2 and the triangle at 7 keys each (audio within 2.5 cents, worst measured 0.01; the PSG's tone period read back equals the NES timer + 1, or twice it for the triangle); level against the NES chip at the same notes within 2 dB (4 for a 12.5% pulse), velocity falls monotonically, zero is silent; four instances at once with each tone found in the mix and each PSG voice's registers read back; notes below 109 Hz silent and counted, ultrasonic and absurd bends harmless; the SMS console holds no game until one is chosen; about 21x real time with four sliding voices (the floor is 5x) and **0 bytes allocated** |
| md | the Genesis chip (APU_MD, console 4): appended as chip 21 with every earlier index unchanged, a saved project keeps it and its patch; FM pitch of pulse 1/2 and the triangle at 7 keys each (audio within 2.5 cents, worst measured 0.7; the FM frequency registers read back within 1.8 cents); level against the NES chip within 2 dB for the sine, brass, square and PSG-square patches and the triangle, velocity falls monotonically, zero is silent; the Instrument parameter picks the patch (the sine has no 3rd harmonic, the additive square -9.5 dB, the PSG square -9.5 dB, the 25% pulse -3 dB at the 2nd; out of range is the last patch) and two instances keep their own; polyphony through four instances with each tone found in the mix and the noise on the PSG; the NES's lowest note plays, a note above the FM chip's top is silent and counted; the editor (real mouse) swaps the duty slider for a 16-item patch dropdown on the pulses and has none on the triangle and noise; the Genesis console holds no game until one is chosen; four sliding voices with the patch changing every second at the floor of 5x real time or better, and **0 bytes allocated** |
| fm | the Master System FM chip (SMSFM, console 3): appended last as chip 22 with every earlier index unchanged, a saved project keeps it and its instrument; FM pitch of pulse 1/2 and the triangle at 7 keys each (audio within 3.6 cents, worst measured 2.4; the chip's frequency registers read back within 3.45); level against the NES chip within 3 dB, velocity falls monotonically, zero is silent; the Instrument parameter picks the instrument (the designed sine has no 2nd harmonic, the rectified sine 0.41 of one, the PSG square its 3rd, the fed-back lead a bright series; out of range is the last entry) and two instances keep their own; polyphony through four instances; the NES's lowest note plays, a note above the chip's top is silent and counted; the editor (real mouse) swaps the duty slider for a 22-item instrument dropdown on the pulses; four sliding voices with the instrument changing every second at the floor of 5x real time or better, **0 bytes allocated** |
| rom-md | ROM mode on Genesis games (a 16 KB ROM made by the test: the 68000 programs the PSG for 440.4 and 330.9 Hz and the YM2612 for two FM sines): it loads from a saved project, runs, draws at a Genesis size, plays the PSG tones and the FM sines through Mix, sits on Mix first; the editor shows the Mix cell; Runaway freezes and silences it and releasing gives the sound back; loading a `.md` file switches a NES emulator to the Genesis console and a file that is not a ROM is refused leaving the game running |
| stems-sega | per-voice stems of Sega games: on the Genesis game eight instances (Mix, PSG tone 1-3, noise, FM 1-3) each carry their own voice and not the others (the other tones under 6% of their own), voices the game does not use are silent, and the stems **add up to the mix** (correlation > 0.9999, largest difference under 0.004); FM 7 on a Genesis, FM on the PSG chip and on a Game Gear game are refused with a message and play nothing; a refused instance does not make the game run ahead; moving an instance to FM 6 and the DAC works; on the Master System the PSG chip's stems work and the stock unit ignores the game's YM2413 writes, the chip swap under the running game accepts the FM instances and the sum still equals the mix across it, swapping back refuses them; a game started on the SMSFM chip has FM 1-2 in its mix and in its stems, summing to the mix; a Game Gear game's two PSG stems sum to its mix |
| rom-sms | ROM mode on Master System and Game Gear games (tiny ROMs made by the test: they set PSG tone 1 to 440.4 Hz): they load from a saved project, run, draw at their own size (256 and 160 wide), play the tone through Mix (within 6 cents), sit on Mix first with a strip of the PSG's voices and no FM voice; Instrument Runaway freezes and silences them and releasing gives the sound back; loading a `.sms` file switches a NES emulator to the SMS console and a `.gg` one replaces it, and the console's game is remembered |
| mono | newest note wins and the older one resumes, back-to-back notes retrigger |
| lifecycle | every voice is handed back with `Voice_Kill`; bad handles are harmless; 64 simultaneous voices |
| cores | each of the 23 sound chips makes the note, accurate ones on pitch |
| swap | switching the chip mid-note keeps it sounding at pitch |
| rates | 22.05-96 kHz and block sizes 1-4096 keep pitch and continuity |
| standalone-rack | the standalone synth's engine (`SynthRack`): each tone channel at the pitch the chip can play (whole period steps: ~8 cents apart at 500 Hz), notes of one channel stay in it, bends (+1 = +2 st), note off and every voice handed back, 64 / 441 / 4096-frame callbacks, four channels at once, all notes off, ~7x real time with four channels, 0 bytes allocated by the plugin and ~40 B by the host while rendering, `Accepting` off drops events, saved settings round-trip and damaged state is survived |
| standalone-midi | `MidiRouter`: MIDI channel to rack channel, channels above 4 ignored, raw Windows messages, 14-bit pitch bend, velocity 0 = off, velocity scales level, AllToSelected, CC 123, system / unsupported messages not counted, stray high bits dropped |
| realtime | well above real time with four instances sliding, **0 bytes allocated** while playing (inside the AOT DLL); ROM mode likewise: the test Game Boy, SNES, Master System and Genesis ROMs at 2.5x or better. `BROKENNES_TEST_GAMES=<file;file...>` adds real games as informational figures (the Mix instance alone, and with every stem claimed) |
| rom-mode | the built-in game (or `--rom`) runs inside the plugin: ~60 fps of frames, sound, picture; the project remembers it; a missing or junk ROM is harmless; ROM mode loads the built-in game by itself |
| rom-chips | the sound chip switches five times while the built-in game runs: it keeps sounding, drawing and running |
| rom-stems | five instances on one game from several threads: the mix and each channel on its own, the four stems sum to the mix (correlation > 0.9), sound chip switch keeps all of them sounding |
| editor | through real mouse messages: every control is drawn, dropdowns open/scroll/pick and tell the host, a click outside closes, emulator and channel picks move the instance, console buttons list only their chips, channel cells, mode switch, slider drag / double-click reset |
| rom-consoles | Game Boy and SNES games (tiny ROMs made by the test) open from a saved project, run, draw at their own size, sit on Mix, switch sound chip; loading a file of another console switches the emulator to it; a file that is not a ROM is refused; the NES comes back with its built-in game |
| reset | Reset Console clears held notes and rebuilds chips for every instance on the emulator (Voice_Kill, silent, registers fresh, nobody moves), and restarts a ROM-mode game from power-on for every listener |
| dpad | the d-pad protection through the real editor: all 16 combinations of the four directions from a simulated keyboard (bound keys) are neutral on an opposite pair when the setting is off (the default) and pass unchanged when it is ticked; the keyboard's Left plus the mouse on the on-screen Right is neutral; the tick box is clicked with the real mouse, drawn, remembered (`InputAllowOpposite`) and read back; Reset to defaults protects again; an old preferences file with no such line reads as protected; a pad set from outside (hook 44) reaches the emulator with all four directions |
| prefs | the settings migration (see "Settings migration" above): a fake old file and real HKCU test keys; the new store is filled from the old one once, the old one is untouched and not created, a non-empty new store is not overwritten, no old store means nothing is created |
| inputs | pressing Start on VRUN's title screen changes the game against an identical run (the pad reaches it); Game Boy and SNES games take pad input; the Inputs window is modal, shows all 12 rows, the on-screen pad presses and releases, switches and cleared bindings are remembered, defaults come back |
| runaway | a frozen NES game stops (frames, picture) and is silent, releasing resumes it, Reset Console ends it, Direct mode refuses it; on a SNES game the sound memory is captured and notes play a known sample at exact pitches (2000/4000/1000 Hz within a few cents), polyphonically, an unused slot plays the game's instrument, a saved project comes back in Runaway and plays; the buttons work |
| rom-audio | a Game Boy game's own 512 Hz tone and a SNES game's sample reach the first instance's output (Mix) whatever channel the instance was on, and from a Direct-mode instance that loaded the file |
| runaway-gb | Runaway on a Game Boy game: the frozen game is silent; pulse 1 and the wave channel (the game's own wave RAM) play FL notes at the right pitch (within 25 cents), pulse 2 and noise sound; the Channel and Duty dropdowns work through real mouse input (Duty only on the pulses); a saved project comes back in Runaway and plays; releasing gives the whole game back on Mix |
| runaway-snes | Runaway on a SNES game: the instrument picker replaces the slider, lists the game's instruments, picking one plays that sample at the right pitch |
| runaway-recipes | Instrument Runaway's game voices (round 6), through the real DLL: a synthetic SNES game whose sound driver bends its voice's pitch (every 9 ms) and fades its volume; Runaway's picker lists the game voice below the sample, the Recipe parameter is set by the click, the voice **bends and fades when played from FL** (side band at the bent pitch 1.19 against 0.004 for the plain instrument, a 1.27x fade against 1.00x) and FL's note 67 moves it a fifth; the Corruption window's *play for* learns a driver corruption that freezing at once cannot (a corrupted bend of 1,040 cents against 0 pitch changes at once); the project keeps the voices (`SPR1`, 1.8 KB) and a reopened project plays the same sound; 25 randomly damaged recipe sections, a lying count and an older project are survived; the Genesis half: a tiny game's FM note and PSG tone become game voices, picked in the editor, played at the game's pitch (261.6 / 392 Hz and 440 / 331 Hz) with FL's on top, `GNR1` round trip, 20 damaged sections; **0 bytes allocated on the audio thread** in every case (this also fixed a 576-byte-per-block allocation the SNES sampler already made) |
| runaway-state | a project keeps the whole machine of a ROM-mode game, a corrupted byte of work RAM included: for NES (VRUN and a counter ROM), Game Boy, Game Boy Color, Master System (PSG and FM chip), Game Gear and Genesis, saved frozen and saved running, the reopened game is at the saved frame with the machine byte for byte the saved one, the corrupted bytes back, the frozen picture back and a frozen game still frozen; after Runaway off and the same frames its machine, picture, memory and frame count equal those of a run that never left; older projects, unknown / damaged / cut / absurd sections, another game at the saved path, an oversize state, several instances of one project, saving while the game is still being built; a game with no state (the SNES) says so over the screen. `BROKENNES_TEST_GAMES` adds real games |
| corruption | corruptions from a stockpile, end to end. The test farms with the shared library the way the desktop does (a synthetic SNES game with a real SPC700 instrument in audio RAM, counter games for the Game Boy, Game Boy Color, Master System, Game Gear and Genesis), writes the stockpile file, then drives the real editor with real mouse messages: the window opens and is modal, an empty window says what to do, Open stockpile reads the file (a bad one is refused with its reason: not there, random bytes, cut off, another format, newer version, empty), entries made on another game or console are refused with their reason and the game is not touched, the fitting one starts and the machine equals the farm's (work RAM, audio RAM, frame), 40 frames later still equal, the result stays over the picture with text; **the SNES instrument corrupted in the stockpile plays 3.6x louder through Instrument Runaway's picker than the clean one, the Game Boy wave RAM corruption turns the square wave's odd harmonics from 0.50 to 0.14 of the fundamental, and both survive a saved project**; saved project to a fresh plugin: the same machine (every console from its saved state; the SNES also run as a console without states, rebuilt by replaying its recorded history), equal after the same frames to a game that never left; a write made after the start comes back; Reset Console ends the corruption; damaged, cut-short and 2 GB-claiming sections are told on screen, an older project loads as before; a replay past the cap is refused with the number, a long one is stopped by Cancel and the running game is untouched; an entry for another sound unit, Direct mode, a NES game and a game of another console are each refused with their reason; **0 bytes allocated on the audio thread** while a corruption is built and its game plays. Twenty-three failure injections are recorded in `docs/projects/round5-corr.md` |
| runaway-sega | Runaway on Master System, Game Gear and Genesis games (tiny ROMs made by the test). **PSG:** the snapshot is captured, the frozen game is silent, notes play the chip's nearest period within a cent (110 Hz to 1.7 kHz), velocity 0.5 and 0.25 play 6.00 and 12.00 dB down (2 dB steps), a note below 109 Hz is silent and counted, chords and a fourth note on three voices, white and periodic noise (the periodic one buzzes near the note), a slide up an octave is followed, the picker lists Auto and three instruments, a saved project comes back. **YM2413:** the user instrument (a sine) plays within a cent of the F-number's pitch, 3 dB steps (6.02 and 12.04 dB down), the chip's flute is not the sine (its octave is 0.33 of the fundamental against 0.000), the synthesizer bass is still the note (its carrier is at half the base), eleven notes on nine channels, a 10 kHz note counted, slides, the PSG's tones, the 20-item picker, a saved project plays the flute. **YM2612:** the DAC sound is found in the write stream (a 32-sample loop at 420.5 Hz), the FM patch plays within a cent, 6.02 and 12.05 dB down (0.75 dB steps), a patch the game never set up is silent and FM 2's patch plays, eight notes on six channels, a 12 kHz note counted; the looped DAC sound plays **at the note's pitch** (440, 220, 880 and 329.6 Hz within a cent) with linear velocity and slides within 20 cents; ten notes on eight DAC voices; the picker; the project round-trips (16,594 bytes); eight deliberate breakages and fourteen random ones of the saved snapshot are survived (dropped, or played finitely); **cost:** every sampler allocates **0 bytes** while playing its busiest chord and runs at 5x real time or better |
| about | the About window opens by itself the first time only (not for a second instance, not on a machine that has seen it), is modal, shows real text, closes, and reopens from the button |
| rom-editor | switching to ROM loads the game and the picture replaces the message; the picture keeps moving with no audio running; core picks reach the game; Direct brings the message back |
| ui | the editor's tabs through real mouse and key messages on the demonstration models (`Ui/StubModels.cs`): the window is 904 x 664 on the Emulator tab, 1324 x 664 on the others, the host is told once per size change (and the test host's frame follows), going back restores 904; the Emulator tab's 24 controls are at the rectangles captured from the plugin before the tabs existed and a round trip through both tabs leaves its picture identical pixel for pixel; the Instruments tab: the Runaway-off, no-game and no-model messages, the instrument list (18 of 30 rows, wheel, thumb drag, page click), selecting an instrument shows its own fields, the fields scroll, slider (the middle is exactly 64 of 127, drag to both ends), stepper (Shift = ten, enum wrap, no call at the end), bit toggles (8 and 14 bits), locks (Lock all, honoured by Randomise), the waveform (hundreds of wave pixels and the loop markers for a blob, none for an empty one, the other blob of a two-blob instrument), the hex field (click, type FF, lower case, arrows, Esc, wheel), Randomise with strength, Nudge on a chosen or any field and on a locked one, Undo / Redo with the history and seed shown, Send to Presets, Push (done and refused); the Presets tab: the library list (14, 11 visible, wheel), search typing (Backspace, Home, Delete, End, Ctrl+A), clear, console filter, Load, Audition, Export, Import (a test names the file; a cancelled dialog reaches nothing), Rename (Enter, Esc), Delete in two clicks; typing in a text field does not press the game's buttons |
| ui-proof | every check of `ui` is shown able to FAIL: 18 deliberate faults (single tests of a mask in the plugin, set through hook 499, zero in normal use) each make the check that watches that place fail, and the two checks with no switch fail when asked a false question; the failing assertion is printed for each |
| inject | Push into the game (round 6), through the DLL on four real games (Super Mario World, Sonic the Hedgehog, Link's Awakening, Galactic Protector; a game that is not there is reported as not tested): the edited instrument is pushed with the model's own tools, the game's own sound memory and hooks change, the corruption farm is told, Instrument Runaway off and the game plays on differently; a project saved with the push reopens to a game that plays on exactly like the one that stayed open (machine hash), without the `INJ1` section it does not; damaged and newer sections give no pushes, a note on the tab and a game that still comes back; push + Remove at once equals the untouched game; Reset Console takes the pushes with it and says so; the corruption farm and the reopened project's hooks are each shown able to fail; no allocation on the audio thread; the Instruments tab with real mouse messages (Push, Remove, "in the game", the standing note, two injected bugs). The library-level measurements (FFT peaks, amplitude, hashes, 39 checks with their deliberate-bug twins) are `FruityHost inject-test`, see `docs/projects/round6-inject.md` |
| presets | presets (round 6): the preset file and the library with their 17 deliberate bugs (`FruityHost preset-test`: 10 checks, 8,068 assertions), then through the DLL: the six Presets chips appended last, a preset played by FL's notes **bit for bit as Runaway** plays the instrument (100 of 100 instruments, SNES / Game Boy / Master System / Game Gear / Genesis; the NES through a register player equal to a bare chip to 1e-5), the project's own copy (library file gone, 8 kinds of damage, a newer section), 0 bytes allocated, the whole path with real mouse and key messages against the real library (rip, name, Send to Presets, Load, rename, delete, export, import, audition, the Emulator tab's picker), SNES game voices in a preset (bit-identical to the game's own), and 5 more deliberate bugs inside the DLL |
| midi | Test MIDI export (round 7): the quick library checks of `FruityHost midi-test` (the synthetic song, title and loop length, template refusals, facts, the `SNG1` section), then the DLL: the header button opens the window (real mouse), Open MIDI (the dialog replaced by editor hook 621) makes the song the NES game and says what it replaced, Stop is silent, Play and Restart play from the start; **the ROM the plugin builds is byte for byte the library's** and the plugin's own machine plays the converter's streams frame by frame (synthetic, dense, `think`, `level2`: every field from the second decode frame, decode in frame 6 from power-on and in frame 0 after a restart); the four stems carry the channels the song uses and add up to the mix (correlation 0.965); FL's transport restarts within one frame (first sound 16.7 ms after FL's Play) and stops, a repeated start and Follow off do nothing; Reload of an unchanged, a re-exported and a deleted file; the project reopens to the same ROM, facts and machine frame, a damaged and a newer section are refused with the reason, a project without one is as before; not-MIDI, no notes, drums only, cut short and an unknown template are refused with the reason and the song plays on; **0 bytes allocated on the audio thread** with Mix and four stems over 1,500 render calls, two transport restarts and a stop; round 9: **pitch bends** (`bendall`, `bendprebent`: the plugin's machine plays VRUN's streams with bends as each NMI left them, the ROM's pitch follows the MIDI's, the window's facts and numbers say where they play); 9 deliberate bugs (hook 616), each caught by its scenario |
| record | Record the music (round 8): the library checks of `FruityHost record-test` without the real games and their 17 deliberate bugs, then the DLL: the header button and the window with real mouse messages (Record, the game runs, Stop, the transcription, Save MIDI..., **the drag handles make an OLE data object whose CF_HDROP is read back** as a drop target reads it (on a single-threaded-apartment thread, like FL's), a click without a drag says how, the live MIDI out check box and port, Close); **the replay**: the recording's MIDI file read by VRUN's parser and played the way FL would into one BrokenNes2 instance per track on the Presets chip with its track's preset, no game, and every S-DSP register the instances' own sound chips write compared with the game's: synthetic driver 3,380 of 3,485 pitch values held 4 ms or more exact and the other 105 reported by the transcription (lanes: 3,485 of 3,485), Super Mario World recorded by the DLL 209 of 245 exact and 36 reported (2.57 cents at most), volumes within 2, the key-on envelope; the capture (1,231 writes = the game's own log, 0 bytes on the audio thread); the project (`REC1` reopened to the same MIDI bytes, damaged / changed under the old checksum / newer refused, absent says nothing); FL's import-and-Replace (instances made with an empty state, their own fresh state, default parameters pushed, or before the offer each recognise their track from their notes and play its preset from the first note; from the middle of the song, 16 bars later, without a song position; a channel of other notes never taken; Play here by mouse; the project; the channels whose FL events lost the bends and volume or read them at 2 semitones still write the game's registers: 3,296 of 3,296 values on the vibrato track); the host log; FL's transport; live MIDI out against a recording made at the same time (every key-on a note-on, pitch and level reproduced, every released note gets its note-off, `MIDIOut_Delayed` with the port, `FHD_ActivateMIDI`, 0 bytes); refusals; 15 deliberate bugs in the DLL (hook 657), each caught |
| record-nes | Record the music of a NES game (round 8, `record-nes`): the library checks of `FruityHost nesrec-test` on 10 s takes (the chip model against APU_FIX on 11 games and 160,000 random writes, Direct mode's register code, the capture, real games transcribed and replayed value by value, the SMF parsers, REC1 version 2, live MIDI out, VRUN's songs against VRUN's decoder model, the matching of duty tracks) and their 12 deliberate bugs, then the DLL: VRUN through the window by mouse (the MIDI file = the library's), the capture on the game's own tap (the bus's count, 0 bytes on the audio thread), VRUN and Base Wars replayed through the recorded-track presets (every period, volume and duty held 4 ms or more written back exactly in the instances' own chips, duty-gated copies silent), the project (REC1 version 2 reopened to the same MIDI file; damaged and changed-under-the-old-checksum sections refused), FL's import-and-Replace with NES tracks (empty state, fresh state, made before the offer, lost bends and volume: each finds its track and settles on PRE-NES; TRK1 reopened), live MIDI out on the game's tap, refusals (Direct mode, the DMC); 6 more deliberate bugs through hook 657 |
| reload | FL's real conventions (reload, 2026-10-18): **the log replay of the user's FL 2026 session** (the evidence copied outside the repository; NOT RUN when absent): Wario's Woods' six channels created with the very calls FL made, their 192 logged notes at their logged times with FL's own pitch (from MIDI 0), colour and velocity, no song position, no channel name: all six find their track on their FIRST note, read FL's pitch from MIDI 0, settle on PRE-NES with the track's preset, identify 192 of 192 notes as the recorded note FL played (31 after FL looped the pattern) and play their recorded key and expression, and each chip plays the preset's instrument (pulse 1 or 2 with the track's duty, the triangle, the noise); Super Mario World's seven channels with the file's notes played the log's way: 7 of 7 on their first note, 168 of 168 notes, the S-DSP keys the preset's sample; own NES takes (VRUN, Wario's Woods from the roster) and Super Mario World played as FL plays imported channels (from the start, from the middle, a loop, a channel made in FL, FL's expression lost): every value the game held 4 ms or more written back; a channel waiting on the game's emulator is silent; FL's song position read when FL gives one; the allocation meter itself (`meter`, 2026-10-23): while another DLL thread builds NES sound chips, the counter as the windows used to read it moves although nothing was allocated, the windows' meter (`AllocStart`: the measuring thread's allocation context emptied first) never does, and a real 24-byte allocation in a window still counts; 9 deliberate bugs, each caught (the r13 matcher's reading fails the log replay as it failed in FL; the 9th is the bench's own old meter) |
| relink | linking channels to a recording's tracks (relink, 2026-10-18), for a SNES and a NES recording: every instance made while tracks are on offer joins at once (an empty state, NON-default parameter pushes, a state of ours with other values, a host that names the channel after its track: taken before any note, a host that answers the name call with a number: not used) and the Record window's counter is checked after each one ("2 channels joined, 0 found their track; 2 not linked" ... "10 channels joined, 10 found their track"), also as the window draws it; a project state with a preset and a click on a slider make one leave; Link to recording and Link all by real mouse messages (instances made before the offer included); every linked channel then finds its track from its notes and settles, the named one plays its track from its first note; 0 bytes on the audio thread; the host log ran for every instance with the box unticked, into the test's scratch file; 12 deliberate bugs, each caught |
| capture | Capture from FL (round 9): the library checks of `FruityHost capture-test` and their 13 deliberate bugs, then the DLL played by the FL-faithful host (the real log's Dispatcher sequence, notes in FL ticks, the pitch above the channel's root, slides inside their parent, per-note automation, no song position): the window by real mouse messages (arm, the take, the song playing at once, the facts on screen, Save MIDI..., the MIDI-out box); the file read back by an independent Python reader: 39 of 39 notes within 0 ticks at 960 per quarter, the played pitch at 4,652 of 4,652 FL ticks within the bend's 14-bit step (0.166 cents), the period registers the instances' own chips held equal to the file's pitch through Direct mode's arithmetic (2,878 of 2,888, the other 10 at a rounding edge), at 48 kHz with FPE_Tempo, at the user's 44.1 kHz / 130 BPM without it, and on imported channels (root MIDI note 0, found and said); the song in VRUN's engine (the converter's streams = the ideal file's, 5,032 of 5,032 field-frames, the loop at the bar line); FL's Export at another rate; the MIDI out (every note, the pitch within 0.293 cents); the edges (nothing played, cancel, a channel FL never starts, a note held over Stop, back to Direct mode); 0 bytes on the audio thread; 15 deliberate bugs in the DLL (hook 849), each caught |
| robust | 60 create/destroy cycles, three emulators concurrently on three threads, garbage calls, destroy with a voice playing |
| log | `BROKENNES_PLUGIN_LOG=<file>` records the host's calls (for diagnosing a host with no output) |
| hostlog | last of the run: the user's real `Documents\BrokenNes2 host log.txt` is unchanged by the whole run (every run of the test bench logs into a scratch file it names before loading the plugin; the comparison is shown able to fail); the user's box turns the log on and off for new instances when no tracks are on offer |

## Building, installing

```bash
pwsh Plugin/build-dist.ps1                             # Native AOT; needs the MSVC tools. Puts BrokenNes2_x64.dll in Plugin/dist (git-ignored)
dotnet publish Windows/BrokenNes.Windows.csproj -c Release   # the desktop picks Plugin/dist up and ships it as Plugin\BrokenNes2_x64.dll
```
Output `BrokenNes2_x64.dll` (about 7 MB: it carries the built-in game). FL loads it from
`<FL>\Plugins\Fruity\Generators\BrokenNes2\`. That is under Program Files, so copying needs administrator rights: the standard way is the desktop
app's **Install to FL Studio...** (see "Installing, and the standalone synth"), one UAC prompt, refuses while that FL is open.
`UAT/plugin/fl/install-to-fl.cmd` remains as a developer shortcut for FL 2026 only.

If FL crashes or shows nothing, set `BROKENNES_PLUGIN_LOG` to a file path and start FL: the last line is what FL was
asking the plugin when it stopped.
