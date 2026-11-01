# Round 6: the VST grows tabs: Emulator, Instruments, Presets (brief for the agents)

Date: 2026-10-17. Written from the user's request and their answers to the interview. Read `docs/projects/round5-brief.md` first (hard rules, budget protocol, traps), then this page, then your own prompt.

## The user's vision, in their words (summarised)

The emulator's corruptor is good at finding nice corruptions to replay and take instruments from, but it is **not precise enough for corrupting instruments**. So the VST gets "a very specific set of tools for corrupting instruments directly".

1. **Reshape the plugin window a little** so the right side can run **in tabs**, like the RTC panel in the main emulator.
2. **Tab 1, Emulator**: exactly what the right side holds today.
3. **Tab 2, Instruments**: works **in tandem with Instrument Runaway** to give a better view of the instruments loaded in the game, **with the settings attached to each** (the configuration that makes it what it is; on the SNES, a sampler, that is more complicated than on synth-based chips). The settings display should let the user see how instruments work and **tweak them manually**; more importantly **corrupt instruments on demand**: a **fully randomised shuffle** or **little nudges**, to break an instrument and make their own corrupted ones.
4. **Tab 3, Presets**: **no presets are provided**. The user sends an **uncorrupted or corrupted instrument from the Instruments tab to Presets**: ripping instruments from games. Those can then be **reloaded in direct mode without a ROM**.
5. (From the interview) They also want to **resume the game with the corrupted instruments, or inject them back into the running game, to hear the song resume with the corrupted instruments.**

## Decisions (the interview, 2026-10-17)

| Question | Decision |
|---|---|
| Scope of the first version | **Every console, shallow** (SNES, Genesis/Mega Drive, Game Boy/Color, Master System/Game Gear, NES). SNES and Genesis get the most depth because Runaway captures real instruments there. |
| Preset storage | **A disk library shared by every project, plus a copy embedded in the FL project** (the project still plays when a file moves). |
| What an edit changes | **The Runaway copy (what FL notes play), plus an option to push the edited instrument into the running game** so the song resumes with it. |
| Layout | **Widen the window on the Instruments and Presets tabs**; the Emulator tab stays exactly as it is, the game picture stays visible on the left. |
| Corruption tools | **Randomise all with a strength; nudge; field locks; history with undo, redo and seeds.** Plus pushing into the game (above). |
| Preset content | **One instrument (sample, loop, envelope, gain, tuning, flags) plus the learned note behaviour** the `runaway` agent is working out (the time-varying part of a corrupted driver), when it exists. |
| Delivery | **All at once**: one merge, one hand-off build (internally in phases). |
| Playing a preset | **Both**: one Presets chip that plays any console's preset, and shortcuts under each console's own chips. |

## Architecture (the lead's plan; agents may refine with evidence)

### The instrument model (pure library, no UI)
A console-neutral description: an **Instrument** is a set of typed **fields** (id, display name, kind: enum / integer range / bit field / signed, min, max, current, a one-line explanation of what it does) plus zero or more **blobs** (a SNES BRR sample, a Genesis DAC sample, a Game Boy wave RAM, a NES DMC sample). Per console:
- **SNES**: sample from the directory (BRR bytes, block structure: header range/filter/loop/end bits and 4-bit samples), loop point, ADSR1/ADSR2/GAIN (mode and value), voice volume L/R, tuning (pitch base), echo / noise / pitch-modulation flags. Source: `SpcSnapshot`/`SnesSampler`. Corruptible: header bits, nibbles, loop offset, envelope fields, gain, volumes, tuning.
- **Genesis**: YM2612 patch (algorithm, feedback, four operators: DT, MUL, TL, RS, AR, AM, D1R, D2R, SL, RR, SSG-EG; pan; AMS/FMS), DAC samples (8-bit PCM, rate, loop), PSG tone/noise settings. Source: `SegaSnapshot`/`SegaSamplers`.
- **Master System / Game Gear**: PSG settings, YM2413 user instrument (8 bytes) and the choice among the ROM instruments.
- **Game Boy / Color**: pulse duty/envelope/sweep, wave RAM (32 nibbles), noise parameters. Source: `GbSampler`.
- **NES**: pulse duty/envelope/sweep, triangle, noise mode/period, DMC sample.
"Shallow" for the non-SNES/Genesis consoles means a field list with ranges, nudge, randomise and presets, no special editors.

