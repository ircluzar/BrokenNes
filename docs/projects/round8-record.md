# Round 8 `record`: a SNES game's music into FL Studio (MIDI file + presets), and live MIDI out

Date: 2026-10-17. Agent `record`, branch `worktree-agent-ac654591771c45680`. Brief: `docs/projects/round8-record-brief.md`. User-facing description: `Plugin/README.md` ("Record the music"); the user's steps: `docs/handoff-testing-guide.md` (4e); handoff for the next agent: `Plugin/CLAUDE.md`.

## 1. What the user asked for, and decided

Freeze a game, take its instruments, then **record the game playing them** and get the result into FL Studio as notes, **one track per instrument**, each replayed "as is" by a BrokenNes2 instance on the Presets chip **with no game running**: the song replay (how the sound program drives the chip) is the half of a corruption that the instruments alone do not hold. SNES first. Decisions of the workshop (from the brief): both a MIDI file (drag out of the plugin, or Save MIDI...) and live MIDI out; one track per instrument (an instrument changing inside a note is not supported: counted and said); detect the tempo with a confidence and fall back to real time at FL's tempo; pitch bends and slides, velocity plus a volume controller, a preset per instrument named like its track, root-note detection with the preset's tuning compensating so the **replay pitch is exact**.

## 2. What exists now

| Part | Where |
|---|---|
| Capture (every S-DSP write between Record and Stop, preallocated, nothing allocated on the audio thread) | `Plugin/BrokenNes2.Plugin/Record/RecSession.cs` (`SpcRecorder`, `RecSession`) |
| Notes with the raw registers (pitch register as written, both volumes, envelope, what cannot be replayed counted) | `Record/RecNotes.cs` |
| Root note of a sample (BRR decode, YIN at half-sample lags) and the driver's 12-TET offset | `Record/RecRoot.cs` |
| Tempo (onsets, tatum, beat, confidence, tempo changes) | `Record/RecTempo.cs` |
| Transcription (tracks, channels, bend ranges, exact pitch intervals, smoothing, lanes, presets, facts, the deliberate bugs `RecMutants`) | `Record/RecTranscribe.cs` |
| SMF writer | `Record/RecMidi.cs` |
| Project section `REC1` | `Record/RecSection.cs` |
| Live MIDI out | `Record/RecLive.cs`, `Emulator.Live.cs` |
| The plugin side (window state, Start / Stop between frames, feeder thread, files, presets, deal, project) | `Emulator.Record.cs`, `Bn2Plugin.Record.cs`, `Bn2Editor.Record.cs`, `Ui/DragOut.cs` |
| Engine change: one rounding from FL's cents + the preset's tuning | `SnesSampler.cs` (`PitchForTuned`, `NoteOnCents`, `UpdateCents`) |
| Taps | `SpcRecipe.cs` (`SpcWriteLog.Recorder`, `LiveOut`), `Game.cs` (`SessionGame.SpcUnit`, `UnitChanged`) |
| SDK | `BrokenNes.Fruity/FruitySdk.cs` (`Fpf.MidiOut`, `Fhd.ActivateMidi`, host slots 9 `MIDIOut_Delayed` and 10 `MIDIOut`), `BrokenNes.SynthHost/HostSim.cs` (answers 9 / 10) |
| Checks | `Plugin/BrokenNes.FruityHost/Record.Checks.cs`, `Record.Fixtures.cs`, `Record.Live.cs`, `Record.SmfCheck.py`, `SelfTest.Record.cs` |

## 3. How it works (the parts that are not obvious)

**The capture.** Record (UI thread) makes an `SpcRecorder` with its first memory blocks (6 chunks of 65,536 writes, more fed by a thread of its own, 160 at most = 10,485,760 writes, about 160 MB) and the delegate the audio thread will set. The recording **begins and ends between two frames** (`Emulator.RecordService`, under `romLock`, from `Produce` or from the feeder when no audio is pulled): at once, when Instrument Runaway is switched off, or with FL's Play (an option); Stop, FL's Stop (an option, on by default), the memory cap or the game being replaced end it. From the game's `SpcWriteLog` tap every write goes in with its 32 kHz sample time; a key-on of a sample number whose directory entry is new is an **instrument version** (a marker in the stream, and a copy of the sound memory, shared with the last one when the sample's bytes are equal). A capture that runs out of blocks **counts** what it lost (the window and the facts say so). Writes, markers and copies are what the project keeps.

