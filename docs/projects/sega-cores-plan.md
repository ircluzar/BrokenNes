# Sega Master System, Game Gear and Genesis / Mega Drive: research and plan

Status: **research and planning only, nothing implemented** (written 2026-10-03). Four research passes fed this: how BrokenNes integrated Game Boy and SNES
(read from the code, `git log` and the project memory notes), Master System / Game Gear hardware, Genesis hardware, and reference implementations plus
licensing. Facts are tagged where the sources disagree. The "Verify before relying" list at the end names what was not confirmed.

## 1. What was decided (the interview)

| # | Question | Decision |
|---|---|---|
| 1 | Order | **All three in parallel**, after a shared foundation phase (see 6). |
| 2 | Playbook | **Same as SNES/GB:** spec-first against test ROMs and an oracle emulator, no boot ROM, a ~20-game climb per machine, then bridges and the plugin. |
| 3 | Bridges | Sound both ways, picture both ways, CPU swaps. The generic hub is **the NES register model**: every Sega chip is bridged to and from NES registers/PPU once, instead of N x N pairwise bridges. FM timbre collapsing on that path is accepted. |
| 4 | FL plugin | **"The works"** (Direct mode, ROM mode with stems, Instrument Runaway), with FM handled as a **patch picker first**; a full operator editor is a later wave. |
| 5 | Sourcing | **Hybrid per chip:** port the commodity chips from permissive sources with notices; write both VDPs and all bus timing clean-room from docs and oracle diffs. |
| 6 | Genesis accuracy | **Tier 2.5**, built on a master-clock timeline with VDP access slots in the data structures, so a slot-level (Tier 3) VDP is an upgrade, not a rewrite. |
| 7 | Region | **NTSC ships first, PAL later; every timing table is region-parameterised from day one** (decision A), and PAL runs headless as a certification gate so Overdrive 2 and the PAL-only SMS titles can be proven before any PAL setting reaches the UI. |
| 8 | Extras | In: **SG-1000 / SC-3000 / Mark III**, **Genesis 3-button and 6-button pads**. Out: Light Phaser / paddle / mouse / Menacer / multitaps for now; **Sega CD, 32X and the Power Base Converter are dropped from the plan** (decision E: no design provisions for expansion hardware). |
| 9 | Process | **Foundation first, then three parallel tracks** in separate worktrees/sessions (like the SNES and GB sessions). |
| 10 | Where | **Desktop, Lite (WASM/AOT), FL plugin, and Workshop/headless first.** |
| 11 | Genesis model (decision B) | **Selectable per game from the start:** Model 1 / Model 2 / Model 3 is a first-class setting (menus and plugin) with a per-game default; it carries YM2612 vs YM3438 behaviour (ladder effect, status/busy quirks), TAS write-back, VSRAM size and the other model differences. |
| 12 | Ids (decision C) | **Chip names in the CPU slot, console-style in PPU and APU:** CPU `Z80` and `M68K`; PPU `SMS` / `GG` / `MD` (the VDPs); APU `SMS` / `MD` (the sound units). **The joke `CPU_Z80` is retired** once the real Z80 exists, which frees the id `Z80` (section 2.4a lists the work). |
| 13 | Provenance housekeeping (decision D) | Root `THIRD_PARTY_NOTICES.md`, a `sega/THIRD_PARTY_NOTICES.md` ledger and a QuickNES entry are foundation tasks; the LGPL `DSP_SFC` stays as a **documented exception** for now (no SNES code change). |
| 14 | Tracking (decision F) | **One live dashboard for all three tracks** (phases and exit gates, suite pass counts, the 20-game rosters with per-game checks, a gallery), kept current by whichever session is working. |
| 15 | Oracle and test downloads | **You approve the Phase 0 download list once** (each file with source and size); the files then go into a tools folder outside the repo, and GPL/LGPL/non-commercial material stays external and oracle-only. |

Standing project rules that apply (from memory and the code): no firmware or boot ROM is ever required (start from the documented post-boot state); new
"variant" chips are forks, existing chips are never edited; no silent fallbacks (a cell only looks alive if the chosen core really does the work, an
honest "dead" beats a disguised rescue); ROMs and unlicensed test material are never committed; the FIX family is the NES accuracy target and the other
NES cores are frozen; a per-family `THIRD_PARTY_NOTICES.md` ledger records everything borrowed.

## 2. What the codebase gives us (and what it does not)

### 2.1 Shape of a console

- A console is a **sealed package**: board + CPU + PPU + APU + cartridge, in its own folder and namespace (`gb/`, `snes/`), with **no shared base class**.
  Console-native cores deliberately do **not** implement `ICPU` / `IPPU` / `IAPU`, so `CoreRegistry` (which scans for `CPU_` / `PPU_` / `APU_` types that
  implement them) never lists them as NES cores. Only the bridge cores that stand in for NES chips implement them.
- Console identity and plumbing live in `Windows/NesEmulator/systems/`: `ConsoleKind` (Nes, Snes, GameBoy, GameBoyColor), `PadButtons` (12 of 16 bits),
  `RomDetect`, `CoreCatalog` (per console and slot, own family first, plus three bridge tables), `IConsoleSession` and the factory
  `ConsoleSessions.Create`. A session exposes `SetPad`, `RunFrame`, `Frame` (ARGB), `ReadSamples` (stereo shorts), battery export/import and
  `TrySwapCore` (hot swap). It has **no savestates**.
- Two board styles to copy from: **GB** is M-cycle based (every `Read/Write/Idle` is one M-cycle and ticks timer, DMA, PPU and APU first); **SNES** is
  master-clock based (21.477 MHz, the board advances its clock inside each access). The SNES audio unit is its own computer inside the APU slot
  (`ISnesApu`: four ports plus `RunTo(masterClock)`), which is the right template for the Genesis Z80 + YM2612 + PSG sound subsystem.
- Hot swap: PPU choices are "views" of the running chip (free to swap), the GB APU carries state through `SaveState/LoadState`, parked CPUs resume.

### 2.2 The bridges today

