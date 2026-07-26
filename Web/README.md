# BrokenNes Web

A deliberately minimal **Blazor WebAssembly** front end for the BrokenNes emulator: load a ROM,
run it, see video, hear audio, press buttons. Nothing else.

This is the revived descendant of the old `webonly` branch (the pre-desktop Blazor prototype),
rebuilt as a thin shell over the *current* cores rather than a fork of the old app.

## What it is / isn't

**Is:**

- Every current CPU / PPU / APU core, hot-swappable at runtime.
- The web-exclusive **clock system** (`CLOCK_CLR`, `CLOCK_FMC`, `CLOCK_TRB`), selectable and persisted.
- Keyboard input, WebAudio output, 2D canvas video.
- ROM loading from `wwwroot/roms/` or the user's disk via a file picker.

**Isn't** — all deliberately dropped:

- Shaders. The old web shaders were GLSL, the desktop is HLSL; there is no shader pipeline here at all.
- Achievements, deck builder, cards, levels, story, ROM masquerade.
- Corruptor / RTC / Glitch Harvester / Imagine / benchmark / savestate metagame.

## Relationship to the desktop app

`Windows/NesEmulator/` is the **single source of truth** for all cores. This project links those
files with `<Compile Include>`; it does not copy or fork any of them. A change to a core is
picked up by both apps.

The desktop solution is untouched — `BrokenNes.sln` still contains only
`Windows/BrokenNes.Windows.csproj`, so a bare `dotnet build` at the repo root still means
"build the desktop app". This project is built explicitly by path.

Regression check that the coupling stays one-directional:

```bash
git status --porcelain -- Windows/     # must be empty after any work here
```

### Why the old `Emulator*.cs` was not revived

`Windows/NesEmulator/board/Emulator*.cs` is the old Blazor UI shell. It is retained in the repo
but excluded from the desktop build, and it is *not* used here either. It is a single partial
class spread over 9 files whose constructor hard-requires `IShaderProvider`, `GameSaveService`
and `InputSettingsService`, and whose siblings pull in achievements, the corruptor, Imagine and
the benchmark UI. Reviving it would mean reviving exactly the feature families this project
exists to omit. `Web/Emulation/Emulator.cs` (~270 lines) replaces it.

Note that those excluded files have drifted and no longer compile (e.g. `NES.GetSavedRomName`
became static; `ClearPendingDeckContinueAsync` changed arity). Nothing here depends on them.

## Layout

```
Web/
├── BrokenNes.Web.csproj      # links the shared cores; see the <Compile Include> block
├── Program.cs                # one DI registration: HttpClient
├── App.razor, _Imports.razor
├── Emulation/Emulator.cs     # the thin host: clock plumbing, frame loop, input, JSInvokable surface
├── Pages/Nes.razor           # the only page
├── LinkerConfig.xml          # only used if PublishTrimmed is enabled
└── wwwroot/
    ├── index.html
    ├── css/app.css
    ├── lib/nesInterop.js     # video/audio/input/IndexedDB. No WebGL, no shaders.
    └── roms/demo.nes         # boot ROM
```

## Run

```bash
dotnet run --project Web/BrokenNes.Web.csproj --urls http://localhost:5011
```

Publish:

```bash
dotnet publish Web/BrokenNes.Web.csproj -c Release -o Web/pub
```

The published output is a static site — serve `Web/pub/wwwroot` from anything. Note the app is
routed at `/`, so serve the directory root; hitting `/index.html` directly renders the router's
NotFound page.

Controls: D-pad `Arrows`/`WASD`, A `X`, B `Z`, Select `Space`, Start `Enter`.

## Performance and AOT

Build configuration matters enormously here — far more than core choice.

| Build | Median frame | Effective FPS |
|---|---|---|
| Debug (dev server) | 78 ms | ~13 |
| Release, no AOT | 23 ms | ~35–43 |
| Release, no AOT, `SPD/SPD/SPD` cores | 20 ms | ~51 |

**Always measure in Release.** Debug is ~3.4x slower and is not representative.

To close the remaining gap to 60 fps, enable AOT. It needs the `wasm-tools` workload, which
runs an MSI and therefore must be installed from an **elevated** terminal:

```powershell
dotnet workload install wasm-tools
```

Then build with AOT on:

```bash
dotnet publish Web/BrokenNes.Web.csproj -c Release -p:EnableWasmAot=true -o Web/pub
```

(`EnableWasmAot` defaults to `false`; leaving it on without the workload fails with `NETSDK1147`.)

## Gotchas worth knowing

These each cost real debugging time; they are load-bearing, not style.

- **`board/` must stay an explicit file list, never a glob.** Globbing it pulls in the old Blazor
  shell and detonates the build with errors about `BrokenNes.Services`, `ImagineModelLoaded`, etc.
- **Never write the fingerprint placeholder token anywhere in `index.html` except the real script
  tag** — not even inside a comment. The SDK rewriter substitutes the first occurrence it finds
  and leaves the real tag alone, which 404s at boot with no build error.
- **ROMs must be physical files under `wwwroot/roms/`.** A `<Content Include Link=...>` pointing
  outside the project cone publishes fine but serves `Content-Length: 0` under the dev server.
- **`NES.Get*CoreId()` returns the CLR type name (`CPU_SPD`) but `Set*Core()` and
  `CoreRegistry.*Ids` use the bare suffix (`SPD`).** `Emulator.StripCorePrefix` reconciles them.
- **`PublishTrimmed` is off deliberately.** `CoreRegistry`, `ClockRegistry` and
  `NullProviderRegistry` all discover by `Assembly.GetTypes()` + name prefix and swallow their
  own failures, so trimming them away yields a silent black screen. Turn it on only with
  `LinkerConfig.xml` wired up, and re-verify the clock dropdown still lists all three.
- **`MSBuildEnableWorkloadResolver=false` is deliberately absent.** It silently turns
  `RunAOTCompilation` into a no-op with no warning.
- **`APU_WF` and `APU_MNES` are desktop-only** (winmm MIDI / filesystem SF2 probe). They are
  linked because `NES.cs` references them by type name, and they appear in the dropdown labelled
  accordingly, but they produce silence in a browser. They are why `NAudio.Midi` and `MeltySynth`
  are referenced.
- **`CLOCK_CLR` and `CLOCK_TRB` can wedge the tab** when a frame overruns its budget: their wait
  branch (which holds every `Task.Delay`/`Task.Yield`) is skipped when perpetually behind, and the
  fallback yields only every 16 frames. On WASM's single thread that starves the renderer. They
  are labelled experimental in the UI. `FMC` is the default and is always safe.
- **`nullproviders/`: only 7 of 26 are linked.** The other 19 `using BrokenNes.Windows.Rendering`
  (`ColorMath`), which lives outside the emulator tree. Discovery is reflection-based so the rest
  are simply absent. At least one concrete provider must remain — `NullProviderRegistry` throws if
  the discovered list is empty.
- **`wwwroot/roms/test.nes` renders pure black forever.** It is a launcher stub the old shells
  painted TV static over. `demo.nes` is the working boot ROM.
