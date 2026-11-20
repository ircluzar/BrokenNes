# cor64 compatibility harness

Tooling for certifying games one at a time, by many agents in parallel. Every run writes only into its own folder;
there is no shared mutable state apart from the ROM cache (filled atomically) and the files you edit on purpose
(`scripts/<id>.txt`, `judgements.tsv`, `known_ucode.tsv`, `status.json`).

```
compat/
  games.tsv           the 30 games: id, tier, archive under X:/EMULATION/Nintendo 64, why it is on the list
  scripts/<id>.txt    pad script per game (starts as a copy of scripts/_boot.txt)
  known_ucode.tsv     RSP microcodes known to work (text hash -> name)
  judgements.tsv      verdicts from people/agents who looked at the screenshots (see below)
  status.json         one row per game, written by tools/summary.py
  roms/               extracted ROMs, .z64 byte order (git-ignored)
  results/<id>/<ts>/  one folder per run: report.json, *.png, *.state, probe.log, script.txt, header.txt (git-ignored)
  tools/get_rom.sh    <id>: extract/copy + byte-order normalise into roms/<id>.z64, print header (name, code, CIC, size)
  tools/run_game.sh   <id> [script]: one scripted run
  tools/run_all.sh    [jobs] [ids...]: run_game.sh over the list, a few at a time
  tools/summary.py    latest run of every game + judgements -> status.json
```

## Running a game

```
BUILD=/c/.../lanes/<lane>/cor64/bin  COMPAT_TWICE=1  compat/tools/run_game.sh mk64
```

- `BUILD` is the folder holding `SessionProbe.exe` (default: `cor64/bin` of this checkout). A lane tests its own build
  by passing its own `bin` (build it with `./build_cer64.sh`). Do not rebuild a `bin` while runs use it.
- `COMPAT_TWICE=1` reruns the script in a fresh session and compares every checkpoint (determinism).
- `COR64_RDP=software|parallel` picks the renderer as for the probe. `TIMEOUT` (s, default 1800), `TAG`, `RESULTS`.
- Several runs at once are fine; keep it to 3 when a bench may be running (machine load affects bench numbers, not
  results, which are deterministic).
- The probe itself: `SessionProbe <rom> compat <script.txt> <outdir>`. It changes its working directory to `<outdir>`, so
  core debug files (`telemetry/`, `DmaDumps/`) land there. `run_game.sh` sets `COR64_COMPAT_LOG=1`.

## Script language (one command per line, `#` comments; frames are VI fields, 60/s NTSC, 50/s PAL)

| command | effect |
|---|---|
| `run N` (`frames`, `wait`) | run N frames with the current held buttons and stick |
| `seconds S` | run S seconds |
| `press BTN [on=5] [off=5]` | hold BTN on top of the held set for `on` frames, then `off` frames without it |
| `pulse BTN [N=60] [period=10]` | N frames, BTN down for the first half of every period (repeated presses) |
| `hold BTN` / `release [BTN]` | add to / remove from the held set (`release` alone clears it) |
| `stick X Y` | analog stick, -128..127 (80 = full tilt on a real pad); stays until changed |
| `screenshot NAME` | checkpoint + `NAME.png` |
| `checkpoint NAME` | record video hash, audio hash and RMS since the previous checkpoint, blank-screen check, frame times |
| `savestate NAME` / `loadstate NAME` | write / read `NAME.state` in the run folder |
| `reset` | power cycle (keeps the battery save) |
| `poke ADDR HEX` | write bytes into RDRAM between frames, big-endian as the game sees them (`poke 800F89D3 1D`; KSEG0/1 or physical addresses). A cheat: list every poke in the game's `judgements.tsv` line |
| `pokeeach N ADDR HEX` | write the bytes before each of the next N frames (holds a value, e.g. a shield), running those N frames |
| `pokehold ADDR HEX` / `pokeoff [ADDR]` | write the bytes now and before every following frame, whatever command runs it (`pulse`, `press`...), until `pokeoff` (all, or that address) |
| `peek ADDR [N=4]` | script development: print N bytes of RAM to probe.log (`PEEK addr frame=.. hex`) |

Buttons: `A B Z START L R CU CD CL CR DU DD DL DR`, combined with `+` (`press A+B`). Controller 1 only.

## report.json

- `title`, `gameId` (SHA-1 of the ROM), `cic`, `header` (name, game code, byte order), `framesRun`, `wallMs`
- `frameTime`: avg/p50/p99/max ms per frame and `realtimePercent` (includes JIT warm-up and machine load: a hint only;
  the certified speed figure comes from `climb/bench.sh`-style measurement)
- `checkpoints[]`: `video` (hash of every frame so far), `frameHash` (this picture), `audio` (hash of all samples so far),
  `rms`/`peak`/`samples` (since the previous checkpoint), `solid` (whole picture one colour), `dominantFraction`,
  `dominantColor`, size, `msAvg`/`msMax`, `rdramHighNonZero` (words used in the 4-8 MB Expansion Pak area)
