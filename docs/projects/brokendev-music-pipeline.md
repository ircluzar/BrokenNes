# BrokenDev music pipeline: from FL Studio to a ROM, a preview, and an export (design)

Status: **design**, written 2026-10-16 by track `bd` of round 5. Nothing in the plugin changes in this document. What is *measured* is
marked as measured and comes from `BrokenDev/tools/music-replay-check.ps1` (section 7); everything else is a plan or a guess and says so.

## 1. What the user asked for

> compose a song in FL Studio, then have the plugin, somehow, turn the FL Studio data into a ROM and play it in the emulator so that we can
> preview if it replays correctly, before exporting it to a file and then importing it to a game during dev.

In one paragraph: the song stays a **MIDI file** from FL to the game. Between them sits VRUN's own converter, which is the only thing that
knows how a song becomes the four byte streams the game's sound engine plays. A *preview* is that same engine running in a small ROM on a
BrokenNes core, fed with the same streams, so what is heard is what the game will play. The *export* is the MIDI file (plus a report of what
the converter did to it) that the game's build already consumes. The step the plugin does not have yet is "FL notes in, streams into the
ROM": the plugin's ROM mode today plays whatever the ROM plays by itself and ignores FL's notes (`Plugin/README.md`: "FL's notes do **not**
drive the game yet").

## 2. What exists today

