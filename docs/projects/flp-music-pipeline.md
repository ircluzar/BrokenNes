# The .flp music pipeline: the FL project is the music source of every game

Status: **phase 1 built** (2026-11-09, branch `flp-pipeline`, agent FLP1): design, shared reader (JS + C#, byte-identical), the first back end (VRUN NES, MMC3 main build's engine), the plugin's "This FL project" button. Every other back end is design only.

Related: `docs/projects/round7-midi.md` (Test MIDI export, the VRUN sound-test template), `docs/projects/round9-capture.md` (capture from FL: the same VRUN MIDI layout), `Nano/gb/tools/flp2nano` (the first .flp reader, Nano GB), `docs/projects/brokendev-music-pipeline.md` (the older MIDI-based pipeline plan), `docs/projects/round6-presets.md` (PRS1).

## 1. The idea

The user composes in FL Studio with BrokenNes2 plugin instances (one instance = one console sound channel, in Direct mode) and saves the project. A game's build reads the **.flp itself**: no MIDI export, no "Prepare for MIDI export" macro, no lost slides (FL's MIDI export drops piano-roll slides). The plugin closes the loop: "This FL project" takes the project's own last-saved .flp, converts it with the game's real converter, builds a test ROM with the game's real sound driver and plays it in the plugin's emulator: what you hear is what the game plays.

