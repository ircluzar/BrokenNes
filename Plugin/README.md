# BrokenNes2: the FL Studio plugin

BrokenNes runs four ways. All of them use the cores in `Windows/NesEmulator/`:

| Entrypoint | Project | Certified by |
|---|---|---|
| Desktop | `Windows/` | `UAT/entrypoints/desktop-smoke.ps1` |
| Web (Lite) | `WebLite/` | `UAT/entrypoints/web-smoke.ps1` |
| Plugin in a test host | `Plugin/BrokenNes2.Plugin` + `Plugin/BrokenNes.FruityHost` | `FruityHost selftest` (21 checks) |
| Plugin in FL Studio | the same DLL | `UAT/plugin/fl/fl-certify.ps1` |

`UAT/certify.ps1` builds and runs all of them and writes one report.

## Why a native plugin, not a VST

FL Studio only sends piano-roll **slides and per-note pitch** to its own native ("Fruity") plugins. A VST
instrument gets per-note velocity and nothing else. Pitch bends are the point of BrokenNes2, so it is a native
plugin: a DLL that exports `CreatePlugInstance` and hands FL an object shaped like a C++ `TFruityPlug`
(the layout Bendy uses, github.com/superjoebob/bendy_redo). It is written in C# and compiled with Native AOT: no
.NET runtime needs to be installed.

## The projects

- **`BrokenNes.Fruity`** (library): the FL SDK in C#: structures, the function table built from
  `[UnmanagedCallersOnly]` pointers, parameters / automation / saved state, a generic editor, and
  `FruityPluginBase`, the class a plugin derives from.
- **`BrokenNes2.Plugin`** (Native AOT DLL, `BrokenNes2_x64.dll`): the instrument, on `Core/BrokenNes.Core`.
- **`BrokenNes.FruityHost`** (exe): the test bench, a stand-in for FL's host (see below).

## BrokenNes2 today: Direct mode

FL's notes drive a bare NES sound chip, with no ROM in between:
- **4 channels:** pulse 1, pulse 2, triangle, noise. Each is monophonic, last note wins, and the older note resumes
  when the newer one ends. The note's **colour** in the piano roll picks the channel (0 pulse 1, 1 pulse 2,
  2 triangle, 3 noise, repeating), the way Bendy maps colours to MIDI channels; the *Channel* parameter can force one.
- **Any NES sound chip:** the *Sound chip* parameter lists all of BrokenNes's APU cores (FIX, QN, HI, ..., and
  DMG/DMGS = the Game Boy chip through the NES bridge). It can be switched while notes are held.
- **Pitch bends:** pitch, volume and pan are re-read from FL's live voice values every 64 samples (~1.3 ms) and
  written straight to the chip's registers. Slides and per-note pitch automation therefore bend smoothly, far finer
  than a ROM-driven NES (one change per 16.6 ms frame).
- Parameters: Sound chip, Volume, Pan, Coarse (+-24 st), Fine (+-100 ct), Pulse duty, Channel, Noise mode.

The VRUN ROM-driven mode (the AudioPlayer engine) comes later, on `NesAudioMachine` in `Core/`.

**Known limitations**
- **Pulse waveform restart at a period high-byte change.** On the NES, writing a pulse channel's period high byte
  also restarts its waveform. A bend crossing a multiple of 256 (period 255 <-> 256 is concert A) does that: one
  audible click, as on hardware. Triangle and noise do not. The tests measure it, count it, and exclude those
  windows from the tight pitch limits. A phase-aware write (defer the high-byte change to the cycle boundary) is the
  planned fix.
- **Mono output.** The chip is mono; the *Pan* parameter pans the whole mix. A note's own pan is ignored.
- **16 volume steps** (the chip's), so velocity and volume automation are stepped.
- **QLQ** (a deliberately unstable QuickNES variant) plays an octave sharp; the self-test reports it as a warning.

## The test bench: `BrokenNes.FruityHost`

A faithful stand-in for FL's host. It loads a plugin DLL, hands it the object FL would (the C++-vtable-shaped
`TFruityPlugHost` that the plugin calls back into), and plays timelines of notes into it the way FL's mixer does:
- `TriggerVoice` per note-on, `Voice_Release` per note-off;
- live voice levels (pitch in cents from C5, volume, pan) rewritten every block from slide chains and bend curves;
- `NewTick`, then `Gen_Render`;
- the plugin hands finished voices back with `Voice_Kill`, which the host relays like FL does.

```
BrokenNes.FruityHost selftest <dll> [--only id,id] [--json out.json] [--md out.md]
BrokenNes.FruityHost render   <dll> <fixture.json|name> out.wav [--rate 48000] [--block 256] [--chip QN]
BrokenNes.FruityHost track    <wav> <fromMs> <toMs> [--ref 440]   # print a pitch track
BrokenNes.FruityHost regtrace <dll> <fixture.json> <channel>      # print period-register changes
BrokenNes.FruityHost fixture  <name> out.json                     # the notes FL's project is generated from
BrokenNes.FruityHost analyze  <wav> <fixture.json>                # certify audio rendered by FL
```

**`selftest` checks (each measures real audio, not just that calls return):**

| Test | What is proven |
|---|---|
| load | the DLL exports `CreatePlugInstance`; the info block FL reads (SDK version, names, flags, parameter count) is right |
| params | names and value text, info flags, set/get/clamp, FL's 0..2^30 automation scaling |
| state | state round-trips and is stable; foreign, truncated, absurd and future-version streams are survived |
| silence | no voices means exact silence and finite samples |
| pitch | pulse 1/2 and triangle at 7 keys each: period register exact, audio within 2.5 cents (worst measured 0.1) |
| range | pitches outside the chip's range clamp; the pulse mutes below period 8; absurd bends stay finite |
| coarse-fine | tuning shifts the pitch by exactly that much |
| slide | slides up and down on all three tonal channels follow the glide (RMS ~1.4 cents) |
| bend | +-60 cent vibrato is followed (RMS ~1 cent) |
| volume, pan | level falls with velocity, zero is silent, master volume scales; pan places the sound |
| routing | all 16 note colours map to the right channel; the Channel parameter overrides |
| mono | newest note wins, the older resumes, back-to-back notes retrigger |
| lifecycle | every voice is handed back with `Voice_Kill`; bad handles are harmless; 64 simultaneous voices |
| cores | each of the 22 sound chips makes the note, accurate ones on pitch |
| swap | switching the chip mid-note keeps it sounding at pitch |
| rates | 22.05-96 kHz and block sizes 1-4096 keep pitch and continuity |
| realtime | 23x real time with 4 sliding voices, **0 bytes allocated** while playing (inside the AOT DLL) |
| editor | the editor opens in a host window, shows the values, a slider move reaches the plugin and `OnParamChanged` |
| robust | 60 create/destroy cycles, 3 instances concurrently, garbage calls, destroy with a voice playing |
| log | `BROKENNES_PLUGIN_LOG=<file>` records the host's calls (for diagnosing a host with no output) |

## Building, installing

```bash
dotnet publish Plugin/BrokenNes2.Plugin -c Release     # Native AOT; needs the MSVC tools (vswhere on PATH)
```
Output `BrokenNes2_x64.dll`. FL loads it from `<FL>\Plugins\Fruity\Generators\BrokenNes2\`. That is under Program
Files, so copying needs administrator rights: `UAT/plugin/fl/install-to-fl.ps1` does it (one UAC prompt) and refuses
while FL is open.

If FL crashes or shows nothing, set `BROKENNES_PLUGIN_LOG` to a file path and start FL: the last line is what FL was
asking the plugin when it stopped.
