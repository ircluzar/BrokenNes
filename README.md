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

## ROMs
You must supply your own legally obtained NES ROMs. The included `test.nes` (if present) is for internal testing only.

## Contributing
Lightweight contributions welcome (bug reports, small fixes). Larger changes: please discuss first via an issue outlining intent.

## License
[Digital Lifeform License 1.1](LICENSE.txt)

## Disclaimer
This project is an educational emulator experiment and is not affiliated with or endorsed by Nintendo.
