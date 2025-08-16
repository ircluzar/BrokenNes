<p align="center">
  <img src="wwwroot/nes-favicon.svg" width="96" height="96" alt="BrokenNes logo" />
</p>

# BrokenNes

A web-based NES emulator prototype built with Blazor WebAssembly (.NET 10 preview). It focuses on clarity and approachability while experimenting with performance-oriented options (AOT, SIMD, trimming) in modern .NET.

## Status
Early work-in-progress. Expect incomplete hardware features, potential timing inaccuracies, and rough edges in the UI.

## Features (current / partial)
- 6502 CPU implementation
- PPU scanline-based rendering (experimental)
- Basic APU scaffolding
- Mapper support: 0,1,2,3,4,5,7 (in various states)
- Input handling
- Blazor WASM front-end with simple status bar

## Roadmap (short-term)
- Improve PPU correctness & palette handling
- APU audio stabilization
- Save state & SRAM persistence
- Additional mappers & edge case tests
- Performance profiling & frame pacing

## Development
Prerequisites: .NET 10 preview SDK.

Run the dev server:
```bash
 dotnet run
```
Open the app in a browser (default: http://localhost:5000 or the HTTPS variant the dev server prints).

## Project layout
- `NesEmulator/` core emulation code
- `Pages/` Blazor pages (e.g. `Nes.razor` for the emulator view)
- `Shared/` shared UI components
- `wwwroot/` static assets (ROM test file, favicon, scripts)

## SoundFont Core Switching (WF vs MNES)
BrokenNes supports two SoundFont playback paths for APU note events:
- WF: Lightweight WebAudio oscillator / optional sampled instruments (`nesSoundFont`).
- MNES: FluidSynth (SF2) via js-synthesizer AudioWorklet (`mnesSf2`).

Routing is gated by a global active core flag managed in JS (`nesInterop.setActiveSoundFontCore`). Only the selected core receives note events unless layering is explicitly enabled (debug toggle in the Debug panel). A debug badge shows the active core (WF, MNES, None). Use the Flush button to immediately silence lingering tails when switching.

Troubleshooting double audio:
1. Open browser console and run `nesInterop.debugReport()`.
2. If both wf and mnes counters increment while layering is off, the active-core flag wasn't set early enough—toggle APU core again or flush.
3. Use the Flush button (or `nesInterop.flushSoundFont()`) then reselect desired core.

Program mapping docs: see `docs/soundfont-mapping.md` (stub) for channel->program conventions.

## Core lifecycle and lazy cores
- Cores are discovered by name (CPU_*, PPU_*, APU_*) and created via a small factory.
- CPU is eager, while PPU/APU are created on first use to avoid large startup allocations.
- PPU exposes `ClearBuffers()` to drop frame buffers when hot-swapping or after load.
- APU exposes `ClearAudioBuffers()` and `Reset()` to drop queued audio and restart pacing without reallocation.
- SaveState omits the framebuffer and uses pre-serialized subsystem JSON for AOT/WASM builds.

See `docs/core-lifecycle.md` for details.

## Contributing
Don't even bother

## License
[Digital Lifeform License 1.1](LICENSE.txt)

## Disclaimer
The jank is normal.
