# Round 5, agent `corr`: corruption in the plugin. Record

Design: `docs/projects/round5-corr-design.md` (read it first; this record says what was built and how it was checked). Branch `worktree-agent-ab051cb992266ca6d`.
Work is in milestones C1 to C5, all done except what the last section lists, and the section "After snes-state merged" says what changed when the SNES got save states (numbers there and in the C4 table are the current ones); each section below says what was built, every verifier with its numbers and its proof of failure, and what is not tested.

**How a user does it, in one paragraph.** Farm a corruption in the BrokenNes desktop app (RTC blast or Glitch Harvester; SNES, Game Boy / Color, Master System, Game Gear, Genesis), keep it in the stockpile, **Save file...**. In FL, load the same game in the plugin's ROM mode, press **Corruption**, **Open stockpile...**, pick the corruption, tick "Freeze the game and switch Instrument Runaway on" and **Start**: the game is rebuilt at the place it was corrupted (a saved machine or a replay from power-on) with the corruption on it, and Instrument Runaway's picker now lists the corrupted instruments. Save the FL project and the corrupted game and its instruments come back.

## C1: design (done, commit 95ba2a4)

Research read of the corruptor, the Glitch Harvester engine, API and web module, the console sessions and the plugin's ROM mode and Runaway. Findings the rest rests on:
a stockpile has no file on disk (in-memory; only an export), Lite's export spells the machine state differently so it re-imports without one and Replay is then a silent no-op,
the NES domain sizes are guessed by probing (the UAT shows PRG 256 B), TILT and BITFLIP in the stash path are plain random bytes, the SNES has no save states (the lead has put the agent
`snes-state` on that), and the plugin already keeps the SPC700 ARAM and DSP registers of a project saved in Runaway (`SPC1`).

Decision from the lead (design section 9.4): the **new library uses true sizes for every console; the existing NES farming UI keeps its legacy sizes** behind an explicit setting
(`corruptionTrueDomainSizes`, off), documented with the measured difference, and the stockpile records the size model an entry was made with (`source.sizeModel`: `true` or `legacy`). Done in C3.

## C2: the shared library and the SNES first (done)

### What exists

`Windows/NesEmulator/corruption/` (namespace `NesEmulator.Corruption`), no UI, no reflection, linked by `Core/BrokenNes.Core.csproj` (so the plugin) and `Workshop`; the desktop compiles it by its default glob:

| File | What it is |
|---|---|
| `StockpileModel.cs` | `Stockpile`, `StockpileEntry` (name, description, game, machine, anchor, writes, source, `Summary()`, `ContentHash()`), `Anchor` (none / state / boot / ref, with `Alt`), `GameRef`, `MachineSpec`, `BlastSource`, `CorruptionWrite(Domain, Address, Value, Was, At)` |
| `StockpileJson.cs` | the v2 file: `Utf8JsonWriter` / `JsonDocument`; one line per write; atomic `SaveFile`; `Read`/`LoadFile` returning refusals and warnings, never throwing on content; older files (the engine's and Lite's exports); limits (64 MB file, 4,096 entries, 1,000,000 writes, 64 MB per state with bounded inflation) |
| `MemoryDomains.cs` | `IMemoryDomain`, `ArrayDomain`, `WordDomain`, `FuncDomain`, `MissingDomain`, `IMemoryDomainSource`, `MachineDomains.For(IConsoleSession)` (SNES complete) and `.For(NES)` (true sizes) |
| `CorruptionApplier.cs` | `Apply` (with journal, with a report that says what was skipped and why), `Revert`, `SeededRandom` (SplitMix64), `BlastGenerator` (RANDOM, TILT, NOP, BITFLIP, RANDOMTILT with final values and `was`) |
| `InputScript.cs` | the pad-per-frame text (`"60:Start,90:Right+A,120:"`, the `--console-run --input` syntax), `Player`, and `InputRecorder` (pre-sized, allocation-free, poisonable) |
| `AnchorStart.cs` | `AnchorStart.Start(entry, file, rom, fileName, console, running, options)`: game check, console check, the anchor kinds, the alternative anchor, the replay cap and cancel and progress, the private copy of the ROM, the writes; `CorruptionTimeline` for writes with `at` > 0 |

Edits outside the new folder, all additive: `SnesSession.Board` and `SnesSession.Spc` (`systems/ConsoleSessions.cs`: the memories the domains need; `Spc` is the real SPC700 while it runs, also inside a NES sound-chip bridge, null under HLE);
`SpcSnapshot` moved from `SnesSampler.cs` to its own file `Plugin/BrokenNes2.Plugin/SpcSnapshot.cs` (the same class, so the Workshop tests build the very object Instrument Runaway captures); `Core/BrokenNes.Core.csproj` and
`Workshop/BrokenNes.Workshop.csproj` link the folder; `Workshop/Program.cs` dispatches the two new modes; `Workshop/README.md` lists them.

