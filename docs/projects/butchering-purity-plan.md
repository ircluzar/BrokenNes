# Butchering mode: purity ranking + the next effects (plan, 2026-11-04)

Every effect gets a **purity** score: how close it is to something a real, damaged console does on its own (pure) versus
something we have to generate and push in from outside (forced). The score does nothing yet; it is recorded now so a later
feature can use it (a purity filter or slider, "pure only" rolls, presets labelled by purity, a score on a run).

## 1. The scale

Score 0-100, in five tiers. Three questions decide it:

1. **Is it a physical fault?** Something that breaks, wears, shorts or drifts (pure), or a behaviour we compute (forced)?
2. **Where does it happen?** On a pin, trace, socket or external chip (a board-level fault is common), or inside a chip's die (only on a damaged chip: less likely)?
3. **Does it need knowledge?** A fault that hits blindly is pure. One aimed at what the game is doing (learned variables, the history of writes, a value picked to sound bad) is steered.

| Tier | Score | Meaning | Example |
|---|---|---|---|
| **Pure** | 90-100 | A board-level fault that real consoles get: an open trace, a short to a rail, a solder bridge, a dirty or loose contact, a worn button, a failing RAM cell. | A stuck cartridge data line |
| **Plausible** | 70-89 | A real fault, but rarer or needing conditions: a drifting crystal, a noisy interrupt line, a chip's internal line failing, damage that corrosion or heat could cause. | A clock that slows down and speeds up |
| **Mechanism-real** | 40-69 | The hardware really can produce the result, but only through a misbehaving program or a fault it is very unlikely to have: register values changing mid-frame, a sprite table scrambled, a channel muted. | A band of scanlines in another background mode |
| **Steered** | 15-39 | Physically flavoured, but aimed with knowledge of the game: learned variables, a replay of past writes, choices made to be audible or visible. | RAM nudges on confirmed game variables |
| **Forced** | 0-14 | Generated from outside and pushed in: a waveform, a colour transform, a value no fault would produce. | A sine wave injected into the sound |

Flicker (an intermittent fault) does not lower a fault's purity: it is what dirty contacts and cracked joints do. **Bend writes** on a group keeps its score (a bad pin is bad in both directions).

## 2. The current effects, ranked

Score, then why. "Internal" means the line is inside a chip on that console, so it needs a damaged chip, not just a bad board.

### Picture
| Effect | Score | Why |
|---|---|---|
| Console pins (PPU / VDP to VRAM bus), stuck low / high | 95 | External bus to external VRAM chips on NES, SNES, Genesis, SMS: the classic open or shorted trace. (Game Boy: the DMG VRAM is an external chip too.) |
| Console pins, bridged | 92 | A solder bridge or a conductive whisker between neighbours. |
| Cartridge pins (CHR bus, NES) | 97 | The single most common NES fault: dirty or bent cartridge connector pins. |
| CIRAM A10 (mirroring line) | 93 | A real cartridge pin; a bad contact gives exactly this. |
| Video DAC (24 output lines) | 72 | On the NES, SNES and Game Boy the DAC is inside the picture chip (internal); on the Genesis it is internal too. A dead colour bit is a real failure, but of a chip. |
| Palette RAM bus | 70 | Internal to the picture chip on every console here (CGRAM, CRAM, palette RAM are on-die). |

### Sprites
| Effect | Score | Why |
|---|---|---|
| X / Y jitter | 50 | Sprite positions read wrong: a real result of a bad OAM path, but chosen as an offset, not a bit fault. |
| Tile | 52 | As above, on the tile index. |
| Attribute bits (flip, palette, priority) | 62 | A stuck attribute bit is close to a real OAM cell failure. |
| Group lag (8 sprites keep last frame) | 45 | Looks like a missed DMA; a real thing, rarely this tidy. |
| Clone | 35 | One sprite copied over others: aimed. |
| Field swap | 40 | Fields of a sprite entry shifted: mechanism-real, unlikely as a fault. |
| Force bit | 60 | A sprite-table bit held: close to a stuck OAM bit, but chosen. |
| Sprite-0 guard (NES, automatic) | n/a | A safety net, not an effect. |

