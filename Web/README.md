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

AOT is the whole story here — build configuration matters far more than core choice, and
interop marshaling (the JS↔.NET boundary) turned out **not** to be the bottleneck despite
looking like an obvious suspect.

| Build | Emulation only¹ | Full `FrameTick` round-trip² | Effective FPS |
|---|---|---|---|
| Debug (dev server, no AOT) | ~24.7 ms | ~25.5 ms | ~13 |
| Release, no AOT (native-relinked) | ~24.7 ms | ~25.5 ms | ~35–51 (core-dependent) |
| **Release, AOT + trimmed** | **~2.9 ms** | **~4.3 ms** | **60 (pacing-gate capped)** |

¹ `SetInputs`+`RunFrame()` only, timed server-side in a loop — no interop, no array fetch.
² The real `[JSInvokable] FrameTick()` call as JS sees it, including the JSON marshaling of
the framebuffer + audio payload back across the WASM boundary.

The gap between columns 1 and 2 (interop marshaling cost) is consistently **~1–1.4 ms**
regardless of build — it does not scale with the rest. AOT is an **8.4x** speedup on the
emulation itself (2.9 ms vs 24.7 ms), which is what actually closes the gap to 60 fps; the
`CLR`/`TRB` boundary crossing was a red herring, not the bottleneck. `Web/Emulation/Emulator.cs`
exposes two diagnostic `[JSInvokable]` methods (`BenchEmulationOnlyMs`, `BenchEmulationPlusFetchMs`)
if you want to re-run this split yourself after a future change.

**Always measure in Release.** Debug never gets AOT (see the csproj) and is not representative.

AOT needs the `wasm-tools` workload, which runs an MSI and therefore must be installed from an
**elevated** terminal:

```powershell
dotnet workload install wasm-tools
```

Then build with AOT on — this is now the recommended way to actually ship/deploy this app:

```bash
dotnet publish Web/BrokenNes.Web.csproj -c Release -p:EnableWasmAot=true -o Web/pub
```

(`EnableWasmAot` defaults to `false` so the project still builds on a machine without the
workload; leaving it on without the workload fails with `NETSDK1147`. It also flips
`PublishTrimmed` on — see the Gotchas entry below on what that requires.)

One side effect worth knowing: `CLOCK_CLR` and `CLOCK_TRB` were previously **unsafe** (busy-wait
could hang the tab — see the entry below) specifically because per-frame cost exceeded their
16.7 ms budget. With AOT's 2.9 ms cost they're comfortably under budget and no longer hang —
verified by switching to each live. `TRB` in particular is intentionally *uncapped* (not
60 fps-gated like `FMC`/`CLR`) and will now run around 2x real speed once frame cost is cheap;
that's existing, documented `TRB` behavior, not a bug introduced here.

The frame loop paces itself to 60 fps regardless of display refresh rate (`nesInterop.js`
`startEmulationLoop`), and `playAudio` time-stretches each buffer to the actual gap between
calls rather than assuming a fixed 60 Hz cadence. Below ~30 emulated fps the stretch clamp
(max 2x slowdown, to keep pitch shift bearable) can't fully absorb the deficit and some audio
gaps return — this only affects Debug (~13 fps); Release stays effectively gapless, AOT or not.

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
- **`PublishTrimmed` is tied to `EnableWasmAot`** (AOT requires trimming — the SDK errors
  otherwise). `CoreRegistry`, `ClockRegistry` and `NullProviderRegistry` all discover by
  `Assembly.GetTypes()` + name prefix and swallow their own failures, so trimming them away
  yields a silent black screen with no error. `LinkerConfig.xml` explicitly roots every
  `CPU_*`/`PPU_*`/`APU_*`/`CLOCK_*`/`NullProviders.*` type against exactly this. Verified after
  enabling AOT: all 3 clocks / 7 CPU / 11 PPU / 17 APU cores still populate their dropdowns.
  Re-check this after adding a new core or changing `LinkerConfig.xml`.
- **Pin `RuntimeFrameworkVersion` explicitly.** SDK 10.0.300 bundles runtime 10.0.8 by default,
  but the packages here (and the `wasm-tools` workload) are 10.0.10. Without an explicit pin,
  different MSBuild targets can resolve different pack versions for the managed corlib vs the
  native Mono/AOT bits, and the app fails at boot with `MONO_WASM: mono_wasm_load_runtime()
  failed` / "out of sync library: System.Private.CoreLib.dll" — a runtime failure with no build
  error. If you ever hit that after touching package versions, `rm -rf Web/bin Web/obj` and
  republish; a stale `obj/` from before the pin was added can also cause it.
- **`MSBuildEnableWorkloadResolver=false` is deliberately absent.** It silently turns
  `RunAOTCompilation` into a no-op with no warning.
- **`APU_WF` and `APU_MNES` are desktop-only** (winmm MIDI / filesystem SF2 probe). They are
  linked because `NES.cs` references them by type name, and they appear in the dropdown labelled
  accordingly, but they produce silence in a browser. They are why `NAudio.Midi` and `MeltySynth`
  are referenced.
- **`CLOCK_CLR` and `CLOCK_TRB` can wedge the tab** when a frame overruns its budget: their wait
  branch (which holds every `Task.Delay`/`Task.Yield`) is skipped when perpetually behind, and the
  fallback yields only every 16 frames. On WASM's single thread that starves the renderer. **This
  is a real risk without AOT** (non-AOT frame cost of ~24.7 ms already exceeds their 16.7 ms
  budget) but **verified safe with AOT** (2.9 ms frame cost, comfortably under budget — switched
  to both live without issue). They stay labelled experimental in the UI regardless, since a
  future core/feature addition could push cost back over budget. `FMC` is the default and is
  always safe either way, since it's externally paced by the JS rAF loop rather than self-timed.
- **`nullproviders/`: only 7 of 26 are linked.** The other 19 `using BrokenNes.Windows.Rendering`
  (`ColorMath`), which lives outside the emulator tree. Discovery is reflection-based so the rest
  are simply absent. At least one concrete provider must remain — `NullProviderRegistry` throws if
  the discovered list is empty.
- **`wwwroot/roms/test.nes` renders pure black forever.** It is a launcher stub the old shells
  painted TV static over. `demo.nes` is the working boot ROM.
