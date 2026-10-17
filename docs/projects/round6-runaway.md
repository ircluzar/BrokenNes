# Round 6, `runaway`: corrupted instruments that sound clean in the plugin

Date: 2026-10-17. Domain: Instrument Runaway for SNES (SPC700) and Genesis games: what it captures, how the captured instruments are played, how the Corruption window uses it.
Branch `worktree-agent-a144df44a0d045116` (worktree `.claude/worktrees/agent-a144df44a0d045116`), builds in `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r6_runaway`, lab output in `...\r6_lab`. Nothing pushed, nothing merged into `main`; `refs/heads/main` (`ui`, then `imodel`) is merged into the branch.
User-facing text: `Plugin/README.md` ("Game voices"), `docs/handoff-testing-guide.md`. Developer notes: `Plugin/CLAUDE.md` ("Game voices").

## The report, and the answer in one table

The user's own test in FL: the corruption flow worked mechanically, but on SNES and Genesis games, instruments that sound corrupted *in the game* sounded clean in the plugin with Instrument Runaway on. Their hypothesis: the damage is in **how the game's driver plays the instruments**, not in the sample or chip data. A later observation: with a very corrupted state running and the screech playing, switching Runaway on still gave clean instruments.

**Verdict: partly right.** For the corruptions that live in the sound *program* (its code, its variables, its tables, its sequence data, the main CPU's RAM that feeds it) the hypothesis is right: Runaway of the old kind captured **nothing** of them when frozen at once (0 of 20 instruments differed from the clean game's, on both SNES games, 0 of 7 on all three Genesis games), and little ten seconds later. For the corruptions in the *instrument data* (BRR samples, the sample directory, the DSP's global registers) it is wrong: those were carried, at once and later. And in a heavily corrupted state the audible change is almost entirely the driver (static data alone gives a sixth to a third of the distance), but a good part of "the driver" is the driver *dying*, which leaves no notes to learn.

Distances below are the lab's spectral distance (log energies in 20 bands, FFT 1024; 0 = same sound). Scale: two different clean instruments of one game are about 4.2 apart; a note against silence is 10 to 20. "Old at once" is the old Runaway frozen at the start, "old after 10 s" the old Runaway frozen after the corrupted game ran ten seconds (instruments that differ from the clean game's, out of those the clean game's capture lists). "New" is the listening capture after ten seconds: the **recipes** (what the driver did with each instrument) and the instruments. Three seeds per class; Super Mario World (SMW) and Donkey Kong Country (DKC) on the SNES, Sonic 1 / Sonic 2 / Streets of Rage 2 on the Genesis. Raw output: the `final-*.out.txt` files named at the end.

### SNES (SMW | DKC)

| class | kind | the game's sound changes by | old Runaway, at once | old, after 10 s | new capture (10 s) | why |
|---|---|---|---|---|---|---|
| BRR sample data (400 writes) | (a) static | 0.49 \| 0.64 | carried: 17/20 \| 11/24 instruments differ | carried (17/20 \| 12/24) | carried (the instrument and its recipes play the damaged sample) | the damage is in the copy |
| sample directory (8 writes) | (a) static | 1.45 \| 0.45 | carried: 18/20 \| 14/24 | carried | carried | same |
| DSP global registers (6 writes: echo, FLG, master volume) | (a) static | 7.6 \| 7.5 (level x1.5 \| x0.38) | carried: 7/20 \| 24/24 | carried | carried | registers are in the copy |
| echo buffer | (a) static | 0.00 \| 0.00 | nothing to carry | nothing | nothing | inert: nothing hears the buffer's old contents |
| DSP voice registers | (b) driver | 0.21 \| 0.02 | 1/20 \| 0/24 | 0 \| 0 | recipes differ in a few | the driver rewrites them at the next note |
| driver code (30 to 43 writes) | (b) driver | 6.5 \| 10.5 (level x0.20 \| x0.06) | **not carried**: 0/20 \| 0/24 | 4/20 \| 7/24, mean 0.4 | **nothing to learn**: 0 recipes in 3 of 3 seeds, both games (the driver keyed nothing) | the corrupted program stops playing; no notes exist |
| instrument table (SMW only) | (b) driver | 1.1 | **not carried**: 0/20 | 2/20 | carried: 14 to 15 recipes, 4 to 6 unlike any clean one | the driver reads its settings from it at every key-on |
| driver variables ($0000-$01FF) | (b) driver | 4.7 \| 3.3 | not carried: 0/20 \| 0/24 | 3/20 \| 0/24 | partly: SMW seed 1 carried (12 unlike), seeds 2, 3 the driver died; DKC seeds 1, 3 carried | state the driver keeps between notes |
| sequence data, tables | (b) driver | 4.5 \| 8.9 | not carried: 0/20 \| 0/24 | 3/20 \| 0/24 | partly: SMW 9, 6, 8 recipes unlike; DKC 1 of 3 seeds | what the driver is told to play |
| WRAM (main CPU RAM, 600 writes) | (b) driver | 1.4 \| 0.00 | 0/20 \| 0/24 | 4/20 \| 0/24 | SMW carried (16, 15, 11 unlike); DKC nothing audible | feeds the driver |

### Genesis (Sonic 1 | Sonic 2 | Streets of Rage 2; chip registers are the "static" part, everything below is the driver or its data)

| class | the game's sound changes by | old Runaway, at once | old, after 10 s | new capture (10 s) |
|---|---|---|---|---|
| Z80 RAM (driver code, variables, tables) | 1.05 \| 2.86 \| 0.12 | **blind**: 0/7 \| 0/7 \| 0/7 | 4/19 \| 8/19 \| 4/10 | recipes unlike the clean ones in every seed that keeps the driver alive: 8, 5, 6 of 12 \| 6 of 6 \| 6, 6, 5 of 9 |
| 68000 work RAM (variables, sound queue) | 1.08 \| 0.75 \| 0.93 | blind: 0/7 | 4/19 \| 1/19 \| 1/10 | 10, 10, 9 of 12 \| 8, 11, 12 of 13 \| 8, 6, 10 of 9 |
| ROM (the whole cartridge) | 1.41 \| 0.12 \| 0.50 | blind: 0/7 | 5/19 \| 0/19 \| 3/10 | 3, 6, 8 \| 7, 10, 13 \| 1, 1, 3 |
| ROM bytes the Z80 reads (music, patches), Sonic 2 only | 1.17 | blind: 0/7 | 2/19 | 10, 12, 10 of 11 to 13 |

Why the Genesis old Runaway is blind at once: its capture reads the chip's registers as they are at that instant, and a corrupted driver has not yet written a corrupted register (0 of 8 injected driver corruptions differed in Sonic 1, `Sonic 1: frozen at once the old capture sees none of the 8 driver corruptions`); the new one records the writes the driver makes (`7 of 7 audible corruptions: the recipes differ from the clean game's`).

### (a) static data against (b) driver behaviour in a heavily corrupted state

A state made by every class together (4,624 writes), then the same writes split into the instrument data alone (BRR + directory) and the driver, registers and RAM alone:

| | SMW | DKC |
|---|---|---|
| everything (4,624 writes): the game's sound changes by | **6.66** (level x0.23) | **10.71** (level x0.02) |
| (a) the instrument data alone (1,757 writes in SMW, 2,517 in DKC) | 2.23 = **33 percent** of the distance (level x0.75) | 1.68 = **16 percent** (level x1.22) |
| (b) the driver, registers and RAM alone (2,867 writes in SMW, 2,107 in DKC) | 6.66 = **100 percent** (level x0.23) | 10.71 = **100 percent** (level x0.03) |
| old Runaway after 10 s on the whole state | 15/20 instruments differ (the (a) part: 19/20 for the data alone, 6/20 for the driver alone) | 19/24 differ (data alone 11/24, driver alone 10/24) |
| new capture on the whole state | 0 recipes (the game keyed nothing) | 1 recipe (the game keyed almost nothing) |

So: the garble of a heavy state is (b) by the measure of distance, 100 percent of it when the driver part is applied alone, and (a) explains a sixth to a third (the two overlap). But read the last row: in these particular heavy states the driver is **dead or nearly so** (level x0.23 and x0.02: the game mostly goes quiet), so there are no notes for any capture to learn. What the user heard as a screech is a different kind of state (a driver that is alive and playing garbage, or a voice stuck on); see "Screech hunt" below for how common that is and what is learned from it.

## What was changed

Capture (the cause): a copy of the memory at one instant holds the samples and patches but not what the driver *does* with them. Now the plugin **listens**.

| piece | where |
|---|---|
| a log of every write to the S-DSP (SNES) and to the YM2612 and PSG (Genesis); the starting register state is kept so a note is judged against the registers it was keyed with | `SpcRecipe.cs` (`SpcWriteLog`), `GenRecipe.cs` (`GenWriteLog`); hooks `APU_SFC.DspWriteTap` and `MdSoundUnit.BankReadTap` (additive, null by default) |
| **recipes**: for each distinct way the driver sounded an instrument, the key-on settings plus a **macro** of timed changes (pitch as cents from the key-on pitch, volumes, envelope, noise/echo flags; FM: the whole patch, total levels, LFO; PSG: period, attenuation, noise control), and **held voices** (a voice sounding with no key-on in the window: a stuck screech) | `SpcRecipes.Derive` / `Notes`, `GenRecipes.Derive` |
| playing them: FL's C5 is the game's own pitch, FL's pitch is added to the macro's, velocity scales the volume; the same eight / six+four voices as before | `SnesSampler.NoteOnRecipe` and macro ticks, `OpnSampler` / `PsgSampler` recipe paths |
| the Instrument picker lists "Game voice n" after the instruments; parameter 14 `Recipe` | `Bn2Params.cs`, `Bn2Editor.cs`, `Bn2Plugin.cs` |
| the Corruption window waits before it captures: **play for N s** (0 at once, 5, 10 (default), 20, 30), Runaway switches on by itself when the time is up; a corruption written into a running game restarts the listening | `Emulator.Corruption.cs`, `Corruption.cs`, `Game.cs` (`RestartListening`) |
| persistence: `SPR1` (SNES) and `GNR1` (Genesis), versioned, varint-coded, every count and range checked; damaged, truncated or newer sections are dropped and the instruments still load; an older project loads as before | `Bn2Plugin.SaveRestoreState` |
| never silent: the banner says what the listening found ("Listened for 10 s: 187 key-ons, 13 game voices"), or that the game was not played long enough; a zero window learns nothing and says so | `Bn2Editor.DrawRunawayBanner`, `ListenNote` |

Found and fixed on the way: `SpcSnapshot.HasSample` / `Resolve` allocated 576 bytes per 256-sample block on the audio thread in SNES sampler mode (LINQ); the new check measures it. When `refs/heads/main` (`imodel`) was merged, the Instruments tab's model wrote edited instruments into a copy of the snapshot that **dropped the game voices without a word** (`SnesInstruments.Write`, `SegaInstruments.Copy`): fixed, and the plugin check `runaway-recipes` now has an edited-instrument case that fails without the fix (four separate expectations).

## What this can and cannot do (the honest limits)

- Runaway plays **FL's notes**. It does not run the game's sequencer. A corrupted song (wrong notes, wrong order, wrong timing) **never** carries: the sequencing is the part of the driver the plugin does not have. What carries is *how one note of the driver sounds*.
- A recipe is a static macro of one typical note (the one with the most going on). It **cannot** reproduce behaviour that is random or depends on state that differs per note (a driver writing garbage into its voices, a pitch that drifts because a variable is corrupted), anything that needs other voices (SNES pitch modulation uses the neighbouring voice's output; the YM2612's channel-3 special mode, CSM), the echo buffer's history, or global effects that are not one voice's (echo feedback and FIR are kept as the game left them at the end, not replayed).
- A corrupted driver that **stops playing** (driver code corruptions: 0 recipes in every seed on both SNES games) leaves nothing to learn. Neither capture can carry silence.
- The Master System, Game Gear, Game Boy and NES have **no game voices** (their Runaway is the copy of the registers as before). The Genesis DAC is still the 65,536-write recorder.
- Genesis FM fidelity is bounded by the game's own FM pipeline (its audio comes about 27 ms after the logged write): the clean game's notes played from their own macros come out at 1.1 from the game itself, the old plain instrument at 1.5, so the Genesis gain is smaller (macro 27 percent closer) than the SNES's.

## Fidelity: how close the recipes get to the game

Resynthesis ceiling: the game's last 10 s played again from the recordings, against the game's own sound, 8 corruptions of 150 random audio-RAM writes (and 3 DSP-register writes) per SNES game. "[macro]" = each note with its own recorded macro, "[exemplar]" = each note with its recipe's (one per distinct instrument setting), "[plain]" = the old static instrument, "[silence]" for scale.

| game | clean game | corruptions that keep the driver playing | corruptions that kill the driver |
|---|---|---|---|
| SMW | macro 0.02, exemplar 0.37, plain 0.87, silence 8.1 | macro 0.00 to 0.04, exemplar 0.00 to 0.59, plain 0.27 to 2.23 | 3 of 8 keyed nothing at all |
| DKC | macro 0.02, exemplar 0.15, plain 1.12, silence 10.8 | macro 0.00 to 0.05 (seeds 1, 4, 5, 6), exemplar 0.00 to 0.17, plain 0.00 to 6.22 | 3 of 8 keyed nothing; **seed 7 (loud, harsh, 471 key-ons, 27 percent of the energy above 4 kHz) is not reproduced: macro 1.69, exemplar 1.81, plain 3.29, silence 1.97** |

Genesis (Sonic 1, 400 random Z80-RAM writes, DAC muted): FM macro 1.09 to 1.21 against plain 1.51 (the clean game against itself is the floor: 1.09), PSG macro 0.82 to 0.85 against plain 1.46 to 1.49. In the two corruptions that killed the driver (16 key-ons, then 0) neither is meaningfully better than silence-ish plain, which is the right answer: there is nothing to play.

## Screech hunt: the loud, harsh case the user described

The user's state was a heavily corrupted game that screeched. The lab hunts for such states (`--runaway-lab snes-screech`): random corruptions of 30, 100 and 300 audio-RAM writes (and 0, 2, 6 S-DSP register writes), the game run for 10 s, "harsh" = more than a quarter of the energy above 4 kHz and audible (rms at least 0.005 and a tenth of the clean game's) over the last 5 s.

| game | corruptions | driver still keyed notes | harsh |
|---|---|---|---|
| SMW, any audio-RAM byte | 48 | 36 | **1** (100 writes, seed 15) |
| DKC, any audio-RAM byte | 48 | 43 | 0 |
| DKC, only the driver's own memory (code, variables, tables, sequences) | 36 | 10 | 0 |
| DKC, the resynthesis lab's 8 corruptions (150 writes + 3 DSP writes) | 8 | 5 | **1** (seed 7) |

So a state that is loud and harsh while the driver is alive is **rare under random corruption (2 in 140)**: most corruptions either leave the driver playing something close to the game, or kill it. The two found are exactly the user's kind of state, and the new capture does **not** reproduce them:

- SMW seed 15: rms x3.9 of the clean game, 76 percent of the energy above 4 kHz, 196 key-ons in 10 s, 22 recipes (1 with noise, 3 with pitch modulation, 5 with echo); echo feedback register $87, echo volume $B8/$FE, echo delay register $B8; the driver wrote 11,612 voice registers and 3,414 global ones (1,235 of them to the key-on register) in the window. Distance of the game to the notes played again with their own macros **5.01**, with one macro per instrument 5.01, with the old plain instrument 5.09, to silence 9.02. The macros are no closer than the plain instrument.
- DKC seed 7: 471 key-ons, 48 recipes, 27 percent of the energy above 4 kHz: own macros 1.69, per instrument 1.81, plain 3.29, silence 1.97: closer than the plain instrument, no closer than silence.

**What it is that the macros miss is not established.** The features these two states have in common are the ones the recipes list as not replayed: pitch modulation (the neighbouring voice drives the pitch), noise (one rate for all voices) and a strong echo feedback, that is, effects between voices and global ones. An attempt to isolate it (the same corruption again with the echo output, the echo feedback, the pitch modulation or the noise switched off a quarter of the way into the window) **could not tell**: its own control (master volume muted) did not silence the sound, so the experiment cannot discriminate (the corrupted driver writes the master volume and the echo feedback twice each in the window, and may put them back). The attempt was removed from the lab rather than kept as a result.

What the user can check in FL (step by step in `Plugin/README.md`): start the corruption with *play for 10 s*, listen to whether Instrument Runaway's game voices carry the sound, and compare with the game. If a screech is still missing, the useful report is which of its features (a steady noise, a high pitched wobble, a long ringing tail) was missed.

## What was run

All on the merged tree (`refs/heads/main` 7bd01f1 merged: `ui` and `imodel`), 2026-10-17. Builds in `bn_r6_runaway`; every exe was built fresh and its exit code checked before it ran.

| what | result |
|---|---|
| `BrokenNes.Workshop --runaway-test` (scripted notes against the chips they were written to, controls, hostile `SPR1`/`GNR1`, real SMW and Sonic 1) | **82 passed, 0 failed, 0 not run** |
| `FruityHost selftest` on the Native AOT DLL (every check of the plugin, including `runaway-recipes`, `imodel`, `imodel-ui`, `ui`, `corruption`, `dpad`) | **55 passed, 1 warning (the QLQ core is an octave sharp on purpose), 0 failed** |
| `runaway-recipes` alone | passed (4 s): SNES: plain instrument side band 0.004, fade 1.00x; game voice side band 1.19, fade 1.27x; 0 bytes allocated on the audio thread for plain and game voice; the corrupted driver's bend frozen at once: 0 pitch changes, after playing 1 s: 114 to 129 pitch changes (largest 1040 cents); a 1,756-byte recipe section; 25 projects with damaged recipe sections loaded, all kept their sound memory; Genesis: 0 bytes for an FM and a PSG game voice |
| `BrokenNes.Workshop --corrupt-test` (the shared corruption library) | 275 passed, 0 failed |
| `--snes-state-test` / `--md-state-test` | 147 / 168 passed, 0 failed |
| UAT suite (`UAT\run-suite.ps1 -Exe` the fresh Workshop exe) | 125 pass, 0 fail, 16 skip (ROMs absent on this machine), 3 informational |
| `UAT\certify.ps1 -Only plugin-host,plugin-install` (private build folder through `BROKENNES_ART`) | plugin-host PASS (55 passed, 1 warning, 0 failed, 235 s), plugin-install PASS (14/14 checks, 37 s); report in `%LOCALAPPDATA%\VRUN_Nes_Dev_work\certify\20261017_154005` |

**Checks that can fail, shown failing.** Every verifier of the listening was run against a deliberately broken build, and each made a named check fail (before the merge, on the tree at `1b3f1dc`): M1 pitch changes dropped from the macro, M2 DSP writes not logged, M3 the SNES macro does not play, M4 no held voices, M5 a recipe with pitch 0 accepted, M6 FM patch writes dropped, M7 the FM macro does not play, M8 all notes one recipe (the grouping), M9 the Genesis window ignored (Workshop `--runaway-test`); P1 play-for ignored (Runaway at once), P2 `SPR1` not saved, P3 an allocation in the SNES macro step, P4 the Genesis PSG tap not installed, P5 the Recipe parameter ignored, P6 `GNR1` not saved (plugin check `runaway-recipes`). After the merge one more: M10, the edited-instrument copies drop the game voices (`SnesInstruments.Write`, `SegaInstruments.Copy`): `runaway-recipes` failed with four separate messages, restored, passes. The fifteen earlier injections were **not** rerun after the merge.

**Does the new capture catch what the old one missed, and is a broken one caught?** Yes, both are tests, not claims: Workshop SMW "frozen at once, the old capture sees nothing of it (the plain instruments render bit-identical: 77954295F9FB5C9C / 77954295F9FB5C9C)" next to "after ten seconds the new capture has the corrupted setting as a recipe" (the clean game has no such recipe: control; a second clean run finds exactly the same recipes: control); Sonic 1 "frozen at once the old capture sees none of the 8 driver corruptions (8/8 identical)" next to "7 of 7 audible corruptions: the recipes differ from the clean game's"; the plugin check on a synthetic game whose corrupted driver bends its voice: 0 pitch changes frozen at once, 114 to 129 after playing. The deliberately broken captures are the injections above.

**Clean games are unchanged.** The plain instrument paths of the SNES and Genesis samplers were compared old code against this branch before the merge (167 of 167 instruments of SMW and DKC at the start and ten seconds on, Sonic 1 and Streets of Rage 2, rendered bit-identical; the comparison tool was temporary and is not in the tree, so this one cannot be repeated), and after the merge `imodel`'s `neutral` and golden checks and `runaway-sega` (the existing goldens) pass inside the full selftest.

**Not run, so not known:** real FL Studio (the whole flow is certified through the test host that mimics FL, never through FL); any listening (the dev machine has no sound card: every claim is a measured spectrum, hash or count); recipes for the Master System, Game Gear, Game Boy and NES (none exist); the YM2612 channel-3 modes and CSM; SNES pitch modulation across voices and the echo history.

## Decisions, and what is open

- **Recipes are a new list beside the instruments, not a replacement.** The instruments the plugin has always listed are unchanged (the existing SNES/Genesis goldens and the `imodel` neutral check hold); a project without `SPR1`/`GNR1` loads as before; a newer build's sections are skipped by their length.
- **Listening starts with the game**, not with Runaway: the user never has to know to wait (the Corruption window waits for them), and a game that has been playing for a minute gives its last 30 seconds. A corruption written into a running game restarts the listening so the recipes are the corrupted driver's, not a mix.
- **A section that cannot be trusted is dropped with a note**, never repaired or guessed: no silent fallback anywhere in the path (a window of 0 says it learned nothing; an unknown recipe number plays the plain instrument and says so).
- **Interplay with the Instruments tab (`imodel`).** The model reads `SpcSnapshot` / `SegaSnapshot`, so `Recipes` and `ListenNote` are readable there; edited copies carry them (SNES: the game voices play the *edited* sample through the driver's macro; Genesis: a recipe carries the patch the game wrote, so editing the plain FM instrument does not change the game voice). The tab does not list game voices yet; the picker in the Emulator tab does.
- **Open:** the sequencer (a corrupted song never carries); the Master System / Game Gear / Game Boy / NES; the YM2612 channel-3 modes; SNES pitch modulation across voices; echo history; a way to keep several macros per recipe (the typical note is one choice); real FL (below); listening to the result (the dev machine has no sound card: every claim above is measured PCM, spectra or hashes).
- **Not done on purpose:** the Emulator tab's mouse-wheel coordinate bug (the wheel reads screen coordinates as client coordinates) is the lead's, left alone.