`Windows/NesEmulator/mix/` holds the bridges (namespace `NesEmulator.Mix`). They are **pairwise and hand-written**, and the **NES register model is the de facto
hub**: all sound crosses it (GB sound on a SNES chip goes GB regs, NES regs, S-DSP), pictures go through `NesPictureSink` (a real NES running an idle ROM,
programmed through `$2000-$2007`) or the layered `PPU_FIXS` / `ExtPicture` compositor. The shapes to reuse:

| Need | Existing pattern |
|---|---|
| Foreign chip plays a NES game's audio | `APU_SNES`, `APU_DMG` (NES registers in, other chip's registers out) |
| NES chip plays a foreign game's audio | `NesApuOnSnes`, `GbApuOnNes` (the real chip stays in front, a probe exposes its registers, loudest voices map to pulse/triangle/noise) |
| More than four voices | the 8-channel extension bank (`APU_FIXS`, `APU_GBS`, `IApuExtChannels.WriteExtRegister`), joined to the mix only once written so native games stay bit-identical |
| Foreign picture on a NES chip | `GbToNes`, `SnesToNes.RenderLayered` (per-line capture, windows, palettes via a quantizer) |
| NES picture on a foreign chip | `PPU_DMG`, `PPU_SNES` (a NES PPU stays in front for timing, NMI and sprite-0; once per frame the bridge translates VRAM) |
| CPU of another family | `CPU_SM83` on a NES, `Cpu65816OnGb`: instruction-set walls, honest dead cells; a Sega game on a 6502 cannot exist |

Known traps from the earlier mix work that the Sega bridges will meet again: NES `RequestIRQ` is a latch but Z80/68000 interrupts are levels; instant
register writes lose data on `PPU_FIX` (use the `IPpuProbe` backdoor or advance time); bridge PPUs need the board's precise timing switched on
(`IPpuFixTiming`); `Bus.apu` is not the live core after a switch (use `bus.ActiveAPU`); static `MixConfig` fields do not scale to several bridged sessions.

### 2.3 The plugin is NES-shaped

Five fixed channels (Pulse 1, Pulse 2, Triangle, Noise, Mix), 12 append-only parameters (projects store indices), and Direct mode drives NES registers
`$4000-$4017` through any registered NES APU core. Game Boy and SNES chips appear in it **only because** NES-register bridge APUs (`APU_DMG`, `APU_SNES`)
exist. ROM mode works for any `IConsoleSession` (`SessionGame`, stereo mixed to mono), but **per-channel stems exist for NES games only**, and Instrument
Runaway has samplers only for SNES (BRR samples) and Game Boy (a private `APU_GB` per instance). The audio thread must allocate 0 bytes, and a self-test
holds the plugin to 5x real time with four voices.

### 2.4 Things that do not exist, and things that bite

- **No Z80, PSG, FM, VDP or 68000 anywhere.** `CPU_Z80` is a **6502 wrapped in a costume** (a joke core with randomised "chaos", frozen on purpose and
  hard-coded into the Deck Builder data). **Decision C retires it** once the real Z80 exists; until then the real Z80 is a differently named class (section 6),
  and the id `Z80` is freed for the real one afterwards (retirement work in 2.4a).
- **No PAL anywhere** in the engine. `PadButtons` has 4 spare bits. Display sizes are special-cased per console.
- Three **explicit directory lists** (Lite csproj, Workshop csproj, `Core/BrokenNes.Core.csproj`) plus `WebLite/LinkerConfig.xml` rooting must name every new
  folder and discoverable type, or Lite and the plugin silently miss them. The desktop csproj globs. `Web/` deliberately excludes GB/SNES/mix.
- `SessionSavePath` / `SessionSaveKey` send any new console into the Game Boy "else" branch unless edited. ROM-detect extensions are listed in about six places.
- Provenance gaps: no root `THIRD_PARTY_NOTICES.md` although the plugin's About footer points at one; `APU_QN.cs` is a QuickNES port with no ledger entry;
  `DSP_SFC.cs` is an LGPL port (an exception to the permissive-only policy below).

### 2.4a Retiring the joke `CPU_Z80` (decision C): measured scope

A repo-wide search finds it in only about 13 files, so this is a small, bounded foundation task (done by the Z80 work, after the real core exists):
- `Windows/NesEmulator/cpus/CPU_Z80.cs` (15 KB): delete; the CPU menus then list 7 NES CPUs (EIL, FIX, FMC, LOW, LW2, SPD, ULQ) plus the bridge cores.
- Deck Builder (Legacy, BrokenNes 1): `Windows/Webmodules/shared/gameSave.js` (`ownedCpuIds` includes `'Z80'`), `Windows/Webmodules/Continue/continue.js` (the CPU card list) and the
  card art `CPU_Z80` in `Windows/ImageTools/SvgFactory.cs`. Existing saved games that own `'Z80'` need a load-time migration (drop or remap the id) so no campaign save breaks.
- Saved user config: any `config.json` (or `consoleCores`) that selected CPU `Z80` must resolve to the default instead of throwing; `CoreCatalog.Resolve(stored)` already falls back
  to the default, so this is a test, not new code.
- `WebLite/LinkerConfig.xml` (a comment and the not-rooted note), `Workshop/AccuracyCoinTests.cs` and `AccuracyCoinCli.cs` (comments about its failure on frame 1 and the shared `Random`
  pre-warm), `Workshop/README.md`, and docs/UAT findings that list "8 CPUs" (historical notes can stay; live checks that count cores need updating).
- Memory/rules: the "gimmick cores are intentional" rule no longer lists `CPU_Z80`.

### 2.4b History as a pace reference

SNES: verifier suite to a playing Super Mario World with sound in one evening (2026-09-25), coprocessors and Mesen timing the next day. GB: core family, pixel-FIFO
PPU and MBCs in about a day. Bridges plus the console layer: about two more days. So the **calendar unit for this project is days of agent work per track,
not person-months**. The long tail is accuracy (Mesen diffs, test-ROM triage), not first light.

