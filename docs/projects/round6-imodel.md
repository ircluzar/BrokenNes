# Round 6, `imodel`: the instrument model and the instrument corruptor

Date: 2026-10-17. Domain: `Plugin/BrokenNes2.Plugin/Instruments/` (a pure library, no drawing), its glue to the Instruments tab (`Instruments/InstrumentsUi.cs`, `Emulator.Instruments.cs`, `Bn2Plugin.Instruments.cs`) and its checks. Brief: `docs/projects/round6-instruments-brief.md`.
Branch `worktree-agent-ab6b2e10fb6fa8a04`, builds in `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r6_imodel`. Nothing pushed, nothing merged into `main`; `refs/heads/main` (78a7dee, the `ui` merge) is merged into the branch.

## What exists

| Piece | Where |
|---|---|
| the model: `Instrument` = typed `Field`s + `Blob`s, `IInstrumentSource` (`List`, `Read`, `Write`), kinds, audibility flags | `Instruments/InstrumentModel.cs`, `RegisterMap.cs` |
| readers and writers per console over the snapshots the samplers already play | `SnesInstruments.cs`, `SegaInstruments.cs` (YM2612, YM2413, PSG, Genesis DAC), `GbInstruments.cs`, `NesInstruments.cs` (+ `NesSnapshot`) |
| the SNES overlay (envelope, tuning, volume weights, flags, pinned samples) that makes more of a SNES edit audible; the saved section `SPO1` | `Instruments/SnesOverlay.cs`; used by `SpcSnapshot.cs` and `SnesSampler.cs` |
| the corruptor: `Randomise`, `Nudge`, `FlipBits`, `Set`, `SetBytes`, `Lock`/`Unlock`, seeded xoshiro256**, BRR-aware | `Corruptor.cs`, `Prng.cs` |
| the history: operations, cursor, undo, redo, go-to, replay from clean, serialisation with CRC | `InstrumentHistory.cs` |
| the saved form of one instrument (compact, versioned, CRC) | `InstrumentCodec.cs` |
| the model behind the Instruments tab (`IInstrumentModelProvider`), registered through `UiBoot` | `InstrumentsUi.cs` |
| writing an edit into the frozen game, samplers prepared on the UI thread; `InstrumentNote` | `Emulator.Instruments.cs`, `Bn2Plugin.Instruments.cs` (+ three one-line changes in `Bn2Plugin.cs`, see below) |
| checks | `Plugin/BrokenNes.FruityHost/Imodel.*.cs`; run as `FruityHost imodel-test` (no DLL) and the `imodel` and `imodel-ui` checks of `selftest` |

Edits outside my files, all small and additive: `SpcSnapshot.cs` (the `Overlay`, pinned samples in the list, `TryEnvelope` consults the overlay, `HasSample`/`Resolve` without LINQ, the list built when the snapshot is made, `WellFormed`/`TryGameEnvelope`), `SnesSampler.cs` (the overlay, behind a null test),
`Bn2Plugin.cs` (the class is `partial`; the `SPO1` section written after `SPC1` and read in the section loop; `TakePrepared` in the three sampler constructions; the hook range 300-319 routed in `TestDispatcher`'s default), `Windows/NesEmulator/sega/Ym2413Tables.cs` (`Ym2413.RomPatchBytes`: a public copy of a ROM instrument's eight bytes),
`BrokenNes.FruityHost` (csproj links, `Program.cs` verbs `imodel-test`, `imodel-fields`, `imodel-golden`, `imodel-real`; two entries in the suite list).

## The model

