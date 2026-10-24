# WideMess

WideMess is BrokenNes's souped-up SNES fantasy console.
- The plan: `docs/projects/widemess-plan.md`.
- The hardware contract, spec v1: `docs/projects/widemess-spec.md`.

This folder holds what is built for WideMess outside the emulator code.

| Path | What |
|---|---|
| `testroms/layers/` | Test ROM 1, eight layers of 256 colours at 400 x 224 (65816 assembly, WLA-DX), with its negative control. `build.ps1` builds both into `testroms/bin/`. |
| `testroms/sound/` | Test ROM 2, the sound chip: a 65816 program (`sound.asm`) and an SPC700 program (`spc.asm`), WLA-DX; `gen_data.py` makes the samples, the DMA jobs, the SPC700's step lists and the manifest the checks measure against. `build.ps1` builds it and its negative control into `testroms/bin/`. |
| `testroms/bin/` | The built test ROMs: `layers.wms`, `layers.sfc` (the same game as a SNES image) and `layers_nounlock.wms` (the negative control); `sound.wms`, `sound.sfc`, `sound_nounlock.wms` and `sound.json` (the manifest). |

**The emulator side** is in `Windows/NesEmulator/snes/`:
- `WideMess.cs`: the unlock and registers (`WmsExtension`), the `.wms` file (`WmsFile`) and the picture chip (`PPU_WMS`).
- `WideMess.Sound.cs`: the sound chip `APU_WMS` (an `APU_SFC` subclass: the sound RAM above 64 KB and the port `$21F0-$21F3`), and
  `DSP_SFC.WideMess.cs`: the S-DSP's voices 8-31 and its second register page.
- The hooks in `BOARD_SFC.cs`, `PPU_SFC.WideMess.cs`, `DSP_SFC.cs` (one test a sample) and `APU_SFC.cs` (`$F3` with `$F2` at `$80-$FF`).
- The console is registered in `Windows/NesEmulator/systems/ConsoleSystems.cs` (key `wms`).
- It runs on `SnesSession` with `wideMess: true`.

## Tools (BrokenNes Workshop)

```
BrokenNes.Workshop.exe --wms-test  [--roms WideMess\testroms\bin] [--snes "a.sfc;b.smc"] [--frames 900]
BrokenNes.Workshop.exe --wms-pack  --in game.sfc --out game.wms [--title T] [--features 1] [--map 0|1]
BrokenNes.Workshop.exe --wms-shot  --rom game.wms --out shot.png [--frames 60] [--scale 3] [--console wms|snes]
BrokenNes.Workshop.exe --wms-sheet --rom game.wms --out sheet.png [--frames 120]
```

- `--wms-test` runs the milestones' checks, each with a negative control: milestone 1's (sections 1-6) and milestone 3's sound checks
  (S1-S6). With `--snes`, it also runs each SNES game frame for frame on the SNES console and on the WideMess console and requires identical
  pictures and sound.
- `--wms-sheet` draws the 400-wide picture and each of the 8 layers alone.
- Workshop is a GUI-subsystem program. In PowerShell, run it with `Start-Process -Wait -NoNewWindow -RedirectStandardOutput`.

The desktop app's own check is `UAT/entrypoints/widemess-desktop-smoke.ps1 -Exe <BrokenNes.Windows.exe>`. It uses a scratch settings folder.

## Milestone 1 result (2026-10-24)

- **`--wms-test`: 21 of 21 checks pass.**
  - The test ROM unlocks WideMess and draws 400 x 224 with 1,020 colours.
  - Each layer alone shows all 255 colours of its bank.
  - The layers scroll.
  - A save state round-trips.
  - The negative controls hold:
    - registers written without the unlock change nothing;
    - on a SNES the ROM sees no WideMess;
    - the loader refuses damaged, cut and too-new files, and files needing missing features, and says why.
- **8 SNES games run frame-for-frame identical (picture and sound, 900 frames each) on the SNES and WideMess consoles:** Super Mario World,
  A Link to the Past, Star Fox (Super FX), Super Mario RPG (SA-1), Pilotwings (DSP-1), F-Zero, Super Metroid and Donkey Kong Country.
- **The desktop app smoke test passes 7 of 7.**

## Milestone 3 result (2026-10-24): the sound chip

The design is spec section 4. In short:
- The main CPU's unlock unlocks the sound unit too.
- Voices 8-31 are three more banks of the S-DSP's voices in a second register page (`$F2` = `$80-$FF`, `WBANK`).
- Samples are BRR or 16-bit PCM, anywhere in 1 MB of sound RAM, from a 16-byte directory entry.
- The main CPU fills the sound RAM through `$21F0-$21F3`, with DMA.
- The new voices mix into the S-DSP's sums before its master volume and echo. The output stays 32 kHz.

**`--wms-test`: 71 of 71 checks pass** (milestone 1's 23, then 48 for the sound). With the nine SNES games of the rules, 80 of 80.
The sound ROM's checks measure the samples by FFT against the generator's manifest:
- **Each new voice alone:** frequency within 2 Hz, level within 4% (all 24 come out at 0.993 of the expected level), the pan, and a tone
  holding over 99% of the power.
- **All 32 at once:** 32 tones at the 32 frequencies, each within 0.19 dB of its level.
- **16-bit PCM against BRR** at pitch 1.0: harmonics at -68 dB against -29 dB.
- **The rest:** the echo with and without a voice's `WEON` bit, pitch modulation from the voice before, noise, a one-shot PCM sample, a
  ring the 65816 rewrites through the port while the voice plays it, and the playing-address registers.
- **Negative controls:**
  - The same program without the unlock: the new voices are silent, the S-DSP's 8 play, and the sound RAM above 64 KB is untouched.
  - That program on a SNES: bit-identical to it on the locked WideMess.
  - The SNES image on a SNES.
- **The rest of the machine:** a save state into a fresh session, bit-identical; the menu (WMS first) and the swap to SFC, which says so;
  0 bytes allocated over 60 frames of 32 voices.
- **Shown able to fail:** 9 deliberate bugs, each caught. They were:
  - 16-bit addresses;
  - PCM read as BRR;
  - no echo for the new voices;
  - no pitch modulation;
  - the loop bit ignored;
  - a state without the sound RAM;
  - the unlock not reaching the sound unit;
  - the port live while locked;
  - the second page readable while locked.

**SNES games stay SNES:**
- The nine games are frame-identical on both consoles, sound included, and their sound hashes equal the build before milestone 3.
- The ALttP speed-pass golden is IDENTICAL.
- Interleaved A/B on ALttP gives the new build 1.006 times the old speed (best of 3, 12 rounds). The DSP takes 15.65% of the frame
  against 15.70% before.

**Cost:** 32 voices take about 33 ms of CPU a second of sound (the S-DSP's 8: 10 ms).

**The desktop app smoke test passes 8 of 8** (a new step: the sound ROM runs on APU WMS, unlocked).

**Open:**
- An SPC700-side window on the RAM above 64 KB (none now: the main CPU fills it).
- The plugin and Lite keep WideMess hidden until milestone 7.
- No sound driver yet (milestone 5).
