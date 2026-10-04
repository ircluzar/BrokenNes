# Sega track C: bridges and plugin, milestone C1 (the PSG on both sides of the NES hub, and in the plugin)

Written 2026-10-03 at the end of milestone C1. Branch `worktree-agent-a4916655b2f3afc96`. What was built, what was measured, what was decided, what remains. The plan is `sega-cores-plan.md` (sections 2, 6, 7, 8, 12); the
shared rules are `sega-tracks-brief.md`.

## What exists

| Piece | File | What it is |
|---|---|---|
| `APU_SMS` | `Windows/NesEmulator/mix/SegaSoundBridges.cs` | A NES APU core (CoreRegistry id `SMS`) whose sound is a Master System / Game Gear SN76489. A front NES APU (`MixConfig.NesFrontApu`, FIX) stays for `$4015`, the frame IRQ and the DMC's DMA timing; the NES channels are rebuilt from the register writes (`NesChannelModel`) and replayed on a real `Sn76489` about 960 times a second, like `APU_DMG`. |
| `SmsSoundOnNes` | same file | An `ISmsSound` (the board's sound unit) whose sound comes from a NES APU core, registered as id `NES` / `NES:<chip>` in `SmsSoundFactories`. The real `Sn76489` stays in front for register behaviour; its `DescribeVoices` is replayed on the NES chip through `NesVoiceWriter`. |
| Catalog | `systems/ConsoleSystems.cs` | The Master System and Game Gear sound menus list `NES:<chip>` for every NES sound chip ("the PSG on a NES sound chip (mono; no YM2413 FM, no Game Gear stereo)"); the NES APU menu lists `SMS` under "From the Master System". |
| Plugin | `Plugin/BrokenNes2.Plugin/Bn2Consoles.cs` and friends | Console 3 (`SMS`), chip `SMS` appended as chip index 20; Direct mode only. See `Plugin/README.md`, "The Master System console". |
| Verifier | `Workshop/Sega/SegaBridgeTestCli.cs`, route `--sega-bridge-test` | 126 checks (see below). |
| Plugin test | `Plugin/BrokenNes.FruityHost/SelfTest.cs`, test `psg` | The plugin self-test is now 40 checks. |

## How a NES game's sound becomes PSG sound (`APU_SMS`)

The PSG runs from exactly **twice the NES CPU clock** (3,579,546 Hz against the Z80's 3,579,545), so the maths is exact:

| NES | PSG | Pitch error measured |
|---|---|---|
| pulse 1 / 2, timer t | tone 1 / 2, period t + 1 | 0.009 cent worst over 109 Hz to 3.5 kHz |
| triangle, timer t | tone 3, period 2 (t + 1) | 0.009 cent worst over 109 Hz to 1.7 kHz |
| noise, long mode | white noise (FB bit set) | n/a |
| noise, short mode (93 steps) | periodic noise; the shift rate is chosen so the *buzz* keeps its pitch (NES: shift / 93, PSG: shift / 16) | within 18 cents over rates 5 to 11 |
| noise rate | three fixed PSG rates (6991, 3496, 1748 Hz) match NES rates 9, 11, 13 to about 1%; any other rate is exact through the PSG's fourth rate (it follows tone 3) while the triangle is silent; while the triangle sounds, the nearest fixed rate is used and `NoiseRateCompromises` counts it | |
| DMC | nowhere: samples and `$4011` writes are dropped and counted (`DmcStartsDropped`, `DmcDacWritesDropped`), reported in `Describe()` | |

Levels: each NES channel is replayed at the loudness (AC RMS) the NES mixer gives it, in the PSG's 2 dB steps: a thinner pulse (12.5%, 25%) is played quieter, because the PSG's tone is always a 50% square. Measured against APU_FIX
(110 Hz notes): within 1.3 dB over volumes 3 to 15 and all four duty cycles, the triangle within 0.7 dB. The 15 linear NES steps become 9 PSG levels at the loud end (2 dB steps), covering 22 dB.

**Limits that are the chip's, handled and counted, never silent.**
- The PSG's tone period is 10 bits, so nothing plays below **109 Hz** (A2). A NES pulse reaches 54 Hz, a NES triangle 27 Hz, and real NES music (VRUN's bass is at 34 Hz) goes there. `LowNotePolicy`: `OctaveUp` (default for a NES game: the note plays whole octaves
  up, counted in `LowNotesTransposed`) or `Drop` (silent, counted in `LowNotesDropped`; the plugin's choice). Measured on VRUN: the loudness envelope correlates 0.84 with APU_FIX's when octave-up, 0.54 when dropped.
- Pulse duty is lost (one waveform). Game Gear stereo does not apply here (NES is mono).
- A loaded state rebuilds the channel model from the front core's state probe (FIX has one), so the PSG plays what the state was playing; a pulse's sweep period and envelope divider are not in that description, so a sweep restarts from its first step.
- A game's timing does not depend on the sound chip: VRUN's picture after 900 frames is bit-identical on APU_SMS and APU_FIX (the `IDmcDmaSchedulable` of the front core is forwarded so the bus arms its DMA window the same way).

## How a Sega game's PSG becomes NES sound (`SmsSoundOnNes`)

Tones 1 and 2 on the pulses (50% duty), tone 3 on the triangle, the noise on the noise channel (periodic noise on the NES short mode), through `NesVoiceWriter` (stable slots, one bank is enough for four voices; `NES:FIXS` works and has two). A tone period of 0 or 1 (the
DC trick) is silent, a voice muted on both sides by the Game Gear stereo port is silent. Measured: a pulse tone is exact to 0.07 cent; the NES triangle has half the PSG's period steps, so an odd tone-3 period is up to 14 cents off (the bound is checked per period);
levels within 3.1 dB of the stock unit at every PSG volume (the NES pulse's loudest step is 2.5 dB under the stock unit's, its quietest, volume 1, is 23.5 dB under). What it cannot carry, it counts: the **NES triangle has no volume control**, so tone 3 below full
volume plays at the triangle's one level (`TriangleLevelsLost`); YM2413 FM writes (`FmWritesDropped`; the unit says it has no FM, so a game's detection sees a plain console); Game Gear stereo with a voice on one side only (`StereoWritesLossy`).

## Decisions made here (and why)

1. **Sound-unit ids.** The catalog offers `NES:<chip>` (one entry per NES sound chip, like the Game Boy and SNES menus) rather than a bare `NES`; `SmsSoundFactories.Create` resolves the part after the colon and passes it in the new optional `SmsSoundRequest.Detail`; a bare `NES` means FIX.
   An unknown chip is an error. (One shared contract touched, additively, see below.)
2. **Low notes.** Policy above. The lead's text said "pitch kept"; the range limit was not known then. Both policies keep the pitch *class*; neither fakes a note.
3. **No statics for sessions.** The back-end chip travels in the request, not in `MixConfig`. Two process-wide defaults exist for the plugin only (`APU_SMS.DefaultLowNotes`, `DefaultStepFront`); a session never needs them.
4. **Plugin: console 3, Direct only.** The plugin's console list is tied to its Direct-mode chips and ROM-mode consoles, so the PSG chip got its own console button (`SMS`). ROM mode refuses Master System, Game Gear and Genesis files with a message and holds no game on console 3; nothing is faked.
5. **Plugin speed.** The plugin does not step the front NES core (no CPU reads the chip back): four PSG instances run 21x real time against 7x for the NES chip (floor 5x). With the front stepped it is 5.3x.
6. **A new APU core id needs a family first.** `CoreCatalog.NesApuFamily["SMS"] = "Master System"`; without it the plugin's chip list would insert `SMS` among the NES chips alphabetically and shift every saved chip index.

## What ROM mode needs once the boards exist (plugin)

In `Plugin/README.md` ("The Master System console"): the `*Ready` flags and a real `SmsSession`, `Cx.FromKind` / `RomSupported` / the file filter, a PSG write tap for stems (and the `$06` and `$F0-$F2` ports), the channel model growing past five for FM, a PSG snapshot sampler and a `PSG1` state tag for Runaway.

## Verification (2026-10-03, this branch)

| Check | Result |
|---|---|
| `--sega-bridge-test` | 126 passed (catalog 12, APU_SMS 70, a NES game on APU_SMS 5, SmsSoundOnNes 39) |
| `--sega-contracts-test` | 77 passed |
| `--sega-test` | 77 passed (one Phase 0 check widened: the Master System and Game Gear sound menus now also list NES-chip entries) |
| `--z80test --selftest` | 57 passed |
| `--sega-chips-test` | 34 passed |
| Plugin `selftest` (Native AOT DLL) | 40 passed, 1 warning (QLQ plays an octave sharp on purpose), 0 failed; `psg` test: pitch 0.01 cent worst over 21 notes, 21x real time, 0 bytes allocated |
| Desktop smoke, `desktop-smoke.ps1` | 9 of 9 (audio "unverified": no playback device) |
| `sega-preview-smoke.ps1` | 25 of 25 |
| Desktop, driven over the HTTP API | APU_SMS selectable, 60 fps, six hot swaps FIX/SMS/FIXS, the APU menu lists it under "From the Master System" |
| Lite dev server (port 5016) | builds; the APU picker lists `SMS:SN76489+Fix`; selecting it and loading VRUN raises no console error |
| NES golden suite `UAT/run-suite.ps1` | 46 pass, 0 fail, 16 skip (the known unimplemented mappers) |
| SNES golden (`--snesbench --preset smw --golden`) | video and audio hashes IDENTICAL |
| Game Boy Blargg CPU suite | 11 of 11 |

**Proof that the verifiers can fail** (a bug injected, flagged, reverted): pulse period off by one (9 checks fail), pulse volumes 6 dB quiet (23), short-mode noise played white (2), DMC `$4011` writes not counted (1), the front core not stepped (1), the frame-sequencer jump with a missing half-frame (1),
`SmsSoundOnNes` periodic noise sent as white (1), a loaded state ignored (1), its pitch 2% sharp (13), its stereo mask ignored (1), the catalog's NES entries removed (6); in the plugin self-test, tone 3 muted and 16 bytes allocated per step (the `psg` test fails both).

## Not tested

- **Audio through a real speaker or WASAPI** (no playback device on the dev machine): every audio claim is measured in captured samples (`--sega-bridge-test`, `--apu-audio` WAVs, the plugin renderer). Nobody has listened to APU_SMS or SmsSoundOnNes.
- **Lite as AOT / trimmed**: only the dev server was run. `APU_SMS` is rooted in `WebLite/LinkerConfig.xml`; the trimmed build was not made, and Lite's speed with the PSG was not measured.
- **The plugin inside FL Studio** (only the test host that loads the DLL like FL does); the real FL certification was not re-run.
- `SmsSoundOnNes` has no session to run in yet (the Master System board is Track A's), so it was exercised by register scripts through the factory, not by a game. The Genesis side (`GenesisSoundFactories` id `NES`) was not started.
- The Game Gear's stock unit id is `GG` in the catalog but `SmsSoundFactories` knows only `SMS`: Track A's session must map it.

## Shared files touched (all additive)

`Windows/NesEmulator/sega/SegaSoundFactories.cs` (one factory line, the `Detail` parameter on `SmsSoundRequest`, and `NES:<chip>` resolution in `Create`; `--sega-contracts-test` still 77), `Windows/NesEmulator/systems/ConsoleSystems.cs` (catalog),
`Windows/NesEmulator/mix/ApuBridges.cs` (`NesChannelModel.ClockBulk` and `FrameCycle`, new members; the older bridges are untouched), `WebLite/LinkerConfig.xml` (one root), `Workshop/Program.cs` (one route),
`Workshop/Sega/SegaFoundationTestCli.cs` (one Phase 0 check widened), `sega/THIRD_PARTY_NOTICES.md` (two rows), `Core/NesApuInstrument.cs` (read-only `Apu`), the plugin files, `Plugin/README.md` and `Plugin/CLAUDE.md`.

## What the next milestone needs from Tracks A and B

The next milestone is the FM bridges (YM2413, YM2612), the picture bridges, the CPU walls, ROM-mode Mix with stems and Runaway.
- **Track A:** a session that builds its sound unit with `SmsSoundFactories.Create(<APU menu id>, new SmsSoundRequest(machine, console, variant))` and plays `unit.ReadSamples` at `unit.SampleRate` (the bridge's rate is the NES chip's, 44,100 Hz, not the machine's); map the Game Gear's menu id `GG` to the stock unit; the YM2413 behind `ISmsSound` with `DescribeVoices`
  reporting FM voices (so `SmsSoundOnNes` and `APU_SMS`'s mirror for FM can carry them), and the stock unit's `HasFm` true only when it has the chip; `Consoles.MasterSystemReady` when the board is certified (the plugin's ROM mode follows).
- **Track B:** `IGenesisSound.DescribeVoices`-style hub voices for the YM2612 (six FM channels, the DAC) and a unit factory id `NES` is mine to add once they exist; the Genesis PSG variant's tone-0 behaviour is already in `Sn76489`; the YM2612 write order and timing for an `APU_MD` (NES registers to PSG + YM2612 patches) need the chip's register interface.
- **Both:** the picture bridges need each VDP to expose a per-line capture (as `GbLineCapture` does) and its palette and tile data without touching the board's timing; the CPU walls need the CPU cores behind an interface a NES board can drive (`CPU_Z80`, `CPU_M68K` as bridge ids per decision C).


---

# Milestone C2: the Genesis sound on both sides of the NES hub, the FM patch bank, and the plugin's Genesis chip

Written 2026-10-04 at the end of milestone C2. Branch `worktree-agent-a4ebb922a19fa0542` (it starts from main at `6d4b3c7`, which already holds Phase 0, Track A milestone 1 and Track B milestone 2b). The plan is `sega-cores-plan.md` (sections 4, 6, 8); the
shared rules are `sega-tracks-brief.md`; C1 is above.

## What exists (C2)

| Piece | File | What it is |
|---|---|---|
| `APU_MD` | `Windows/NesEmulator/mix/GenesisSoundBridges.cs` | A NES APU core (CoreRegistry id `MD`) whose sound is a real `Ym2612` and `Sn76489`. A front NES APU stays for `$4015`, the frame IRQ and the DMC's DMA timing; the NES channels are rebuilt from the register writes (`NesChannelModel`) and replayed about 960 times a second: pulses 1 and 2 on FM voices 1 and 2 with the chosen **patch**, the triangle a sine on FM voice 3, the noise on the PSG noise, the DMC dropped and counted. |
| `FmPatchBank` | `mix/FmPatchBank.cs` | Sixteen YM2612 patches as register sets (fifteen FM and a PSG-square entry), written for this project (ledger), the block/fnum solver, the carrier level maths including the discrete chip's ladder effect, and the table of measured levels. |
| `MdSoundOnNes` | `mix/MdSoundOnNes.cs`, registered as Genesis sound unit id `NES` / `NES:<chip>` in `GenesisSoundFactories` | The real `MdSoundUnit` (Z80, YM2612, PSG, the bus handshake the 68000 sees) running untouched inside; its eleven hub voices replayed on a NES chip through `NesVoiceWriter` with a slot allocator. |
| Catalog | `systems/ConsoleSystems.cs` | The Genesis APU menu lists `NES:<chip>` for the 17 chips that make sound (WF and MNES play MIDI, not a chip: left out); the NES APU menu lists `MD` under "From the Genesis"; `NesApuFamily["MD"] = "Genesis"`. |
| Settings | desktop `MainForm.Volume.cs`, `EmulatorConfig.cs`, `/api/audio/emulation-volume`; Lite `Nes.razor` | The FM patch is selectable: a combo box in the desktop's volume panel (saved as `genesisFmPatch`, also over the HTTP API) and a picker next to Lite's APU core when it is `MD`. `APU_MD.SetPatchEverywhere` reaches running cores. |
| Plugin | `Plugin/BrokenNes2.Plugin/*` | Console 4 (`Cx.Genesis`) with chip `MD` appended as chip index 21; Instrument = the FM patch; ROM mode for the Master System and Game Gear. See `Plugin/README.md`. |
| Verifier | `Workshop/Sega/SegaBridgeMdTests.cs` inside `--sega-bridge-test` (`--only mdcatalog\|mdapu\|mdunit\|mdgame\|md`), lab `Workshop/Sega/MdPatchCli.cs` (`--md-patches`) | See below. |

## How a NES game's sound becomes Genesis sound (`APU_MD`)

The clocks are exact: the Genesis master clock is 30 NES CPU clocks, the PSG runs from master / 15 (two per NES cycle) and the YM2612 from master / 7 (30/7 chip clocks per NES cycle, whole-number steps). One second of NES time gives the FM chip exactly 53,267 samples; the
chip's 53.27 kHz output is brought to the host rate by the same band-limited mixer the Genesis sound unit uses.

| NES | Genesis | Measured (captured samples, `--sega-bridge-test`) |
|---|---|---|
| pulse 1 / 2, timer t | FM voice 1 / 2 on the chosen patch; block and fnum by nearest, each carrier at an integer multiple of the note | frequency registers within **0.78 cent** from 54 Hz up (1.59 cent at the triangle's bottom note, 27 Hz); audio **0.71 cent** worst over 48 note-patch pairs (A2, C4, A5); the fundamental is the lowest strong partial for 45 of 45 pairs |
| triangle | FM voice 3, a sine (algorithm 7, one carrier), at the triangle's fixed loudness | 0.96 cent worst, 27 Hz to 3.5 kHz; -0.5 dB from the NES triangle |
| noise | PSG noise, white or periodic; any NES shift rate exact through tone 3 | short-mode buzz within 18 cents of APU_FIX over indices 5 to 11; slow noise within 1.0 dB, fast noise +3.9 dB (APU_FIX's own output low-pass takes about 3 dB out of 7 kHz noise) |
| level | the carriers' total level, 0.75 dB a step, from the NES mixer's own formulas (a thinner duty is quieter) | within **0.6 dB** of the NES mixer's formula at all 15 volumes and four duty cycles (sine, brass and additive-square patches); within 0.9 dB of APU_FIX from volume 3 up (APU_FIX itself is 0.4 dB off the formula); all 16 patches within 0.7 dB of APU_FIX at the loudest step; the 22 dB range of the NES is covered (22.1 dB) |
| DMC | nowhere (the YM2612's DAC could carry it: not built); `$4011` writes and sample starts counted | |
| PSG-square patch | the pulses on PSG tones 1 and 2 (50% duty, 109 Hz and up; notes below follow `LowNotePolicy`, counted) | period = NES timer + 1, 0.000 cent over 109 Hz to 3.5 kHz |

**Two things the measurements forced, both documented in the code.** (1) A modulator at a ratio of 1 or 2 to its carrier changes the carrier's power with its modulation index (a sideband lands on a harmonic or on DC), so a patch's loudness cannot be derived from its total levels: it is **measured** and
kept in `FmPatchBank.Calibration` (steady state of a held C4, 6 s), and the verifier measures every patch again and fails when the chip no longer plays one at the recorded level (worst 0.06 dB now). A feedback operator that is also a carrier (algorithm 7, operator 1) would make level non-linear in its total level: the patches keep
feedback on modulators. (2) The discrete YM2612's ladder effect (its DAC adds 4 to a positive channel value and subtracts 3 from a negative one) makes a quiet note up to 3 dB louder; the loudness maths compensates (`LadderIdeal`), which is what makes volume 1 land within 0.6 dB of the formula.

**The patch bank** (written from FM theory and the register map; no driver's or emulator's patches were read; nobody has listened to them): 0 Brass, 1 Buzz lead, 2 Square (additive 1, 3, 5, 7 at 1/n), 3 Pulse 25% (additive), 4 Organ, 5 Strings, 6 Clarinet (a 2:1 modulator: odd harmonics), 7 Flute, 8 E. piano, 9 Pluck,
10 Bell, 11 Marimba, 12 Bass punch, 13 Bass growl, 14 Sine, 15 PSG square. `--md-patches` prints each patch's measured harmonic series, spectral centroid and level swing over a held note (the additive square's 3rd, 5th and 7th partials measure close to 1/n; the brass and strings
beat by 2 to 4 dB because their carriers are detuned a few cents: chorus); `--md-patches --wav-dir <dir>` writes a short phrase per patch so they can be heard.

## How a Genesis game's sound becomes NES sound (`MdSoundOnNes`)

The inner unit is the stock one, so the game's Z80 driver, the YM2612's registers and timers, the busy flag and the handshake behave as on the stock unit: a real Z80 driver loaded into Z80 RAM by the 68000 side and run inside the bridge makes the same 29 YM2612 and 3 PSG writes as on the stock unit, and a real game
(Sonic the Hedgehog, 1,560 frames) leaves the 68000's work RAM and VRAM bit-identical on the stock unit, `NES:FIX` and `NES:FIXS`, with the same 146,425 YM2612 and 778 PSG writes. Every 1,864 NES cycles the eleven voices are read and allocated:

- **Tonal voices** (FM 1 to 6, PSG tones 1 to 3) go to the NES pulse slots (two per bank) and, when a voice is below the pulse's range (54.6 Hz) or the pulses are full, to a triangle slot, but only a voice at least 9 dB under the triangle's fixed level (a triangle has no volume). `NES:FIX` has 3 tonal slots, `NES:FIXS` 6.
  A held voice keeps its slot; a newcomer takes a free one; with none free the quietest pulse's voice gives way to a newcomer 6 dB louder, otherwise the newcomer is not played. All counted.
- **Noise voices** (the DAC, the PSG noise) go to the noise slots the same way. **The DAC is played as noise hits** (decision made here, as the plan said): its swing sets the level of a noise at a fixed 4 kHz shift rate; its samples are not carried.
- **Level**: every voice is replayed at the loudness the stock unit gives it (an FM voice's from its carriers' total level and envelope), less **4.0 dB** (`LevelScale`, -4.02: the NES pulse's loudest step is 4 dB under a full-scale FM carrier; everything is scaled alike so the balance is kept and the loud end does not clip), in the NES's 15 steps.
- **Low notes** follow `LowNotePolicy` (default OctaveUp for a game), counted.

Measured against the stock unit: FM notes on all six channels at the stock pitch to within the NES timer's own step (1.1 cent at 440 Hz; 12.5 cents at 1.76 kHz and 3.5 kHz where the timer rounds), PSG tones 0.0 cent, levels as near -4.0 dB as the 15 volume steps allow, the whole Sonic soundtrack's loudness envelope correlating **0.905** and its pitch-class chroma **0.63** (`NES:FIXS`; 0.58 on `NES:FIX`) against 0.22 for the same game 2.5 s apart.

**Counters, what is not carried:** FM timbre (every FM note is a plain pulse: `FmNotes`), the DAC's samples (`DacSyncs`, `DacStreams`), one-sided pan (`OneSidedNotes`), the LFO's vibrato and tremolo (`LfoSyncs`; SSG-EG and CSM not counted), voices without a channel (`VoicesWithoutChannel`, `VoicesStolen`), the triangle's fixed level (`TriangleLevelsLost`),
voices below the NES's quietest step (`VoicesBelowNesFloor`), low notes (`LowNotesTransposed`, `LowNotesDropped`). `Describe()` prints them all. Over Sonic's 1,560 frames on `NES:FIXS` they read: 126 FM notes as plain pulses, 9,508 DAC syncs as noise, 3,788 voice-syncs without a channel (2 stolen), 1,816 triangle level losses, 1,667 below the NES floor, 28 one-sided.

## Verification (2026-10-04, this branch, final tree)

| Check | Result |
|---|---|
| `--sega-bridge-test` | **251 passed, 0 failed**: C1's 126 plus C2's 125 (menus and factory 9, APU_MD 66, MdSoundOnNes 38, a NES game on APU_MD and a Genesis game through MdSoundOnNes 12) |
| `--sega-contracts-test` | 80 passed |
| `--md-test` | 143 passed |
| `--ym-oracle-test` | 43 passed |
| `--z80test --selftest` | 57 passed |
| `--sega-test` | 65 passed |
| `--sega-chips-test` | 34 passed |
| `--smstest --roms <Track A's roster>` | 189 passed, 0 failed |
| Plugin `selftest` (Native AOT DLL) | 43 tests: 42 passed, 1 warning (QLQ plays an octave sharp on purpose), 0 failed. `md`: pitch 0.59 cent worst (audio) and 0.61 (register) over 21 notes 65 Hz to 2 kHz, level against the NES chip within +2.1 dB worst (brass at 25% duty; sine 50% +0.3), 8.3x real time with four sliding voices, 0 bytes allocated, ten patch changes under the voices. `rom-sms`: tiny Master System and Game Gear ROMs load, draw at their own size, play their tone through Mix (19.4x real time in the `realtime` test) |
| Desktop smoke, `desktop-smoke.ps1` | 9 of 9 |
| `sega-preview-smoke.ps1` | 38 of 38 |
| Desktop, driven over the HTTP API | `MD` is in the NES APU menu (family Genesis, the honest label); selecting it gives `APU_MD` at 60 fps; eleven hot swaps among FIX, MD, SMS, FIXS and DMG all succeed at 55 to 60 fps; all 16 patches set through `/api/audio/emulation-volume` come back by name while the game runs |
| Lite dev server (port 5019) | builds; the APU picker lists `MD:YM2612+SN76489+Fix`; an FM patch picker with the 16 patches appears next to it; selecting MD and the Sine patch and loading VRUN draws its title screen with no console error; on the Genesis console the APU picker lists the stock unit and 17 `NES:<chip>` entries. Speed in the interpreted dev build is about 1 frame a second (the NES chip's too), AOT not measured |

**Proofs that the verifiers can fail** (a bug injected by text replacement, built, the group run, reverted with `git checkout`; the count is the number of failed checks): *`--sega-bridge-test`*, 25 injections, every one flagged: FM frequency low byte +3 (6), carriers 6 dB too quiet (8), ladder compensation off (2), long-mode noise played periodic (5),
`$4011` writes not counted (1), a new patch never taken up (9), FM clock 29/7 instead of 30/7 (8; the sample count per second is 51,491, not 53,267), a 4-byte allocation per sync (1: 10,848 bytes), the sine's carrier at MUL 0 (9), the brass edited without recalibrating (7), the block/fnum error report +3 cents (10),
the bridge's level scale doubled (6), its pitch 2% sharp (9), one-sided pan not counted (2), a louder voice that can never steal (1), the bus grant always true (1), 68000-side PSG writes dropped (3), the DAC not played (1), a 4-byte allocation per replay (1: 11,296 bytes), the Genesis menu without its NES chips (3 and 1),
FM voices a semitone sharp in the game test (1), the front NES core not stepped (2), the inner unit's FM run at the wrong time (1: Sonic's hashes differ), `SetPatchEverywhere` not reaching a running core (1). *Plugin self-test*, 6 injections, every one flagged: Instrument not reaching the chip (`md`), a Master System game refused in ROM mode (`rom-sms`), a Master System game mapped to the NES console (`rom-sms`),
the Genesis chip inserted first in the chip list (`consoles`, `psg` and `md` fail: the append-only index), a 4-byte allocation per sync seen from the plugin (`md`), the FM block one too small (`md`).

**What the proof pass found in the verifiers themselves** (all fixed, commits `1f2d92f`, `be41602`, `6d538ce`): the `SetPatchEverywhere` check named the wrong patch (index 7 is Flute) and failed on a correct build; the unit level check read its expected -4.0 dB from the very constant it checked, so doubling the constant passed (now written in the test and tied to the constant); the bus-grant check only
looked at a request both units grant (now before, during and after); three groups crashed the whole run when the code under test went silent (each group is now guarded: an exception is a failed check); a 4-byte allocation that nothing keeps was removed by the JIT's stack allocation, so the first allocation injection proved nothing (kept alive in a static now); the front-core injection was aimed at a group that did not hold its check;
the plugin's `RomSupported` only changes a status message, so the first ROM-mode injection did nothing (the injections now sit on the real refusal and mapping); the plugin `cores` test listed the Genesis chip at +1200 cents (zero crossings double a brass timbre): it now uses the sine patch and holds the pitch.

## Decisions made here (and why)

1. **The triangle is always a sine and does not follow the patch**, as the brief said (algorithm 7, one carrier). It is the most useful bass voice and keeps the Instrument parameter meaning "the pulses' timbre".
2. **A PSG-square entry in the bank.** "The PSG for tones where that is the better match" became an explicit patch: the NES pulse's own 50% sound, exact in pitch, no FM. It is the last patch, so a value past the end of the parameter's range lands on it.
3. **Calibrated levels, not derived ones** (above), and a verifier that re-measures them.
4. **Triangle slots only for loud enough voices** (9 dB), so a quiet voice is never played far too loud there; the price is that a quiet sixth voice on `NES:FIXS` can go unplayed (counted).
5. **-4.0 dB for the whole Genesis-on-NES path** instead of letting loud FM voices clip at the NES pulse's loudest step.
6. **WF and MNES are not offered** in the Genesis APU menu (they play a NES game as MIDI; a Genesis game's voices would reach nothing). The verifier checks every chip listed sounds.
7. **No persistence of the patch in Lite** (a reload starts on the first patch); the desktop saves it in config.json.
8. **Master System and Game Gear ROM mode in the plugin** was cheap through `SessionGame` (Mix works for any `IConsoleSession`), so it is done; stems and Runaway for them are not. Genesis ROM mode stays refused.

## Open problems and risks

- **Whole-game loudness is not matched.** Per note the level is within 0.6 to 0.9 dB, but a whole NES game on the Brass patch measures +3.6 dB over APU_FIX (rms 0.089 against 0.059): the brass's harmonics sit above APU_FIX's output low-pass and several voices add. The "same music" checks (envelope 0.855, chroma 0.52 against a ceiling of 0.53 and a control of 0.08) pass; nothing claims the game's overall level.
- **Nobody has heard any of it.** Every claim is measured in captured samples; the patch names say what each was designed to be.
- **Lite speed** with FM is unmeasured (the dev build interprets WASM at about 1 frame a second); the trimmed/AOT build was not made. `APU_MD` is rooted in `WebLite/LinkerConfig.xml`.
- **The Genesis chip's six FM voices are not all used** by `APU_MD` (a NES has four channels): voices 4 to 6 could carry a second patch layer or the DMC.

## Not tested

- Nobody has listened to any of it (see above).
- `MdSoundOnNes` is not reachable from the app's menu on this branch (`Consoles.GenesisReady` is false here; Track B2a flips it on its branch, where the Genesis APU menu will list the `NES:<chip>` entries): it is tested by register scripts, by a real Z80 driver in Z80 RAM, and by a real game (Sonic the Hedgehog, 1,560 frames through `MdSession` with `--apu NES:FIXS`), not through the app. An app-level smoke of the Genesis console on `NES:FIXS` belongs to the merge.
- A hot swap of the Genesis sound unit while a game runs (`MdSession.TrySwapCore` restarts): the NES chip stage can be swapped by the app only on a restart.
- Lite as AOT / trimmed; the plugin inside FL Studio (only the test host that loads the DLL like FL does); the NES golden suite was not re-run (no NES path changed: the catalog, the factories and a new core only).
- Nothing in Track B2a's files (`md/MdVdp.cs`, `MdSession.cs`, `BOARD_MD.cs`) or Track A2's (`sms/`, `sega/Ym2413*`) was edited; no change to them is needed for C2, and none is requested.

## Shared files touched (all additive)

`sega/SegaSoundFactories.cs` (the `NES` ids and `Detailed` lookup in `GenesisSoundFactories`; `--sega-contracts-test` unchanged at 80), `systems/ConsoleSystems.cs` (catalog), `mix/SegaSoundBridges.cs` (three `internal` accessors on `APU_SMS`), `WebLite/LinkerConfig.xml` (one root), `WebLite/Pages/Nes.razor` (the picker),
`Workshop/Program.cs` (one route), `Workshop/Sega/SegaBridgeTestCli.cs` (the call), `Workshop/Sega/SegaFoundationTestCli.cs` (one Phase 0 check widened to the Genesis sound menu), `sega/THIRD_PARTY_NOTICES.md` (one row), desktop `MainForm.Volume.cs` / `EmulatorConfig.cs` / `WebApiServer.Endpoints.Audio.cs`, the plugin files, `Plugin/README.md`, `Plugin/CLAUDE.md`.

## What the next milestone needs

- **The YM2413 bridge (Track A2):** `Ym2413` behind `ISmsSound` with `DescribeVoices`; `SmsSoundOnNes` then needs the same slot allocator `MdSoundOnNes` has (it maps a fixed four voices today), and `APU_SMS`'s mirror for FM is an `APU_MD`-style patch bank (the 15 built-in YM2413 instruments are a bank already).
- **Genesis stems and Runaway in the plugin:** a write tap on `MdSoundUnit.YmPortTap` / `PsgPortTap` (they exist), one-voice chips per FM voice with `Ym2612.MuteMask`, new `Ch` values (FM 1-6, DAC, PSG), a YM2612 snapshot sampler and a `YM1` trailer; the patch picker then lists patches lifted from the running game (the same seven operator registers the bank uses).
- **DMC on the YM2612's DAC** (`APU_MD`): the DMC's delta output is a 7-bit DAC stream, which the Genesis DAC plays natively; the front core would have to expose its DMC output level.
- **Picture bridges and CPU walls:** unchanged from C1 (per-line capture from each VDP; the CPU cores behind an interface a NES board can drive). The Genesis picture exists on Track B2a's branch.
- **A hot-swappable Genesis sound unit** (state carried between units) so the app's menu can change `NES:<chip>` under a running game.