An **Instrument** has a console, a kind (`SnesSample`, `OpnPatch`, `OpnDac`, `PsgTones`, `PsgNoise`, `OpllUser`, `OpllRom`, `GbPulse1/2/Wave/Noise`, `NesPulse1/2/Triangle/Noise/Dmc`; the numbers are stored in saved forms: append only), an `Index` (the number the plugin's Instrument picker uses), a name, **fields** and **blobs**, read-only `Facts` (address, length, where the envelope came from), `Origin` (a SNES sample's audio-RAM address), `Editable`/`ReadOnlyReason` and `Sounding`.
A **Field** has a stable string id, a name, a group, a kind (`Enum`, `Range`, `Bits`, `Signed`), min, max, the bit width of the register it lives in, value, a one-line explanation read from the chip cores (register and bit named), and **whether the sampler plays it** (`Audible.Yes`, or `No` with the reason). A **Blob** has an id, name, kind (`Brr`, `Pcm8`, `Nibbles`, `Bytes`), length limits (a sample in audio RAM has a fixed length), the bytes, the same audibility flag.
Instrument counts per snapshot: SNES one per sample the sampler lists; Genesis 6 FM patches + the DAC sounds + 3 PSG; Master System PSG 3, YM2413 19 (the user instrument, 15 ROM instruments, 3 PSG); Game Boy 4 channels; NES 5.

**The tab's contract** (`Ui/Contracts.cs`) is implemented by `EmulatorInstrumentProvider` over a per-emulator `InstrumentSession` (one per frozen game, shared by every instance's editor so the histories, locks and the working copy agree; it starts again whenever the working copy changes by any road but the session's own: Runaway re-captured, a project restored, a test hook).
Mapping decisions: the contract's `Locked` is the model's (the tab never holds it); locks are history operations only when a random tool is next used (so Undo steps over them); a `Set` dragged on one field is one history step (`SetCoalesced`); `Randomise` keeps a SNES sample's structure up to strength 50% and may break it above (the chance rises with the strength; the status line says so);
`Nudge` of a field moves it by a 64th of its range (a bit field: one bit flips; a sample: about 3% of its bytes by one); with no field chosen the model draws one unlocked field or sample with the operation's seed; the explanation of a field not played today ends with "Not played today: <reason>"; a ROM instrument of the YM2413 is listed read-only and every edit of it is refused with the reason.
`TakeForPreset` gives a `PresetDraft` whose `Payload` is an `InstrumentPayload` (the instrument as it is now, the clean one, the history with its seeds, in the saved forms, plus the facts a preset engine needs besides the instrument). `PushIntoGame` calls `InstrumentPush.Handler` when the `inject` work sets one and otherwise refuses: "Pushing an edit into the running game is not built yet".
Nothing in the contract was a blocker; the things it has no room for are listed under "Open problems".

## Which fields each sampler plays

The sampler classes read the snapshot like this; the `audible` check proves each claim on rendered PCM (below). The generated tables (`FruityHost imodel-fields`) have every field with its explanation.

**SNES** (`SnesSampler` over `SpcSnapshot`): plays the BRR bytes and the directory's loop pointer exactly; takes the envelope from `TryEnvelope` (an edit's own, else the game's voice that used the sample, else the organ-like default $FF $E0 $7F); pitch and level come from FL; echo is on for every voice if the game used it on any, noise and pitch modulation are off.
So before this work a SNES edit of the envelope of a sample no voice used, of tuning, of volumes or of the flags had no effect. **Minimal change, implemented:** `SpcSnapshot.Overlay` (`SnesOverlay`, up to 256 entries of 14 bytes keyed on the sample number: envelope, tuning in cents, left/right volume weights, echo/noise/pitch-modulation flags, pinned+length). `SnesSampler.NoteOn/Update` scale the pitch and the two volumes by the entry and set the voice's bits in EON/NON/PMON; `TryEnvelope` returns the entry's envelope first; `Instruments()` lists a pinned sample even when its BRR no longer parses (the user broke it on purpose).
All of it is behind `Overlay != null`. A pinned entry exists only while the sample is not well-formed by the list's own rule, and an entry whose values equal what the snapshot gives anyway is removed, so an edit that is undone leaves the snapshot's bytes exactly as they were (the `restore` check).

