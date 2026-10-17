# Round 5, `snes-state`: save states for the SNES (every console now has them)

Branch `worktree-agent-ae348ce0e34608aa6` (not pushed), 2026-10-16/17, on top of `round5-brief.md`. The user: *"the SNES savestates is a requirement, we need to have savestates on all consoles"*. Before this round the Super Nintendo session said `SupportsState = false` ("The SNES has no save states yet"); after it, F7 / F5, Save State... / Load State..., the HTTP quick-save API, the Lite buttons and the plugin's project state (c4b) work for every SNES game, including the DSP-1, SA-1 and Super FX ones. **Everything below is what was run**; "not tested" is said where it is.

## 1. What was built

| Area | What | Where |
|---|---|---|
| Component states | one `SaveState(BinaryWriter)` / `LoadState(BinaryReader)` per component, symmetrical, each starting with a version byte that the loader checks first (a component that reads more or fewer bytes than its writer wrote shows up as the next component's version byte being wrong, with the component's name) | `snes/CPU_SFC.State.cs`, `PPU_SFC.State.cs`, `BOARD_SFC.State.cs`, `SMP_SFC.State.cs`, `DSP_SFC.State.cs`, `APU_SFC.State.cs`, `APU_HLE.State.cs`, `SnesCartridge.State.cs`, `DSP1_SFC.State.cs`, `NECDSP_SFC.State.cs`, `SA1_SFC.State.cs`, `GSU_SFC.State.cs`, `SnesStateIo.cs` (the shared helpers) |
| Session | `SnesSession.SaveState / LoadState / SupportsState`, the container `ConsoleStateFile` with the units `apu` (the sound unit), `chip` (the cartridge chip, with the firmware's hash for Nintendo's DSP-1 image) and `format` (`sfc-1`) that must match; `cpu` and `ppu` are information only; header checked before a byte of the machine is touched; undo copy taken before a load and put back if the payload does not fit | `systems/SnesSession.State.cs` (SnesSession is now `partial`) |
| Tool / test hooks | `BOARD_SFC.RunToLine(line)` (stop between two instructions at a line, including the vblank lines `RunFrame` cannot reach), `SnesSession.RunToLine`, `Board`, internal `PayloadForTest / LoadPayloadForTest / ParkedSpc` | `BOARD_SFC.State.cs`, `SnesSession.State.cs` |
| Interface | `ISnesCoprocessor` gained `StateId`, `SaveState`, `LoadState` (four implementations, all in `snes/`) | `snes/ISnesCoprocessor.cs` |
| Container | table-driven CRC-32 (the same function; the bit-by-bit one stays as `Crc32Reference` for the test; about ten times faster on a half-megabyte state), a `chip` refusal text | `systems/ConsoleStates.cs` |
| NES sound unit on the SNES | `NesApuOnSnes.Rebase(masterClock)`: after a load the NES chip keeps what it plays and listens to the loaded voices from the board's new time (no new NES host is built) | `mix/ApuBridges.cs` (one small additive method, outside `snes/`) |
| Verifiers | `--snes-state-test` (147 checks), `--state-audit` (every core of every console), `--alloc-probe`, UAT `states` suite (20 cases through `--session-test --rewind`), `snes-state-smoke.ps1` (real desktop app), `test-lite-snes-state.mjs` (real browser, AOT build) | `Workshop/SnesStateTest.cs`, `SnesFieldWalker.cs`, `SnesProgs.cs`, `StateAuditCli.cs`, `AllocProbeCli.cs`, `Workshop/Sega/SessionTestCli.cs`, `UAT/cases/states.json`, `UAT/entrypoints/snes-state-smoke.ps1`, `UAT/lite/test-lite-snes-state.mjs` |
| App | the HTTP quick save / load answer says the real refusal reason (it used to say "the console log has the message"); the hand-off guide, Workshop and web API READMEs updated | `MainForm.SessionStates.cs`, `MainForm.SaveStates.cs`, `docs/handoff-testing-guide.md`, `Workshop/README.md`, `Windows/webapi/README.md`, `UAT/README.md` |

The desktop (`MainForm.SessionStates.cs`) and the Lite page (`Emulator.Consoles.cs`) were already written against `IConsoleSession.SupportsState`, and so is c4b's project state in the plugin (`Plugin/BrokenNes2.Plugin/Game.cs`: `StateSupported => s.SupportsState`): they switched to the SNES by the one flag, with no change of their own.

## 2. Every piece of SNES machine state (S1)

The SNES is instruction-granular on the board (65816 steps, every access ticks the master clock, DMA / HDMA run inside the write that starts them) and **lazy** everywhere else (the audio unit, the SA-1 and the Super FX catch up to the master clock from clocks they carry themselves; the S-PPU draws a line at the start of each visible line from its registers). So a state is complete at any point **between two 65816 instructions**, which is what `RunFrame` (stops at the start of vblank), `RunToLine` and the apps' save point (between frames, under the emulation lock) all are.

