# BrokenNes: Sega cores, hand-off build {{TAG}}

Built {{DATE}} from commit `{{COMMIT}}`. This build carries the three Sega consoles (Master System, Game Gear, Genesis) at the finish the Game Boy and SNES got: playable rosters checked against the reference emulators, saves and save states, every core swappable while a game runs, picture and sound bridges to the NES, SNES and Game Boy cores, and the FL Studio plugin with the Sega chips, ROM mode, stems and Instrument Runaway. `CERTIFIED.txt` lists what the scripts did and did not cover for this exact build.

**What only you can do:** nobody has heard any of the sound (the build machine has no sound card) and FL Studio itself has not run the plugin. Section 5 is a 15-minute listening checklist; that is the last open item of the whole exercise.

## 1. Put the plugin into FL Studio (the way you will normally do it)

1. Close FL Studio.
2. Start the app (`Run BrokenNes.cmd`), then **Config > Synthesizer Mode > Install to FL Studio...** (or run `Install to FL Studio.cmd`, the same thing). Pick your FL Studio version(s), accept the administrator prompt.
3. Start FL Studio, rescan plugins if it does not appear on its own, and add **BrokenNes 2** as an instrument. Delete nothing: the installer replaces the old plugin file.
4. To go back to the previous plugin, run the installer from the previous build you were using.

