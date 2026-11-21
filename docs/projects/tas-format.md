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