| field | played | note |
|---|---|---|
| `env.adsr`, `env.ar`, `env.dr`, `env.sl`, `env.sr` | yes | ADSR fields matter when the mode is ADSR; sustain and decay only when the level and rate leave something to hear |
| `gain.mode`, `gain.curve`, `gain.rate` | yes | when the mode is GAIN; a direct level of 0 is silence |
| `vol.l`, `vol.r` | yes (overlay) | weights of the note's level, -128..127 |
| `tune.cents` | yes (overlay) | +-4800 |
| `mix.echo`, `mix.noise`, `mix.pmod` | yes (overlay) | echo needs the game's echo volume and delay |
| `loop.addr` | yes | only for a sample whose last block has the loop flag, and only once it has played through |
| blob `sample` | yes | header range/filter/loop/end and the nibbles |

**Genesis** (`OpnSampler`): `LoadPatch` writes all 28 operator registers and $B0 as they are and $B4 with both pan bits forced on; the carriers' total level is the base the note's velocity adds attenuation to; the multiples of the carriers decide the note's fundamental; the chip's LFO ($22) is the snapshot's. So **every field is played except `pan.l`/`pan.r`** (the sampler plays both sides).
The DAC sound is played from its samples, rate, loop and loop pitch (`dac.cycle`; 0 means "play at middle C like a one-shot"). The PSG instruments: the registers (`tone*.period`, `*.att`, `stereo`, `noise.*`) are **not** played (the sampler plays the PSG from FL's pitch and level; only the white/periodic choice, which is the instrument you pick); each says so.
**Proposal, not implemented** (it would change clean snapshots): honour pan bits when the game set them; a PSG overlay with a fixed duty or noise rate.

**Master System / Game Gear** (`OpllSampler`): the user instrument's eight registers are loaded as they are (every field played); the ROM instruments are the chip's own (listed from `Ym2413.RomPatchBytes`, read-only; `SegaInstruments.CopyRomToUser` puts one in the user instrument to start a corruption from it). Notes on this chip's core: a modulator does not release on key-off, so `mod.rr` matters only when the modulator is percussive (`mod.eg` off), and `mod.eg` only once the modulator has reached its sustain level — the explanations say so.

**Game Boy** (`GbSampler`): played: a pulse's `duty` (unless the Instrument dropdown overrides it) and the envelope's `env.dir` and `env.pace`; the wave RAM; the noise channel's `env.dir`/`env.pace`. **Not played**, each with its reason: `sweep.*` (the sampler turns the sweep off), `length` (no length limit), `env.vol` (the note starts at FL's level), the wave channel's `dac`/`length`/`level` (from the note), the noise `clock.shift`/`width`/`divisor` (from the note's pitch and the Noise mode slider).
**Proposal, not implemented:** an overlay like the SNES one carrying a sweep and a noise width.

