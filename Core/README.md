# BrokenNes.Core: plugin mode

BrokenNes runs three ways. They all use the same cores, in `Windows/NesEmulator/`. That folder is
the only copy; every project links from it.

| Mode | Project | Notes |
|---|---|---|
| Desktop | `Windows/BrokenNes.Windows.csproj` | WinForms, NAudio |
| Web (Lite) | `WebLite/`, `Web/` | Blazor WebAssembly |
| **Plugin** | `Core/BrokenNes.Core.csproj` | a `net10.0` class library that is AOT-compatible; the BrokenNes2 FL Studio plugin (`Plugin/`) compiles it into a Native AOT DLL |

Plugin mode has two entrypoints of its own: the DLL **inside FL Studio**, and the same DLL inside the **test host**
(`Plugin/BrokenNes.FruityHost`) that certifies it outside FL. `UAT/certify.ps1` runs all four entrypoints; see
`Plugin/README.md`.

Plugin mode adds nothing to the other two. The files in this folder are only compiled here. The
shared-tree changes made for plugin mode are additive:
- `IAPU.ReadSamples`, which has a default implementation;
- `NES.ActiveApu`;
- a direct call instead of reflection in `NES.SetApuChannelEnableMask`;
- cached addressing-mode delegates in `CPU_FIX`.

## What is in it

- **Every core:**
  - NES: CPU, PPU, APU, mappers, MMC5;
  - SNES and Game Boy;
  - the cross-console bridges (`APU_DMG`/`APU_DMGS`: NES music on the Game Boy sound chip;
    `APU_SNES`: NES music on the S-DSP);
  - the BrokenNes 2 console sessions.
- **The pure-emulation `APU_WF` / `APU_MNES`** (the `.Web.cs` siblings): they make PCM
  themselves, with no Windows MIDI and no SoundFont file.
- **`ILLink.Descriptors.xml`**, embedded: it keeps the whole assembly when a host is trimmed or
  AOT-compiled. Cores are found by reflection (`CoreRegistry`), so this is what keeps every core
  id selectable.
- **`NesEmulator.Plugin.NesAudioMachine`:**
  - a headless NES for an audio host: one ROM, one frame at a time, sound pulled at the host's
    rate;
  - FIX cores and Workshop's `--strict` settings by default, with real NTSC frame timing;
  - RAM by `.mlb` symbol name, PRG pokes, and APU hot swap;
  - with `CaptureApuWrites` on, the game's writes to `$4000-$4017` during each frame are logged with the APU clock
    (`ApuLog`), through a tap in `Bus` that costs one null check per APU write when off. A host replays them on
    `ApuStem`s.
- **`NesApuInstrument`:** a bare NES sound chip as an instrument: the host writes `$4000-$4017` and pulls audio at its
  own sample rate, with no ROM or CPU program. Registers can change between any two blocks (BrokenNes2 does it every
  64 samples), so pitch bends are smooth. Any APU core can sit behind it and be swapped while sounding. Steps the APU
  32 cycles at a time (the speed-hack cores sample at the end of a step).
  `Advance(cycles)` / `ReadAvailable` run it by CPU cycles instead of by output samples (for stems), and a constructor
  `enableMask` gives it just one channel.
- **`ApuStem`:** one channel of a running game on its own: a bare chip that receives only that channel's registers (plus
  `$4015` masked to it and the shared `$4017`), replayed at the clock the game made them. Its sound is the channel as
  the game plays it; summed, the four stems match the game's mix (correlation 0.998 on VRUN). BrokenNes2's ROM mode gives
  each channel's stem to its own FL instance.
- **`MlbSymbols`:** Mesen / NESFab label files.
- **`StreamResampler`:** converts a core's native rate (NES 44.1 kHz, Game Boy bridge 48 kHz,
  S-DSP 32 kHz) to the host's.

## Real-time rules

The plugin calls `NesAudioMachine.ReadSamples` on the audio thread, so steady state must not
allocate (garbage collections are audible).

Measured in a Native AOT test (`VST_Dev/spikes/CoreCheck`):
- 10 s of `vrun_audioplayer.nes` at 48 kHz, in 512-sample blocks;
- 2.9-4.4x real time for every core.

| | Cores |
|---|---|
| **0 bytes in steady state** | CPU FIX with APU FIX, FIXS, FMC, HI, HI2, HI2X, LOW, LQ2, ULQ |
| Still allocate through the default `ReadSamples` (about 180 KB/s) | EIL, LQ, MNES, QLOW, QLQ, QLQ2, QN, SPD, SPD2, WF, DMG, DMGS, SNES |

The second group needs its own `ReadSamples`. To add one:
- read the core's ring straight into the span;
- if the class re-implements `IAPU` (as `APU_FIXS` does), declare it there with `new`, or the
  interface maps to the base class.

The other CPU cores still allocate one delegate per instruction (the `CPU_FIX` fix has not been
applied to them). Plugin mode uses `CPU_FIX`.

## Building

```bash
dotnet build Core/BrokenNes.Core.csproj -c Release
```

A host references it with a `ProjectReference` and publishes with `PublishAot`. The Native AOT
linker needs the MSVC tools; if it fails with "vswhere.exe is not recognized", add
`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to `PATH`.