## 3. Master System, Game Gear (and SG-1000 / SC-3000 / Mark III)

Tags: **[D]** documented, **[M]** measured by emulators, **[F]** folklore or inferred.

**CPU.** Z80 at 3.579545 MHz NTSC (3.546895 PAL), 228 CPU cycles per line, 262 lines NTSC (about 59.92 Hz) [D]. No ROM wait states. Needs the undocumented
set: SLL, IXh/IXl forms, DDCB/FDCB register copies, ED duplicates, X/Y flag bits (3 and 5), MEMPTR/WZ (for `BIT n,(HL)`), the R register, `LD A,I/R`
IFF2 behaviour, EI delay, HALT. IM 1 (vector `$0038`) is what games use; the INT line is **level-triggered** and stays asserted until the VDP status
read clears the flag. NMI is the Pause button (edge, `$0066`). I/O decodes only A7, A6 and A0.

**VDP** (315-5124 SMS1, 315-5246 SMS2, 315-5378 Game Gear). 16 KB VRAM, CRAM 32 bytes (SMS 6-bit) or 64 bytes (GG 12-bit with an even/odd write latch),
registers 0-10, Mode 4 plus the TMS9918 modes 0-3 (which give SG-1000 for free; SMS1 lets legacy register bits mask VRAM addresses, needed by Ys). 64 sprites,
8 per line, 8x8 or 8x16, Y+1, `$D0` terminator in 192-line mode only, overflow and collision status bits, the SMS1 zoom bug. SMS2/GG add 224/240-line
modes. The control-port latch, buffered reads, and the read-to-clear interrupt flags are the classic traps. Vertical scroll latches at end of active display;
the line counter reloads from register 10 outside the active area. Exact sub-line positions of the frame IRQ, line IRQ and flag-setting differ between
cores and are only quantified by FluBBa's VDP test.
**Game Gear:** the VDP renders 256x192 and the LCD shows a **160x144** window (about 48 px cropped left and right, 24 lines top and bottom); ports `$00`
(Start, region, PAL flag), `$06` (PSG stereo), CRAM latch; sprites outside the window still count toward the 8 per line.

**Sound.** SN76489: 3 tone + noise, divider /16, tone period 0 behaves as 1 (the DC trick behind PCM playback), Sega's 16-bit LFSR (taps 0 and 3, reset on a
noise-register write), 2 dB steps, band-limited synthesis is the norm. **YM2413 (OPLL)** exists only on the Japanese Mark III / SMS (ports `$F0-$F2`,
nine 2-operator channels or six plus five rhythm, 15 ROM instruments plus one user patch, 49.716 kHz); the audio-control port `$F2` is also how games detect it.

**Cartridge.** 8 KB RAM mirrored; Sega mapper (registers `$FFFC-$FFFF`, 16 KB slots, battery RAM in slot 2), Codemasters, Korean variants (MSX-style, Nemesis,
Janggun), 93C46 EEPROM for some GG titles. Header `TMR SEGA` at `$7FF0` (also `$1FF0`/`$3FF0`), region nibble and size nibble at `$7FFF`, checksum
`$7FFA`. **BIOS is optional everywhere**; BIOS-less post-boot state is documented in emulator sources (VDP registers 0-6 = `$36,$80,$FF,$FF,$FF,$FF,$FF`,
memory-control `$AB` on SMS / `$A8` on GG, RAM pattern matters for two Mark III games, California Games II and Speed Ball need the BIOS-set VDP registers).
**Input:** `$DC/$DD` active-low, 2 buttons, Reset on `$DD` bit 4, TH-based region detection, GG Start via `$00`.

**Accuracy needed.** A scanline VDP with an instruction-level CPU is documented as 100% compatible with released SMS/GG software (Genesis Plus GX); Mesen2
syncs at every access; ares steps per pixel. About 20 titles need **PAL timing** (Sonic 2, Shadow of the Beast, Sensible Soccer, The Addams Family...), a
few need SMS1-only or SMS2-only VDP features, two need the Japanese I/O chip. Games that time writes to the VDP access window (about 29 T-states apart
during active display, 16 in vblank) exist; the exact rule is only partly documented.

