# Round 9 `capture`: Capture from FL (an FL song with its pitch bends, from the BrokenNes2 channels themselves)

Date: 2026-10-18. Agent `capture`, branch `worktree-agent-aff9e0435d29c8ea2` (from `refs/heads/main` at 5fcae89). User-facing description: `Plugin/README.md` ("Capture from FL"); maintenance notes: `Plugin/CLAUDE.md`; the user's steps: `docs/handoff-testing-guide.md` 4g. Design background: `docs/projects/brokendev-music-pipeline.md` sections 9-11 (milestone P3, the "capture pass"), `docs/projects/round7-midi.md` (Test MIDI export), `docs/projects/round8-record.md` (the SMF writer, live MIDI out, the host log).

## 1. The report, and what was asked

In the user's words (FL Studio): "When exporting to MIDI, the pitch bends do not get translated correctly to MIDI ... even with their standard MIDI Out channel that doesn't work ... being able to make bends in the piano roll and use that automation in the MIDI is key to being able to make good songs for the game." And: "in order to export to MIDI, we have to replace all the BrokenNes channel instances with MIDI Out channels ... We need to be able to keep the BrokenNes VST as-is and have it handle the MIDI out stuff."

The lead's brief: BrokenNes2 captures what FL plays into a MIDI file with the pitch bends, one track per instance on the MIDI channel VRUN's converter expects, and feeds it straight into Test MIDI export ("Capture from FL": arm, Play from the start, Stop: the song converts and plays in VRUN's sound-test player, the facts shown); optionally the notes and bends as live MIDI out; proof with an FL-faithful test host; docs. Out of scope: a pitch-bend command in VRUN's engine (the user's decision).

## 2. What FL tells a plugin (the evidence, and two corrections)

From the user's real FL log (`Documents\BrokenNes2 host log.txt`, FL 26.0.1, 2026-10-18; read, never written by any test):

| What | Evidence | What the capture does with it |
|---|---|---|
| No song position | every logged note: "song position none" | its own clock: the samples FL rendered on each instance since its `FPD_SetPlaying` 1, at the rate they were rendered at |
| `FPD_SetSamplesPerTick` (id 20) | value 1129579756 at every Play and many times while idle | **a float in Value**: 1129579756 = **0x435404EC** (the brief computed 0x43544CEC: wrong) = **212.0192 samples per tick = 44,100 x 60 / (130 BPM x PPQ 96)** exactly: the user's FL ran at 44.1 kHz, 130 BPM, PPQ 96 (`capture-test` check `tempo`) |
| `FPD_SetTimeSig` (id 14) | a value 16 bytes past the instance's host tag | a pointer to FL's `TTimeSigInfo { StepsPerBar, StepsPerBeat, PPQ }`, read only where the memory is committed and readable (`VirtualQuery`), absurd values refused |
| Play / Stop | per instance: SetSamplesPerTick, SongPosChanged 0, SetPlaying 1, SetSamplesPerTick, then the notes at the same millisecond | a lane starts at its own SetPlaying 1 and stops at SetPlaying 0 |
| Velocity | `FPV_GetVelocity` 0.632 for MIDI 81, 0.5 for 64 | MIDI velocity = FL's value x 128 |
| The pitch's base | **correction from the `reload` agent's analysis** (lead's message, `r8-reload` 6180510, `Record/FlNotes.cs`): FL hands a voice's pitch in cents above the **channel's** root note: C5 on a channel made in FL (Bendy: note = Pitch / 100 + 60; this plugin's FL render certification at that base), MIDI note 0 on a channel FL made by importing a MIDI file (the log's 7100 for key 71). My first version read InitLevels from MIDI note 0 and FinalLevels from C5, which was wrong for the user's normal case (channels made in FL): fixed, see section 4 | the base per channel: C5 unless the notes only fit MIDI note 0; said on screen and in the file |
| `FHD_GetMixingTime` | `TFPTime.T` is an INPUT "optional offset in samples" (fp_plugclass.h); the older code passed NaN, which is why FL answered nothing (reload's finding, confirmed by the lead) | the capture asks with offset 0 at every note and reports FL's answer against its own clock (a fact when FL answers); nothing depends on it |
| `FPE_Tempo` | not in the log (ProcessEvent was not logged) | used when FL sends it; else samples per tick x PPQ; else 120 BPM said as an assumption |

## 3. What exists

| Part | Where |
|---|---|
| A lane per instance: preallocated chunks of events (note-on with FL's InitLevels / FinalLevels pitch as FL hands them and its velocity, pitch changes, releases, the instance's tuning, FL's song-position answers), the clock, losses counted | `Plugin/BrokenNes2.Plugin/Capture/CaptureLane.cs` |
| The take: armed, recording, finishing, done, cancelled; the worker that feeds chunks and builds | `Capture/CaptureSession.cs` |
| What FL said about time, and the take's tempo | `Capture/FlClock.cs` |
| Lanes -> notes, the base key per channel, the owner's bends, bend ranges, MIDI channels, the bar-line end, the facts (ranked), the SMF | `Capture/CaptureSong.cs` |
| FL's pitch above the channel's base, velocity / 128 (reload's, one copy) | `Record/FlNotes.cs` (byte for byte `r8-reload` 6180510) |
| Deliberate bugs | `Capture/CaptureMutants.cs` (15) |
| The instance's hooks (Dispatcher timing calls, TriggerVoice / Release / Kill, the start and end of each block), identity, test hooks 840-859 | `Bn2Plugin.Capture.cs`; small lines in `Bn2Plugin.cs`, `Bn2Plugin.Record.cs` |
| The notes and bends as live MIDI out | `Bn2Plugin.CaptureLive.cs` |
| The process-wide hub: registry, Arm / Cancel, the finished take -> file -> Test MIDI export, Save MIDI, the live switch | `CaptureHub.cs` |
| The window: Capture from FL and Save MIDI... beside Open MIDI..., the status and facts strip, the MIDI-out box (ids 2120-2122, editor hooks 860-869); the window grew to 650 pixels | `Bn2Editor.Capture.cs`, `Bn2Editor.Song.cs` |
| The host log runs while a capture is armed | `HostLog.SetCapture` |
| Checks: the FL-faithful host, the library checks, the independent reader, the DLL scenarios | `Plugin/BrokenNes.FruityHost/Capture.FlHost.cs`, `Capture.Checks.cs`, `Capture.SmfDump.py`, `SelfTest.Capture.cs`; `editor-shot --capture` |

## 4. How it works (the parts that are not obvious)

**The clock.** FL gives no song position, so each lane counts the samples FL asks its instance to render (`Render`'s length, at `EmulatorHub.HostRate`) from that instance's own `FPD_SetPlaying` 1: a note-on is stamped with the time the next block starts (FL triggers voices between blocks), a pitch change at the start of the block FL set it for (FL rewrites FinalLevels between blocks, so once a block is as often as FL changes it). Rate changes (FL's export at another rate) are followed block by block. Seconds -> ticks: 960 per quarter at the take's tempo (the file's own microseconds per quarter, so the file plays the notes at their real times). A lane that never got a Play of its own but gets a note while the take runs joins at the others' clock (said). Lanes whose clocks differ by more than 50 ms at Stop are named (FL's Smart disable would do that).

**The base key** (`CaptureSong.ResolveBases`). FL counts a voice's pitch from its channel's root note and tells a plugin nothing about it. A note's key above the base is the nearest key of InitLevels + the instance's Coarse / Fine. Per channel: the base the instance knows (none before reload merges; then reload's `pitchBase` when it found it from a track); else **C5**; but when C5 would put a key above 127 (and every key fits from MIDI note 0), **MIDI note 0** ("only fits there", said among the first facts, in the warning colour); a channel with a key below 0 from MIDI note 0 is C5 for sure; a channel whose notes fit both follows the sure ones when they all said MIDI note 0 ("like the other channels", said), else C5 (said when others were not). The base used is in the facts, the host log and the file's text. A bend does not depend on the base (it is the played pitch minus the key, both read from the same base).

**The bends.** What the instance plays = FinalLevels (from the base) + Coarse x 100 + Fine. A BrokenNes2 channel plays its newest sounding note, so the track's bend at every change is that pitch minus the newest note's key; written whenever its 14-bit value changes (before the note-on at the same tick: order note-offs, controllers, bends, note-ons); the range per track (RPN 0) is the smallest of 2..48 semitones whose 14 bits reach every move (up to +range x 8191/8192, down to -range); beyond 48 clamped and counted. Error left by the 14 bits: at most range x 100 / 16,384 cents (0.166 at range 27).

**The tracks.** One per instance that got a note, named after its role (Pulse 1, Pulse 2, Triangle, Noise, DMC from a NES preset's kind, or FL's name for the channel when FL gives one; two of one role get the instance number). MIDI channels for VRUN's converter (`Midi2Nes.SelectChannelMap`): Pulse 1 -> 1, Pulse 2 -> 2, Triangle -> 3, Noise -> 10, others 5-9, 11-16 (4 never: the converter compares channel 10 with channel 4 for the noise). The converter still chooses by itself (the three busiest non-noise channels; the lowest on average gets the triangle): the builder runs the converter's own choice on the file and says when a part does not land on its own NES channel ("VRUN's converter chooses by itself: triangle plays Pulse 2, ..."), and which parts VRUN cannot play (more than four).

**The end and the loop.** Every track ends at the bar line after the last note-off (FL's time signature; 4/4 when unread): VRUN's converter loops the song at the file's span. Notes still sounding at Stop end there (said).

**Feeding Test MIDI export.** The worker writes `%TEMP%\BrokenNes2\Captures\FL capture HH.mm.ss.mid` and calls `Emulator.OpenSong` on the emulator of the instance that armed the take: the round 7 path (VRUN's converter, the template, the song as the NES game in ROM mode, its facts). The window shows the take's status and its first three facts above the song box (which has the converter's facts). Arming again while that emulator plays the capture puts it back in Direct mode (said), so FL's notes are heard while capturing.

**Live MIDI out** (off by default, Prefs `CaptureMidiOut`): note-ons with FL's velocity, note-offs, the newest note's bend at range 48 (RPN 0 before the first note on each channel), through `MIDIOut_Delayed` port 1 on the capture's channels; keys from the base the instance knows, else C5, else MIDI note 0 from the first note that cannot be read from C5 on; unticking ends what sounds (CC 123). Independent of a take.

## 5. Verification (numbers from runs on this machine; no FL here, no sound card)

### `FruityHost capture-test` (no DLL): 5 of 5 checks, 695 assertions, 13 of 13 deliberate bugs caught (under 2 s)

| Check | What was measured |
|---|---|
| `pitch` | `FlNotes`: 7100 from MIDI note 0 and 1100 from C5 are both key 71, 1100 cents from C5; the scripted song's lanes on channels made in FL read from C5 (the bass and drums sure: from MIDI note 0 they would be below key 0), on imported channels from MIDI note 0 (the pulses sure, the triangle and noise like them, said), a mix (imported pulses, a triangle made in FL: the noise that fits both read from C5 and said as such) |
| `tempo` | the log's 1129579756 = 0x435404EC = 212.0192 = 130 BPM at 44.1 kHz with PPQ 96; FPE_Tempo 141.3 with samples per tick agreeing; samples per tick x PPQ alone (2e-7 relative); nothing (120, said); samples per tick without a readable time signature (PPQ 96 assumed, said); an absurd TTimeSigInfo refused |
| `build` | the scripted FL song (section 6) as lane events exactly as the plugin writes them, built, read back by the independent Python reader: at 48 kHz / 141.3 BPM and at 44.1 kHz / 130 BPM, with and without FPE_Tempo, channels made in FL and imported channels: **39 of 39 notes, start and end within 0 ticks** at 960 per quarter, velocities exact, channels 1/2/3/10, one tempo, FL's 4/4, the end at the bar line (5 bars); **the played pitch at 4,652 of 4,652 FL ticks within the 14-bit step** (worst 0.1641 cents, allowed 0.166 at range 27); ranges 27 / 4 / 3 / 2 = the smallest that hold each part's moves |
| `convert` | the captured file through VRUN's converter (the plugin's copy) = the ideal file written from the script (same notes, no bends): **streams 55 + 28 + 24 + 82 bytes identical byte for byte**, loop 510 frames; the converter counts the 646 bends as not played; the capture's view of the converter's choice = the converter's; a Pulse 2 lower than the triangle is said |
| `lanes` | 0 bytes allocated for 20,000 events; a lane whose worker never comes keeps 32,768 and counts 5,000 lost of 37,768; three one-second blocks at 48, 44.1 and 96 kHz = 3.000000 s; nothing kept before Play or after Stop |

Deliberate bugs (each caught): every channel read from MIDI note 0; C5 always even where the notes only fit MIDI note 0; velocity x 127; no RPN 0; every bend the other way; Pulse 2 on channel 1; notes held over Stop left out; the end at the last note-off; FL's tempo ignored; every 40th pitch event lost silently; slides not captured; an allocation per event; the clock at 44.1 kHz whatever the rate.

### `FruityHost selftest --only capture` (the Native AOT DLL): PASS, 7 scenarios, 15 of 15 DLL deliberate bugs caught (about 2 minutes)

The DLL is played by the **FL-faithful host** (`Capture.FlHost.cs`): the Dispatcher calls of the real log in its order (SetSampleRate, SetBlockSize 512, SetTimeSig with a real `TTimeSigInfo`, SetSamplesPerTick float bits twice, SetNumSends, SetPlaying 0, ProcessMode 3072, ... PluginLoaded; at Play SetSamplesPerTick, SongPosChanged 0, SetPlaying 1, SetSamplesPerTick, optionally FPE_Tempo), the song rendered **one FL tick at a time** (floor((k+1) x samples per tick) - floor(k x samples per tick) samples, NewTick, the releases, the note-ons, every voice's FinalLevels, then Gen_Render, every instance every tick), the pitch above the channel's root (InitLevels = FinalLevels at the note-on, as reload's HostSim does), velocity / 128, slide notes gliding the parent voice, per-note pitch automation, **no song position** (FHD_GetMixingTime leaves the time as it was), Stop = SetPlaying 0 then the held voices released.

| Scenario | Result |
|---|---|
| flow | real mouse messages: Capture from FL beside Open MIDI..., no control overlapping another; ARMED, "4 BrokenNes2 channels listen"; FL plays and stops; "Captured FL capture ...: 39 notes on 4 channels"; the facts on screen (the bends VRUN drops); Test MIDI export plays it at once (the converter's facts: square 1 <- MIDI 1, square 2 <- 2, triangle <- 3, noise <- 10, "646 pitch bends not played"), NES ROM mode, audible; Save MIDI... writes the take's bytes; the MIDI-out box both ways, off by default |
| file | three takes read back by the independent reader: 48 kHz / 141.3 BPM with FPE_Tempo, the user's 44.1 kHz / 130 BPM without it (the tempo from samples per tick x PPQ 96), and imported channels (root MIDI note 0): each **39 of 39 notes within 0 ticks, the pitch FL played at 4,652 of 4,652 ticks within 0.166 cents (worst 0.1641)**, ranges 27/4/3/2; on channels made in FL the period registers the instances' own chips held equal the file's pitch through Direct mode's arithmetic at **2,878 of 2,888 ticks, the other 10 at a rounding edge (within the bend's step), 0 other**; the base said (C5, or "read from MIDI note 0" among the first facts); "FL left the time as it was (no song position)" in the report |
| engine | the take's song: the ROM = the library's for the same bytes; **the plugin's machine plays the converter's streams 5,032 of 5,032 field-frames** from frame 6; the streams = the ideal file's; loop 510 frames = 5 bars; the 646 bends counted as not played |
| render | FL's Export: rendered at 48 kHz on instances made at 44.1 kHz, PM_IsRendering: the same 39 notes within 0 ticks and pitch within 0.166 cents; "captured while FL rendered"; a host that answers FHD_GetMixingTime: "FL's own song position agrees with the capture's clock at 39 notes" |
| alloc | **0 bytes allocated on the audio thread** over a second take of 1,608 FL ticks on 4 channels, 39 notes, slides, MIDI out on |
| live | every note-on as FL played it (keys with Coarse, velocities), every note-off, RPN 0 = 48 before the first note, MIDIOut_Delayed on port 1 channels 1/2/3/10; **the pitch at 4,652 of 4,652 ticks within 0.293 cents** (worst 0.2891); unticked: no note goes out |
| edge | nothing played: "Nothing captured: no BrokenNes2 channel got a note between FL's Play and Stop", no song loaded; cancel: nothing kept, FL's Play records nothing; a channel FL never starts joins at the others' clock (its 16 notes in, the first at tick 0, said); the note held over Stop is in the file ending at the Stop, said; arming again over the take's song puts the emulator back in Direct mode, said |

Deliberate bugs through the DLL (hook 849), each caught by its scenario: flow (built but not handed to Test MIDI export), file (every channel from MIDI note 0; C5 always; velocity x 127; FL's tempo ignored; bends the other way; slides not captured; no RPN 0; every 40th pitch event lost), engine (Pulse 2 on channel 1; the end at the last note), render (the clock at 44.1 kHz), alloc (an allocation per event: 169,120 bytes), live (no bends), edge (notes held over Stop left out).

### The whole self-test and the other suites

`refs/heads/main` did not move during this work (5fcae89, fetched again at the end with `--no-tags`): nothing to merge. DLL and test bench built from this tree into `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r9_capture` (the DLL with `dotnet publish`, Native AOT, as `Plugin/build-dist.ps1` does).

| Run | Result |
|---|---|
| `FruityHost capture-test` | 5 of 5 checks, 695 assertions, 13 of 13 deliberate bugs caught, 0 NOT RUN |
| `FruityHost selftest --only capture` (the final DLL) | PASS: 7 scenarios, 15 of 15 DLL and 13 of 13 library deliberate bugs caught (about 2 minutes) |
| `FruityHost selftest` (all, the DLL one commit before the final one: the last commit only frees a finished take's lanes and changes one text; `capture` and `midi` were rerun on the final DLL: both PASS) | **64 results: 62 pass, 1 warning (QLQ an octave sharp, on purpose, as before), 1 failed: `realtime` 4.9x real time where the floor is 5x.** Not this work: `main`'s own DLL, published from `refs/heads/main` and run interleaved with this one on the same machine at the same time, measured 4.5, 4.7, 4.6, 4.6x against 4.9, 4.9, 4.9, 4.7x for this branch (the machine was 26-35 % busy with other agents; `Plugin/CLAUDE.md` warns that a busy machine cuts the figures). `midi`, `record`, `record-nes`, `relink`, `capture` and `hostlog` (the user's real host log untouched) all PASS in that run |
| `BrokenNes.Workshop --corrupt-test` (fresh build) | 275 passed, 0 failed, 0 not run |
| `UAT\run-suite.ps1 -Exe <fresh Workshop>` | 84 pass, 0 fail, 60 skip (their ROMs are not at this worktree's paths), 0 info |
| `UAT\entrypoints\notices-check.ps1` | pass (488 files scanned; no `*.bn2preset` tracked) |
| The user's `Documents\BrokenNes2 host log.txt` | read once, never written: the test bench's `HostLogGuard` and the `hostlog` check (last of the full run) |

## 6. The scripted FL song (what the checks play)

Four BrokenNes2 channels, 141.3 BPM (or 130), PPQ 96, 4/4, Stop at tick 1,600 (16.7 beats): **Pulse 1** (12 notes: plain; fine pitch +30 and -30; a slide up 3 semitones; vibrato by per-note automation +-40 cents; two overlapping notes; the same key twice in a row; a slide chain; a slide down an octave; a 26-semitone slide; a note held over Stop), **Pulse 2** with Coarse +12 (5 notes, a slide down), **Triangle** with Fine +7 (6 notes, a slide up), **Noise** (16 hits, two keys, two velocities). 39 notes, 646 bend events.

## 7. Not verifiable without FL (said plainly)

- **FL itself.** Everything above is the stand-in host doing what the log shows FL doing. Whether FL renders tick by tick for a generator (the capture follows whatever blocks FL renders; the tests show it exact when FL renders by ticks), what FL sends during File > Export > WAV (SetPlaying? another rate? PM_IsRendering?), and whether FL calls every generator every tick (Smart disable would make a lane's clock lag: said when it happens) are not known.
- **FHD_GetMixingTime asked with offset 0**: whether FL then gives the song position. The capture asks at every note and the window / host log say what FL answered against the capture's clock; nothing depends on it.
- **The root note**: a channel whose root note the user set to something else than C5 (or an imported channel at MIDI note 0 whose notes fit both readings when no other channel is sure) lands in another octave; the facts say which base each channel was read from.
- **The MIDI out**: whether FL routes a generator's `MIDIOut_Delayed` to channels with that MIDI input port, and whether FL's own MIDI export includes plugin MIDI out (the SDK's `FPF_MIDIOut` comment suggests it). The user's step 4g.7 asks.
- **FL's import of the file**: FL users report its MIDI import reads bends at 2 semitones whatever the RPN says (round 8's record); the file is right by the SMF spec; other programs read the range.
- **Hearing any of it.**

## 8. Decisions taken here (and for the user)

Taken: the capture reads what the instance PLAYS (FinalLevels + Coarse / Fine), not only the piano roll; one track per instance on the converter's channels for its role; the bend follows the newest note (as the channel plays); the smallest bend range per track; real times with one tempo (FL's at Play; later changes said: VRUN honours the first only); the end at the bar line after the last note; notes held over Stop end there; the captured song goes to the emulator that armed the take and plays at once; arming again over it goes back to Direct mode; the live MIDI out at range 48, port 1, off by default; the base key C5 by default, MIDI note 0 only when the notes show it.

For the user: whether the loop should end at the bar after the last note (now) or where FL was stopped; whether to add a pitch-bend command to VRUN's engine (the file has the bends: the converter would need to keep them); the live MIDI out's port and range.

## 9. Ids, hooks, places this took

| What | Taken |
|---|---|
| Control ids | 2120-2139 (2120 Capture from FL, 2121 Save MIDI..., 2122 the MIDI-out box) |
| Plugin test hooks | 840-859 (840-853 used; 790-819 are reload's) |
| Editor test hooks | 860-869 (860-863 used) |
| Deliberate bugs | `CaptureMutants` 1-15 (plugin hook 849) |
| Prefs | `CaptureMidiOut` |
| Files | `%TEMP%\BrokenNes2\Captures\FL capture HH.mm.ss.mid` |
| The window | Test MIDI export grew from 590 to 650 pixels high (the capture row and strip) |
| Shared file | `Record/FlNotes.cs` (reload's, identical bytes) |

## 10. Merging with `reload` (when it is in main)

A dry merge of `r8-reload` (6180510) into this branch conflicts only in `Bn2Plugin.cs`: the Voice class's fields (keep both sets) and TriggerVoice (keep reload's `OnNote` line and add `v.CapOff = true; v.LiveSent = false;` and the `CaptureNoteOn` call). `FlNotes.cs` is the same file. After the merge: `CaptureBaseHint` should return reload's `pitchBase` when the instance knows its base (a track found or given), and the FL-faithful host can use reload's `HostSim.RootNote` / `FlNoteOn`.

**Done (2026-10-18, `reload` merged main 4cda3b2 into `r8-reload`):** the conflicts were exactly the three expected places (Voice fields: both sets kept; TriggerVoice: reload's `OnNote` first, then `CaptureNoteOn`, so a base found on that very note is already the hint; the test hook routing: 790-819 and 840-859 both), plus `SelfTest.cs` (both checks registered) and the hand-off guide (reload's 4e/4f, capture's 4g). `CaptureBaseHint` is `pitchBaseKnown ? pitchBase : -1` (`Bn2Plugin.Tracks.cs`: known when every hypothesis of the matcher agreed on the base, when a followed track gave it, or when a project's `TRK1` v3 restored it; forgotten with the track). `Capture.FlHost.cs` keeps its own root (`FlSong.Root`); it was not switched to `HostSim.RootNote`, the two agree (FL's pitch above the channel's root).