.flp files go into the game repos as the music source (the user OK'd it: the samples were ripped by the user from Yamaha keyboards).

```
 song.flp ──► shared reader ──► neutral song model (JSON, canonical) ──► back end per game engine ──► engine data + caveats ──► test ROM ──► plugin / emulator
            (flpsong.mjs /       channels, notes, slides, volumes,       VRUN NES (built)                                   (sound-test player
             FlpSong.cs)         per instance: console/chip/channel/     CTW SMS, Vaults GB/GG, Super VRUN SNES/MD,           of that game)
                                 mode/params/sections/preset (PRS1)      DOS/Win95 OPL, Nano NES/GB (design)
```

## 2. The shared reader and the neutral song model

Two twins that must write the **same bytes**:

- JS: `BrokenDev/tools/music/flp/flpsong.mjs` (`parseFlp`, `parseBn2State`, `songFromFlp`, `songJson`), CLI `flpsong-cli.mjs song.flp [--out x.song.json]`. `Nano/gb/tools/flp2nano/flp.mjs` now re-exports its parser from here (flp2nano unchanged: its own test 24/0).
- C#: `Plugin/BrokenNes2.Plugin/Music/Flp/FlpSong.cs` (`FlpSong.Read(bytes)`, `FlpSong.Json(model)`): pure (no UI, no files), linked into the plugin, FruityHost and Workshop.

The model (format `brokennes-song`, version 1) is **neutral and lossless for what a back end needs**; it never decides what a console does:

| part | what |
|---|---|
| `source` | FL version, title, PPQ, tempo x 1000 (integer: no float formatting differences between the twins), file size + sha256 |
| `timeline` | `playlist` (the first arrangement with clips; clips placed, cut at their end, muted clips skipped) or `patterns-in-order` (empty playlist: patterns one after another rounded up to whole 4/4 bars, as flp2nano always did), the song's end in ticks |
| `instruments[]` | every rack channel: index, name, plugin, type, FL's channel levels (pan, volume, pitch knob), its notes on the timeline: `[start, end, key, fine, velocity, pan, colour, slide, pattern]` |
| `instruments[].bn2` | for a BrokenNes2 instance: state version, all parameter values (Bn2Params order), console (+ short name), core (chip index into `Cx.Chips`: resolved by the back end, the list is the plugin's), `direct`/`rom`, channel (Channel parameter, or Auto = where it sat: EMU2), emulator id, ROM path, every saved section (tag, length, sha256), and the preset of a `PRS1` section: CRC checked, the .bn2preset file's JSON header, its blobs (tag, length, sha256) and the whole file (base64) |
| `caveats[]` | what the reader itself knows it did not read: event stream off its declared length, extra arrangements, audio/automation clips, muted clips, empty playlist, recorded controller events, automation channels, unreadable BrokenNes2 states, damaged presets, notes on missing rack channels |

Canonical JSON: compact, fixed key order, integers only, every non-ASCII UTF-16 code unit as `\uXXXX`, one trailing newline. Text from the .flp is decoded as raw UTF-16 code units in both twins (lone surrogates survive).

## 3. Back ends (one per game engine)

Each back end reads the model, writes its engine's data, and reports **caveats: one line for everything it plays differently from FL or not at all** (no silent drops, `feedback_no_silent_fallbacks`). Same instruments in FL are "NES-style" (the plugin's Direct-mode channels); each engine maps them to its own sound with the differences said.

| back end | engine / data | how | status |
|---|---|---|---|
| **VRUN NES** (MMC3 main build; same audio engine as mapper 30) | NESFab four-decoder engine, opcode streams (`engine/audio/converter` = `Music/Vrun/Midi2Nes.cs`) | model -> MIDI in the capture's layout (square 1 = ch 1, square 2 = 2, triangle = 3, noise = 10) -> round 7 `SongConvert` -> `MusicTemplate.Patch` (the sound-test ROM) | **built** (`Music/Flp/VrunNesBackend.cs`) |
| CTW SMS (Sega VRUN ports, S2) | the port's PSG driver (C, converted from the NESFab mapper-30 build) | model -> the port's song tables; 3 tone + noise; the NES triangle becomes a tone channel (a square: caveat), duty caveat, noise 16 periods vs SMS 3 + tone-3 | design |
| Vaults GB (VRUN: Vaults of Obsolete Code) | the Game Boy port's driver | 2 pulse (duty is real here: no duty caveat), wave (triangle table = the NES triangle exactly), noise (LFSR clocks nearest the NES periods, flp2nano's mapping) | design |
| Vaults GG | the Game Gear driver | as SMS (GG stereo: pan becomes real per channel L/R/both) | design |
| Super VRUN SNES | the SPC700 driver | samples **from the presets stored in the .flp** (PRS1: `INS1` blob = the instrument, `CTX1` = context): a BrokenNes2 instance on a Presets chip carries the BRR sample; NES-style instances get generated square/triangle/noise BRR samples; caveat for anything without a sample | design |
| Super VRUN Genesis | the 68k/Z80 driver (YM2612 + PSG) | NES pulses -> PSG tone (duty caveat), triangle -> FM patch or PSG, noise -> PSG noise; FM presets from PRS1 Genesis presets | design |
| DOS / Win95 (Vaults DOS, Super VRUN DOS / 95) | OPL2 driver (+ PC speaker fallback is NOT a fallback: a separate build) | NES channels -> 2-op OPL patches chosen per channel; caveats: OPL envelope, no duty | design |
| Nano NES / Nano GB | the web synth + MIDI player | flp2nano already does Nano GB (`.nano.mid` + `.nano.json`); a Nano NES twin from the same model | Nano GB exists (JS, own parser path), to move onto the model |

### 3.1 VRUN NES in detail (what "plays as the plugin plays" means)