### The corruptor for instruments (pure, deterministic)
Operations on an Instrument: `Randomise(strength 0..1)`, `Nudge(field or random field, step)`, `FlipBits(blob/field, count)`, with **field locks** honoured, driven by a **seeded PRNG** (xoshiro-style, stored with the operation) so the same seed and operation reproduce the same corruption. A **history** (list of operations with seed and parameters) gives **undo, redo and replay from the clean instrument**; a corrupted instrument is the clean one plus its history, and also stored as the final bytes (so it does not depend on the corruptor's version).

### The Instruments tab
Lists the instruments of the frozen game (Runaway's own picker content; follows whatever the `runaway` agent decides an "instrument" is, including learned note behaviour), shows the fields of the selected one (sliders / steppers / bit toggles / hex for blobs), a waveform view for samples with loop markers (BRR decoded for display), the corruption tools (strength, Randomise, Nudge, locks, undo/redo/seed), "Send to Presets", and "Push into the game". Edits take effect on the Runaway copy immediately (audible from FL notes, no allocation on the audio thread).

### Pushing into the running game (honest design)
Writes the edited instrument into the **running game's** sound memory so the song resumes with it. What persists and what does not depends on the console and has to be measured and stated on screen, never silently:
- SNES: sample bytes and directory live in audio RAM and persist until the game reloads them; ADSR/GAIN/volume/tuning are rewritten by the SPC700 driver at every note, so a **key-on override** keyed on the sample number (when a voice with SRCN X is keyed on, force the edited envelope/gain/volumes) is probably needed (a small optional hook in `DSP_SFC` with a no-cost default; prove the default path is byte-identical).
- Genesis: the driver rewrites the YM2612 patch per note; a **patch substitution on key-on** (when a channel's registers equal the original patch, replace them with the edited patch) is probably needed. DAC samples: replace on the stream.
- Game Boy / Master System / NES: best effort per chip, or "cannot push on this console: <reason>" on screen.
Whatever cannot be done is refused with a reason (no silent fallback).

### The Presets tab and the preset file
`*.bn2preset`: versioned container, UTF-8 JSON header (name, tags, notes, console, source game name and ROM SHA-1 for reference only (never the ROM), format version) + binary blobs + **a reserved block for learned note behaviour** (filled when the `runaway` agent's capture exists) + CRC-32. Library folder `%APPDATA%\BrokenNes\Presets` (subfolders = categories), listed in the tab with rename / delete / export / import / audition. A preset used in a project is **embedded in the project** (new versioned section, e.g. `PRS1`; old projects load unchanged, a damaged section is refused with a message). **No presets ship with the product, and ripped presets contain copyrighted samples: they stay local, never in the repository** (add a `.gitignore` rule and a test that fails if a preset file is tracked).
Playing in direct mode: a **Presets chip** in the chip list that plays any preset on a small private engine per console (SNES: `SnesSampler` over a synthesised audio RAM; Genesis: the YM2612 direct chip with the patch; and so on) with the preset picker in the plugin, **plus a shortcut under each console's own chip list**. Bit-exactness target: a preset played in direct mode renders the same PCM as Runaway playing that instrument from the game it was ripped from (same note, velocity).

### The window
Fixed-size, custom-drawn GDI editor (`Bn2Editor.cs`, 904 x 664 today; screen 520 x 488 on the left, a 332-pixel column on the right). Tabs on the right column: **Emulator** (unchanged content, unchanged behaviour, unchanged parameter layout for FL's host), **Instruments**, **Presets**. The Instruments and Presets tabs **widen the window** (the picture stays visible); the self-test drives the real editor with real mouse messages, so the new tabs need stable control ids and must be reachable by it.

## Phases and domains (agents work in separate worktrees; the lead merges)

| Id | Domain | Depends on |
|---|---|---|
| `runaway` (running) | Runaway capture: static instrument data vs time-varying driver behaviour; learned note macros | none |
| `ui` | the tab framework and window widening in `Bn2Editor.cs`, the Emulator tab unchanged, the Instruments and Presets tab shells and the shared widgets (scrolling list, slider/stepper/bit toggle/hex field, waveform view); self-test coverage with real mouse messages | none (shells use a stub model) |
| `imodel` | the instrument model, per-console readers/writers over the snapshots, the corruptor, history, seeds, locks, tests (new folder `Plugin/BrokenNes2.Plugin/Instruments/`) | reads the snapshots; no UI |
| `presets` | preset file, library, project section, direct-mode chips, import/export | `imodel`, `ui`, the `runaway` result |
| `inject` | pushing an edited instrument into the running game (hooks in the sound cores, per-console feasibility measured) | `imodel` |
| the lead | merges, regressions, hand-off build, test-round page, artifacts | all |

Edits outside your domain are small, additive and named in your report. Shared files that more than one agent touches (`Bn2Editor.cs`, `Bn2Plugin.cs`, `Game.cs`): merge `refs/heads/main` often and keep your changes in separate regions/new files where you can.

## Verification expectations (every verifier shown able to fail)
Model round trips (read -> write -> identical bytes), seed determinism and locks, undo/redo/replay equal to the final bytes; Runaway's output for an edited instrument equals a direct render of the same edit; direct-mode preset replay equal to the source instrument's Runaway render; project persistence with old and damaged sections; real-mouse self-test scenarios for the new tabs; nothing allocates on the audio thread; the existing FruityHost self-test, `--corrupt-test`, state tests and UAT suite stay green; a clean game plays exactly as before (goldens).
