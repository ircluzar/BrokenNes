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
| Verifier | `Workshop/Sega/SegaBridgeTestCli.cs`, route `--sega-bridge-test` | 125 checks (see below). |
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
| `--sega-bridge-test` | 125 passed (catalog 12, APU_SMS 69, a NES game on APU_SMS 5, SmsSoundOnNes 39) |
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
`SmsSoundOnNes` periodic noise sent as white (1), its pitch 2% sharp (13), its stereo mask ignored (1), the catalog's NES entries removed (6); in the plugin self-test, tone 3 muted and 16 bytes allocated per step (the `psg` test fails both).

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