SNES domains: `WRAM` 131,072, `VRAM` 65,536, `CGRAM` 512 (the top bit of a colour is not stored, as on the chip), `OAM` 544 (its write invalidates the S-PPU's sprite cache), `SRAM` (cartridge RAM, listed
unavailable with the reason when there is none), `ROM`, **`ARAM` 65,536** and **`DSP` 128** (written through the S-DSP's register write, so a KON write keys a voice). Under the silent HLE sound unit `ARAM` and `DSP`
are listed as unavailable with the reason.

### Workshop tools

- `--corrupt-test [--smw <rom>] [--only group,...]`: 150 checks at the end of C2 (149 run, 1 not run; below); the `engine`, `nes` (C3) and `consoles` (C5) groups bring it to 247.
- `--corrupt-apply --rom X --stockpile S [--entry id|name|number] [--frames N] [--allow-other-game] [--max-replay N]`: starts the entry per its anchor, runs the same anchor without the writes beside it, and prints per memory the bytes that
  differ (count and first addresses), the audio hash and peak of both, the picture hash of both, and what `SpcSnapshot` would hold. Exit 0 ok, 1 refused (with the reason), 2 usage.

### The test game

`Workshop/Corruption/SynthSnesRom.cs` builds a 32 KB LoROM **in memory** (nothing of anyone's, no file): its 65816 program turns the sprite layer on, uploads an SPC700 program and a one-note instrument
through the boot loader's real handshake (777 bytes), then loops copying the pad to the SPC700 and into work RAM. The SPC700 program sets up the S-DSP (sample directory at $0400, a 2 kHz square-wave BRR sample
at $0500, ADSR, key-on) and loops counting button-held passes in audio RAM word $12. So it is a small game with a visible sprite layer, a sound driver with an instrument in audio RAM, and state that depends on the
pad frame by frame in both CPUs' RAM. (Found while building it: the 65816 must wait for the loader to echo the "execute" command before it starts writing the pad to port 0, or the pad overwrites the command: the first
version sat in the loader for ever; the check "its sound program was uploaded ... and runs" caught it.)

### Verification (run on this machine, 2026-10-17)

`--corrupt-test`: **149 passed, 0 failed, 1 not run** (30 s; Super Mario World present at `!!! Experiments\AI SMW Hacker\smw.smc`). The one not run is the SNES state anchor: **it prints "NOT RUN" with the reason while the
SNES has no save states and runs for real when `SupportsState` becomes true** (the check is written against `SupportsState`, never against a console name).

| Group | Checks | What they show |
|---|---|---|
| format | 16 | write, read, write is byte-identical; every field survives (anchors with state, battery, alternative; writes with was/at; source; bases); a base nothing points to is not written; one line per write; atomic save/load; content hash ignores name and description, sees writes and anchor. Golden hash of the fixture `B9E93D96A2EF7597` (**says "unchanged since recorded", not "right"**) |
| legacy | 9 | the engine's export and Lite's export (the `BaseState` spelling) both read, with the game identity taken from the state and the user's path dropped from the entry; an entry with no machine state is read as `none`, flagged `no-state`, with a warning |
| hostile | 14 | truncated, other JSON, version 99, not a byte, not a domain id, negative address, unknown anchor kind, damaged state, missing base, 1,000,001 writes, a 70 MB deflate bomb in 92 KB of text: each refused with a message and the good entry beside it still read |
| input | 10 | scripts parse and format; the recorder records changes only; a full log is poisoned with a reason, first reason kept; **0 bytes allocated for 100,000 frames with 3,000 changes** |
| synth | 7 | the test game boots, uploads, plays (peak 2220), sets the S-DSP, loops; a held button reaches both CPUs' memory |
| domains | 20 | true sizes; round trip at first, second, middle and last byte; out-of-range ignored; CGRAM mask; the HLE case; **the OAM hook shown by its consequence on the picture** |
| apply | 8 | ROM undo exact; twice is idempotent; skipped writes counted with reasons (unknown domain, past the end, unavailable); a timeline lands a write at exactly its frame |
| blast | 12 | same seed same writes; TILT is +-1 of the byte that was there (it was a random byte in the stash path), BITFLIP one bit, NOP the console's no-op; weights; HLE never chosen; golden `44B0D16D9CDC0C31` over memories of fixed contents |
| anchors | 20 | on a test machine: none/state/boot/alt/refusals (wrong game, wrong console, state refused, cap with the numbers, cancel disposes the half-built machine and applies nothing, progress messages, save RAM before frame 0, private ROM copy) |
| boot | 8 | **determinism on the SNES test game: the same boot anchor twice gives the same work RAM, ARAM, DSP, sound and picture; A held 61 frames instead of 60 gives different ARAM and work RAM**; the replay equals a plain run with the same pad |
| audio | 11 | **an ARAM corruption (8 bytes of the instrument) changes the sound and sounds the same every time; `SpcSnapshot` of the corrupted machine differs from the clean one in exactly those 8 bytes**; a header corruption or a moved directory entry drops the instrument from the picker's list; a DSP pitch write changes the sound |
| cli | 7 | `--corrupt-apply` end to end: reports ARAM as the only memory that differs (8 bytes with addresses), sound DIFFERENT, picture SAME, the Runaway snapshot; wrong game exit 1; unreadable stockpile exit 1; missing entry exit 2 |
| state | 0 + 1 not run | see above |
| smw | 7 | Super Mario World: same anchor twice gives the same ARAM/DSP/work RAM; the input left out differs in both; Start one frame later changes the work RAM; **a real instrument's BRR data corrupted (224 bytes) changes the game's sound over 300 frames, deterministically, and `SpcSnapshot` differs in exactly the written bytes** |

