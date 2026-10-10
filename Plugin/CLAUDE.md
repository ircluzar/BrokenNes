# Plugin/ - Bogue :: BrokenNes 2 (FL Studio native plugin)

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

## State at the pause

- Host tests: `FruityHost selftest` = **47 checks: 46 pass, 1 warning, 0 failed** (`psg` is Sega track C1's, `md` and `rom-sms` C2's, `fm`, `rom-md`, `stems-sega` and `runaway-sega` C3b's) (the warning: the QLQ core plays an octave sharp on purpose).
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
| `BrokenNes.FruityHost` | the test bench that loads the DLL like FL does: `selftest`, `render`, `track`, `state`, `analyze`, `editor-shot`, `makerom gb\|gbtone\|sfc`, `romtrace` |
| `../Core` | `BrokenNes.Core`: allocation-free NES audio path + `ApuStem` (per-channel stems from a Bus write tap) |
| `../UAT/plugin`, `../UAT/certify.ps1` | FL render certification, installer (`fl/install-to-fl.cmd`), the four-entrypoint certification |

Names: the plugin is branded "Bogue :: BrokenNes 2" but the DLL, folder and FL's plugin identity stay `BrokenNes2` (`::` is not valid in file names).
Params: 13 (`Bn2Params.cs`; Emulator, Console, Instrument and Voice are the later additions). State version 2; trailers after the params: ROM path,
`EMU2` (emulator id + channel), then in Runaway one of `SPC1` (SNES sound memory), `GBS1` (Game Boy sound settings), `PSG1` / `OPL1` / `OPN1` (a Sega game's chips). Older projects load defaults.

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
- Editor screenshots: `FruityHost editor-shot <dll> out.bmp --console 1 --rom game.gb --runaway [--open instrument|duty|channel|inputs]`.
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
- The plugin's remembered settings: registry `HKCU\Software\Bogue\BrokenNes2` (About shown, input mappings); tests use `BROKENNES_PREFS_FILE`.
- Spec for the ROM-driven mode: the VRUN project's `docs/26-live-audio-bridge-spec.md` (not copied here).

## Ideas parked

- ROM mode ignores FL's notes until a VRUN live-driver ROM exists (the spec above).
- Defer the pulse period-high-byte write to a sequencer phase to remove the bend click; sleep idle chips to save CPU.
- NES Instrument Runaway sampler; Game Boy Runaway for the DMGS chip; per-channel stems for Game Boy / SNES games.
- Real SNES / Game Boy games were never tried (only the tiny ROMs the tests build): if a game stays silent in FL, start with `romtrace` and
  the `BROKENNES_PLUGIN_LOG` log.
- The SNES instrument list is a heuristic read of the sample directory, not a certainty.