Read: every BrokenNes2 instance on the NES console in Direct mode on pulse 1/2, triangle, noise. As the plugin plays them (`Record/NesDirect.cs`, `Bn2Plugin.ResolveOwner`): one note at a time per channel (newest held wins), pitch = key + note fine pitch + Coarse/Fine (FL's channels count from C5 = 60), slides glide the sounding note in FL's measured shape (0.85 tick early, 1.8 ticks longer: flp2nano `FL_SLIDE`), level = round(15 x velocity/128 x Volume), written as velocity = level x 8 (the converter's volume is velocity >> 3: exact). Bends: RPN 0 per channel, a bend per FL tick through a glide and one at its exact end; the noise key = `$30 | period index` (VRUN's noise decoder takes the low nibble).

Caveats it writes: not BrokenNes2 / ROM mode / other console / Mix-DMC channel (notes "not in the game"); chip other than FIX; two instances on one NES channel (merged); Duty other than 50% (VRUN's squares are fixed at 50%: `$B0 | vol`); short noise (VRUN is long mode); noise softer than 15 (VRUN's noise hits at `$3F`); silent notes; pan (the NES is mono); slides with no note under them; zero-length notes; PPQ that does not divide 960; and after conversion, where VRUN's converter put a part elsewhere (it chooses by itself: `Midi2Nes.SelectChannelMap`). Round 7's own facts (truncation over 1,280 bytes per channel, thinning, bends not played, tempo) come on top.

### 3.2 Not handled yet (phase 1)

Automation clips and recorded controller events (Volume/Pan/pitch automation: said, not converted); FL's channel volume knob (event 219: it is in the model, FL applies it after the plugin; the game does not; no caveat yet: needs the user's view of whether they mix with it); per-note pan/release/mod X/Y; tempo changes; FL's portamento switch; DMC.

## 4. The plugin "listen in game" loop

Test MIDI export window (header button, every tab):

- **Open MIDI...** now also opens a `.flp` (filter "MIDI files and FL projects").
- **This FL project** (new, control id 2008): takes this project's last-saved `.flp`, converts it (reader + VRUN NES back end + round 7 template) and plays it in the plugin's emulator in NES ROM mode. **Reload** reads the .flp again after the user saves in FL ("save the project in FL, then Reload").
- The facts line "N things from the .flp not played as in FL: <first> (+N more)" replaces "all notes have a NES channel" when nothing was dropped (six lines fit); the full caveat list goes to the diagnostics log. The song kept in the FL project (SNG1) is the generated MIDI (a reopened project plays what was heard).

## 5. How the plugin finds its project's .flp (evidence)

- **The repo has no FL SDK header.** `Plugin/BrokenNes.Fruity/FruitySdk.cs` is a C# mirror of what Bendy uses (fp_plugclass.h quotes). Its host calls: ParamMenu, EditorResized, ActivateMidi, GetParamMenuEntry, GetMixingTime, GetPlaybackTime, GetContextInfo (CI_TrackName, CI_TrackIndex); dispatcher events include `FPD_ProjLoaded` ("called after a project has been loaded": **no path in it**). None returns a project path. Without the real header (not downloaded for this work) it cannot be ruled out that the SDK has a path call; my memory of the SDK is not evidence.
- **The real FL log** (reference_fl_real_conventions): FHD_GetContextInfo CI_TrackName answered 0 for every instance: context info is not a reliable channel either.
- **So the plugin uses a file picker (2026-11-10, the user's call: safer than parsing FL's window title, whose form was never verified):** the first press of "This FL project" opens the file dialog; the pick is kept in the instance's saved state (section `FLP1`: u16 version 1, u16 + UTF-8 path, CRC-32; `ProjectFlp.cs`), so it rides in the FL project itself; "Change .flp" (id 2010) asks again and replaces it; a kept file that is gone asks again. No window title is read (FlpTitle.cs removed). Checked by `selftest --only midi` scenarios `flp-pick` / `flp-change` (deliberate bugs `FlpPickNotSaved`, `FlpChangeIgnored`; hooks 621 pick, 623 dialogs wanted, 624 cancel dialogs).
- Better options to test in real FL later: the real SDK header (if it has a path call); FL's recent-files list (registry; unverified).

## 6. Test plan

`FruityHost flp-test [--songs DIR] [--out DIR] [--only ...] [--no-mutants]` (no DLL; songs default `C:\Users\philt\Desktop\Reverse Coaster`, read only; NOT RUN when absent):

