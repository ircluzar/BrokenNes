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

(The deal below was replaced on 2026-10-18 by the content match: see section 11.)

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

## 11. 2026-10-18: FL's import, then Replace (the user's report, fixed)

Branch `record-replace` (from `refs/heads/main` at 421c4d8, where round 8 was merged).

### The report

In their words: importing the MIDI file into FL makes MIDI Out channels; replacing those channels by BrokenNes2 instances does not assign the tracks, only newly added instances are detected, and they want to replace the MIDI Out channels so that the notes do not have to be moved by hand. The creation-order deal (round 8) assigned a track in the plugin's constructor; why FL's Replace defeated it is not known (FL may restore a default state or parameters after construction, or create the instance another way), and nothing here can run FL. So the fix does not depend on how FL creates or restores the instance.

### What was built

- **The offer** (`TrackOffer.cs`): a finished recording, and a recording a project brings back, puts its tracks on offer until the Record window withdraws them ("Tracks on offer: stop"). A worker reads each track's preset once and keeps one engine (a `SnesSampler` at FL's rate) ready per track; it builds the next when one is taken, and settles the instances that found their track. The creation-order deal is gone: it would have fought the content match (an instance dealt track 1 by creation order, then playing track 3's notes).
- **Untouched** (`Bn2Plugin.Tracks.cs`): an instance nobody configured. It stays untouched through an empty or foreign state (`RestoredCleanly` false), a state of ours whose instance-level values (all but Sound chip, Mode and Console, which belong to the emulator and follow it) are the defaults and that holds no preset (`PRS1`) and no track (`TRK1`), and parameter pushes that set a default or the value already there. It becomes configured when the user changes a value in its editor (`OnUserChange`, a new hook in `FruityPluginBase.SetFromUi`), FL sets a parameter to a value that is neither, a project state configures it, the user loads a preset, picks a game, starts Instrument Runaway, a recording or a MIDI test song from it, it settles on a track, or a track is picked for it by hand.
- **The match** (`Record/RecTracks.cs`, `TrackIndex` + `TrackMatcher`, per instance, everything made with the instance): at each note FL starts (`TriggerVoice`, the audio thread, before the note is rendered) the untouched instance asks FL for the mixing time in beats (`FHD_GetMixingTime` = 36, `GT_Beats`) and the note's colour (`FPV_GetColor`), and weighs the note against every offered track's notes. *Aligned*: the same key within 0.06 beat of the song position (the key may carry the bend the file starts the note with, at the file's range or at 2 semitones); the tracks that had every note so far stay candidates; one candidate is taken at once; several (unison) play the first until a note tells them apart, unless the colour equals exactly one candidate's MIDI channel; a note no candidate explains starts the count from it (a jump, an edited note). *Shifted*: offsets from the note to every same-key note of the tracks, kept while later notes fit; one track left after 4 notes is taken (a pattern placed elsewhere, or no song position from FL: then the instance's own clock at FL's tempo). Sure (matching stops): 3 aligned notes on one candidate, 8 in unison, 5 by an offset. A choice not sure yet is let go when a note supports it no more. Nothing allocated per note (measured).
- **Taking the track** before the note sounds: the instance takes the slot's prebuilt engine with one exchange and plays the track's preset on a bound render path whatever emulator it sits on; the worker then moves it to the tracks' emulator (Direct mode, PRE-SNES chip: the one used before while it is still in preset mode, else the lowest free number) where it is an ordinary preset instance. A second instance on the same track before the next engine is built is silent until it is (counted, never built on the audio thread). All eight emulators in use: it plays its track where it is and says so.
- **Bends and volume** (`TrackExpression`): FL's manual says controller data is not imported with a MIDI file's notes ("must be loaded separately", Event Editor > Import MIDI file, per CC), and FL users report that the import reads pitch bends at 2 semitones whatever the file's RPN says (forum thread "MIDI Import Pitch Bend Range (again)", 2022; not confirmed by Image-Line). After import and Replace the channel's pitch and volume are therefore not the recording's. A channel that plays a track takes, for each note, the track's key at that time, and every 64 samples the track's bend and controller 7 at FL's song position (one host question per block): the S-DSP writes the game's registers whatever FL kept. The window's "Bends + volume from the recording" switches it for every track channel (on by default, remembered as Prefs `TrackExpression`); off, a channel follows FL's own pitch and volume, which the user can edit. The transcription is unchanged: a file whose bends survive FL needs a range of 2, which cannot hold the slides the games make (DKC up to 19 semitones, corrupted states 34); playing the recording's expression in the plugin keeps every one of them exact.
- **Play here**: each row of the Record window has a button that gives that track to this channel by hand (any channel); a line above the tracks says what this channel plays or how it will find its track. The window lists this emulator's recording, else the last one on offer (a replaced channel sits on another emulator).
- **The project**: section `TRK1` (version 2: flags, the recording's session id, the track, how it was found, names, the track's expression, CRC-32) after `PRS1`; version 1 is read too; a damaged or newer one is dropped (the preset still plays). An instance never settled plays its track again on the bound path.
- **The host log** (`HostLog.cs`, the window's "Log FL's calls", remembered as Prefs `HostLog`): `Documents\BrokenNes2 host log.txt` (tests: `BROKENNES_HOSTLOG_FILE`) gets, per instance numbered in creation order: its creation, every state saved or restored (values, ROM path, place, the sections by tag and size, and what it made of the instance), the parameters FL sets and the Dispatcher calls it makes (the first 160), the first 32 notes (pitch, song position with and without `GT_LocalTime`, colour, length, velocity), how each note moved the match, how far FL moved each note's pitch while it sounded (whether FL applies bends), the settling, the offer. The audio thread writes fixed records into a ring made once; a thread formats them.

### Verification

| Check | Result |
|---|---|
| `record-test match` (no DLL) | the synthetic recording's 4 tracks: every track found and settled with or without the start bend in FL's pitch, the 8 unambiguous first notes bound on that note; from the middle of the song 4 of 4 on the first note; 64 beats later 4 of 4 by the offset; without a song position 4 of 4 by the own clock; unrelated channels (16 random notes, 20 times, and other keys at a track's own times) 0 of 24 taken; unison: the first track plays, then the right one after they part (1 switch); the colour settles it at once; an added note lets a provisional choice go and the next notes bring it back; expressions read back the same and every note's key is found at its time; 0 bytes per note. 2 deliberate bugs caught (the key alone, any key) |
| `selftest --only record`, scenario `replace` (the DLL) | instances made with an empty state (A), their own fresh state (B), every parameter pushed at its default (C), and before the tracks were offered (D) are untouched before any note; A, B, C, D each play their track (assigned in a shuffled order), settled on the tracks' emulator, **their first note played by the track's preset**; E (playback from the middle) on its first note; G (the song 64 beats later) and H (no song position, own clock at the file's tempo) after 4 notes; F (other notes at the very times of a track) never taken; L (a track of a second recording, made on another emulator and on offer beside the first) takes that recording's track 2; **K (FL lost the bends and the volume) and K2 (FL read the bends at 2 semitones) write the game's pitch register on every value held 4 ms or more**: K2 3,296 of 3,296, K 28 exact and 60 reported by the transcription (two notes of one instrument at once), 0 unreported, volumes within 2; **0 bytes allocated on the audio thread** while 10 channels played and recognised their tracks; Play here by mouse; A's project reopened in a fresh instance plays track 3 with its expression; withdrawn: an untouched channel keeps its sound; the host log holds the creations, states, D's first note and match, and the settling |
| deliberate bugs (DLL, plugin hook 657) | 9 (with the other scenarios' 6, 15 through the DLL), each caught by `replace`: a default state counts as configuring; the tracks never offered; instances made before the offer ignored; the track taken after the first note was played; the engine built on the audio thread (2.5 to 3.3 MB allocated); TRK1 not written; Play here says done and does nothing; any key fits; track channels ignore the recording's expression ($0C48 written for the game's $0C47) |

### Not verifiable without FL (said plainly)

- Whether FL keeps the notes when a channel is replaced (the user says it does: that is the workflow), and what it hands the replacing plugin: a state (which), parameter values, in which order. The design does not depend on it; the log shows it.
- What FL's mixing time is during `TriggerVoice` in song mode and in pattern mode (absolute song beats is assumed; any constant offset is found by the shifted match), whether `GT_LocalTime` changes it, and whether FL's note colour is the imported MIDI channel (used only to break a tie).
- Whether FL keeps the imported pitch bends as channel events after Replace and at which range it applies them; that controller 7 is lost (the manual says CC data is imported separately). With "Bends + volume from the recording" on, neither matters; the log records how far FL moved each note's pitch, which answers it.
- Hearing any of it.

### Decisions (and for the user)

Taken: the offer lasts until withdrawn (no time limit); an untouched channel is matched, a configured one only by hand; the first note binds when it is unambiguous; unison plays the first candidate meanwhile; track channels play the recording's bends and volume by default. For the user: whether "Bends + volume from the recording" should stay the default once the log shows what FL keeps; whether a channel whose notes are moved in FL should keep following the recording's expression at the new place (it does: the expression is read at the song position).

### Ids, hooks, places this took

Control ids 2034 (Log FL's calls), 2035 (Bends + volume from the recording), 2050-2054 (Play here); plugin test hooks 700-718 (`Bn2Plugin.Tracks.cs`); section `TRK1`; `RecMutants` 20-28; Prefs `HostLog`, `TrackExpression`; the host log file; FruitySdk `Fhd.GetMixingTime` / `GetPlaybackTime`, `Gt`, `TFPTime`; `HostSim.SongBeats` answers `FHD_GetMixingTime` in tests.

### Final runs (this section)

`refs/heads/main` had not moved from 421c4d8 (fetched again at the end): nothing to merge. Every DLL figure is from the Native AOT DLL published from this tree into `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r8_record`.

| Run | Result |
|---|---|
| `FruityHost record-test` (with the games) | 9 of 9 checks pass, 164 assertions, 17 of 17 deliberate bugs caught, 0 NOT RUN |
| `FruityHost selftest --only record` | PASS (15 of 15 DLL deliberate bugs caught, 17 of 17 library ones) |
| `FruityHost selftest` (all) | 60 results: 59 pass, 1 warning (QLQ an octave sharp, on purpose), 0 failed (9 min 57 s). One earlier full run on this branch gave a second warning in `presets` (an allocation the JIT-hosted part of that check saw once on the Game Boy chip); two reruns and this run were clean, and nothing of this work touches that path |
| `BrokenNes.Workshop --corrupt-test` (fresh build) | 275 passed, 0 failed, 0 not run |
| `BrokenNes.Workshop --snes-state-test` | 147 passed, 0 failed, 0 skipped |
| `UAT\run-suite.ps1 -Exe <fresh Workshop>` | 84 pass, 0 fail, 60 skip (their ROMs are not at this worktree's paths), 0 info |
| `UAT\entrypoints\notices-check.ps1` | pass (453 files scanned; no `*.bn2preset` tracked) |

## 12. 2026-10-18: the NES (agent `record-nes`)

Branch `worktree-agent-a594f6a114873faad`, from 421c4d8; `refs/heads/main` (1a04aad, section 11) merged in before the NES was wired into the window, the offer and the project.

### The request and the design calls

The user: "add support for NES for the replay thing we just did on SNES": record what a NES game plays in ROM mode into FL as MIDI tracks, each replayed "as is" by a BrokenNes2 instance in Direct mode with no game running; the same decisions as the SNES (file and live MIDI out, tempo, pitch bends, volume as velocity + controller, one track per instrument). The lead's call: a NES "instrument" is a channel and its timbre: **one track per (pulse channel, duty)**, the triangle, **one per noise mode**, **one per DMC sample**; the replay must write the game's period registers (exact or reported); hardware envelopes and sweeps carried by evidence.

### The evidence that shaped it (`FruityHost nesrec-survey`)

A sixth of the roster (253 files at `X:\EMULATION\Nintendo Entertainment System\`, every sixth from two offsets; 40 s each, Start pressed once at 5 s): 36 do not run here (mappers not supported, a crash), 12 play no note in that time, **205 play notes**:

| How the driver drives the chip | Games | Share |
|---|---|---|
| the hardware **decay envelope** on 5 %+ of its pulse / noise notes | 82 (40 %) | about 19 % of all pulse / noise notes |
| the **length counter** running (halt off) when the note is struck | 99 (48 %) | about 31 % of pulse / noise notes |
| the hardware **sweep** on at the note | 46 at all, 30 on 5 %+ of pulse notes | about 5 % of pulse notes |
| the **duty changed under a sounding pulse** | 131 (64 %) | 12,464 changes for 68,053 pulse notes |
| the triangle's **linear counter** ending notes (control off) | 122 | about 47 % of triangle notes |
| **DMC samples** / raw PCM through $4011 | 29 / 12 | |

So the register writes alone do not say when a NES note ends or how loud it is: the transcription reads what the chip **sounds**.

### What exists

| Part | Where |
|---|---|
| A model of the chip (APU_FIX's rules, event-driven: writes and quarter / half frames) | `Record/NesApuModel.cs`; its starting state from `APU_FIX.ProbeControl()` (new, read-only) |
| The capture (every $4000-$4017 write with its APU clock, frame marks, DMC sample copies through the mapper, never the bus) | `Record/NesRecorder.cs` (`NesRecorder`, `NesSession`, payload kind 'N'); `NesAudioMachine.RecordTap` (new) |
| Notes from what the chip sounds | `Record/NesNotes.cs` |
| Transcription (tracks, duty gating, exact bends and CC7, distinct velocities for a note's copies, presets, facts) | `Record/NesTranscribe.cs` |
| Direct mode's register code, in one place | `Record/NesDirect.cs` (moved verbatim from `Bn2Plugin.Apply`; Direct mode and the recorded-track presets call it) |
| The NES recording seen through the SNES shape (window, files, offer, matching, expression unchanged) | `Record/NesAdapter.cs`, `RecordHeld.Nes` |
| Live MIDI out | `Record/NesLive.cs`, `Emulator.Live.cs` |
| The plugin side | `Emulator.RecordNes.cs`, `Bn2Plugin.RecordNes.cs`, `NesPresetPlayer` (track mode), `TrackOffer.cs` / `Bn2Plugin.Tracks.cs` (NES engines, PRE-NES), `RecSection.cs` (version 2), `PresetNote.cs` (version 3), `RecTracks.cs` (velocity tie-break) |
| Checks | `FruityHost nesrec-test` (`NesRec.*.cs`), `selftest --only record-nes` (`SelfTest.RecordNes.cs`), `nesrec-dump`, `nesrec-survey` |

### How it works (the parts that are not obvious)

**The chip model.** Each write and each quarter / half frame (the frame sequencer's own clock, 4- or 5-step, from the game chip's counters at Record) updates the model; after each, every channel's state is read: audible (enabled, length counter running, period at least 8 and not muted by the sweep's target, volume above 0; the triangle: linear counter above 0, period at least 2), the period after the sweep, the volume in force (constant or the decay level), the duty, the noise's mode and index. Times are CPU cycles: a write at clock W comes after the cycles 0..W-1, as the bus's tap reports it.

**A note** is a stretch during which a channel is audible, cut where the driver strikes it again (its fourth register: the wave and the envelope restart, as a new FL note restarts them in the replay). A silence splits notes (the replay's next note restarts the wave, inaudibly, since nothing sounds before it). Two refinements found on real games: a channel made audible by its volume write a few cycles before the driver strikes it is one note, struck; the triangle sounds from the quarter frame after its strike ($400B reloads the linear counter then), so its note starts at the strike, the driver's moment (the replay's own chip delays it alike), and ends at the $4008 write that set its counter to 0. Notes shorter than 2 ms are left out and counted.

**Tracks and duty changes.** One track per (pulse channel, duty), triangle, (noise, mode), DMC sample (by its bytes). A note whose duty changes while it sounds is on the track of **each** duty it uses, struck at the same tick on each, its level 0 on a track while another duty sounds (controller 7 = 0). The instances' waves start together, so when the level moves from one instance to the other the duty changes exactly when the game changed it, and the wave does not restart (the replay check proves the silent copy is silent at every compared moment). The cost: such an instrument shows on two tracks.

**Pitch, volume.** The key is the musical note of the period the note holds longest (noise: index i is key 70 - 3i, Direct mode's mapping). Every period value is the key plus a 14-bit bend searched with the plugin's own `NesDirect` functions on FL's float cents at the track's bend range (the smallest of 2..48 that holds every move): the `direct` check shows every pulse / triangle period 8 (2) .. $7FF and every noise index reachable at the ranges 2, 3, 8, 12, 24 and 48 (24,600 of 24,600). The volume is velocity x controller 7 / 100 through `NesDirect.Level`, solved exactly (the middle of the interval of exact values, away from rounding edges). The sweep's periods are bends, the decay envelope's levels controller 7 values at each quarter frame: values exact, timing within FL's control step.

**The carrier: a NES preset with the recorded-track behaviour** (decided here). The preset is the round-6 NES preset (kind, duty or noise mode; constant volume, halt, no sweep, length 31: the registers Direct mode writes) plus a note block, version 3, that makes `NesPresetPlayer` play FL's notes with `NesDirect.Write`: the volume register from velocity x controller 7, the preset's duty. Why not plain Direct mode on the FIX chip: Direct mode's channels are exclusive per emulator (a recording has several tracks of one channel: "Pulse 1, 25%" and "Pulse 1, 50%" need a pulse slot each, two per emulator, eight emulators at most), and the SNES flow (presets in the library, the offer, the matching, settling on a Presets chip, PRS1 / TRK1) takes presets. The register code is Direct mode's own, so the registers are those Direct mode would write. Without the block (an older BrokenNes2) the preset plays as a plain NES preset and the Presets tab says "note behaviour this version cannot play". Instances settle on a **PRE-NES** emulator of their own. The DMC: a preset with the sample's bytes, refused with "cannot play on this console yet".

**FL's import-and-Replace.** The matcher (section 11) saw the duty copies of a note as unison candidates (same key, same position); copies now get velocities at least 3 apart (exactness kept: CC7 is solved for each copy's own velocity), the offer's index knows each note's velocity, and an untouched instance breaks a unison tie by FL's velocity of the note (`FPV_GetVelocity`). On Alien 3, 4 of 8 tracks were found for sure from their first 12 notes without the velocity, 8 of 8 with it.

**The project.** `REC1` **version 2** holds a NES session (the same header, deflate and CRC-32); SNES recordings stay version 1 (older DLLs read them); an older DLL refuses a NES section as made by a newer BrokenNes2 and writes it back as it came. A version 1 section holding a NES session, or version 2 a SNES one, is damaged.

**Live MIDI out.** The same model on the audio thread (allocation-free): a note-on per strike / audibility on its instrument's MIDI channel (channel and duty or mode, in first-sounding order), bends (range 12) and controller 7 as the values move, note-offs at silence; a duty change ends the note on one channel and strikes it on the other (live cannot strike both in advance).

### Verification (no sound card: every claim is a register comparison or a count)

The ROMs: `X:\EMULATION\Nintendo Entertainment System\` (never committed), VRUN built in (`Resources/default.nes`), VRUN's AudioPlayer roster through VRUN's converter (the VRUN tree at `P:\BrokenDev\env\...`). Each game picked by the survey for a feature: SMB1 (envelopes, length counters, linear counter), Bayou Billy (the sweep on every pulse note), Rad Gravity (the decay envelope on every note), Base Wars (duty changes, DMC samples), Contra (DMC, linear counter), Alien 3 (duty changes), Anticipation (envelopes, counters), Abadox, Dash Galaxy, Balloon Fight.

#### `FruityHost nesrec-test` (no DLL; 20 s takes)

9 of 9 checks pass (one with the ternary-tempo warning below), 187 assertions, 11 of 11 deliberate bugs caught, 0 NOT RUN, 525 s (a twelfth deliberate bug, the capture allocating, was added to the library list afterwards: caught, 6,600,000 bytes for 300,000 writes, in `nesrec-test --only capture`).

| Check | What it compares | Result |
|---|---|---|
| `model` | the model against APU_FIX's own state (`ProbeApuState`): 40 bare chips fed random writes, and 11 games at every frame's end for 30 s | 160,000 random comparisons equal; VRUN, SMB1, Bayou Billy, Abadox, Rad Gravity, Dash Galaxy, Base Wars, Contra, Balloon Fight, Anticipation, Alien 3 (476 to 25,756 writes each) equal at all 1,802 frames |
| `direct` | `NesDirect` against Direct mode's old expressions; every period reachable by a key and a bend | 200,000 pitches and levels identical; 24,600 of 24,600 (channel, period, bend range) reachable exactly |
| `capture` | the capture against the bus's own count of writes (a second, independent tap) | VRUN 351, SMB1 8,891, Contra 2,624 writes = the bus's count, 0 lost; Contra's 2 DMC samples = the cartridge bytes; 0 bytes for 300,000 writes; a starved capture counted 761,352 lost of 786,432 sent; every cut and a newer version of the saved form refused |
| `transcribe` | the MIDI file's bytes played through the presets' own register code (`NesDirect`, a FIX chip per track), the registers it writes against the game's at every moment a value was held 4 ms or more | VRUN 169/169 values, SMB1 1,201/1,201, Bayou Billy 2,723/2,723 (411 sweep notes), Rad Gravity 476/476 (36 envelope notes), Base Wars 2,848/2,848 (2,377 duty-gated moments silent; 243 duty-changing notes; 3 DMC tracks), Contra 1,436/1,436, Alien 3 3,900/3,900 (2,699 gated), Anticipation 3,560/3,560 (147 envelope notes), corrupted VRUN 159/159, corrupted SMB1 1,337/1,337; 0 reported, 0 unreported; note starts within 0.95 ms; tempo SMB1 100.15, Base Wars 150.26, Contra 150.28, Anticipation 100.16 BPM (5 of 5 stretches each) |
| `smf` | the file read back by an independent Python parser and by VRUN's own `MidiFile` | Base Wars: 11 tracks, both parsers agree with the writer |
| `section` | REC1 version 2 | 1,136 bytes round trip; 304 of 304 damaged or cut sections refused; the SNES reader says "made by a newer BrokenNes2" |
| `live` | live MIDI out fed the game's writes against the transcription | VRUN 89/89 notes, Rad Gravity 24/24, Alien 3 1,076/1,076 (82 duty changes struck on the other channel), Bayou Billy 124/124: same key, same moment; 0 bytes |
| `vrun-song` | 7 songs converted by VRUN's own converter, played by VRUN's engine; the transcription against VRUN's decoder model frame by frame | 1,848 + 6 x 5,456 of the same frame fields; tempo: synthetic 120.20 against 120 (+0.17 %), think 99.00, boss 130.00, chicagovoices 80.00 exact; baropen 150.01 and baropencrpt 150.13 against 100, puzzle 114 against 152: the right grid, another beat (a three-feel; warning) |
| `match` | the matcher of section 11 on the NES tracks' first 12 notes | Base Wars 7/7 (6/7 without FL's velocity), Alien 3 8/8 (4/8 without), VRUN 4/4 |

#### `FruityHost selftest --only record-nes` (the Native AOT DLL)

The library checks again on 10 s takes (12 of 12 deliberate bugs caught), then the DLL, published from this tree as `Plugin/build-dist.ps1` does:

| Scenario | Result |
|---|---|
| flow | VRUN through the window with real mouse messages: Record, 15 s, Stop: 4 tracks, 65 notes; the MIDI file the window wrote = the library's transcription of the same session; every period and level exact (the facts) |
| capture | 218 writes in the capture = 218 seen by an independent tap on the machine; **0 bytes allocated on the audio thread over 1,800 blocks** |
| replay | the MIDI file played through FL-like notes into instances on PRE-NES holding the recorded-track presets; the registers each instance's own FIX chip wrote (hook 741, `NesReplayProbe`) against the game's: VRUN 89 of 89 values, Base Wars 831 of 831 + 12 duty-gated moments silent; note starts within 1.01 ms; 0 bytes |
| project | REC1 version 2 (1,146 bytes) reopened to the same 1,978-byte MIDI file; a damaged section and a changed one under the old checksum refused with a message |
| replace | FL's import-and-Replace with NES tracks: instances with an empty state (A), their own fresh state (B), made before the offer (D), and with the bends and volume lost (K) each found their track from their notes and settled on PRE-NES; K replayed 36 of 36 values exact from the recording's expression; Play here; A's project reopened plays its track (TRK1); 0 bytes on the audio thread |
| live | 94 note-ons, 94 note-offs on port 4 through `MIDIOut_Delayed`; 54 of 54 transcribed notes found; 0 bytes |
| refusals | Record in Direct mode refused; a DMC track's preset: "cannot play on this console yet: the NES DMC reads its sample from the game's memory and Direct mode has no game to read it from" |
| deliberate bugs | 6 of 6 caught: the capture allocates (4,312 bytes), track presets played as plain NES presets (volume 15 for the game's 4), duty presets at 50 % (duty 2 for 1), the section's checksum not checked, track channels ignore the recording's expression ($193 for $192), live MIDI out silent |

#### The deliberate bugs (each must make its check fail; all are caught)

| Bug | Caught by |
|---|---|
| the model ignores the hardware sweep | `model` (100,325 of 160,000 comparisons differ: pulse 2 period $027 against the chip's $02B) |
| the model's envelope decays two steps at a time | `model` (pulse 1 volume 13 against 14) |
| pitch bends one register step off | `direct` (24,573 of 24,600 periods unreachable); `transcribe` (the replay wrote $64C, the game $64D) |
| every 1,000th write lost without a word | `capture` (SMB1: 3,091 kept, the bus reported 3,094) |
| controller 7 one value past its interval | `transcribe` (the replay wrote volume 10, the game 9) |
| the duty tracks' presets all at 50 % | `transcribe` (Bayou Billy's 75 % track); DLL `replay` (Base Wars: duty 2 written for the game's 1) |
| note starts quantised to the 16th grid | `transcribe` (volume 9 written where the game still had 15) |
| live MIDI out sends nothing / live note-offs lost | `live` |
| the copies of a duty-changing note keep the same velocity | `match` (Base Wars: 38 of 43 copies not 3 apart) |
| the capture allocates at every fourth-register write | `capture` (6,600,000 bytes for 300,000 writes); DLL `capture` (4,312 bytes on the audio thread) |
| the track presets play as plain NES presets | DLL `replay` |
| the project section's checksum is not checked | DLL `project` (a changed section under the old checksum) |
| track channels ignore the recording's bends and volume | DLL `replace` (K wrote $193 for the game's $192) |
| live MIDI out sends nothing (DLL) | DLL `live` (0 messages) |

### Found on the way

- **The triangle's onset.** A triangle note sounds at the quarter frame after its strike; starting the transcribed note when it sounds put it up to 4.2 ms after the driver's write, which the VRUN cross-check showed as frames where the engine had written the note and the transcription had not started it. Notes now start (and end) at the driver's write; the replay's chip adds the same quarter-frame wait, so on average the replay is on time.
- **A volume write a few cycles before the strike** (VRUN writes $4000 then $4003, 19 cycles apart) made 19-cycle notes: now one note.
- **Duty copies looked like unison** to section 11's matcher (same key, same position): 4 of 8 Alien 3 tracks were not found for sure. Distinct velocities and the velocity tie-break fixed it.
- **Never read the CPU bus from inside the APU write tap**: `NES.PeekCpu` is a bus read (it moves open bus and, in precise windows, steps the PPU). DMC sample bytes are read through the mapper (`TryCpuToPrgIndex` + `PeekPrg`).
- **88 bytes on the audio thread when a recording began there** (the DLL `capture` scenario): `NesAudioMachine` made its tap delegate the first time a tap was wanted, which is on the audio thread when Record starts with Instrument Runaway off; and the chip's name was read there. The delegate is now made with the machine (this also covers a NES stem claimed for the first time from the audio thread) and the name is read on the UI thread at Record: 0 bytes.
- **The library's allocation counts** (JIT-hosted, `GC.GetAllocatedBytesForCurrentThread`) read 4,008 bytes once in a combined selftest (and the SNES check 6,192 once): the first passes of the JIT and a collection inside the window. The capture and live checks now run the same loop once before measuring and collect first (the DLL's counts, AOT, never showed it); a real allocation per write would be 300,000 x 24 bytes or more, and the deliberate bug still shows.
- **The first DLL run** also found: two channels on one track waited for the second engine (replace K 28 ms late: the scenario now puts its channels on distinct tracks, as FL's import does); the deliberate checksum bug was not caught (the project scenario now also writes a changed section under the old checksum); the plain NES preset path had no probe (bound now); Bayou Billy's take began before its music (parked at 900 frames); a 300-byte corruption silenced VRUN's music (24 bytes: 47 notes).
- The SNES record suite's refusal "Record on the NES" became a Game Boy; its "newer section" test uses version 3 (version 2 is a NES recording's now).

### Not tested (said plainly)

- **FL Studio itself** (its import of the file, Replace, which velocity FL hands the plugin, MIDI out from a generator) and **hearing** any of it, as for the SNES.
- **The NES DMC replay**: no engine plays a DMC sample yet; the tracks carry the notes (when each sample started, its rate as the key) and the presets say so.
- **Raw PCM** written to $4011 (12 surveyed games): not transcribed (counted).
- **A game on another chip than FIX**: the capture rebuilds the starting state from the last registers (said); the transcription follows FIX's rules; only FIX was recorded by the checks.
- **PAL games**: the plugin's NES is NTSC.
- **Recordings longer than a few minutes**: the capture is checked to 300,000 writes and starved; the 39-minute length cap is arithmetic, not a run.

### Decisions taken here (and for the user)

Taken: notes are what the chip sounds (the model), cut at strikes; a NES track = (channel, duty) / triangle / (noise, mode) / DMC sample (the lead's call); duty changes as gated copies on each duty's track (exact; the alternative, a duty "macro" carried by one preset per instrument, would keep one track per instrument but needs a timed macro in the NES preset engine); the carrier is a NES preset with the recorded-track behaviour on PRE-NES (above); the triangle starts and ends at the driver's write; copies get distinct velocities; tempo reused unchanged from the SNES (a ternary feel can come out at 3:2 or 3:4 of the notated tempo, on the right grid: VRUN's baropen, baropencrpt, puzzle).

For the user: (1) duty changes: two tracks (exact, now) or one track per instrument with a duty macro (more readable, a new preset feature); (2) the replay chip: FIX always, or the game's own chip carried in the track preset (for a game recorded on a gimmick chip); (3) whether a DMC sample engine is worth building next (29 of 205 surveyed games use samples); (4) the tempo rule for a three-feel (fold to 80-160 BPM in twos, as now, or prefer threes when the grid allows both).

### Open problems and next steps

- A DMC sample engine (the bytes are in the presets already).
- `RecTempo` cannot tell a three-feel from a two-feel on some songs (3 of 7 VRUN songs: the grid right, the bar lines wrong).
- Copies that cannot be given distinct velocities (a copy already at 127 next to another) are counted; such channels may need Play here.
- Live MIDI out strikes a duty change again on the other duty's channel (the file keeps the wave running).

### Ids, hooks, tags and places this took

| What | Taken |
|---|---|
| Plugin test hooks | 740-779 (`Bn2Plugin.RecordNes.cs`; 740-747 used) |
| Editor test hooks | 780-789 reserved |
| Control ids | 2080-2119 reserved (the NES uses the Record window's own controls) |
| Project section | `REC1` version 2 = a NES session (payload kind 'N') |
| Preset note block | version 3 = a recorded NES track (2 bytes: format 1, flags 0) |
| Deliberate bugs | `RecMutants` codes 101-103 (capture), 111-118 (transcription), 121-122 (live); `NesModelMutants` 1-2 |
| Shared tree, additive | `APU_FIX.ProbeControl()`, `NesAudioMachine.RecordTap`, `NesApuInstrument.WriteTap` / `SamplesRendered` |

### Final runs

`refs/heads/main` had not moved from 1a04aad (fetched again at the end, `--no-tags`): nothing more to merge. The DLL was published from this tree into `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r8_nes`.

| Run | Result |
|---|---|
| `FruityHost nesrec-test` (the games, 20 s takes) | 9 of 9 checks pass (`vrun-song` with the three-feel warning), 187 assertions, 11 of 11 deliberate bugs caught, 0 NOT RUN (525 s); with the twelfth bug added, `--only capture` caught it |
| `FruityHost selftest --only record-nes` (the final DLL and test bench) | PASS (7 min 36 s): library 12 of 12 deliberate bugs caught, DLL 6 of 6; capture 0 bytes on the audio thread over 1,800 blocks |
| `FruityHost selftest` (all) | 61 results: 60 pass, 1 warning (QLQ an octave sharp, on purpose), 0 failed (16 min 46 s); `record` and `record-nes` both pass |
| `BrokenNes.Workshop --corrupt-test` (fresh build) | 275 passed, 0 failed |
| `BrokenNes.Workshop --snes-state-test` | 147 passed, 0 failed, 0 skipped |
| `UAT\run-suite.ps1 -Exe <fresh Workshop>` | 84 pass, 0 fail, 60 skip (their ROMs are not at this worktree's paths), 0 info |
| `UAT\entrypoints\notices-check.ps1` | pass (471 files scanned; no `*.bn2preset` tracked) |

## 13. 2026-10-18: relink (the user's report from FL, build r12)

Agent `relink`, branch `relink` from `refs/heads/main` at 71c538d (section 12 merged).

### The report

In the user's words: "The detection of the BrokenNes instances doesn't work anymore when trying to reload the channels. I drag and drop the MIDI file, it creates MIDI Out channels, then I replace them one by one and the counter never increases. If I try what I could do before, which is adding new BrokenNes channel instances, it doesn't detect them either. The count stays 0 forever. Perhaps we need a button for those instances to be able to force relink them or something. I would prefer if it could detect them automatically as we replace the channels, but if that cannot be done, an anchor button to relink to the bunch could be a decent option."

The evidence: `Documents\BrokenNes2 host log.txt` held 35 lines, every one from the test bench (host tag 0x77, "created ...; no tracks on offer", 00:41-04:01), nothing from the user's FL session.

### What was wrong (three things, each enough)

- **The counter counted only channels that had found their track**, and a channel finds its track at its first note, during playback: before Play the window could only show 0, whatever happened. (Section 11 built it that way; the round 8 deal before it assigned at construction, which is what the user was used to.)
- **Eligibility was an "untouched" heuristic** that any parameter FL set to a non-default value, or a state of ours with non-default values, turned off for good, silently. What FL's Replace hands the new instance is not known (nothing here can run FL); if it pushes the replaced channel's values or a default preset of the user's, every replaced channel was out, with nothing on screen.
- **The log was off**: a box to tick in the Record window, and the user's session wrote nothing. The test bench, meanwhile, wrote into the user's real file: the record scenario put `BROKENNES_HOSTLOG_FILE` back to "unset" while the plugin's writer still had the last "created" line queued (written 200 ms later to Documents).

Found on the way, by the editor-shot capture of the new banner: the first version counted every instance on the recording's own emulator out as "the recording's channel". With a NES game that emulator has free channels, and FL's Auto emulator puts the first replaced channels there: the first four replaces of a NES song would again have counted 0. Only a configured instance there (the one that recorded, the user's own stems of that game) is the recording's own now; check and deliberate bug `SourceEmulatorExcludes`.

### What was built

- **Joining, visibly** (`Bn2Plugin.Relink.cs`): every instance made while tracks are on offer (a new channel, or the one FL makes for a Replace) joins them in its constructor; instances still pristine (nothing but defaults) when tracks are offered join then; the others alive at that moment get a "not linked" reason. FL's parameter pushes and states without a game, preset or track never unlink (they are logged). An instance leaves only when it is clearly the user's own: a restored project state that holds a game (its machine, a frozen game's sound, a song, a recording, a corruption, pushed instruments), a preset or a track; a change in its editor; a preset or game loaded into it; Record pressed in it.
- **The counter** (`TrackOffers.Count`, `CounterText`): the Record window's new **CHANNELS** line says "N channels joined, M found their track; K not linked" and lists every instance ("#4 "name": waiting for its first note", "#5: plays track 2 (found by its channel's name)", "#6: not linked (its project state holds a preset)"); the offer button repeats "On offer (N joined, M found): stop". The window was laid out again to make room (one line of description, three facts instead of four, the scroll note in the TRACKS header).
- **The anchors** (the user's idea): a banner over the screen of every BrokenNes2 while tracks are on offer (`Bn2Editor.Link.cs`): not linked = red, the reason and a big **Link to recording** button; linked = green "LINKED TO THE RECORDING: Press Play in FL and I will find my track from my first note" with **Unlink**; found = the track and how. **Link all BrokenNes2 channels** in the Record window links every instance that is not linked and is not the recording's own. A hand link survives knob changes in that editor; a preset, a game or Unlink ends it.
- **The channel's name** (item 3 of the brief): the public SDK header (`fp_plugclass.h`, the copy the repository mirrors) has `FHD_GetContextInfo = 76` ("get information about various things. Index is the information type (see CI_ constants)"; "requested info is returned as the result of the function") with `CI_TrackName = 0` ("(R/W) PAnsiChar encoded as UTF-8"), and `FPD_ContextInfoChanged = 41` ("call FHD_GetContextInfo for the new value(s)"); there is no FPD_SetName and no other name call for a plugin (`FHD_SetNewName = 10` is the other direction: the plugin names its channel). FL's own release notes show the "track info" is FL's wrapper feature ("some track information can now be shown in VST3 plugins"; "the alpha value for the track color provided to plugins is zero"; "the CLAP version of FabFilter Pro-Q 4 doesn't show track names"). The header does not say whether a generator gets its channel's name or its mixer track's. A linked instance asks when FL has opened it (`FPD_PluginLoaded`), when FL says the context or the names changed (`FPD_ContextInfoChanged`, `FPD_RoutingChanged` with `RCV_NamesChanged`), after a project load (`FPD_ProjLoaded`), and once a second from FL's Idle and from its open editor while it waits. The answer is used only as a pointer when it is one (above 64 KB, in committed readable memory, `VirtualQuery`, a terminator within 1 KB). A name equal to one offered track's name (case and outer spaces aside), or that name followed by a non-alphanumeric suffix (" #2"), takes that track at once, through the same path as Play here (the engine prepared on the UI thread, settled on the tracks' emulator; TRK1 `how` = 3). A name that starts with "BrokenNes2" is explained: FL's "Auto name channels" (Options > General settings) renames a channel whose plugin is replaced (FL's release notes: "14108 Channel name changes when replacing and "Auto name channels" is disabled", fixed; "14245 Don't rename a channel if a plugin asks this and the "Auto name channels" option is off"). The track names were already unique and recognisable ("<game> HH.mm.ss - NN ..."); the `relink` check holds that the MIDI file's track names (what FL names imported channels) are the offered names and are distinct.
- **The host log** (`HostLog.cs`): on by itself for every instance while tracks are on offer (from the constructor; the instances alive when tracks are offered are named with their place and values), and whenever the box is ticked (process-wide, remembered as Prefs `HostLog`); FL's host version, Dispatcher calls by FPD_ name, parameters by name, every answer to the name call, joining / leaving / linking; under 1 MB (the older part kept as `BrokenNes2 host log.old.txt`). **Tests never write the user's file**: the test bench names a scratch file and a scratch folder standing for Documents (`BROKENNES_HOSTLOG_FILE`, `BROKENNES_HOSTLOG_DEFAULT_DIR`) before it loads the plugin (`HostLogGuard.Install`, first thing in `Program.cs`), and the `hostlog` check, last of the run, fails when the real file changed.
- **An offer ends with its emulator**: the emulator that made a recording on offer closing (its last channel deleted, the project closed) withdraws the tracks (`EmulatorHub.Drop`), so the channels of the next project FL opens do not join a recording that is gone.

### Verification

`FruityHost selftest --only relink` (the Native AOT DLL), on the synthetic SNES recording (20 s, 4 tracks) and on a NES VRUN take (15 s): PASS, 125 s in the full run; the same 11 channels and steps for both recordings, every counter string equal, 0 bytes on the audio thread for each, 13 of 13 deliberate bugs caught; the host log of the scenario had 381 lines (SNES) and 247 (NES).

| Step (before any note) | Counter expected and read (also as the window draws it, editor hook 677) |
|---|---|
| tracks offered; made before: one pristine, two FL had set values on | "1 channel joined, 0 found their track; 2 not linked" |
| a replacing channel, empty state | "2 channels joined, ..." |
| one FL pushed NON-default values on (volume 640, pan -25, duty, noise mode, instrument 3, emulator 7, channel) | "3 channels joined, ..." |
| one given a state of ours with other values | "4 channels joined, ..." |
| one FL names after its track (the test host answers `FHD_GetContextInfo`) | "5 channels joined, 1 found its track; 2 not linked" (taken before any note; its first note played by the track's preset) |
| one whose host answers the name call with the number 7 | "6 channels joined, ..." (not read as a pointer) |
| one FL put on the recording's own emulator, named "BrokenNes2" | "7 channels joined, ..." (the list explains the name) |
| a new channel, then a project state with a preset | "8 joined" then "7 channels joined, 1 found its track; 3 not linked" |
| a new channel, a click on its Volume slider, then its banner's Link to recording (real mouse) | "8 joined", "7 joined; 4 not linked", "8 channels joined, 1 found its track; 3 not linked" |
| Link all in the Record window (real mouse) | "11 channels joined, 1 found its track" |
| after Play (each linked channel's first 12 notes at the file's song positions) | "11 channels joined, 11 found their track"; all settled; 0 bytes allocated on the audio thread |

The host log of each scenario (box unticked) holds every instance's creation or presence, its joining, the name FL gave, the number refused, the preset channel leaving, the slider click leaving, both kinds of hand link and FL's parameter pushes; nothing was written in the folder standing for Documents; the user's real file unchanged.

Deliberate bugs (each must make the check fail; all are caught):

| Deliberate bug (`RecMutants`) | Caught by `relink` |
|---|---|
| 29 FL's non-default parameter pushes make a joined channel leave (the r12 rule) | the counter after those pushes said "2 channels joined, 0 found their track; 3 not linked" |
| 30 instances made while tracks are on offer do not join them (SNES, and again on the NES) | "1 channel joined, ...; 3 not linked" after the empty-state replace |
| 33 the counter counts only the channels that found their track (what the user saw) | "0 channels joined" right after the offer |
| 32 Link to recording says linked and does nothing | the channel stayed not linked (role 2) |
| 31 Link all counts the channels and links none | the three were still not linked |
| 34 the channel's name never takes its track (SNES, and again on the NES) | the named channel played no track before its notes |
| 37 a project state with a preset stays joined | that channel was still joined (role 3) |
| 35 the host log does not run by itself while tracks are on offer | the scenario's instances missing from the log |
| 36 the host log ignores the test's file (it would write the user's Documents) | nothing in the scratch file, a file in the folder standing for Documents |
| 23 the track's engine is built on the audio thread | 3,637,360 bytes allocated on the audio thread |
| 39 every instance on the recording's emulator counted out | the channel FL put there counted as the recording's own (role 1) |
| 38 the host log is never capped | `hostlog`: past 1 MB the file kept growing (1,100,547 bytes), no older part |

### Not verifiable without FL (said plainly)

- **What FL hands a replacing instance** (a state, parameter values, in which order, whether it destroys and re-creates): none of it changes the joining any more, and the log records all of it.
- **Whether FL answers `FHD_GetContextInfo(CI_TrackName)` for a generator** with its channel's name, its mixer track's name, or nothing; whether FL's Replace keeps the imported channel name (with "Auto name channels" on, FL's own notes suggest it renames the channel after the new plugin). The name path is a shortcut: the notes still find every track.
- When FL calls `Idle` for a plugin whose editor is closed (the name is also asked at the dispatcher events and from the open editor).
- Hearing any of it.

### Decisions (and for the user)

Taken: instances made while tracks are on offer join whatever FL pushes (a user's own instance is recognised by content: a game, a preset, a track, or an editor change), instances made before join only when pristine (a project opened while tracks are on offer must not hand its configured synth channels to the matcher), configured instances on the recording's emulator are its own; the log runs by itself while tracks are on offer; a channel whose name names a track takes it at once (as Play here); the offer ends with the emulator that made it. For the user: whether the banner should also show over a game's picture for configured channels (it does now, while tracks are on offer); whether to rename a channel after the track it found (`FHD_SetNewName`; FL ignores it when "Auto name channels" is off).

### Ids, hooks, places this took

Control ids 2036 (Link all), 2060 (Link to recording), 2061 (Unlink); plugin test hooks 719 (notes in the offer's index), 720-738 (`Bn2Plugin.Relink.cs`); editor hooks 677-679; `RecMutants` 29-39; TRK1 `how` 3 (by the channel's name; older builds drop such a binding, the preset still plays); `FruitySdk`: `Fpd.RoutingChanged` 25, `ProjLoaded` 27, `PluginLoaded` 40 (documented), `ContextInfoChanged` 41, `Fhd.GetContextInfo` 76, `Ci.TrackName` 0; `FruityPluginBase.Idle`; `HostSim.TrackName`, `RawContextAnswer`, `ContextInfoCalls`; environment `BROKENNES_HOSTLOG_DEFAULT_DIR`; selftest results `relink` and `hostlog` (last); `editor-shot ... --record s --relink-shots prefix`.

### Final runs (this section)

`refs/heads/main` had not moved from 71c538d (fetched again at the end, `--no-tags`): nothing to merge. Every DLL figure is from the Native AOT DLL published from this tree into `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r8_relink`.

| Run | Result |
|---|---|
| `FruityHost selftest` (all) | 63 results: 62 pass, 1 warning (QLQ an octave sharp, on purpose), 0 failed (19 min 13 s); `relink` 13 of 13 deliberate bugs caught, `hostlog` (last) the user's real log untouched, `record` 15 of 15 DLL and 17 of 17 library deliberate bugs, `record-nes` 6 of 6 and 12 of 12 |
| `BrokenNes.Workshop --corrupt-test` (fresh build) | 275 passed, 0 failed, 0 not run |
| `BrokenNes.Workshop --snes-state-test` | 147 passed, 0 failed, 0 skipped |
| `UAT\run-suite.ps1 -Exe <fresh Workshop>` | 84 pass, 0 fail, 60 skip (their ROMs are not at this worktree's paths), 0 info |
| `UAT\entrypoints\notices-check.ps1` | pass (475 files scanned; no `*.bn2preset` tracked) |
| The user's `Documents\BrokenNes2 host log.txt` after every run of this work (selftests, editor-shot captures) | unchanged: 2,208 bytes, last written 04:01 (the 35 lines the r12 test runs left in it; they are all host tag 0x77, the test bench's, and can be deleted) |

