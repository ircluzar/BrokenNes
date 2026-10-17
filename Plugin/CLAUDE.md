# Plugin/ - BrokenNes2 (FL Studio native plugin)

Handoff note for whoever works here next (human or agent). **Development is paused; the state is very good.** Read `Plugin/README.md` for the
full description of what the plugin does and how each test proves it; this file is the practical rest: where things are, how to run them, what bites.

## Synth system, wave 1 (2026-10-03): the plugin is part of the desktop app now

The desktop app (`Windows/Synth`, menu **Config > Synthesizer Mode**) installs the plugin into FL Studio and runs it as a **standalone synth**; see
"Installing, and the standalone synth" in `Plugin/README.md` for what each does. Pieces: `Windows/Synth/` (`FlStudioInstall` detection, `PluginInstaller`,
`InstallVstDialog` / `InstallVstFlow` = `--install-vst`, `SynthMode` / `SynthForm` / `SynthAudioOutput` / `SynthMidiInput` / `SynthSettings` = `--synth`),
`Plugin/BrokenNes.SynthHost` (the live rack and MIDI router, headless-testable). Certified by `UAT/plugin/install-smoke.ps1` (13 checks, fake FL folders, no UAC)
and `UAT/plugin/synth-smoke.ps1` (12 checks, real window, keys, restart handoff; `certify.ps1 -Only plugin-install` / `plugin-synth`).
**Not verified by anyone: the WASAPI handoff to a real playback device and the UAC click.** The dev machine had no playback device when this was built
(the Focusrite was off), so the synth's audio path was certified through `BROKENNES_SYNTH_AUDIO=null`; play a key through real speakers once by hand.
**Next waves (not done):** per-channel stems / the Mix channel and a game's pad in the synth, an uninstall item, more than the four tone voices, anything
for DAWs other than FL (this is a native Fruity plugin, not a VST).

## Instrument model and corruptor (2026-10-17, Round 6 `imodel`): a pure library over the Runaway snapshots