In the plugin: a **Master System PSG** chip, a **Master System FM (YM2413 + PSG)** chip (the Instrument control picks one of 22 instruments: the chip's 15, six designed here, the PSG square), a **Genesis (YM2612 + PSG)** chip (Instrument = one of 16 FM patches), and **ROM mode for Master System, Game Gear and Genesis games**. In ROM mode every sound unit hands out its voices as **stems** (PSG tone 1-3 and noise; Genesis FM 1-6 and the DAC; Master System FM 1-9 and the five rhythm voices) that add up to the game's mix. **Instrument Runaway** works for the Sega chips: freeze a game, and the PSG, YM2413 and YM2612 (plus the sounds found in the Genesis DAC's write stream) play polyphonically from FL notes, saved with the project. The standalone synth (`Run standalone synth.cmd`) is a quick sound check without FL.

## 2. What is in the app

| Console | State | Notes |
|---|---|---|
| NES, SNES, Game Boy, Game Boy Color | unchanged | Regression-checked; the old joke Z80 CPU core is gone (saves that listed it still load). |
| **Master System** | finished | 20 of 20 roster games played through against Mesen 2.1.1; 97.2 percent of 96,465 stable frames identical over 118 games (99.2 percent with Mesen's own Z80 timing); the 460 dumps of your library run (438 classified fine, the rest examined); FluBBa's VDP test 55 of 55; three battery games' saves match Mesen byte for byte; save states; every core swappable in a running game. Pause = the **Select** key (Space). |
| **Game Gear** | finished | 19 of 20 roster games played through against Mesen (the 20th is a demo); two battery games' saves match Mesen; 160x144 window, stereo PSG, save states. |
| **Genesis / Mega Drive** | nearly finished | 18 of 20 roster games playable against BlastEm 1.0.0; 16 of them picture-for-picture identical on all 15 comparison pictures taken over 1,500 frames of scripted play (Flicky, Altered Beast, Sonic 1, Golden Axe, Streets of Rage, Castle of Illusion, Ecco, Sonic 2, Kid Chameleon, Gunstar Heroes, Streets of Rage 2, Castlevania Bloodlines, Contra Hard Corps, Phantasy Star IV, Sonic 3, Sonic and Knuckles with Sonic 3); of 948 ROMs in your library 892 run clean (32X carts and SVP games are refused); 3 and 6 button pads; battery and EEPROM saves (NBA Jam EU and College Slam match BlastEm's files byte for byte); **save states**; interlace mode 2; the sound unit swaps live. |
| SG-1000 / SC-3000 | test programs only | No real game exists in your library; `.sg` `.sc` files load. |

**Games:** use your library under `X:\EMULATION` (Sega Master System, Sega Game Gear, Sega Genesis). The Master System files are `.7z` archives: **unzip them first** (the app takes `.zip`, not `.7z`). `docs\projects\sega-rosters.md` (in the repo) lists the 60 games the work was measured on, easiest first.

**Keys (default):** arrows; **X** = A / button 1, **Z** = B / button 2, **Enter** = Start, **Space** = Select (Master System: Pause; Genesis: Mode). Genesis six-button pad: **A** = X, **S** = Y, **W** = C, **Q** = Z. A game pad works too; remap in the controller window as usual.

**Saves** live in `%APPDATA%\BrokenNes\SmsSaves`, `GgSaves`, `MdSaves` (one file per game, named by hash). **Save states:** **F7** = quick save, **F5** = quick load, and Save State... / Load State... in the menu, for Master System, Game Gear, Genesis, Game Boy **and SNES** games (the SNES since round 5: about 100 to 150 KB a state, taken in about 16 ms, for every game of the roster including the DSP-1, SA-1 and Super FX ones); the quick slot is one file per game in `%APPDATA%\BrokenNes\States`. In the Lite page the **Save state / Load state** buttons do the same for every console, the state kept in the browser (IndexedDB). A state belongs to the sound unit it was saved with (and, on the SNES, to the cartridge chip: the homemade DSP-1 and Nintendo's DSP-1 firmware are different chips): loading it after switching the APU core is refused with a message that names both units, and the game keeps running as it was. A SNES state saved with a NES chip as the sound unit keeps the game exactly; the NES chip's own phases start again.

## 3. Swap anything while the game runs (the Console menus)

The **CPU**, **PPU** and **APU** menus of a Master System, Game Gear or Genesis game list the console's own chip and every other family's chip as `NES:<chip>`. Pick one while the game runs: the game keeps going. If a swap cannot be done in place the app restarts the game and says why in the title bar. Some combinations are dead on purpose and are marked so in the menu with the reason (for example a Z80 or 68000 running a NES game).

**The Sega machine menu** (new, in the app's menus): video standard (NTSC or PAL), the Genesis board model 1, 2 or 3, the Master System board. Saved in the config and applied when the next game loads.

## 4. Sound and picture options (this is the part to listen to)

- **Master System** APU menu: `SMS` (the PSG), **`SMS-FM`** (adds the YM2413: only Japanese games use it; reload the ROM after switching), `NES:<chip>` (the Sega sound replayed on a NES sound chip) and **`NES-FM:<chip>`** (the YM2413 and PSG together on a NES chip, with a voice-stealing policy when there are more voices than channels: a loud newcomer takes the quietest pulse, the rest are dropped and counted).
- **Genesis** APU menu: the stock unit (real Z80 + YM2612 + PSG) and `NES:<chip>` (FM timbre is lost, the DAC becomes noise hits). The sound chips of the SNES and Game Boy families are now converted to the console's sample rate (they used to play at the wrong speed).
- **NES** APU menu: **`SMS`** ("From the Master System": a NES game on the PSG) and **`MD`** ("From the Genesis": a NES game on real YM2612 FM voices; the patch is chosen in the volume panel, 16 timbres). Notes below the PSG's lowest note (about 109 Hz) are played octaves up (counted, not hidden).
- **Picture:** the PPU menu lists **`NES:<chip>`** (the picture drawn by a NES picture chip, e.g. `NES:FIXS`; `NES:DMG` shows the 160 x 144 Game Boy window; the 4-shade chips lose colour on purpose). The NES PPU menu has **`SMS`** and **`MD`** (a NES game drawn by that VDP). Cost: the Genesis drawn by a NES picture chip runs at about 2x real time on the desktop but **below real time in the browser build**.

## 5. Listening and playing checklist (15 minutes, this closes the exercise)

Please note what is wrong, not what is right; a short description of a sound is enough.

1. **Master System, 3 minutes:** Alex Kidd or Sonic 1 for the PSG (tone, noise, volume steps); then a Japanese FM game with `SMS-FM` (Phantasy Star Japan, Fantasy Zone II): does the FM music sound like an FM chip, and are the drums right?
2. **Game Gear, 2 minutes:** Sonic Chaos or Shinobi: left/right panning of the PSG (the stereo port).
3. **Genesis, 5 minutes:** Sonic 1 (green hill music, ring and jump effects), Streets of Rage 2 (drums and bass), Ecco. Is the **FM to PSG balance** right (PSG is a choice, quieter)? Is it too bright or harsh (no analog low-pass is modelled)? Do speech and drum samples (the DAC) sound right? Press F7 mid-song and F5 later: does the music resume cleanly?
3a. **SNES save states, 2 minutes (new):** Super Mario World or any SNES game: F7 in a level, play on, F5: you are back exactly where you pressed F7 (music resumes, not restarted). Try a Super FX game (Star Fox, Yoshi's Island) and a DSP-1 game (Super Mario Kart), and the same in the Lite page with its Save / Load state buttons. Switch the APU core to the silent `HLE` unit and press F5: it must refuse and say the state was saved with the SFC sound unit.
3b. **Swap while playing:** during a Genesis song switch APU `MD` to `NES:FIXS` and back: a brief change of voices is fine, a hang or a squeal is not.
4. **FL Studio, 5 minutes:** play notes on the Master System PSG, Master System FM and Genesis chips (try several patches); load a Genesis ROM in ROM mode and solo two stems; freeze a game with Instrument Runaway and play it from the keyboard; save the project, reopen it.
5. **Anything else you normally use** (NES, SNES, Game Boy, the standalone synth): anything that behaves differently from before is a regression.

## 6. Known limits (so you do not report these as new)

- **Genesis:** two roster games are still "working", not "playable": **Strider** (its intro matches BlastEm exactly but its gameplay drifts: the level load finishes a frame later than on BlastEm, a VDP-port timing question) and **Shinobi III** (the game's random number is one idle-loop iteration off after about 700 frames: a game-side phase, not an emulation rule). Comix Zone differs from BlastEm by 1 to 3 pixels in every picture, Streets of Rage 3 is one frame behind at the last picture. Fatal Rewind and Killing Game Show run now. Not implemented: 3-wire EEPROM games, the SVP (Virtua Racing), some pirate mappers. One Titan Overdrive 1 frame (1007) is chaotic and differs from BlastEm; the H40 CRAM-flicker phase differs slightly (about 0.3 percent in the 68000's VDP write cost).
- **Master System / Game Gear:** a handful of games differ from Mesen in isolated frames (Fantastic Dizzy, Bubble Bobble, Black Belt and others); Mesen's own Z80 instruction lengths differ from the Zilog manual in a few instructions and this emulator follows the manual (14 games match Mesen only with Mesen's lengths: that is a tool setting, not a bug). Game Gear Castle of Illusion hangs at a blue screen (it does in Mesen too). Mesen drops some fast VDP writes during the picture; this emulator keeps every byte.
- **Sound:** the FM chips and PSG were compared with oracles sample by sample and by register logs, but the YM2413's snare/cymbal/hi-hat noise sequence is not the oracle's; YM2612 envelopes/SSG-EG/CSM are within stated bounds, not exact. The 6 designed YM2413 instruments and every patch are untested by ear.
- **Lite (browser) build:** not bundled in this hand-off. Tested by script (111 browser checks on all three consoles); the stock Genesis runs at about 2 to 2.4 times real time there and the Genesis on a NES picture chip below real time.
- **Not checked by any script:** sound through a real playback device, FL Studio itself, the installer's administrator prompt.

## 7. If something goes wrong

Note: which console and game, which CPU/PPU/APU core, what you did, and what you saw or heard (a screenshot helps). The settings that matter are in `%APPDATA%\BrokenNes\config.json`; delete a console's save file to start a game fresh. If the app will not start after a test, delete `config.json` (it is rebuilt) and tell me what was selected.

Come back with: what sounded wrong, what looked wrong, what froze, and which known limit bothers you most. If nothing is wrong, say so: that closes the exercise.

---
For the repository: `pwsh UAT\certify.ps1 -Only cores,desktop,plugin-host,plugin-install,plugin-synth,suite,sega-gate,sega-preview,notices` (run with `-Command`, not `-File`) re-runs the automated certification; `pwsh UAT\sega-gate.ps1 -Build -Prove` runs every Sega verifier in one table.