**Exact pitch.** The plugin used to round twice (FL's cents to an integer register, then the preset's tuning factor on the register), so some registers were unreachable. `SnesSampler.PitchForTuned(cents, tune)` = round(4096 x 2^((cents + tune) / 1200)), clamped to 1..$3FFF, **one rounding**; plain notes (`NoteOnCents` / `UpdateCents`) use it, recipes and the old integer path are unchanged. For a game value P on key k the transcription computes the interval of bend values b (as float32 cents, the way FL hands them over: k x 100 + b x R x 100 / 8192) that give exactly P, and picks one; with a bend range R of at most 8 semitones every register is reachable, wider ranges are chosen only when a note moves further (up to 48; a note that wanders further is keyed again at the middle of its range, counted).

**One channel per instrument.** Notes of one instrument that sound together share a MIDI channel and so a bend and controller 7: at every moment the bend is chosen inside the **intersection** of the exact intervals of every sounding note when there is one, else the newest note wins and every value that is then not exact is **listed** (`InexactPitch` / `InexactVolume`, with the error, in the facts). **Lanes** (an option) puts such notes on extra tracks of the same instrument (same preset): then everything is exact.

**Below FL's control rate.** The plugin reads a voice's levels every 64 samples, so pitch and volume values held for less than 2 ms (`MinHold` = 64 S-DSP samples; a pitch written in two halves, a fast sweep) are merged into the next value or sampled every 2 ms, and counted.

**Root notes and tuning.** The sample is BRR-decoded (exactly as the S-DSP does); its period by YIN at half-sample lags (the first dip under max(0.15, minimum + 0.2), parabolic refinement); the key of a note = the root + 12 log2(register / $1000). Drivers tune whole instruments a few cents off 12-TET: when 80 % or more of an instrument's key-on pitches agree on one offset (circular mean), it is folded into the preset's tuning so the keys are the notes the driver meant. The preset's tuning = 100 x (60 - root) + offset (clamped to +-48 semitones).

**Volume.** Velocity = the key-on level (max of |VOL L|, |VOL R|; raised when the note later swells past 127 %); controller 7 = later levels relative to it (100 = as keyed). The preset carries the instrument's most common left / right balance. What FL does with controller 7 is the open question (section 7).

**Tempo.** Onsets (note starts merged within 4 ms) are aligned to a grid: the tatum is the period between 0.06 and 1.2 s with the best alignment (mean resultant length), the beat is the tatum x 2^n (3 x 2^n with ternary evidence) folded into 80-160 BPM; it is **found** when 85 % of the onsets are on the grid (tolerance max(8 ms, 6 % of the tatum, 0.6 x the onset lattice)) or when 3 of 4 stretches of 6 s agree on it alone (and 40 % are on it); stretches that disagree become tempo changes with bridges that keep bar lines. Not found: the file says why and is written at FL's tempo (`FPE_Tempo`, followed by the plugin) with the notes at their real times.

**The file.** SMF type 1, PPQ 960: a conductor (tempo map, the facts as text events), one track per instrument (name = "<recording> - NN sample S[a]", channel 1-16 without 10, past 15 tracks the channel numbers repeat on separate tracks), RPN 0 with the track's range, then for each tick: note-offs, controllers, bends, note-ons. Also each track alone as a type-0 file (the window's row drag handles).