| check | proves |
|---|---|
| `identity` | the C# model = the JS model byte for byte on every song (node runs the CLI) |
| `synthetic` | a project written by the test: playlist placement and clip cuts, Auto channel, sections, ROM/GB/sampler/Mix caveats, keys, levels (velocity x 8), Coarse, two-octave slide bends, noise period keys |
| `fl-pitch` | frame by frame, the MIDI the back end wrote plays FL's pitch on every NES channel, worked out from the model alone (independent code); frames next to note starts/stops are timing (the engine check's) |
| `engine` | round 7's engine check on each song's ROM: the converter's streams frame for frame with three negative controls; bends against the MIDI |
| `bend-signs` | (2026-11-10) hand-written streams, no song needed: slides of every sign and size (positive below one period unit is the case that broke) crossing the period's high byte on square 1, square 2, triangle, exact in the template; deliberate bug `SlideSignLost`. Added after the FL song achievements failed `engine`: NESFab miscompiled square 2's 16.8 + 8.8 slide add (positive slides added the period's low byte into the high one), fixed in VRUN 6f1c439 (the slide is held as 16.8) |
| `rom` | each song's ROM, MIDI, facts, caveats (with `--out`: written) |

Deliberate bugs (all caught): slides the wrong way, note fine pitch ignored, oldest note wins, Coarse/Fine ignored (back end); the C# reader dropping a note (identity). Plus `selftest --only midi` scenario `flp` (the real DLL, real mouse messages: This FL project with FL's title from hook 623 and the pick from hook 621; facts; sound; Reload) with its deliberate bug `FlpReadAsMidi`.

Results on the Reverse Coaster songs (2026-11-09) are in the phase 1 record below.

## 7. Phase 1 record (2026-11-09)

See the agent report (and the commit message on `flp-pipeline`) for the numbers: identity 7/7, fl-pitch all frames on FL's pitch, engine 7/7 exact, ROM hashes.

Open questions for the user:
1. Duty: the songs' pulses are 25% in FL; VRUN plays 50%. Add a duty opcode to VRUN's engine (like the bends), or compose at 50%?
2. Title Theme square 1 (1,289 B) and battle triangle (1,700 B) exceed the sound-test template's 1,280 bytes per channel: they go silent before the loop in the test ROM. Is that the game's limit too, or only the template's?
3. Noise volume: VRUN's noise always hits at full volume; FL plays the hits softer (battle 152, victory 6).
   **Done 2026-11-10 (items 1 + 2):** VRUN 7c9c4a1 adds the noise-volume opcode `$D4` (1 byte, first opcode of the noise stream, read free at pointer 0; MIDI CC 15 >= 64 on the noise's
   channel with roster `"noise_volume": true`; the `$C0-$CF` volumes then play, a hit peeks its volume so it sounds on its own frame). The back end writes CC 15 = 127 when a noise part has a
   hit under 15. The templates' song room is measured, not 1280: VRUN NES 3,609 bytes a channel (bank 30's free run shared out), CTW SMS 3,736 (ctwsms_room.py re-runs fab2c's far packing
   in data banks 2-18). Title Theme square 1 and battle's triangle fit both. Checks: flp-test `noise-volume`, `room`.
4. ~~What FL's main window title looks like~~: moot since 2026-11-10 (file picker, pick kept in the project).
5. FL's channel volume knob: should it count (it changes the mix in FL, not in the game)?

## 8. Phase 2: Super VRUN (SNES, WideMess, MD, DOS, 95), the fence, Nano GB (2026-11-10)

