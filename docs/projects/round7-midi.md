# Round 7 `midi`: Test MIDI export (an FL song replayed by VRUN's own sound engine, inside the plugin)

Record of the `midi` track, 2026-10-17. Design it builds on: `docs/projects/brokendev-music-pipeline.md` (sections 5 to 9; this is milestone P2, path B "template + patch"). User-facing description: `Plugin/README.md` ("Test MIDI export"); maintenance notes: `Plugin/CLAUDE.md`; the user's steps: `docs/handoff-testing-guide.md` step 4d.

## 1. What the user asked for, and decided

Compose in FL Studio, export a MIDI file, and in the BrokenNes2 plugin press **Test MIDI export**: the MIDI is compiled into VRUN's sound-test player and replayed through the plugin at once, so the user can certify by ear that the composition runs right in VRUN's sound engine. Decisions (interview, 2026-10-17): template + patch (no build tools in FL; a full rebuild stays the reference in the tests only); VRUN's C# converter copied into BrokenNes with a byte-identity test against VRUN's own; the template built from the user's AudioPlayer and bundled like `default.nes`, with offsets and a format signature/version; a script that rebuilds it and a test that the bundled one equals what the script makes; everyone sees the button; "certify" means the user listens: no automated A/B report, only the short facts that explain a difference.

## 2. What exists

