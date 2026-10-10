# BrokenNes: Sega work in progress, hand-off build {{TAG}}

Built {{DATE}} from commit `{{COMMIT}}`. This is a **work-in-progress build for you to test**: three new consoles (Master System, Game Gear, Genesis), the sound **and picture** bridges to the NES cores, save states for the Master System and Game Gear, and the FL Studio plugin with the new chips, ROM mode for every Sega console, per-voice stems and Instrument Runaway. Everything below was checked by scripts on a machine **with no sound card**: nobody has listened to any of this yet, so sound is where your ears matter most. `CERTIFIED.txt` lists what the scripts did and did not cover for this exact build.

## 1. Put the plugin into FL Studio (the way you will normally do it)

1. Close FL Studio.
2. Start the app (`Run BrokenNes.cmd`), then **Config > Synthesizer Mode > Install to FL Studio...** (or run `Install to FL Studio.cmd`, the same thing). Pick your FL Studio version(s), accept the administrator prompt.
3. Start FL Studio, rescan plugins if it does not appear on its own, and add **BrokenNes 2** as an instrument. Delete nothing: the installer replaces the old plugin file.
4. To go back to the previous plugin, run the installer from the previous build you were using.

New in the plugin: a **Master System PSG** chip, a **Master System FM (YM2413 + PSG)** chip (the Instrument control picks one of 22 instruments: the chip's 15, six designed here, the PSG square), a **Genesis (YM2612 + PSG)** chip (Instrument = one of 16 FM patches), and **ROM mode for Master System, Game Gear and Genesis games**. In ROM mode every sound unit hands out its voices as **stems** (PSG tone 1-3 and noise; Genesis FM 1-6 and the DAC; Master System FM 1-9 and the five rhythm voices) that add up to the game's mix. **Instrument Runaway** works for the Sega chips: freeze a game, and the PSG, YM2413 and YM2612 (plus the sounds found in the Genesis DAC's write stream) play polyphonically from FL notes, saved with the project. The standalone synth (`Run standalone synth.cmd`) also works for a quick sound check without FL.

## 2. What is in the app

| Console | Status | Notes |
|---|---|---|
| NES, SNES, Game Boy, Game Boy Color | unchanged | Regression-checked; the old joke Z80 CPU core is gone (saves that listed it still load). |
| **Master System** | ready | Compared with Mesen 2.1.1 on 118 games (97.2 percent of stable frames identical; 99.2 percent with the oracle's Z80 timing); the 460 dumps of your library all run (438 classified fine, the rest explained or compared); FluBBa's VDP test 55 of 55; battery saves; save states. Pause = the **Select** key (Space). |
| **Game Gear** | ready | 160x144 window, stereo PSG, save states. |
| **Genesis / Mega Drive** | **new, rough** | All 948 ROMs of your library were run headless (most reach a picture; 32X carts are refused). The first ten roster games match BlastEm's pictures at the checked frames; **Sonic the Hedgehog 1 played with the pad is picture-for-picture identical to BlastEm over 1,500 frames; no other game has been played through**. 3 and 6 button pads, battery saves and EEPROM games. No save states. |
| SG-1000 / SC-3000 | test programs only | No real game exists in your library; `.sg` `.sc` files load. |

**Games:** use your library under `X:\EMULATION` (Sega Master System, Sega Game Gear, Sega Genesis). The Master System files are `.7z` archives: **unzip them first** (the app takes `.zip`, not `.7z`). `docs\projects\sega-rosters.md` (in the repo) lists the 60 games the work was measured on, easiest first.

**Keys (default):** arrows; **X** = A / button 1, **Z** = B / button 2, **Enter** = Start, **Space** = Select (Master System: Pause; Genesis: Mode). Genesis six-button pad: **A** = X, **S** = Y, **W** = C, **Q** = Z (the L and R keys). A game pad works too; remap in the controller window as usual.

**Saves** live in `%APPDATA%\BrokenNes\SmsSaves`, `GgSaves`, `MdSaves` (one `.sav` per game, named by hash). **Save states** (new): **F7** = quick save, **F5** = quick load, and Save State... / Load State... in the menu, for Master System and Game Gear (and Game Boy) games; the quick slot is one file per game in `%APPDATA%\BrokenNes\States`. A state belongs to the sound unit it was saved with: switching the APU core and loading an old state is refused with a message. The Genesis and the SNES say they have no states yet.

## 3. Sound options to try (this is the new part)

- **Master System** APU menu: `SMS` (the PSG), **`SMS-FM`** (adds the YM2413 FM chip: only Japanese games use it, and you must reload the ROM after switching), and `NES:<chip>` (the Sega sound replayed on a NES sound chip: mono, no FM).
- **Genesis** APU menu: the stock unit (real Z80 + YM2612 + PSG) and `NES:<chip>` (replayed on NES chips; FM timbre is lost, the DAC becomes noise hits).
- **NES** APU menu: new entries **`SMS`** ("From the Master System": a NES game played on the PSG) and **`MD`** ("From the Genesis": a NES game played on real YM2612 FM voices; the patch is chosen in the volume panel, "Genesis FM patch", 16 timbres). Notes below the PSG's lowest note (about 109 Hz) are played octaves up (counted, not hidden).
- **Picture bridges (new):** the PPU menu of a Master System, Game Gear or Genesis game now also lists **`NES:<chip>`** (the picture drawn by a NES picture chip, e.g. `NES:FIXS`; `NES:DMG` shows the 160 x 144 Game Boy window, and the 4-shade Game Boy chips lose colour on purpose). The game keeps running while you switch. The NES PPU menu has two new entries, **`SMS`** and **`MD`** ("From the Master System / Genesis": a NES game drawn by that VDP). A bridged picture costs speed (measured headless on a busy machine: Master System 52-69 frames a second, Genesis 28-40, so a Genesis game on a NES picture chip may run **below real time**).
- Your ears: is the **FM versus PSG balance** right on a Genesis game? (It is a choice, not calibrated: PSG is quieter.) Do Genesis games sound too bright or harsh (no analog low-pass is modelled)? Does the DAC (speech, drums) sound right? Sonic 1 and Streets of Rage are good tests.

## 4. A suggested test round (roughly in order of value)

1. **FL Studio plugin**: install, load, play notes on each chip (NES chips as before, then SMS, then Genesis with a few patches), save and reload a project, try several instances. Anything crackling, out of tune or too loud?
2. **Genesis**: Sonic the Hedgehog 1/2/3 (3 has saves), Streets of Rage 1/2/3, Golden Axe, Ecco, Gunstar Heroes, Comix Zone, Phantasy Star IV (save). Play past the first level; note pictures glitches, wrong colours, sprite flicker, input trouble, freezes, and anything with wrong or missing music.
3. **Master System / Game Gear**: Alex Kidd, Sonic 1/2, Out Run, Space Harrier, Phantasy Star (save), Golden Axe Warrior (save), Wonder Boy, Shinobi; Game Gear Sonic Chaos, Shinobi, Castle of Illusion, Micro Machines (PAL). Try `SMS-FM` on a Japanese FM game (Phantasy Star Japan, Fantasy Zone II, Shinobi Japan).
4. **Bridges**: a NES game on the `SMS` and `MD` APU cores and PPU cores; a Genesis and a Master System game on `NES:FIXS` (sound and picture); look for flicker, wrong colours, missing sprites.
5. **Save states**: F7 then F5 on a Master System / Game Gear game after some play; switch away and back to the game.
6. **Plugin extras**: the Master System FM chip, Genesis ROM mode, stems, Instrument Runaway on a Sega game.
7. **Everything else** you normally use: NES, SNES, Game Boy games, TAS/ML tools, the standalone synth. Anything that behaves differently from before is a regression.

## 5. Known limits (so you do not report these as new)

- **Genesis:** only Sonic 1 is verified playable against BlastEm; the other roster games are "working", not "playable". The **vertical interrupt arrives about 780 master clocks late** (found, measured, not fixed yet): games that time raster effects to it can misbehave. No interlace mode. Some later frames differ from BlastEm and nobody has traced why. Known visual residue: the H32 CRAM flicker scene is 7 pixels off on its last line, H40 phase, Chilly Willy's Direct Color DMA test, three scenes of Titan Overdrive 1. **Kid Chameleon's boot sound** differs from BlastEm's; **Fatal Rewind** halts after Start. Not implemented: 3-wire EEPROM games, the SVP (Virtua Racing), some pirate mappers; a real Super Street Fighter II dump was never run. Speed about 3.7x real time on one core.
- **Master System / Game Gear:** a handful of games differ from Mesen in isolated frames (Fantastic Dizzy, Bubble Bobble and others; a VRAM access-timing question that the evidence says is not a hardware queue). Game Gear Castle of Illusion hangs at a blue screen (it does in Mesen too). Mesen's own Z80 timing differs from the Zilog manual in a few instructions; this emulator follows the manual. The Game Gear library sweep was not finished, and the menu-driven games' scripts are not compared with Mesen.
- **Sound:** the FM chips and PSG were compared with oracles sample by sample, but the **YM2413's snare/cymbal/hi-hat noise sequence** is not the oracle's; YM2612 envelopes/SSG-EG/CSM are within stated bounds, not exact. Nothing was heard by anyone.
- **Bridges:** a whole NES game on the `MD` core with the Brass patch measures about 3.6 dB louder than on the normal core; individual notes are within 1 dB.
- **Lite (browser) build:** not part of this hand-off; its speed through the page is about 3.5x real time, and a Sega game drawn by a NES picture chip was never seen on screen in it.
- **Not checked by anyone:** sound through a real playback device, FL Studio itself, the installer's administrator prompt, the plugin's real-mouse input test on a busy desktop.

## 6. If something goes wrong

Note: which console and game, which APU core, what you did, and what you saw or heard (a screenshot helps; a short description of the sound is enough). The settings that matter are in `%APPDATA%\BrokenNes\config.json`; delete a console's `.sav` file to start a game fresh. If the app will not start after a test, delete `config.json` (it is rebuilt) and tell me what was selected.

Come back with: what sounded wrong, what looked wrong, what froze, and which of the Known limits above bothers you most; the next milestone is picked from that.
