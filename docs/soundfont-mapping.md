# SoundFont Program & Channel Mapping

Status: Draft (initial stub)  
Date: 2025-08-16

## Overview
Two SoundFont playback cores exist:
- WF (oscillator / optional sampled) – minimal GM subset
- MNES (SF2 via FluidSynth) – MNES.sf2 with NES-specific patches

Both receive abstract channel identifiers from the emulator: `P1`, `P2`, `TRI`, `NOI`, `DPCM`. These are translated to MIDI channels & programs per core.

## MNES Core Mapping (SF2)
| NES Channel | MIDI Channel | Default Program (GM) | Notes |
|-------------|--------------|----------------------|-------|
| P1 | 0 | 80 Lead 1 (square) | Square wave approximation |
| P2 | 1 | 81 Lead 2 (saw) | Sawtooth or alternate pulse |
| TRI | 2 | 32 Acoustic Bass | Emulates triangle tonal role (bass) |
| NOI | (noise synth) | (noise burst) | Not SF2-driven; uses white-noise fallback |
| DPCM | 3 | 0 (Bank 128 program 0 reserved) | Bank 128 program 0 now active (stub pitch model) |

## WF Core Mapping (Oscillator/Sample)
| NES Channel | Program (Internal) | Waveform | Notes |
|-------------|--------------------|----------|-------|
| P1 | 80 | square | Lead 1 square when sample mode enabled |
| P2 | 81 | sawtooth | Lead 2 saw |
| TRI | 32 | triangle | Sustained amplitude with lower gain |
| NOI | n/a | noise buffer | One-shot burst with short decay |

## Layering Considerations
Layering is off by default. When enabled, both cores process note events; gain staging may need normalization (see backlog J2). Avoid leaving layering on for performance-sensitive scenarios.

## Future Additions
- DPCM sample mapping (Bank 128) now active: plays a sustained note sized to sample length; loops when bit set.
- Per-channel gain normalization & ADSR shaping
- Runtime patch remapping UI

