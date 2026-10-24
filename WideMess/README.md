# WideMess

WideMess is BrokenNes's souped-up SNES fantasy console.
- The plan: `docs/projects/widemess-plan.md`.
- The hardware contract, spec v1: `docs/projects/widemess-spec.md`.

- Making a game for it, the devkit (milestone 5): `docs/projects/widemess-devkit.md`; the devkit itself is `BrokenDev/widemess/`.

This folder holds what is built for WideMess outside the emulator code.

| Path | What |
|---|---|
| `testroms/layers/` | Test ROM 1, eight layers of 256 colours at 400 x 224 (65816 assembly, WLA-DX), with its negative control. `build.ps1` builds both into `testroms/bin/`. |
| `testroms/picture/` | Milestone 2's test ROMs: `sprites`, `windows`, `widesnes`. One 65816 register player (`player.asm`) plays the script of register writes, DMA and HDMA that `gen_picture.py` generates for each scene; the same writes drive the generator's own model of spec section 3, which draws the picture each ROM must produce (`<rom>.expect.png`). `build.ps1` builds the ROMs, their negative controls and the pictures into `testroms/bin/`. |
| `testroms/sound/` | Milestone 3's test ROM, the sound chip: a 65816 program (`sound.asm`) and an SPC700 program (`spc.asm`), WLA-DX; `gen_data.py` makes the samples, the DMA jobs, the SPC700's step lists and the manifest the checks measure against. `build.ps1` builds it and its negative control into `testroms/bin/`. |
| `testroms/cpu/` | Milestone 4's test ROMs (WLA-DX; `build.ps1` builds them into `testroms/bin/`): `speed.asm`, the fast CPU and fast DMA measured against the spec's cycle model, with its no-unlock control; `bigmap.asm` + `gen_bigmap.py`, a 4 MB game on the WideMess map (1 MB work RAM, the 20-bit WRAM port, 256 KB battery RAM, 64 ROM banks). |
| `sample/` | Milestone 5's sample game, **Wide Blaster**, made with the devkit (`brokendev new widemess widemess-sample`): C and 65816 on PVSnesLib with the WideMess extensions, its PNG, WAV and MIDI sources and `wms.json`. BrokenDev project `widemess-sample`. |
| `testroms/devkit/` | Milestone 5's test ROMs: `build.ps1` builds the devkit's template and the sample game, each with its negative controls, into `testroms/bin/` with BrokenDev's devkit. |
| `testroms/bin/` | The built test ROMs: `devkit_template.wms`, `devkit_template_nounlock.wms`, `devkit_sample.wms` (+ `.sym`), `devkit_sample_nounlock.wms`, `devkit_sample_snesspeed.wms`; `layers.wms`, `layers.sfc` (the same game as a SNES image) and `layers_nounlock.wms` (the negative control); `sprites`, `windows`, `widesnes` (`.wms`, `_nounlock.wms`) with the model's pictures (`.expect.png`, `_nounlock.expect.png`, `.<variant>.expect.png`); `sound.wms`, `sound.sfc`, `sound_nounlock.wms` and `sound.json` (the manifest); `speed.wms`, `speed.sfc`, `speed_nounlock.wms`; `bigmap.wms` (its controls, cut to 2 MB or on the HiROM map, are made by `--wms-test`). |

**The emulator side** is in `Windows/NesEmulator/snes/`:
- `WideMess.cs`: the unlock and registers (`WmsExtension`), the `.wms` file (`WmsFile`) and the picture chip (`PPU_WMS`).
- `WideMess.Picture.cs` (milestone 2): `PPU_WMS`'s wide compositor, one for the WideMess mode and the SNES modes drawn 400 wide (main and sub
  screens, windows, colour math), both sprite tables and the registers `$21A0-$21BF`.
- `WideMess.Sound.cs` (milestone 3): the sound chip `APU_WMS` (an `APU_SFC` subclass: the sound RAM above 64 KB and the port `$21F0-$21F3`), and
  `DSP_SFC.WideMess.cs`: the S-DSP's voices 8-31 and its second register page.