| Piece | Where | What it does |
|---|---|---|
| The converter | `Plugin/BrokenNes2.Plugin/Music/Vrun/` (`MidiFile.cs`, `Midi2Nes.cs`, `FabWriter.cs`, `SOURCE.txt`) | VRUN's `engine/audio/converter`, copied byte for byte (namespace `Vrun.Audio` kept, never edited here). Native AOT clean (no new trim warning). |
| The template | `Plugin/BrokenNes2.Plugin/Resources/musictest.nes` (524,304 bytes, embedded), `musictest.txt` (manifest, embedded), `musictest.mlb` (labels, for the tests) | VRUN's AudioPlayer (`src/audioplayer`: `main.fab`, the shared engine and player screen) with a one-song roster (BrokenDev's synthetic FL-style song) whose four stream arrays are padded to 1,280 bytes (`pad_streams.py`), a 24-character title and a sentinel loop length. Regions at PRG `0x078433`, `0x078933`, `0x078E33`, `0x079333` (as BrokenDev measured); title at `0x01C079` (25 bytes, the AudioPlayer charmap: ASCII - 32, end mark 95); loop length low byte at `0x03C04D`, `0x058099`, high byte at `0x03C051`, `0x05809E`. |
| The script | `BrokenDev/tools/music/build-musictest.ps1` (+ `set_loop_frames.py`) | Rebuilds the template from the VRUN tree in a scratch copy (the VRUN tree is only read): copies `build.sh`, `engine/audio`, `src/audioplayer`, the font, NESFab's compiler and `lib`; writes the one-song project; runs VRUN's own `build.sh` (its prebuild builds VRUN's converter, runs it, generates the decoders); builds three times with three loop-length sentinels (`0x6D29`, `0x92D6`, `0x5AA5`) and keeps the bytes that hold the three sentinels' bytes; writes the manifest. `-Mode song -Mid x.mid` is the full one-song rebuild (the reference). |
| Pure library | `Music/MusicTemplate.cs`, `Music/SongConvert.cs`, `Music/SongSection.cs` | The manifest reader (refuses another format, version, opcode format, region size, a ROM whose SHA-256 is not the manifest's, a non-iNES or non-mapper-30 ROM, another period table, offsets that do not point at a stream + filler, a title or loop byte not where it says), `Patch` (the four streams, the rest of each region filled, the title, the loop length), the conversion and its facts, the `SNG1` section (+ CRC-32). Linked into FruityHost. |
| The plugin | `Emulator.Song.cs`, `Bn2Editor.Song.cs`, `Bn2Plugin.Song.cs`, small hooks in `Emulator.cs`, `Emulator.Rom.cs`, `Emulator.State.cs`, `Game.cs`, `Bn2Plugin.cs`, `Bn2Editor.cs` | The header button and the window; conversion on a worker; the song as the emulator's NES game in ROM mode (`NesGame { IsSongTest }`), a note when it replaced something; Play / Restart / Stop and FL's transport (flags; a restart swaps in a prepared copy between frames); Reload; `SNG1` in the project; the Game list lists the song. |
| The checks | `Plugin/BrokenNes.FruityHost/Midi.*.cs`, `SelfTest.Midi.cs` | `FruityHost midi-test` (no DLL) and `selftest --only midi` (the DLL). |

## 3. How it works (the parts that are not obvious)

- **Where the button is:** in the header, after "one instance = one channel" (x = 330 to 480, y = 13 to 39), so every tab shows it and no control of the Emulator tab moves (the `ui` check's golden rectangles and pixel round trip still hold). Control ids 2000-2007 (2000-2019 reserved), outside the Emulator tab's (< 1000) and the tabs' (1000-1999).
- **What a load does to the emulator:** NES console (its default chip FIX when it was another console; a NES chip already picked is kept and the window says VRUN itself uses FIX), ROM mode, Instrument Runaway off, the song from power-on. The note says what was replaced and how to go back ("The song replaced VRUN (built-in) in emulator #1: pick it again in the Game list to go back."; Direct mode and another console have their own sentence); it shows over the picture for 12 s and in the window. The replaced game file stays in the Game list.
- **Restart without allocating:** Play after Stop, Restart and FL's transport start only set a flag. The audio side (`ApplyPending`, between frames) swaps in a **standby**: a fresh copy of the song game that the song worker built and ran for `MusicTemplate.Preroll` = 6 frames (the boot; the decode begins in the 7th frame, index 6, for every song: measured), with its four stems and its APU write tap already made. The first frame after the swap is the decode frame. The worker then builds the next standby. The worker is one thread for the process, polling a flag every 10 ms (a wait handle's `Set()` allocated 48 bytes on the audio thread, measured); it exits after 30 s with no song.
- **FL's transport:** `FPD_SetPlaying` reaches `Emulator.TransportChanged`: a start (from stopped or unknown) restarts, a stop (only after a start was seen) stops; repeats from the other instances of the project do nothing; the window's tick turns it off. Real FL was not tried: that FL sends it on Play and Stop, and from which thread, is the SDK's description.
- **The project:** `SNG1` (u16 version 1, u16 flags: the song is the NES game / follow FL's transport / stopped, u16 template format version, u16 path bytes, u32 MIDI bytes, the path, the MIDI file, CRC-32), written before `GAM1`, by every instance like `GAM1`. Reopened, the first instance's emulator holds it as pending and the loading thread converts and patches it before building the game, then `GAM1` restores the machine (the patched ROM's SHA-1 is the game id, so it fits). A damaged, newer, unknown-flag or lying section is refused with the reason in the window; an older BrokenNes2 skips it by its length.

## 4. Verification (numbers from runs on this machine, the VRUN tree present)

### `FruityHost midi-test` (no DLL): 9 of 9 checks pass, 245 assertions, 7 of 7 deliberate bugs caught (about 6 minutes, the first run builds 14 reference ROMs)

| Check | What was measured |
|---|---|
| synthetic-port | the synthetic song written in C# is make_test_mid.py's output byte for byte (430 bytes) |
| converter | the sources equal VRUN's (line endings aside); **VRUN's two baked `music_data.fab` (`src/audioplayer`, `src/game`) regenerated byte for byte** by the copy, and the copy's log lines equal those in VRUN's `build_report.json`; **VRUN's own `midi2nes` CLI** (built from VRUN's sources by the template script in its scratch copy) and the copy give the same output file and the same log on **18 songs** (11 roster, 6 written here, a no-note file) and both refuse 3 files (not a MIDI, cut short, empty); the plugin's own path (`SongConvert`) gives VRUN's streams, and refuses the no-note and drums-only files whose VRUN streams hold no note |
| title | the title follows the roster's rules (upper case, the player's characters, spaces folded, 24 at most, "MIDI TEST" when empty), the charmap encoding, the loop bytes, each region's filler; nothing else of the ROM changes |
| template-refusals | 17 broken templates refused, each with its reason (version 2, other format, opcodes, region size, a region one byte off, two regions at one place, outside the ROM, title and loop bytes elsewhere, another period table, no version, not a manifest, empty, a ROM changed by one bit, not iNES, mapper 1, shorter) |
| template | a fresh build of the script against the bundled template: the same manifest except the ROM's hash; **24 bytes differ, all inside the strings or pointers to the same string**; 6 songs (synthetic, dense, long, think, level2, vectorplane) x 420 frames patched into both: the same 24 sound registers, the same pictures, the same player position and loop count, every frame |
| engine | **16 of 16 songs** (VRUN's 11 roster songs + synthetic, dense, long, manychannels, lownotes) patched into the bundled template and run on the plugin's machine (NesAudioMachine, FIX cores): every one of the 8 modelled fields agrees in every frame from the second decode frame (19,192 of 19,192 field-frames for a 2,400-frame window; the window holds a whole loop for satnkrt, vectorplane, synthetic, dense, manychannels, lownotes); three negative controls fail for every song (the model's first 8 square-1 notes a semitone off, the decode start one frame late, another song's streams); the decode begins in frame index 6 for every song; the player's loop counter turns at the song's loop length minus one frame (it counts from 0) |
| reference | a **full rebuild** of 14 songs by VRUN's toolchain (one-song AudioPlayer, no padding) and the patched template write **the same 24 sound registers and keep the same player position and loop count in every frame from the decode start** (2,400 frames or loop + 120; the first decode frame included); the decode starts in the same frame |
| facts | BrokenDev's measured figures for the synthetic song (40 notes, square 1 <- 1, triangle <- 2, noise <- 10, loop 480 frames, 121 bytes); thinning, truncation (square 1 1,923, triangle 3,843 bytes), 55 dropped notes on 5 channels, 2 ignored tempo changes (a repeat of the first tempo is not counted), 16 pitch bends, 5 notes below A1 are each said; no notes, not a MIDI, cut short, drums only and over 4 MB are refused with their reasons |
| section | round trip with every flag combination; 932 cut or damaged sections refused (every length, a flipped bit in every byte); newer version, unknown flag and lying lengths refused with a valid checksum; a section without the MIDI refused |

Deliberate bugs (`MusicMutate`, or a change in what the check is fed), each caught: every note a semitone up after conversion (converter: 16 failures), square 1 patched one byte late (engine 21, title 2, reference 16: "differ in 684 of 2400 frames"), the fresh template's engine changed in one byte (template: "PRG 0x014000: 0xA5 / 0x85"), the facts hide dropped notes (facts), the section written without the MIDI (section).

### `FruityHost selftest --only midi` (the Native AOT DLL): PASS, 8 scenarios, 7 of 7 DLL deliberate bugs caught (about 3.5 minutes)

- **flow** (real mouse messages): the button is in the header and overlaps none of About, the tabs, the Emulator row, Inputs, Corruption; the window opens and is modal; Open MIDI (the file dialog replaced by editor hook 621, as the Corruption window does) makes the song the game ("Loaded flow song.mid: it plays from its start"), the note says it replaced VRUN (built-in), the facts are the synthetic song's, the emulator is NES ROM mode, the song sounds; Stop is silent (rms < 1e-6), Play sounds again, Restart restarts, the follow tick is on by default and toggles; after Close the note is over the picture.
- **engine**: the ROM the plugin built (hook 610) is **byte for byte the one the library builds** (the one `midi-test` verifies) for synthetic, dense, think and level2; the plugin's own machine (hook 611: the APU latch after each frame, frames run as the audio thread runs them) plays the converter's streams exactly (4,792/4,792, 4,472/4,472, 19,192/19,192, 19,192/19,192 field-frames), decode in frame 6 from power-on and **in frame 0 after a restart**.
- **stems**: Mix + four stem instances: square 1 rms 0.057, square 2 0.000000 (the song has nothing there), triangle 0.067, noise 0.019; the four stems sum to the mix with correlation 0.965.
- **transport**: FL's Play restarts (decode in the first frame); **first sound 16.7 ms after FL's Play** (one NES frame); FL's Stop is silent; a repeated start does not restart again; follow off: Play and Stop change nothing.
- **reload**: an unchanged file says so; a new export over the same file plays (its loop, its ROM byte for byte); a deleted file is refused and the song plays on.
- **project**: saved, the instance destroyed, a fresh instance opens it: the same ROM bytes, the same facts, the game's state put back (restore result 1, the saved frame 150); a damaged section ("damaged (the checksum does not match)") and a newer one are refused with the reason and no song plays; a project without the section opens to the built-in game with nothing said.
- **refusals**: Reload with no song; not a MIDI, no notes, drums only, cut short: each "was not loaded: <reason>. Nothing changed: the song that was playing goes on", and it does; a version-2 template refuses Test MIDI export ("cannot run in this build: the music test template is version 2; this build of BrokenNes2 understands version 1 only") and nothing loads.
- **alloc**: Mix and the four stems claimed, **0 bytes allocated on the audio thread over 1,500 render calls**, two transport restarts and a stop (`FPD_SetPlaying` itself: 0).
- Deliberate bugs (hook 616), each caught by its scenario: Play stops; square 1 patched one byte late; FL's transport ignored; Reload re-converts the bytes it had; the section without the MIDI; a refusal says nothing; a restart drops the prepared stems (13 MB allocated on the audio thread).

### The whole self-test and the other suites

(filled in at the end of the round: see section 9)

## 5. Found on the way

- **NESFab 1.8 is not byte-reproducible.** Two builds of the same sources, with one compiler thread (`-j 1`) or many, place a few strings of the `/strings` group in another order (and the pointers to them follow): 23 or 24 bytes differ. So the bundled template cannot be compared with the script's output by hash; the `template` check compares what the two do, and that every differing byte is a string or a pointer to the same string.
- **The first decode frame of a track:** in the frame the player starts the track, the noise registers still hold the silence its start wrote and the noise's first note shows one frame later (the squares and the triangle play at once). `decoder_model.py` skips that frame for the same reason; here it is measured on the register latch, and the `reference` check compares that frame too, byte for byte, with VRUN's own build: the template behaves exactly like the game's build there.
- **A drums-only song plays nothing in VRUN's converter:** with no channel besides MIDI channel 10, `SelectChannelMap` falls back to its old fixed map (MIDI channels 1-4) and the drums are left out. The plugin refuses such a file and says why (add a melodic channel); VRUN's build would bake a silent song.
- **Three allocations on the audio thread, fixed:** `NesAudioMachine.CaptureApuWrites` (in `Core`) made a new closure every frame the plugin set it (64 bytes a frame while a NES game's stems were claimed: an existing ROM-mode cost, not only the song's); the Fruity dispatcher built its log line on every call even with logging off (96 bytes per `FPD_SetPlaying`, and per any dispatcher call); `AutoResetEvent.Set` (48 bytes).
- **Windows PowerShell 5.1 started from a process that inherited PowerShell 7's `PSModulePath`** loses `Get-FileHash`; the test bench removes `PSModulePath` when it starts the BrokenDev script.

## 6. Not tested (said plainly)

- **Nobody listened.** The machine has no sound card: every claim is a register trace, a ROM hash, an RMS or a correlation. Whether a replay sounds like FL is the user's call, as decided.
- **Real FL Studio:** the button, the file dialog, `FPD_SetPlaying` on Play and Stop (and from which thread), a project saved and reopened in FL.
- A song whose four streams are exactly at the 1,280-byte limit in the template (the converter never makes more; the truncation path is tested with a 1,923 / 3,843-byte song).
- Other NES sound chips than FIX for the song (they play it; only FIX was traced).
- The SFX-resync decoder variant (`"resync": true`, VRUN's `level2` in the game): the template has the plain decoders; in the player no sound effect borrows a channel, so both play the same streams, but the variant itself was not run here.

## 7. Decisions taken here

- Button in the header (visible on every tab, nothing of the Emulator tab moves) rather than the bottom row (it would have pushed out Instrument Runaway's hint text).
- "Stop" stops and rewinds: Play after Stop plays from the start (a player, not a pause).
- Follow FL's transport both ways (Play restarts, Stop stops), on by default, a tick in the window.
- The song's name and loop length are patched too (the player shows them; the loop counter would otherwise count the template song's length): found by the three-sentinel build, checked against the full rebuild frame by frame.
- The template's bundled `.mlb` is kept in the repository (7.8 KB of text, not embedded) for the template check's string and pointer test.
- Facts in plain lines, coloured by whether they explain a difference; no A/B report.

## 8. Open problems and next steps

- The song starts one NES frame (about 17 ms) after FL's Play; FL's own MIDI starts at once. Removing it needs a preroll of part of a frame (the template boots in whole frames).
- Following FL's song position (start in the middle: simulate the streams to frame F, spec 26 milestone P4).
- A `music import` verb (BrokenDev) that puts the MIDI into the game's own roster and checks the game's Sound Test (pipeline milestone P5); the VRUN game's ROM budget is the limit to watch.
- When VRUN changes its converter or engine: copy `engine/audio/converter` again, rebuild the template with the script, rerun `midi-test`: the `converter` and `template` checks say what changed.

## 9. Final runs

(filled in at the end of the round)