**NES**: no sampler exists (Instrument Runaway on a NES game freezes and silences it; Direct mode's Duty and Noise mode are plugin parameters), so every field says "no NES sampler exists yet". `NesSnapshot.FromLatch(latch, written, peekCpu)` builds a snapshot from the NES core's APU register latch and a CPU peek (the DMC sample is read from $C000 + 64 x $4012, 16 x $4013 + 1 bytes); the model, the corruptor and the codec work on it, which a NES sampler or the presets can use.

## Reading and writing

`Read` fills a schema from the snapshot; `Write(edited)` returns a **copy** of the snapshot with the edit, or a refusal with the reason (a wrong kind or console, a value outside its range, a blob of the wrong length, a DAC loop that does not fit its sound, a ROM instrument). Writing is **diff-based**: only what differs from the snapshot's own reading is touched, bits no field covers stay as they were, a field that did not change is not rewritten. Consequences, all checked: read -> write unchanged is byte-identical; the bytes depend on the instrument's state and not on the path to it (ten random operations written one after the other equal the direct write, and undoing all of them gives the original memory byte for byte); editing one instrument leaves the others alone (except what is shared by design: the YM2612's LFO, the PSG's noise register).
A sample's length cannot change (it sits between other data); a Genesis DAC sound may be 1..65,536 samples; the Game Boy wave is exactly 16 bytes; a NES DMC sample 0..4,081.

## The corruptor

Everything that decides a corruption is an integer comparison on numbers drawn from one `Prng` (xoshiro256** seeded through splitmix64, `Below` by Lemire's method) seeded by the operation's own 64-bit seed; strength is an integer in permille. No floating point, no clock.
- `Randomise(strength, keepStructure, target)`: picks `round(strength x fields)` unlocked fields (at least one) and moves each by up to `strength x range` (full strength: uniform over the range); a blob gets about `strength/2` of its units re-rolled (full strength: all of them). For BRR: nibbles 76%, header range 12%, filter 8%, the last block's loop flag 4%; **keeping the structure** means every range stays 0..12, the end flag stays on the last block only, and the loop pointer sits on a block boundary inside the sample; **without it** each mutation breaks the structure with probability strength squared (a range of 13 to 15, the last block's end flag cleared, an end flag in the middle, a stray loop flag, a header of noise).
- `Nudge(target, step, count)`: a field by `step` (clamped); a BRR sample's nibbles by `step` (saturating signed 4-bit) or its header ranges (`PartHeaders`); a PCM/DMC byte or a wave nibble by `step`; a SNES loop pointer by `step` blocks when keeping the structure. An empty target picks a random unlocked field or blob.
- `FlipBits(target, count)`: distinct bits inside the field's width (clamped into the legal range afterwards), or bits of a blob (BRR: headers only, nibbles only, or both; keeping the structure takes back a flip that would break it).
- `Set` / `SetBytes`: manual edits; they ignore locks (a lock protects against the random tools only) but legality (a DAC loop inside its sound) beats a lock.
- **Locks** are honoured by every random tool and recorded as history operations. A legality rule never moves a locked field (it switches the DAC loop off instead).
- **History**: the clean instrument and the ordered operations (kind, target, A, B, 64-bit seed, flags, bytes), a cursor; `Undo`, `Redo`, `GoTo`, `Replay(n)` (from clean), `SetCoalesced`; at most 4,096 operations (then it says so). `Current` always equals a replay of the first `Cursor` operations (checked at every step of 980 random steps).
- **Saved forms** (`BNIH` history, `INS` instrument, `SPO1` overlay): versioned, little-endian, CRC-32 over everything; a damaged, cut, newer or absurd stream is refused with a reason, never trusted. The history stores the clean instrument, the operations and the **final bytes**; if the corruptor changes later and a replay no longer gives the saved final bytes, the saved bytes are what `Current` is and `ReplayMatchesSaved` is false. An instrument is its field values in schema order (zig-zag varints) and its blobs: a Genesis patch is about 60 bytes. A future change to a kind's field list needs a new `Version`.
- Determinism evidence: the PRNG equals an independent implementation (`Plugin/BrokenNes.FruityHost/Imodel.PrngReference.py`, written from the published algorithms; 16 outputs and 32 bounded draws, 4 seeds); fixed scripts on five instruments hash to recorded values (`GoldenScripts`); two runs equal, 16 different seeds give at least 15 different results for every instrument tested.

## Applying an edit to the frozen game

`Emulator.ApplyInstrument(Instrument)` (UI thread, Runaway on): writes into a copy of the working copy, **builds every instance's sampler on the UI thread** (`Bn2Plugin.PrepareSamplerFor`), swaps the snapshot; the audio thread hands the prepared sampler over (`TakePrepared`) instead of building one. Measured through the Native AOT DLL: **the first block after an edit allocates 0 bytes; a swap with no prepared sampler allocates 362,968 bytes** on the audio side (SNES; the control that shows the measurement sees it).
Held notes restart on the new sampler (the existing `DropSampler`), which is how an edit is heard at once. `Emulator.InstrumentSource` reads the working copy; `Emulator.InstrumentNote` carries the words for a project whose edits could not be restored. A project saved in Runaway keeps the edits: samples, loop pointers, PSG/FM/DAC registers and wave RAM are in the snapshots the project already saves (`SPC1`, `GBS1`, `PSG1`/`OPL1`/`OPN1`), the SNES overlay in `SPO1`;
an older DLL skips `SPO1` (its section loop skips unknown tags), a damaged or newer one is dropped by this DLL with a note, the project opens in Runaway without the edits.
Test hooks 300-312 (`Bn2Plugin.Instruments.cs`): 300 write a packed instrument, 301 the last result's words, 302 hash of the working copy (overlay included), 303 overlay entries, 304 instrument count, 305 the project's instrument note, 306 bytes allocated, 307/308 read an instrument packed, 309 install a SNES working copy from bytes, 312 switch a deliberate bug on.

## Verification (every verifier shown able to fail)

`FruityHost imodel-test` (28 s, no DLL): **18 checks, 35,860 assertions, 19 of 19 mutants caught**. The same through `selftest <dll> --only imodel` plus the DLL scenarios, and `--only imodel-ui`. Real games used (from the user's library, never committed): SNES Super Mario World and Donkey Kong Country, Genesis Sonic the Hedgehog, Game Boy The Legend of Zelda: Link's Awakening, Game Boy Color Dr. Mario DX, Game Gear The Addams Family, Master System Alex Kidd in Miracle World (PSG) and Argos no Juujiken on the FM chip, NES Contra (registers and DMC from the APU latch). 117 instruments.

| check | what it proves | numbers |
|---|---|---|
| `schema` | unique ids, ranges that fit their widths, a one-line explanation, a reason on every field marked not played, all 17 kinds | 6,431 assertions |
| `registers` | made-up snapshots encoded by independent code (hand-written register bytes, the test data builders) read as the chip layouts say: every SNES ADSR/GAIN/BRR field, YM2612 operator registers, YM2413 user and ROM bytes, PSG, Game Boy NR registers, NES registers; `NesSnapshot.FromLatch` | 188 |
| `roundtrip`, `roundtrip-real` | read -> write unchanged -> identical bytes (overlay included), the original untouched, the result a copy | 50 + 117 instruments |
| `edit-readback`, `-real` | every field set to min, max, middle and its neighbours, one at a time (through the corruptor's `Set`), written and read back equal, no other instrument moved; every blob inverted and its last byte changed | 1,565 + 3,999 edits |
| `boundaries` | an empty sound memory, empty / short / long / oversized blobs, out-of-range values, a wrong console or kind, DAC loops that do not fit, ROM instruments, strength 0 and 7.5, unknown targets, a full history, garbage to every decoder | 38 |
| `prng`, `determinism` | see above; plus every tool's result is legal | 49 + 159 |
| `locks` | half the fields and every blob locked, 60 random operations each, nothing locked changes; all locked: nothing changes and the tool says so; targeted at a locked id: nothing; unlocked: the tools work again | 1,680 operations on 14 instruments |
| `history` | at each of 980 steps of undo/redo/go-to/lock/new operations: the instrument and the locks equal a replay from clean, `CanUndo`/`CanRedo` are right, the saved history reads back equal | 5,788 |
| `restore` | ten operations per instrument written one after the other equal the direct write; undone, they give the original memory byte for byte | 76 instruments, 760 writes |
| `codec` | instruments, histories, overlays read back; every truncation and every flipped bit is refused with a reason; newer version, unknown kind, wrong console, value outside the schema (valid checksum), wrong blob length, a history whose replay no longer matches | 2,583 / 658 / 315 damaged streams refused of as many |
| `structure` | 1,560 structure-keeping operations on 13 real and made-up samples never leave a malformed sample or a loop pointer off the blocks; without it the break rate is 3% at strength 15%, 100% at 60%, 94% at 100%; a broken sample still plays and sounds different | 1,579 |
| `audible` | PCM through the very sampler classes the DLL uses (linked from the same source files): every field marked played changes the sound for some value (in the context its prelude sets up), every field marked not played leaves the PCM **bit-identical** for every value; blobs the same; on made-up snapshots and real games | 694 assertions; e.g. YM2612 patch: 54 played + 2 not (pan), PSG 7 + 3 not, Game Boy pulse 3 + 5 not |
| `neutral` | a snapshot without an edit plays bit for bit as before the SNES overlay existed: 8 made-up snapshots and SMW, Sonic 1, Zelda LA, Addams Family against hashes recorded from the unmodified samplers before the change (commit 4a83ae1) | 12 hashes |
| `alloc` | the SNES sampler allocates 0 bytes over 400 blocks of notes and updates, with and without an overlay | 0 B |
| `real-corrupt` | all three tools on real instruments: legal, writable, read back, rendered finite | 46 instruments, 35 sound different |

**Through the DLL** (`imodel`): the SNES overlay played by the plugin's sampler (an unchanged write gives the same bytes and the same sound; tuning +1200 cents plays at 4000 Hz and back to 0 sounds exactly as the original; a slow attack is quieter in the first 60 ms; a right weight of 0 silences the right side; the noise flag changes the sound), kept in a saved project (tuning measured after reopening, save -> load -> save stable),
a damaged `SPO1`, a newer `SPO1` and a project without one open in Runaway without the edits and say so (the sound is the game's own again), a 3-note chord for 3 s on an edited instrument allocates 0 bytes, the first block after an edit 0 bytes; a Game Boy duty edit and a Genesis carrier level edit are heard and kept in the project; refusals say why and change nothing.
**Through the real tab** (`imodel-ui`, real mouse and key messages, the patterns of the `ui` check): a SNES game with a made-up sound memory and a Game Boy game are frozen; the list, fields and explanation come from the real model; ten shift-clicks on Tuning's plus are one history step and the note's pitch rises by exactly 100 cents; Undo gives the sound memory back byte for byte and Redo again; a byte typed into the hex field changes the sample in the sound memory and the sample stays listed and plays;
locking a field and the sample data, Randomise at full strength leaves them alone and changes the rest, the seed and "step 1 of 1" are shown, Undo restores the exact bytes and Redo reproduces the same corruption; Nudge moves the chosen field by +150 and back; a nudge of a locked field is refused with the reason; Push says it is not built; a change made outside the tab shows on it; Runaway off brings the first message back.

**Proof that each verifier can fail** (`Imodel.Checks.cs`, `Mutants`/`TestMutants`; each sets one deliberate bug and the named check must fail):

| deliberate bug | caught by |
|---|---|
| locks ignored | `locks` ("locked field env.adsr changed") |
| the seed is ignored | `determinism` ("24 different seeds gave only 1 results") |
| a bit flip leaves its field's width | `determinism` ("gain.curve = 6 outside 0..3") |
| the seed is not stored with the operation | `history` (replay differs) |
| undo leaves a byte of the undone change; replay drops the last operation | `history` |
| a wide register field loses its top bit on the way in; the loop pointer is not written; YM2612 operators 2 and 3 exchanged; Game Boy wave nibbles swapped | `edit-readback` |
| an undone edit leaves an envelope entry behind | `restore` |
| keep-structure lets BRR headers break; lets the loop pointer leave the sample | `structure` |
| a damaged checksum is not noticed | `codec` (2,008 accepted) |
| the sampler ignores the overlay | `audible` (14 fields) |
| a played field declared not played (operator 1 total level); a not-played field declared played (pan) | `audible`, in both directions |
| a clean snapshot played through a stray overlay | `neutral` (hash differs) |
| the sampler allocates a little on every block | `alloc` |
| an edit stays in the history and never reaches the game; Randomise ignores the locks; Undo changes the history but not the game | `imodel-ui` |
| no `SPO1` section / a damaged one (the project negative scenarios) | the persistence check of the DLL scenario |

**The whole suite** (`selftest` on the Native AOT DLL built the way `build-dist.ps1` does, with `refs/heads/main` 78a7dee merged, 173 s): **55 results: 54 pass, 1 warning (the QLQ core's octave, expected), 0 failed**, including `ui` (22 scenarios) and `ui-proof` unchanged (they install their own stubs through hook 403, so the real model is invisible to them), `runaway-*`, `runaway-state`, `corruption`, `realtime` (0 bytes allocated), `params` (no FL parameter added) and the two new checks `imodel` and `imodel-ui`.
Not run: `UAT` (FL render certification, installer, desktop and web smokes): nothing in the desktop or web builds was touched except the one-line visibility addition in `Ym2413Tables.cs`, which compiles into them (the Core and plugin projects built; the desktop and Lite projects were not built).

## Findings outside my domain

- `SpcSnapshot.HasSample`, `Resolve` and the lazy sample list allocated on the audio path (LINQ, an interface enumerator, a list built on the first note). The new `alloc` check showed 94 KB per 400 blocks in the JIT; they are plain loops now and the list is built when the snapshot is made. (The AOT DLL's own tests had measured 0 before: the AOT compiler removed the closure; this no longer depends on it.)
- The brief's ROM paths (`...\Super Nintendo (U)\Super Mario World (U) [!].smc`) are right but a PowerShell `Test-Path` treats `[!]` as a wildcard: use `-LiteralPath`.
- Master System games in the library are `.7z`; the checks read them through 7-Zip's command line (`C:\Program Files\7-Zip\7z.exe`) into a temporary folder that is removed again.

## Decisions

- **The overlay, not a bigger sampler.** The existing samplers stay as they are; the SNES gets one small per-sample table behind a null test (neutral by hash), because it is the only console where the interesting settings are not in the snapshot in a form the sampler plays. The Genesis, Master System and Game Boy edits are already played by writing the snapshot's registers.
- **Diff-based writes** (see above): the only way read -> write is byte-identical and undo is exact.
- **Integer everything in the corruptor** and the seed stored with each operation; the final bytes stored beside the history.
- **Locks are operations**, but the tab's undo steps over them (they are grouped with the operation they precede).
- **No silent fallbacks:** a refused write, a ROM instrument, a damaged section, a push that does not exist, a field the sampler ignores: each says so on the tab.

## Not tested

- Real FL Studio: nothing was installed or listened to (no sound card; every sound claim is a measured PCM or hash). Whether FL redraws the Instruments tab while a note plays, and the feel of dragging a slider with the game running, are untried.
- The NES has no sampler: its model is tested (round trip on a real game's registers and DMC sample, edits, corruption), not heard.
- `FlipBits`/`Randomise` are not tested for *musical* quality; only legality, determinism, locks, structure and audibility.
- Pushing an edit into the *running* game (the `inject` work); the preset engine playing a `InstrumentPayload` without a ROM (the `presets` work).
- `SetBlobByte` through the tab on a Genesis DAC or a NES DMC sample; the tab scenarios cover the SNES and the Game Boy. The Genesis and Master System are covered through the plugin's hooks and the library.
- More than one instance editing at once from two editors (the session is shared and everything runs on the GUI thread; not driven).

## Open problems and next steps

1. **ROM instruments of the YM2413** are read-only in the tab. A "Copy to user instrument" action (`SegaInstruments.CopyRomToUser`) needs a button; the contract has no room for it.
2. **The contract cannot say a field is not played today**; the provider appends it to the explanation. A flag on `InstrumentField` would let the tab dim it.
3. **Strength and structure**: one slider; above 50% a SNES sample's structure may break (the chance rises with the strength). A "keep valid" toggle would be clearer.
4. **Genesis pan, PSG registers, Game Boy sweep/length/noise width** are shown as not played; making them audible needs an overlay of the SNES kind on those snapshots (proposals above).
5. **A NES sampler** (and the NES half of Runaway) would make 5 more kinds audible; `NesSnapshot.FromLatch` is ready.
6. **Presets**: `InstrumentPayload.Context` is my guess of what a preset engine needs (SNES: the 128 DSP registers; Sega: clocks); the `presets` work should say what else.
7. **Push into the game**: `InstrumentPush.Handler` is the seam; the overlay's entries are the natural "key-on override keyed on the sample number" for the SNES.
8. A snapshot swap restarts held notes. A smoother edit (the same sampler, new data) would need `Reload` in the five sampler classes.
9. The history holds the clean instrument and operations in memory per instrument per session; `GoTo` replays from clean (fast enough for the 4,096-operation cap; a checkpoint every N operations would make long histories instant).