- `WideMess.Memory.cs` (milestone 4): the WideMess map's limits and the cartridge a `.wms` file becomes (`SnesCartridge.LoadWideMessMap`).
- `BOARD_SFC.WideMess.cs` (milestone 4): fast mode, the 1 MB work RAM and its 20-bit WRAM port, fast DMA.
- The hooks in `BOARD_SFC.cs`, `PPU_SFC.WideMess.cs` (read-only views of the SNES chip's registers; the SNES chip's own drawing is untouched), `DSP_SFC.cs` (one
  test a sample) and `APU_SFC.cs` (`$F3` with `$F2` at `$80-$FF`).
- The console is registered in `Windows/NesEmulator/systems/ConsoleSystems.cs` (key `wms`).
- It runs on `SnesSession` with `wideMess: true`.

## Tools (BrokenNes Workshop)

```
BrokenNes.Workshop.exe --wms-test  [--roms WideMess\testroms\bin] [--snes "a.sfc;b.smc"] [--frames 900] [--reference-paths]
BrokenNes.Workshop.exe --wms-pack  --in game.sfc --out game.wms [--title T] [--features 1] [--map 0|1|2] [--sram BYTES] [--battery]
BrokenNes.Workshop.exe --wms-shot  --rom game.wms --out shot.png [--frames 60] [--scale 3] [--console wms|snes] [--wide]
BrokenNes.Workshop.exe --wms-sheet --rom game.wms --out sheet.png [--frames 120]
BrokenNes.Workshop.exe --wms-run   --rom game.wms [--frames N] [--input "f:Buttons,..."] [--png-at f,...] [--out-dir d] [--voices] [--wram ADDR:LEN] [--ram-trap] [--trap-pc BBXXXX]
```

- `--wms-test` runs the milestones' checks, each with a negative control (milestone 3's sound checks are its sections S1-S6). With `--snes`, it also runs each SNES game frame for frame on the SNES
  console and on the WideMess console and requires identical pictures and sound; then draws each game 400 wide (WIDE set on the chip, the game
  itself still locked) and requires every line's columns 72-327 to be the SNES chip's own line. `--reference-paths` turns the SNES chips' fast
  paths off.
