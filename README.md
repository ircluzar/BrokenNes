# BrokenNes

BrokenNes is an experimental NES emulator project that mixes conventional emulation with progression systems, collectible cards, unlockable tools, and deliberate corruption workflows. Instead of presenting the emulator as a fixed appliance, BrokenNes treats it like a buildable loadout: you unlock parts, assemble a deck, launch cartridges through a progression layer, and then push the machine into unstable territory with RTC, Glitch Harvester, Imagine, and other activity modules.

At its core, this repository contains a desktop host, an NES emulation stack with multiple CPU, PPU, and APU implementations, a shader pipeline, RetroAchievements integration, audio tooling, ROM management, and a large set of embedded web modules that act as the front end and experimentation surface.

## Not your typical Nes emulator

- It is an emulator and a meta-game at the same time. Progression, achievements, cards, unlocks, and challenge activities are part of the product rather than an external launcher layer.
- It supports many interchangeable core variants across CPU, PPU, APU, clock, and shader categories, including fast, degraded, unstable, experimental, and secret variants.
- It includes corruption tooling as a first-class feature, not a side utility. Real-time blasting, savestate-based glitch harvesting, replayable stockpiles, automated slop loops, and scanline-targeted Imagine modes are built into the experience.
- It leans into emulator personality. Some cores aim for performance or fidelity, while others are intentionally odd, glitch-prone, stylized, or outright questionable.

## Feature Overview

### Modular Emulator Core System

BrokenNes ships with a large mix-and-match core roster rather than a single fixed emulation profile.

- Swap CPU, PPU, APU, clock, and shader components as part of your current build.
- Browse and inspect unlocked core cards through the Cores gallery.
- Use standard control cores as baselines, then branch into faster, degraded, enhanced, unstable, debug, and experimental variants.
- Explore unusual core ideas such as speedhack-heavy variants, Emit IL experiments, scanline-aware Imagine support, bleeding-frame visuals, MIDI-style audio output, and deliberately unreliable joke cores.

This means BrokenNes is not just about whether a game runs. It is also about how it runs, how it sounds, how it looks, and how much instability you are willing to introduce.

### Cards, Unlocks, and Deck Builder

The card system is the progression backbone of BrokenNes.

- Cards represent more than CPU and PPU parts. The project also tracks authored cards for backgrounds, null providers, webmodules, and feature unlocks.
- Your save tracks owned cards, achievement stars, current level, and unlocked modules/features.
- Deck Builder acts as the hub, showing your current collection, star count, and progression state.
- Continue is the campaign-style build console where you equip your current CPU, PPU, APU, and shader loadout, inspect installed cartridges, review achievement progress, and advance when star requirements are met.
- Level progression grants new cards and milestone rewards, including new activity modules such as Glitch Harvester, Time Jump, Corruption Slop, and Target the Beam.
- After the authored campaign levels, progression continues with blind-bag style random unlocks so the collection loop keeps going.

In practice, BrokenNes treats emulator configuration like a collectible deckbuilding game instead of a static settings page.

### Achievements and Progression

Achievements are part of the main progression loop, not just an overlay.

- Achievement unlocks are saved into the game save and converted into star count.
- Stars gate level progression in Continue.
- Unlocks can award cards, backgrounds, null providers, and module milestones.
- Achievements Runtime provides an in-game overlay for tracking unlocked and locked achievements.
- The repository also contains RetroAchievements support and documentation for achievement authoring and testing.

### Corruption Tools

BrokenNes includes several distinct corruption workflows.

- RTC (Real-Time Corruptor): choose memory domains, blast type, and intensity, then apply corruption manually or every frame. Experimental ML-based engine included.
- Blast modes include random writes, tilt-style byte nudges, NOP injection, bit flips, and Imagine-based modes.
- Crash behavior is configurable, including ignore-style behavior and Imagine-assisted recovery paths.
- Glitch Harvester: create named savestate bases, blast from a known state, stash interesting results, replay them, and promote good outcomes into a permanent stockpile.
- Stockpiled glitches can be renamed, replayed, exported, and imported.
- Corruption Slop: an automated loop that repeatedly restores, corrupts, and reruns content for rapid glitch generation.
- Target the Beam: a more experimental corruptor that predicts bytes and can target instruction flow at specific scanlines instead of only between frames.

These tools are meant for both playful chaos and controlled experimentation. You can improvise live corruption or build repeatable glitch workflows around known savestates.

### Emulator Quirks

BrokenNes deliberately preserves a strange identity instead of flattening everything into a single "best" mode.

- Some cores are intended to be dependable baselines.
- Some are optimized at the cost of compatibility.
- Some intentionally degrade sound, visuals, or timing.
- Some expose experimental rendering ideas or aggressive CLR-level optimization attempts.
- Some are openly unstable or comedic, such as obviously dubious processor experiments.

That design choice is part of the appeal. BrokenNes is willing to let an emulator feel expressive, risky, and a little broken.

## Typical Loop

1. Load a ROM or install cartridges into the library.
2. Earn achievements and convert them into stars.
3. Use Deck Builder and Continue to unlock and equip better or stranger cards.
4. Launch a game with your chosen CPU, PPU, APU, shader, and related systems.
5. Use activities like Glitch Harvester, Time Jump, or Target the Beam to explore alternate states, replay glitches, and generate corruption-heavy runs.

