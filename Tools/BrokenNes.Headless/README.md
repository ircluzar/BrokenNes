# BrokenNes.Headless

This is a command-line harness for building and testing BrokenNes emulator cores **without
the Windows host**. It runs on Linux and macOS (and Windows) with only the .NET 10 SDK installed.

It compiles the same `Windows/NesEmulator/**` sources as `BrokenNes.Windows`, using the same
exclusions. Any `CPU_*`, `PPU_*` or `APU_*` class you add there is found automatically by
`CoreRegistry`, so you never need to edit this project when adding a core.

The only host code it pulls in is `Windows/Rendering/ColorMath.cs`, which the null providers use.
`APU_MNES` and `APU_WF` build because the project references the cross-platform `MeltySynth` and
`NAudio.Midi` packages. Their MIDI and SoundFont output can't actually be heard here.

## Setup

```bash
Tools/BrokenNes.Headless/setup.sh   # installs .NET 10 to ~/.dotnet if needed, fetches test ROMs, builds
```

The test ROMs come from [christopherpow/nes-test-roms](https://github.com/christopherpow/nes-test-roms)
and go into `testroms/`, which is git-ignored and never committed.

## Commands

`./nesh` rebuilds incrementally and then runs the harness. It hides the emulator's `[...]` debug
lines; set `NESH_RAW=1` to see them.

| Command | What it does |
|---|---|
| `./nesh cores` | Lists every discovered CPU, PPU and APU core id |
| `./nesh run <rom> --frames 600 --cpu X --ppu Y --apu Z --png out.png --wav out.wav --input "200-205:START"` | Runs a ROM. Saves the last frame as a PNG (or every Nth frame with `--png-every N --outdir d`) and all audio as a WAV. Prints the frame hash, speed and registers |
| `./nesh nestest <nestest.nes> <nestest.log> --cpu X` | Runs the CPU core alone and compares its registers with the reference log before every instruction. Stops at the first divergence |
| `./nesh blargg <rom\|dir>... --cpu X --png-dir d` | Runs test ROMs that report through blargg's `$6000` status byte and lists pass or fail with the ROM's own message |
| `./nesh compare <rom> --a ppu=FMC --b ppu=NEW --frames 600 --wav` | Runs two core loadouts side by side. Reports the first frame where the pictures differ, how many frames differ and by how much, the audio difference and each loadout's speed. Saves A, B and diff PNGs |
| `./nesh bench <rom> --kind cpu\|ppu\|apu --frames 600` | Measures frames per second and the final frame hash for every core of one kind |
| `./suite.sh [--cpu X --ppu Y --apu Z]` | Full accuracy scorecard: nestest plus 55 blargg ROMs (~45 s) |

Input buttons are `A B SELECT START UP DOWN LEFT RIGHT`. `F:BTN` holds a button for frame F only.
`F-G:A+RIGHT` holds the buttons from frame F through frame G.

## Baseline (default loadout CPU_SPD / PPU_FMC / APU_FMC)

- **nestest:** every built-in CPU core passes the official-opcode section, lines 1–5003.
  All of them diverge at line 5039, the first unofficial opcode (`*NOP #imm`, `$80`).
- **suite.sh:** 9 pass, 25 fail, 21 other. The failures are real accuracy gaps rather than harness
  problems: unofficial opcodes, VBL/NMI timing, APU frame IRQ timing, MMC3 IRQ and dummy reads.
  "Other" covers older ROMs that only show their result on screen (use `--png-dir`) and timeouts.
- **Speed:** about 400–1100 fps per core in the container, depending on the ROM, which is roughly
  7–18× real time.

Re-run `suite.sh` with a new core to get its scorecard. Re-run `compare` against `FMC` to check
how far it drifts from the baseline, whether that drift is a bug or the point of the core.

## Workflow for a new core

1. Add `Windows/NesEmulator/{cpus,ppus,apus}/CPU_XYZ.cs` (or `PPU_`/`APU_`). Implement `ICPU`,
   `IPPU` or `IAPU`, or subclass an existing core. Give it either a `(Bus bus)` constructor or a
   parameterless one.
2. Follow `docs/core-lifecycle.md`. `GetState`/`SetState` must work, because the bus copies state
   across when it hot-swaps cores. Use `ClearBuffers`/`Reset` to free large buffers, and avoid
   reflection-heavy serialization.
3. Test it with `./nesh nestest ... --cpu XYZ`, `./suite.sh --cpu XYZ`, `./nesh compare <rom> --a cpu=FMC --b cpu=XYZ`
   and `./nesh run <rom> --cpu XYZ --png ...`.
4. Make sure the Windows host still compiles, even on Linux:
   `dotnet build Windows/BrokenNes.Windows.csproj -p:EnableWindowsTargeting=true`.
5. **Hook it into the game.** The emulator finds the core on its own, but the card and progression
   layer lists cores by hand. These are the places that mention existing cores:
   - `Windows/Data/default-db.json` and `Windows/Webmodules/shared/models/default-db.json`:
     the card entry (`"id": "CPU_XYZ"`) and, optionally, level `requiredCards`
   - `Windows/ImageTools/SvgFactory.cs`: the card art (`CPU_XYZ` property)
   - `Windows/Webmodules/Continue/continue.js` (`fallbackCoreData`) and
     `Windows/Webmodules/shared/gameSave.js` (`unlockAllCores`)
6. Feel and audio playback still have to be checked on Windows in the real host.