| Piece | Where | State |
|---|---|---|
| Converter, MIDI to four opcode streams | VRUN `engine/audio/converter` (`Vrun.Audio.Converter`: `MidiFile`, `Midi2Nes`, `FabWriter`; CLI `midi2nes`) | **Done** (spec 26, milestone M0). .NET 10, AOT compatible, no reflection. Byte-identical to the Python original on 1,674 MIDI files (11 real songs, 150 overloaded, 1,500 synthetic), per the spec. |
| Opcode format and the per-frame decoders | `engine/audio/gen_music_decoders.py` (templates), `audio_engine.fab` | Done, in use by the game and the AudioPlayer ROM. One opcode per channel per frame at most (`AUDIO_LOOP_CAP = 1`). |
| AudioPlayer ROM | `./build.sh audioplayer`, `src/audioplayer` | Done. Roster in `roster.json`; builds in about 2 s once the toolchain is warm (measured, section 7). |
| Live audio bridge | VRUN `docs/26-live-audio-bridge-spec.md` | **Specification only** apart from M0. M1 (live ROM with a mailbox), M2 to M5 (plugin side) are not built. |
| BrokenNes2 plugin | `Plugin/BrokenNes2.Plugin` (native Fruity plugin, Native AOT) | Direct mode (FL's notes play a bare chip) and ROM mode (a game runs; per-channel stems through `ApuWriteTap`; the whole machine saved in the project as `GAM1`). **Song input since round 7:** "Test MIDI export" (path B, template + patch; `docs/projects/round7-midi.md`). |
| PRG poke and write tap | `Core/NesAudioMachine.cs` (`PokePrg`), `Windows/NesEmulator/board/Cartridge.cs` (`PokePrg(index, value)`), `Bus.cs`/`NES.cs` (`ApuWriteTap`) | Exist. `PokePrg` already serves the desktop's corruption feature. |
| SNES side | SuperVRUN `tools/music/sf2it.py` + `MNES.sf2` -> Impulse Tracker modules -> PVSnesLib soundbank -> SPC700 driver | A separate lane with its own converter. Its own record says "Lab vs Mesen: 89,475/89,475 DSP writes identical": the same idea as section 6, on the DSP. |
| BrokenDev music tools | `BrokenDev/tools/music-replay-check.ps1`, `tools/music/*.py` | **New, measured**: Phase 0 and the template fast path (sections 5 and 7). |

## 3. The data formats, and which one to use

Five candidates, judged on: is it what the game consumes, can FL produce it, can it be verified, how much new machinery it costs.

| # | Format | Used for | Verdict |
|---|---|---|---|
| A | **Standard MIDI File** (`.mid`) | FL's "export MIDI"; the converter's input; the game build's input (`roster.json` `midi`) | **The interchange format.** FL makes it, the converter reads it, the game build reads it. A capture pass in the plugin writes the same thing (spec 5.2). No new format. |
| B | **VRUN opcode streams** (four byte arrays, at most 1280 bytes each, `00-7F` note on, `80` off, `81-BF` wait, `C0-CF` volume, `D0 xx` long wait, `FF` loop) | What the ROM plays | **Generated, never authored.** Produced by the converter from A. They exist as `music_data.fab` text in the build, and as bytes in the ROM's PRG. |
| C | A neutral song container (JSON or binary: frame-stamped events per abstract channel, tempo map, loop) | Would let several targets (NES, SNES, Sega) share one capture | **Not now.** It would be MIDI again with another name. Reopen it only when a target needs something SMF cannot say (per-note pitch curves, for example, which FL has and the NES engine cannot play anyway). |
| D | **APU register-write log** (frame or cycle, register, value) | What any engine boils down to | **The verification currency, not an export.** Cannot be edited back into music. Used to prove that a replay ROM does what the streams say (section 6). |
| E | **NSF** (a 6502 driver plus data) | Plays in third-party NES players | **Not wanted.** The game does not use it, the driver would not be VRUN's engine, and it would verify nothing about the game. Reopen only if the user wants to share a song outside BrokenNes. |

So: **A in, B inside, D to check, A out.**

## 4. The pipeline

```
FL Studio                  BrokenNes2 plugin (later)               VRUN converter (C#, shared)            replay ROM on a BrokenNes core
 notes, tempo   --notes-->  capture pass --> song.mid  --------->   Midi2Nes.ConvertSong  --> 4 streams --> PRG (template) or build   --> sound
 or "export MIDI" ---------------------> song.mid  ------------->  + report (channel map, dropped, bytes)       |                         |
                                                                                                         stems per channel          D: APU state per frame
                                                                                                                                      compared with the decoder model
                                  export: song.mid + report  ----->  game project: audiotest/, roster.json, ./build.sh game
```

Where each stage runs, and what is needed at the user's machine:

| Stage | Runs in | Needs |
|---|---|---|
| Capture or MIDI export | FL Studio + plugin | the plugin (capture) or nothing (export) |
| Convert | the plugin (library) or `midi2nes` (CLI) | .NET 10; the library is AOT compatible, so no runtime is needed inside the plugin |
| Put the streams into a ROM | Phase 0: the VRUN build (Git Bash + NESFab + dotnet). Phase 1+: `PokePrg` on a template ROM | Phase 1 needs no build tools at all |
| Play | a BrokenNes core (FIX family by default) | nothing beyond the plugin or the desktop app |
| Verify | `decoder_model.py` over a Workshop `--trace` | Python (tooling only, not part of the plugin) |

## 5. Three ways to get a ROM that plays the song

| | A. Rebuild | B. Template + patch | C. Live ROM (spec 26) |
|---|---|---|---|
| Idea | Throwaway one-song AudioPlayer project, built by `build.sh` | One prebuilt ROM whose four stream arrays have a fixed size (1280 bytes, the converter's maximum); write the new streams over them by PRG offset | Mailbox in RAM: the plugin pushes note events frame by frame; an "Exact" region in PRG for whole songs |
| Exact (what the game plays) | Yes | Yes | Only in its Exact mode; Live mode is an approximation by design |
| Latency from a change | about 2 s build (measured) + load | a few milliseconds of pokes + a reset (not measured in a plugin; the patch itself is a file write in the measurement) | one NES frame (spec) |
| Build tools at run time | Git Bash, NESFab, dotnet | none | none |
| New ROM code | none | none (the template is a normal build of the AudioPlayer with padded arrays) | yes (spec 26 M1) |
| Status | **Measured**, works today (`music-replay-check.ps1`) | **Measured** with the patch applied to the ROM file (`-Template`); the plugin-side poke is not written or tested | Not built |
| Right for | the regression truth; CI | the plugin's first "preview this MIDI" | composing note by note, hearing each key at once |

The recommendation is **B first, A as the oracle, C later if the user wants it**. B gives the thing the user described (hear exactly what
the game will play, right after dropping or exporting a MIDI) without new ROM code, and A guards it: every release can rebuild the same
songs the slow way and check they agree.

How B works, as measured: the template is built once from a one-song roster whose `music_data.fab` arrays are padded to 1280 bytes
(`tools/music/pad_streams.py`; each channel's filler differs, so NESFab cannot merge two arrays). The ROM's `.mlb` names the array
offsets (`NesPrgRom:078433-078932:music_song_square1@30_0:` and so on). `tools/music/patch_streams.py` writes a song's streams at those
offsets. The decoders are the engine's own and read the arrays through the same pointer, so the patched ROM plays the new song and loops
at the song's own `FF` opcode. The ROM boots straight into its track (measured: the decode begins at trace frame 6 with no input).

## 6. How a preview is verified

A preview is only useful if "plays correctly" means something. The layers, each with its own check:

| Claim | Check | Status |
|---|---|---|
| The converter turns MIDI into the right streams | Spec 26: byte-identical against the Python original on 1,674 files; `music_data.fab` regenerates byte-identical | Done in VRUN's repository; not re-run here |
| The ROM plays the streams it was given, frame for frame, loop point included | `decoder_model.py`: a 60-line model of the four decoder templates predicts, for every frame, the period and volume of both pulses, the triangle period and linear counter, and the noise period and volume; the Workshop `--trace` of the ROM on BrokenNes (CPU/PPU/APU FIX) is compared with it | **Measured**, section 7 |
| Patching a template gives the same ROM behaviour as a full build | the same model, on the patched ROM's trace | **Measured**, section 7 |
| The model itself is right | negative controls (below), and a hand-worked case in `BrokenDev/tests/run-tests.ps1` | Measured |
| The sound is good | peak and RMS only (0.436 and 0.089 for the synthetic song on the FIX cores). **Nobody has listened.** | Not verified; the user must listen |
| FL's captured notes equal FL's exported MIDI | spec 26 open question 1 (end-of-track position, channel numbering, resolution) | **Not measured** |
| A song imported into the real game plays like the preview | the game's Options > Sound Test is the same player screen; the model can be run on a game trace from there (no SFX borrowing the channels) | Not built |

Negative controls, run every time by `music-replay-check.ps1`; each must make the check fail or the script itself fails: the model's
first 8 square-1 notes one semitone off; the model's decode start one frame out of step; a different song's streams. The tool also keeps a
self-test of its own (4 cases in `BrokenDev/tests/run-tests.ps1`: a hand-made trace matches, one wrong period fails, one frame out of step
fails, a trace without a columns header is refused).

## 7. What was measured (2026-10-16, BrokenNes FIX cores, Workshop `--trace`)

* **Full build, 12 songs**: the 11 songs of VRUN's roster (`think`, `boss`, `baropen`, `baropencrpt`, `chicagovoices`, `puzzle`, `rosbif`,
  `satnkrt`, `sundaydrive`, `vectorplane`, `level2`) and one synthetic song written like an FL export (a lead on channel 1, a bass on
  channel 2, drums on channel 10, 16 beats at 120 BPM; the converter put it on square 1, triangle and noise, loop 480 frames, 121 bytes).
  Each was built as a one-song AudioPlayer in 2.0 to 2.5 s, run on BrokenNes, and compared with the model over 2,400 frames (560 for the
  synthetic one): **19,192 of 19,192** channel-field-frames agree for every real song (4,472 of 4,472 for the synthetic), from the second
  frame of the decode on. The first frame is not compared: the ROM starts the track in it and its start-up writes straddle the trace's
  sample point (the tool says so in its header). The triangle linear counter is compared with one frame of slack, because it only reloads
  on the next quarter-frame clock; the pitch is compared exactly. The window held a whole loop for the synthetic song (480 frames),
  `satnkrt` (855) and `vectorplane` (1,733), so the wrap is covered for those three.
* **Template and patch, the same 11 real songs**: 19,192 of 19,192 each against the song's own streams; against the template's own
  song the same trace scores 0.33 (0.327 and 0.331 where the figure was read; the negative control failed as required in 11 of 11).
* **Audio** of the synthetic song on the FIX cores, 600 frames: 440,274 samples at 44.1 kHz, peak 0.436, RMS 0.089.
* **What this does not show**: how the song sounds; that FL's capture equals FL's export; anything about the plugin (it was not involved);
  the SNES lane; a poke on a running machine (the patch was written to the ROM file, which is what `PokePrg` does to the PRG array, but
  `PokePrg` itself was not exercised).

Reproduce: `BrokenDev\tools\music-replay-check.ps1 -Mid <song.mid> -Name mysong -Frames 2400 -Template` (needs the restored
`vrun_nes_dev` and the Workshop; leaves `out\music-replay\<name>\result.json`, the ROM and the traces).

## 8. Export and import into a game

* **What is exported**: the `.mid` (the game's input) and the converter's report (`build_report.json` format: channel map, notes dropped,
  loop length, bytes per channel, truncation). Not the streams, not a ROM: the game's build regenerates the streams from the MIDI, so the
  MIDI is the single source and the report says what the build will do to it.
* **What the game needs** (from `engine/audio/README.md`): the MIDI under `src/audiotest/` (or any path the roster names), an entry in
  the project's `roster.json` (`name` lowercase, `title` up to 24 characters of the player font, `midi`, optional `resync`), then
  `./build.sh game`. `src/game/music_data.fab` is committed, so a change to it is a diff to review.
* **A BrokenDev verb for it** (not written): `brokendev.ps1 music import <mid> -Project vrun-nes -Name x -Title "X"` would copy the file, add
  the roster entry, rebuild, and run the model check on the result.
* **Limits the preview must show before the user commits to a song**: four channels; one opcode per channel per frame (denser music is
  thinned, and the report counts it); at most 1280 bytes per channel (longer is truncated); the loop is the whole song's length and only the
  first tempo counts; and the **ROM budget**: VRUN's game ROM is at its ceiling (fixed bank 31 has no slack; `vrun-nes` known issues), so a
  new song may not fit at all. The AudioPlayer and the template have room; the game may not. The report's bytes-per-channel is the number to
  watch.

## 9. What the BrokenNes2 plugin would add (a plan, not code)

1. **A song source in ROM mode**: a dropped `.mid`, or the FL pattern captured while the transport plays. The converter library is referenced
   directly (it is AOT compatible); the song's bytes are kept in the project in a new section next to `GAM1` (suggested tag `SNG1`: the
   `.mid` bytes or the captured events), so a reopened project replays the same song.
2. **The template ROM** shipped like `Resources/default.nes`, with its symbol table (the `.mlb` offsets) beside it and a signature/version
   so the plugin refuses a template whose opcode format it does not know.
3. **Poke and reset**: `NesGame` (in `Game.cs`) exposes `PokePrg` (it exists on `NesAudioMachine`), writes the four streams, resets the console, plays.
   The four NES channels then arrive as the plugin's existing per-channel stems, one FL instance each, so the user can mix them in FL.
4. **Transport**: first version restarts the ROM when FL's transport starts; the second seeks (spec 5.2: simulate the four streams to frame F
   in C#, then restore the pointers). Following FL's tempo is not possible and not wanted: the game ignores tempo changes after the first.
5. **UI**: a Re-convert button and the converter's report (spec 9), the bytes-per-channel line of section 8.
6. **Live mode** (spec 26 M1, M3) only if composing note by note through the engine matters; it needs the live ROM and a `ChannelTranslator`
   in the converter library.

## 10. Milestones

| # | Milestone | Done when | State |
|---|---|---|---|
| P0 | `music-replay-check.ps1`: MIDI to ROM to BrokenNes to model | 12 songs agree; three negative controls fail | **Done** |
| P0b | Template ROM + patch, offline | 11 of 11 songs agree with the full build's model | **Done** |
| P1 | Desktop: BrokenDev menu "Preview a MIDI file..." (convert, patch the template with `PokePrg`, reset, play) | the same model check passes on a trace taken from the live machine | Not started |
| P2 | Plugin: song source + stems + restart on transport | a project with a dropped MIDI reopens and replays; FruityHost selftest gains a check | **Done** in round 7 (`midi`): "Test MIDI export", see `docs/projects/round7-midi.md` |
| P3 | Plugin: capture pass; measure capture against FL's export (spec question 1) | a stated list of differences, measured in FL | Not started; needs FL |
| P4 | Plugin: seek | model check from a non-zero start | Not started |
| P5 | `music import` verb; Sound Test check in the real game | the model check passes on the game ROM's trace | Not started |
| P6 | Live mode (spec M1, M3) | spec 26 tests | Not started; optional |
| P7 | SNES lane (modules and the SPC700) | DSP-write equality with Mesen, as the SuperVRUN lab already does | Not started |

I give no durations: nothing here is estimated by measurement.

## 11. Risks and open questions

* **Capture is not export.** What a plugin sees as voices (note on/off with velocity, slides, pitch bends) may not equal FL's exported
  MIDI (tick order, channel numbers, the end-of-track position that sets the loop length). Until P3 the dropped `.mid` is the truth.
* **Shared code across repositories.** The converter lives in VRUN's repository; the plugin would need it. Options: a copy in BrokenNes with
  a test that regenerates `music_data.fab` byte-identical, or a reference to the VRUN project. A silent drift would break "same as the game".
* **The template and the engine must agree.** If VRUN changes its opcode format or decoder templates, the shipped template and the
  plugin's converter must change together; the signature check in section 9 item 2 is the guard.
* **ROM budget** of the real game (section 8).
* **Sound accuracy of the cores.** The preview is only as true as the APU core. This round fixed five inaccuracies in `APU_FIX` found on
  the SMB1 remake against Mesen (see `brokendev-report.md`); the frame-level behaviour of the decoders is what section 6 checks, not the
  analogue sound.
* **Loop and the plugin's one-instance model.** One emulator serves several FL instances; the song belongs to the emulator, not to an
  instance (spec open question 2).
* **FL's loop markers** against the song's own loop (spec open question 3).
* **Nobody has listened** to any of the previews. The user should, on a song they know, before anything is built on this.

## 12. Decisions to ask the user

1. Is the NES lane first, with the SNES lane (modules) later? (Recommended: yes.)
2. Is "exact preview of a dropped or exported MIDI" (B) enough for the first plugin version, with note-by-note live mode (C) postponed?
3. May BrokenNes ship a template ROM built from VRUN's engine (it is the user's own code), and may the converter be copied into BrokenNes with a
   byte-identity test?
4. Should the real game's Sound Test be the place where an imported song is verified?
