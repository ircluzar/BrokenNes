# Sega track B2a: the Genesis picture (record)

Branch `worktree-agent-a7e6220722d0ae708`, written 2026-10-03 and 04 against `docs/projects/sega-tracks-brief.md`, on top of Track B milestone B1 (`docs/projects/sega-track-b.md`).
B2a is the VDP's renderer and everything the picture needs: the real session (`MdSession` shows the VDP's picture, takes the pad), the access-slot and counter model made faithful to the oracle,
battery saves, the SSF2 and Sonic & Knuckles mappers, and the first ten roster games on screen against BlastEm. **`Consoles.GenesisReady` is flipped to true** on this branch (the picture is
verified for ten of ten roster games; the FM sound is Track B2b's, merged separately).

## What is where

| Piece | Files | Notes |
|---|---|---|
| Renderer | `Windows/NesEmulator/md/MdVdpRender.cs` (partial `MdVdp`, 460 lines) | planes A/B (32/64/128 cells, scroll full/8-line/per-line, vscroll full/2-cell), window and its distortion bug, priority ladder, shadow/highlight, backdrop, sprites (80/20/320, 64/16/256 in H32) with link list, per-line cut-off, X=0 masking, overflow and collision flags, H32/H40, V28/V30, PAL, CRAM to ARGB, mid-frame writes honoured; `Picture`, `CopyPicture`, `FramesRendered` |
| Slots and counters | `md/MdVdp.cs` | slot tables from the oracle's probes (below), `VBlankFlag`, status flags, hooks that bring the renderer up to the pixel being output before any register/CRAM/VSRAM/VRAM change |
| Session | `md/MdSession.cs` | the real picture (320x240 buffer, 256 or 320 by 224 or 240 shown), pad mapping (R to C, L to Z, Select to Mode; player 1 six-button, player 2 three-button), saves under `MdSaves\` through `SessionSavePath` / `SessionSaveKey` |
| Cartridge | `md/MdCartridge.cs` | SSF2 bank registers, Sonic & Knuckles lock-on image (the locked-on game's SRAM header at `$2001B0`), `Mapper` description |
| Console flag | `systems/ConsoleSystems.cs` | `GenesisReady = true` |
| Workshop | `Workshop/Sega/MdPictureTest.cs` (`--md-pic-test`, 93 checks), `MdGoldenCli.cs` + `Workshop/MdGolden/md_golden.json` (`--md-golden`), `MdFindCli.cs` (`--md-find`), `MdRenderCli.cs` (`--md-render`), `ImgDiffCli.cs` (`--img-diff`), `MdRunCli.cs` (more options: `--png-at`, `--pic-hash-at`, `--alloc-probe`, `--input`) | |
| Oracle drivers | `Workshop/Sega/oracle-tools/md-picture/` | our own scripts that drive BlastEm from outside (key presses, screenshots, quick-save states), the 68000 mini-assembler and probe ROM builders, the poison-injection proof; BlastEm itself is not in git |

## How the renderer works

One line at a time, caught up lazily. Two stages per line:

- **Index stage** (ahead): for every pixel one byte, bit 7 the priority, bits 5-4 the palette line, bits 3-0 the colour index (0 is transparent but the tile's priority bit is kept, see the
  shadow/highlight fact below). Window, plane B, plane A and the sprites are composed here.
- **Colour stage** (at the pixel the beam is on): the index goes through a 192-entry table (normal, shadow, highlight; DAC levels 0, 27, 49, 71, 87, 103, 119, 130, 146, 157, 174, 190, 206, 228,
  255) when the pixel is output. The index stage runs `RenderLead` = 16 pixels in front of the colour stage.

Everything that changes what a pixel would look like (`SyncRender()`) brings both stages up to the beam first: a register write, a CRAM / VSRAM / VRAM write, the slot machinery. A CRAM write puts the
colour it writes on the pixel being output (the "dot", also with the display off). Writes to registers that act on the output side (R1 display and mode bits, R7 backdrop, R12 shadow/highlight and width)
rewind the index stage (`RewindIndexStage`) so the change shows from the output pixel on. The window registers R17/R18 latch at offset 2560 of a line for the next one. The picture is published at the last
active line (buffers swap); nothing is allocated per frame.

## Hardware facts found by comparing with the oracle

Each one was seen on BlastEm 1.0.0's screen or in a probe's output, not in its source, and is now in the code and in `--md-pic-test` or the golden suite:

| Fact | Found with | Pinned by |
|---|---|---|
| Shadow/highlight state is "normal" if either plane's tile priority bit is set, even where the pixel is transparent | MacDonald's `vctest` | `--md-pic-test` (shadow/highlight group; poison case 4), golden `vctest` frame |
| Sprite operators: palette 3 colour 14 highlights, 15 shadows; a low-priority visible sprite is itself shadowed | Nemesis' sprite masking test | `--md-pic-test` (sprite and shadow groups) |
| Sprite masking: an X=0 sprite masks the following sprites only if a non-zero-X sprite came before it on the line, or the previous line used its pixel budget (reaching the limit counts); a link at or past the table size (64/80) ends the list | the same ROM in H32 and H40 | `--md-pic-test` (masking checks; poison case 3) |
| Plane width code 2 repeats row 0 | Fonzie's window test, `vctest` | `--md-pic-test`, golden Fonzie frame |
| Window distortion bug: after a left-side window, the plane column (a pair of cells) that supplies the left part of the first screen column is read from the next column when `(hscroll & 0xF) != 0` | MacDonald's window distortion bug ROM, 14 captured states | `--md-pic-test` (window group), golden window-bug frame |
| The VBlank status flag follows the V counter: it rises at the counter's step to 224 (H8 = `$A5`, offset 2640) and falls at the step to the last line | probe ROMs on the oracle | `--md-test` (counters and flags), golden |
| The pre-render (last) line has picture slots; the tail of a line after the counter step already uses the next line's slot table | probes | Nemesis' port test ROM, its own tally (test 16 passes) |
| With the display on there is an external slot every 16 pixels from pixel 12, every fourth one a refresh slot (pixels 28, 92, 156, 220, 284), 18 free per H40 line and 16 per H32 line | Nemesis' CRAM flicker ROM, probes | golden (CRAM flicker H32 pixel-identical) |
| A blanked H40 line has 198 DMA words (a refresh costs two) in a 16-clock grid | Chilly Willy's direct colour DMA demo (its picture needs this), probes | `--md-test` (DMA timing), the direct colour DMA golden frames |
| CPU writes to the VDP ports cost what the oracle's do to within about one clock per access | probes | measured only |

## Verification

All on the branch tip with `GenesisReady = true`, built with `--artifacts-path %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_sega_b2a`:

| What | Result |
|---|---|
| `--md-pic-test` | 93 of 93 (89, plus four checks for 32- and 128-cell planes added last): planes, priority, scroll modes, window and its bug, sprites and their limits and masking, shadow/highlight, H32/H40, V28/V30, PAL, mid-line display switch and CRAM dot, window latch, pad mapping, SRAM saves with a synthetic ROM, SSF2 banks, lock-on SRAM header, `.smd` normalisation |
| `--md-test` | 113 of 113 (B1's suite, with Nemesis test 16 now passing) |
| Nemesis' VDP port test ROM, its own tally | all pages (9, 16, 34 and 86 passed at the page ends I read) with 0 failed; page 2 (tests 10 to 16) shows `Results: (16/ 0/ 16)`. B1 was 82 of 83 (test 16, FIFO wait states). Cause: the slot model (access slots, refresh, irregular HSYNC) was not faithful |
| `--md-golden` | 75 frames of 18 entries match: 71 frames that matched BlastEm's own screenshots pixel for pixel (ten roster games 43, Titan Overdrive 1 on PAL 13, Overdrive 2 on PAL 10, `vctest`, Fonzie's window test, the window distortion bug, Nemesis' CRAM flicker H32 2), 4 regression-only frames (the direct colour DMA demo, the VDP port test) |
| `--m68ktest` | 1,317,560 vectors, 0 failed |
| `--sega-contracts-test`, `--sega-test`, `--z80test --selftest`, `--sega-chips-test`, `--sega-bridge-test` | 78 of 78, 79 of 79 (the foundation test adapted for a finished Genesis), 57 of 57, 34 of 34, 126 of 126 |
| `UAT\entrypoints\desktop-smoke.ps1` | 9 of 9 |
| `UAT\entrypoints\sega-preview-smoke.ps1` | 28 of 28, adapted deliberately: the Genesis no longer needs the switch; its synthetic ROM is a 94-byte 68000 program that sets H40 and draws the pad as the backdrop colour (idle `92FF00`, Right `92AE00`); input is held and sampled repeatedly. Fixed along the way: the script's frame reader dropped two colour channels (`[byte] -shl` stays a byte in PowerShell: a green backdrop counted as black), it now waits for the first frame, and the menu read retries |
| Lite | builds (0 errors) and runs a Genesis picture in the browser: the synthetic program's backdrop and Fonzie's window test (WINDOW / PLANE A / PLANE B) in both the dev server (debug, interpreted) and an AOT-trimmed publish. Sonic the Hedgehog loads and runs (console switches to Genesis, 320x224 frames arrive) but did not reach its logo in the time I watched: the embedded browser pane throttles `requestAnimationFrame` (the NES demo also ran at 2.7 fps there), so no speed figure for Lite is claimed |

**Shown able to fail** (a verifier that cannot fail proves nothing): `poison.ps1` injects a bug into the renderer or mapper source, rebuilds, runs `--md-pic-test` and counts the failures; each of
seven injected bugs (sprite priority bit ignored, per-column vscroll uses column 0, X=0 masks as the first sprite, shadow ignores transparent tile priority, sprites one line short, 21 sprites per
line in H40, SSF2 bank register offset) failed the suite (failing checks per case: 1, 1, 1, 1, 1, 2, 2; the source is restored and rebuilt after each); the four plane-size checks were shown the same way by hand (128-cell width treated as 64: 2 failures; 32-cell width treated as 64: 2 failures). The golden suite with a poisoned colour table changed all
75 frames. The smoke script failed for real before it was fixed (black-looking green backdrop, empty menu read). The ROM pages, Nemesis' tally and BlastEm's screenshots are independent of
our own test code.

## Against the oracle, what matches and what does not

| ROM | Result against BlastEm 1.0.0 |
|---|---|
| MacDonald `vctest` | pixel-identical |
| Fonzie's window test | pixel-identical |
| MacDonald window distortion bug | 14 of 14 captured states rendered pixel-identical; the running picture identical at the matching frame |
| Nemesis sprite masking test (H32 and H40) | pixel-identical (not in the golden file: `--md-pic-test` covers the rule) |
| Nemesis CRAM flicker, H32 | pixel-identical at the matching frame (dots, staircase, colour cycle). **H40: differs**: the colour phase (BlastEm cycles its colours per frame, ours holds one) |
| Chilly Willy direct colour DMA | recognisable image; **not pixel-exact**: about 8 pixels of horizontal phase, the refresh and slot structure of the blanked lines is close but not the oracle's |
| Titan Overdrive 1 (PAL) | 13 pinned frames pixel-identical; three more captured shots differ (glitch and DMA-raster scenes, the capture's shots 4, 6 and 12) |
| Titan Overdrive 2 (PAL) | attempted as asked, 10 pinned frames pixel-identical; one more differs (a sprite phase). Not complete: audio sync and any interlaced scene untested |

**Roster, first ten games** (ROMs in `X:\EMULATION\Sega Genesis`, not in git). The method: BlastEm runs a wall-clock script (key presses and screenshots, `roster_scan.ps1`); our emulator runs the same
presses at the frames they fall on and `--md-find --scan` looks, for each screenshot, for the frame of ours that matches it best. Frames with 0 differing pixels are in the golden suite; the rest are listed.

| # | Game | Frames pixel-identical (golden) | Differences, and why |
|---|---|---|---|
| 01 | Flicky | 1 (title) | gameplay shots not compared exactly: input timing against a wall-clock capture; Right during the title/demo does not change the picture in my scripts (attract mode), so no play check |
| 02 | Altered Beast | 6 | none in the captured shots; Right changes the picture in-stage |
| 03 | Sonic the Hedgehog | 3 | gameplay shots after Right not exact (input timing); Right changes the picture (68%) |
| 04 | Golden Axe | 6 | one shot differs by 4,428 pixels (a typed-text phase, not investigated); with A pressed to clear the text box Right changes the picture |
| 05 | Strider | 5 | one shot differs by 47 pixels (frame 1286, not investigated); Right changes the picture |
| 06 | Streets of Rage | 3 | two later shots differ (19,047 and 55,861 pixels): input timing in the menus, not investigated; Right changes the picture |
| 07 | Castle of Illusion | 5 | two later shots differ by most of the picture (story pages, input timing); my scripts did not reach controllable play, so no play check |
| 08 | Ecco the Dolphin | 5 | none in the captured shots; Right changes the picture |
| 09 | Sonic the Hedgehog 2 | 3 | later shots differ (input timing; my first script's third Start paused the level); with the script fixed Right changes the picture (65%) |
| 10 | Kid Chameleon | 6 | none in the captured shots; my scripts did not reach controllable play |

The dashboard status for each is **working** (picture checked against the oracle at several points through title, menus and the first stage; pad Start works in all ten, a held direction
moves the game in seven). None is called playable: nobody has played any of them for a stretch, and the held-input comparison against BlastEm needs frame-exact input that the wall-clock harness
cannot give.

## Saves and mappers

- **Battery SRAM**: `MdSession` exports and imports the cartridge's SRAM through the repo's own pattern (`SessionSavePath` / `SessionSaveKey`, `MdSaves\` on the desktop, IndexedDB on Lite), tested with a synthetic ROM that writes its SRAM, in `--md-pic-test` (write, export, import into a fresh session, same bytes; the `$A130F1` write protect). Not tested with a real game's save (Phantasy Star IV, Sonic 3: roster 17 and 18).
- **SSF2 bank mapper**: the registers `$A130F3` to `$A130FF` choose a 512 KB page for the matching window; `$A130F1` enables and write-protects the SRAM. Tested with a synthetic 4 MB+ ROM.
- **Sonic & Knuckles lock-on**: the locked-on game's SRAM header (at `$2001B0` of the combined image) says how the battery RAM is wired; a plain 4 MB ROM is not mistaken for one (checked). Not run with Sonic 3 in play.
- `.smd` copier images (interleaved blocks) read as the same ROM as the plain file.

## Speed and allocation

`--md-golden` runs 27,109 frames in 88.6 s: **306 frames/s overall (5.1 times real time at 59.92 fps)**, 216 to 569 frames/s per entry (3.6 to 9.5 times), on a machine shared with other
jobs. `--md-run --alloc-probe` over 600 frames of Sonic the Hedgehog: picture copy 0.0 bytes per frame, sound drain 0.0 (was 5.9 KB: `Sn76489.ReadSamples` allocated two arrays when a video
frame's audio is more than 512 sample frames; it now works in 512-frame chunks on the stack, same output, a shared-file change), `board.RunFrame` 23.2 bytes per frame (one small object per frame
somewhere in the sound unit or Z80 path, not the VDP; `MdSoundUnit.cs` is B2b's file, not changed here).

## Decisions

- Two-stage per-line renderer with a fixed lead rather than a per-pixel pipeline: the VDP's own behaviours (dots, mid-line register changes) are reproduced where the oracle shows them, and the rest stays cheap.
- Pad mapping on the existing twelve-button layout: R acts as C, L as Z, Select as Mode, so a six-button pad is reachable from every app's current layout.
- No silent fallbacks: a Genesis ROM whose picture needs a feature we lack shows what it shows; interlace is not faked (it renders as non-interlaced, see open problems).
- The golden suite pins this emulator, and labels each entry `oracle` only when the frame matched BlastEm's own screenshot pixel for pixel. A missing ROM is skipped and counted.
- Roster status words are conservative (see above).

## Shared files touched (minimal, additive)

`Windows/NesEmulator/systems/ConsoleSystems.cs` (`GenesisReady`), `Windows/NesEmulator/sega/Sn76489.cs` (`ReadSamples` chunked, same output), `Workshop/Program.cs` (routes `--md-render`, `--img-diff`, `--md-find`, `--md-pic-test`, `--md-golden`),
`Workshop/Sega/SegaFoundationTestCli.cs` (adapted for a finished Genesis), `UAT/entrypoints/sega-preview-smoke.ps1` (adapted, see above), `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md` (section 2b and the
oracle-scripts line in section 4), `docs/projects/`. Contracts: no change to `IConsoleSession`; `MdSession.DisplayWidth` is 320 for both H32 and H40 (the session letterboxes H32 as the other consoles' sessions do).

## Not tested

- **Interlace** (both modes) is not implemented: an interlaced game shows one field's lines doubled. B3.
- Sound, speed in the browser, touch-pad layout for the Genesis in Lite (the layout key falls back as for the other consoles; I did not look at it on a phone), a game saving its SRAM in real play, the Sega logo and PAL games beyond Titan Overdrive 1 and 2.
- The 20 roster games' later halves: ten games from 11 on are untouched.
- The VSRAM left-column quirk (the leftmost 8 pixels with per-2-cell vertical scroll read a different cell on hardware) is not implemented.
- 32- and 128-cell planes, V30, and the sprite overflow and collision flags are covered by synthetic `--md-pic-test` checks only, not by an oracle frame.

## Open problems and risks

- H40 CRAM flicker colour phase (above); direct colour DMA phase (above); Overdrive 1's three scenes and Overdrive 2's one frame: all are slot or DMA phase questions in the same area, likely one root cause.
- The sprite table is read from VRAM in full at the start of each line. The hardware keeps an internal copy of part of it (updated as VRAM is written), so a game that edits the table late in a frame may differ; not compared against the oracle.
- Sega Genesis Plus GX or any other emulator's source was not read for any of this (ledger section 2b).
- The golden suite depends on the ROMs being at the same paths; a changed ROM image shows as "changed", a missing one as "skipped".

## Next (B3)

1. Interlace (modes 1 and 2, field flag in the status register, the picture doubling): needs an oracle shot of an interlaced game (Sonic 2 two-player special stage, Ristar? any that uses it) and a test.
2. The three Overdrive 1 scenes, the one Overdrive 2 frame, the H40 CRAM flicker phase and the direct colour DMA phase: take the slot model apart against more probe ROMs (HV counter, DMA word timing at every pixel of a blanked line).
3. Play the ten roster games with frame-exact input: use BlastEm's pause and frame advance (`bl.ps1` actions `pause` and `adv`) with `--md-find --scan` over input start frames, then raise the ones that match to playable.
4. The rest of the roster (11 to 20), in particular Phantasy Star IV and Sonic 3 for saves, Sonic & Knuckles for the lock-on mapper, Streets of Rage 3 for its protection.
5. The VSRAM left-column quirk and the sprite attribute cache.
6. Merge with the sound track's `MdSoundUnit` (real YM2612) and run `--md-golden` on the merged tree: the golden pictures do not depend on audio, but a sound unit that stalls the 68000 differently would show up there.
7. A Lite speed measurement in a real browser window (the AOT publish exists: `dotnet publish WebLite/BrokenNes.Lite.csproj -c Release -p:EnableWasmAot=true`, served with `SubProjects/LiteStaticServer`).

## Reproduce

```
dotnet build Workshop\BrokenNes.Workshop.csproj -c Release --artifacts-path %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_sega_b2a
Workshop.exe --md-pic-test                       # 93 checks
Workshop.exe --md-golden [--update] [--poison]   # the golden pictures (ROMs from games: and tests: folders, see Workshop\MdGolden\md_golden.json)
Workshop.exe --md-find <rom> --shots a.png,b.png --scan --input "737:Start,743:"   # which frame of ours matches each screenshot
Workshop.exe --md-render --bstate <blastem quick-save> --png out.png                # our renderer on BlastEm's saved VDP state
Workshop.exe --img-diff a.png b.png --search --out diff.png
Workshop.exe --md-run <rom> --frames 600 --alloc-probe
pwsh UAT\entrypoints\sega-preview-smoke.ps1 ; pwsh UAT\entrypoints\desktop-smoke.ps1
```

The BlastEm drivers (`Workshop\Sega\oracle-tools\md-picture\`): put BlastEm 1.0.0 in a `blastem\` folder next to the scripts; `bl.ps1 -Rom r -OutDir d -Script "w2000,k:Start,w3000,shot"` runs it
(actions: `wN` wait ms, `k:Key` tap, `d:Key` hold, `u:Key` release, `shot`, `pause`, `adv[N]`, `snap` quick-save state, `resume`); `cmp.ps1` and `shotcmp.ps1` render and diff; `roster_scan.ps1` is the ten-game
climb; `probe.py` plus `m68kasm.py` build the timing probe ROMs; `poison.ps1` is the proof of failure (edits the source files, rebuilds, restores them: run it on a clean tree). The Workshop exe
is a WinExe: start it with `Start-Process` and redirect its output (`ws.ps1` does).