- `--wms-pack --map 2` packs a WideMess-map ROM; `--sram` is its battery RAM in bytes and `--battery` makes it the game's save.
- `--wms-shot --wide` draws a SNES game's modes 400 wide, the way the wide SNES modes show it.
- `--wms-sheet` draws the 400-wide picture and each of the 8 layers alone.
- `--wms-run` (milestone 5) runs a game and reports what it uses (unlocked, picture, layers drawing, sprites on the picture, fast CPU and DMA,
  work RAM above 128 KB, voices sounding: the measure BrokenDev's `verify` checks), the sound unit, the audio and the hottest PCs; `--voices`
  lists the voices' registers and the WideMess directory, `--wram` dumps work RAM, `--ram-trap` / `--trap-pc` stop at a crash and list the
  instructions before it.
- Workshop is a GUI-subsystem program. In PowerShell, run it with `Start-Process -Wait -NoNewWindow -RedirectStandardOutput`.

The desktop app's own check is `UAT/entrypoints/widemess-desktop-smoke.ps1 -Exe <BrokenNes.Windows.exe>`. It uses a scratch settings folder.

## Milestone 1 result (2026-10-24)

- **`--wms-test`: 21 of 21 checks pass.**
  - The test ROM unlocks WideMess and draws 400 x 224 with 1,020 colours.
  - Each layer alone shows all 255 colours of its bank.
  - The layers scroll.
  - A save state round-trips.
  - The negative controls hold:
    - registers written without the unlock change nothing;
    - on a SNES the ROM sees no WideMess;
    - the loader refuses damaged, cut and too-new files, and files needing missing features, and says why.
- **8 SNES games run frame-for-frame identical (picture and sound, 900 frames each) on the SNES and WideMess consoles:** Super Mario World,
  A Link to the Past, Star Fox (Super FX), Super Mario RPG (SA-1), Pilotwings (DSP-1), F-Zero, Super Metroid and Donkey Kong Country.
- **The desktop app smoke test passes 7 of 7.**

## Milestone 2 result (2026-10-24): the picture, complete

**What it is.** Spec section 3 (3.1-3.4) and, in `PPU_WMS`, one wide compositor for the WideMess mode and the SNES modes drawn 400 wide:
- the SNES modes 0-7 at 400: layers past the SNES's 256 columns, offset-per-tile, mosaic, hi-res averaged to 400, Mode 7 continued to both edges;
- sprites: the SNES table across 400, or the WideMess table (256 sprites, 64 a line, 8 x 8 to 64 x 64, 4 or 8 bpp, 10-bit X, slots between the 8 layers);
- windows (9-bit positions, or the SNES's at the SNES picture's place) and colour math, main and sub screens, in both.

![Super Mario World drawn 400 wide](../docs/projects/widemess-images/m2-smw-wide.png)
![F-Zero's Mode 7 drawn 400 wide](../docs/projects/widemess-images/m2-fzero-wide.png)

**How it is tested.** `testroms/picture/gen_picture.py` holds a second implementation of spec section 3, in Python. Each scene's register writes
go both into the ROM's script and into that model, and the model draws the picture the ROM must produce. Workshop compares BrokenNes's picture
with it pixel for pixel.

| Scene | What the ROM draws | Its pictures (`testroms/bin`) |
|---|---|---|
| `sprites` | The WideMess table at 400: 16 sizes, 4 and 8 bpp, flips, X = -512 / -64 / -63 / -8 / 380 / 396 / 400 / 511, Y = -8 / 216, table order over priority, `WMOBJPRI` slots, 70 sprites on a line (64 drawn), 30 sprites 64 wide on a line (128 slivers drawn), `WMOBJSTAT`. | the ROM; the no-unlock control; `WMOBJ` off (the SNES table, X centred); the WideMess mode 256 wide |
| `windows` | 9-bit windows (window 1 shaped line by line by HDMA), OR / AND / XOR / XNOR / inverted for BG1-BG8, main and sub masks, add / subtract / half, sub screen, fixed colour, clip to black, math region, the per-sprite math bit. | the ROM; the no-unlock control; the SNES's `WH0-WH3` at 400 |
| `widesnes` | Bands of mode 1, mode 0, hi-res mode 5, mode 2 (offset-per-tile with mosaic) and Mode 7 drawn 400 wide (HDMA on `BGMODE`, `MOSAIC`, `BG3VOFS`): a 64-wide map's both halves, a 32-wide map repeated, offset-per-tile columns left of the SNES edge, the SNES table across 400 (X = 0 / 255 / 256 / 300 / 327 / 328 / -72 / -100), SNES windows with an edge on the SNES edge. | the ROM; the no-unlock control (it must equal the wide picture's columns 72-327); 9-bit windows; the WideMess table in a SNES mode |

**Results.**
- **`--wms-test`: 43 of 43 without games; 70 of 70 with the 9 SNES games** (Super Mario World, A Link to the Past, Star Fox, Super Mario RPG,
  Pilotwings, F-Zero, Super Metroid, Donkey Kong Country, Chrono Trigger):
  - every test-ROM picture, control and variant is pixel-exact against the model;
  - three deliberate bugs planted to test the tests (sprite v-flip, the XNOR window, the SNES X split at 328) failed 8 checks between them, in all three scenes;
  - a save state carries the sprite table and the new registers;
  - milestone 1's 8-layer picture is unchanged (frame 61 hash `1977C01DE6BFDDE7`);
  - each game is frame-identical (picture and sound) on the SNES and the WideMess consoles;
  - drawn 400 wide, every line's columns 72-327 are the SNES chip's own line (fast and reference paths).
- **Coverage of the games drawn wide** (3,000 frames each, about 6 million lines, all identical in the centre): modes 1, 2 (offset-per-tile),
  3, 5 (hi-res), 7, mosaic, colour math, windows and sprites. The games check the centre only; the side columns are checked by the test
  ROMs' model (modes 0, 1, 2, 5, 7). Modes 4 and 6 drawn wide are exercised by no game and no test ROM yet; mode 3's sides only by the
  shared code path.
- **Speed in Workshop** (the whole console; the picture alone in brackets; best of two timings on a shared CPU): layers 489 fps (553),
  sprites 687 (957), windows 351 (341), widesnes 302 (730). Drawing allocates nothing. `--wms-test` gates at 90 and 120.
- **The SNES path is unchanged:** the SNES chip's own drawing is untouched; the ALttP speed-pass golden is IDENTICAL; an interleaved A/B
  against milestone 1's build (ALttP, 2,400 frames, 4 pairs) shows no slowdown (mean of medians 705 fps for milestone 1, 853 for this build:
  noise on a shared CPU, the code is the same).