- **One song file for five engines:** every Super VRUN build plays `SuperVRUN_dev/snes-dev/supervrun/audio/*.it`, made by `tools/music/sf2it.py` (MIDI + MNES.sf2; needs `py -3.10`: mido + numpy) and read again by the MD (`itconv.py`) and DOS/95 (`oplconv.py`) converters. `Music/Flp/SuperVrunBackend.cs` writes sf2it's MIDI layout (one channel per instance, program = duty 0-3 / triangle 4 / noise 5, noise key 48/64 + period index, velocity 127 sqrt(level/15), pan, bends).
- **The fence** (`Music/Flp/FlpFence.cs`, format `brokennes-fence` v1: ok / note / over per rule): before conversion the back end (instruments not in the game, 15 MIDI channels, voices: SNES 8 with 2 shared with effects, MD 5 FM + 3 PSG + 1 noise, DOS/95 7 + 1 noise with 1 shared with effects, SNESMOD range, duty/timbre/pan/noise per engine); after conversion `BrokenDev/tools/music/flp/supervrun_fence.py` (sf2it's dropped/shortened notes, drift, patterns <= 64, instruments <= 64, sound RAM, check_modules.py, itconv.py channels without a voice, oplconv.py steals).
- **Test build:** `BrokenDev/tools/music/flp/build-supervrun.ps1 -Flp x.flp -Engine snes|wms|md|dos|95 [-Out dir] [-FenceOnly]`: the song replaces ROSBIF (module 7, the title music) in a scratch copy of the game; the game's own build. Verified on overworld: SNES, WideMess, MD images open headless and play the song at the title (pitch classes agree SNES/WMS 0.82, SNES/MD 0.65, vs the original title 0.35); DOS on the x86 console plays module 7 with a different OPL stream (229 -> 242 writes, hash DDF4D22F -> E2647A9C); the x86 run then reported "opl not found at 388h". **Cause (2026-11-10):** the console charged no time for port accesses, so at the test runner's 130 M instr/s the game's AdLib detection (timer 1 = 80 us, then 2000 status reads) finished in ~77 us, before the timer fired (at the desktop's 60 M it passed). Fix: `PcMachine.AdlibWait` charges ~0.55 us of ISA time per 388h/389h access; now "opl found at 388h" at 130 M, and the overworld test build gives the same stream on x86 and DOSBox-X (370 writes, hash CB8498B7 at frame 600). Listen: `C:\BrokenNes-work\flp_out\overworld.dos.wav` (Workshop `--x86session exe --wav --opllog`).
- **Plugin:** Test MIDI export window, "In game: ..." button (id 2009) picks the target; "This FL project" then runs the script (Emulator.GameBuild.cs), shows the fence headline and loads SNES / WideMess / MD images in ROM mode; DOS / 95 / Nano GB are built and reported. Not exercised through the DLL by a check yet.
- **Nano GB:** `Nano/gb/tools/flp2nano/fence.mjs` writes `<name>.nano.fence.json` from flp2nano's warnings. **Nano NES has no song player**: no target.
- Checks: `FruityHost flp-test` `sv-synthetic`, `sv-fence`, `sv-songs` (+4 deliberate bugs); `node Nano/gb/test/flp2nano.test.mjs` (29).

## 9. Presets: the instruments designed in the plugin reach the games (2026-11-10)

**What a preset is** (Presets/PresetFile.cs): one instrument ripped by Instrument Runaway (clean or corrupted), not a recording: INS1 the instrument now (SNES: BRR bytes at their ARAM address, loop pointer, ADSR/GAIN, tuning, volumes, echo/noise/pmod flags; Genesis: FM patch registers or a DAC sample; PSG / Game Boy / NES registers), CLN1 the clean one, HIS1 the corruption history with seeds, CTX1 the engine context (SNES: 128 S-DSP registers + any ARAM a corrupted sample plays into), NOTE learned game voices. It rides in the instance's state as section PRS1 (u8 version, u8 flags, u16, u32 length, the file, CRC-32). The shared reader (FlpSong.cs / flpsong.mjs) already carried it byte for byte (`FlpBn2.Preset`): the canonical JSON is unchanged, the decoding is C#-only in `Music/Flp/FlpPresetSound.cs`.

**The sample** (`FlpPresetSound.Of`): a SNES preset = EXACT: its BRR walked the way the S-DSP walks it (own bytes, loop pointer, the kept overrun ARAM), the loop unrolled until the BRR filter state repeats, C5 = 32000 x the pitch register the plugin keys at middle C / $1000. Any other preset = RENDERED: the plugin's own engine for it plays middle C 1 s at 33488 Hz (128 x 261.63 Hz), the last 10 periods (1280 samples, 80 BRR blocks) loop. A NES preset is not rendered (its player lives in the plugin): OVER.