`Plugin/BrokenNes2.Plugin/Instruments/` describes the instruments a Runaway snapshot holds as typed **fields** (id, name, kind enum / range / bits / signed, min, max, value, a one-line explanation, whether the sampler plays it) and **blobs** (BRR sample, DAC sound, wave RAM, DMC sample),
reads them from `SpcSnapshot` / `SegaSnapshot` / `GbSnapshot` / `NesSnapshot` and writes an edited instrument into a **copy** of the snapshot that the existing samplers play (`InstrumentSources.For(snapshot)`: `List`, `Read`, `Write`). A write is diff-based (only what differs from the snapshot's own reading is touched), so read -> write unchanged is byte-identical.
The corruptor (`InstrumentHistory`: `Randomise`, `Nudge`, `FlipBits`, `Set`, `Lock`, undo, redo, replay) is integer-only and seeded (xoshiro256**); a seed + the operation list gives the same bytes on any machine, and `FinalBytes()` / `InstrumentCodec.Pack` store the result independent of the corruptor. Full description, the field tables per console (which fields each sampler plays) and the numbers: `docs/projects/round6-imodel.md`.

- **Engine changes (all behind a null test, golden-hash neutral):** `SpcSnapshot.Overlay` (`SnesOverlay`: per-sample envelope, tuning, left/right weights, echo/noise/pitch-modulation flags, "pinned" samples) is read by `SnesSampler` and `SpcSnapshot.TryEnvelope` / `Instruments()`; a snapshot without an overlay plays bit for bit as before (`imodel` check `neutral`: made-up snapshots and SMW, Sonic 1, Zelda LA, Addams Family GG). `HasSample`/`Resolve` lost their LINQ (an allocation on the audio path).
- **Persistence:** section `SPO1` (after `SPC1`; CRC-32; a damaged or newer one is dropped and `Emulator.InstrumentNote` says so). Edits to samples, loop pointers, PSG/FM/DAC registers, wave RAM live in the snapshots the project already saves (`SPC1`, `GBS1`, `PSG1`/`OPL1`/`OPN1`).
- **Applying an edit:** `Emulator.ApplyInstrument(Instrument)` (UI thread, Runaway on) swaps the working copy and **prepares each instance's sampler on the UI thread** (`Bn2Plugin.PrepareSamplerFor` / `TakePrepared`): the audio thread only hands the new sampler over, no allocation, no click (measured: 0 bytes for the first block after an edit, 362,968 without the preparation). Test hooks 300-312 (`Bn2Plugin.Instruments.cs`).
- **The Instruments tab's model:** `Instruments/InstrumentsUi.cs` implements `Ui/Contracts.cs`'s `IInstrumentModelProvider` over one `InstrumentSession` per emulator (histories, locks and the working copy shared by every instance's editor) and registers itself through `UiBoot.RegisterInstrumentModel()`. Presets get an `InstrumentPayload` (instrument now, clean, history with seeds, context); `InstrumentPush.Handler` is where the `inject` work plugs in. A ROM instrument of the YM2413 is read-only.
- **Checks:** `FruityHost imodel-test [--only id] [--no-mutants]` (no DLL; 18 checks, 35,860 assertions, 19 mutants) and, in `selftest`, `imodel` (the same, then the model through the real DLL: SNES overlay played, saved, damaged; Game Boy and Genesis edits) and `imodel-ui` (the real model behind the real tab, real mouse and key messages, 3 more mutants). `imodel-fields` prints the field tables, `imodel-golden` / `imodel-real` the hashes the sampler change was held to.
- **Rules this added:** a field the sampler ignores must say so (`Audible.No` + reason) and the `audible` check proves it on PCM in both directions; a new field needs its audibility claim tested; keep `Instruments/*` free of UI and of anything that runs on the audio thread; new corruptor behaviour changes the `GoldenScripts` hashes on purpose (re-record them and say why).

## Sega track C (2026-10-03): the Master System PSG chip

Console 3 (`SMS` on the buttons) holds one chip, `SMS` (chip index 20, appended; the 12 parameters are unchanged): the SN76489 PSG through the bridge core `APU_SMS` (`Windows/NesEmulator/mix/SegaSoundBridges.cs`), Direct mode only. Mapping, limits and **what ROM mode needs once the Sega boards exist** are in `Plugin/README.md` ("The Master System console"); Sega games are refused in ROM mode with a message (`Cx.RomSupported`). Rules this added: **a new APU core id must get a family in `CoreCatalog` (`NesApuFamily`) before the plugin sees it**, otherwise it joins the NES chips alphabetically and shifts every saved chip index; the plugin sets `APU_SMS.DefaultLowNotes = Drop` and `DefaultStepFront = false` before it makes a chip (notes below 109 Hz are silent and counted; the front NES core is not stepped, 3x faster). Test hooks 52-55 read the PSG registers and the dropped-note count of this instance's chip.

## Sega track C2 (2026-10-04): the Genesis chip and Master System / Game Gear ROM mode

Console 4 (`MD` on the buttons, `Cx.Genesis`) holds one chip, `MD` (chip index 21, appended last; the 12 parameters unchanged in meaning): the YM2612 + SN76489 through the bridge core `APU_MD` (`Windows/NesEmulator/mix/GenesisSoundBridges.cs`, patches in `mix/FmPatchBank.cs`), Direct mode only.
**The Instrument parameter is the FM patch picker for this chip** (`Bn2Plugin.Apply` sets `APU_MD.Patch` on this instance's own chip; the editor shows a patch dropdown on the pulse channels only; out of range is the last patch, the PSG square). The plugin sets `APU_MD.DefaultLowNotes = Drop` and `DefaultStepFront = false` like the PSG chip.
Genesis *games* were refused in ROM mode at C2 (they run since C3b, see the next section); **Master System and Game Gear games run in ROM mode** from C2 through `SessionGame` (any `IConsoleSession` mixes through `Mix`; `Cx.FromKind` maps both to console 3; the file filter gained `.sms .gg .sg .sc`). Test hooks 56-62 read the Genesis chip's patch, FM keys and counters.
Checks: `md` (chip index, pitch 0.6 cent worst, level against the NES chip, the Instrument parameter per instance and through the editor, polyphony, 8x real time, 0 bytes allocated) and `rom-sms` (tiny Master System and Game Gear ROMs made by the test: load, draw at their own size, play their tone through Mix). Note for a test that measures pitch from zero crossings: use patch 14 (the sine) on this chip, a rich timbre doubles the count (`cores`, `swap` and `consoles` do).
What Genesis stems, Runaway and games need: `Plugin/README.md` ("The Genesis console") and `docs/projects/sega-track-c.md` (milestone C2).

## Sega track C3b (2026-10-09): the Master System FM chip, Genesis games and Sega stems in ROM mode, Sega Runaway

- **Chip 22, `SMSFM`** (appended last; core `APU_SMSFM` in `Windows/NesEmulator/mix/SmsFmBridge.cs`, the 22-instrument bank in `mix/OpllPatchBank.cs`: the chip's 15, six designed ones, the PSG square). The Instrument parameter is its instrument picker (`Cx.PatchBankOf` 2, `Cx.PatchItems`). Test hooks 63-70 read its registers and counters.
- **Genesis games in ROM mode:** `Cx.RomSupported` is true for every console; the file filter has `.md .gen .smd`. A Sega game (`SessionGame`) offers `IGame.Stems` (`IGameStems`: `Has`, `Want(mask)`, `Read`); `SessionGame.StemOf` maps plugin channels to the sound unit's stems (`IVoiceStems` in `sega/SegaStems.cs`, implemented by `SmsSound`, `SmsFmSound`, `MdSoundUnit`).
  Stems cost CPU only for the channels somebody sits on (`Emulator.ClaimedChannels`).
- **Channel model** (`Bn2Params.cs`, `Ch`): ids 0-4 Pulse 1, Pulse 2, Triangle, Noise, Mix; 5 the Sampler (a hole: never a real channel); 6-20 FM 1-9, DAC, Bass drum, Hi-hat, Snare, Tom, Cymbal; `Ch.Total` = 21. Parameter 12 is `Voice` (0 = use *Channel*). **Everything that indexes by channel uses `Ch.Total`; ids are append-only (a saved project stores them).** A new voice needs `AllNames`,
  the editor's `CompactNames`, `Cx.Channels` (which console and chip offers it), `StemOf` and `Ch.Label`. An instance that asks for a voice the emulator lacks keeps a Problem and stays attached (`Emulator.Attach`), and is placed again when the chip or console changes (`SetChipLocked`).
- **Sega Runaway:** `SegaSnapshot.cs` (the PSG1 / OPL1 / OPN1 sections, `DacRecorder`, `DacAnalyzer`), `SegaSamplers.cs` (`PsgSampler`, `OpllSampler`, `OpnSampler`; notes go to numbered slots, `SegaSnapshot.SlotBase`), `Bn2Plugin.RenderSegaSampler` / `UpdateSegaVoices`, `Emulator.SegaState` / `SegaSamplerMode` / `AnySamplerMode`, `IGame.CaptureSega`
  (`SessionGame` taps `MdSoundUnit.YmPortTap` for the DAC writes). The Instrument parameter is 0 = the game's choice, else the instrument + 1. The state reader checks the tag against the bytes' kind and the length (<= 300,000), `SegaSnapshot.FromBytes` validates every field; anything that fails is dropped, never trusted. Test hooks 71-80.
- **Rules this added:** a new APU core id needs a family in `CoreCatalog` *and* a root in `WebLite/LinkerConfig.xml` (as for C1); the plugin sets `APU_SMSFM.DefaultLowNotes = Drop` and `DefaultStepFront = false`; ask for a stem before the unit's first write when you can (one asked for later joins the mix within about 0.3 s, which the verifier measures);
  every sampler must stay allocation-free (`runaway-sega` measures 0 bytes); speed tests read processor time (`Pace`) but a busy machine still skews them (other agents compiling cut every figure by a third or more): rerun when it is quiet and compare with the NES chip measured in the same run.
- **Checks:** 47 in all (46 pass + the QLQ warning): new `fm`, `rom-md`, `stems-sega`, `runaway-sega`. Failure injections recorded for them are in `docs/projects/sega-track-c.md` (milestone C3b).

## Project game state (2026-10-16, track C4b): a project keeps the whole machine of a ROM-mode game

**The bug it fixed:** a project saved with a game in ROM mode (Instrument Runaway on or off) stored the ROM path and, in Runaway, the sound snapshot, but not the game: after reopening, the game was at power-on and Runaway OFF restarted it from reset.
**What a project keeps now:** the machine as the game's session writes it (CPU, picture chip, sound chips, memory, cartridge: NES, Game Boy / Color, Master System, Game Gear, Genesis), the frame count, whether Runaway was on and, for a frozen game, its picture. Reopened, the game is exactly there: Runaway OFF goes on from that state, and **any change made to the machine before the save is part of it** (the user's corruption plan).

- **Format:** a new section `GAM1` (`GameStateSection.Tag`, `GameState.cs`: 52-byte header, then game id, note, the state deflated when that helps (CRC-32 checked), the picture) appended after the sound section. Sections after the `EMU2` record are now read in a loop (`tag, length, bytes`, at most 8): `SPC1`, `GBS1`, `PSG1`/`OPL1`/`OPN1` and `GAM1` are parsed, any other tag is skipped by its length (up to 32 MB), a stream that ends or lies stops the loop. Older DLLs ignore a section they do not know, and a project without the section loads exactly as before.
- **Writing** (`Emulator.State.cs`, `CaptureGameSection`, from `Bn2Plugin.SaveRestoreState`; every instance writes it, like the Runaway snapshots, so the project grows by the section once per instance): the game's `SaveGameState()` is taken under `romLock` between frames, the compression happens after the lock. Not written when the game is still being built or waits unapplied (`carriedSection`: the section the project came with is written back as it was, so saving at the wrong moment loses nothing). A game that cannot keep its state, a state that cannot be written and a section over `GameStateSection.MaxStored` (8 MB; hook 87 lowers it) are written as a marker section that says why, and the user is told at once (`Emulator.StateNote`, a banner over the screen) and again when the project is opened.
- **Restoring:** `Bn2Plugin.SaveRestoreState` -> `Emulator.Adopt(..., restoredGame, gameProblem)` (only the first instance of a project to open adopts, as before) -> `StartLoad(RestoreRequest)`. A section saved in Runaway sets Runaway before the loading thread starts (the game never runs before its state is in place); `ApplyRestore` runs **on the loading thread, on the game it just built, before it is handed to the audio side** (nothing allocates on the audio thread; a restore that fails leaves the game at power-on). It checks console + game id (the ROM's SHA-1; the NES's has `nes` in front), then `IGame.LoadGameState`; a frozen game's picture is put back too. A mismatch (the file at the saved path is another game), a damaged / cut-short / newer / oversize section or a state the game refuses is a note (`StateNote`, `StateRestoreResult` 2) and a game at power-on; a project saved in Runaway keeps its Runaway (frozen at power-on, the note says switching it off starts the game).
- **Written against `IGame.StateSupported` / `IConsoleSession.SupportsState`, never against a console name.** A game without state writes the marker and says "<console> games cannot keep their state in a project yet: the game restarts when Instrument Runaway is switched off" (Runaway frozen at power-on after a reopen; "...the game started from power-on" otherwise). Every console has states now (the SNES since `snes-state` merged), so that message is only seen when a state is refused; `runaway-state` runs the full scenarios for every console (it asks hook 89).
- **The corruption hook:** `Emulator.PokeWorkRam(offset, value)` / `PeekWorkRam` (`IGame.PokeWorkRam`, between frames, game running or frozen) write the main work RAM: NES 2 KB system RAM, Master System / Game Gear 8 KB (`$C000`), Genesis 64 KB (`$FF0000`), Game Boy / Color 8 KB (`$C000-$DFFF`, `GbSession.PokeWorkRam`). Whatever was changed in the machine before `SaveGameState()` is in the saved bytes; Runaway on / save / reopen / Runaway off continues from the corrupted machine (`runaway-state` does exactly this with a counter the game keeps in RAM). For memory outside work RAM, change the session's board object under `romLock` the same way (`Emulator` has no generic hook yet; `SessionGame.Concrete` is the way to the `SmsSession.Board` / `MdSession.Board` / `GbSession` internals). The blob itself is `GameStateSection.State`: a `ConsoleStateFile` for the sessions (deflated payload of the board's own state, CRC-checked, units named), `NES1` + latch + the NES core's JSON for the NES; editing those bytes then rebuilding the section with `GameStateSection.Build` is the offline way.
- **Things to know:** the NES core drops its queued audio when a state is loaded, so a restored NES machine differs from the one that never left in the APU's audio bookkeeping only (ring pointers, resampling phase, output filter memory: `NesGame.SaveComparableState` zeroes them for the tests; the emulation is identical and the picture and RAM match exactly). Reading a Genesis sound unit's voices for the Runaway snapshot resets the YM2612's DAC swing (`Ym2612.DescribeVoices`), a bookkeeping value the state carries and nobody hears: the control that a frozen project is compared with does the same switch on and off. The NES APU register latch is not in the NES core's state: it is saved beside it (`Bus.SetApuLatch`) so a per-channel stem made after a reopen starts from the right registers. A save takes 1 to 55 ms (Genesis 26 ms; Titan Overdrive 53 ms), of which the machine's own write is the part under the emulator's lock.
- **Checks:** `runaway-state` (Native AOT DLL): for NES (VRUN and a counter ROM), Game Boy, Game Boy Color, Master System (PSG and FM chip), Game Gear and Genesis, saved frozen and saved running: the section is there, the restore result is 1, the game is at the saved frame, **the machine's state is byte for byte the saved one, the corrupted bytes are back, the frozen picture is the saved one, a frozen game stays frozen, and after Runaway off and the same frames the machine, picture, memory and frame count equal those of a run that never left**; two runs of the control are the same; older projects, an unknown section before and after ours, a damaged / truncated / 1 GB-claiming / absurdly long section, another game at the saved path, an oversize state, several instances of one project (only the first puts its state back), saving again while the game is still being built, and the no-state message (banner drawn: editor hook 90). Test hooks 81-90 (`Bn2Plugin.TestDispatcher`). `BROKENNES_TEST_GAMES` runs the same scenarios on real games with 10 s before the project and 5 s after.
- **Project size** (frozen = with the picture; per instance; the base project is 80 to 436 bytes): NES VRUN 24 KB frozen / 16 KB running, NES counter ROM 9.1 / 2.2 KB, Game Boy 4.6 / 0.7 KB, Game Boy Color 1.4 / 0.5 KB, Master System 6.9 / 3.7 KB (FM chip 7.2 / 3.9), Game Gear 1.9 / 0.9 KB, Genesis 9.5 / 7.1 KB; real games: Titan Overdrive (600 frames in) 97 KB frozen / 69 KB running, the Master System test suite 24 / 15 KB.

## Corruption from a stockpile (2026-10-16, Round 5 `corr`): farm in BrokenNes, play the corrupted instruments in FL

**What it is:** the user farms a corruption in the desktop app, keeps it in a stockpile file, opens the file in the plugin's **Corruption** window (button between Inputs and Instrument Runaway), picks a corruption, **Start**s it on the loaded
ROM-mode game and uses Instrument Runaway on the result; a saved FL project keeps all of it. User-facing description: `Plugin/README.md` ("Corruption: farm in BrokenNes, play it in FL"); design `docs/projects/round5-corr-design.md`; record and numbers `docs/projects/round5-corr.md`.

- **The shared library** is `Windows/NesEmulator/corruption/` (namespace `NesEmulator.Corruption`; linked by `Core`, so the plugin; also by Workshop and the desktop): the stockpile file (`StockpileJson`, v2, `Utf8JsonWriter`/`JsonDocument`, no reflection, AOT-safe), memory domains
  (`MachineDomains.For(session)`: SNES WRAM/VRAM/CGRAM/OAM/SRAM/ROM/ARAM/DSP, Game Boy WRAM/HRAM/VRAM/OAM/CRAM/SRAM/ROM/WAVE, Master System / Game Gear and Genesis their RAM, VRAM, colour RAM, ...), `AnchorStart` (state / boot / none anchors),
  `ConsoleFarm` (records the pad and every corruption write of a console game, describes where the game is, starts an entry). Do not fork it for the plugin: anything the plugin needs from it is an additive change there, verified by `BrokenNes.Workshop --corrupt-test` (247 checks).
- **Plugin files:** `Corruption.cs` (`CorruptionPanel`: what the window shows between paints and what the worker threads set; `CorrRow`/`Fit`; `CorruptionSection`, tag `CRP1`), `Emulator.Corruption.cs` (open a stockpile on a worker, `CorruptionRows` = which entries fit,
  `StartCorruption`/`RunCorruptionStart`, `RebuildFromHistory`, `CaptureCorruptionSection`, test helpers), `Bn2Editor.cs` (`DrawCorruption`, `DrawCorruptionNote`, ids `IdCor`..`IdCorRow+4` = 500-524), `Game.cs` (`SessionGame.Farm`, the pad recorded in `RunFrame`).
- **A started corruption replaces the game:** `RunCorruptionStart` builds a NEW `SessionGame` around the session `AnchorStart` made (a saved state loaded into a fresh machine, or the replay of the recorded pads and writes from power-on), puts it in `pendingGame` and
  applies it under `romLock` between frames, so the audio thread keeps rendering the old game until the swap and **nothing on the audio thread allocates** (the `corruption` check measures 0 B). The CPU/sound unit factory is the plugin's own: an entry
  made with another sound unit is refused with the reason, never swapped in. "Freeze at once" calls `SetRunaway(true)` after the swap, so the instruments captured are the corrupted ones.
- **The farm** (`ConsoleFarm`, on `SessionGame.Farm`) records the pad per frame (`Farm.OnFrame`, allocation-free) and notes every write made to the game. Anything that makes the recording untrue **must poison it** (`Farm.Poison(reason)`): a test tone, a chip swap, Reset,
  a state put back by a project (`LoadGameState`). A new way to write into a running game must call `Farm.NoteWrites(...)` (`PokeWorkRam` and the test hook that pokes audio RAM do); otherwise a saved project replays a history that lacks the write.
- **What a project keeps:** `CRP1` goes after `GAM1` (`Bn2Plugin.SaveRestoreState` writes both, reads both in the section loop; an older DLL skips a tag it does not know). For a console that keeps its state (all of them now) the machine is in `GAM1` and `CRP1` only says where it came from; for a game that cannot (`!StateSupported`) or whose state the project could not hold (`stateNoteIsSave`: too big, refused) `CRP1` carries the farm's replay recipe
  (the boot anchor, or the one beside a saved state) and the reopened game is rebuilt on the loading thread by `RebuildFromHistory` (progress goes to `romStatus`). **Code against `StateSupported`, never a console name.** `CRP1` is a CRC-32 plus JSON, so a flipped byte is a refused section, not a changed game.
  The `corruption` check runs every flow for the SNES twice: with its real state, and as a console without one (hook 121, `SessionGame.NoStateForTests`, plus a stockpile entry whose anchor is the replay recipe).
- **Never silent:** `CorruptionSection.Read` returns `Damaged(reason)` instead of null, `StartLoad` shows it (`Corruption.SetProblem`, which stays over the picture; ordinary results show for 12 s), a project that said a corruption was started but did not keep the game's place says so.
- **Test hooks:** plugin 100-120 (open a stockpile, busy flags, rows and their fit flags, select, freeze, start/cancel/remove, the panel's texts, ARAM/WRAM/snapshot hashes, ARAM peek/poke, 120 = the editor stops running the game to keep its picture moving: **a test that counts frames
  must set it**, the editor otherwise advances the game about 60 frames a second while it is open), editor 140-144 (window open, note drawn, row count, scroll, the file the Open button takes instead of the dialog). Ids and meanings are in the code comments.
- **Pitfalls paid for:** a plugin instance that is created while another still exists joins its emulator (`Auto`), so a test that wants a *fresh* load of a project must dispose the instance that saved it first (the first versions of the check compared a machine with itself);
  `CorruptionRows` looks at the game that is about to run (`pendingGame ?? game`), because a game that is built but not yet swapped in is the one the user has loaded; the editor repaints on the panel's counters (`Signature`).
- **`corruption` check** (`SelfTest.Corruption.cs`; FruityHost references `Core` and links `Workshop/Corruption/SynthSnesRom.cs` for it): see the table in `Plugin/README.md` and the failure injections in the record. To run only it:
  `FruityHost selftest <dll> --only corruption` (about 14 s).
- **A new session's first frame must not allocate:** the SNES picture chip used to build a 128 KB colour table per brightness lazily, inside the first frame it drew, which is on the audio thread when a game is loaded from a state (no frames replayed on the worker); the tables are now built once per process (`PPU_SFC.brightnessLut`). The `corruption` check measures 0 B across a state-anchor start.
- **Not done:** starting a corruption on a **NES** game in the plugin (the library has the NES domains, the plugin's `NesGame` has no farm; the row says "the plugin starts corruptions on the console games") and `at > 0` timelines.

## Editor tabs (2026-10-17, Round 6 `ui`): Emulator | Instruments | Presets

**What it is:** a strip of three tabs on the header rule above the right column. **Emulator** = the editor as it was (904 x 664, same controls, same ids, same parameters; verified: golden control rectangles + 0 differing pixels outside the strip in six states).
**Instruments** and **Presets** widen the window by `UiLayout.WideExtra` (420 -> 1324 x 664). Both are shells on two interfaces: `Ui/Contracts.cs` (`IInstrumentModelProvider`, `IPresetLibrary`, `UiServices`); `Ui/StubModels.cs` holds demonstration
models that **only the self-test installs** (hook 403). User-facing description: `Plugin/README.md` ("Tabs: Emulator, Instruments, Presets"); record and evidence: `docs/projects/round6-ui.md`.

- **How the window grows (the contract question):** `IFruityEditor` has only `Hwnd` and `Destroy`, but FL's public SDK header (`fp_plugclass.h`) has the host call `FHD_EditorResized = 2` ("to notify the host that the editor (EditorHandle) has been resized").
  `Bn2Editor.ApplyWindowSize` does `SetWindowPos` on its own window, then `plugin.Host.Dispatcher(HostTag, Fhd.EditorResized, 0, 0)` (only when the width changes). `HostSim.EditorResized` (SynthHost) is what a host owner hooks (the test host fits its frame, `SynthRack.EditorResized` -> `SynthForm.FitEditorPanel`
  resizes the standalone synth's panel; checked on the real synth window: 904 -> 1324 -> 904). **Real FL was not tried:** whether FL fits its frame around the editor window after this call is the open question; if it does not, the right of the wide tabs is clipped.
- **Seams (keep them):** `Bn2Editor` is `partial`; everything of the tabs lives in `Ui/` (`Bn2Editor.Tabs.cs` = strip, resize, event routing, hooks; `InstrumentsPanel`, `PresetsPanel`; widgets `ScrollList` / `VScrollBar`, `Slider`, `Stepper`, `BitToggle`, `TextInput`, `HexField`, `WaveView` on `UiCanvas`).
  The editor file only gained: `Kind.Ui`, the `if (tab == 0) { ... } else DrawTabContent(...)` around the right column (no line of it re-indented), the Reset block, `Mix(UiSignature())`, the back buffer at `UiLayout.WideWidth`, and one-line routes in `OnMouse` / `WndProc` / `Test`.
  The real models register in their own files through `Ui/UiBoot.cs` (partial methods `RegisterInstrumentModel` / `RegisterPresetLibrary`, called when an editor is created).
- **Ids:** 1000-1999 are the tabs' (map in `Ui/UiIds.cs`); never reuse an id below 1000. Test hooks 400-499 through the editor (list in `Ui/Bn2Editor.Tabs.cs`). Typing: `WM_CHAR` / `WM_KEYDOWN` reach the focused text or hex field; while one has the keyboard the Inputs polling is off.
- **Pitfalls paid for:** `WM_MOUSEWHEEL` carries the cursor in SCREEN coordinates (the tabs convert with `ScreenToClient`; `FruityHost` `Win32Host.WheelAt` sends it that way). The Emulator tab's old wheel-over-a-slider code still reads the raw lParam as client coordinates
  (`OnMouse` sets `mouseX` from it first), so on that tab it hits whatever is at the screen position: left alone on purpose, that tab must not change; a latent bug. A model that throws while the tab paints is caught (a paint that never ends is repainted without end): `DrawTabContent` / `UiGuard`.
  `Test(30, id)` is also the self-test's only way to find a control: register every control with `UiCanvas.AddHit`.
- **Fault switches:** `UiMutate` (`Ui/UiIds.cs`) are single tests of a mask at the place a check watches; `ui-proof` sets them through hook 499 and demands the check fails. A new check on the tabs should get a switch and a line in `UiFaults` (`SelfTest.Ui.cs`).
- **Not done / not verified:** real FL (the resize call, typed characters and the wheel reaching a plugin window, a 1324-wide window on a small screen); no clipboard in the text field; the tab is not remembered when the window closes and opens again (it opens on Emulator); the models themselves (`imodel`, `presets`).

## Push into the game (2026-10-17, Round 6 `inject`): an edited instrument goes into the RUNNING game

**What it is:** the Instruments tab's **Push into the game** (and **Remove from game**) put the edit of the Runaway copy into the game's own sound memory / chip, so the game's music plays it after Runaway is switched off. Feasibility per console, measured on real games:
`docs/projects/round6-inject.md` (table first). User-facing: `Plugin/README.md` (Tabs section). Never silent: `PushOutcome.Message` says what stuck, what the driver overwrites, or why not (`GamePush.WhyNot` for the kinds that cannot be pushed).

- **Shape:** `Instruments/GamePush.cs` (records, `GamePush.Push/Remove/RemoveAll/Verify/Serialize/Restore`, `PushHooks.Install`) works on an `IConsoleSession` (no Emulator, no UI: the checks drive real games without the DLL); per console `GamePushSnes.cs`, `GamePushOpn.cs` (FM patch + DAC), `GamePushOther.cs` (Game Boy wave,
  YM2413 user instrument); `GamePushFile.cs` = project section **`INJ1`** (version, deflate, CRC-32; records with the ORIGINAL bytes + the Genesis hook's volume memory) and `PushMutants` (test-only deliberate bugs). Glue: `Emulator.Inject.cs` (under `EmulatorHub.Gate` then `romLock`, like `SetRunaway`; tells the corruption farm:
  memory writes -> `Farm.NoteWrites`, hooks -> `Farm.Poison`; `NotePushesLost` when the game is replaced), `Instruments/InstrumentPushHandler.cs` (`InstrumentPush.Handler`), `Bn2Plugin.Inject.cs` (test hooks 320-334), the tab: `IInstrumentModelProvider.IsPushed/RemoveFromGame/Notice` (default members), the Remove button and the standing note in `InstrumentsPanel.cs`.
- **The hooks in the shared emulator tree (all `null` by default, one null test, nothing in any machine state; goldens unchanged):** `DSP_SFC.Overrides` (`DspVoiceOverrides`, keyed on the voice's live SRCN register: envelope, tuning, volume weights, echo/noise/pmod flags; `DSP_SFC.WriteTap` is a probe), `Ym2612.Override` (`IYm2612Override`: `OnKeyOn` + `OnDacWrite`; `Ym2612.OverrideRegister`),
  `Ym2413.Override` (`OnKeyOn` on the user instrument), `APU_GB.Override` (`OnTrigger`). **A new hook must keep the default path byte-identical (prove it with the SNES/Genesis goldens) and allocation-free.**
- **Where a push goes:** SNES sample bytes + directory loop word -> the game's `APU_SFC.Aram` (`Farm.NoteWrites("ARAM", ...)`); everything the driver rewrites -> the hook. Genesis patches are matched by CONTENT (the original patch ignoring the carriers' total level), so every channel the game plays it on is edited; the carriers' level keeps the driver's volume
  (`TL = driver's + (edited - original)`, remembered per channel in `OpnPushHook.lastTl`, saved in INJ1). Records are per game (`GamePushes`, a ConditionalWeakTable on the session) and die with it.
- **Project:** `Bn2Plugin.SaveRestoreState` writes `INJ1` last and reads it in the section loop; the restore waits for the game (`Emulator.SetInjectRestore`, applied by `ApplyRestore` after the machine state is back; a game whose state was not restored drops the pushes and says so). `Game.cs` `CaptureSpc` carries the pushes' overrides into a new Runaway copy (`SnesPusher.Carry`) so FL's notes and the game agree after a re-freeze.
- **Checks:** `FruityHost inject-test [--only snes,genesis,gb,sms,other] [--no-mutants]` (no DLL; real games, FFT peaks, bit-identity, 80+ checks, a deliberate bug per check), `selftest --only inject` (through the DLL: 4 real games, save/reopen/damaged/newer/absent, remove, replaced game, farm, allocation, the tab with real clicks), `inject-probe snes|genesis` (what a driver rewrites).
- **Rules this added:** a push never changes the driver's registers (an override is applied where the chip reads them); a section is checked before it is trusted; every kind that cannot be pushed is refused with a reason, never half done; INJ1 stays the last section (an older build skips it by its length).
- **Not done / not verified:** real FL; hearing any of it (no sound card: every claim is a measured PCM or hash); Game Boy pulse/noise, PSG, NES, YM2413 ROM instruments (refused); a DAC sound the analyzer split into pieces is pushed per piece; a SNES game that reloads its sound set puts the original sample back (the overrides stay).

## State at the pause

- Host tests: `FruityHost selftest` = **53 results: 52 pass, 1 warning, 0 failed** (round 6 `ui` added `ui` and `ui-proof` = the editor's tabs; round 5 added `dpad` = d-pad protection, `prefs` = the settings migration and `corruption` = stockpile corruptions; `psg` is Sega track C1's, `md` and `rom-sms` C2's, `fm`, `rom-md`, `stems-sega` and `runaway-sega` C3b's) (the warning: the QLQ core plays an octave sharp on purpose).
- Real FL Studio 2026: certified **15/15** (headless render, pitch within 0.1 cent, slides ~2-3 cents) on the old single-instance build. The
  current build (emulator hub, editor, ROM mode for all consoles, Inputs, Instrument Runaway) was installed by the user and worked in manual
  use but was **not re-certified in FL** yet: run `pwsh UAT/certify.ps1 -Only plugin-fl` (FL must be closed) when development resumes.
- One FL instance = one channel of an emulator; instances share emulators through a static hub (`EmulatorHub`, Auto or #1..#8). Consoles NES /
  Game Boy / SNES / Master System / Game Gear / Genesis, Direct mode (private bare chip) or ROM mode (a game runs, picture in the editor). NES stems per channel, Sega games per voice
  (PSG, FM, DAC, rhythm); Game Boy / SNES games play through Mix. Instrument Runaway freezes a game: SNES = instrument picker over the game's BRR samples, Game Boy = the real channels
  with the game's duty/waveform, Sega = the game's chips on private copies (PSG, YM2413, YM2612 patches and the Genesis DAC's sounds); NES = freeze and silence only (no DMGS sampler either).

## Layout

| Project | What |
|---|---|
| `BrokenNes.Fruity` | FL native SDK mirror (vtable via `[UnmanagedCallersOnly]`), plugin base class, GDI editor base (`Win32Gdi.cs`) |
| `BrokenNes2.Plugin` | the plugin (Native AOT DLL `BrokenNes2_x64.dll`): `Bn2Plugin` (voices, params, state), `EmulatorHub` / `Emulator` / `Emulator.Rom` (shared emulators, rings, ROM mode), `Game.cs` (NesGame / SessionGame), `Bn2Editor` (self-drawn UI), `SnesSampler` / `GbSampler` (Runaway), `Bn2Input`, `Prefs`, `About` |
| `BrokenNes.SynthHost` | library: `HostSim` / `PluginInstance` / `PluginLibrary` (moved here from the test bench, namespace `BrokenNes.FruityHost` kept), `SynthRack` (four instances played live), `MidiRouter`. Used by the test bench and by the desktop's standalone synth |
| `BrokenNes.FruityHost` | the test bench that loads the DLL like FL does: `selftest`, `render`, `track`, `state`, `analyze`, `editor-shot`, `bmpdiff`, `editor-golden`, `makerom gb\|gbtone\|sfc`, `romtrace` |
| `../Core` | `BrokenNes.Core`: allocation-free NES audio path + `ApuStem` (per-channel stems from a Bus write tap) |
| `../UAT/plugin`, `../UAT/certify.ps1` | FL render certification, installer (`fl/install-to-fl.cmd`), the four-entrypoint certification |

Names: the plugin is called **BrokenNes2** everywhere (the name it reports to FL, the DLL `BrokenNes2_x64.dll`, the folder, FL's plugin identity, the editor header). Earlier builds reported a longer branded name; no user-visible text carries it any more.
Params: 13 (`Bn2Params.cs`; Emulator, Console, Instrument and Voice are the later additions). State version 2; trailers after the params: ROM path,
`EMU2` (emulator id + channel), then in Runaway one of `SPC1` (SNES sound memory), `GBS1` (Game Boy sound settings), `PSG1` / `OPL1` / `OPN1` (a Sega game's chips), then (ROM mode, any state) `GAM1`, the whole machine of the game (see "Project game state"). Older projects load defaults.

## Commands (Git Bash or PowerShell; build output goes OUTSIDE the repo)

```bash
export PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer"      # Native AOT needs the MSVC tools (vswhere)
A="$LOCALAPPDATA/VRUN_Nes_Dev_work/bn_build"
dotnet publish Plugin/BrokenNes2.Plugin/BrokenNes2.Plugin.csproj -c Release --artifacts-path "$A" --disable-build-servers
dotnet build   Plugin/BrokenNes.FruityHost/BrokenNes.FruityHost.csproj -c Release --artifacts-path "$A" --disable-build-servers
"$A/bin/BrokenNes.FruityHost/release/BrokenNes.FruityHost.exe" selftest "$A/publish/BrokenNes2.Plugin/release_win-x64/BrokenNes2_x64.dll" [--only runaway-gb,editor]
```

- Everything at once, with the report and a copy of the DLL in `Plugin/dist` (git-ignored): `pwsh UAT/certify.ps1 -Only plugin-host`.
  Just the DLL, no tests: `pwsh Plugin/build-dist.ps1`. The desktop build copies `Plugin/dist/BrokenNes2_x64.dll` in (and **publish** ships it as a loose file:
  `ExcludeFromSingleFile`, otherwise the single-file exe swallows it).
- Install into FL: the desktop app, **Config > Synthesizer Mode > Install to FL Studio...** or `BrokenNes.Windows.exe --install-vst` (**needs a UAC click**, so an
  agent cannot do the real thing; FL must be closed). `install-to-fl.cmd` is a FL-2026-only shortcut. FL render
  certification: `-Only plugin-fl` (~70 s, never kills FL by name, refuses when FL is open).
- `selftest` drives real windows with real mouse messages: keep the desktop quiet while it runs (`runaway-gb` failed once when other processes were
  running, passed alone and in two further full runs).
- Editor screenshots: `FruityHost editor-shot <dll> out.bmp --console 1 --rom game.gb --runaway [--open instrument|duty|channel|inputs]`; the tabs: `--demo --mode rom --freeze --tab instruments [--click 1212,1181] [--chars text]` (the `.bmp` is 32-bit top-down; `bmpdiff` compares two).
- Test hooks into the plugin and editor: `Fpd.TestBase + n` (editor 30-39, plugin 40-51: reset, SetPad, runaway, test snapshot, ...). They
  exist so tests can drive the real editor with real mouse messages; keep new UI testable the same way.

## Rules this tree follows

- **Edits in the shared emulator tree must stay additive** (BrokenNes desktop/web must not change behaviour). The plugin's hooks there:
  `Windows/NesEmulator/board/Bus.cs` + `NES.cs` (APU write tap), `systems/ConsoleSessions.cs` (`SnesSession.Apu`, `GbSession.Apu`).
  The plugin links that tree: after changing core/session APIs (`IConsoleSession`, `APU_GB`, `APU_SFC`/`DSP_SFC`, `CoreCatalog`) run `selftest`.
- No allocation on the audio thread (a test measures 0 bytes). Per-instance state is private; shared state lives under `EmulatorHub.Gate` / `romLock`.
- Verify audio claims by measuring (tests render real audio and measure pitch/RMS), not by reading code.

## Pitfalls we already paid for

- SDK structs mix `#pragma pack` (`TFruityPlugInfo` is pack 4). FL slide notes lie INSIDE the parent note (flags 0x4008, release byte 0, group 0).
- FL templates: `make_template.py` splices FL's own `Vocoder.flp` (path under `C:\Program Files\Image-Line\FL Studio 2026`); select pattern 1
  (event 67), strip the mixer effect plugins, and never delete channel blocks (FL hangs).
- `python3` in Git Bash is the Windows Store stub and hangs: use `python`. In the Claude tool shell, very long heredocs fail: write scripts to files.
- A test that leaves an instance alive keeps its claim on emulator #1/channels: dispose instances before the next scenario.
- The pulse waveform restarts when the period high byte changes (bends across period 256 click): known, see "Ideas parked".
- Git: the stray tag named `main` was deleted on 2026-10-02; if `main` is ever ambiguous again, use `refs/heads/main` when scripting.
- A NES channel only has whole period steps (~8 cents apart at 500 Hz): compare measured pitch with `Nes.PeriodHz(Nes.Period(cents, triangle))`, not with the ideal 12-TET frequency.
- The plugin may call `Voice_Kill` from the editor's thread (Reset Console). `HostSim` therefore guards its voice table and a live host sets `DeferFree`,
  freeing a killed voice's levels block on the audio thread (`FreeDeferred`); never free it from the callback.
- UI Automation: a MessageBox owned by a WinForms window sits UNDER that window in the UIA tree (search `Descendants`, not `Children`). A system OK-only box
  cannot take UIA focus; `CloseMainWindow()` / WM_CLOSE dismisses it. Menu items that open a modal dialog hang `Invoke()` (see `UAT/lib/UiaHelpers.ps1`).
- This harness refuses `Remove-Item` on unresolved variables and on scratchpad globs: use fresh uniquely named folders instead of cleaning up.

## Outside the repo (machine-specific)

- FL Studio install path and `Plugins\Fruity\Generators\BrokenNes2\` (installer target); FL's `Vocoder.flp` as the render template.
- `%LOCALAPPDATA%\VRUN_Nes_Dev_work\` (build output, certification work dirs). `certify.ps1` has a default VRUN ROM path in `VRUN_Nes_Dev`.
- The plugin's remembered settings: registry `HKCU\Software\BrokenNes\BrokenNes2` (About shown, input mappings, the d-pad setting); an older key is copied over once and never changed (see `Prefs.cs`); tests use `BROKENNES_PREFS_FILE` (and `BROKENNES_PREFS_OLD_FILE` / `_REGKEY` / `_OLD_REGKEY` for the migration check).
- Spec for the ROM-driven mode: the VRUN project's `docs/26-live-audio-bridge-spec.md` (not copied here).

## Ideas parked

- ROM mode ignores FL's notes until a VRUN live-driver ROM exists (the spec above).
- Defer the pulse period-high-byte write to a sequencer phase to remove the bend click; sleep idle chips to save CPU.
- NES Instrument Runaway sampler; Game Boy Runaway for the DMGS chip; per-channel stems for Game Boy / SNES games.
- Real SNES / Game Boy games were never tried (only the tiny ROMs the tests build): if a game stays silent in FL, start with `romtrace` and
  the `BROKENNES_PLUGIN_LOG` log.
- The SNES instrument list is a heuristic read of the sample directory, not a certainty.