- **UAT `run-suite.ps1`: 125 pass, 0 fail, 16 skip, 3 info.** Lite and the plugin's Core build (WideMess stays hidden there).
- **The desktop app smoke test passes 9 of 9**: it now also checks that the app shows `sprites.wms` and `widesnes.wms` exactly as the model draws them.
- **With milestone 4 merged** (WMFEAT `$3B`, state format `wms-3`): `--wms-test` 113 of 113 with all milestones' checks and the 9 games, the same
  on the reference paths; the ALttP golden IDENTICAL; desktop smoke 9 of 9; UAT 125 pass, 0 fail, 16 skip, 3 info; Lite and Core build.

## Milestone 3 result (2026-10-24): the sound chip

The design is spec section 4. In short:
- The main CPU's unlock unlocks the sound unit too.
- Voices 8-31 are three more banks of the S-DSP's voices in a second register page (`$F2` = `$80-$FF`, `WBANK`).
- Samples are BRR or 16-bit PCM, anywhere in 1 MB of sound RAM, from a 16-byte directory entry.
- The main CPU fills the sound RAM through `$21F0-$21F3`, with DMA.
- The new voices mix into the S-DSP's sums before its master volume and echo. The output stays 32 kHz.

**`--wms-test`: 71 of 71 checks pass** (milestone 1's 23, then 48 for the sound). With the nine SNES games of the rules, 80 of 80.
The sound ROM's checks measure the samples by FFT against the generator's manifest:
- **Each new voice alone:** frequency within 2 Hz, level within 4% (all 24 come out at 0.993 of the expected level), the pan, and a tone
  holding over 99% of the power.
- **All 32 at once:** 32 tones at the 32 frequencies, each within 0.19 dB of its level.
- **16-bit PCM against BRR** at pitch 1.0: harmonics at -68 dB against -29 dB.
- **The rest:** the echo with and without a voice's `WEON` bit, pitch modulation from the voice before, noise, a one-shot PCM sample, a
  ring the 65816 rewrites through the port while the voice plays it, and the playing-address registers.
- **Negative controls:**
  - The same program without the unlock: the new voices are silent, the S-DSP's 8 play, and the sound RAM above 64 KB is untouched.
  - That program on a SNES: bit-identical to it on the locked WideMess.
  - The SNES image on a SNES.
- **The rest of the machine:** a save state into a fresh session, bit-identical; the menu (WMS first) and the swap to SFC, which says so;
  0 bytes allocated over 60 frames of 32 voices.
- **Shown able to fail:** 9 deliberate bugs, each caught. They were:
  - 16-bit addresses;
  - PCM read as BRR;
  - no echo for the new voices;
  - no pitch modulation;
  - the loop bit ignored;
  - a state without the sound RAM;
  - the unlock not reaching the sound unit;
  - the port live while locked;
  - the second page readable while locked.

**SNES games stay SNES:**
- The nine games are frame-identical on both consoles, sound included, and their sound hashes equal the build before milestone 3.
- The ALttP speed-pass golden is IDENTICAL.
- Interleaved A/B on ALttP gives the new build 1.006 times the old speed (best of 3, 12 rounds). The DSP takes 15.65% of the frame
  against 15.70% before.

**Cost:** 32 voices take about 33 ms of CPU a second of sound (the S-DSP's 8: 10 ms).

**The desktop app smoke test passes 8 of 8** (a new step: the sound ROM runs on APU WMS, unlocked).

**Merged with milestones 2 and 4** (WMFEAT `$3F`, state format `wms-4`):
- `--wms-test` passes 136 of 136, and 163 of 163 with the nine SNES games, also with `--reference-paths`.
- The games' sound hashes are still the ones from before milestone 3, and the ALttP golden is IDENTICAL.
- The desktop smoke passes 10 of 10. Lite and Core build.
- The plugin self-test passes 64, with 1 warning (QLQ +1200 cents) and 0 failures. It ran on the branch merged with milestone 4.
- UAT: 125 pass, 0 fail, 16 skip, 3 info.

  From a git worktree, the suite's story and 65-o-borked ROMs exist only in the main checkout's ignored build folders, so 44 more cases skip
  there; they were linked in for this run.

**Open:**
- An SPC700-side window on the RAM above 64 KB (none now: the main CPU fills it).
- The plugin and Lite keep WideMess hidden until milestone 7.
- No sound driver yet (milestone 5).

## Milestone 4 result (2026-10-24): CPU and memory

- **`--wms-test` checks M4.1-M4.6 pass** (64 checks in all with milestone 1's), the same on the reference paths (`--reference-paths`).
  - Fast mode: the speed ROM's loops run the iterations the spec's cycle model predicts, to one iteration (loop A 3,208 a frame at SNES speed,
    12,386 fast: 3.86x; loop B, with one I/O read, 2,407 and 7,539: 3.13x). From FastROM, loop A runs 3,610 at SNES speed (the 96-clock
    prediction) and the same 12,385 in fast mode: `MEMSEL` does nothing there.
  - Fast DMA: 5,632 bytes a vblank in 512-byte transfers, 10,752 with fast DMA; fast CPU alone leaves DMA as it was.
  - The WideMess map: a 4 MB game with 256 KB of battery RAM finds all 64 ROM banks and their mirrors, the 1 MB work RAM over `$70-$7F`
    (A bus, 20-bit WRAM port, DMA both ways), and its battery save comes back in a new session.
  - Save states carry the new work RAM, the 20-bit port and fast mode. The new paths allocate nothing.
  - The negative controls hold: locked, the speed ROM measures exactly what a SNES does; a LoROM cartridge has no work RAM at `$70-$7D` and a
    17-bit port; the 4 MB game cut to 2 MB loses banks 32-63; on the HiROM map it finds ROM, not RAM; the loader refuses map-2 files the
    map cannot hold, and maps 0 / 1 that disagree with their image.
- **SNES games stay SNES:** the 9 games of the milestone (Super Mario World, A Link to the Past, Star Fox, Super Mario RPG, Pilotwings,
  F-Zero, Super Metroid, Donkey Kong Country, Chrono Trigger) are frame-for-frame identical on both consoles; the speed-pass goldens are
  unchanged with and without the reference paths; SNES speed is unchanged (interleaved A/B).
- **UAT 125 pass, 0 fail** (16 skip, 3 info); the desktop smoke test passes 7 of 7; Lite and Core build.
- Not covered by a test ROM: HDMA tables in the new work RAM (HDMA reads it through the same path as DMA, which is tested), and the old joypad
  ports' 12 clocks in fast mode (the I/O rule is tested through `$4214`).

## Milestone 5 result (2026-10-24): the devkit

**What it is** (the guide: `docs/projects/widemess-devkit.md`; the code: `BrokenDev/widemess/`):
- **PVSnesLib with WideMess extensions:** `widemess.h` (every register of the spec, the unlock and ID check) and a library in C and 65816 for
  the 8-layer mode, the video RAM and colour ports, the 256-sprite table, fast CPU and DMA, the 1 MB work RAM and the sound RAM port; the
  WideMess map's memory setup (`wmshdr.asm.in`: PVSnesLib's HiROM map, far RAM in banks `$70-$7D` through its SLOT 3).
- **Converters:** `wmsgfx.py` (PNG to 8-bpp layers and 4/8-bpp sprites, one palette of up to 1,024 colours, a video RAM layout and an upload
  table), `wmssnd.py` (WAV to 16-bit PCM or BRR through PVSnesLib's snesbrr, MIDI to songs with drum kits, effects, the whole sound RAM image).
- **A sound driver** for the 32-voice chip (`driver/wmsdrv.asm`, SPC700): songs on 24 channels (voices 8-31), effects on any voice, loaded
  through the sound RAM port.
- **`brokendev new widemess <name>`**, the template, and WideMess projects in BrokenDev: `build`, `verify` (with `expect.wms` checks), `run`,
  `compat` (BrokenNes's column only).
- **The sample game, Wide Blaster** (`sample/`): 8 layers in parallax, up to 199 sprites with a 64 x 64 256-colour boss, a song on up to 12 of
  the new voices with 16-bit PCM and BRR, fast CPU and DMA, its pools and a 64 KB recorder in the far work RAM.

![Wide Blaster](../docs/projects/widemess-images/m5/m5-sample-play.png)

**How it is tested.** The devkit's ROMs are built by the devkit (`testroms/devkit/build.ps1`) and measured from outside the game
(`WideMessCli.Measure`: the console's state, each layer drawn alone, the WideMess sprite table, the voices' envelopes through the DSP's second
page, the work RAM above 128 KB).

| `--wms-test` | What it shows | Its negative control |
|---|---|---|
| M5.1 | the template unlocks, draws 400 x 224 in the 8-layer mode with its 3 layers and its sprite, plays its song on voices 8-31 (243 of 300 frames), runs in fast mode with far RAM in use | M5.2: the same template built with `-D WMS_NOUNLOCK` (every WideMess register written, locked): 256 wide, no layer, no sprite, no voice, one colour |
| M5.3 | the sample draws 8 layers, 147 sprites on the picture (more than a SNES table's 128), up to 12 of voices 8-31 at once (more than the S-DSP's 8) in 881 of 900 frames, fast CPU and DMA, 3.7 KB of far RAM, and its main loop keeps 60 frames a second (game_frame 888 after 900 frames) | M5.4: the sample without the unlock (nothing), the template against the sample's thresholds (3 layers, 1 sprite, 4 voices: the checks say no), the sample at SNES speed (game_frame 439 after 900: fast mode is what keeps it at 60) |

- **BrokenDev's tests** (`BrokenDev/tests/run-tests.ps1`, section *widemess devkit*): WideMess manifests and their refusals, `new widemess` and
  its refusals, a missing project folder refused, `wmsgfx`'s 8-bpp tiles byte-identical to gfx4snes's (one changed byte caught), flip
  deduplication, colour limits refused, PCM groups and BRR blocks, 24 notes at once built and 25 refused, a pitch above 4x refused, and a
  project made by `new` built with PVSnesLib that passes `verify` while its no-unlock build fails every WideMess check. 115 passed, 0 failed,
  0 skipped.
- **`brokendev verify widemess-sample`** passes (8 layers, 147 sprites, 12 voices, 830 of 840 frames sounding); `compat` works on BrokenNes, with
  game_frame changing on every frame of 600-900.
- **The existing BrokenDev projects** with this BrokenDev (the user's workspaces and his BrokenDev home only read): dry-run builds of all seven
  pass; `verify` of his own built ROMs gives every checkpoint hash of his records for vrun-nes, vrun-nested, vrun-nested-enhanced, smb1-nes and
  smb1-nested, and passes for supervrun (his record is of an older ROM); real builds of vrun-nes and supervrun in scratch copies of the
  workspaces pass, supervrun byte-identical to its reference md5, and verify.
- **Emulator changes:** none in the chips. `PPU_WMS.MainLayers` (a read-only view of `WMTM`, for the measure); Workshop's `--brokendev verify`
  and `run` take console `wms`.
