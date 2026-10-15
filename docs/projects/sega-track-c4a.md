# Sega track C4a: Lite for the three consoles, and speed (round 4)

Written 2026-10-15 at the end of the track. Branch `worktree-agent-af2b737cd5901fa8e`, cut from `main` e758833 (`main` had not moved when this was written, so the "merge main" step was a no-op and every number below is on the integrated tree). Plan: `sega-cores-plan.md`; rules: `sega-tracks-brief.md`. **Read "Open problems" first.**

The job: make the Master System, Game Gear and Genesis first-class in BrokenNes Lite (the browser build), and fast enough on the desktop and in WebAssembly, the way the SNES speed pass and the Lite integration did for the SNES and the Game Boy. Both halves were measured in a real browser window (headless Edge over the DevTools protocol, the AOT build), not argued.

## What exists

| Piece | Where | What it is |
|---|---|---|
| Lite integration | `Windows/NesEmulator/board/Emulator.Consoles.cs`, `Emulator.PublicApi.cs`, `Emulator.cs`, `NesController.cs`, `NesControllerModels.cs`, `systems/ConsoleSystems.cs`, `WebLite/Pages/Nes.razor(.css)`, `WebLite/wwwroot/lib/nesInterop.js` | Per-console touch layouts (`data-layout` sms / gg / md with the right buttons), `.zip` import and a visible refusal of `.7z` / RAR (by the file's bytes, not its name), the speed badge, the audio and picture crossing to JavaScript (raw PCM bytes, pixels converted a word at a time), the picture size and aspect for 256x192, 160x144, 320x224, 256x224 and the Genesis H32/H40 change while running, status text that says when a core swap restarted the game. |
| Lite test lab | `UAT/lite/litelab.mjs` | A Node (22+, no packages) DevTools-protocol lab: a tiny static server for a publish folder (`.wasm` served as `application/wasm`, no caching), headless Edge with a fresh profile per case, `page.eval`, `page.profile` (the CPU profiler, wasm self time named through the AOT symbol map), emulated touch and key input, process CPU time through `SystemInfo.getProcessInfo`. Hardened after losing runs to Edge dying after start-up (exit handler, timeouts, retry) and to timers keeping Node alive. |
| Lite functional suite | `UAT/lite/test-lite-sega.mjs`, `UAT/lite/synth-roms.mjs` | 12 groups (console, layout, rom, import, pickers, swap, saves, states, video, audio, control, speed), 109 checks. The programs that need something a commercial game cannot be made to do on demand are assembled by `synth-roms.mjs` (an SMS / GG Z80 program that adds one to its battery save on every run; a Genesis SRAM counter; Genesis programs that run H40, H32, and switch H32 to H40 while running). Games come from `--roms`, never from the repository. |
| Lite speed suite | `UAT/lite/bench-lite.mjs`, `profile-lite.mjs` | Frames per second per console and per path in the browser window. The page's own `FrameTick` is driven in a JavaScript loop under the same pad script as the desktop suite, timed with `performance.now()` and with the process's CPU seconds (more robust to a loaded machine than wall time), split by the page's profile hook into core / audio read / pixel conversion / crossing. `--twin name=0\|1` flips an optimisation twin inside the page (`Emulator.SetTwin`) so one build can be measured with it on and off. `profile-lite.mjs` prints the wasm functions by self time. |
| Desktop speed suite | `Workshop/Sega/SegaBenchCli.cs` (`--sega-bench`), `Workshop/Sega/golden/sega-bench-golden.json` | 22 cases over `<roms>\sms\|gg\|md`: stock cores, the YM2413, the Master System / Game Gear / Genesis on NES picture chips and NES sound chips. Per case: ms per frame, x real time, bytes allocated per frame, **a hash over every frame's pixels and a hash over every audio sample of 900 frames of scripted play** (with checkpoints every 250 frames, so a mismatch says roughly where), compared with a golden recorded on the unchanged build (`--update` re-records). `--ab NAME` runs an optimisation's on and off paths interleaved in one process and reports process CPU per frame for each, and fails if the two paths' hashes differ; `--state-every N` also hashes the Genesis sound unit's whole saved state every N frames in both paths (so a fast path must leave the *machine* in the same state, not only what is seen and heard). `--reference-paths` turns every optimisation off (re-record a golden on the reference paths after a merge that changes timing). The games come from `--roms` or `BROKENNES_SEGA_ROMS`; none are in the repository. |
| Entrypoint | `UAT/entrypoints/lite-sega.ps1` (row in `UAT/entrypoints/README.md`) | One command: publish (or `-Publish dir`), gather the games from the user's libraries, run the functional suite, `-Bench` adds the speed table, ends with one JSON line like the other entrypoints. |
| Bridges, faster | `NES.cs` (`RunIdleFrame`, `RunIdleToVblank`, `AlignIdleToVblank`), `mix/NesPictureSink.cs` (`IdleMode`) | The NES behind a picture bridge no longer runs two full NES frames (CPU, APU and PPU) per Sega frame. See "What made it faster". |
| Genesis Z80, faster | `md/MdSoundUnit.cs` (made `partial`, hooks), `md/MdSoundUnitLoops.cs` (new), `sega/Z80/Z80.State.cs` (`LoopMark`, `MatchesLoopMark`, `SkipLoop`) | The sound Z80's polling loops are counted instead of ticked: exact, with a twin (`MdSoundUnit.FastLoops`). |

## Lite: what is complete

Verified by `test-lite-sega.mjs` on the AOT publish of this branch's final code (109 of 109, below): the console dropdown lists NES, SNES, Game Boy, Game Boy Color and the three Sega consoles by name; choosing a console sets the layout; the layouts have exactly the right buttons (SMS: 1 2 PAUSE, Game Gear: 1 2 START, Genesis: A B C X Y Z MODE START and the pad), no two buttons overlap, every button is inside a 390 px screen; `?rom=` loads a game and the console follows the ROM; the picture is the right size and moves, sound reaches the page every frame at 44,100 Hz and the right amount per frame; the file input offers the Sega extensions and `.zip`, imports ROMs of all three consoles, unpacks a `.zip`, refuses a `.7z` (and a `.7z` renamed `.zip`) with a message that says why and what to do; the CPU / PPU / APU pickers list the catalog (the console's own chips first, then `NES:<chip>` entries, then the other consoles' chips labelled "From the ..."), and the NES pickers offer the Master System and Genesis chips; hot swapping through `TrySwapCore` (Master System APU to the FM unit, picture to a NES chip and back, in place, picture moving and sound unbroken; Genesis picture in place; a Genesis sound-unit swap says out loud that the game restarted); IndexedDB battery saves (`battery_v1:` keys) for all three consoles by a program that adds one to its save every run, across a page reload, plus the two EEPROM games (NBA Jam 24C02, Wonder Boy in Monster World X24C01) and Phantasy Star's cartridge RAM; Lite save states for the Master System and Game Gear (the next 60 frames are the same pictures after a load); the Genesis says in words that it has no save states yet; the Genesis 320x224 / 256x224 sizes and aspects, and a game that switches H32 to H40 while running; the PAL Genesis rate (50 frames a second, 887 samples a frame); pause, play and reset; the speed badge appears, with the rate the device manages, when a console cannot hold its rate.

**Not Lite-complete**
- Genesis save states: the Genesis sound unit cannot save one yet (B4b). The page says so; the test accepts that message and nothing else.
- Genesis CPU and APU hot swap: it restarts the game on the new core with a visible message (the Z80 sound unit and the 68000 cannot be exchanged under a running game). The picture (PPU) swap is in place.
- Lite session audio is mono for every console (existing design: the frame crossing mixes the stereo down and the browser resamples); the Sega consoles' stereo is not heard in Lite.
- `.7z` archives are refused (the browser has no 7z); unpack first or use `.zip`.
- Sound through a loudspeaker, a real phone's touch, and PAL Master System games are not tested.

## Speed

### Method
1. **Profile first.** `dotnet-trace` on the desktop (Genesis Sonic, Master System on `NES:FIXS`, stock Master System), the CDP CPU profiler on the Lite AOT build (wasm functions named through `dotnet.native.js.symbols`).
2. **The SNES method for every change that was kept.** The old path stays behind a static switch (a twin). `--sega-bench` records picture and sound hashes of 22 cases on the **unchanged** build (the golden); a change must reproduce them **identically**, and `--ab NAME` interleaves both paths in one process and reports process CPU per frame for each (a machine shared with other agents makes any comparison across runs meaningless; only same-window interleaving counts). `--state-every` compares the whole machine state, not only what is seen and heard.
3. **Lite is measured in the browser**, the AOT build, headless Edge, one fresh profile per case, the same pad script as the desktop, 200 frames of warm-up, 400 to 600 measured. Reported as wall time per `FrameTick` and as process CPU time per frame (the latter is the number to trust on a loaded machine). Base and final builds are measured back to back for each case.
4. **Zero allocation per frame** is a standing requirement: the suite prints bytes per frame. All stock paths allocate 0 to 1 bytes a frame (the 1 is the suite's own counter); the bridged paths still allocate (see Open problems).

### Desktop: before and after
Baseline = the Workshop build of `main` e758833; final = this branch. The two executables ran alternately over the same 22 cases (`--repeat 5` each, 2 rounds, best of the rounds' medians), one core, a shared machine. **Read the "change" column as a ratio with about +-25 % of noise**: the Master System and Game Gear stock rows run code this track did not touch (their change is noise by construction).

| Case | Picture | base ms | base x real | final ms | final x real | change |
|---|---|---:|---:|---:|---:|---:|
| sms-alexkidd | 256x192 | 1.254 | 13.3x | 0.974 | 17.1x | 1.29x (noise) |
| sms-fantasyzone | 256x192 | 1.273 | 13.1x | 1.061 | 15.7x | 1.20x (noise) |
| sms-shinobi | 256x192 | 1.344 | 12.4x | 1.190 | 14.0x | 1.13x (noise) |
| sms-sonic | 256x192 | 1.194 | 14.0x | 1.067 | 15.6x | 1.12x (noise) |
| sms-alexkidd + YM2413 | 256x192 | 1.471 | 11.3x | 1.325 | 12.6x | 1.11x (noise) |
| sms-alexkidd, PSG on APU_FIX | 256x192 | 1.368 | 12.2x | 1.173 | 14.2x | 1.17x (noise) |
| **sms-alexkidd, picture on NES:FIXS** | 256x240 | 9.012 | 1.9x | 3.068 | **5.4x** | **2.94x** |
| **sms-alexkidd, picture on NES:FIX** | 256x240 | 10.195 | 1.6x | 3.349 | **5.0x** | **3.04x** |
| gg-columns | 160x144 | 0.947 | 17.6x | 0.937 | 17.8x | 1.01x (noise) |
| gg-sonic | 160x144 | 0.857 | 19.5x | 0.899 | 18.5x | 0.95x (noise) |
| gg-sonicchaos | 160x144 | 1.003 | 16.6x | 1.003 | 16.6x | 1.00x (noise) |
| gg-shinobi2 | 160x144 | 0.920 | 18.1x | 1.030 | 16.2x | 0.89x (noise) |
| **gg-sonic, picture on NES:FIXS** | 256x240 | 7.169 | 2.3x | 3.939 | **4.2x** | **1.82x** |
| md-sonic | 320x224 | 4.466 | 3.7x | 4.011 | 4.2x | 1.11x |
| md-sonic2 | 320x224 | 3.891 | 4.3x | 3.833 | 4.4x | 1.02x |
| md-streetsofrage | 320x224 | 4.111 | 4.1x | 3.681 | 4.5x | 1.12x |
| md-goldenaxe | 320x224 | 4.162 | 4.0x | 4.268 | 3.9x | 0.98x |
| md-gunstar (PAL, 50 Hz) | 320x224 | 4.939 | 4.1x | 3.557 | 5.7x | 1.39x |
| md-comixzone | 320x224 | 4.572 | 3.7x | 4.741 | 3.5x | 0.96x |
| md-sonic, sound on APU_FIX | 320x224 | 4.767 | 3.5x | 4.470 | 3.7x | 1.07x |
| **md-sonic, picture on NES:FIXS** | 256x240 | 12.200 | 1.4x | 7.904 | **2.1x** | **1.54x** |
| **md-sonic, picture on NES:FIX** | 256x240 | 15.869 | 1.1x | 7.596 | **2.2x** | **2.09x** |

The same hardware in a loaded window (other agents compiling and running oracles) gave Genesis stock 1.5x to 2.7x and Genesis on `NES:FIXS` / `NES:FIX` 1.2x (a single `--repeat 1` pass of the final golden check), so the desktop's worst case is the Genesis on a NES picture chip at about 1.2x. Allocation: every stock row allocates 0 to 1 bytes a frame; the bridged rows 183 KB to 378 KB a frame (unchanged by this track).

**The two optimisations, measured in-process (twin on against off, process CPU per frame, golden IDENTICAL and, for `md-loop`, the saved machine state identical every 10 frames):**

| Twin | Case | off ms | on ms | gain |
|---|---|---:|---:|---:|
| `idle-frame` (picture bridges) | sms-alexkidd on NES:FIXS | 9.844 | 4.236 | +132 % |
| | sms-alexkidd on NES:FIX | 12.465 | 4.566 | +173 % |
| | gg-sonic on NES:FIXS | 11.406 | 5.052 | +126 % |
| | md-sonic on NES:FIXS | 17.691 | 11.267 | +57 % |
| | md-sonic on NES:FIX | 18.889 | 10.521 | +80 % |
| `md-loop` (Genesis sound Z80) | md-sonic | 4.236 | 3.941 | +7.5 % |
| | md-sonic2 | 4.062 | 3.524 | +15 % |
| | md-streetsofrage | 4.601 | 4.062 | +13 % |
| | md-goldenaxe | 4.288 | 4.306 | 0 |
| | md-gunstar | 4.549 | 3.542 | +28 % |
| | md-comixzone | 4.340 | 4.497 | 0 (noise) |
| | md-sonic on NES:FIXS / NES:FIX | 10.833 / 10.503 | 10.278 / 10.174 | +5 % / +3 % |

Golden Axe and Comix Zone gain nothing from the Z80 loops: their sound drivers do not wait in a loop that can be counted (they poll the YM2612 status or write while they wait).

### Lite: before and after (AOT build in a headless Edge window, x real time = 60 frames a second)
Baseline = the AOT publish of `main` e758833 (`lite-base`), final = `lite-p7` (this branch's final code), each case measured with both builds back to back, 600 frames after 200 of warm-up. "cpu" is the browser process's CPU time per frame; "wall" is the time per `FrameTick` call including the browser's event loop. Other agents were using the machine (less than in my earlier runs; those are mentioned below).

| Case | Picture | base wall ms | base x (wall / cpu) | final wall ms | final x (wall / cpu) | core ms | crossing ms |
|---|---|---:|---:|---:|---:|---:|---:|
| sms-alexkidd | 256x192 | 5.11 | 3.26 / 3.29 | 4.74 | 3.52 / 3.99 | 2.94 | 1.57 |
| sms-fantasyzone | 256x192 | 5.18 | 3.22 / 3.14 | 4.76 | 3.50 / 3.76 | 3.15 | 1.39 |
| sms-sonic | 256x192 | 5.00 | 3.33 / 3.44 | 4.78 | 3.49 / 3.84 | 3.10 | 1.45 |
| sms-alexkidd + YM2413 | 256x192 | 7.33 | 2.27 / 2.69 | 5.72 | 2.92 / 3.09 | 4.48 | 0.98 |
| sms-alexkidd, PSG on APU_FIX | 256x192 | 6.24 | 2.67 / 2.79 | 5.79 | 2.88 / 3.14 | 4.52 | 1.04 |
| **sms-alexkidd, picture on NES:FIXS** | 256x240 | 26.40 | **0.63** / 0.62 | 12.31 | **1.35** / 1.35 | 10.99 | 1.01 |
| **sms-alexkidd, picture on NES:FIX** | 256x240 | 28.67 | **0.58** / 0.58 | 10.47 | **1.59** / 1.60 | 9.32 | 0.89 |
| gg-columns | 160x144 | 4.84 | 3.44 / 3.61 | 5.13 | 3.25 / 3.77 | 3.52 | 1.47 |
| gg-sonic | 160x144 | 4.93 | 3.38 / 3.49 | 4.79 | 3.48 / 4.19 | 2.98 | 1.69 |
| **gg-sonic, picture on NES:FIXS** | 256x240 | 24.33 | **0.69** / 0.69 | 11.74 | **1.42** / 1.42 | 10.58 | 0.91 |
| md-sonic | 320x224 | 13.72 | 1.21 / 1.22 | 10.27 | **1.62** / 1.66 | 9.17 | 0.86 |
| md-sonic2 | 320x224 | 11.15 | 1.49 / 1.52 | 9.79 | **1.70** / 1.74 | 8.72 | 0.83 |
| md-streetsofrage | 320x224 | 12.07 | 1.38 / 1.37 | 12.27 | 1.36 / 1.50 | 10.66 | 1.28 |
| md-gunstar (PAL, 50 Hz) | 320x224 | 12.13 | 1.37 / 1.35 (of 50: 1.65 / 1.62) | 9.57 | **1.74** / 1.79 (of 50: 2.09 / 2.15) | 8.33 | 0.86 |
| md-sonic, sound on APU_FIX | 320x224 | 12.82 | 1.30 / 1.28 | 12.44 | 1.34 / 1.33 | 11.28 | 0.89 |
| **md-sonic, picture on NES:FIXS** | 256x240 | 35.13 | **0.47** / 0.47 | 21.83 | **0.76** / 0.76 | 20.78 | 0.85 |

Earlier windows, same builds or performance-identical ones, for the spread: in a heavily loaded window (the first complete table, `lite-p4`) the Genesis stock games were 1.31x to 1.46x by CPU and the bridged Master System 1.40x; in a quiet window with the Z80-loop twin flipped inside one build (`bench-lite.mjs --twin md-loop=0|1`, 300 frames) (two rounds, off then on) Sonic ran 1.46x / 1.64x off and 1.99x / 1.79x on, Streets of Rage 1.34x / 1.59x off and 1.53x / 2.02x on, Gunstar 1.59x / 1.49x off and 1.96x / 1.98x on. **So the stock Genesis in the browser is 1.3x to 2.1x real time depending on how busy the machine is; its median is about 1.6x.** The crossing plumbing (`payload` twin): Master System Sonic 4.40 / 5.05 ms CPU per frame with the old JSON-array audio path against 3.84 / 3.74 with raw PCM bytes (about 0.5 to 1.3 ms a frame); on the Genesis it is within noise (9.27 / 9.92 against 9.63 / 9.45).

### What made it faster (kept: each with a twin, golden IDENTICAL)

1. **The picture bridges' NES (`NesPictureSink`).** Every Sega frame on a NES picture chip used to run **two full NES frames** (CPU, APU and PPU; the first only latched the new picture, the second drew it) around an idle CPU loop (`JMP $C000`). Now the sink steps *only the PPU*, a scanline at a time, from the vertical blank through one whole picture back to the blank (`NES.RunIdleToVblank`), then programs the PPU in the blank so the next picture is drawn whole from its first line: one frame of PPU work, no CPU, no APU. Output is bit for bit the same (22-case golden; `--picture-bridge-test` 187/187; the NES golden suite unchanged). An intermediate form (`IdleMode = 1`, two idle frames without the CPU) is kept as a third path (`--ab idle-frame-1`). Gain: table above (desktop 1.6x to 2.7x of CPU, the Master System and Game Gear on NES picture chips 2.2x to 2.7x in the browser and now real time with margin).
2. **The Genesis sound Z80's polling loops (`MdSoundUnit.FastLoops`).** The Genesis sound Z80 spends most of its time in tiny loops that wait for the 68000 to write a flag (`LD A,(flag); OR A; JR Z,loop`). The unit detects such a loop exactly (the same PC and the same complete CPU state at an instruction boundary, no write, no port, no read outside the Z80's own RAM, no interrupt, no bus request or reset in the turn), records one turn, and when the same turn would repeat it advances the Z80 by the number of whole turns it provably repeats, with the cycle count, the refresh register, the time and the whole state identical to ticking every T-state (`Z80.SkipLoop`). The Z80 is brought up to date (the whole turns skipped, the rest of the last one ticked for real) the moment anything can change the answer: a 68000 access to the Z80's RAM, an interrupt, a bus request or release, a reset, a save state, a tool reading its state. Gain: table above (0 to 28 % of a Genesis frame by game; 8 to 27 % in the browser).
3. **Lite plumbing.** `FramePayload` carries the audio as raw bytes (`pcm`, a `Float32Array` on the JavaScript side, one exact-size buffer per sample count on the C# side) and the pixels are converted a word at a time; `Emulator.SetTwin("payload")` keeps the old path to compare.

### Where the time goes now (profile of the final Lite build; the VDP rows belong to B4a and were not touched)

Genesis, Sonic 1, `lite-p7`, 300 frames sampled, 10.5 ms a frame (self time, wasm):

| Share | What |
|---:|---|
| 15.1 % | `MdSoundUnit.RunInner` (the sound unit's catch-up: the Z80 tick loop, the loop detector, the clock grid) |
| 5.4 % | `BOARD_MD.SyncSound` (the 68000 side of that catch-up, called on every bus access) |
| 18.5 % | the VDP: `RunTo` 6.9, `DrawScrollRun` 5.2, `Compose` 4.0, `FinishLineRender` 2.0, `NextFreeSlot` 0.4 |
| 9.9 % | the bus: `BOARD_MD.BusRead` 5.9, `ReadWord` 2.6, `RunFrame` 1.4 |
| 17 % | the 68000 instruction engine (`MdBus.Step` 5.1, `Execute` 4.2, `Dbcc` 2.9, `Prefetch`, `Bcc`, `Move`, ...) |
| 7.2 % | the sound chips: `RunFm` 3.0, `Ym2612.ClockEngine` 2.3, `GenerateSample` 1.6, `ClockEnvelope` 0.3 |
| 2.7 % | the PSG (`Sn76489.Tick`, `SetOutput`, `ReadSamples`) |
| 1.6 % | the Z80's own micro-ops (`Z80.Tick`) |
| 2.5 % | `RunSessionFrame` (audio mix-down and pixel conversion) |

Master System, Sonic: 4.87 ms a frame of which 25.9 % is the idle wait for the JavaScript round trip, `SmsBus.Tick` (Z80) 16.4 %, `RenderMode4` 9.8 %, `BOARD_SMS.RunLine` 7.2 %. The core is about 3 ms; the crossing is 1.4 to 1.7 ms and is not an emulator cost.

Hot spots for other tracks (reported, not edited):
- **B4a (VDP).** `MdVdp.RunTo` is called two to three times per 68000 bus access and is 6.9 % self time; a "nothing due" early exit before the call, or a cached next-event time, would make the bus path cheaper. VDP drawing (scroll runs, compose) is another 11 %: a frame the host will not present (frame skip under load) could skip drawing; only the VDP's owner can make that exact.
- **B4b (sound unit, 20.5 % of the frame).** `MdSoundUnit.RunInner` (15.1 % self) and `BOARD_MD.SyncSound` (5.4 %) together are the largest item of the Genesis frame, and the 68000 side calls `SyncSound` on every bus access. Syncing only when the 68000 touches something the Z80, the YM or the PSG can see (the Z80 RAM, their ports, the bus request, the interrupt line, the bank register) would remove most calls; it can change *when* chip events are time-stamped, so it must be proven with the same machinery (`--sega-bench --ab`, golden, `--state-every`). I did not try it (the Z80's clock phase depends on the path, see "Failed").
- **C4b (bridges).** The translators `SmsToNes` / `MdToNes` allocate their layer, tile and sprite arrays every frame (183 to 378 KB a frame): the bridged paths are the only ones that allocate. Pool them. The Genesis on a NES picture chip is 0.76x in the browser: the Genesis core (9 ms) plus the translator and the NES PPU (about 12 ms).

### Failed or discarded (so nobody repeats them)
- **A "nothing due" quick path in `MdSoundUnit`** (twin `md-sync`): the short path almost never applies because the 68000's calls nearly always have Z80 ticks due; +-2 % (noise) on six cases. Reverted. Merging the sound unit's catch-up calls is not an exactness-neutral change either: the Z80's clock phase (`z80Master`) depends on the path, so merged calls would change time stamps.
- **Replacing the divisions in the sound clock grid with a reciprocal multiply, and a lazy mixer advance**: about 0 gain. Reverted.
- **Skipping the Z80's ticks while it is halted** (Master System and Game Gear, twin `sms-halt`): the games do not halt it; the twin measured 0 to 4 % slower, within noise. Reverted.
- **Upper-bound experiments** (`exp-psg`, `exp-fm`, `exp-z80`, `exp-notick`, `exp-noloop`; deliberately not exact, the golden flagged each of them): switching the PSG off would save at most 16 % of a Genesis frame, the FM chip 8 %, the Z80 29 %, skipping only the Z80's loop ticks 28 %. This is what told me the Z80 loops were the one big exact target; the exact version recovers 8 to 28 % by game, because loops that poll the YM2612 status or write anything are not parkable.
- **Division-free calls and dropping a try/finally in the sound unit's call path**: inconclusive in a same-build twin (within noise), removed rather than kept as an unproven switch.
- **Parking, first version**: it recorded a loop turn while the 68000 was writing the Z80's RAM; the state-hash twin (`--state-every`) showed the machine diverging after frame 820 of Gunstar (audio differed). Fixed (any 68000 write to the Z80's RAM spoils the recorded turn and unparks; bus request, interrupt and reset end a loop); the final version is state-identical every 10 frames.

## Verification

All run on this branch's final code (`main` = e758833 unchanged, so this is also the integrated tree), with the Workshop built from it (`ws`), the AOT Lite publish `lite-p7`, and a desktop app built from it.

| Verifier | Result |
|---|---|
| `UAT/entrypoints/lite-sega.ps1 -Publish lite-p7` (12 groups, real Edge) | **109 passed, 0 failed** (285 s) |
| `--sega-bench --suite --golden Workshop/Sega/golden/sega-bench-golden.json` (22 cases, 900 frames each, picture and sound hashes) | **22 of 22 IDENTICAL** to the golden recorded on the unchanged build; bytes per frame 0 to 1 on every stock path |
| `--sega-bench ... --ab md-loop --state-every 10` (9 Genesis cases, 6 runs each, on and off alternating) | golden IDENTICAL, picture, sound **and saved machine state** identical between the two paths in all 9; no `!!` line |
| `--sega-bench ... --ab idle-frame` (7 bridged cases, 4 runs each) | golden IDENTICAL in all 7 |
| `--z80test --selftest` | 57 / 57 |
| `--md-test` | 144 / 144 |
| `--md-golden` | 75 frames match, 0 changed (27,109 frames, 124 s) |
| `--sega-contracts-test` | 80 / 80 |
| `--sega-test` | 65 / 65 |
| `--sega-chips-test` | 34 / 34 |
| `--sega-stems-test` | 38 / 38 |
| `--sega-bridge-test` | 320 / 320 |
| `--ym-oracle-test` | 43 / 43 |
| `--smstest --roms <sms dir>` | 301 pass, 0 fail, 18 skip (the 18 skipped are the roster golden: my game folder does not have the roster's files) |
| `--md-cart-test` | 47 / 47 |
| `--picture-bridge-test --sms-roms --gg-roms --md-roms` | 187 / 187 |
| NES golden suite (`UAT/run-suite.ps1 -Exe <ws exe>`, main's ROM roots) | 46 pass, 0 fail, 16 skip, 3 info (identical to the baseline) |
| Desktop app build (`Windows/BrokenNes.Windows.csproj`) | 0 errors |
| `UAT/entrypoints/desktop-smoke.ps1 -Exe <built app>` | 9 / 9 |
| `UAT/entrypoints/sega-preview-smoke.ps1 -Exe <built app>` | 47 pass, 0 fail |
| Lite AOT publish | exit 0 |

Not run: the plugin self-test (this track does not touch the plugin; its picture and sound hosts are the unchanged `--sega-bridge-test` and `--picture-bridge-test`), and anything that needs a sound device (none on the development machine).

## Proofs that the verifiers can fail
Bugs were injected into a copy of the source, built into a separate folder (`ws-inj`), run, and reverted with `git checkout`:

| Verifier | Injected bug | Result |
|---|---|---|
| `--sega-bench` golden | one hex digit of a golden audio hash changed in a copy of the golden | `golden: DIFFERENT (... audio differs)`, exit 1 |
| `--sega-bench` golden (the idle-frame path) | `RunIdleToVblank` stops at line 100 of the picture instead of the vertical blank | `golden: DIFFERENT (first differing checkpoint: frame 250; video differs, audio same)`, exit 1 |
| `--sega-bench --ab md-loop --state-every 10` | the loop skip adds one too many to the Z80's refresh counter | **picture and sound hashes IDENTICAL, golden IDENTICAL**, but `!! first differing state after frame 10` and `hashes differ` between the two paths, exit 1. This is why the twin hashes the machine state: the golden alone could not see this bug. |
| `--sega-bench --ab md-loop` | the 68000's write to the Z80's RAM no longer marks the recorded turn impure; the interrupt no longer ends a loop | **not detected** on Gunstar. These two are belt and braces: the bus request (a 68000 must request the bus before touching the Z80's RAM) and the Z80's own polling already cover the same hazard in the games checked. Recorded here so nobody believes this twin proves those two lines necessary. |
| Lite functional suite | run against the AOT publish of `main` (`lite-base`), groups console, layout, import, pickers, speed | 36 pass, **9 fail**: the SMS, GG and Genesis layouts show the NES buttons "B A SELECT START" (6), both `.7z` refusals (2), the speed badge (1) |
| Lite functional suite | during development | failed on faults it then led me to fix: the Genesis pacing check on a loaded machine (the page now shows its "Slow: ..." badge and the test accepts a slow page only if it says so), the console choice overwritten by app start-up, and a speed-group test I had written wrong (it restarted the page loop with no ref, so nothing ran); chasing that last one showed the badge counted calls instead of console frames while fast-forwarding, fixed in `Emulator.Consoles.cs` (it now reads "Slow: 109 of 180 frames a second") |
| The earlier experiment runs | `exp-psg`, `exp-fm`, `exp-z80`, `exp-notick`, `exp-noloop` | each flagged `hashes differ` by the golden machinery (they are not exact on purpose) |

## Decisions
1. **Twins, not rewrites.** Every optimisation keeps the old path behind a static switch, registered in `SegaBenchCli.AbSwitches` and, for the browser, in `Emulator.SetTwin`. The switches stay on by default; the old path is the reference the golden was recorded on.
2. **CPU time, not wall time**, for the Lite numbers that decide anything, and interleaving for every comparison: the machine is shared.
3. **The picture bridges' NES is stepped, not "run".** The sink's NES has no program worth running; stepping the PPU alone from the blank is exact (the golden says so frame by frame) and is the bulk of the bridge speed-up. A PPU that has no frame clock falls back to the original two frames.
4. **Loop parking is exact or it does not park.** The match is on the complete CPU state (all registers except the refresh counter's low 7 bits, which are advanced arithmetically; interrupt flags; the pending-prefix state); anything the 68000 or the bus can change ends it. Z80 loops that poll the YM status register are left ticking (their answer depends on the chip's clock).
5. **No silent fallbacks in the UI.** A device that cannot hold the console's rate shows "Slow: X of Y frames a second on this device" (counting console frames, also while fast-forwarding; a hidden tab does not trigger it). A refused `.7z` says so; a Genesis state or core swap that cannot happen says so.
6. **Binary audio across the interop boundary**, with the old JSON path kept only as a twin for comparison.
7. **`--sega-bench` needs `--roms`** (or `BROKENNES_SEGA_ROMS`): no path of mine is baked into a committed tool.

## Open problems
1. **Genesis stock in Lite is borderline against the 1.5x bar.** 1.3x to 2.1x by CPU time depending on how busy the machine is (median about 1.6x). Real time (1.0x) is met in every window I measured. The remaining cost is the sound unit's catch-up (20 %), the VDP (18 %), the 68000 (17 %) and the bus (10 %); see the hot spots.
2. **The Genesis on NES picture chips is not real time in the browser (0.76x, best case).** Not usable as is. The desktop runs it at 1.2x (loaded) to 2.2x. The page shows its "Slow: ..." badge when it runs slow; the honest label is "Genesis on a NES picture chip: desktop only for now". Genesis sound on a NES sound chip is 1.3x (real time, no margin).
3. **The Master System / Game Gear on NES picture chips are real time with 1.35x to 1.6x** in the browser (stock is 3.3x to 4.2x).
4. **Desktop pacing could not be measured for underruns** (the development machine has no audio playback device). It is argued from throughput: every stock path runs 3.5x to 19.5x real time on one core in a quiet window (1.5x at the worst in a loaded one), every bridged path at least 2.1x in a quiet window and 1.2x in a loaded one; the Genesis on a NES picture chip is the only desktop path near the line.
5. **Z80 loop parking for the Master System / Game Gear** (A4's files): the design in `MdSoundUnitLoops.cs` is ready to port, but the Master System Z80 runs at 12x to 19x real time already; not needed for speed. The `sms-halt` experiment showed nothing to gain from HALT.
6. **Lite allocation per frame is zero on stock paths, not on the bridges** (183 to 378 KB a frame).
7. **All Lite numbers were taken while other agents were using the machine.** Re-run on a quiet machine before quoting them outside this record.
8. **Expected merge conflict with B4b** (their branch at the time of writing): `MdSoundUnit.LoadState`. Keep their new lines (`fmLeft.LoadState` ... `Z80Ticks`) **and** my `ResetLoops();` before them; `SaveState` must still begin with `Unpark();` (a parked Z80 must be brought up to date before its state is written). Their `GrantLatencyTicks` bus grant should be compatible (a bus request calls `LoopBreak()` before the grant logic), but I have not run the two together. After merging, their timing changes the golden: re-record it on the reference paths (`--sega-bench --suite --roms <dir> --reference-paths --golden <file> --update`), then check `--sega-bench --suite --ab md-loop --state-every 10 --golden <file>` is IDENTICAL for the Genesis cases.

## Next steps
1. **B4b**: look at the sound unit's catch-up (20 % of a Genesis frame), as above; implement Genesis save states, then flip the Lite test's accepted message (the Master System / Game Gear checks are the template).
2. **B4a**: a "nothing due" fast path for `MdVdp.RunTo`; then a frame-skip render path. Together I expect the browser Genesis to clear 1.5x in a loaded window too.
3. **C4b**: pool the translators' per-frame arrays (`SmsToNes.*`, `MdToNes.*`), re-measure the Genesis on a NES picture chip in Lite; wire `lite-sega.ps1` into the one-command certification run.
4. Re-run `lite-sega.ps1 -Bench` and `--sega-bench --suite --repeat 5` on a quiet machine and replace the tables above.
5. Port the loop parking to the Master System Z80 only if it ever matters.

## Shared files touched (all small and additive)
`Windows/NesEmulator/board/Emulator.Consoles.cs`, `Emulator.PublicApi.cs`, `Emulator.cs`, `NesController.cs`, `NesControllerModels.cs`, `NES.cs` (idle stepping, additive), `systems/ConsoleSystems.cs` (`RomDetect.IsUnsupportedArchive`), `mix/NesPictureSink.cs` (`IdleMode`), `md/MdSoundUnit.cs` (made `partial`, loop hooks) + new `md/MdSoundUnitLoops.cs`, `sega/Z80/Z80.State.cs` (`LoopMark`, `MatchesLoopMark`, `SkipLoop`; additive), `Workshop/Program.cs` (one route line, `--sega-bench`), `WebLite/Pages/Nes.razor(.css)`, `WebLite/wwwroot/lib/nesInterop.js`, `UAT/entrypoints/README.md` (one row). New: `Workshop/Sega/SegaBenchCli.cs`, `Workshop/Sega/golden/sega-bench-golden.json`, `UAT/lite/*`, `UAT/entrypoints/lite-sega.ps1`. `UAT/certify.ps1` was not changed (C4b wires the new entrypoint into the one-command run). No ROM, firmware or archive is in the repository.

## Reproduce
```
# desktop speed + golden (the games: <roms>\sms\*.sms, gg\*.gg, md\*.bin from your libraries)
BrokenNes.Workshop.exe --sega-bench --suite --roms <roms> [--only md-sonic] [--repeat 5] [--ab md-loop|idle-frame|idle-frame-1] [--state-every 10] [--json out.json]
BrokenNes.Workshop.exe --sega-bench --suite --roms <roms> --golden Workshop\Sega\golden\sega-bench-golden.json     # exit 1 on any hash change
# Lite: publish AOT, functional suite, speed table
dotnet publish WebLite\BrokenNes.Lite.csproj -c Release -p:EnableWasmAot=true -p:WasmEmitSymbolMap=true -o <dir>
pwsh -File UAT\entrypoints\lite-sega.ps1 -Publish <dir> -Roms <roms> [-Only rom,saves] [-Bench]
node UAT\lite\bench-lite.mjs --publish <dir> --roms <roms> [--cases md-sonic] [--frames 600] [--twin md-loop=0] [--json out.json]
node UAT\lite\profile-lite.mjs --publish <dir> --roms <roms> --rom md/<file> --symbols WebLite\obj\Release\net10.0\wasm\for-publish\dotnet.native.js.symbols
```