### Sound
| Effect | Score | Why |
|---|---|---|
| Register bus pins (CPU to sound chip) | 90 | External on the Genesis (YM2612), the SMS FM unit and the SNES (APU ports); internal on the NES and Game Boy (APU on the CPU die): 80 there. |
| Sample fetch pins | 88 | The DMC / sample DMA path. External on the Genesis (Z80 bus), internal on the NES. |
| DAC pins | 75 | Internal DAC on most; external on the SNES (the DSP's serial DAC). |
| Bit flips (register writes) | 65 | Noise on a write: plausible, but random and per write. |
| Detune +-1 (nudge) | 30 | Aimed at the pitch registers. |
| Drop-outs (channel muted) | 45 | A dead channel output is real (a failed mixer resistor); muting on a timer is not. |
| Stutter (replay of an old write) | 25 | Needs the history of writes: steered. |
| Drone (volume held at max) | 12 | A value chosen to be loud: forced. |
| Duty force | 15 | A value chosen. |
| Detune (XOR the period) | 30 | Aimed at period registers, but a bit pattern. |
| Clock wobble (pitch drift) | 78 | A drifting or failing crystal / resonator: real. |

### Bands (scanline effects)
All of these are "the register changed for a few lines": a real result of a misbehaving CPU writing mid-frame, not of a physical fault. **45** each: shift, slip, pattern swap, nametable swap, BG off, sprites off, sprite height, bend, and the per-console extras (Mode 7 wobble, BG mode flip, colour math flip; window off / shift / map swap; sprite zoom, left column blank, sprite shift; shadow / highlight, plane size, window side). The SNES Mode 7 wobble is **35** (an aimed matrix push).

### Palette (display only)
| Effect | Score | Why |
|---|---|---|
| Hue rotation modes, brightness flip | 5 | A colour transform: no console fault does this. |
| Invert | 8 | As above (a failed video inverter stage is conceivable on composite, barely). |
| Channel swap | 20 | Swapped R/G/B: a mis-wired SCART / RGB cable is real; not a console fault. |
| Posterize | 5 | A transform. |
| Cycle (auto mode change) | 3 | A clock-driven show. |

### Unsafe
| Effect | Score | Why |
|---|---|---|
| CPU pins (cartridge / ROM bus) | 97 | Dirty cartridge connectors: the classic. |
| Sound CPU pins (SPC700, Z80) | 88 | External bus on the SNES (ARAM chips) and Genesis (Z80 to its RAM). |
| Work-RAM bus | 93 | External WRAM chips on every console except the Game Boy Color (on-die): 75 there. |
| DMA pins | 70 | The DMA unit's address lines are internal on all of them. |
| Coprocessor pins (SA-1, Super FX, DSP-1) | 90 | On the cartridge board: real. |
| Video-chip register bus | 85 | External between CPU and PPU on the NES and SNES (B-bus), internal on the Game Boy: 70. |
| Ghost NMI / dropped NMI / stuck IRQ | 80 | Noise or a broken trace on an interrupt line: real (the IRQ line is open-collector and shared on the NES). |
| Z80 bus hold / reset hold (Genesis) | 82 | The BUSREQ / RESET lines held: a real board-level fault. |
| Clock stall | 76 | A clock that stops (a cracked crystal, a bad solder joint on it). |
| Clock rush | 55 | A clock running at double rate: a wrong crystal or oscillator, unlikely as a fault. |
| Controller lines (stuck released / pressed / bridged) | 98 | Worn membranes, broken cable wires, stuck buttons: the most common fault of all. |
| RAM rot (bit flips) | 88 | Failing DRAM / SRAM cells and soft errors: real. |
| VRAM rot | 88 | As above. |
| Sound RAM rot | 88 | As above. |
| Echo abuse (SNES) | 20 | Echo pushed to its longest and loudest: a chosen register state (it does reproduce the famous echo-buffer overrun, hence not lower). |
| RAM nudges (learned variables) | 18 | Steered: it learns which bytes are game variables and nudges them within their range. |

### The fault codes themselves
Stuck low **96**, stuck high **95**, bridged **92**; flicker keeps the fault's score.

## 3. What each console has that we do not touch yet

New effects, with their expected score. **Bold** = recommended for the first batch.

### All consoles
- **Floating line** (a new pin fault code, beside low / high / bridged): an open line keeps the last value driven onto it, as NMOS buses do (open bus). **96**. Cheap: every pin group gets it at once.
- **Power sag / brown-out**: a short dip resets the machine with RAM partly kept (bits decay toward 0 or 1 by chip). **92**.
- **Reset line bounce**: short spurious reset pulses. **90**.
- **Heat soak** (a modifier, not an effect): intermittent faults get more frequent the longer the game runs, and calm down after a reload. **85**.
- **Electrostatic hit**: one moment of bit flips across several memories at once. **80**.
- **Bad capacitors on the audio path**: the output filter drifts (muffled sound, then crackle, then cut-outs). **93**.
- **Dirty volume / contrast potentiometer** (portables): crackle and jumps in volume or contrast. **94**.
- **Sine / noise / hum injection into the sound** (the example of a forced effect): **5** (mains hum at 50 / 60 Hz could be **60**: real ground-loop hum).

### NES
- **CIC lockout fault**: the console resets about once a second (the famous blinking screen). **97**.
- **Expansion port / cartridge audio line** (Famicom): a bad cart audio return. **85**.
- **APU length / sweep counter glitch**: **45**.

### SNES
- **The two VRAM chips separately**: one of the two SRAM chips failing (half the bits of every word wrong). **93**.
- **WRAM DRAM refresh failure**: rows of work RAM fade unless the game touches them. **82**.
- **CIC fault**: **97**.
- **APU port latch faults** ($2140-$2143 bridged / stuck): **90** (it is the CPU-to-SPC700 board bus).
- **Cartridge battery / SRAM corruption** (a dying battery): **95** (read-only test: corrupt the save, never write it while the mode is on).

### Game Boy / Game Boy Color
- **LCD column / row driver failure**: dead or stuck vertical lines (the classic DMG fault). **99**.
- **LCD ghosting** (slow pixel response, the DMG blur pushed further): **88**.
- **Link port noise**: garbage serial bytes. **80**.
- **Cartridge seated badly** (the GB is tilt-sensitive): bursts of cart-bus faults. **96**.
- **Speaker / headphone jack crackle**. **94**.

### Master System / Game Gear
- **Card slot vs cartridge slot** (SMS): the card connector faults separately. **95**.
- **Game Gear LCD faults**: dead lines, the well-known capacitor fade (dim, washed-out colours, no sound). **97**.
- **Pause button stuck** (SMS console button): **95**.
- **FM unit (YM2413) bus**: covered by the sound bus pins; **FM unit disconnected** (no FM, only PSG): **85**.

### Genesis
- **VRAM is DRAM**: refresh failure on the VDP's video RAM. **82**.
- **YM2612 DAC crossover / ladder distortion pushed further** (the Model 1 audio ladder): **80**.
- **TMSS / lockout** (Model 1 VA6+): **90**.
- **Cartridge / Z80 bank register bus** ($A06000 latch): **88**.
- **Controller TH line fault** (6-button pads switch on TH): **92**.

## 4. How the scores are kept

- One catalog in the engine (`ButcherCatalog`): per effect an id (the settings path, e.g. `unsafe.cpuPins`), its group, its **purity score**, the console it is scored for when it differs (`soundBus`: 90 Genesis / 80 NES), and a one-line reason.
- `GET /api/butcher/catalog` serves it, so the panel (and later the Lite app) read one source.
- Nothing uses it yet. The panel may show the score as a small number on hover; no filtering until we decide what it drives.
- A golden test: every settings path in `ButcherSettings` has a catalog entry (a new effect cannot be added without a score).

## 5. Order of work

| Phase | What | Notes |
|---|---|---|
| **R0** | Catalog + retroactive scores (section 2), `/api/butcher/catalog`, the "every effect is scored" test | **DONE 2026-11-04**: `corruption/ButcherCatalog.cs` (55 effects + 4 palette sub-options + 3 fault codes; Pure 8, Plausible 17, Mechanism-real 21, Steered 8, Forced 5), the S0 scenario. No behaviour change. |
| **R1** | Pure batch: floating line fault code, LCD column / row failure (GB, GG), CIC lockout resets (NES, SNES), loose cartridge bursts, power sag, reset bounce, bad audio caps | **DONE 2026-11-04**: fault code 4 floating (96), `lcd` (99), `unsafe.cicReset` (97), `unsafe.looseCart` (96), `sound.caps` (93), `unsafe.powerSag` (92), `unsafe.resetBounce` (90). Off and Load State drop a pending reset. Battery: suite 137/0, scenarios 796/0; fuzz found 2 reload mismatches, both cross-console-core cases not needing any R1 effect (open). |
| **R2** | Plausible batch: SNES VRAM chip halves, DRAM refresh rot (SNES WRAM, Genesis VRAM), heat soak modifier, ESD hit, link port noise, controller TH, FM unit unplugged | **DONE 2026-11-04**: `vramChip` (93, SNES only: the Genesis VRAM path is 8 bits), `unsafe.padTh` (92), `unsafe.heatSoak` (85), `sound.fmUnplug` (85, heard only in an FM game: none in the library, mechanism checked), `unsafe.dramRot` (82), `unsafe.esd` (80), `unsafe.linkNoise` (80). Battery: suite 137/0, scenarios 796/0; fuzz: the 2 known NES cross-core seeds plus GB on `GG:GGS` with `unsafe.videoBus` (picture-only after reload, needs no R2 effect; open). |
| **R3** | Forced batch, labelled as such: sine / noise / hum injection, test-tone sweeps, image overlay / channel effects | **DONE 2026-11-04**: `sound.hum` (60), `video.roll` (35), `video.ghost` (30), `video.misconverge` (25), `video.snow` (8), `sound.inject` (5: sine / square / saw / noise, added or ring-modulated), `sound.sweep` (3); display / output only. Battery fixes on the way: a scanline band in progress was put back after a reload (SMS / GG, Genesis, GB: bands end mid-line at a frame boundary), and NES states now save the nametable mirroring (MMC3 only sets it on a write). Battery: suite 137/0, scenarios 796/0; fuzz: 3 reload mismatches, all cross-console or gimmick cores (open). |
| **R4** | Use the scores: decided then (a purity floor slider, "pure only" Randomize, preset badges, a purity score per run) | Needs a decision. |

Open questions for the user before R1: the scores in section 2 (adjust any you disagree with), and whether R1 is the right first batch.

## 6. R4: what the scores drive (options, 2026-11-04)

The catalog has every effect scored (`GET /api/butcher/catalog`, per console). Ways to use it, cheapest first:

| # | Option | What the player sees | Cost |
|---|---|---|---|
| A | **Badges on every effect** | A small tier dot and the number beside each effect name (Pure 96, Forced 5), colour per tier. | Small: the panel reads the catalog once. |
| B | **Live purity meter** | One number for the whole setup: the average score of the effects switched on, weighted by how strongly each is set, with its tier ("This butchering is 88 - Plausible"). | Small. |
| C | **Preset badges** | Built-in and saved presets show their purity in the list; Save names a new preset with its tier. | Small (reuses B). |
| D | **Purity bias in Randomize / Mutate** | A second slider beside Strength, from "pure only" to "anything goes"; rolls pick effects weighted by their score. | Medium. |
| E | **Purity floor** | A "show / allow effects from N up" control: effects below are greyed out and stay off (a "pure hardware only" mode). | Medium: every group switch and roll must respect it. |
| F | **A score per run** | The meter's value is kept with each capture / bug report / Glitch Harvester entry, so a result says how it was made. | Medium; depends on where runs are kept. |

Recommended first: A + B + C (visible, no behaviour change), then D. E and F after, if wanted.
