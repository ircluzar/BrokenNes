# Round 5, agent `corr`: corruption in the plugin. Record

Design: `docs/projects/round5-corr-design.md` (read it first; this record says what was built and how it was checked). Branch `worktree-agent-ab051cb992266ca6d`.
Work is in milestones C1 to C5; each section below says what is done, every verifier with its numbers and its proof of failure, and what is not tested.

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

- `--corrupt-test [--smw <rom>] [--only group,...]`: 150 checks (149 run, 1 not run; below).
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

## C3: the desktop side

(not started)

## C4: the plugin side

(not started; waits for `c4b`'s merge)

## C5: other consoles

(not started)