**Per engine** (Super VRUN back end; the instance's MIDI program is 6, `flp-backend` writes `<name>.presets.json` + WAVs, `build-supervrun.ps1` passes `--presets` to sf2it.py, which makes the sample instrument `pcm<ch>`):
- SNES / WideMess: the preset is the song's own instrument (smconv re-encodes it as BRR). Fence: Ok + Notes for the envelope (SNESMOD holds the level), noise / pmod flags, no loop; `sound-ram` OVER past $1A00-$FE00; `range` OVER above the S-DSP's $3FFF.
- Genesis: itconv.py plays `pcm..` samples on the DAC (driver patches are a fixed Z80 table: no per-song FM patch without a driver change). Note: one clip at a time, shared with effects, loop held up to 1 s, no note-off.
- DOS / 95: oplconv.py plays its default patch: OVER.
- Vaults GB: a Game Boy wave preset's 32-sample wave RAM becomes the song's wave table (exact), pulse = its duty, noise = its width; envelope as level steps. Every other preset on Vaults GB / GG, VRUN NES and CTW SMS: the ENGINE APPROXIMATION below.

**Measured** (headless BrokenNes, the plugin's own render of the preset vs the game build's recording, spectral similarity): SNES preset on Super VRUN SNES 0.999 (pitches exact: 2000/2516/3000/4000 Hz), Genesis FM preset on SNES 0.999, on WideMess 0.974, on MD 0.891 once pitch is compensated: **the MD DAC plays at ~0.74x the pitch itconv.py assumes** (middle C at ~194 Hz) - **resolved 2026-11-10** (the game's Z80 driver, see below).

**Checks:** flp-test `sv-preset-snes`, `sv-preset-engines`, `preset-others`, 8 deliberate bugs (PsMutants). Game converters: sf2it.py (SuperVRUN c101aff) and itconv.py (SuperVRUN_MD 10442ad), default off, every existing song byte-identical.

**Engine approximation** (2026-11-10, `Music/Flp/FlpPresetApprox.cs`): the register engines (VRUN NES, CTW SMS, Vaults GG, Vaults GB for non-GB presets) play a preset as what their channels can express, read from its registers: the channel role (pulse / triangle-wave / noise; a sample or FM preset keeps the instance's channel), the duty (NES / GB pulse), the noise mode (NES short, GB 7-bit, PSG periodic), the volume envelope as level steps per 60 Hz frame (NES decay with its loop flag: 15 down one step per (period+1) quarter frames; GB NRx2 start / direction / pace) and the pitch as steps per frame (NES sweep: (period+1) half frames per step of period>>shift). Written as: VRUN NES / CTW SMS = CC 14 duty, CC 7 level steps inside the note (the converter's volume = velocity x CC7/127, no restart), pitch bends on top of FL's; Vaults GB / GG = "vol" and "freq" events per frame. The fence line names the preset, what is KEPT and what is LOST (a sample's / FM patch's timbre, the GB sweep and wave shape, length counters, the corruption history, plus each engine's own: VRUN's long-only noise and volume-less triangle, the PSG's 50% squares and white noise). Damaged / empty presets stay OVER. Not yet: captured note lives (the extraction branch's NOTE v4) are not read here. Checks: flp-test `preset-approx` (+2 deliberate bugs: steps dropped, envelope flat), `preset-others`, `ctwsms-preset` updated.

**The MD DAC pitch** (resolved 2026-11-10, SuperVRUN_MD eea7834): the driver (src/z80/mdsnd.s80) played one sample per main loop and a loop took 1.5-4 sample periods (bank switches, a music command, Z80 ROM reads that wait on the 68000's bus); only one lost period per loop was made up. Same on BrokenNes and BizHawk Genplus-gx (title: 4960 / 4970 loops/s against 7610 samples/s), so neither the emulator nor itconv.py's rate. Fix: timer A polled between every stage, the 68000 fills the 256-byte ring and counts the frame in the VInt (plat_md.c md_snd_feed, ~130 us bus hold), the periods a frame did not play become bytes the clip skips. Bytes consumed per frame with a clip (expected 127.0), title + 1900 frames of play: Genplus-gx 0.9997, BrokenNes 1.011. Cross-check tooling: compat reads `zram` on the Genesis (BizHawk adapter + Workshop). Open: ~30% of periods are still skips (the loop runs long: effective ~5.4 kHz); BrokenNes's ~1% over Genplus-gx not chased.

## FL channel volume (2026-11-10)

FL's channel volume knob (event 219, `Levels[1]`; default 10000 = FL's 78%) is FL's mixer, not the plugin's: no game carries it. Every back end (VRUN NES caveats, CTW SMS, Vaults GB / GG, Super VRUN fences) gets one note line `[fl-volume]` per BrokenNes2 channel with notes whose knob is off its default: "FL channel volume isn't carried; use the plugin's Volume" (`FlpFence.ChannelVolume`). Check `flp-test fl-volume` (+ deliberate bug `ChannelVolumeSilent`). On the user's songs only overworld moves knobs, on its two 808 sampler channels (not in the game anyway): no line.

## 10. Instrument extraction: an instrument taken from a game becomes a preset (2026-11-10)

The other direction of section 9: an instrument a game plays is captured from one of its channels and kept as a Runaway-style preset (`*.bn2preset`) that the plugin loads and plays from FL's notes. Three front ends share ONE capture core (`Plugin/BrokenNes2.Plugin/Extract/`: `InstrumentCapture.cs` the definition and the recorder, `ExtractFeeds.cs` the glue to a running chip, `CapturePreset.cs` the preset), linked as source by the plugin, the FruityHost bench, the Workshop and the desktop app:

| front end | how | where |
|---|---|---|
| (a) the plugin, ROM mode | Presets tab: **Capture next note: <channel>** (the Emulator tab's Channel picks it; shown only while a NES game on the FIX chip or a Game Boy game runs). The channel's next whole note goes into the library. | `Emulator.Extract.cs`, `PresetLibrary` (`IPresetCapture`), hooks 1140-1143 |
| (b) BrokenNes desktop | **Emulator > Capture Instrument > (channel)**: the channel's next whole note, exported with a Save dialog | `Windows/MainForm/MainForm.Extract.cs` |
| (c) BrokenDev CLI | `brokendev.ps1 instrument extract <rom> --frame N --channel C [--out f.bn2preset] [--input "40:Start,44:"] [--name T] [--json]` (headless Workshop `--extract-instrument`; NES on the FIX cores, Game Boy on the stock cores; deterministic: the same ROM, input and frame give the same preset) | `BrokenDev/lib/Instrument.ps1`, `Workshop/ExtractInstrumentCli.cs` |

### The capture (what a captured instrument IS)

Sampled once per video frame per channel, from the chip's own state at the frame's end (NES `APU_FIX.ProbeControl`, Game Boy `APU_GB`'s channel probe) plus its register-write observer:

- **key-on**: the frame the game re-started the channel. NES: a write to `$4003/$4007/$400B/$400F` (the length load that restarts the envelope and the sequencer). Game Boy: a write to NRx4 with bit 7. Also any frame where the channel's level goes from 0 to above 0 (a driver that never re-keys).
- **the note** asked for (`--frame N`): the last key-on at or before N whose note still sounds at N; if the channel is silent at N, the next key-on after N. (The desktop and the plugin take the next whole note after the click.)
- **snapshot at the key-on**: the console's whole sound-register block at the key-on frame (NES `$4000-$4017` as last written; Game Boy NR10-NR51 + wave RAM + the CGB flag). The preset's instrument (duty, envelope, sweep, length, wave table, noise width...) is read from it with the same `NesInstruments` / `GbInstruments` readers Instrument Runaway uses.
- **envelope over the note's life**, one entry per frame from the key-on to the key-off: **level** 0-15 (what the chip outputs: NES constant volume or the envelope's decay level, 0 when the length counter ran out or a pulse timer is below 8, the triangle 15 while both its counters run; Game Boy the envelope volume, the wave channel 15/7/3/0 from NR32), **period** (NES timer or noise index; Game Boy 11-bit frequency or NR43), **duty** (pulse duty; NES noise mode; Game Boy wave level code, noise width). Pitch is kept as the period, so vibrato, slides, arpeggios and the hardware sweep are all in it (as cents from the key-on).
- **key-off**: the first frame after the key-on where the level is 0 (*Silenced*), or the channel is keyed again (*Retrigger*), or 600 frames (*Cap*), or the recording ended.
- Not captured: the NES DMC (its sample lives in the game's memory), SNES / Master System / Game Gear / Genesis (the presets of those come from Instrument Runaway; their extraction is the next step: the same recorder with an S-DSP voice / PSG / FM-channel probe).

**In the preset**: the instrument (INS1/CLN1 from the snapshot), the context, and the note behaviour **version 4** (`PresetNote.Captured`): `CapturedNote.ToBytes()` = u8 format 1, console, channel, end, u32 key-on frame, u8 + register block, u16 frame count, then per frame u8 level, u16 period, u8 duty (bit 7 = keyed). Older versions of the plugin keep the block and play the plain instrument.

**Playback**: NES (`NesPresetPlayer`): FL's note plays the captured life frame by frame (60.0988 Hz): constant volume = the frame's level, the frame's duty / noise mode, the pitch = FL's pitch + the frame's cents from the key-on (sweep off: the periods carry it); after the last frame a *Silenced* note goes quiet, any other holds its last frame while FL holds the key. Game Boy (`UpdateGbVoice`): the snapshot's registers play (its hardware envelope is the level), FL's pitch + the captured cents per frame, and the note stops at a *Silenced* end. (A Game Boy driver that changes the volume by re-triggering is captured, but plays its key-on volume only: the GB envelope cannot change under a sounding note.)

### Round trip (checked: `selftest --only extract`; real games: `FruityHost extract-roundtrip <dll> <rom> --frame N --channel C`)

A captured note is made a preset, loaded into the real DLL on the Presets chip and played by one FL note at the captured pitch for the captured length; the result is compared with the game's channel alone (scale-free): **pitch** = the median cents between the two pitch tracks, **envelope** = per-frame RMS each normalised to its peak, mean absolute difference, **spectral similarity** = cosine of the two magnitude spectra of the whole note.

| source | note | pitch | envelope error | spectral |
|---|---|---|---|---|
| synthetic NES driver, pulse 1 (decay, duty change, vibrato) | 36 frames | +0.0 c | 0.1% | 0.999 |
| synthetic NES driver, triangle | 40 frames | -0.1 c | 8.7% | 1.000 |
| synthetic Game Boy, square 1 (hardware decay + legato pitch change) | 27 frames | 0.0 c | 5.0% | 1.000 |
| Crazy Luigi (SMB1 hack), pulse 2 @ 214 | 7 frames | +0.6 c | 13.3% | 0.999 |
| Crazy Luigi, pulse 2 @ 430 | 7 frames | +1.1 c | 10.1% | 1.000 |
| Crazy Luigi, triangle @ 213 / 429 | 8 frames | +0.3 / +0.1 c | 7.8 / 11.3% | 0.999 / 1.000 |
| Codename 23 (SMB1 hack), pulse 2 @ 205 / 395 | 7 frames | +0.2 / +2.8 c | 14.7 / 9.5% | 0.999 / 1.000 |
| Codename 23, triangle @ 204 / 394 | 8 frames | +0.3 / 0.0 c | 15.7 / 2.2% | 0.998 / 1.000 |

(The SMB1 hacks play their music on pulse 2 and the triangle; pulse 1 is the sound effects. Their envelope error on 7-8-frame notes is mostly sub-frame timing: the game keys mid-frame, the preset at the frame's start.) The check also fails on a deliberate bug (the envelope flattened to the key-on level: caught by the envelope error) and runs the plugin's ROM-mode capture on the built-in game (Pulse 1, 12 frames; the preset reads back with its envelope, loads and sounds).
