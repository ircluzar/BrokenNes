# Butchering replay research (proof of concept, 2026-11-07)

Goal: prepare Butchering mode for a future glitch-harvester-like system, where a corruption can be saved and replayed. Butchering
generates *corruptors* (processes running beside the game), not corruptions (fixed byte writes, as in RTCV). A replayable Butchering
corruption therefore needs: the setup (effects and knobs), the game savestate, a seed, and the input (plus any timed setup changes).

## The experiment

`tools/butcher-replay` (`breplay <console> <outdir> [--frames N]`, built like `tools/butcher-scenarios`). For the control (all off)
and each of the 75 catalog effects switched on alone, from one entry state, with the same scripted input, picture and sound hashed
every frame:

| run | what | asks |
|---|---|---|
| A | load state, apply the effect, run | a "blast" from a state |
| B | load state again without re-applying | does the effect carry its own state over a Reload State? |
| C | apply the same settings again, load state | does re-applying reset it? |
| D | brand-new machine, same warm-up, load, apply | the reference: a fresh replay |

Runs: NES (Super Mario Bros., 600 frames), SNES (Super Mario World), Game Boy (Super Mario Land), Genesis (Sonic), Master System
(Sonic 2), 300 frames each. Results in `C:\BrokenNes-work\breplay_out\<console>.txt`.

## Results

| console | fresh-repeatable (A = D) | reload-repeatable (B = A) | reapply-repeatable (C = A) |
|---|---|---|---|
| NES | 72 / 76 | 44 / 76 | 55 / 76 |
| SNES | 68 / 76 | 35 / 76 | 50 / 76 |
| Game Boy | 69 / 76 | 34 / 76 | 53 / 76 |
| Genesis | 69 / 76 | 34 / 76 | 51 / 76 |
| Master System | 71 / 76 | 38 / 76 | 52 / 76 |

(76 = the control + 75 effects; some effects are inert on a console or in that stretch of play: they count as repeatable trivially.)

1. **The emulator and nearly every effect are deterministic from a fresh start.** Everything off replays exactly everywhere, and
   ~90 % of effects give the same frames and sound on a brand-new machine. Nothing in the effects engine reads the clock or
   `Random.Shared`; each effect group has its own xorshift generator with a constant seed.
2. **A Reload State does not reset the effects (about half leak).** Sprites, sound events, bands, the generated tones (hum,
   injection, sweep), capacitors, snow / roll, RAM nudges and the unsafe events keep their generator position, timers, phases and
   filter memory across a state load: the savestate rewinds the game, not the corruptor. This is why every reload gives a new
   result in the app today.
3. **Re-applying the same settings resets only some groups.** `Butcher.Apply` rebuilds the pin faults with fixed seeds (they reset)
   and reseeds a group when its settings change; re-applying identical settings leaves bands, tones, capacitors, snow / roll and
   most unsafe effects running on.
4. **A few effects are not repeatable even from a fresh machine** (A differs from D):
   - unsafe effects that reach into the machine itself: `powerSag`, `looseCart`, `esd`, `vramRot`, `dramRot`, `ramRot`,
     `echoAbuse` (SNES). They change state the savestate does not hold, or depend on what earlier runs left in the machine object;
   - `sound.stutter` on SNES, Game Boy, Genesis and Master System, and `sound.detune` on Game Boy: their history (the replayed
     writes) is not cleared when they are switched on.

## What a replayable corruption needs (design notes for later)

- **One master seed and a full reset ("blast")**: `Butcher.Reset(seed)` puts every group's generator, timers, phases, filters,
  history buffers and band lifetimes back to values derived from the seed. Called when a corruption starts and on every replay.
- **The effects' state in the savestate** (alternative or complement to the reset): so a state taken mid-corruption resumes it.
- **Timed setup changes**: settings applied at frame boundaries and recorded with their frame (an automation track), so knob moves
  replay at the same frames.
- **Input**: the pad input recorded as a movie (Timeflow records pads) - a Butchering corruption follows the game's path, unlike an
  RTCV byte list. Without it a replay matches only until the input differs.
- **The unsafe effects that touch the machine** need their own audit: what they change outside the savestate, and whether that can
  be saved or derived from the seed.



## The seeded reset (built 2026-11-07)

- `ButcherSettings.Seed` (long, 0 = built-in seeds) is the master seed. `Butcher.Reset(seed)` copies every private field's starting
  value from a brand-new engine of the same console (host-attached memory, callbacks and `LcdHost` are kept), re-applies the
  settings and derives every `rng*` generator and pin-fault seed from the master seed (`SeedRng`, SplitMix mix of seed + name).
- Every host's `LoadState` calls `Butcher.RequestReset()`; the reset runs at the start of the next `FrameStart`. `Apply` also
  requests one when the seed changes, so a new seed starts the corruption over.
- `breplay --seed N`. Rerun, 300 frames (NES 600 at seed 0), seed 777:

| console | fresh (A = D) | reload (B = A) | reapply (C = A) |
|---|---|---|---|
| NES | 77 / 77 | 77 / 77 | 77 / 77 |
| SNES | 77 / 77 | 76 / 77 | 76 / 77 |
| Game Boy | 77 / 77 | 77 / 77 | 77 / 77 |
| Genesis | 77 / 77 | 76 / 77 | 76 / 77 |
| Master System | 77 / 77 | 77 / 77 | 77 / 77 |

(77 now includes the `seed` setting itself.) Even the machine-touching unsafe effects replay now. Two leftovers, both interrupt lines
held outside the savestate: `unsafe.dropNmi` on Genesis and `unsafe.stuckIrq` on SNES (same on a fresh machine, differ on a reload
of a machine that already ran them). Next: audit those two, then the effects' state inside the savestate, timed setup changes, input.