# Compatibility catalog: the agent play playbook

How an agent plays a game for the Great Compatibility Catalog through the brokennes MCP server (P0, scope items 4 and 5 of
[compat-catalog-plan.md](compat-catalog-plan.md)). Three tools do the work: `play_step` (the loop), `record` (the movie) and
`reference` (the lock-stepped reference emulator). Everything else in the server (watch, search_memory, save_state, the debugger) still works
on the same session.

## The loop in one picture

```
open_rom path=<rom> checks=true          -> session id (keep it: ids are private)
reference action=open align_frames=240   -> optional: Mesen / BizHawk on the same ROM, boot offset found
record action=start text="<game> pass 1"
repeat:
  play_step buttons=[...] frames=20 scale=0.5 ram=["lives=[$075A]"]
     -> small PNG (+ reference score), frame, lagging, ram values
  decide the next buttons from the picture and the values
  record action=mark text="world 1-2 reached" state=w12     (at every milestone)
record action=stop path=<catalog movie path>.bnm
reference action=close ; close_session
```

## play_step

One call = one decision. The pads are held for `frames` frames (default 15), then released.

| input | meaning |
|---|---|
| `buttons`, `buttons2` | pad 1 / pad 2 buttons, by name: Up Down Left Right A B Start Select (X Y L R on the SNES; C X Y Z Mode on the Genesis; 1 / 2 / Pause on the Master System). Empty = nothing pressed. |
| `frames` | frames to hold them. 0 only looks (picture and RAM, no frame runs). |
| `until`, `max_frames` | instead of a frame count: stop as soon as the expression is non-zero (checked after every frame), within the budget (default 600). Examples: `[$000E] == 8`, `[$075A] < 2`, `frame >= 3000`. `stoppedBy` says `condition` or `budget`. |
| `scale` | the picture: 0.5 (default, half size: a few KB), 1, 2, or 0 for none. |
| `ram` | values read after the step: expressions, optionally named: `lives=[$075A]`, `x={$0086}` (a word), `[7E:0DBF]`. |
| `ref_picture` | with a reference open: return its picture too (second image). |

The result: `frame` (frames since power-on), `framesRun`, `stoppedBy`, `pressed`, `lagFrames` / `lagging` (only when the session was opened with
`checks:true`: a frame where vblank came before the game reached its frame wait; `null` otherwise), `ram`, `watches` (the session's watch
tool), `hash` (the picture's hash: two equal hashes = the screen did not change), `ms` (the call's own time), and `reference` when one is open.

Tips:

- 15-30 frames per decision is the sweet spot: a jump is ~30-40 frames, a menu cursor move ~10.
- A button held across two steps is released between them for no frame (the release is at the end of a step and the next step presses it again
  before its first frame), so holding Right for 200 frames as 10 steps of 20 is the same as one step of 200.
- To tap a button (menus, Start): `frames:1` with the button, then a step without it. Many games ignore a press held from the frame they open
  the menu on: tap, wait 10, tap.
- Stuck? `hash` unchanged over several steps with buttons pressed = a frozen game or a pause screen. Check `lagging` (a game slowing down)
  versus a freeze (`framesRun` normal, the picture unchanged, RAM unchanged).
- Progress detection: put the level / lives / score in `ram` and write them into the notes; a level counter that changes is the cheapest
  "made progress" signal.
- Speed measured on this machine (2026-11-14, shared with other agents): 20 frames per step with `checks:true` = 240-370 ms per call on the NES;
  the reference adds the reference emulator's time (see below).

## record