**Test material.** Z80: SingleStepTests/z80 (MIT; generated from ares' core, so also run **z80test**, MIT, hardware-verified on real Zilog and NEC parts),
zexdoc/zexall (GPL, local only). VDP: FluBBa's SMS VDP Test (H-count of the VINT flag, line IRQ, overflow and collision flags), sverx's SMSTestSuite,
Gearsystem's vdptest 1-3. PSG/FM: JoppyFurr's SN76489 and YM2413 test ROMs (2024). SDSC Debug Console for headless text output.
**Oracles:** Mesen2 (SMS/GG since 2.1.0; trace logger, Lua, `--testRunner`; you already use it), Emulicious (excellent debugger, closed), Gearsystem, MAME.

**Effort (experienced human, research estimate):** Z80 5-8 d, VDP 8-14, PSG 2-3, mappers/detect/saves 3-4, YM2413 1 (port) to 7-10 (scratch), GG extras 1-2,
input 1, differential testing 3-5: **about 30-45 d total, 15-20 d for "plays everything, no peripherals"**. SG-1000/SC-3000/Mark III add 2-3 d.

## 4. Genesis / Mega Drive

**Clocks.** Master 53.693175 MHz NTSC (53.203424 PAL). 68000 = master/7 (7.67 MHz), Z80 and PSG = master/15, YM2612 input master/7 then /6 /24:
**one sample per 144 chip clocks = 1008 master clocks (53,267 Hz)**. Every line is **3420 master clocks** (488 68K cycles = 228 Z80 cycles) in both H32 and H40.
Frame: NTSC 262 lines (59.92 Hz), PAL 313 (49.70 Hz).

**Buses.** 68K map: ROM `$000000`, Z80 area `$A00000`, I/O chip `$A10000`, BUSREQ `$A11100`, RESET `$A11200`, mapper `$A130xx`, TMSS `$A14000`, VDP
`$C00000`, 64 KB RAM `$E00000`. Z80 map: 8 KB RAM, YM2612 `$4000`, 9-bit bank register `$6000` (serial, one bit per write), PSG `$7F11`, 32 KB window `$8000`
into 68K space. The Z80 INT is the VDP VINT only, asserted for one scanline (228 Z80 cycles). Z80 bank-window accesses stall the Z80 by about **3.3 Z80
cycles** and the 68K by about **9.7-11 cycles** (sources disagree; the newer test-ROM measurement says 9.7). 68K refresh costs about 2 of every 128 cycles.
During VDP DMA the 68K is halted (fill and copy do not halt it). Interrupts: VINT level 6, HINT level 4, EXINT level 2, autovector, VINT beats HINT; the VDP
asserts IPL 90-210 ns after the relevant write and the 68K samples it at instruction end (one-instruction latency; Sesame Street Counting Cafe needs it).
**TMSS** is inside the I/O chip (absent on early Model 1); without a boot ROM emulators leave the VDP unlocked, so nothing is needed.

**68000.** Cycle accuracy that matters is the **time of each bus access** (VDP, FIFO, DMA, Z80 bus and refresh interact with it), not just per-instruction totals.
No TAS write-back on most consoles, MOVEM extra read, CLR read-modify-write, address errors (rarely hit; 99% of the SingleStepTests start word-aligned).
Reference for timing: Yacht (Cwik). **Test vectors:** SingleStepTests 680x0 (about a million tests, transactions with cycle counts, assume immediate DTACK, TAS/TRAPV
dubious, ADD.l/SUB.l-to-An and DIVS/DIVU cycle errors known) and SingleStepTests/m68000 (generated from MAME's microcoded core, includes address-error cycles).
**No permissive, bus-cycle-accurate C# 68000 exists.** Candidates: **ares m68000 (ISC)**, instruction-stepped with timing callbacks (best fit for timestamped
accesses); Musashi (MIT), instruction-level only; Moira (MIT), exact but C++20 templates and hard to port; MAME's microcoded core (BSD-3), hard to port.

**VDP (YM7101 / 315-5313).** 64 KB VRAM, CRAM 64 x 9-bit, VSRAM 40 words (64 on late Model 2), registers `$00-$17`, command/data protocol with a pending flag,
4-word write FIFO (VRAM write = 2 slots per word, CRAM/VSRAM 1; address increments immediately; the CPU stalls when full), planes A/B/window (32/64/128
cells; window replaces A; the window-distortion bug), scroll full/8-line/per-line horizontally and full/per-2-cell vertically, priority ladder, shadow/highlight,
80 sprites per frame / 20 per line / 320 px (H40; 64/16/256 in H32) with a write-through attribute cache and the X=0 masking rule, H32/H40, V28/V30,
interlace modes 1 and 2, DMA (68K to VDP, fill, copy; official bytes per line: 68K to VRAM 16/167 H32 and 18/205 H40, active/blanked), H/V counters (only the
top 8 bits of H are readable; V jumps; H32 `$00-$93,$E9-$FF`, H40 `$00-$B6,$E4-$FF`), VINT delayed about 788 mclk (H40) / 770 (H32) after the VBlank flag, HINT
counter reload per line. Status bits: FIFO empty/full, VINT happened, sprite overflow, collision, odd frame, VBlank, HBlank, DMA busy, PAL. **Slot structure:**
210 slots per H40 line, 171 per H32, 32 bits each, with refresh slots and a table of irregular HSYNC slot lengths (BlastEm steps per slot and tunes constants
against test ROMs).
Games needing precise timing: Mickey Mania (sprite masking, display disable), Double Clutch and Chaos Engine (FIFO timing), Road Rash (HBlank colour changes),
Sesame Street Counting Cafe and Legend of Galahad (interrupt timing), Top Gear 2 / Skitchin / Toy Story (scroll latches), Clue / Microcosm / Pac-Man 2
(refresh), Direct Color DMA and the Titan Overdrive demos. **Overdrive 2 is PAL/50 Hz only** and uses an undocumented VDP register.

**Sound.** YM2612: 6 channels x 4 operators, 8 algorithms, LFO, SSG-EG, channel-3 special and CSM modes, DAC on channel 6 (`$2A`/`$2B`, 8-bit with the LSB from `$2C`),
timers A/B, busy flag about 192 chip clocks. The discrete YM2612 has the **ladder effect** (crossover discontinuity; After Burner II depends on it); YM3438 and
ASIC-integrated cores do not. Games expose model differences through the busy flag (Hellfire, Earthworm Jim). The PSG is the same Sega variant as the SMS
(16-bit LFSR), mono. DAC playback pitch depends on exact Z80 timing (bank-window stalls change it), so the Z80 must be timed in master clocks. Per-channel
stems are cheap with ymfm's channel masks: up to **11 voices** (6 FM, or 5 + DAC, 3 tones, noise). Drivers: SMPS, GEMS, Sound Images, MUCOM-MD, Echo/XGM.

**Cartridge and input.** Header `SEGA` at `$100`; checksum never verified by hardware; SRAM header at `$1B0` (`RA`), odd bytes at `$200001`; SSF2 mapper
(seven registers at `$A130F3-FF`, 512 KB windows); 13 EEPROM types (I2C pin wiring varies by publisher); Pier Solar has a private mapper. Formats: `.md`,
`.gen`, `.bin` (ambiguous), `.smd` (interleaved, needs de-interleave), zips. **3-button pad:** TH=1 reads `?1CBRLDU`, TH=0 reads `?0SA00DU`; **6-button:** a 7-step
TH sequence with a timeout of about 8192 68K cycles (Mortal Kombat 3 and Decap Attack depend on it).

**Accuracy tiers** (what the reference emulators do): Tier 2 per-scanline (Genesis Plus GX; about 99% with hacks); **Tier 2.5** instruction-level CPUs with
timestamped bus accesses, refresh, Z80/bus stalls and a timed VDP FIFO/DMA (jgenesis; runs Overdrive 1 and 2); Tier 3 slot-level VDP and bus-cycle CPUs (BlastEm,
Exodus; passes Nemesis's FIFO test, Direct Color DMA, CRAM dots); Nuked-MD is gate-level (300-400x slower than hardware). One user-run comparison (jgenesis issue #669, June 2026; the origin of its test ROMs is not stated, so
treat it as an indication, not a benchmark) scored `misc_test` / `misc_test 2` as: Nuked-MD 10/25, BlastEm 8/22, Genesis Plus GX 7/16, ares 6/20, Exodus 5/6, jgenesis 3/12.

**Effort (experienced human, research estimate):** 68K 8-12 d, Z80 4-6, VDP ports/FIFO/DMA/counters/IRQ 8-10, VDP renderer 8-12, YM2612 5-8 (port) or 12-18 (scratch),
PSG 1-2, bus arbitration/refresh/stalls 3-5, mappers/SRAM/EEPROM 3-4, pads 2-3, integration and test triage 8-12: **about 50-70 d for Tier 2.5; 85-120 d for Tier 3.**
(Sega CD roughly +25-40, 32X roughly +30-45, out of scope.)

**Test material.** Nemesis (VDP port access, sprite masking, CRAM flicker, FIFO), MacDonald (V counter, 68000 illegal opcodes and memory, window distortion), flamewing
BCD verifier, r57shell opcode sizes, Chilly Willy's Direct Color DMA, Fonzie's window test, Titan Overdrive 1 and 2; BlastEm's own tests are GPL (oracle use only).
**Oracles:** BlastEm (realtime, debugger, GDB stub), Nuked-MD (offline), Genesis Plus GX, jgenesis, Exodus. No redistributable hardware-recorded audio/video
set exists: golden audio would be generated offline from VGM register logs through Nuked-OPN2 / Nuked-OPLL.

## 5. Sourcing and provenance (hybrid per chip)

The repo license is the bespoke **"Digital Lifeform License 1.1"** (grants rights to digital life forms, forbids modification without LLM assistance, requires
provenance records, requires the license in all copies). It cannot carry GPL-3 (no further restrictions), LGPL (modification terms) or non-commercial code
(it grants a right to sell). **Policy: MIT, BSD, ISC and zlib may be ported with notices; GPL, AGPL, LGPL and non-commercial sources are oracle-only.**

| Chip / part | Plan | Source and obligation | Verification |
|---|---|---|---|
| **Z80** | Port | ares `z80` (ISC) or BizHawk `Z80A` (MIT, local tree `Bizhawk52X-Vanguard`); do **not** copy the old Buchmueller V3.9 copies (Genesis Plus GX, PicoDrive, SMS Plus: "non-commercial"); MAME's current `z80.h` is BSD-3 | SingleStepTests/z80, z80test (local), zexdoc/zexall (local) |
| **SN76489** | Port | MAME `sn76496.cpp` (BSD-3, documents the SMS/Sega variants) or BizHawk `SN76489sms.cs` (MIT, 258 lines) | PSG test ROMs, Mesen2 audio diffs |
| **YM2413** | Port | emu2413 (MIT; a C# port of v1.5.9 exists inside `sms-debug-mcp`, MIT) or ymfm OPLL (BSD-3). **Not BizHawk's `YM2413.cs`** (its header admits "uncertain licensing terms") | YM2413 TestRom, Nuked-OPLL as offline oracle |
| **YM2612** | Port | **ymfm** (BSD-3, `ym2612` and `ym3438` classes, channel masks for stems; C++ so a C# port is needed). Nuked-OPN2 (LGPL) stays an offline oracle | Nuked-OPN2 goldens via VGM, DevSter FM test |
| **68000** | Port | **ares `m68000` (ISC)**, primary candidate (timing callbacks); Musashi (MIT) fallback. A one-day spike decides | SingleStepTests 680x0 and m68000 (skip TAS/TRAPV, known-bad cases per the jgenesis runner notes) |
| **SMS/GG VDP** | **Clean-room** | from MacDonald's VDP document, SMS Power docs, FluBBa's test; BizHawk's MIT VDP and MAME's BSD-3 `315_5124.cpp` may be *read* for structure only if the ledger records it | FluBBa VDP test, vdptest, Mesen2 trace diffs |
| **Genesis VDP + bus timing** | **Clean-room** | Sega's overview, Kabuto's notes, MacDonald's `gen-hw.txt`, Plutiedev, SpritesMind threads; Exodus (MIT) may be read for quirks; MAME's `315_5313.cpp` disclaims correct timing; BlastEm / jgenesis / Genesis Plus GX are GPL or non-commercial: oracle only | Nemesis test ROMs, MacDonald ROMs, Overdrive 1 (and 2 once PAL), BlastEm diffs |
| **Mappers, header, saves** | Port / write | BizHawk's MIT SMS memory-map code (as was done for Mapper 30); Genesis mappers from Plutiedev | per-game climb |
| **Resampler** | Existing | the repo's own `StreamResampler`; avoid Blip_Buffer (LGPL) | |

Ledger work in the foundation phase: a root `THIRD_PARTY_NOTICES.md` (the plugin footer already points at it), a `sega/THIRD_PARTY_NOTICES.md` graded like the SFC one
(taken from source / written from memory / hardware facts / deliberately not taken), an entry for the unlisted QuickNES port, and a note that `DSP_SFC` (LGPL) is an
existing exception. BSD-3 notices must travel with every distributed binary: the plugin DLL, the desktop zip and the Lite WASM site.

## 6. Target architecture

**Identity.** `ConsoleKind` gains `MasterSystem` (SMS, plus SG-1000 / SC-3000 / Mark III as models), `GameGear` (separate kind, like GBC vs GB) and `Genesis` (display
"Genesis / Mega Drive"). Families `SMS` and `MD`. Folders `Windows/NesEmulator/sega/` (shared chips), `sms/`, `md/`; namespace `NesEmulator.Sega`. Console-native
cores follow the GB/SNES rule (not `ICPU`/`IPPU`/`IAPU`). Shared chip classes are named by chip (`Z80`, `Sn76489`, `Ym2612`, `Ym2413`, `M68k`) with no `CPU_`/`APU_`
prefix, so `CoreRegistry` ignores them (and nothing collides with the joke `CPU_Z80` while it still exists). NES-facing bridge ids follow decision C: CPU slot by chip name
(`Z80`, `M68K`), PPU and APU slots console-style (`SMS`, `GG`, `MD`). The Genesis model (Model 1 / 2 / 3) is a first-class per-game setting on `BOARD_MD`, not a compile-time choice.

**Boards.** `BOARD_SMS` (Z80 bus with the I/O port decode, VDP, PSG, mappers; GG and SG-1000 as models) and `BOARD_MD` on a **master-clock timeline**: 68000, Z80,
VDP and YM dividers derived from one clock with a region parameter, a bus arbiter (BUSREQ/RESET handshake, bank window, refresh, DMA stalls), VDP access slots kept as data
(the Tier 3 upgrade path). The Genesis sound subsystem (Z80 + YM2612 + PSG) is its own unit in the **APU slot**, like `ISnesApu`, but its contract must carry the
bus handshake (a plain four-port interface is not enough). Factory constructors with optional CPU/PPU/APU factories from day one (`BOARD_SFC` retrofitted this at
`25b8c3b`; do not retrofit twice).

**Sessions.** `SmsSession`, `GgSession`, `MdSession` implement `IConsoleSession`; saves under `SmsSaves\`, `GgSaves\`, `MdSaves\` (SRAM/EEPROM); `RomDetect` learns `TMR SEGA`,
the GG region nibble, `SEGA` at `$100`, SMD de-interleave and the ambiguous `.bin`; `PadButtons` takes C, Z, Mode (and a Pause mapping) in its 4 spare bits;
display sizes: SMS 256x192, GG 160x144 (window of the 256x192 render), Genesis 256/320 x 224 (240 PAL).

**Bridges (NES register hub).** Each new console talks to the NES model **once**; Sega-to-GB/SNES paths cross the NES model like GB-to-SNES sound does today. Existing
bridges stay as checkpoints (variant-chip rule).
- *Sound, Sega game on NES chips:* the SN76489 maps almost 1:1 (tone 1 and 2 to the pulses, tone 3 to the triangle, noise to noise; Game Gear stereo is dropped,
  the apps are mono). Genesis FM voices map the loudest voices to pulses/triangle/noise, with the 8-channel extension bank carrying up to 8 voices (as `NesApuOnSnes`
  does for the DSP); the DAC becomes a noise hit. FM timbre is lost, as accepted.
- *Sound, NES game on Sega chips:* `APU_SMS` (NES registers to PSG, near-direct) and `APU_MD` (NES registers to PSG and YM2612; FM patches give the pulses a chosen
  timbre, a nice built-in gimmick).
- *Picture, Sega game on NES chips:* SMS Mode 4 (4 bpp, 32 colours from 64) and the Genesis (two planes + window + 80 sprites, 64 colours) go through the layered
  `PPU_FIXS` / `ExtPicture` path like `SnesToNes.RenderLayered`; the flat NES path is lossy and kept for the NES-faithful chips.
- *Picture, NES game on Sega chips:* `PPU_SMS`, `PPU_MD` (a NES PPU in front for timing, NMI and sprite-0; NES 2 bpp tiles translated to Sega 4 bpp each frame).
- *CPU:* a Z80 or 68000 running NES code is an instruction-set wall (the `CPU_SM83` precedent: interrupts through the NES vectors, lock-ups as NOP, most games die at once);
  a Sega game on a 6502 cannot exist. Honest "dead" is reported as dead.

**Plugin.** Append-only extensions: new `Cx` consoles and chips (PSG, YM2413, YM2612+PSG) reach Direct mode through **NES-register bridge APUs** exactly as DMG and SNES do today.
The existing `Instrument` parameter doubles as the **FM patch picker** (YM2413's 15 ROM instruments, a built-in YM2612 bank, patches lifted from a running game by
Runaway). ROM mode gets Mix for free through `SessionGame`. Stems need a write tap per chip and the channel model must grow past 5 (new `Ch` values for FM 1-6, DAC,
PSG tones/noise; append-only). Runaway: PSG snapshot (simple), YM2413/YM2612 patch capture to a sampler. A full operator editor is a later wave.
Budgets to hold: 0 bytes allocated on the audio thread, and a speed floor to be set per chip once measured (YM2612 and 68K cost are the unknowns).

## 7. Verification strategy (spec first, then game climb, then bridges)

| Layer | Gate |
|---|---|
| Z80 | SingleStepTests/z80 (all non-excluded files) + z80test + zexdoc/zexall, plus a proof that the verifier can fail (inject a bug, see annotated failures, as with the SNES verifier) |
| 68000 | SingleStepTests 680x0 + m68000 (documented exclusions), then Genesis-specific bus timing against a test ROM |
| SMS/GG VDP | FluBBa VDP test, vdptest 1-3, Mesen2 frame and register-trace diffs; roster of 20 SMS and 20 GG games from `X:\EMULATION` (342 and 665 titles on disk) |
| Genesis VDP | Nemesis ROMs (port access, sprite masking, CRAM flicker, FIFO), MacDonald's V-counter/window ROMs, Direct Color DMA, Overdrive 1; Overdrive 2 needs PAL (certified headless, decision A); BlastEm diffs; roster of 20 from 948 titles |
| Sound | PSG and YM2413 test ROMs; golden WAVs generated offline from VGM logs through Nuked-OPN2/OPLL (oracle only); pitch and level measured as in the plugin self-test |
| Whole core | golden-hash frame/sample suites (`--smsbench`-style, with a reference-twin for any fast path), `--smstest` / `--mdtest` / `--sega-run` Workshop CLIs, sweep tools, UAT cases, the plugin self-test extended |
| Bridges | `--console-run` style sweeps classifying every game x core combination as refused/crashed/dead/frozen/alive, honest results only |

**Oracle setup (foundation task):** a durable Mesen 2.1.1 copy (SMS/GG; currently only in an old scratchpad) and a BlastEm install (Genesis), outside the repo, driven black-box
(Lua traces, frame dumps). **Test asset policy:** commit only explicitly licensed material (z80test, SingleStepTests, MIT) with `LICENSE` and `SOURCE.txt`; keep unlicensed ROMs
(Nemesis, MacDonald, FluBBa, SMSTestSuite) and GPL zexall out of git via `.git/info/exclude`, as `gb-test-roms` already is; never commit game ROMs; no Sega firmware is ever
required (the user's `! Firmwares` folder holds none).

## 8. Roadmap

**Phase 0, foundation (sequential, short).**
1. Oracle and test infrastructure (above), after you approve the download list once; test-asset manifests; `--smstest` / `--mdtest` skeletons; the **live dashboard** (one for all three tracks) created and wired to the suite outputs.
2. Provenance: root `THIRD_PARTY_NOTICES.md`, `sega/THIRD_PARTY_NOTICES.md`, the QuickNES entry; `DSP_SFC` recorded as a documented LGPL exception.
3. Console plumbing: `ConsoleKind`, `Consoles.*`, `RomDetect`, `CoreCatalog`, `PadButtons`, save paths, the three csproj lists + `LinkerConfig.xml`, menu/Lite/plugin stubs so a dummy session appears everywhere.
4. Shared chips, ported once: **Z80** and **SN76489**, each with its verifier. Then **retire the joke `CPU_Z80`** (scope in 2.4a) and claim the id `Z80` for the real core.
5. Contracts: the `BOARD_*` factory shape, the Genesis sound-subsystem contract (bus handshake included), the **region-parameterised master-clock timeline** (NTSC and PAL tables both present, PAL reachable headless), the Genesis model (Model 1/2/3) setting, the bridge scaffolding for NES-register hub back-ends.
*Exit gate:* Z80 passes the vectors and the proof-of-failure; PSG matches oracle audio; a dummy console lists in desktop, Lite and the plugin; ROM-detect tests pass; the old `Z80` id is gone with Deck Builder saves and stored configs still loading; all merged to main.

**Track A, SMS / GG / SG-1000 / SC-3000 / Mark III.** Board and mappers; Mode 4 + TMS-mode VDP with SMS1/SMS2/GG revisions and counters/IRQ timing; FluBBa/vdptest green;
YM2413 (port) and the `$F0-$F2` ports; GG window, ports `$00`/`$06`; session, input, saves; BIOS-less post-boot table from public documentation; the climb on 20 + 20 games with Mesen2 diffs.
**Track B, Genesis.** 68000 (ares spike, then SingleStepTests); timeline and bus arbiter; clean-room VDP at Tier 2.5 with slot-ready data; YM2612 (ymfm port, chip-type switch discrete
YM2612 / YM3438); Z80 integration with bank-window stalls; mappers/SRAM/EEPROM; 3- and 6-button pads; the Nemesis/MacDonald gates; the climb of 20 games vs BlastEm. A **WASM/AOT speed spike
as soon as the VDP draws**, not at the end.
**Track C, bridges and plugin.** Sound bridges first (cheapest, and what the plugin needs), then picture bridges, then CPU walls; catalog tables and menus; Lite wiring; plugin Direct chips +
patch picker, then ROM-mode Mix, then stems, then Runaway; plugin self-test additions. C depends on A and B contracts but starts on the Phase 0 stubs.
**Phase 4:** Lite performance, certification across desktop/Lite/plugin (including the headless PAL gate), docs, and a final pass on the dashboard.

**Effort summary** (experienced-human estimates from the research, for scale only; this project's recorded pace for GB and SNES was days per track):
SMS/GG about 30-45 d, Genesis about 50-70 d (Tier 2.5), bridges and plugin not estimated by the research (their closest precedent is the GB/SNES mix work, about two days of agent time per bridge family).

## 9. Risks and decisions

**Risks.**
- **Genesis in Lite (WASM).** A 68000 + slot-aware VDP + YM2612 in the browser is the biggest performance unknown; the SNES needed AOT. Spike early; a Lite-only reduced mode would be a decision, not a fallback.
- **Plugin budgets.** 68000 + VDP + YM2612 inside the plugin's allocation-free, 5x-real-time frame; ROM mode may need a lower floor for Genesis than the 5x used for four NES voices.
- **FM collapse on the NES hub** is accepted, but a Genesis game on NES chips will sound thin; the extension bank mitigates it for pitch/level, not timbre.
- **PAL is deferred in the UI, not in the engine:** the timing tables carry the region from day one and PAL is certified headless (Overdrive 2, the ~20 PAL-timing SMS titles). The risk is a table written NTSC-only by habit: every track's review checks for it.
- **Retiring `CPU_Z80`** touches Legacy Deck Builder saves; the migration must be tested against a real saved game before the id disappears.
- **No permissive cycle-exact C# 68000**: the ares port is the bet; the spike must show it carries timestamped bus accesses cleanly.
- **Static `MixConfig`** does not scale to several concurrent bridged sessions; new bridges should avoid new statics.
- **Reference availability:** oracle emulators change (see "verify" list); keep local pinned copies.

**Decisions resolved (2026-10-03).**
- **A. PAL:** region-parameterised timing tables from day one; NTSC ships first; PAL certified headless.
- **B. Genesis model:** Model 1 / 2 / 3 selectable per game from the start (menus and plugin), with a per-game default. Open detail for Track B: which default each game gets (start from Model 1 for everything, add per-game overrides as tests show a game needs another model).
- **C. Ids:** CPU slot by chip name (`Z80`, `M68K`), PPU/APU console-style (`SMS`, `GG`, `MD`); the joke `CPU_Z80` is retired (2.4a).
- **D. Housekeeping:** root notices + Sega ledger + QuickNES entry; `DSP_SFC` stays as a documented exception.
- **E. Sega CD and 32X:** dropped from the plan (with the Power Base Converter); no design provisions.
- **F. Dashboard:** one live dashboard for all three tracks.
- **Downloads:** one approval of the Phase 0 download list.

**Still genuinely open (small).** The per-game Genesis model defaults (above); whether Game Gear is its own `ConsoleKind` (assumed, like GBC) or a model of the SMS board in the menus; the exact Phase 0 download list (written when Phase 0 starts).

## 10. Verify before relying

Not confirmed by the research: Mesen2's current status (one summary says the repository was archived on 2026-06-04 with a successor, MesenCE); a claimed Genesis core in a Mesen2 fork; jgenesis's
license history (MIT versus GPL-3); MEKA's license text; Z80dotNet's zexall result; the exact `$DC/$DD` bit table and the Sports Pad page (from general knowledge); the NTSC-240 V-counter row in
MacDonald's SMS table (sums to 263, likely a typo); the Kabuto V28/V30 start-line labels (probably mislabelled); the Z80-stall figures (3.3 Z80 cycles; 9.7 versus 11 68K cycles); exact intra-line
positions of the SMS frame and line IRQs; Sega Retro and jsgroth.dev could not be read (bot walls), so no page from either is cited.

## 11. Key sources

- SMS/GG: MacDonald VDP and hardware notes (smspower.org/uploads/Development/msvdp-20021112.txt, smstech-20021112.txt); SN76489 notes (smspower.org/Development/SN76489); Sean Young, *The Undocumented Z80 Documented*;
  Sega *Game Gear Hardware Reference Manual*; SMS Power wiki (Mappers, ROMHeader, BIOSes, Port3E, InterruptMechanism); FluBBa SMS VDP Test; Mesen2 `Core/SMS`; ares `ms/vdp`; Genesis Plus GX `sms_cart.c`.
- Genesis: Sega *Genesis Technical Overview*; MacDonald `gen-hw.txt`; Kabuto's hardware notes (plutiedev.com/mirror/kabuto-hardware-notes); Plutiedev; SpritesMind research threads (t=2227, 2492, 2202, 787, 3221);
  Exodus techdocs (test ROM list); BlastEm, jgenesis (`timing.rs`), Genesis Plus GX sources; ymfm `GeneralInfo.md`; Nuked-OPN2 / Nuked-MD; SingleStepTests (z80, 680x0, m68000).
- Licensing: repo `LICENSE.txt`; BizHawk, MAME, ares, ymfm, emu2413, Musashi, Moira, Exodus license files and headers (URLs in the research notes); GPL-3 sec. 10 and 5(c), LGPL-2.1 sec. 6.
- Local: BizHawk tree `PROJECTS\Bizhawk52X-Vanguard` (`Consoles\Sega\SMS`, `CPUs\Z80A`, `Sound\SN76489sms.cs`); ROM sets `X:\EMULATION\Sega Master System` (342), `Sega Game Gear` (665), `Sega Genesis` (948), `Sega CD and Mega-CD` (3).
## 12. Phase 0 outcomes (2026-10-03)

What building the foundation settled or changed, so the tracks start from facts rather than the plan's guesses.

- **Preview switch.** The three consoles are hidden until their boards exist: `BROKENNES_SEGA=1` (desktop, Workshop) or `?sega=1` (Lite) turns them on, and with it off the app is unchanged (the Console menu lists four, a stored Sega selection falls back, Lite refuses Sega files). Each track flips its own `*Ready` flag when its board is real.
- **Verifiers added** (Workshop): `--sega-test` (plumbing, 77), `--sega-chips-test` (SN76489 protocol, pitch, noise periods, aliasing, 34), `--sega-oracle-test` (SN76489 against Nuked-PSG sample for sample, 17), `--sega-contracts-test` (contracts, 69), `--z80test` (Z80, see its own results). UAT: `UAT\entrypoints\sega-preview-smoke.ps1` (desktop, 25).
- **Oracle tools** live in `C:\BrokenNes-tools\sega` (43 items, hash-verified; `Workshop\Sega\fetch-tools.ps1`, `build-oracles.ps1`). The PSG oracle showed the **Genesis PSG differs from the Master System one** at tone period 0 or 1 (it flips every PSG clock; the SMS part holds the output high), so the chip has three variants, `Sega`, `MegaDrive`, `Ti`. The oracle's attenuation levels part from 2 dB steps by up to 12.9 dB at volume 14; left to Track B.
- **Corrections to the research.** The Genesis VDP pixel clock is master/10 in H32 and master/8 in H40 (not /5 and /4). The Master System cartridge header cannot tell NTSC from PAL (its export region covers both markets), so region comes from the file name's tag or the user. Decision B's default is applied: `Auto` resolves to Model 1, with a per-title table (`SegaSettings.GenesisModelByProductCode`) for overrides.
- **A real bug found in Lite.** Its ROM import filtered on a hard-coded extension list in two places of `nesInterop.js`, so Sega files were dropped silently even with the preview on; the filter now follows the page's own accept list. The "ROM extensions are listed in about six places" warning in 2.4 should read eight.
- **Contracts in `sega/`:** `SegaTimeline` (master clock, device dividers, exact `Due` accounting, both regions), `SegaBoardParts<TBus,TCpu,TPpu,TApu>` (optional factories every board takes), `SegaSettings`, `IGenesisSound` + `GenesisSoundStub` (handshake, bank window, PSG from the 68000; no Z80 and no FM, and it says so), and `SegaHub` (`HubVoice`, `NesVoiceWriter`: PSG voices to NES registers with stable slots and extension banks, checked end to end on a real NES APU core).
- **Plugin.** Phase 0's gate asked for a dummy console in the plugin; the plugin's console list is tied to its Direct-mode chips, so it moves to Track C. What holds today: the plugin refuses Sega ROMs cleanly, `Bn2Consoles.FromKind` no longer defaults an unknown console to NES, and its self-test still passes 39.
- **`CPU_Z80` retired** (code, card art, Deck Builder lists, comments) with a load-time migration in `gameSave.js` and `continue.js`; tested on saves that owned it, preferred it, and used the legacy format, and a stored `SelectedCpuCore=Z80` falls back to CPU_FIX in the running app.