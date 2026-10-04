# BrokenNes: Sega work in progress, hand-off build {{TAG}}

Built {{DATE}} from commit `{{COMMIT}}`. This is a **work-in-progress build for you to test**: three new consoles (Master System, Game Gear, Genesis), the sound bridges to the NES cores, and the FL Studio plugin with the new chips. Everything below was checked by scripts on a machine **with no sound card**: nobody has listened to any of this yet, so sound is where your ears matter most. `CERTIFIED.txt` lists what the scripts did and did not cover for this exact build.

## 1. Put the plugin into FL Studio (the way you will normally do it)

1. Close FL Studio.
2. Start the app (`Run BrokenNes.cmd`), then **Config > Synthesizer Mode > Install to FL Studio...** (or run `Install to FL Studio.cmd`, the same thing). Pick your FL Studio version(s), accept the administrator prompt.
3. Start FL Studio, rescan plugins if it does not appear on its own, and add **BrokenNes 2** as an instrument. Delete nothing: the installer replaces the old plugin file.
4. To go back to the previous plugin, run the installer from the previous build you were using.

New in the plugin: a **Master System PSG** chip, a **Genesis (YM2612 + PSG)** chip, and **ROM mode for Master System and Game Gear games** (Genesis ROM mode is still refused with a message). The Instrument control on the Genesis chip picks one of 16 FM patches. The standalone synth (`Run standalone synth.cmd`) also works for a quick sound check without FL.

## 2. What is in the app

| Console | Status | Notes |
|---|---|---|
| NES, SNES, Game Boy, Game Boy Color | unchanged | Regression-checked; the old joke Z80 CPU core is gone (saves that listed it still load). |
| **Master System** | ready | Verified against Mesen 2.1.1 on 40 roster games; FluBBa's VDP test 55 of 55; battery saves. Pause = the **Select** key (Space). |
| **Game Gear** | ready | 160x144 window, stereo PSG. |
| **Genesis / Mega Drive** | **new, rough** | The first ten roster games run and match BlastEm's pictures at the checked frames; **nobody has played any of them through**. 3 and 6 button pads, saves. |
| SG-1000 / SC-3000 | test programs only | No real game exists in your library; `.sg` `.sc` files load. |

**Games:** use your library under `X:\EMULATION` (Sega Master System, Sega Game Gear, Sega Genesis). The Master System files are `.7z` archives: **unzip them first** (the app takes `.zip`, not `.7z`). `docs\projects\sega-rosters.md` (in the repo) lists the 60 games the work was measured on, easiest first.

**Keys (default):** arrows; **X** = A / button 1, **Z** = B / button 2, **Enter** = Start, **Space** = Select (Master System: Pause; Genesis: Mode). Genesis six-button pad: **A** = X, **S** = Y, **W** = C, **Q** = Z (the L and R keys). A game pad works too; remap in the controller window as usual.

**Saves** live in `%APPDATA%\BrokenNes\SmsSaves`, `GgSaves`, `MdSaves` (one `.sav` per game, named by hash). There are no savestates for the Sega consoles.

## 3. Sound options to try (this is the new part)

- **Master System** APU menu: `SMS` (the PSG), **`SMS-FM`** (adds the YM2413 FM chip: only Japanese games use it, and you must reload the ROM after switching), and `NES:<chip>` (the Sega sound replayed on a NES sound chip: mono, no FM).
- **Genesis** APU menu: the stock unit (real Z80 + YM2612 + PSG) and `NES:<chip>` (replayed on NES chips; FM timbre is lost, the DAC becomes noise hits).
- **NES** APU menu: new entries **`SMS`** ("From the Master System": a NES game played on the PSG) and **`MD`** ("From the Genesis": a NES game played on real YM2612 FM voices; the patch is chosen in the volume panel, "Genesis FM patch", 16 timbres). Notes below the PSG's lowest note (about 109 Hz) are played octaves up (counted, not hidden).
- Your ears: is the **FM versus PSG balance** right on a Genesis game? (It is a choice, not calibrated: PSG is quieter.) Do Genesis games sound too bright or harsh (no analog low-pass is modelled)? Does the DAC (speech, drums) sound right? Sonic 1 and Streets of Rage are good tests.

## 4. A suggested test round (roughly in order of value)

1. **FL Studio plugin**: install, load, play notes on each chip (NES chips as before, then SMS, then Genesis with a few patches), save and reload a project, try several instances. Anything crackling, out of tune or too loud?
2. **Genesis**: Sonic the Hedgehog 1/2/3 (3 has saves), Streets of Rage 1/2/3, Golden Axe, Ecco, Gunstar Heroes, Comix Zone, Phantasy Star IV (save). Play past the first level; note pictures glitches, wrong colours, sprite flicker, input trouble, freezes, and anything with wrong or missing music.
3. **Master System / Game Gear**: Alex Kidd, Sonic 1/2, Out Run, Space Harrier, Phantasy Star (save), Golden Axe Warrior (save), Wonder Boy, Shinobi; Game Gear Sonic Chaos, Shinobi, Castle of Illusion, Micro Machines (PAL). Try `SMS-FM` on a Japanese FM game (Phantasy Star Japan, Fantasy Zone II, Shinobi Japan).
4. **Bridges**: a NES game on the `SMS` and `MD` APU cores; a Genesis game on `NES:FIXS`.
5. **Everything else** you normally use: NES, SNES, Game Boy games, TAS/ML tools, the standalone synth. Anything that behaves differently from before is a regression.

## 5. Known limits (so you do not report these as new)

- **Genesis:** the roster's first ten games are "working", not "playable". No interlace mode (a game using it shows a non-interlaced picture). Some later frames differ from BlastEm and nobody has traced why. Known visual residue: H40 CRAM flicker colour phase, Chilly Willy's Direct Color DMA test, three scenes of Titan Overdrive 1. **Kid Chameleon's boot sound** differs from BlastEm's (unresolved). Real-game saves (Phantasy Star IV, Sonic 3, S&K lock-on) are only tested on synthetic programs. Speed about 3.7x real time on one core.
- **Master System / Game Gear:** Bubble Bobble (both) and a few games show a handful of isolated odd frames compared with Mesen (a VRAM access-timing effect, diagnosed, not fixed). Game Gear Castle of Illusion hangs at a blue screen (it does in Mesen too). Sonic 2 (GG) differs more than the rest. Mesen's own Z80 timing differs from the Zilog manual in a few instructions; this emulator follows the manual.
- **Sound:** the FM chips and PSG were compared with oracles sample by sample, but the **YM2413's snare/cymbal/hi-hat noise sequence** is not the oracle's; YM2612 envelopes/SSG-EG/CSM are within stated bounds, not exact. Nothing was heard by anyone.
- **Bridges:** a whole NES game on the `MD` core with the Brass patch measures about 3.6 dB louder than on the normal core; individual notes are within 1 dB.
- **Lite (browser) build:** not part of this hand-off; its speed through the page is about 3.5x real time.

## 6. If something goes wrong

Note: which console and game, which APU core, what you did, and what you saw or heard (a screenshot helps; a short description of the sound is enough). The settings that matter are in `%APPDATA%\BrokenNes\config.json`; delete a console's `.sav` file to start a game fresh. If the app will not start after a test, delete `config.json` (it is rebuilt) and tell me what was selected.

Come back with: what sounded wrong, what looked wrong, what froze, and which of the Known limits above bothers you most; the next milestone is picked from that.
