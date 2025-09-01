<p align="center">
  <img src="wwwroot/nes-favicon.svg" width="96" height="96" alt="BrokenNes logo" />
</p>

# BrokenNes

A browser-based NES emulator and corruption toolkit built with Blazor WebAssembly (.NET 10). It aims for approachability while experimenting with modern .NET performance options.

## DeckBuilder

An achievement-based card-like game where you construct and experiement with emulator parts while playing a game of your choice.

## Status
Early work-in-progress. Expect incomplete/experimental hardware features, timing quirks, and some UI jank (some of it intentional).

## What’s here (current/partial)
- Emulation
  - 6502 CPU core(s)
  - PPU with scanline/event scheduling experiments
  - APU with two playback paths: raw PCM and SoundFont synth
- SoundFont synth mode (toggle in Debug)
  - Two selectable synth cores: WF (oscillators/sampled) and MNES (SF2/AudioWorklet)
  - Optional layering, overlay counters, dev logging, and instant flush
- Mappers implemented (various states): 0, 1, 2, 3, 4, 5, 7, 9, 33, 90, 228 (+ SPD variants for 1 and 4)
- Core system & pickers
  - Multiple CPU/PPU/APU/Clock implementations discoverable at runtime
  - UI pickers with ratings/perf/category metadata; hot-swappable at runtime
  - Shader picker with a growing set of post-processing passes
- ROM Manager
  - Built-in ROM entries plus user uploads (.nes), drag & drop import
  - IndexedDB-backed persistence, search/filter, reload, delete uploads
- Save states & SRAM
  - Quick Load/Save (also available in mobile fullscreen controller view)
- Tools/Debugging
  - Real-Time Corruptor (RTC) with intensity, domains, and auto-corrupt
  - Glitch Harvester (stash/stockpile, replay, export/import)
  - Benchmarks modal (weighted runs, history, timeline, diff view, copy)
  - Mini debug panel (state dump, event scheduler toggle)
- Input
  - Keyboard, gamepad, and mobile touch controller (fullscreen)

## Pages and flows
- Home: title screen with Deck Builder, Emulator, Options, About (health warning gate for audio)
- Nes: main emulator view (ROM Manager, RTC, Glitch Harvester, Debug, Achievements panel)
- Options: volume sliders (master/music/sfx), reset core prefs to FMC, save editing helpers, feature unlock toggles
- Cores: gallery/list of unlocked cores (CPU/PPU/APU/Clock/Shaders) with grouping/sorting
- Continue: meta flow integration (used by achievements/story)
- Input/InputSettings: configure players (keyboard/gamepad), view bindings
- DeckBuilder/Story: meta-progression and intro flow

## Run locally
Prerequisites: .NET 10 SDK and a modern browser.

Run the dev server from the repo root:
```bash
 dotnet run
```
Open the app in a browser (default: http://localhost:5000 or the HTTPS variant the dev server prints).

## Build & publish
- Debug build: use your IDE task or `dotnet build -c Debug`
- Optional diagnostic define: add `-p:EnableDiagLog=true` to append `DIAG_LOG` to DefineConstants

## Project layout
- `NesEmulator/` core emulation (CPU/PPU/APU, clocks, mappers, shaders, retro achievements)
- `Pages/` Blazor pages (e.g., `Nes.razor` is the emulator view)
- `Layout/` shared shell and navigation
- `wwwroot/` static assets (icons, audio, scripts)
- `Tools/` shader generator wiring (used at build)

## SoundFont synth mode (WF vs MNES)
Two SoundFont playback paths are available when SoundFont Mode is enabled from Debug:
- WF: lightweight WebAudio oscillators with optional sampled instruments
- MNES: SF2 synthesizer running in an AudioWorklet

You can switch the active core, enable layered mode for comparison, show an on-screen overlay, and flush all voices instantly to avoid lingering tails.

## Core lifecycle (high level)
- Cores are discovered by name (CPU_*, PPU_*, APU_*) and created via a small factory
- CPU is eager; PPU/APU are created on first use to keep startup light
- PPU and APU expose clear/reset helpers for hot swapping and recovery
- Save states omit large transient buffers and use compact DTOs in WASM builds

## Contributing
Don't even bother

## License
[Digital Lifeform License 1.1](LICENSE.txt)

## Disclaimer
The jank is normal.
