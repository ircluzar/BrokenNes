# Round 6, `presets`: keep a ripped instrument, play it with no ROM

Record of the work (2026-10-17). Branch `worktree-agent-af456d789c4a5fb37`, merged with `refs/heads/main` (`runaway` and `inject` included) and re-verified on the merged tree. User-facing steps: `Plugin/README.md` ("Presets"); the hand-off recipe: `docs/handoff-testing-guide.md` (step 4c); how to work on it: `Plugin/CLAUDE.md` ("Presets").

## What exists, in one table

| Piece | Where | What it does |
|---|---|---|
| The preset file `*.bn2preset` | `Plugin/BrokenNes2.Plugin/Presets/PresetFile.cs` | versioned, checksummed, deterministic bytes; instrument now + clean + history with seeds + context + a reserved note-behaviour block; **never the ROM** (the game's name and SHA-1 only, for reference) |
| The folder library | `Presets/PresetStore.cs` | `%APPDATA%\BrokenNes\Presets` (`BROKENNES_PRESETS_DIR` overrides), subfolders are categories, search, add, load, rename, delete, export, import |
| The Presets tab behind `IPresetLibrary` | `PresetLibrary.cs`, registered by `UiBoot.RegisterPresetLibrary()` | the tab's Load / Rename / Delete / Export / Import / Audition, "Send to Presets" with a name prompt (Instruments tab) |
| Direct-mode playback | `Presets/PresetPlay.cs`, `Bn2Plugin.Presets.cs`, `NesPresetPlayer.cs` | a preset becomes a snapshot of its console around the one instrument and is played by the very sampler class Runaway uses (SNES, Game Boy, Master System / Game Gear, Genesis); the NES has a register player |
| The Presets chips | `Bn2Consoles.cs`, `Emulator.Presets.cs` | chip `PRESETS` (every console) and `PRE-NES`, `PRE-GB`, `PRE-SNES`, `PRE-SMS`, `PRE-MD` (one per console), appended after every earlier chip (indexes 23-28, 29 chips in all) |
| The project's own copy | `Presets/PresetSection.cs` | versioned section `PRS1` (the whole preset file inside, CRC-32); the project plays with the library file gone; a damaged or newer section is refused with a note on screen |
| The picker on the Emulator tab | `Bn2Editor.Presets.cs` | on a Presets chip the channel strip gives way to a banner, the Instrument row is the preset picker, the Channel row is the **Voice** dropdown for a preset that carries game voices |
| Game voices in the reserved block | `Presets/PresetNote.cs` | a SNES instrument sent from a game that was listened to (`runaway`) keeps the voices (recipes) that play on its sample alone, as `NOTE` version 1 |
| The repository rule | `.gitignore` (`*.bn2preset`), `UAT/entrypoints/notices-check.ps1`, `hygiene` check | ripped presets hold a game's samples: git ignores the extension, a check fails if one is ever tracked |

No preset ships with the product (the library is empty until the user rips one).

## The file

```
"BN2P"  u16 format (1)  u16 flags (0: any other value is "a feature this version does not know")  u32 header length
header  UTF-8 JSON, fixed key order: format, app, name, tags[], notes, console, kind, kindId, index, instrument, game, sha1, created, fields, blobs, steps, noteBehaviour
u16 blob count, then blobs: tag (u32 LE) + length (u32) + bytes
  INS1 the instrument as it is now (InstrumentCodec)      CLN1 the clean instrument (as the game had it)
  HIS1 the corruption history with every seed (InstrumentHistory.Serialize)
  CTX1 the context the engine needs besides the instrument (below)
  NOTE u16 version + bytes: the reserved note-behaviour block (version 0 = empty)
  any other tag: kept byte for byte when read and written again
CRC-32 of everything before it (the codec's own `InstrumentCrc`)
```

Limits (each enforced on read and write): the file 4 MiB, the header 64 KiB, a part 2 MiB, 16 parts, a name 80 characters, notes 4,000, 24 tags of at most 32. A file that is empty, cut short at any length, has any bit flipped, is not a preset, is newer, claims a length it does not have, has parts that disagree with each other (with a *valid* checksum: the deep read checks the history leads to the instrument, the clean instrument is the history's start, the header describes the instrument, the context fits the console), or is too big is **refused with the reason**; a damaged file in the library is still listed, with its reason, never hidden.

Context (`PresetContext`): SNES = the 128 DSP registers, plus (rare) the stretch of audio RAM a corrupted sample plays on into when its end block or loop lies outside it (up to 16 KiB); Sega = 19 bytes (PSG clock, variant, YM clock, unit kind, the PSG slot, the DAC's exact loop pitch); Game Boy = one byte (colour or not); NES = empty.

## How a preset is played (the target and what was measured)

Target: the same PCM as Runaway playing that instrument from its game, same notes, same velocity. A preset is built into a snapshot of its console around the one instrument (SNES: blank audio RAM with the sample at its address, the directory entry that points at it, the envelope / tuning / volume weights / mix flags as an overlay entry, the DSP registers with the game's voices zeroed; Sega and Game Boy: a blank snapshot of the context's kind with `SegaInstruments.Write` / `GbInstruments.Write` putting the instrument in), then handed to `SnesSampler` / `OpnSampler` / `OpllSampler` / `PsgSampler` / `GbSampler`. The engine is built on the UI thread (load, project open, Reset Console) and handed to the audio thread the way an instrument edit is (`PrepareSamplerFor` / `TakePrepared`).

| Console | Engine | Measured against Runaway |
|---|---|---|
| SNES | `SnesSampler` over the synthesised audio RAM | bit-identical PCM, 124 of 124 instruments (made-up and real games, clean and corrupted) |
| Game Boy | `GbSampler` | 32 of 32 |
| Master System, Game Gear | `PsgSampler`, `OpllSampler` | 64 of 64 |
| Genesis | `OpnSampler` (FM patches and the DAC) | 64 of 64 |
| NES | `NesPresetPlayer`: writes the preset's register fields to a private FIX chip | no Runaway sampler exists for the NES, so the reference is a bare chip written by hand with the game's registers: equal to within 1e-5 for pulse 1, pulse 2, triangle, noise. **The DMC is refused** with "cannot play on this console yet: ..." (nothing is loaded) |

Through the plugin DLL with FL's notes (two overlapping notes, a glide, a short one) the same comparison holds for 100 of 100 instruments (kinds: GbNoise, GbPulse1, GbPulse2, GbWave, OpllRom, OpllUser, OpnDac, OpnPatch, PsgNoise, PsgTones, SnesSample).

**Where "bit-identical" needs a footnote (SNES):** Runaway's copy as captured still holds two leftovers of the game that a preset starts without: the old contents of the echo buffer (the tail of the song that was playing) and the pitch registers of the game's eight voices. Against Runaway's copy *as captured* 76 SNES instruments differ: error rms relative to the signal 15.0% on average, 90.6% at worst (Donkey Kong Country sample 11, a note whose first moments are the previous song's echo). Of that, the old echo is 14.8% and the idle voices 0.4%. With both cleared (which is what a preset is) the two are the same engine and the PCM is identical; the comparison above is made that way and says so.

Nothing allocates on the audio thread: a chord for 5 s allocates 0 bytes; the first block after loading a preset or after Reset Console allocates 0 bytes for SNES, Genesis FM, Genesis DAC, Game Boy and NES (the engine was prepared on the UI thread); the control (a sample-rate change, which makes the audio side build its own sampler) allocates 363,688 bytes, which shows the meter can see an allocation.

## The project's copy

Every instance on a Presets chip writes its preset into the project as section `PRS1` (u8 version, u8 flags, u16 reserved, the preset file's length, the file, a CRC-32 of all that), before `GAM1`; the section loop reads up to ten sections. Reopening: the preset comes back from the project's own bytes (the library file is not consulted), the Presets tab lists it as "This project" (kept in this FL project only) until the library has a file with the same checksum. An older DLL skips the tag by its length. A damaged, cut-short or newer section, or a preset that does not fit, is refused with a note drawn over the picture ("The project's preset was not used: ..."), and the instance has no preset. Checked: a project reopened with no library file sounds the same (264,704 values compared), saves the same bytes again, and survives 8 kinds of damage (flipped byte in the section's own header, bad CRC, truncated, length lie, newer version, ...) with the note on screen; two instances keep two presets; renaming the library file an instance plays makes the instance follow (no second copy under the old name); deleting it leaves the project's copy playing.

## Game voices (the reserved block, filled for the SNES)

`runaway` learns, from a game that was listened to, a recipe per way the game's sound driver keyed an instrument. **A SNES instrument sent to Presets now keeps the recipes of its own sample**: `NOTE` version 1 holds `SpcRecipe.Write` bytes (the format of the `SPR1` project section: versioned, every count and range checked) of the recipes that key that sample and never switch the voice to another sample (a preset holds one sample) and that make a sound. In the Emulator tab the Channel row of a preset instance becomes **Voice**: *Plain sample, as sent* (the default whenever a preset is loaded) or *Game voice n* (the existing `Recipe` parameter, so a saved project keeps the choice).

Checked: five hand-made recipes per sample of every SNES instrument (made-up and real), two of which can play alone: exactly those two are kept, the bytes are the same every run, they come back unchanged and rewrite to the same bytes (24 presets); six wrong blocks are refused with the reason (another sample's, a recipe that switches samples, not a recipe list, an empty list, a list cut short, SNES voices in a Game Boy preset); a block of a later version is read, kept byte for byte through a rewrite, **not played**, and called "note behaviour this version cannot play" (the instrument still plays); 48 of 48 game voices played from the preset are bit-identical to the same voice played from the game's sound memory and none equals the plain sample; through the DLL with the real tab (Send to Presets from a frozen game holding five voices -> the library file holds the two that play alone -> an instance on its own emulator loads it -> the Voice dropdown lists the plain sample and the two voices -> FL's notes play each voice bit-identical to Runaway on the same game, and the project reopened with the library file deleted plays voice 2 the same).

**Not done:** Genesis recipes (`GenRecipe`) are not stored (planned as `NOTE` version 2). A Genesis recipe is keyed to a channel's FM patch *as it was at the key-on* and carries its own patch bytes; the preset engine plays a patch by its picker index, so storing a Genesis recipe means deciding what "the instrument" of a game voice is (the channel's patch? the patch plus the macro?) and that is a design question for the user, not a size question. Game Boy, Master System and Game Gear have no recipes at all in `runaway`. The audition (Presets tab) plays the plain sample, not a voice.

## What was checked, with numbers

`FruityHost preset-test [--only id] [--no-mutants]` (no DLL): **10 checks, 8,068 assertions, 17 deliberate bugs** each caught by the check named; and, in `selftest`, the `presets` check (the same, then the DLL, then 5 more deliberate bugs inside the DLL, switched on through hook 350).

| Check | What it proves |
|---|---|
| `file-roundtrip` | every instrument of every snapshot (made-up and, when present on this machine, real games), clean and corrupted, goes into a file and comes back equal; same bytes every time; the history replays to the instrument |
| `file-refusals` | cut at every length, any bit flipped, not a preset, newer, too big, parts that disagree with a valid checksum: refused with a reason |
| `file-forward` | the note block and unknown parts survive read + write byte for byte; limits on every text |
| `store-ops` | add (never overwrites: " (2)"), list, search by name / tag / console / game, rename, delete, export, import (duplicate, damaged, newer, oversize and foreign files refused), damaged files listed with their reason |
| `play-builds` | every instrument builds a playable preset or says why (NES DMC: "cannot play on this console yet") |
| `play-equals-runaway` | the table above (284 of 284) |
| `play-alloc` | the engines allocate nothing while notes play |
| `note-block`, `note-plays` | the game voices (above) |
| `hygiene` | no `*.bn2preset` tracked, the ignore rule is in place |
| DLL scenarios | chips (29, the six appended last, console follows the chip, ROM mode puts the console's own chip back, the editor lists them in Direct mode only); equal to Runaway through the DLL (100 of 100); the NES player; the project (above); allocation (above); the whole user path with **real mouse and key messages against the real library in a scratch folder** (rip a corrupted instrument with the Instruments tab, name it, Send to Presets, find it in the Presets tab, the instance that runs a game refuses Load with the reason, an instance of its own loads it and the chip switches, rename, delete in two clicks, the project's copy stays, export, import, audition, search and filter, the Emulator tab's picker); the game voices |

Deliberate bugs (a check that cannot fail proves nothing): checksum not checked; history not checked; library overwrites; import skips the deep read; search ignores tags; rename keeps the old name; delete leaves the file; export writes half the file; the SNES envelope / tuning not set on the engine; the Genesis engine plays the patch after the one asked for; the Game Boy duty not set; a game voice of another sample not noticed; Send to Presets keeps no game voice; the Runaway side played at another velocity; an engine that allocates a little on every block; the Runaway side's voice with another envelope; and inside the DLL: the project keeps no copy of the preset, the project section's checksum not checked, the library hands the plugin a file with its last byte missing, Send to Presets keeps no game voice, a preset plays its plain sample whatever voice is picked. Every one is caught, with the failing assertion's words in the report.

The merged tree (with `runaway` and `inject`) runs the whole `selftest` green: **58 results, 57 pass, 1 warning (`cores`: the QLQ core plays an octave sharp on purpose, as before), 0 failed, in 240 s** (the last full run, on the final code; `ui`, `ui-proof`, `imodel`, `imodel-ui`, `inject`, `runaway-recipes`, `params`, `consoles`, `editor`, `realtime` among them). `UAT/entrypoints/notices-check.ps1`: all checks pass, including the three new ones (the ignore rule, no tracked `*.bn2preset`, git ignores a preset anywhere); with a preset file force-added to the index the tracked-file check fails, as it must.

## Decisions worth knowing

- **Presets are Direct mode only.** A game cannot run on a Presets chip; asking for ROM mode puts the console's own chip back, a Presets chip asked for in ROM mode leaves ROM mode, and the chip list in ROM mode leaves them out. Loading a preset into an instance that runs a game is refused with the reason and changes nothing (`PresetBlocker`).
- **The NES is honest, not forced into a sampler.** It has no instrument memory to synthesise a sampler from; a NES preset is the registers the game wrote, played by a register player on a private chip. The DMC is refused, because playing a game's sample bytes through the register path would be a fake.
- **Hook numbers:** plugin hooks 340-359 (`Bn2Plugin.Presets.cs`, routed from `Bn2Plugin.cs`), text hooks 17-19 through the editor, 420 index 12 = the name field; control ids 1194-1196 (`SendName`, `SendOk`, `SendCancel`); section tag `PRS1`; chips 23-28. `runaway`'s (122-129, 150-152, `Recipe`, `SPR1`/`GNR1`) and `inject`'s (320-334, `INJ1`, id 1191) are untouched.
- **Emulator tab unchanged in every state that is not a Presets chip** (the ui work's golden rectangles and pixel diffs still pass).
- **Send to Presets asks for a name** (offered: the instrument's own, " (corrupted)" appended after a corruption; Enter adds, Esc cancels, the status line says what happened). The category folder is the game's title.

## Not tested, open

- **Real FL Studio** (the user must try it: see the guide). No sound card on the build machine: every audio claim is a measured PCM or hash, nobody has listened to a preset.
- **Genesis game voices** in presets (above); the **audition** plays the plain sample; the **NES DMC** cannot be played.
- A preset made from a game that was **not listened to** has no voices (the note block is empty); only a game whose Runaway captured recipes (SNES, via `runaway`) fills it.
- The library's file watching is a throttled rescan (1.5 s) when the tab or picker is open, not a notification; two programs writing the same folder at once is not handled beyond "never overwrite".
- Clipboard in the name field (the field has none, like the other text fields).

## An intermittent allocation in `play-alloc` (investigated 2026-10-17, by the lead after the round-6 merge)

**What happened.** In one full `certify.ps1 -Only plugin-host,...` run on the merged tree the `presets` check failed with `[play-alloc] Game Boy preset: the engine allocated 8072 bytes in 100 blocks`. The same DLL passed `selftest --only presets` three times, a second full `selftest` and the `preset-test` alone, and it has not failed since.

**What this check measures.** `play-alloc` (Preset.Checks.Play.cs) is a *pure* check: it builds a managed copy of the sampler (`GbSampler`, `SnesSampler`, `OpnSampler`) inside the JIT-compiled FruityHost process and reads `GC.GetAllocatedBytesForCurrentThread()` around 100 `Render` calls. The shipped plugin is Native AOT and its real-time guarantee is measured separately through the DLL (the `presets` and `inject` DLL scenarios read the plugin's own counter: 0 bytes for a 5 s chord and for the first block after a load).

**What was ruled out (measured, not argued).**
- *The counter.* A probe (a console program, not in the repo) showed that a garbage collection landing inside a window in which the thread allocates nothing never changes the count: 0 bytes in 2,577 windows with a collection inside, with another thread allocating and collecting concurrently. A real small allocation is reported at its size (136 to 344 bytes for a `new Thread`), not rounded up to an allocation quantum.
- *The engines' code.* The render path (`GbSampler.Render`, `APU_GB.Tick` / `FrameSequencerStep` / `ReadSamples`, `StreamResampler`) has no allocating construct; the fixtures are seeded (`Random(11)`, fixed `Randomise` seeds); no mutant switch is consulted on the Game Boy path.
- *Timing and tiering in this check.* `FruityHost alloc-probe-loop` (new, below) builds the four engines of the check hundreds of times in one process, with garbage and sleeps between iterations and with every core busy, and records every block: **12,000 windows of 100 blocks, 0 allocated**. 300 quiet and 250 loaded runs of the check in fresh processes: 0 failures. 14 more `selftest --only presets` runs: 0 failures. Forcing tier-1 promotion, on-stack replacement, `TieredPGO=0`, `ReadyToRun=0` and `TieredCompilation=0` into the window (environment variables, 8 settings x 15 runs): 0 failures.

**What is known.** The JIT *can* allocate on the calling thread: in a minimal program a single on-stack-replacement compile of a hot loop allocated 1,960 bytes on the calling thread (with TieredPGO on or off). That cannot happen in the Native AOT plugin, which has no JIT. The one failing window is therefore a one-off event of the test host's runtime on that thread, which could not be reproduced and was not attributed to a method. Honest status: **cause not located; the engine is not the cause on every evidence we can produce.**

**What changed.**
- `play-alloc` now measures **5 windows of 100 blocks per engine**, block by block into arrays made beforehand (printing inside the window had shown up in the next block). An allocation in **every** window is the engine's and fails the check (the old verdict, strengthened: the mutant that allocates on every block is caught in all four engines). An allocation in only some windows is reported as a **warning** with its size, window and block, never hidden.
- Three controls inside the check prove the verdict can tell the cases apart (a block that allocates every time = recurring, once = one-off, never = clean); with the verdict deliberately broken (any window counts as recurring) the one-off control fails, as it must.
- New diagnostics: `FruityHost alloc-probe [gb|snes|fm|dac] [blocks]` prints this thread's allocation per step of one engine (the FM engine allocates about 7 KB in its very first block, which is why the check warms up for four blocks), and `FruityHost alloc-probe-loop [iterations] [garbageMB]` is the stress loop above. If a window ever allocates again the warning names the block.

**If it comes back.** A warning (`a one-off allocation ... none in the other windows`) is that event; run `alloc-probe-loop 3000 8` and `selftest --only imodel,imodel-ui,presets` in a loop and look at the block index. An *error* (`allocates on every pass`) is a real regression in an engine.
