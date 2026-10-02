# Plugin/ - Bogue :: BrokenNes 2 (FL Studio native plugin)

Handoff note for whoever works here next (human or agent). **Development is paused; the state is very good.** Read `Plugin/README.md` for the
full description of what the plugin does and how each test proves it; this file is the practical rest: where things are, how to run them, what bites.

## State at the pause

- Host tests: `FruityHost selftest` = **37 checks, all pass** (one expected warning: the QLQ core plays an octave sharp on purpose).
- Real FL Studio 2026: certified **15/15** (headless render, pitch within 0.1 cent, slides ~2-3 cents) on the old single-instance build. The
  current build (emulator hub, editor, ROM mode for all consoles, Inputs, Instrument Runaway) was installed by the user and worked in manual
  use but was **not re-certified in FL** yet: run `pwsh UAT/certify.ps1 -Only plugin-fl` (FL must be closed) when development resumes.
- One FL instance = one channel of an emulator; instances share emulators through a static hub (`EmulatorHub`, Auto or #1..#8). Consoles NES /
  Game Boy / SNES, Direct mode (private bare chip) or ROM mode (a game runs, picture in the editor). NES stems per channel; Game Boy / SNES
  games play through Mix. Instrument Runaway freezes a game: SNES = instrument picker over the game's BRR samples, Game Boy = the real channels
  with the game's duty/waveform; NES = freeze and silence only (no DMGS sampler either).

## Layout

| Project | What |
|---|---|
| `BrokenNes.Fruity` | FL native SDK mirror (vtable via `[UnmanagedCallersOnly]`), plugin base class, GDI editor base (`Win32Gdi.cs`) |
| `BrokenNes2.Plugin` | the plugin (Native AOT DLL `BrokenNes2_x64.dll`): `Bn2Plugin` (voices, params, state), `EmulatorHub` / `Emulator` / `Emulator.Rom` (shared emulators, rings, ROM mode), `Game.cs` (NesGame / SessionGame), `Bn2Editor` (self-drawn UI), `SnesSampler` / `GbSampler` (Runaway), `Bn2Input`, `Prefs`, `About` |
| `BrokenNes.FruityHost` | the test bench that loads the DLL like FL does: `selftest`, `render`, `track`, `state`, `analyze`, `editor-shot`, `makerom gb\|gbtone\|sfc`, `romtrace` |
| `../Core` | `BrokenNes.Core`: allocation-free NES audio path + `ApuStem` (per-channel stems from a Bus write tap) |
| `../UAT/plugin`, `../UAT/certify.ps1` | FL render certification, installer (`fl/install-to-fl.cmd`), the four-entrypoint certification |

Names: the plugin is branded "Bogue :: BrokenNes 2" but the DLL, folder and FL's plugin identity stay `BrokenNes2` (`::` is not valid in file names).
Params: 12 (`Bn2Params.cs`; Emulator, Console and Instrument are the later additions). State version 2; trailers after the params: ROM path,
`EMU2` (emulator id + channel), `SPC1` (SNES sound memory), `GBS1` (Game Boy sound settings). Older projects load defaults.

## Commands (Git Bash or PowerShell; build output goes OUTSIDE the repo)

```bash
export PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer"      # Native AOT needs the MSVC tools (vswhere)
A="$LOCALAPPDATA/VRUN_Nes_Dev_work/bn_build"
dotnet publish Plugin/BrokenNes2.Plugin/BrokenNes2.Plugin.csproj -c Release --artifacts-path "$A" --disable-build-servers
dotnet build   Plugin/BrokenNes.FruityHost/BrokenNes.FruityHost.csproj -c Release --artifacts-path "$A" --disable-build-servers
"$A/bin/BrokenNes.FruityHost/release/BrokenNes.FruityHost.exe" selftest "$A/publish/BrokenNes2.Plugin/release_win-x64/BrokenNes2_x64.dll" [--only runaway-gb,editor]
```

- Everything at once, with the report and a copy of the DLL in `Plugin/dist` (git-ignored): `pwsh UAT/certify.ps1 -Only plugin-host`.
- Install into FL: `UAT/plugin/fl/install-to-fl.cmd` (**needs a UAC click**, so an agent cannot do it; FL must be closed). FL render
  certification: `-Only plugin-fl` (~70 s, never kills FL by name, refuses when FL is open).
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
- Git: a tag named `main` exists next to the branch `main`; use `refs/heads/main` when scripting.

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
