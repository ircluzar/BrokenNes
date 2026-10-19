# Round 9 `bends`: VRUN's sound engine plays pitch bends, and Test MIDI export plays them

Date: 2026-10-18. Agent `vrun-bends`. VRUN branch `audio-bends` (VRUN repo, from `6c561f6`; worktree `%LOCALAPPDATA%\VRUN_Nes_Dev_work\vrun-bends\VRUN`), BrokenNes branch `vrun-bends` (from `refs/heads/main` `5fcae89`; worktree `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r9_bends\wt`). User-facing: `Plugin/README.md` (Test MIDI export, "Pitch bends"); VRUN side: `engine/audio/README.md` ("Pitch bends") in VRUN; handoff: `Plugin/CLAUDE.md`. Builds on `docs/projects/round7-midi.md`.

## 1. What the user asked for, and decided

"Add bends to VRUN's engine": slides and bends composed in FL Studio should play in the game exactly as BrokenNes2's Test MIDI export previews them. The game ROM's fixed bank is at its ceiling, so the cost was measured and shown before anything goes into the game build (phase 1, VRUN side, reported 2026-10-18; the game part waits for the user's decisions). Phase 2, this record's BrokenNes side: the converter copied again, the template rebuilt with the bend decoders, the frame-exact model and the checks extended, the facts say what the bends did. Standard MIDI pitch bend with RPN 0 (2 semitones when absent) so any FL or MIDI source works. The `capture` track (BrokenNes2 writes what FL plays as MIDI with bends + RPN 0 ranges) is concurrent; its record will be `docs/projects/round9-capture.md`.

## 2. The engine (VRUN side, phase 1)

| Piece | What |
|---|---|
| Opcodes | `$D1 lo hi` pitch: play period hi:lo now (a note-on at an exact period). `$D2 lo hi` slide: add hi.lo (signed 8.8, +-128 period units a frame) to the period every frame from the next one. One frame of the channel's one-opcode budget each; a note-on and the loop stop a slide. |
| Converter (`Bends.cs`) | Opt-in per song (`ConvertOptions.Bends`, CLI `--bends`, roster `"bends": true`). The note stream is built and retimed as before; bend opcodes go only into frames where the decoder idles (waits are split): **notes never move**, a song without bends gets the old bytes, bends are what is thinned. Held bends land exactly on floor(exact period) (the note table's rule: +2 held = the note 2 up); moving bends within 10 cents or half a frame of their own motion. Over 1,280 bytes: coarser tolerance, then held pitches only, then none (never fewer notes). The noise's bends are counted and ignored. Everything is logged and in `SongResult.Bends`. |
| Decoders | Bend variants of square 1, square 2 (plain and SFX-resync) and triangle, picked per channel from the converted streams (`"bend_decoders": "all"` for a template). 16.8 period + 8.8 slide in RAM (5 bytes a channel), the slide stepped before the wait check (low register every frame, high only when it changes: a pulse's high write restarts its waveform), cleared on a frame whose pointer is 0. |
| VRUN proofs | `converter/tests/run-identity.sh`: 1,511 files (11 roster + 1,500 random) byte-identical to `6c561f6` without `--bends`; with it, no file changes without a bend channel, and the 705 that change keep every note (volume, gate, noise equal over two loops). `convertertests bends`: 12 synthetic songs through a frame-exact model against the MIDI's own pitch curve, 4 broken-model controls caught. `tests/bend_rom_check.sh`: 6 one-song AudioPlayers on BrokenNes, the decoders' own APU writes (rebuilt from `--instr-trace`) equal the model in every NMI over two loops. |
| Cost (measured) | CPU +50 to +190 cycles a frame per bending channel (all three: 552 average / 1,194 max vs 216 / 618). ROM: a bend decoder is 190-210 bytes bigger, in its song's bank. Game: roster as committed = zero cost (identical generated files); bends on its 3 bending songs = builds, fixed bank 31 unchanged (1,777 free), -2.5 KB switchable, +15 B RAM; worst case (all 11 songs, bend decoders everywhere) builds, bank 31 unchanged, -9.4 KB switchable. |

## 3. What changed in BrokenNes

| Piece | Where | What |
|---|---|---|
| The converter | `Plugin/BrokenNes2.Plugin/Music/Vrun/` | Copied again byte for byte from VRUN's `audio-bends` (four files; `Bends.cs` new); `SOURCE.txt` hashes. |
| The conversion | `Music/SongConvert.cs` | Every song converted with bends on (a no-op for a song without bends: checked). `SongFacts.Bends` = the converter's reports; `BendLine()`; the window keeps six lines (with a thinning line and a bends line the tempo goes on the loop's line). Hook 615 indexes 14-17. |
| The template | `Resources/musictest.*`, `BrokenDev/tools/music/build-musictest.ps1` | Version 2: `opcodes = vrun-music-2`, `bend-decoders = square1 square2 triangle`; the roster track asks `"bends": true, "bend_decoders": "all"` and the script checks the generated decoders. The stream regions moved (square 1 `0x07868A`, square 2 `0x078B8A`, triangle `0x07908A`, noise `0x07958A`; title and loop bytes unchanged). `MusicTemplate` refuses version 1 (its plain decoders read a bend opcode as the loop) and a template without the three bend decoders. |
| Registers per NMI | `Music/NmiLatch.cs`, `MidiRun` (FruityHost), hook 611 bit 30 | See section 4. |
| The model | `Plugin/BrokenNes.FruityHost/Midi.Model.cs` | VRUN's bend decoders modelled register by register (16.8 period, 8.8 slide, step before the wait, clear at pointer 0, high register only when it changes); `flipSlides` for a negative control. |
| The checks | `Midi.Checks.cs`, `Midi.Fixtures.cs`, `SelfTest.Midi.cs` | New check `bends`, new fixtures `BendSongs()` (glides, octave slides, vibrato, bent notes + a held bend across notes, all three channels + drums, dense), `converter` with `--bends`, `engine` with a fourth negative control, `facts` and `template-refusals` extended, the DLL scenario `bends`; deliberate bugs `MusicMutate` 10-13. |

How the window decides bends for a dropped (or captured) MIDI: **always on**. The converter with bends on gives a song without bends exactly its old bytes (the `bends` check holds it on every song), so "on whenever the file has bends" needs no switch; the facts line shows the converter's report. VRUN's game build plays a song's bends only with `"bends": true` in its roster entry: the README says so.

## 4. Found on the way: a busy frame's NMI runs past the end of the emulator's frame

The AudioPlayer's NMI starts at vblank, near the end of `RunFrame` (which returns at the end of the last vblank line). With round 7's plain decoders the music was always written before that; with the bend decoders on three channels the decoders cost more, and on a busy frame (all channels decode; once a second the player also redraws its status line) the NMI is still writing the last channels (the noise, sometimes the triangle) when `RunFrame` returns. A latch read there (round 7's sample point, and the Workshop `--trace`) shows those registers one frame late. Measured in the `engine` check: 100 frames in all over 22 songs (about 45,000 frames), 0 to 17 a song; the Workshop instruction trace shows the CPU inside the decoder at the sample point, and the value one frame later. The values are right, the sample point is not: `Music/NmiLatch.cs` rebuilds the registers each NMI left from the APU write tap (a write in the first half of a frame belongs to the previous NMI), and every check that compares registers with a model or another ROM uses it. On the real machine and in the plugin's audio this is a write about 0.5 to 1 ms later in the frame on those frames; the model is frame-level and unaffected.

The same lag touches the player's position and loop counter (RAM, read when the frame ends; `pl_audio_post` updates them after the decoders): the `reference` check allows a sample one frame late there and counts it; any other difference fails.

## 5. Verification

| Run | Result |
|---|---|
| `FruityHost midi-test` (no DLL, the VRUN tree present) | 10 checks pass; 11 deliberate bugs caught. `engine` 22/22 songs exactly as the converter's streams (100 frames in all whose NMI wrote past the frame's end, compared as each NMI left them); `reference` 20/20 songs: the full rebuild and the patched template write the same registers per NMI (77 frames where a player position was sampled one step late, counted); `bends`: every song without a played bend byte for byte as without bends, the bend songs' pitch in the ROM within the converter's own count (0 off on the written fixtures), no note moved |
| `FruityHost capture-test` (no DLL) | 5 checks, 708 assertions pass; 15 deliberate bugs caught (13 capture + 2 of Test MIDI export's: bends off, slides negated). `convert`, the scripted take: streams 279 + 40 + 53 + 82 bytes with bends (55 + 28 + 24 + 82 without = the ideal file's), loop 510 frames, 646 bends; "bends played on square 1, square 2, triangle: 19 slides + 66 pitch steps (+265 bytes), held exact, moving within 29 cents"; the ROM over 1,020 frames = the decoder model (8,152 field-frames), no note moved; triangle 744 frames judged (706 held, 0.0 cents from the NES period; moving max 9.6), square 1 560 (moving max 29.0: the 26-semitone slide, half a frame of its speed), square 2 676; 0 frames off on each, against 736 / 330 / 38 off without bends |
| A project saved by `main`'s plugin (round 7's template) reopened by this one (`FruityHost song-compat save <old dll> bendall s.state`, then `song-compat open <new dll> s.state`: two processes) | bendall and manychannels: the song is converted again from the MIDI the project keeps, it is the game and sounds (114 of 120 frames), its bends play now ("bends played on square 1, square 2, triangle: ..." where round 7 said "440 pitch bends not played"); the saved machine state belongs to the old ROM, so the plugin's own message says the song starts from its beginning ("made with another build of the MIDI test's player"); saved and reopened by this plugin, the state comes back (restore 1, frame 270) |
| `selftest --only midi,capture` (Native AOT DLL) | both pass; midi 9 of 9 DLL bugs caught (the `refusals` scenario now writes the next template version and round 7's version 1, both refused); capture 15 + 15 bugs caught; `engine` after a take by the FL-faithful host: the plugin's own machine 5,032/5,032 field-frames per NMI, triangle 579 / square 1 271 / square 2 38 bent frames, 0 off |

## 6. Not tested (said plainly)

- **Nobody listened.** No sound card on the build machine: every claim is a register trace, a model or a count.
- **Real FL Studio:** its MIDI export's bend resolution and range (RPN 0 or not), FL's slide notes as exported, and a take of Capture from FL in real FL (the FL-faithful host's take is checked end to end: FL's pitch -> the file -> the converter -> the plugin's machine).
- **The game's own build with bends** (VRUN side): see section 8 (built and run in a test copy, the CPU measured; nothing goes into the user's game until the user says so).
- **Square 2's bend decoder while a sound effect borrows the channel**: it ran in the test game during combat (section 8: CPU only); its registers after a sound effect were not compared with a model.

## 7. Decisions taken here

- Bends always on in the plugin (no switch): a no-op without bends, proven per song.
- The template carries the bend decoders on all three tonal channels (any song can be patched in), format version 2; version 1 refused.
- Registers compared per NMI; the player's RAM position allowed a one-frame sample lag, counted.
- No new hook number (the `capture` track works in the same window): hook 611 gained a flag bit, hook 615 indexes 14-17.
