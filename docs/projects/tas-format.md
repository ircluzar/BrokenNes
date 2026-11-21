# TAS format and runner (TAS workline T1)

Plan: [tas-workline-plan.md](tas-workline-plan.md). Code: `Workshop/Tas/` (script, targets, runner, CLI), MCP `BrokenDev/mcp/Tools.Tas.cs`.

## Pieces

| piece | file | what |
|---|---|---|
| script language | `Workshop/Tas/Script/TasScript.cs` | the N64 compat language (UAT/n64/compat/README_compat.md), unchanged, console-neutral, plus `input BTNS N`, `pad N`, `mark TEXT` |
| button maps | `Workshop/Tas/Script/TasButtons.cs` | per console: names in bit order, stick yes/no, pad count |
| per-frame log | `.tlog` (TasScript.WriteLog/ReadLog) | the exact expansion: one `F` line per frame, `@` lines for ops between frames |
| targets | `Workshop/Tas/TasTargets.cs`, `N64Target.cs` | `ITasTarget`: pads in, frame, picture, sound, state, poke/peek, console ops |
| runner | `Workshop/Tas/TasRunner.cs` | chapters, checkpoints, goals, expect check, run-twice determinism, frame sink |
| CLI | `Workshop --tas run|check|expand|import|import-autotas|export|branch|render` | `Workshop/Tas/TasCli.cs` header has the options |
| MCP | `tas_run`, `tas_branch` | the same runner in the MCP server |

## Script language

One command per line, `#` comments, frames = the console's frames (NES 60, N64 VI fields).
`run N` (`frames`, `wait`), `seconds S`, `press BTN [on=5] [off=5]`, `pulse BTN [N=60] [period=10]`, `hold BTN`, `release [BTN]`,
`input BTNS N` (held set becomes exactly BTNS, then N frames: the compact per-frame form that imports write), `stick X Y` (consoles with a stick),
`pad N` (later input commands drive pad N), `mirror N` (pad 1 also drives pads 2..N),
`checkpoint NAME`, `screenshot NAME`, `savestate NAME`, `loadstate NAME|FILE.state`, `reset`, `poke ADDR HEX`, `pokeeach N ADDR HEX`,
`pokehold ADDR HEX` / `pokeoff [ADDR]`, `peek ADDR [N]`, `savefile NAME`, `mark TEXT`.
Console ops: N64 `pak`, `prenmi`, `reload`, `clearsave`, `dumpram`. Unknown commands are refused at expansion, before anything runs.

Buttons (`+` joins): NES `A B Select Start Up Down Left Right` (aliases U D L R); N64 `A B Z START L R CU CD CL CR DU DD DL DR` + stick -128..127;
other consoles (T7 start): the BrokenNes pad names (Up Down Left Right A B X Y L R Start Select C Z Mode).

Expansion resolves press/pulse/seconds/mirror/pokehold/pokeeach into explicit frames and `poke` ops, so the runner, the log and the exporters see only
frames and ops. `--tas expand` writes the log; running the log gives the same checkpoints as running the script (checked on SMB1: identical video hash).

## Run file (`*.tasrun.json`): chapters

```json
{ "schema": 1, "console": "nes", "game": "smb1", "rom_sha1": "...", "rom": "optional path", "cores": "FIX/FIX/FIX", "power_on": "fceux", "ntsc": false,
  "chapters": [
    { "id": "boot", "title": "...", "start": "power-on", "script": "boot.tas",
      "goal": ["peek 0770 == 01"],
      "expect": { "frames": 194, "checkpoints": { "end": { "picture": "A8F7...", "ram": "22A7..." } } } },
    { "id": "1-1", "start": "previous", "script": "1-1.tas" } ] }
```

- `start`: `power-on`, `previous` (continue the machine where the last chapter ended: identical to one continuous run), or `state:FILE`.
- Every chapter ends with an automatic `end` checkpoint and writes `<out>/<id>/end.state`.
- `goal` (T1): `peek ADDR[:N] OP HEX` lines checked at the chapter end (memory goals; T2 adds expressions and picture goals).
- `expect`: frames + named checkpoints (`video`, `picture`, `ram`, any subset). `--tas check` fails on any mismatch; `--bless` writes them from a run.
- ROM lookup: `--rom`, the run's `rom`, `BROKENNES_TAS_ROM`, or `BROKENNES_TAS_ROMDIRS` (folders searched by `rom_sha1`).