**Proof the checks can fail.** Two ways, both run:
1. Controls inside the tests (a sabotaged input that the same check must catch): 17 of the 149 are controls (marked `control:`), e.g. a replay that ignores the input is told apart from the real one; a corruption that writes the bytes already there
   sounds exactly like none; the same sprite poke without the hook gives a stale picture; a valid stockpile has no refusals; the older reader (which looked for `State` only) finds no state in Lite's file.
2. Real injections into the library, one build each (all reverted from git afterwards): eight breakages applied together and the suite run: **114 passed, 28 failed** and every injection was caught by the check named for it:
   OAM hook off (the picture check), replay ignores the pad (boot/anchors/cli/SMW), `Revert` does nothing (apply), writer renames `address` (format/legacy), state limit raised to 512 MB (the deflate bomb is accepted),
   generator constant changed (the blast golden), CGRAM mask removed (CGRAM), timeline off by one frame (timeline). The OAM injection was repeated alone: the main check fails (`9426B1C18C878383` vs `E398F5EDA0DB56FB`), not only its control.
   (The first run of injections crashed the harness on a null; groups now run under a guard, so a throwing group is a failed group.)

### What C2 does not prove

- Instrument Runaway itself (the plugin's freeze and sampler) is not run here; the Workshop builds `SpcSnapshot` the way `SessionGame.CaptureSpc` does. The plugin-level proof (open stockpile, Start, Runaway, the picker) is C4's.
- Super Mario World's SPC700 ARAM does **not** change when Start is pressed 1, 2, 4 or 8 frames later (scanned at frames 400 and 600, looked at 10, 30, 90 and 300 frames after): at the title screen the music does not depend on the press timing,
  though the work RAM does. The ARAM-sensitivity claim therefore stands on the SNES test game (A held one frame longer), and on SMW only "input left out differs" (ARAM and work RAM).
- Game Boy, Master System, Game Gear and Genesis domains are not built (C5). The NES domain adapter exists (`MachineDomains.For(NES)`) but is not wired or tested (C3/C5).
- Timing of a replay in the plugin (the SNES at about 588 frames/s headless here, on a busy machine) is not measured inside the plugin.

## C3: the desktop side (done, commits 60129a9 and 564afd2)

### What was built

- **The Glitch Harvester engine speaks the v2 file** (`Windows/NesEmulator/corruptor/GlitchHarvesterEngine.cs`, `GhStockpileMap.cs`, `CorruptorModels.cs`): `ExportStockpile` / `ImportStockpile` write and read the v2 stockpile (older exports still read, Lite's `BaseState` spelling
  too; an id is kept unless it is taken), `SaveStockpileFile` / `OpenStockpileFile` (atomic save, a refusal list with reasons on open), `DescribeStockpileEntry`. The Lite web app's export and import go through the same engine, so
  two defects found in C1 are gone: Lite's export no longer re-imports without a machine state, and Replay is no longer a silent no-op.
- **Console games farm** (`GlitchHarvesterEngine.Console.cs`, `IConsoleGhHost`): AddBaseState, CorruptAndStash, LoadBase, ReplayEntry and LiveBlast now work on a SNES, Game Boy / Color, Master System, Game Gear or Genesis game.
  `ConsoleFarm` (library, `Windows/NesEmulator/corruption/ConsoleFarm.cs`) records the pad and every corruption write of a game as it runs, describes where the game is (**a saved machine with the replay recipe beside it when the console keeps states,
  else the recipe from power-on, else the reason it cannot**), starts an entry and rebases the recording on the machine that results. Anything that makes the recording untrue poisons it with a reason (a reset, a swapped core, a state loaded).
- **Desktop wiring** (`Windows/MainForm/MainForm.ConsoleFarm.cs`, edits in `MainForm.Consoles.cs`, `.UI.cs`, `.SessionStates.cs`, `.Corruption.cs`): the session loop records pads and writes per frame, auto-corrupt, the RTC tab lists the console's own
  memories at their true sizes, a replay rebuilds the machine and swaps it in under the emulation lock, and **the player's own save RAM is protected** once a corruption or a replay touched it.
- **API** (documented in `Windows/webapi/README.md`, `docs/webmodule-api-requirements.md`): `GET /api/gh/status`, `POST /api/gh/stockpile/save|open`, `GET /api/gh/stockpile/files`, `GET /api/console/domains|peek`, `POST /api/console/poke`, `GET|POST /api/rtc/true-domain-sizes`;
  the existing `/api/gh/*` and `/api/rtc/blast` take the console path when a console game runs, and list entries carry description, game, summary and anchor.
- **Web module** (`Windows/Webmodules/GlitchHarvester/`): Save file... / Open file..., a description in Rename, a farm status line that says honestly what can be stored for this game, blast from the current position when "Load on operation" is off, and the write
  counts (the list read a field the API never sent).
- **The NES domain-size decision** (lead, design section 9.4): the library has true sizes for every console; the existing NES farming UI keeps its probed sizes unless `corruptionTrueDomainSizes` (`EmulatorConfig`, off) is turned on; an entry records the model it was made with
  (`source.sizeModel`). NES behaviour is unchanged by default.

### Verification

- `--corrupt-test` grew to **182 checks** with the `engine` and `nes` groups: a farmed corruption saved to a file and started on a different machine reaches exactly the corrupted machine (work RAM, audio RAM and S-DSP equal); blasts made on a replayed machine carry
  the earlier ones as events; a live blast is remembered; a reset and a non-native sound unit are refused with the reason; **the NES path (state text, writes, replay) is byte-identical through the new file**; the older exports import.
- `UAT/corruption/desktop-farm.ps1` drives the real app (one instance of its own, never the user's): a generated SNES game is loaded, the memories read, a blast on the audio RAM changes only the audio RAM, a base and a corrupt-and-stash are made, the stockpile is saved,
  the file is read and started with the headless tool, **replayed in the app and the machine reached has the same audio RAM as the one that was corrupted**, a sound-unit swap makes the app refuse to store a position with the reason, the file opens back, an older export imports,
  the NES path loads and blasts as before and the RTC domain sizes show the probed sizes and, with the setting on, the true ones. The app's config is put back at the end. **29 checks, all passed** (2026-10-16, a build from this branch).

### The NES domain sizes, measured (lead's decision 9.4)

On the story game `page1_binty.nes` (UAT, the app's RTC tab, same machine): **probed (the default, what the corruptor has always used): PRG 256, PRG RAM 256, CHR 1,024, system RAM 2,048; true (`corruptionTrueDomainSizes` on): PRG 16,384, PRG RAM 8,192, CHR 65,536, system RAM 2,048.**
So with the default a NES blast could only ever reach the first 256 bytes of PRG ROM and 1 KB of CHR; the library, the plugin and the console games use true sizes. NES behaviour is unchanged unless the setting is turned on (the lead will ask the user). The stockpile entry records which model made it.

## C4: the plugin side (done; commits 96aa9b1 and 0e868c9)

### What was built

The plugin has a **Corruption** window (the button between Inputs and Instrument Runaway). The user flow, end to end, and the code behind each step are in `Plugin/README.md` ("Corruption: farm in BrokenNes, play it in FL") and `Plugin/CLAUDE.md` (rules and pitfalls). In short:

| Step | What happens | Where |
|---|---|---|
| Open stockpile... | the file dialog (starting where the desktop app keeps stockpiles, or where the last one came from) names a file; it is read on a worker thread; a file that cannot be used says why | `Bn2Editor.cs`, `Emulator.Corruption.cs` `OpenStockpile` |
| the list | every corruption with a dot (fits / does not), its summary or the reason in orange: another game (ROM SHA-1), another console, made with another CPU or sound unit, not in ROM mode, a NES game; the first one that fits is selected | `CorruptionRows` / `Evaluate` |
| Start | builds a **new** game on a worker thread: a saved state is loaded into a fresh machine, or the game is replayed from power-on with the pads and writes it was handed (cap 36,000 frames, progress shown, Cancel); the old game plays on until the new one replaces it between frames | `RunCorruptionStart`, library `AnchorStart` |
| freeze tick | Instrument Runaway is switched on at the very moment the corrupted game exists, so the instruments captured are the corrupted ones | `SetRunaway(true)` after the swap |
| Remove corruption | puts the game back where the corruption was made, without its writes | `StartCorruption(withWrites: false)` |
| the note | a start's result shows over the picture for 12 s; a project's problem stays | `DrawCorruptionNote` |
| the project | `CRP1` (see below) after the game-state section | `Corruption.cs`, `Bn2Plugin.SaveRestoreState` |

A game keeps a **farm** (`SessionGame.Farm`, the library's `ConsoleFarm`) that records the pad it is handed each frame (allocation-free, in the frame loop) and every corruption write, from power-on. That recording is what describes the game's place when the console cannot save a state.
Anything that makes it untrue poisons it with a reason (a test tone, a chip swap, Reset, a machine put back from a saved state).

**The project format.** A new section `CRP1` after `GAM1` (an older DLL skips it by length): UTF-8 JSON in the stockpile file's own format (one entry named "project history" whose anchor is the replay recipe) plus a `project` object (stockpile path, entry id / name / description / summary / content hash, frozen, the frame it started at, a note). For a console that keeps its state the history is left out (the game-state section holds the machine) and the note says so.
Reopened, a game with no state in the project (a console without states, or a state too big for it) is rebuilt on the loading thread by replaying the history (the game's status line shows the progress); a game with its state comes back from `GAM1`. (When this was written the SNES had no states; it has them now, see "After snes-state merged".) `CorruptionSection.Read` never returns a quiet nothing: a damaged, cut-short or 2 GB-claiming section becomes a problem shown over the picture.
Sizes in the check's test games: 0.6 to 1.2 KB of `CRP1`; a SNES project with a corrupted instrument is 67 KB (the 64 KB audio RAM copy is the old `SPC1`), a Super Mario World project 79 KB.

### Verification (2026-10-16, this machine)

| Check | Result |
|---|---|
| `FruityHost selftest` (the whole suite, Native AOT DLL) | **50 passed, 1 warning (QLQ plays an octave sharp on purpose, as before), 0 failed** (51 results; `corruption` is new, about 27 s) |
| `Workshop --corrupt-test` | **275 passed, 0 failed, 0 not run** (the SNES state anchor now runs for real) |
| `UAT/plugin/install-smoke.ps1` | 13 checks + the notices check pass |
| `UAT/corruption/desktop-farm.ps1` (the real app, one instance of its own) | 30 checks, ALL PASSED |
| NES golden suite `UAT/run-suite.ps1` (fresh Workshop; the story and corpus ROMs copied into the git-ignored folders the cases name) | **113 pass, 0 fail, 16 skip (the unimplemented mappers, as before), 3 info**; `romtest` hashes identical (93 before snes-state's cases came in) |
| Native AOT publish of the plugin, Windows and Lite builds | all build, 0 errors |

What the `corruption` check proves (`Plugin/BrokenNes.FruityHost/SelfTest.Corruption.cs`). It farms with the same library the desktop uses (`ConsoleFarm`, a stockpile entry, a stockpile file), then drives the real DLL through its real editor with real mouse messages. Games: a synthetic SNES game with a real SPC700 instrument in audio RAM
(`SynthSnesRom`, generated, nothing of anyone's), counter games for the Game Boy, Game Boy Color, Master System, Game Gear and Genesis, a tone game for the Game Boy wave channel, and, **when a Super Mario World ROM is named, a real game** (not in the repository; `BROKENNES_TEST_SMW` or the Workshop checks' path).

| Claim | Numbers |
|---|---|
| the window opens, is modal (nothing behind it answers), an empty window says what to do | per console; text found by pixels |
| open: 3 entries, flags fits / other game / other console; refusals carry their reason and leave the game untouched | 6 families |
| the machine Start builds equals the farm's (work RAM, audio RAM, frame count), and 40 frames later still does | SNES by saved state, and as a console without states by replay of 150 frames; the others by saved state |
| **a corrupted SNES instrument sounds different through Instrument Runaway** | the synthetic game's note: rms 0.0769 clean, 0.2782 corrupted (3.62x), odd harmonics 0.379 to 0.538 of the fundamental; **Super Mario World's slot 2 (29 blocks, 224 bytes inverted): rms 0.0441 to 0.1189, correlation 0.200 with the clean note**, the instrument snapshot equals the farm's corrupted audio RAM, 900 frames replayed in 1.5 s |
| **a corrupted Game Boy wave RAM changes the wave channel's sound** | square wave odd harmonics 0.503 to a triangle's 0.140 of the fundamental |
| the project keeps it | saved in Runaway, opened in a fresh plugin: the same machine (every console from its saved state; the SNES also run as a console without states, rebuilt by replaying its history), and after the same frames **equal to a game that never left** (work RAM, audio RAM, frame count, whole-machine hash); the corrupted SNES instrument sounds the same (rms within 2%); SMW's reopened game equals the saved one |
| a write made after the start rides in the history | a byte of audio RAM written 20 frames after the start is there after the reopen; Reset Console ends the corruption |
| everything that cannot work says so | not there / random bytes / cut off / another JSON format / a newer version / empty (each "could not open ..." and the previous list gone), another sound unit, Direct mode, a NES game, a game of another console, a replay over the cap (names 36,000), Cancel stops a long replay and the running game is untouched, damaged / cut-short / 2 GB-claiming `CRP1`, an older project with no section |
| Remove corruption, a 12-entry list (first fit selected and scrolled to, the wheel, a click) | six families; the SNES list |
| **0 bytes allocated on the audio thread** while a corruption is built and its game plays 4 s | measured inside the AOT DLL: 0 B over about 50 render calls |

**Proof the check can fail: 18 injections, one build each, each reverted afterwards (`git status` clean), every one caught by the check named for it.** Messages is the number of failure messages the check printed.

| Injection | Messages | First thing it says |
|---|---|---|
| Start drops the entry's writes | 44 | the work RAM / audio RAM after the start are not the farm's |
| the game's pad is not recorded | 31 | the machine that came back is not the machine that was saved (frame 150, saved at 190) |
| a write made after the start is not noted | 26 | the byte written after the start did not come back |
| a project's history is never replayed | 31 | the game is at frame 0, it was saved at 190 |
| an entry made on another game is said to fit | 46 | an entry made on another game should say so (the library's own game check still refuses the start: two layers) |
| `CRP1` is not saved | 35 | the project has no corruption section |
| the game allocates every frame | 23 | 13,328 bytes allocated by the plugin on the audio thread |
| the freeze tick does nothing | 29 | starting with freeze: Runaway 0, sampler mode 0 |
| nothing is shown over the picture | 32 | the result is not shown over the picture |
| a damaged `CRP1` is dropped without a word | 23 | a damaged corruption section said '' |
| a state put back still claims a replay record | 27 | the reopened game's replay record is usable, expected unusable |
| controls behind the window answer clicks | 28 | controls behind the Corruption window are still reachable |
| Cancel does not stop a replay | 24 | the replay did not stop |
| a started game forgets how many frames it ran | 32 | the game reports 0 frames run, expected 150 |
| Remove corruption keeps the writes | 33 | the work RAM is not the farm's clean work RAM |
| the first fitting entry is not selected | 29 | opening selected 'entry 01', expected entry 06 |
| the wheel does not scroll | 30 | three notches left the list at row 1, expected 4 |
| only one of the real game's writes lands | 62 | SMW: the instrument snapshot is not the farm's corrupted audio RAM; sounds like the clean one (correlation 1.000) |

**Found while building it (and fixed):** (1) the editor runs the game about 60 frames a second to keep its picture moving, so an exact-frame test needs the switch `NoPumpForTests` (hook 120); (2) a new plugin instance joins the emulator of one that is still alive, so a first version of the check "reopened" a project into the very machine that saved it and passed
for the wrong reason; every reopen is now into a fresh emulator; (3) a game that was built but not yet swapped in (no audio running) looked like "no game is loaded" to the list; (4) a machine put back by a project had a farm that still described it as a game from power-on: now poisoned; (5) a started game reported 0 frames run; (6) Remove corruption,
the first fit and the scrolling were added after the first working version, each with an injection.

## C5: other consoles

| Console | Memories (true sizes) | Anchors | In the plugin | Instrument-level proof |
|---|---|---|---|---|
| SNES | WRAM, VRAM, CGRAM, OAM, SRAM, ROM, **ARAM, DSP** | saved state + replay recipe (the recipe alone for a console without states: also tested) | start, remove, cancel, project by saved state, and by replayed history as a console without states: proven | **yes**: synthetic instrument 3.6x louder; Super Mario World instrument correlation 0.2 |
| Game Boy / Color | WRAM (8 KB / 32 KB), HRAM, VRAM, OAM, CRAM (Color), SRAM, ROM, **WAVE** | saved state + replay recipe | start, remove, project by saved state: proven | **yes**: wave RAM corruption changes the wave channel |
| Master System / Game Gear | RAM, VRAM, CRAM, SRAM, ROM | saved state + replay recipe | start, remove, project: proven | no (the PSG / YM2413 registers are not memories; the lever is the driver's data in RAM / ROM, not exercised) |
| Genesis | RAM, VRAM, CRAM, VSRAM, **ZRAM** (the Z80 driver's RAM), SRAM, ROM | saved state + replay recipe | start, remove, project: proven | no (the DAC sounds come from ROM / Z80 RAM; not exercised) |
| NES | PRG, PRG RAM, CHR, RAM (true sizes in the library; probed in the desktop UI by default) | saved state (desktop) | **not started in the plugin** (the row says so) | n/a: Instrument Runaway on a NES game freezes and silences only |

The library side is checked by `--corrupt-test consoles` (65 checks): per console the memory list and sizes, a write / read / restore on every memory, and that a corruption riding a saved state or a replay from power-on reaches the machine that never left (work RAM and picture equal), each with a control that shows the equality can fail.

## After snes-state merged (2026-10-17): the SNES keeps a state, and the corruption flow agrees

The lead merged `snes-state` and this branch into main (b771fa7) and measured on it: `--corrupt-test` 241 passed / 9 failed, the plugin check `corruption` failed for the SNES only (the frame, the damaged-section checks, **131,096 bytes allocated on the audio thread**, Super Mario World's reopened game),
and a second plugin check (`fm`) failed. Reproduced on a clean export of main (`git archive`, Native AOT DLL as before), then decided case by case which side was wrong.

**The code was right where it mattered, and the expectations were the old truth** (the SNES has no save state). With states, `ConsoleFarm.Capture` stores a **saved machine with the replay recipe beside it** (`Anchor.Alt`), and **starting loads the machine** in place (nothing replayed, nothing installed); the recipe stays the fallback and what a console
without states, or a sound unit whose state is refused, gets. Nine library checks, one T9 check, the desktop UAT, and the plugin check expected the recipe. What was changed:

| Where | What was wrong | Change |
|---|---|---|
| Library tests (`CorruptEngineChecks`) | expected a boot anchor, a replay and an installed machine for a SNES base / stash / start / live blast / load-on-operation / reset / HLE | the whole group now runs **twice**: on the real SNES (a saved machine with the recipe as `Alt`, "loaded its saved machine", 0 installs; after a reset or under a foreign sound unit the saved machine is still a position, with no recipe beside it) and on **the same SNES behind `StatelessSession`**, a wrapper that says it has no save states (Workshop-only test double; a real session, only its states taken away) with the old expectations unchanged. A console without states stays tested end to end |
| T9, `state` group | its audio hash included the 120 frames before the state was taken | `SessionRun.ResetAudio()` before both machines run; it **passes for real** (the machine after Start equals the saved machine corrupted directly: audio RAM, work RAM and the sound after 60 frames). A new SMW check: the corrupted instrument started from the real game's saved machine at frame 900 is the machine the 900-frame replay reaches, with the sound and picture 300 frames on (control: the clean saved machine is not it) |
| Plugin check | the reopened game's frame, the damaged-section checks (a flipped byte now lands inside a JSON string and still parsed: the section had **no checksum**), SMW's frame | the check is state-aware; **`CRP1` now carries a CRC-32**, so a flipped byte is a refused section (message), not a quietly changed game; frames are read from the game, the farm's count only where the farm was not reset |
| Plugin, a console without states | no console lacked states any more, so the replay path (`CRP1` history, `RebuildFromHistory`) would have gone untested | test hook 121 (`SessionGame.NoStateForTests`): the plugin reports no state; the check runs every flow for **"SNES (replay)"** (entry = the recipe, project = GAM1 marker + history) as well as the SNES with its state |
| Plugin, a state the project cannot hold | a console with states whose state was too big or refused wrote a marker and lost the game's place | `CaptureCorruptionSection` writes the history whenever the state is not in the project (`!StateSupported || stateNoteIsSave`), taking the recipe from the saved-state anchor's `Alt`; the reopened game is rebuilt by replaying it. New scenario in the check: a SNES project with a 300-byte cap on the state section |
| **The audio-thread allocation** | **`PPU_SFC` built a 128 KB colour table per brightness lazily, inside the first frame it drew.** A new game that has run no frame yet (any game loaded from a state, c4b's project restore too) therefore allocated 131,096 bytes on the audio thread at its first frame. It never showed before because the replay ran 150 frames on the worker first | the 15 tables are built **once per process** (`PPU_SFC.brightnessLut`, static, immutable: the picture is pixel-identical, and a session no longer carries 2 MB of its own). This is a change in the shared core, audio-thread allocation only. `--snes-state-test` still 147/147 |
| Desktop UAT | expected a boot anchor, and "no position can be stored after a sound-unit swap" | expects the saved machine with the recipe as `alt` in the file; after the swap the status says **no replay recipe can be stored and why** (new fields `replayRecipe` / `replayReason` in `GET /api/gh/status`) and a base is still stored as a saved machine. 30 checks pass |

**The other failing plugin check** was `fm`: "4.8x real time with 4 Master System FM instances (needs 5x or better)". It is a speed floor and it depends on how busy the machine is: on the same clean export of main it passes at **6.1x, 6.2x and 6.1x** on three quiet runs, and on my tree in every full run. Nothing in this work touches the Master System FM path.

**Evidence (2026-10-17, clean rebuild of this tree)**

| Check | Result |
|---|---|
| plugin `selftest`, whole suite | **50 passed, 1 warning (QLQ), 0 failed**; `corruption` 27 s: 7 families (SNES with state, SNES as a console without states, GB, GBC, SMS, GG, Genesis), Super Mario World, 0 B on the audio thread in both SNES modes |
| `--corrupt-test` | **275 passed, 0 failed, 0 not run** |
| `--snes-state-test` | 147 / 147 |
| install smoke | 13 checks + notices, all ok |
| desktop farm UAT | 30 checks, ALL PASSED |
| golden suite | 113 pass, 0 fail, 16 skip, 3 info |

**Five more failure injections** (each reverted; `git status` clean afterwards), all caught: the colour tables built lazily per session again (the check prints **131,096 bytes allocated on the audio thread**, the lead's number), the `CRP1` checksum skipped (a damaged section is accepted), the history taken only from a direct boot anchor (the too-big project comes back at frame 0), no history for a state too big for the project (same), and, in the library, **the farm preferring the replay recipe to the saved machine** (7 checks of the `engine` and `consoles` groups fail: "replayed 120 frames from power-on" where "loaded its saved machine" is expected).
That makes 23 injections in all.

## What is not tested, and what is not done

- **Real FL Studio.** The plugin was not re-certified in FL (`certify.ps1 -Only plugin-fl`, which needs FL closed and was not run); everything above is the test host that loads the same DLL the way FL does. The WASAPI hand-off and the UAC click of the installer remain unverified, as before.
- **Real games other than Super Mario World** (a SNES one, on the title screen). No real Game Boy, Master System, Game Gear or Genesis game was corrupted in the plugin: their checks use test games of our own.
- **NES corruption in the plugin.** Not started: `NesGame` has no farm. A NES stockpile entry is shown with the reason. Next: load the entry's NES state into a fresh `NesGame` (`LoadGameState`), apply the writes through the library's NES domains, swap it in like the console games; Instrument Runaway for the NES is still freeze-and-silence, so the gain is the corrupted game's sound through Mix and the stems.
- **"Start anyway" on another game** (design 6.1 allowed it with a warning) is not offered: a corruption made on another game is refused, with the reason. The library has the option (`StartOptions.AllowOtherGame`, used by `--corrupt-apply --allow-other-game`).
- `at > 0` timelines (writes that land some frames after the start) work in the library (`CorruptionTimeline`), but the plugin and desktop do not offer them.
- Lite does not farm console games (it compiles the library and its export / import use the engine).
- A SNES replay is about 590 frames/s headless here, 900 frames of Super Mario World in 1.5 s inside the plugin; a 10-minute (36,000 frame) recipe takes about a minute (the check cancels one). Not measured on a slow machine.
- The file dialog's starting folder (the desktop's `%APPDATA%\BrokenNes\Stockpiles`, then the last folder) is code that compiles and cannot be driven by the test host (a common dialog is modal).

## Next steps

1. Re-certify the build in real FL (`pwsh UAT/certify.ps1 -Only plugin-fl`, FL closed), then try the whole path by hand with a real game: farm in BrokenNes, Save file..., FL, Corruption > Open stockpile..., Start with the freeze tick, pick the corrupted instrument, play it.
2. NES in the plugin (above).
3. (Done 2026-10-17: `snes-state` merged, the SNES goes through the same path as the Game Boy; see "After snes-state merged".)
4. Ask the user about `corruptionTrueDomainSizes` (the lead holds the question).
5. Corruption that targets the Sega chips' instruments directly (the PSG / FM / DAC settings are not memories): either memory domains for the chips' registers or a lever in the driver's data; a Genesis or Master System real-game check.