| Component | What is written | Fields (reflection count, Super Mario World) |
|---|---|---|
| Board (`BOARD_SFC`) | master clock, frame / line / line-clock counters, the next-event bound, vblank line, the HDMA / refresh stall, `$4200-$421F` (NMI/IRQ enables, H/V time, multiply / divide unit and results, WRIO, MEMSEL), NMI / IRQ / chip-IRQ lines, the CPU open-bus value, the bus address (for the SA-1's conflict timing), held pads, the auto-read result, the serial shift registers and latch, the WRAM port address, the 128 KB of WRAM, the 128 DMA registers, the HDMA done / transfer flags and counters, the diagnostic ring of `$420C` writes | 72 |
| 65816 (`CPU_SFC`; the SA-1 has a second one) | A X Y S D PC DBR PBR P E, stopped / waiting latches, pending NMI, IRQ line, instruction counter, the operand address register | 17 |
| S-PPU (`PPU_SFC`) | VRAM, CGRAM, OAM, every register with its latches (the two-write scroll latch, the Mode 7 latch, the CGRAM and OAM write latches, the VRAM prefetch, the H/V counter latches, the PPU1 / PPU2 open-bus values), range / time overflow, the display flag, the 256-wide frame in progress and published picture, the rows drawn in a hi-res mode at their 512-wide width and their flags, the visible height | 64 |
| Audio unit (`APU_SFC`) | 64 KB ARAM, both directions of the four ports (the IPL handshake latches), IPL switch, DSP address, the three timers with their dividers, the unit's clock (SPC cycles run, and toward the next DSP sample), samples produced, **the unread samples in the output ring** (logical content; where they sit in the ring is not state) | 15 |
| SPC700 (`SMP_SFC`) | A X Y SP PSW PC, halted, extra cycles of the instruction | 8 |
| S-DSP (`DSP_SFC`) | 128 registers, each of 8 voices (12 decoded BRR samples, positions, key-on delay, envelope, hidden envelope, mode), KON / KOFF latches, noise, the global rate counter (+ its phase table), echo (position, length, 8-tap history, last output) | 87 |
| Cartridge | the RAM (battery RAM; the Super FX's and the SA-1's work RAM are this same array) and the write counter | 2 |
| DSP-1 homemade | port phase, command, input / output words and positions, byte-half latch, raster stream, the three attitude matrices, the camera (focus, distances, angles, eye, view and screen vectors, held horizon) as bit-exact doubles | 37 (with Super Mario Kart) |
| DSP-1 firmware (µPD77C25) | data RAM, 4-level stack, PC / SP / RP / DP, both accumulators and flag sets, K L M N, TR TRB, DR SR SO, instruction clock; the **firmware's hash is part of the chip id**, so a state refuses another image | covered by the sweep on Super Mario Kart with the firmware |
| SA-1 | its own 65816, the clock, I-RAM, every control / status register of both directions, the vectors, timer, Super MMC banks, BW-RAM mapping and write protection, DMA and character conversion (incl. the bitmap register file and line counter), multiply / divide / accumulate, variable-length bit reader | 60 + 17 (its CPU) |
| Super FX | 16 registers, SFR, bank / screen / config registers, the fetch pipeline and prefix state, the ROM read buffer and RAM write buffer **with their pending clocks**, the 512-byte instruction cache and valid bits, the two-entry pixel cache, clock and counters, the SNES-side lock-out latch | 50 |
| HLE stand-in unit (`APU_HLE`) | ports, its 64 KB, the loader's state, entry point, bytes uploaded | |
| Session | which kind of sound unit; for the stand-in, the **real SPC700 + S-DSP that was swapped out behind it** (swapping back resumes it, as the live swap does); for a NES chip as the sound unit, the SPC700 + S-DSP behind it | |

**Field counts (reflection over the whole object graph, `--snes-state-test`, group "every field"):** Super Mario World 265 fields (359,995 values), Super Mario Kart 302 (360,070), Kirby Super Star 342 (376,472), Star Fox 315 (424,089). A "field" is a primitive, an enum, or an array of primitives; arrays of objects and structs are walked into.

### What is **not** written, and why (each is listed by pattern in the test with its reason)

* The game's ROM and the chip's firmware image: the game's identity (the header names the game by SHA-1; the firmware by hash).
* Cartridge header facts (`HiRom`, `MapMode`, `ChipsetByte`, `Chip`, `HasBattery`, `Title`) and chip wiring fixed at construction (mapping, clock ratio, address masks, "owns its RAM").
* S-PPU per-line scratch (`bgColor, bgPrio, objColor, objPrio, objMath, rankMain/Sub, colorMain/Sub, layerMain/Sub`): rewritten whole for every line before it is read. S-PPU caches (window masks and signatures, decoded sprite attributes and versions, rank table, brightness tables, the bridge's OAM copy): rebuilt on use; the loader calls `InvalidateCaches()`. `HiResBuffer` rows that were not drawn at 512 width are rebuilt from the 256-wide frame on request.
* The board's page table (derived from `$420D` and the chip's bank registers; rebuilt at the end of a load, after the chip, whose bank registers it depends on).
* The audio ring's storage and read position (only the unread samples are state).
* Tool hooks and settings (`InstructionHook`, `WriteWatch`, `SmpHook`, `BridgeLines`, `DebugLineRegisters`, `DebugLayerMask`, `DropVramWritesDuringDisplay`, the Super FX trace / watch tools, the DSP-1 port log) belong to whoever installed them and are left alone by a load.
* The picture chip: a NES / Game Boy chip drawing the SNES picture is a **view** of the S-PPU (`SnesToNes` translates line by line from the S-PPU's registers and `BridgeLines`). After a load the session builds the bridge afresh (`SetPicture`), as `SegaPictureSession.LoadState` does; the bridge's own caches are not state.
* A NES chip as the **sound unit**: a listener (`NesApuOnSnes`), not part of the machine. The SPC700 + S-DSP behind it is saved whole; the NES chip keeps what it plays and re-listens from the next sync (`Rebase`). Audio after such a load is alive but not sample-identical to the session that carried on; the machine, the pictures and the SPC700 / S-DSP are.

### Where a state can be taken

At every point the board can be stopped: a frame boundary (the apps' save point), and **any line** (`RunToLine`: tested at lines 30, 112, 190, 226 and 250 of the frame: display, the lines after it, and the vblank). Not inside an instruction and not inside a DMA / HDMA transfer: the board never stops there (a transfer is one call inside the write that starts it). A state taken in the middle of a frame holds the picture drawn so far; a session that loads it finishes the frame.

### What a state belongs to (refusals)

The header carries the console, the game id (`sfc<SHA-1>`), the sound unit id (`apu`: `SFC`, `HLE`, `NES:<chip>`), the cartridge chip (`chip`: `none`, `DSP-1 (homemade)`, `DSP-1B firmware <hash>`, `SA-1`, `Super FX`, `Super FX 2`) and the payload layout (`format`). A state of another game, console, sound unit, chip or layout, or a damaged / cut-short / foreign file, is refused with its reason **before anything is touched**; a payload that passes the checksum but does not fit is undone (the machine is put back as it was) and the message says so. A state is never loaded into a different sound unit and no part is swapped to make it fit.

## 3. The verifiers (S2) and the proof that each can fail

### 3.1 `Workshop --snes-state-test [--games ...] [--only group,...] [--quick] [--poison NAME]`

**147 passed, 0 failed, 0 skipped** (6 minutes on a shared machine). Roster (all from `X:\EMULATION\Super Nintendo\Super Nintendo (U)`, scripted pads that play into menus and levels; nothing copied into git): Super Mario World, Zelda A Link to the Past, Donkey Kong Country, Super Metroid, Street Fighter II Turbo, Contra III (plain boards); Super Mario Kart (DSP-1, homemade, and again on Nintendo's `dsp1b` firmware from the user's BizHawk folder); Kirby Super Star and Super Mario RPG (SA-1); Star Fox and Yoshi's Island (Super FX and Super FX 2).

| Group | Checks | What it proves |
|---|---|---|
| round trip | 55 | at two save points per game, a state is loaded into a **session of the same game running somewhere else** (as F5 in a running game); the 120 frames that follow are the picture of every frame, every audio sample (count and hash), and **the state each machine writes at the end (the whole machine, byte for byte)** of the session that carried on; the picture right after the load is the saved one; saving again right after the load gives the same bytes; the loaded session's page table equals a rebuild; plus a **power-on state loaded into the game running at a save point** (what the running game derived from its registers - page table, caches - must follow the loaded ones) |
| every field | 12 | a restored session is compared with the one that saved **field by field by reflection** (counts above), the unread audio samples and the hi-res rows included; the comparison is shown able to see: a freshly started machine differs from the saved one in 158 / 184 / 165 / 209 of those fields; then **each field in turn is changed in the saving machine and must come back out of a load** (scalars: a flipped bit, retried with other values if the loader's range check refuses one; arrays: first, middle and last element): 265 / 302 / 342 / 315 fields, 337-422 changes each |
| mid-frame | 15 | states between two instructions at lines 30, 112, 190, 226, 250 of the frame (Super Mario World, Zelda, Super Mario Kart, Kirby, Star Fox): every field equal right after the load, re-saving gives the same bytes, the next 41 frames and the state 41 frames later identical |
| refusals | 15 | another sound unit (`HLE`, `NES:FIX`, and the reverse), another game, a Game Boy state, a flipped byte ("damaged"), a cut-short file, a foreign file, **the homemade DSP-1's state refused by a session on Nintendo's firmware and the other way, naming both chips**, a payload of the right header and wrong size (refused and undone); the refusing session is byte-for-byte unchanged and still loads a good state afterwards |
| sound units and picture chips | 19 | a state saved after a live swap to `HLE` (the real SPC700 + S-DSP parked behind it comes back with the state; swapped back to the real unit, both resume the same unit: 90 frames identical), to `NES:FIX` and `NES:FIXS` (machine and SPC700 + S-DSP exact, pictures equal for 120 frames, sound alive), a state saved on the SFC picture loaded under `NES:FIXS` / `NES:FIX` and the other way round; the sound unit swaps live after a load |
| factory | 5 | `ConsoleSessions.Create` (what the apps build) saves and loads; the quick-save slot file (`States\snes\sfc<sha1>.quick.bnstate`) reads back; the header names console / game / unit / chip / layout; the CRC-32 equals the bitwise definition on buffers of 0 to 531,000 bytes |
| program | 8 | a 32 KB program of ours (`SnesProgs`, no game data) that counts frames while Right is held and shows the counter as the backdrop colour: the counter and the colour come back, it counts on identically, a power-on state puts it back to 0, on `HLE` and `NES:FIX` too; the hex the app smoke test carries is byte for byte the program the Workshop builds |
| size and time | 14 | table below |
| allocation | 4 | 300 frames of play allocate 0 bytes a frame (Super Mario World, Zelda, Kirby); Star Fox allocates 3,058 bytes a frame **on the build before this round too** (`--alloc-probe` on commit 15795e6 and on this one: 917,672 bytes over 300 frames, to the byte) |

**Proofs of failure (all run):**

* **Poison per component group** (`--poison NAME`, a field changed in the restored session after the load, standing for a loader that left it unrestored): *every one of the 17 groups makes the suite fail* (FAIL lines): `wram` 9, `vram` 12, `cgram` 11, `oam` 7, `ppureg` 8, `cpu` 8, `timing` (line clock) 10, `dma` 8, `hdma` 6, `aram` 9, `spc` 12, `dsp` 12, `timers` 12, `ports` (the CPU-to-SPC latches, the "stale SPC port" note) 10, `sram` 10, `chip` (Super FX register / SA-1 bank / DSP-1 count / µPD77C25 accumulator) 15, `parked` (the real unit behind the stand-in) 3.
* **Real loader omissions injected into the source** (read and discard instead of restore), built, run, reverted: `irqLine` (65816), `m7a` (S-PPU), `hdmaen` (board), `extraCycles` (SPC700), `noise` (S-DSP), `timerDiv64k` (audio unit), `bmaps` (SA-1), `romPending` (Super FX). **The field sweep names each of the eight**; the field compare and the byte-for-byte round trip fail too (Super Mario World's *pictures* were unaffected by most of them: only the byte comparison saw them).
* **Derived-state omissions injected** (the loader's `BuildPageTable()` and `InvalidateCaches()` left out): the frame-boundary round trips alone did **not** see them (a finding that changed the test: the loading session is now a game running elsewhere, and a power-on state is loaded into a running game); with those two additions the page-table omission fails *Super Metroid's* power-on check and the cache omission fails the mid-frame group on Zelda, Super Mario Kart and Kirby.
* `SupportsState` forced to `false`: `--smstest --only sessionstate` fails its SNES assertion ("SupportsState is false: The SNES has no save states yet.").
* The UAT states suite below, with the board's work RAM left out of the load: the six SNES rewind cases fail (four of them already by the picture hash: Zelda, Super Mario Kart, Star Fox, Yoshi's Island; Super Mario World and Kirby by the sound hash), the six plain ones and the four other consoles' cases pass.

### 3.2 `SessionStateSuite` (`--smstest --only sessionstate`, 48 pass)

The old assertion "the SNES session says it has no states" is turned round: the SNES session must report `SupportsState`, return a state whose header names the SNES, and a fresh session must load it and give the same state back. It fails if `SupportsState` is ever false again (shown above).

### 3.3 `UAT/cases/states.json` (20 cases, `run-suite.ps1 -Filter 'state-*'`: 20 pass)

Ten games, each as a *plain* run and a *rewind* run (`--session-test --rewind N:M`: a state is taken after frame N and loaded back after frame M; frames N+1..M are played again with the same pad; the first pass's sound is dropped). A complete state ends the run on exactly the plain run's picture hash, sound hash and colour count, so **the two cases of a pair carry the same golden**. Six SNES pairs (Super Mario World, Zelda, Super Mario Kart, Kirby Super Star, Star Fox, Yoshi's Island) and one pair each of the Game Boy, Master System, Game Gear and Genesis (so the other consoles' states are exercised the same way: they all agree too).

### 3.4 Desktop: `UAT/entrypoints/snes-state-smoke.ps1` (19 checks pass in the real app)

Loads the counter program as a real SNES game through `RomDetect`: right console, `supportsState` true in `/api/console`, still picture without input, the backdrop changes with Right held; F7's method (the quick-save API calls it): slot file written, header bytes name `snes`, `sfc<sha1>`, `apu`, `chip`, `sfc-1`; hold Right (picture moves on), F5's method: **the saved picture is back**; the program runs on after the load. Refusal: the sound unit swapped to `HLE` (live), the load is refused with a reason that names both units and the picture does not change; swapped back, the same state loads. Then Super Mario World: loads, quick save writes a 78 KB slot, quick load succeeds at 61 fps, the game runs on. The user's `config.json` is copied aside and put back (the quick-save API is the opt-in `enableWebmoduleSavestateDebugShortcuts`).

### 3.5 Lite: `UAT/lite/test-lite-snes-state.mjs` (18 checks pass; AOT publish, real headless Edge)

For the counter program and Super Mario World: the page runs it as a SNES game; **Save state** stores a state ("State saved (117 KB)", under `state_v1:snes:sfc<sha1>.quick` in IndexedDB); **Load state** and the next 60 frames are the same pictures as the first time (60/60 equal); the picture moves in between; the sound goes on (60/60 frames with samples); a fresh page of the same game loads the state from IndexedDB and runs the same 60 pictures; after the sound unit is changed to `NES:FIX` the load is refused with the reason and the game keeps running; the counter program's state under Super Mario World's slot is refused ("This state belongs to ... not to ..."); Load state with nothing saved says so. **WebAssembly AOT: save 18-21 ms, load 12-17 ms** (the call, from the page).

## 4. No regression (S3)

| Check | Result |
|---|---|
| `--snesbench` goldens (every frame and audio sample hashed) | `smw_sfc_4300.json` and `alttp_sfc_2400.json` **IDENTICAL** after the state code (3d00439), also with `--reference-paths` (the FastPaths twins) and `--injected` (the factory constructor with a wrapped bus) |
| `--snestest` | cputest-basic 0 failures, cputest-full 0, spctest 0 (Success) |
| UAT `run-suite.ps1` (all manifests, fresh `-Exe`) | 72 pass, 0 fail, 60 skip (the skips are the unimplemented mappers and missing ROMs, as before); the new `states` suite is 20 of those |
| `--md-state-test` (the Genesis states, which share the container whose CRC changed) | 168 of 168 |
| `UAT/sega-gate.ps1` (19 steps) | 18 pass; `md-golden` 78 of 79 fails on Titan Overdrive 1 (PAL) frame 1007: **the same failure on the base commit 15795e6** (`--md-golden` run on a build of it: 78 frames match, 1 changed, the same frame and hash `C740C2A7D83CE55F`) - not this round's |
| `--smstest` (596) / `--md-test` 175 / `--sega-contracts-test` 80 / swap-matrix 35 / bridge 356 / picture-bridge 237 / stems 38 / md-cart 79 / md-pic 145 / z80 vectors 1,604,000 / m68k 1,284,785 / oracles 17 + 43 + 173 | all pass (part of the gate) |
| Speed, interleaved A/B of the frame loop against the base commit's build (`--snesbench`, process CPU time, 15 alternations, base first on even runs) | Super Mario World median **99.3 %** of the base (best-of 102.7 %); Kirby Super Star (SA-1) **101.3 %** (best-of 98.6 %); an earlier 7-alternation run gave Super Mario World 97.1 %, Zelda 95.1 %, Star Fox 104.0 %, Kirby 94.7 % - the ranges of the two builds overlap throughout: noise of a machine shared with other agents; no state code is on the frame loop |
| Allocation | 0 bytes a frame (Super Mario World, Zelda, Kirby), and Star Fox's 3,058 bytes a frame is identical on the base commit (see above) |
| Lite / WebAssembly speed | not re-measured (nothing on the frame loop changed) |

After merging `refs/heads/main` (c4b's project state, inputs' d-pad protection and the BrokenNes2 rename) the merge was clean; see section 6 for what was re-run on the merged tree.

## 5. Sizes and times

Desktop JIT, warm, the machine shared with other agents (`--snes-state-test`, group "size and time"):

| Game | State packed | Raw payload | Save | Load (includes the undo copy a load keeps) |
|---|---|---|---|---|
| Super Mario World | 117 KB | 500 KB | 2.2 ms | 1.6 ms |
| Zelda: A Link to the Past | 110 KB | 506 KB | 2.5 ms | 1.6 ms |
| Donkey Kong Country | 108 KB | 500 KB | 2.2 ms | 1.5 ms |
| Super Metroid | 116 KB | 506 KB | 2.2 ms | 1.6 ms |
| Street Fighter II Turbo | 103 KB | 498 KB | 2.0 ms | 1.5 ms |
| Contra III | 100 KB | 498 KB | 2.1 ms | 1.5 ms |
| Super Mario Kart (DSP-1) | 146 KB | 501 KB | 2.5 ms | 1.8 ms |
| Kirby Super Star (SA-1) | 137 KB | 508 KB | 2.4 ms | 1.6 ms |
| Super Mario RPG (SA-1) | 150 KB | 532 KB | 2.6 ms | 1.7 ms |
| Star Fox (Super FX) | 112 KB | 531 KB | 2.2 ms | 1.6 ms |
| Yoshi's Island (Super FX 2) | 105 KB | 531 KB | 2.1 ms | 1.6 ms |
| Super Mario World on `HLE` / `NES:FIX` / `NES:FIXS` | 116 / 117 / 117 KB | | 2.1 / 2.4 / 2.3 ms | 1.8 / 1.8 / 1.6 ms |

Writing the machine itself is 0.1-0.3 ms and reading it back 0.1 ms; the rest is the deflate (fastest) and the checksum of the container, and the undo copy of a load. The counter program's state is 5 KB. In the browser (AOT WebAssembly, called from the page): save 18-21 ms, load 12-17 ms, 117 KB for Super Mario World in IndexedDB. A plugin instance keeping the SNES's whole state in an FL project therefore adds about **100-150 KB per instance** (before the project's own compression); the section cap in `GameStateSection` (8 MB) is far above it.

## 6. App integration (S4)

* **Desktop** (`MainForm.SessionStates.cs`): F7 / F5 and Save State... / Load State... already route every console session through `SupportsState / SaveState / LoadState` under the emulation lock; no SNES-specific line was needed. The only change is `Refuse` now keeps the message so the quick-save HTTP answer carries it (`{ success, gated, reason }`: "This state was saved with the SFC sound unit, but the game is running the HLE sound unit ..."). Every "the SNES has no save states yet" message is gone (`git grep "no save states yet"` finds the contract's generic default text, which no session reaches any more, and the Genesis line of an old record). Verified in the real app through the API (same methods as the keys); **the physical F5 / F7 key presses and the file dialogs of Save State... / Load State... were not driven**.
* **Lite**: `SaveSessionStateAsync / LoadSessionStateAsync` (generic); verified in a real browser on the AOT publish (3.5).
* **Plugin** (c4b, merged into main before this branch merged it): `Game.cs` reports `StateSupported => s.SupportsState`, so SNES games now take the path the other consoles take (`GameStateSection` "GAM1"). c4b's own self test is written to switch by itself (`SelfTest.RunawayState.cs`: if the SNES reports state support it runs the SNES through the state scenarios, otherwise it checks the 'cannot keep its state' note). What the plugin side gets from the SNES: **state 100-150 KB, save about 2-3 ms, load about 2 ms** on the desktop JIT (the AOT plugin DLL is native); the state belongs to the sound unit (`SFC` in the plugin), the chip and the layout `sfc-1`, so a project reopened by a build with another layout refuses it with a reason; `s.Apu` stays the same `APU_SFC` object across a load for the `SFC` unit (loaded in place), so a sampler holding the SPC700's ARAM / DSP register arrays keeps pointing at live memory. See section 8 for the result of the plugin self test on the merged tree.
* **Corruption anchors of kind `state`** (`corr`): `SaveState()` is 2-3 ms and 100-150 KB; a raw payload without the container is 0.1-0.3 ms (internal `PayloadForTest` / `LoadPayloadForTest`, not part of the contract). Hooks installed by the corruptor (`WriteWatch`, `InstructionHook`) are left alone by a load; direct writes to VRAM / CGRAM / OAM need `Ppu.InvalidateCaches()` as before.

## 7. Audit: which state each console really gives (`Workshop --state-audit`)

The audit builds the session of a real game the way the apps do for **every id the console's three menus offer** (the other two slots on the console's own cores), runs 40 frames, asks `SupportsState`, saves, loads into a fresh session of the same combination that has run elsewhere, and compares (the bytes it writes straight after, the bytes the two machines write 20 frames later, the pictures of those 20 frames). Games: Super Mario World, Super Mario Land, 1942 (GBC), Alex Kidd in Miracle World, Columns (GG), Sonic the Hedgehog (Genesis).

**205 combinations supported, 23 partial, 48 refused with a reason, 0 broken, 0 not built.**

| Console | Desktop (F7/F5, menus, API) | Lite (buttons, IndexedDB) | Plugin (project state, c4b) | CPU menu | Picture chip menu | Sound unit menu |
|---|---|---|---|---|---|---|
| NES | own JSON state (`QuickSaveState`), unchanged | own JSON state, unchanged | the NES mode keeps its own state; not re-tested | n/a | n/a | n/a |
| **SNES** | **supported**: 46 of 46 combinations: `SFC` (CPU), the SFC chip and all 18 `NES:` picture chips (a view; rebuilt after a load), the SFC unit, `HLE` (with the parked real unit), all 24 `NES:` sound units (SPC700 + S-DSP exact; the NES chip re-listens). Real app: 19 checks | **supported** (18 checks, AOT) | `SupportsState` true: the same path as the other consoles; the plugin self test passes for the SNES with the test ROM and with four real games (section 8); FL itself not run | `SFC` supported | all 19 supported | all 26 supported |
| Game Boy (DMG) | supported | supported by the same generic code (not browser-tested here) | supported | `GB`, `GB-HALF`, `GB-2X`, `GB-NESCLOCK` supported; **`GB-SNESCLOCK` partial** (below); **`65816` refused** ("Save states need the Game Boy's own CPU and sound chip") | all 17 supported | `GB`, `GBS` supported; **the 23 `NES:` sound units refused** with the same reason |
| Game Boy Color | supported | as above | supported | all 5 supported; `65816` refused | all 17 supported | `GB`, `GBS` supported; the 23 `NES:` units refused |
| Master System | supported | supported (existing Lite suite) | supported | `Z80` supported | all 19 supported | all 27 supported (`SMS`, `SMS-FM`, 25 `NES:` / `NES-FM:`) |
| Game Gear | supported | supported | supported | `Z80` supported | all 19 supported | all 24 supported |
| Genesis | supported | supported (Lite suite from C4a) | supported | `M68K` supported | all 19 supported | `MD` supported; **the 22 `NES:` units are partial** (below) |

**Partial** = the state loads and the game carries on with the same pictures for 20 frames, but the state the machine writes straight after the load is not the state it was given:
* *Genesis, 22 `NES:<chip>` sound units*: the first differing payload byte is byte 0 of 795,962. This is B4b's documented design ("the NES chip it drives is not state ... the audio is alive but not sample-identical"): the NES listener's part is written into the state and not restored. The Genesis machine itself is exact (pictures 20/20).
* *Game Boy, CPU `GB-SNESCLOCK`*: `BOARD_GB.dotAcc` (the fractional dot carry used when the CPU clock factor is not 1) is not in the Game Boy's state; first differing payload byte 10 of 25,605. Flagged to the lead as a task chip ("Save Game Boy dotAcc in the board state"), not fixed here (outside my domain).

**Refused** = the session says `SupportsState = false` with the reason and `SaveState` throws `NotSupportedException` (checked): Game Boy / Game Boy Color with the 65816 as CPU or a NES chip as the sound unit (the 46 + 2 refusals). The refusal text tells the user to switch the part back.

What is missing, for the lead: nothing on the SNES; the NES is a different mechanism (JSON); the Genesis and Game Boy items above; and **the plugin's SNES state in a real FL project** (see section 8).

## 8. On the merged tree

After `git merge refs/heads/main` (12 commits: c4b's project state, inputs' d-pad protection and the BrokenNes2 rename; clean, no conflicts) the Workshop builds and, on the merged tree: `--snesbench` goldens for Super Mario World and Zelda IDENTICAL, cputest-basic / cputest-full / spctest 0 failures, `--snes-state-test` groups refusals + units + program + factory 47 of 47, `--smstest --only sessionstate` 48 of 48. And the plugin:

* `UAT\certify.ps1 -Only plugin-host` (the Native AOT plugin DLL in the FruityHost stand-in for FL) **first failed one check of 50, `runaway-state`, for the SNES only**: c4b's test already switches by itself ("SNES games keep their state in a project" when the session reports support), but the test hooks that corrupt and read back a byte of work RAM had no SNES case (`PeekWorkRam` returned -1), so "the second corrupted byte came back as -1". **The state itself was not the problem.** The fix is three additive lines in `Plugin/BrokenNes2.Plugin/Game.cs` (`SnesSession` -> `Board.Wram`, 128 KB, in `WorkRamSize`, `PeekWorkRam`, `PokeWorkRam`): this is an edit in c4b's domain, named here for the lead. After it: **49 passed, 1 warning (QLQ's pitch, as before), 0 failed**, the SNES frozen (Runaway on) and running both come back with the corrupted bytes exactly as saved and the same continuation as the machine that never left.
* With real games named (`BROKENNES_TEST_GAMES` = Super Mario World, Star Fox, Super Mario Kart, Kirby Super Star; 10 s of play before the project, 5 s after): **49 passed, 0 failed** again, with the project sizes: Super Mario World 119 KB running / 208 KB frozen (the frozen one also carries the 64 KB sound snapshot), Star Fox 96 / 165 KB, Super Mario Kart 143 / 223 KB, Kirby Super Star 187 / 357 KB. The state itself is 100-190 KB; a project with several instances of one game keeps one copy of the machine.
* Not run: FL Studio itself (`plugin-fl`), `plugin-install`, `plugin-synth`.

## 9. Not tested (said, not hidden)

* The physical **F5 / F7 keys** and the **Save State... / Load State...** file dialogs of the desktop app (the HTTP API calls the same methods; UI Automation of modal dialogs hangs the harness by the notes).
* **Game Boy / Game Boy Color states in the Lite page** and the Master System / Game Gear / Genesis ones on this branch's AOT publish (they are covered by C4a's own Lite suite on its branch; here only the SNES was run in a browser).
* A SNES state **in a real FL project** (save and reopen in FL Studio): the plugin's own self test, including four real SNES games, was run on the merged tree (section 8); FL itself was not.
* Sound **by ear** after a load (the audio is compared sample for sample, and the NES-chip sound units are only checked alive).
* The **Super FX / SA-1 games' in-game saves** across a state load against the real hardware's behaviour (the state replaces the cartridge RAM with the saved one; the battery file on disk is not touched until the app next writes it).
* **Interlace** and **PAL**: the SNES core renders interlace progressive and has no PAL; the state format is `sfc-1`, NTSC (a PAL board would bump it).
* A state taken **inside a DMA / HDMA transfer or inside an instruction**: the board cannot be stopped there (so there is no such state to test).
* Star Fox's 3 KB/frame allocation (pre-existing) is not investigated.
* Super Mario RPG, Donkey Kong Country, Super Metroid, Street Fighter II Turbo and Contra III are in the round trip, field and size tests, but not in the mid-frame group or the UAT / Lite / app runs (those use the six games named).

## 10. Decisions, open items, next steps

* **Decision: the payload is raw fields, not a reflection dump** (the Genesis style): fast (0.1-0.3 ms), compact, and the reflection test, not the format, is what keeps it complete. The rule for the next person: *any new instance field of a SNES core that a running game can change must be written in that component's `*.State.cs`*; `--snes-state-test` names the field that was left out, and the exclusion list in the test (with reasons) is where a new scratch buffer or cache is declared.
* **Decision: the sound unit is part of the identity**, as on the Genesis and Game Boy: a state saved with `SFC` is refused by `HLE` / `NES:<chip>` and vice versa; the stand-in's state includes the real unit behind it.
* **Decision: the NES chip as the SNES's sound unit is a listener** (the SPC700 + S-DSP behind it is the machine); after a load it is re-based, not rebuilt.
* **Open (flagged):** the Game Boy `dotAcc` (task chip); Genesis NES-chip units are "partial" by B4b's design; `md-golden` Titan Overdrive 1 (PAL) frame 1007 fails on the base commit too; Star Fox allocates ~3 KB a frame on the base commit too.
* **Next:** (1) the lead can wire anything that wants a SNES state against `SupportsState` (the plugin already does); (2) a Lite page check of Game Boy states; (3) if corruption anchors want thousands of snapshots, a contract-level raw snapshot (`SaveStateRaw`) would cost 0.1-0.3 ms instead of 2-3 ms; (4) when a SNES PAL board or a new coprocessor arrives, add its fields to its `*.State.cs`, bump `SnesSession.StateFormat` and extend the roster; (5) register `--snes-state-test` and the `states` UAT suite in `certify.ps1` / a gate next to `sega-gate.ps1` (not done: those scripts are the lead's).

## Files

`Windows/NesEmulator/snes/*.State.cs` (12 new), `snes/SnesStateIo.cs`, `snes/ISnesCoprocessor.cs`, `snes/GSU_SFC.cs` + `snes/SA1_SFC.cs` (the "owns its RAM" flag), the eleven `partial` conversions; `systems/SnesSession.State.cs`, `systems/ConsoleSessions.cs` (partial; `PublishNativePicture`), `systems/ConsoleStates.cs`; `mix/ApuBridges.cs` (`Rebase`); `MainForm.SessionStates.cs`, `MainForm.SaveStates.cs`; `Workshop/SnesStateTest.cs`, `SnesFieldWalker.cs`, `SnesProgs.cs`, `StateAuditCli.cs`, `AllocProbeCli.cs`, `Program.cs`, `Sega/SessionStateSuite.cs`, `Sega/SessionTestCli.cs`; `UAT/cases/states.json`, `UAT/entrypoints/snes-state-smoke.ps1`, `UAT/lite/test-lite-snes-state.mjs`, `UAT/run-suite.ps1`, `UAT/README.md`; `docs/handoff-testing-guide.md`, `Workshop/README.md`, `Windows/webapi/README.md`.