- `firstChanceExceptions[]`: type @ throwing method, count, message, first stack
- `unimplemented[]` (COR64_COMPAT_LOG=1; logging only, never changes emulation): key, count, first CPU tick, detail.
  Keys: `save:sram-or-flash-dma-*`, `save:flashram-dma-*`, `save:*-cpu` (CPU access to 0x08000000, e.g. FlashRAM
  status/ID), `save:eeprom16k` (EEPROM block > 63: only 4 Kbit is emulated), `pak:controller-pak-*`, `cart:rtc`,
  `cart:64dd-*`, `cart:dom1-addr3`, `cart:rom-dma-write`, `pi:dma-odd-address-*`, `joy:unknown-cmd`, `cpu:invalid-op`,
  `cpu:reserved-instruction`, `rsp:cop0-*-unknown`, `rsp:task-type-other`
  `cpu:tlb-exception` (lane session_leak, 2026-11-13; `COR64_TLBKEY_HANDLED=0` restores the old rule): only a TLB
  exception the game does NOT handle. Games that page code or data through the TLB (naboo, indy, conker) take
  thousands of refills, and their handler ERETs back to the faulting instruction: that is normal behaviour and is not
  logged. A TLB exception is logged when no ERET returns to its faulting EPC within the next 4096 ERETs (libultra's
  fault path parks the faulting thread for good; GoldenEye's handler resumes it later from another thread, which counts
  as handled) or when more than 32 are pending; the detail names which.
- `ucodes[]`: every RSP task kind seen (`gfx:`/`audio:` + hash of the microcode text) with task counts; `unknownUcodes`
  are the ones not in `known_ucode.tsv`. Not a fault by itself: it says the game runs a microcode nobody has checked yet.
- `expansionPakUsed`: anything was written in RDRAM 4-8 MB (cor64 always reports 8 MB to the game)
- `determinism`: with `COMPAT_TWICE=1`, whether the second run gave identical hashes at every checkpoint
- `eepromNonZeroBytes`: how much of the 4 Kbit EEPROM the game wrote

Known blind spots: CPU loads/stores that the recompiled fast paths serve without the memory controller are not seen
(device-region access goes through it, RDRAM does not); RSP DMA and SI DMA are not classified; there is no
microcode database beyond `known_ucode.tsv`.

## Status levels (status.json)

`not-run` < `no-boot` (crash, or all screenshots one colour) < `boots` (draws something; the automatic ceiling) <
`title` < `in-game` < `certified`. The last three need a person/agent to have looked at the PNGs and written a line in
`judgements.tsv`:

```
id <TAB> status <TAB> run folder it was judged on <TAB> issue 1 | issue 2 | issue 3
```

A judgement counts only while its run is the latest for that game, so re-judge after every new run. Then run
`python compat/tools/summary.py`.

## How an agent certifies a game

1. `compat/tools/get_rom.sh <id>`; read the header (CIC, size) and the game's line in `games.tsv`.
2. Run the boot script; open every PNG and judge: black / garbage / title / in-game. Write the verdict to
   `judgements.tsv`.
3. Extend `scripts/<id>.txt` to reach gameplay (menus, a level/race/match) and to play ~5 minutes with screenshots
   along the way. Use `savestate` at the start of gameplay so later runs can `loadstate`.
4. Rerun with `COMPAT_TWICE=1` and check the bar below. Fix nothing in the emulator from the compat lane: file the
   issues (report.json keys, screenshots) for an emulation lane.

### Certification bar (all must hold, on one run of the committed script)

1. Boots to the title screen (looked at, matches the real game).
2. Reaches gameplay through the script.
3. Stable for 5 minutes of scripted play (`framesRun` >= 18000 on NTSC, no crash, no first-chance exceptions).
4. Deterministic: `COMPAT_TWICE=1` gives `determinism.identical: true`.
5. Saves work: the game's save type is emulated (no `save:*` / `pak:*` keys in `unimplemented`), and a save made in the
   script survives `reset` (check in game, e.g. the file shows on the file select).
   Practical rule (lane cert_scripts, 2026-11-13): after gameplay the script ends with a save block:
   `savefile pre_reset` -> `reset` -> boot to the game's file/save screen -> `screenshot reset_<screen>` ->
   `savefile post_reset`. It passes when `pre_reset.sav` has non-fill bytes, `post_reset.sav` still holds them
   (`python compat/tools/savecheck.py <run>` prints the verdict) and the screenshot shows the saved file/progress.
   Games that only save settings/records count when that data survives; games with no battery save (Controller Pak
   only, or none) pass point 5 by `saveNonFillBytes` = 0 and no `save:*`/`pak:*` key, noted in the judgement.
6. Audio present: `rms` > 0 at gameplay checkpoints, and it sounds right in a `COR64_WAV` capture if in doubt.
7. No unimplemented-feature warnings on the path (`unimplemented` empty) and every microcode in `known_ucode.tsv`
   (add it there once the visuals and audio check out).
8. Visual check against expected. Practical rule (2026-11-13; no real-hardware capture set exists): the certifying
   agent opens every PNG of the run and confirms each matches the game's real screen (layout, text, colours, no
   garbage/missing geometry), and every checkpoint `frameHash` is identical in the `COMPAT_TWICE` rerun (point 4).
   The judged run's checkpoint hashes then serve as the known-good reference: a later run of the same script and
   build line that changes them must be looked at again before keeping `certified`.
9. Full-speed figure recorded (recorded, not a pass/fail gate; the speed target is a separate climb goal): low-end profile ms/frame on a gameplay savestate (`climb/bench.sh`-style,
   `PROBE_LOWEND=1 DOTNET_EnableAVX512F=0 SessionProbe <rom> bench <state> 600`), written in the judgement line.

Then set `certified` in `judgements.tsv` and run `summary.py`.