| action | what it does |
|---|---|
| `start` | starts the recording (a mark "recording start"). The movie keeps the session's whole input from power-on, so it replays from power-on and exports to bk2 / fm2 (movie export). Start right after open_rom for a clean movie. |
| `mark` | a checkpoint: a mark at this frame with `text`; with `state` also a named save state (load_state goes back to it). |
| `save` | writes the movie so far (`path`), keeps recording. |
| `stop` | writes it (`path`, default the session's folder) and ends. The result lists the marks. |
| `status` | recording or not, the marks so far. |

The movie is a `.bnm` (BrokenNes movie, with keyframes: picture and RAM hashes every 60 frames). `open_movie movie=<bnm> rom=<rom>` replays it
and says `inSync` (every keyframe matched). The marks are the movie's `m` lines (Movie.Marks).

Note the bad habits that break a movie: `load_state` or `reset` in the middle of a recording starts the session's history again from that
state (the movie then starts from a save state, not power-on, and no longer exports to bk2): do not rewind during a catalog recording;
play forward, mark, and note what went wrong.

## reference

A second emulator on the same ROM, run in lock step: every `play_step` feeds it the same pads for the same frames, and it waits (parked inside
its Lua script) until the next step. `play_step` then returns `reference.diff`:

| field | meaning |
|---|---|
| `score` | 0 = the same picture. Up to ~0.02 = the same up to palette and filtering ("same"); up to ~0.12 = "close" (a sprite or a scanline off, a palette shade, a frame of animation); above = "different". |
| `pixel` | share of the picture whose brightness differs a lot. |
| `edges` | 1 - the overlap of the two pictures' shapes: palette-blind. |
| `ramMismatches`, `ram` | the CPU addresses set with `ram` (open / ram action), read on both machines after the step. |

Actions: `open` (emulator `auto`: Mesen 2 for NES, SNES, Game Boy / Color, Master System, Game Gear; BizHawk Genplus-gx for the Genesis;
`align_frames:240` finds the boot offset at once), `align` (both machines run frames with no pads; the offset whose pictures match best is
kept: the result shows the score curve), `offset` (set by hand), `ram` (`["lives=$075A","level=$0760"]`), `compare` (both pictures now, the
reference's second), `status`, `close`.

Rules:

- Open the reference right after open_rom, before any input, and align it. While it is open, move the session only with `play_step`:
  frames run by other tools (run, input, step) are not mirrored; the next play_step catches the reference up with no pads and says so in `notes`.
- Frames are counted from power-on on both sides; `offset` = the reference's frame minus BrokenNes's. Mesen usually reports its first frame
  before the script gets control: the offset handles it.
- A score that climbs and stays high after a point is the catalog finding: note the frame (it is in the movie), compare the two pictures
  (`reference action=compare`), and mark it (`record action=mark text="diverged from Mesen"`). Differences that come and go (a flicker phase,
  a palette) are usually not bugs: say so in the notes with the score.
- The reference's start cost: Mesen ~2-6 s; BizHawk 20-90 s (EmuHawk is slow to start on a busy machine). Its step cost: Mesen ~150 fps plus
  ~20 ms per picture; BizHawk ~500 fps.
- RAM addresses are CPU bus addresses: NES `$0000-$07FF`, SNES `7E:xxxx`, Game Boy `$C000-$DFFF` / `$FF80`, Master System / Game Gear
  `$C000-$DFFF`, Genesis `$FF0000-$FFFFFF` (BizHawk reads the 68K RAM domain).

## Measured (2026-11-14, this machine, shared with other agents; 20 frames per step, scale 0.5)

| console, game | step ms (checks off / on / + reference) | aligned offset | reference score over the play |
|---|---|---|---|
| NES Super Mario Bros. | 136 / 239 / 506 | 0 | 60 of 60 steps "same" |
| NES Mega Man 2 | - / 367 / 336 | -10 | 50 of 50 "same" |
| NES Castlevania | - / - / 435 | 0 | 59 of 60 "same" |
| SNES Super Mario World | - / - / 502 | 0 | 47 of 50 "same" (Mesen's 239-line picture cropped at lines 7-230) |
| SNES Donkey Kong Country | - / - / 926 | 2 | measured before the crop fix only |
| SNES Super Metroid | 167 / - / - | - | - |
| Game Boy Tetris | - / - / 155 | 83 | 26 of 50 "close" |
| Game Boy Super Mario Land | - / - / 171 | 82 | steady 0.20 ("different"): a candidate finding |
| Game Boy Color Link's Awakening DX | - / - / 296 | 13 (search was too narrow then) | 0.20 median |
| Master System Alex Kidd, Sonic | - / - / 257-280 | 0-1 | 50 of 50 "same" (Mesen's frame cropped at y 24) |
| Game Gear Sonic Blast, Sonic & Tails | - / - / 350-394 | -1, 5 | final 0.04-0.11 ("close"), the screen at x 48, y 48 of Mesen's 256x240 |
| Genesis Sonic 1 (BizHawk Genplus-gx) | 213 / - / 365 | 2 | 41 of 50 "close", median 0.034 |

Boot offsets: Mesen's Game Boy runs ~80 frames ahead of BrokenNes at the same picture (use align_search 90 there); the others are within
10. The movies of every run above replayed in sync (open_movie) with their 3 marks. The lag figure needs `checks:true` and is only as good as
the checks' frame-wait detection: a game whose wait loop is not recognised counts every frame as lag (Castlevania showed 1202 of 1200):
treat a lag count close to the frame count as "unknown".

## What the server copies are

The reference runs from private homes in the session's folder (`%LOCALAPPDATA%\BrokenNes\mcp\sessions\<pid>-<session>\reference-mesen` /
`reference-bizhawk`): a copy of Mesen.exe with its own settings.json (sound off, IO and network access on for the script), or BizHawk's
installed EmuHawk with a config.ini written by `BrokenDev\tools\refstep\bizhawk-config.ps1` (the same pinned settings as BrokenDev's compat
runs). The installed emulators (BROKENDEV_HOME\emulators) are never written. The scripts: `BrokenDev\tools\refstep\mesen_refstep.lua`,
`bizhawk_refstep.lua`. The protocol is described at the top of each.

## Testing it

`BrokenNes.Mcp.exe --agentloop-test [--ref] [--nochecks] [--steps N] [--out dir] --rom <rom> [--rom ...]` (with `BROKENNES_REPO` or `--repo` set to
the checkout): per ROM, open_rom, the reference (with --ref), N play_steps of a crude agent, record start / mark / stop, then the movie replayed
with open_movie must be in sync with 3 marks. One JSON line per ROM: step time (median, max), PNG bytes per step, lag frames, the reference's
alignment and its scores.