## Repository Highlights

- `Windows/`: Windows desktop application, audio engine, web API, embedded web modules, and desktop-specific tooling.
- `Windows/Webmodules/`: progression UI, activities, overlays, debug tools, and card-driven front-end surfaces.
- `Windows/NesEmulator/`: emulator core, cores, mappers, shaders, RetroAchievements support, and corruption systems.
- `Workshop/`: **experimental** minimal WinForms tool for developing/debugging the shared cores
  directly - frame/instruction stepping, register and memory viewers, core switching, save
  states, plus twelve headless CLI modes for scripted accuracy, benchmark, TAS-replay and
  self-play runs (see [Workshop/README.md](Workshop/README.md) and the Tooling map below). Links
  the same cores in `Windows/NesEmulator/`. Part of `BrokenNes.sln` - it's a plain WinForms app
  with none of `Web/`'s AOT/trimming complexity.
- `Web/`: **experimental** minimal Blazor WebAssembly build of just the emulator (no shaders, no
  progression/corruption features), linking the same cores in `Windows/NesEmulator/`. See
  [Web/README.md](Web/README.md). Not part of `BrokenNes.sln` by design - build/run it by path.
- `UAT/`: regression suite and automation harness (`run-suite.ps1`, HTTP/UIA client libraries,
  recorded findings). Not shipped; see the Tooling map below.
- `docs/`: project notes, progression specs, RetroAchievements references, shader docs, and design workpads.
- `SubProjects/`: related experiments and auxiliary work.

## Tooling map

These exist, work, and are easy to miss — this section is here so nobody rebuilds one of them by
accident. All paths are repo-relative.

| Tool | What it is | Start here |
| --- | --- | --- |
| **Workshop headless CLIs** | Twelve `argv[0]` modes on `Workshop/bin/Release/net10.0-windows/BrokenNes.Workshop.exe`, sharing the exact linked cores as the app: `--romtest`, `--headless`, `--benchmark`, `--diag-savestate-roundtrip`, `--irqtrace`, `--playmovie`, `--index-tas-library`, `--tas-baseline-batch`, `--compare-dumps`, `--selfplay`, `--verify-selfplay-movie`, `--accuracycoin`. The fastest way to test an emulation change — no UI, no audio device. | [Workshop/README.md](Workshop/README.md) — its "CLI modes at a glance" table says which need an external asset and whether that asset is on this machine |
| **`UAT/run-suite.ps1`** | The verdict layer the CLIs lack: `UAT/cases/*.json` holds expected values, the runner produces actuals, diffs them, and exits non-zero on disagreement. `-Filter`, `-UpdateGolden`, `-Repeat`, `-Json`. A green run proves determinism, not correctness. | `.\UAT\run-suite.ps1 -Filter 'diag*'` |
| **`UAT/lib/ApiClient.ps1`** | PowerShell client for the desktop app's loopback HTTP control API — load ROMs, swap cores/shaders, peek/poke, read registers and the framebuffer, fire RTC blasts, save/load state. **The preferred way to drive `BrokenNes.Windows.exe` in tests.** | [Windows/webapi/README.md](Windows/webapi/README.md) documents all ~148 endpoints |
| **`UAT/lib/UiaHelpers.ps1`** | UI Automation fallback, for the few things HTTP cannot reach: native MenuStrip item states, WebView2 DOM, message boxes, screenshots. Its header documents the modal-dialog `Invoke()` hang that has burned multiple sessions — read it before automating any menu item that opens a dialog. | Reach for it only after checking no endpoint covers the need |
| **`Windows/webapi/README.md`** | Endpoint reference for the control API above, grouped by route file. | — |

Nothing under `UAT/` is part of a shipped product; it is a test harness and no shipped project
references it.

## Building

From the solution root - this builds both the desktop app and Workshop (`BrokenNes.sln`
intentionally does not reference `Web/`, which has AOT/trimming complexity of its own):

```bash
dotnet build
```

Or build a project directly:

```bash
dotnet build Windows/BrokenNes.Windows.csproj -c Debug
dotnet build Workshop/BrokenNes.Workshop.csproj -c Debug
```

The experimental web build is separate - see [Web/README.md](Web/README.md):

```bash
dotnet build Web/BrokenNes.Web.csproj -c Debug
```

## Running

From the solution root:

```bash
dotnet run --project Windows/BrokenNes.Windows.csproj
```

Or run the built executable directly from the Windows output folder.

For the experimental Workshop dev/debug tool - see [Workshop/README.md](Workshop/README.md):

```bash
dotnet run --project Workshop/BrokenNes.Workshop.csproj
```

For the experimental web build:

```bash
dotnet run --project Web/BrokenNes.Web.csproj --urls http://localhost:5011
```

## Status

BrokenNes is an active experimental project. A large part of its identity comes from iteration, unusual modules, unfinished ideas, and intentionally unstable features. Expect both polished systems and rough edges.

## License

BrokenNes is licensed under the Digital Lifeform License 1.1. See [LICENSE.txt](LICENSE.txt).

