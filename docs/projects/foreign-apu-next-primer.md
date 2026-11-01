# Primer: Foreign APU cores, follow-up test batch (fresh session)

Paste everything below the line into a new Claude Code session opened on this repo.

---

You are picking up the **Foreign APU project** in BrokenNes2 (this repo, `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes`, branch `main`).
27 sound engines ported from old emulators now fill 33 console slots (NES `APU_*` "Foreign" family, SNES sound units, Game Boy chips,
Master System / Game Gear PSG, Japanese Master System FM, Genesis sound units). They were written quickly by a cloud agent swarm and
merged to `main` on 2026-11-01 (merge 40eb1fab). They build, survive random register writes (`Workshop --apu-fuzz`) and a few swap /
state-load checks, but **no one has proven each one actually makes the right sounds**, and **save states are known to be weak**.

## Read first

1. `docs/projects/foreign-apu-project.md`: the status table (which ids sit in which console), the per-core batch tables (source,
   version, quirks), the user's decisions (keep each original's bugs: they are the sound; add no effects).
2. `docs/KNOWN_GAPS.md`: every shortcut (FA-* rows). FA-3 (SNES engine state not saved) and the NES ports not refreshing after a state
   load (P3) are directly relevant.
3. `THIRD_PARTY_NOTICES.md` §2a: the license classes (an accepted exception; don't change it).
4. Workshop's `--apu-fuzz` modes (`Workshop/cli.sh`, `Workshop/Program.cs`): plain, `--swap`, `--md-reload`, `--fm-reload`.
5. The memory notes for this project folder (auto-loaded): especially the self-test toolset, the Sega cores plan (the mix lab, the
   dead-combo sweep, the bridges), the SNES frankenstein and GB cores notes.

## Rules (the user's, non-negotiable)

- Never touch the user's `%APPDATA%\BrokenNes` config or saves, or their Mesen settings: every app run uses scratch
  `BROKENNES_CONFIG_DIR`, `BROKENNES_SAVES_DIR`, `BROKENNES_BUGS_DIR`.
- No downloads without asking the user first. Commercial ROMs and their data never go in git.
- UAT: `UAT/run-suite.ps1 -Exe <your fresh build>` (the default Workshop copy is stale and hangs).
- Commit each verified step; push `main` (to `refs/heads/main`, after an ancestor check) once a phase is green.
- One topic per message to the user. Pause all work at 90% weekly usage.
- Every new check gets a **negative control** (plant the fault, prove the check catches it).
- Fidelity: if a core's channel is silent **because the original emulator was**, that is the sound, not a bug: document it in
  KNOWN_GAPS with the evidence (the original's source line), don't "fix" it. If it's silent because of our port, fix it.

## Phase 1: every channel of every new core makes sound

Prove, per core and per channel, that the channel is audible and not accidentally muted.
- Instrument it: a test ROM per console (or a direct register driver in Workshop, like `--apu-fuzz` does) that plays one channel at a
  time ("beep boop"): NES pulse 1/2, triangle, noise, DMC (+ expansion audio where the port has it, e.g. MMC5); SNES the 8 DSP voices
  (BRR sample, each voice alone, plus echo on/off); Game Boy square 1 (with sweep), square 2, wave, noise; SMS/GG PSG tones 1-3 + noise
  (GG stereo panning); YM2413 the 9 melodic channels and the rhythm mode drums; Genesis the 6 YM2612 FM channels (channel 6 as DAC,
  channel 3 special mode) + the PSG.
- Measure each channel's isolated output (RMS / peak, and a pitch check that it follows the written frequency), compared with the
  stock core for the same writes. Report a table: core x channel -> sounds / silent / wrong pitch, with the stock reference.
- Keep the renders (WAVs in a scratch folder, not git) so the user can listen to any cell.
- Deliver: a Workshop mode (e.g. `--apu-channels`) that runs the whole matrix, its negative control (a muted channel is caught),
  fixes for port bugs, KNOWN_GAPS rows for intended silences.

## Phase 2: save states with the new cores

- Validate first: for each new core, on each console it sits in, play a test ROM / game, save a state mid-note, keep playing, load the
  state, and compare the audio after the load with the audio after the save (sample-exact where the engine is deterministic, else a
  tolerance you justify). Also: save on one core, load on the same core in a fresh session; desktop quick save / quick load; Lite if
  applicable. Expect failures (FA-3 and the NES ports' missing `SetState` refresh are known).
- Then fix / implement: each port saves and restores its own engine state (or a documented equivalent), versioned, loading older
  states without crashing, so they behave like the stock cores. The swap paths (`--swap`, `--md-reload`, `--fm-reload`) must stay
  green, and `--snes-state-test`, `--md-state-test`, `--timeflow-test` must too (add exclusions only with a reason, as before).
- Deliver: a state check per core (with negative control), the fixes, KNOWN_GAPS rows closed with their commit hashes.

## Phase 3: the cross-console fuzzer, focused on the new cores

Shuffle cores between consoles as the earlier console rounds did (the mix lab, the dead-combo sweep, the bridges: `NesApuOnSnes`,
`SnesApuOnChip`, `GbApuOnNes` and the Sega bridges), but weight the sweep toward the new cores: every new sound core placed on every
console it can reach through a bridge, swapped live mid-game, swapped with a state load in between, under the corruption tools.
- Pass: no crash, no exception into the audio thread, no permanent silence (sound recovers or the core reports why), no state
  corruption carried back to the stock core.
- No silent fallbacks: a rescue adapts the core, it never secretly substitutes the stock one (the user's rule).
- Deliver: the sweep mode, its results table (dead combos named with the reason), fixes, and the regression suites green.

## Wrap-up of each phase

Run the suites (`--debug-test`, `--movie-test`, `--bug-test`, the state tests, `--timeflow-test`, `--sega-bench --suite`, the `--apu-fuzz`
modes, UAT with `-Exe`), commit, push, update `docs/projects/foreign-apu-project.md` (status + what's left) and `docs/KNOWN_GAPS.md`,
and tell the user in plain words what works, what doesn't, and what they can listen to.