## Checkpoints and determinism

A checkpoint has: `video` (FNV-1a over every frame's 0xAARRGGBB pixels since the chapter start), `audio` (FNV over every 16-bit sample since
the chapter start), `picture` (the console's own hash: NES = the .bnm keyframe hash, so AutoTAS key_picture values check directly; N64 =
SessionProbe's frameHash), `ram` (NES = .bnm key_ram; N64 = FNV over RDRAM), rms/peak of the sound since the last checkpoint, solid
(blank) and dominantFraction. N64 video/audio/picture use SessionProbe's exact hash, so a compat script gives SessionProbe's checkpoint values.

The determinism check (default; `--once` skips it) runs the whole run a second time in a fresh machine and compares every checkpoint's
video, audio, ram and frame. Audio note: after a loadstate cor64 can hand a frame's samples out one call later; video/picture/ram are the
identity, audio is compared in the twice check (both passes take the same path).

N64: the target sets `COR64_VI_SPEEDUP=1` unless already set (real VI pace, the certification timing run_game.sh uses).

## Movies

- NES `.fm2`: `--tas export S --fm2 OUT --rom R` (power-on movies; loadstate/poke/reset are refused), `--tas import --fm2 F` (soft resets refused).
  The `--movie` tools are unchanged. `.bnm` import: `--tas import --bnm M`; `--tas import-autotas PKG` turns an AutoTAS package into a run.
- N64 and every console: the `.tlog` per-frame log is the export.

## Branching

`--tas branch RUN --chapter ID --frame F --out D [--tail FILE]` (MCP `tas_branch`): runs to frame F of the chapter, saves `branch.state`, writes
`branch.tasrun.json` (one chapter from that state) with the tail script, or without one the rest of the original as an exact `.tlog`.
Running the untouched branch reproduces the original's end picture and RAM.

## Offline replay hook (video)

`ITasFrameSink` (Begin/Frame/Audio/End) gets every frame's picture and the core's audio during pass 1. `--frames-dir D` dumps PNGs + WAV;
`--tas render ... --mp4 OUT` plugs the Media Foundation writer (branch tas-mp4) in here.

## Goals (T2)

Chapter `goal` lines (all must hold; the report lists each with `met`, `firstFrame`, `detail`):

| line | meaning |
|---|---|
| `EXPR` or `end: EXPR` | true at the chapter end |
| `within N: EXPR` | true on some frame <= N (frames since the chapter start) |
| `reach: EXPR` | true on some frame; `firstFrame` = the first (the score for comparing branches) |
| `always: EXPR` | true after every frame (e.g. `always: lives == 2`) |
| `peek ADDR[:N] OP HEX` | T1's form, still accepted |

EXPR: `or and not` (`|| && !`), `== != < <= > >=`, `+ - * / % &`, numbers (`$hex`, `0xhex`, decimal), variable names from the game's RAM map,
`peek(ADDR[,N])` (big-endian; `peekle` little-endian), `picture("ref.png"[, x, y, w, h[, tol]])`, `frame`, `lag`, `abs min max`.

**RAM maps** (`UAT/tas/ramdb/<console>/<game>.json`, found from the run's `game`, or the run's `ramdb`, or `BROKENNES_TAS_RAMDB`):
`{"vars": {"x_page": {"addr": "006D"}, "x": {"expr": "x_page*256+x_screen"}, "speed": {"addr": "0057", "type": "s8"}, "score": {"addr": "07DD", "size": 6, "type": "digits"}}}`.
Types: `u8 s8 u16 s16 u32 s32` (little-endian; `...be` big-endian), `bcd`, `digits` (one decimal digit per byte). The report's chapter `vars` = every
variable at the chapter end. First map: `nes/smb1.json`.

**Picture goals** (games without a RAM map): `picture(ref, x, y, w, h, tol)` compares that region of the frame with the same region of a reference PNG;
true when at most tol per mille of the compared pixels differ by more than 24 on a channel (default tol 20). Transparent reference pixels are skipped:
cut the reference down to the HUD glyph you care about (alpha 0 elsewhere) and it matches over any 3D background. Example: SM64's star counter
`UAT/tas/n64/sm64/star1_hud.png` (the "1" of "x1": 82/103 pixels differ while it shows 0, 0 once it shows 1).

## Precision (T2)

- **Lag frames:** a frame where the game polled no controller (NES $4016/$4017 reads; N64 joybus "read buttons" commands). Each chapter in the report has
  `lag` and `lagFrames`; the log gets a `LAG` line. Input on a lag frame is never read: edits there change nothing.
- **Edit session** (`TasSession`; MCP `tas_session`): open a chapter (earlier chapters are played), `step` N frames (optionally with input, past the end
  appends), `seek` to any frame (in-memory savestate ring: every 10 frames NES, 120 N64, then replay), `set` / `insert` / `delete` input at any frame,
  `status` (cursor, next inputs, RAM-map vars, lag seen, picture), `goals` (plays the chapter with its goals, resuming from the last frame before the
  earliest edit), `save` (DIR/<id>.tlog + DIR/edited.tasrun.json = the run with that chapter replaced, `trim` cuts it).
- **`--tas edit RUN --chapter ID --set F:BTNS[:N] --insert F:BTNS:N --delete F:N [--trim N | --trim-goal G[+K]] --out DIR`**: the same from the command line.
  `--variants FILE` (one edit set per line, `;`-separated) tries each from the base and keeps the best (all goals met, lowest `firstFrame` of goal
  `--score N`): a hand-written local search; the fleet search is T3.
- **`--tas diff A B [--ram-every N] --out DIR`** (MCP `tas_diff`): plays both runs in lockstep and reports the first frame whose picture differs and the first
  whose RAM differs, the differing bytes (NES 2 KB, N64 RDRAM) and RAM-map variables, the picture's differing box (a.png, b.png, diff.png), the first input
  difference and the 12 inputs before it, and a one-line `why` (input change N frames earlier / no input change = start state, ROM, cores, nondeterminism).
- **`--tas vars RUN --chapter ID --frame F`**: the RAM-map variables at a frame.

## Render

`--tas render RUN|SCRIPT --mp4 OUT.mp4 [--mp4size WxH] [--mp4kbps N]`: one pass (no twice check), every frame + the core's sound through Workshop/Movies/Mp4Writer
(Media Foundation H.264/AAC) at the console's rate; timestamps from frame and sample counts, so the video is lag-free at any host speed. The picture is
upscaled by an integer to fit 1280x720 (NES x3 = 768x720); sound at other rates than 44100/48000 is resampled to 48000. `--mp4check OUT.mp4 --grab S,..`
reads it back.

## Adding a console (T7)

1. A button map in `TasButtonMap.For` (names in bit order, stick, pads).
2. A target: the generic `SessionTarget` already drives any IConsoleSession via the BrokenNes pad (poke/peek on its first work-RAM domain);
   a console needing more (stick, own memory map, console ops) gets its own class like `N64Target`, registered in `TasTargets.Open`.
3. Console ops in `TasTargets.ConsoleOps`; a movie exporter if the console has a standard format.

## Proof (2026-11-21)

- NES SMB1 World 1 (AutoTAS package `T:\BrokenDev\Catalog\tas\nes\smb1\v1`, imported to `UAT/tas/nes/smb1`): 5 chained chapters, 9381 frames,
  all 5 chapter-end picture + RAM hashes equal the package's key_picture/key_ram, deterministic twice. Script -> tlog -> run gives the same video
  hash; script -> fm2 -> script is identical.
- N64 SM64 compat script, unchanged (`UAT/n64/compat/scripts/sm64.txt`, 21426 frames; run file `UAT/tas/n64/sm64`, expect blessed): on Garage2
  (PRDP) deterministic twice, and all 23 checkpoints equal SessionProbe's compat report (video, audio, frameHash) from the fleet.
  Found on the way: parallel-rdp's deferred writes reach RDRAM at a GPU-timing moment, so the N64 RAM hash drains the RDP first (as a save state does);
  each pass gets its own Controller Pak folder.
- Branch: SMB1 1-1 branched at frame 1000 and replayed reaches the chapter's end picture + RAM exactly; MCP client-test tas_run / tas_branch /
  tas_branch_replays pass (162/162).