**Getting tracks onto BrokenNes2 instances.** The SDK cannot create channels in FL (the `FHD_` list of `fp_plugclass.h` has none). FL's own release notes (`Help\WhatsNew.htm` of the FL 2026 install) say: dropping a MIDI file on the **channel rack** imports it (an import window; **MIDI Out** channels by default, an option for FLEX; dropping it on the first channel shows a channel-type selector), dropping it on a **piano roll** imports into that channel (Shift / Alt show or force the window). So the shortest paths are: (a) drop the song on the channel rack, then **Give the tracks to new instances** and replace each imported channel by BrokenNes2 in track order (each new instance takes the next track's preset on an emulator of its own, Direct mode, PRE-SNES); or (b) make the BrokenNes2 channels first and drop each **track's row** on its channel's piano roll.

**Drag and drop.** `Ui/DragOut.cs`: `SHParseDisplayName` + `SHCreateDataObject` (a shell data object for the file: CF_HDROP and the shell formats FL's browser reads) + `SHDoDragDrop`. Test mode reads CF_HDROP back through the data object's `GetData` and skips only the modal loop. OLE needs a single-threaded apartment: FL's UI thread is one; on a multithreaded thread the plugin refuses the drag with the reason (`RPC_E_CHANGED_MODE`) and points to Save MIDI....

**The project.** `REC1` (before `GAM1`): version, flags (session / too-big marker), payload and stored lengths, the deflated session, a note, CRC-32. Written by the emulator's first instance; a reopened project transcribes it again on a worker (the same bytes: the transcription is deterministic), without adding the presets to the library by itself. A damaged section is refused with the reason; a newer one is refused and **written back as it came** at the next save.

**Live MIDI out (P2).** `RecLive` sits on the same tap: at a key-on, a note-on on its instrument's channel (the sample number, in first-keyed order, channel 10 left out) with the key from the sample's root and a bend for the rest (range 12, RPN 0 on the channel's first note), velocity = the key-on level, controller 7 relative to it; pitch / volume writes go out once they have held 2 samples (a pitch in two halves is one move) or at the frame's end; key-off = note-off; a key-on while the voice's key-off bit is held = on and off. The queue (8,192 messages) is drained by the instance that pulls the game's sound, after its frames, with **`MIDIOut_Delayed`** (sent when FL's MIDI clock reaches the mixer's: the block it belongs to), the port in the message's fourth byte. Switching it on asks FL to enable MIDI output (`FHD_ActivateMIDI`); the plugin's info flags carry `FPF_MIDIOut`. Before it listens, a worker estimates the roots of every sample the directory names; a sample loaded later is keyed as if its rate were C5 until its root is known (a moment later), counted. Switched off, frozen by Instrument Runaway, or the game replaced: every sounding note gets its note-off and each used channel an all-notes-off.

## 4. Verification

(Numbers from runs on this machine; the ROMs at `X:\EMULATION\Super Nintendo\Super Nintendo (U)\`. No sound card: every claim is a register comparison or a count.)

### `FruityHost record-test` (no DLL)

8 of 8 checks pass, 153 assertions, 15 of 15 deliberate bugs caught (about 1 minute; `games` 37 s).

| Check | Result |
|---|---|
| root | 13 looped BRR samples of known pitch (sine, saw, square, a weak fundamental, fractional periods) within 0.83 cents; a one-shot 500.15 Hz (500); noise: no pitch (aperiodicity 0.94) |
| tempo | synthetic onsets at known tempos (the driver's 2 ms and 4.5 ms ticks as jitter; 16ths, 8ths, triplets) within 0.1 % after folding; a change 120 -> 140 BPM found with its bar; random onsets (3 seeds) give no tempo (15 to 32 % on the best grid) |
| capture | a 60 s synthetic take: every write kept (37,106 entries = the driver's writes + 4 markers), versions and copies right; 300,000 writes: 0 bytes allocated per write; a starved capture counts its losses; the cap halts at 10,485,760 writes |
| transcribe | the synthetic driver (30 s, 132.5 BPM, 4 instruments): 250 notes read back exactly as played; roots within 5 cents; keys = the driver's notes; tempo 132.50 BPM; **replayed from the MIDI bytes through the presets' own pitch function: 5,090 of 5,240 pitch values held 2 ms or more exact, the other 150 reported by the transcription (two notes of one instrument at once), 0 inexact without a word**; volumes 3,322 of 3,322 within 2; note starts within 0.363 ms; a recording with no key-on gives no track and says why |
| smf | an independent Python parser (`Record.SmfCheck.py`) and VRUN's parser read the same structure, tracks, channels, notes, RPN ranges and tempo |
| section | `REC1` round trip; 1,006 of 1,006 damaged sections refused (cut, flipped bits, newer, unknown flag, lying lengths); the too-big marker |
| live | the synthetic driver (20 s) into the live out: 160 note-ons for 160 key-ons on the right channels, velocity = key-on level, key-on pitch reproduced within one register step on 160 of 160, 2,781 of 2,781 held pitch values and 1,473 of 1,473 levels found in order, 160 of 160 released notes got their note-off; roots of the 3 samples loaded at the start primed, the one loaded under another's number at 12 s keyed once before its root was known (counted); 0 bytes per write over 12,348 writes |
| games | **Super Mario World** (overworld, 40 s): 492 notes, 6 tracks; pitch 424 of 496 exact, 72 reported (one channel per instrument, at most 2.57 cents), with lanes (9 tracks) 496 of 496; volumes 492 of 492; starts within 0.418 ms; **tempo 141.60 BPM, 100 % of the notes on the grid, 7 of 7 stretches; the driver's own tempo (timer 0 period 2.000 ms, tempo byte 29 at $0051) gives a sequencer tick of 17.6552 ms and a beat of 23.9999 ticks: the estimate is 6.000 ticks per 16th** (error below 0.01 %); roots: 5 of 5 FFT peaks of the preset engine's output are harmonics of the root found. **Donkey Kong Country** (40 s): 1,026 notes, 23 tracks; pitch 6,682 of 8,306 exact, 1,624 reported (up to 516 cents: chords on one instrument), with lanes (30 tracks) 8,306 of 8,306; tempo 130.05 BPM (54 % on the grid, 7 of 7 stretches agree; the driver ticks every 12.501 ms and its 16th is 9.226 ticks, not a whole number, so the grid is approximate); roots 11 of 11. **A corrupted Super Mario World** (300 random audio-RAM writes, seed 9): 728 notes on 1 track, all exact; no tempo (24 % on the best grid: said, real times kept). **Corrupted intro scenes:** 171 notes, bend ranges 34 and 8, 160 notes wandered further than 48 semitones (keyed again, counted), 625 values off by at most 0.16 cents (the 34-semitone range's resolution: reported), no tempo; 0 inexact values without a word |

### `FruityHost selftest --only record` (the Native AOT DLL)

PASS, about 35 s; the library checks above without `games` (15 of 15 deliberate bugs), then 8 DLL scenarios and 7 of 7 DLL deliberate bugs.

| Scenario | Result |
|---|---|
| flow | the header button (inside the header, right of Test MIDI export, left of the emulator badge, clear of the tab strip) opens the window by mouse; Record, 6 s of the synthetic game (832 writes), Stop, transcribed (1 track, 1 note, 659 of 659 pitch values exact); the MIDI file on disk = the window's bytes; Save MIDI... writes it; **dragging the song's handle and a track's row: an OLE data object is made and its CF_HDROP read back names the right file** (STA thread); a click without a drag says how; the live check box and port by mouse; Close |
| replay | the recording's MIDI played into one instance per track on the Presets chip, no game, every S-DSP write of the instances' own samplers compared with the game's per note: synthetic 3,380 of 3,485 pitch values held 4 ms or more exact, 105 reported, 0 unreported; lanes 3,485 of 3,485; volumes 2,176 of 2,176 within 2; envelope = the game's on 160 of 160 notes; **Super Mario World recorded by the DLL** (20 s): 209 of 245 exact, 36 reported (at most 2.57 cents), 245 of 245 volumes, envelope 245 of 245; the DLL's MIDI bytes = the library's for the same recording; **0 bytes allocated on the audio thread** while 4 / 7 / 6 instances played |
| capture | 9.6 s of the game rendered by the audio thread: 1,231 writes captured = the game's own log's count, 0 lost, **0 bytes allocated on the audio thread** over 1,800 blocks |
| project | a 5,388-byte `REC1` reopened in a fresh instance to the same 8,677-byte MIDI file and facts, presets not added by themselves; damaged, changed (another recording under the old checksum) and newer sections refused with the reason; the newer one written back as it came; no section: nothing said |
| deal | 3 new instances took tracks 1-3 (their names) on emulator #1, Presets chip; a fourth took nothing |
| transport | armed until FL's Play, recording after it, ended by FL's Stop; the take had no key-on and says so |
| live | through `MIDIOut_Delayed` on the set port, `FHD_ActivateMIDI` sent: synthetic game 646 of 646 held pitch values, 81 of 81 levels; root of a sample loaded after the start: keyed as C5 (counted) and found by the worker (95.21 within 5 cents); **Super Mario World** 245 note-ons for 245 key-ons, 245 of 245 key-on pitches, 179 of 179 pitch values, 177 of 177 levels, 241 of 241 note-offs, Instrument Runaway ends the rest; 0 roots unknown; 0 bytes on the audio thread |
| refusals | Direct mode, a NES game, Save with nothing recorded, Give the tracks with nothing recorded: refused with the reason; Save MIDI writes the whole file |

### The deliberate bugs (each must make its check fail; all are caught)

| Bug (`RecMutants`) | Caught by |
|---|---|
| pitch bends one register window off | record-test transcribe |
| the preset forgets its tuning | record-test transcribe; selftest replay (the instance wrote $0BFC, the game $0C47) |
| the root an octave off | record-test transcribe |
| the tempo 1 % off | record-test transcribe |
| no pitch-bend range written | record-test transcribe |
| velocity ignores the instrument's balance | record-test transcribe |
| note starts quantised to the 16th grid | record-test transcribe |
| overlapping notes not counted | record-test transcribe |
| track 2 on track 1's channel | record-test smf |
| every 1,000th write lost without a word | record-test capture |
| a new instrument's marker lost | record-test capture |
| the section's checksum not checked | record-test section; selftest project (a changed section under the old checksum was used) |
| live MIDI out sends nothing | record-test live; selftest live |
| live notes keyed without their sample's root | record-test live |
| live note-offs lost | record-test live |
| the song's drag handle drags the first track's file | selftest flow |
| the capture allocates at every key-on | selftest capture (88 bytes on the audio thread) |
| giving the tracks says armed but gives nothing | selftest deal |
| Save MIDI writes half the file | selftest refusals |

## 5. Found on the way

- **72 bytes on the audio thread per new peak of FL voices** (all instances, all modes): `Bn2Plugin`'s voice pool started empty, so the first time an instance held more FL voices at once than ever before, `TriggerVoice` made a new one. The replay check saw 504 bytes with four preset instances. The pool is now filled with 32 voices when an instance is made.
- The test parser first merged MIDI channel state across tracks: with 23 instruments (DKC) channel numbers repeat on separate tracks, which FL keeps apart; the parser keeps state per track (as FL's import does).
- Root estimation at whole-sample lags found an octave error on a saw of period 37.5 samples: half-sample lags and a stricter dip rule fixed it.
- A beat cannot be told from its double by onsets alone (72 against 144 BPM): the rule folds into 80-160 BPM and says so.
- `OleInitialize` fails on the test bench's main (MTA) thread: the drag scenario runs on an STA thread as FL's UI thread is; the plugin's refusal on an MTA thread names the error.

## 6. Not tested (said plainly)

- **FL Studio itself:** its import of the file (one channel per track, whether it takes RPN 0 into the channel's pitch bend range, what controller 7 becomes: the channel's volume knob, applied after the plugin, would change the loudness but not the S-DSP's volume registers), `MIDIOut_Delayed` from a generator and the routing of live MIDI by port, the drag from the plugin window into FL's windows (the data object is checked, the drop is FL's), FL's `FPE_Tempo` reaching the plugin.
- **Hearing any of it** (no sound card): the replay is proven on registers, not by ear.
- The live MIDI out's timing in FL (messages go out per audio block with `MIDIOut_Delayed`; their order is checked, their timing in FL is not).

## 7. Decisions taken here (and the ones the user should take)

Taken: one instrument = one sample *version* (a sample number whose directory entry changes is a new instrument); controller 7 relative to the key-on velocity (not CC11); bend range per track, the smallest of 2..48 that holds every move; lanes off by default; tempo folded into 80-160 BPM; the deal = the next new instances, in track order, on a free emulator of their own, armed 10 minutes; a recording too big for a project (over 24 MB compressed) is a marker, not a silent drop; live out: range 12, port 1 by default, `MIDIOut_Delayed`.

A known edge of the deal: while it is armed, *any* new BrokenNes2 instance takes the next track, including one FL makes while opening another project in those 10 minutes (its project state then replaces what it took; the deal has advanced). The button disarms it at once.

For the user: (1) **controller 7 or 11** for volume changes, once FL's handling is known; (2) **lanes on by default?** (exact, but more tracks: SMW 6 -> 9, DKC 23 -> 30 in 40 s); (3) whether to keep the deal (Replace in order) or prefer "drop each track on its channel's piano roll"; (4) the live out's bend range (12) and port (1).

## 8. Open problems and next steps

- P3: the other consoles on the same framework (Genesis from `GenWriteLog`, NES and Game Boy from their period / volume registers, Master System PSG / YM2413).
- A multi-timbral SNES instance (several tracks on one S-DSP) for effects between voices: pitch modulation, the shared echo, the noise clock.
- Voices already sounding when Record is pressed are not transcribed (said in the facts).
- Live out: the driver's 12-TET offset is not applied (a live note can be a few cents from the preset of the same instrument); notes of one instrument at once share its channel.

## 9. Ids, hooks, tags and places this round took

| What | Taken |
|---|---|
| Control ids | 2020-2079 (2020 header button, 2021-2033 window, 2040-2044 track rows) |
| Plugin test hooks | 640-669 (recording), 680-699 (live MIDI out) |
| Editor test hooks | 670-679 |
| Project section | `REC1`, written before `GAM1` |
| Header place | right of Test MIDI export: x = max(midiX, 330) + 156, y 13-39, ends before the tab strip (x 556) |
| Deliberate bugs | `RecMutants` 1-19 (plugin hook 657) |
| Plugin flags | `FPF_MIDIOut` added (flags 0x204031) |
| Files | `%TEMP%\BrokenNes2\Recordings\<name>-<id>\`, presets in `<library>\<game>\Recordings\<recording>\` |

## 10. Final runs

`refs/heads/main` had not moved since this branch began (43f7a70, the round 7 `midi` merge): nothing to merge. Every DLL figure is from the Native AOT DLL published from this tree into `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r8_record`.

| Run | Result |
|---|---|
| `FruityHost record-test` | 8 of 8 checks pass, 153 assertions, 15 of 15 deliberate bugs caught |
| `FruityHost selftest --only record` | PASS (7 of 7 DLL deliberate bugs caught, 15 of 15 library ones) |
| `FruityHost selftest` (all) | 60 results: 59 pass, 1 warning (QLQ an octave sharp, on purpose), 0 failed |
| `BrokenNes.Workshop --corrupt-test` (fresh build: the Workshop compiles `SnesSampler.cs` and `SpcRecipe.cs`) | 275 passed, 0 failed, 0 not run |
| `BrokenNes.Workshop --snes-state-test` | 147 passed, 0 failed, 0 skipped |
| `UAT\run-suite.ps1 -Exe <fresh Workshop>` | 84 pass, 0 fail, 60 skip (their ROMs are not at this worktree's paths), 0 info |
| `UAT\entrypoints\notices-check.ps1` | pass (448 files scanned; no `*.bn2preset` tracked) |
