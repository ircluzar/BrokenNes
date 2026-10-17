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
| 68000 work RAM (variables, sound queue) | 1.08 \| 0.75 \| 0.93 | blind: 0/7 | 4/19 \| 1/19 \| 1/10 | 10, 10, 9 of 12 \| 8 to 12 of 13 \| 8, 6, 10 of 9 |
| ROM (the whole cartridge) | 1.41 \| 0.12 \| 0.50 | blind: 0/7 | 5/19 \| 0/19 \| 3/10 | 3, 6, 8 \| 7, 10, 13 \| 1, 1, 3 |
| ROM bytes the Z80 reads (music, patches), Sonic 2 only | 1.17 | blind: 0/7 | 2/19 | 10, 12, 10 of 11 to 13 |

Why the Genesis old Runaway is blind at once: its capture reads the chip's registers as they are at that instant, and a corrupted driver has not yet written a corrupted register (0 of 8 injected driver corruptions differed in Sonic 1, `Sonic 1: frozen at once the old capture sees none of the 8 driver corruptions`); the new one records the writes the driver makes (`7 of 7 audible corruptions: the recipes differ from the clean game's`).

### (a) static data against (b) driver behaviour in a heavily corrupted state

A state made by every class together (4,624 writes), then the same writes split into the instrument data alone (BRR + directory) and the driver, registers and RAM alone:

| | SMW | DKC |
|---|---|---|
| everything (4,624 writes): the game's sound changes by | **6.66** (level x0.23) | **10.71** (level x0.02) |
| (a) the instrument data alone (1,757 | 2,517 writes) | 2.23 = **33 percent** of the distance (level x0.75) | 1.68 = **16 percent** (level x1.22) |
| (b) the driver, registers and RAM alone (2,867 | 2,107 writes) | 6.66 = **100 percent** (level x0.23) | 10.71 = **100 percent** (level x0.03) |
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
| SMW | macro 0.02, exemplar 0.37, plain 0.87, silence 8.1 | macro 0.01 to 0.04, exemplar 0.01 to 0.59, plain 0.27 to 2.23 | 3 of 8 keyed nothing at all |
| DKC | macro 0.02, exemplar 0.15, plain 1.12, silence 10.8 | macro 0.00 to 0.05 (seeds 1, 4, 5, 6), exemplar 0.00 to 0.17, plain 0.00 to 6.22 | 3 of 8 keyed nothing; **seed 7 (loud, harsh, 471 key-ons, 27 percent of the energy above 4 kHz) is not reproduced: macro 1.69, exemplar 1.81, plain 3.29, silence 1.97** |

Genesis (Sonic 1, 400 random Z80-RAM writes, DAC muted): FM macro 1.09 to 1.21 against plain 1.51 (the clean game against itself is the floor: 1.09), PSG macro 0.82 to 0.85 against plain 1.46 to 1.49. In the two corruptions that killed the driver (16 key-ons, then 0) neither is meaningfully better than silence-ish plain, which is the right answer: there is nothing to play.

SCREECH_PLACEHOLDER

## What was run

VERIFY_PLACEHOLDER

## Decisions, and what is open

- **Recipes are a new list beside the instruments, not a replacement.** The instruments the plugin has always listed are unchanged (the existing SNES/Genesis goldens and the `imodel` neutral check hold); a project without `SPR1`/`GNR1` loads as before; a newer build's sections are skipped by their length.
- **Listening starts with the game**, not with Runaway: the user never has to know to wait (the Corruption window waits for them), and a game that has been playing for a minute gives its last 30 seconds. A corruption written into a running game restarts the listening so the recipes are the corrupted driver's, not a mix.
- **A section that cannot be trusted is dropped with a note**, never repaired or guessed: no silent fallback anywhere in the path (a window of 0 says it learned nothing; an unknown recipe number plays the plain instrument and says so).
- **Interplay with the Instruments tab (`imodel`).** The model reads `SpcSnapshot` / `SegaSnapshot`, so `Recipes` and `ListenNote` are readable there; edited copies carry them (SNES: the game voices play the *edited* sample through the driver's macro; Genesis: a recipe carries the patch the game wrote, so editing the plain FM instrument does not change the game voice). The tab does not list game voices yet; the picker in the Emulator tab does.
- **Open:** the sequencer (a corrupted song never carries); the Master System / Game Gear / Game Boy / NES; the YM2612 channel-3 modes; SNES pitch modulation across voices; echo history; a way to keep several macros per recipe (the typical note is one choice); real FL (below); listening to the result (the dev machine has no sound card: every claim above is measured PCM, spectra or hashes).
- **Not done on purpose:** the Emulator tab's mouse-wheel coordinate bug (the wheel reads screen coordinates as client coordinates) is the lead's, left alone.
