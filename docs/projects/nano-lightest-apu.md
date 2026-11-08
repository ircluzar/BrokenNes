# Nano: which of our sound chips is the lightest?

Question (2026-11-08): for a minimum-resource build of BrokenNes Nano, which APU, of every APU we have on any console, costs the least CPU to make audio?

## Answer

- **Lightest overall: `GB:GNUB`** (gnuboy 1.0.3's Game Boy sound, `Windows/NesEmulator/gb/APU_GNUB.cs`): **0.23 % of one core** for real-time audio at 44.6 kHz, **52 ns per sample**, no allocations, the smallest state of the four-channel chips (about 64 KB counted). `GB:VBA` / `GB:VBAM` read a hair lower in CPU % (0.19 %) only because they run at 22 kHz; per sample they cost 85 ns, 1.6x GNUB. `GB:MGB` (minigb_apu) is a statistical tie with GNUB (53 ns, 0.25 % at 48 kHz).
- **Lightest that is still musically useful: also `GB:GNUB`.** It is a full Game Boy sound chip: two pulses with duty and envelope, a 32-step wavetable channel, noise, stereo. Nothing cheaper is more useful; the next tier (the FM chips `PC-OPL:D072` / `D074F` / `M036`, 0.4-0.5 %) is the lightest *rich* option: nine FM voices for twice GNUB's cost.
- **Lightest that keeps Nano a NES synth: `NES:LOW`** (Low Power, `APU_LOW.cs`), 0.46 %, tied with `NES:HI` (0.45 %). That is **4.8x cheaper than `APU_FIX`** (2.17 %), the core Nano's `chip.js` transcribes today. LOW/HI jump from sample boundary to sample boundary; FIX ticks every one of the 1.79 M CPU cycles a second, which is the whole difference. Moving Nano to LOW/HI would cost the bit-exact parity with the FL plugin's chip (the README's proof is against FIX) and FIX's accuracy fixes.

## Method

`tools/apu-bench` (a console project on `Core/`, not in the solution): `dotnet run -c Release --project tools/apu-bench -- -seconds 3 -runs 7 [-only NES:FIX,GB:GNUB] [-csv out.csv]`.

- Every chip plays the same tune: all its channels keyed on at setup, a new note on every channel every 125 ms. The host runs in 1 ms slices (step the chip, drain its samples), like a real-time audio host.
- Each chip runs in the **smallest setup that makes it sound**, through its public seams only (no core changed):
  - NES (`CoreRegistry.ApuIds`, which includes the harvested foreign chips AY124/POKY/FSID/RSID16/PCEG/DARC/... and the bridge cores DMG*/SMS*/MD*/SNES*/WMSS): the IAPU on an idle NES, only the APU stepped, 32 CPU cycles at a time, exactly as `NesApuInstrument` (the FL plugin's chip) drives it. The bridge cores carry a whole foreign sound unit plus the voice translation, so they are always heavier than the same foreign chip natively.
  - Game Boy (`GbCores`): `Tick(16)` T-cycles at a time plus `FrameSequencerStep()` every 8192 T-cycles (the DIV edge the board would supply). No CPU.
  - SNES (`SnesCores`): the S-DSP cannot sound without SPC700 code, so the rig plays the main CPU's half of the IPL boot handshake, uploads a 420-byte driver plus a one-block looping BRR square, the driver keys on all eight voices and spins in `BRA $`. **The SPC700 runs the whole time** (as in any real use), notes are held, not changed. `RunTo` once per ms.
  - Master System / Game Gear / Mark III (`SmsSoundFactories`): `Run(16)` Z80 clocks at a time, no Z80; the FM units also get all nine YM2413 channels.
  - Genesis (`GenesisSoundFactories`): the Z80 held in reset, the 68000 side requests the bus and writes six YM2612 channels plus the PSG directly. `RunTo` once per ms.
  - PC AdLib: every `IOplEngine` (nine OPL2 voices) plus the card's own ymfm-derived `Opl2` on a fake PIT clock.
- Timing: one second of warm-up (JIT tiers), then 7 timed runs of 3 s of emulated audio, median taken; three full passes, the best median kept (the last two at High priority). The pass-to-pass spread (`spread` in the CSV) was 1.01-1.27x, mostly under 1.1x.
- **CPU %** = wall time per emulated second / 1 s (one core). **ns/sample** = wall time / native-rate sample frames produced (so chips at 22 kHz look cheap in % and expensive per sample). **Alloc** = `GC.GetAllocatedBytesForCurrentThread` per emulated second. **State** = a reflection walk of the chip's object graph (arrays by element size, 8 bytes a reference, the NES board itself excluded): a rough retained-size estimate that includes static-looking tables a chip keeps per instance, not a precise heap measurement.
- Every chip was checked to make sound (peak level above -66 dBFS); none was silent.
- Machine: 12 logical CPUs, .NET 10.0.14 JIT (x64), Windows 11. **Load:** other agents were busy: `RunN64` was using about two cores during the runs and total CPU load read 32-54 %. The bench is single-threaded so it had a core to itself, but turbo and cache sharing were affected; absolute numbers may be ~10 % high, the ranking is stable across passes.

## Ranked table (lightest CPU % first)

| # | chip | rate (Hz) | ns / sample | CPU % of one core | alloc KB/s | state KB (approx) |
|---|---|---|---|---|---|---|
| 1 | `GB:VBAM` | 22050 | 84.8 | 0.19 | 0 | 128 |
| 2 | `GB:VBA` | 22050 | 85.1 | 0.19 | 0 | 128 |
| 3 | `GB:GNUB` | 44620 | 52 | 0.23 | 0 | 64 |
| 4 | `GB:MGB` | 48000 | 53 | 0.25 | 0 | 135 |
| 5 | `GB:TGBD` | 44100 | 77.2 | 0.34 | 0 | 1157 |
| 6 | `PC-OPL:D072` | 22050 | 170.4 | 0.38 | 0.5 | 7 |
| 7 | `GB:GYOB` | 32768 | 130.6 | 0.43 | 0 | 139 |
| 8 | `NES:HI` | 44100 | 102.9 | 0.45 | 0 | 128 |
| 9 | `NES:LOW` | 44100 | 103.2 | 0.46 | 0 | 128 |
| 10 | `PC-OPL:D074F` | 44100 | 105.3 | 0.46 | 0.5 | 11 |
| 11 | `PC-OPL:M036` | 44100 | 107.2 | 0.47 | 0.5 | 8 |
| 12 | `NES:HI2X` | 44100 | 108.7 | 0.48 | 0 | 149 |
| 13 | `NES:HI2` | 44100 | 108.7 | 0.48 | 0 | 128 |
| 14 | `SMS:SMSS` | 44100 | 125.2 | 0.55 | 0 | 33 |
| 15 | `SMS:SMSA` | 44100 | 125.6 | 0.55 | 0 | 37 |
| 16 | `SMS:GENS` | 44100 | 128.5 | 0.57 | 0 | 37 |
| 17 | `SMS:M036` | 44100 | 131 | 0.58 | 0 | 35 |
| 18 | `SMS:DARC` | 44100 | 135.3 | 0.60 | 0 | 39 |
| 19 | `SMS:PICO` | 44100 | 136.3 | 0.60 | 0 | 36 |
| 20 | `PC-OPL:D074C` | 44100 | 140 | 0.62 | 0.5 | 19 |
| 21 | `SMS:GWEN` | 44100 | 140.3 | 0.62 | 0 | 33 |
| 22 | `SMS:GPGX` | 44100 | 141.4 | 0.62 | 0 | 33 |
| 23 | `SMS:BLST` | 44100 | 156.3 | 0.69 | 0 | 33 |
| 24 | `GB:GBS` | 48000 | 144.2 | 0.69 | 0 | 129 |
| 25 | `SMS:DEGA` | 44100 | 161.4 | 0.71 | 0 | 92 |
| 26 | `NES:QLQ` | 44100 | 161.7 | 0.71 | 196.1 | 129 |
| 27 | `PC-OPL:YMFM` | 44100 | 172.2 | 0.76 | 0.5 | 3 |
| 28 | `SMS:SMS` | 44100 | 173.7 | 0.77 | 0 | 32 |
| 29 | `GB:GB` | 48000 | 168.8 | 0.81 | 0 | 129 |
| 30 | `SNES:SFC` | 32000 | 337.8 | 1.08 | 0 | 195 |
| 31 | `NES:LQ` | 44100 | 245.1 | 1.08 | 196.1 | 128 |
| 32 | `NES:WF` | 44100 | 248.6 | 1.10 | 196.1 | 128 |
| 33 | `NES:SPD` | 44100 | 253.2 | 1.12 | 196.1 | 128 |
| 34 | `NES:EIL` | 44100 | 254.4 | 1.12 | 196.1 | 128 |
| 35 | `NES:MNES` | 44100 | 261.3 | 1.15 | 196.1 | 128 |
| 36 | `SNES:S916` | 32000 | 363.5 | 1.16 | 0 | 639 |
| 37 | `SNES:PSPC3` | 32000 | 373.3 | 1.20 | 0 | 196 |
| 38 | `SNES:S9X1` | 32000 | 380 | 1.22 | 0 | 320 |
| 39 | `SNES:SNDS` | 32000 | 392.2 | 1.25 | 0 | 262 |
| 40 | `SNES:ZS15` | 32000 | 393.1 | 1.26 | 0 | 197 |
| 41 | `SNES:ZS151` | 32000 | 412.2 | 1.32 | 0 | 266 |
| 42 | `NES:SPD2` | 44100 | 307 | 1.35 | 196.1 | 128 |
| 43 | `SNES:S951` | 32000 | 444.6 | 1.42 | 0 | 551 |
| 44 | `NES:FMC` | 44100 | 424.5 | 1.87 | 0 | 128 |
| 45 | `NES:QLQ2` | 44100 | 429.8 | 1.90 | 196.1 | 129 |
| 46 | `NES:LQ2` | 44100 | 439.4 | 1.94 | 0 | 128 |
| 47 | `SNES:OSPC` | 32000 | 612.6 | 1.96 | 0 | 259 |
| 48 | `NES:QLOW` | 44100 | 451 | 1.99 | 196.1 | 129 |
| 49 | `NES:ULQ` | 44100 | 470.7 | 2.08 | 0 | 128 |
| 50 | `NES:QN` | 44100 | 471.6 | 2.08 | 196.1 | 129 |
| 51 | `NES:FIXS` | 44100 | 485.8 | 2.14 | 0 | 129 |
| 52 | `NES:FIX` | 44100 | 492.1 | 2.17 | 0 | 128 |
| 53 | `NES:TUXN` | 44100 | 503.9 | 2.23 | 0 | 370 |
| 54 | `NES:PCEG` | 22050 | 1021.8 | 2.25 | 0 | 267 |
| 55 | `NES:NSTC` | 44100 | 514.9 | 2.27 | 0 | 273 |
| 56 | `NES:INES` | 44100 | 512.2 | 2.27 | 0 | 351 |
| 57 | `NES:NOFR` | 32000 | 711.1 | 2.27 | 0 | 202 |
| 58 | `MD:MD` | 44100 | 523.4 | 2.31 | 1 | 88 |
| 59 | `MD:MDS` | 44100 | 523.5 | 2.31 | 0 | 88 |
| 60 | `NES:VNES` | 22050 | 1050.4 | 2.32 | 0 | 210 |
| 61 | `NES:NSDS` | 24042 | 979.6 | 2.35 | 0 | 267 |
| 62 | `MD:GENS` | 44100 | 553 | 2.44 | 0 | 570 |
| 63 | `MD:M036` | 44100 | 573.3 | 2.53 | 0 | 108 |
| 64 | `NES:DARC` | 44100 | 576.3 | 2.54 | 0 | 262 |
| 65 | `NES:POKY` | 44100 | 590.5 | 2.60 | 0 | 202 |
| 66 | `NES:FCEU` | 44100 | 606.3 | 2.68 | 0 | 229 |
| 67 | `NES:DMG_VBA` | 22050 | 1217.2 | 2.68 | 280.7 | 274 |
| 68 | `NES:DMG_VBAM` | 22050 | 1220.9 | 2.69 | 280.7 | 274 |
| 69 | `NES:DMG_GNUB` | 44620 | 604.1 | 2.69 | 371.1 | 210 |
| 70 | `NES:SMSS` | 44100 | 615.1 | 2.71 | 0 | 682 |
| 71 | `NES:SMS` | 44100 | 620.3 | 2.73 | 0 | 426 |
| 72 | `NES:DMG_MGB` | 48000 | 574.2 | 2.75 | 381.8 | 284 |
| 73 | `NES:FSID` | 44100 | 625.2 | 2.76 | 0 | 228 |
| 74 | `NES:SMS_SMSA` | 44100 | 627.8 | 2.77 | 0 | 431 |
| 75 | `NES:DMG_GYOB` | 32768 | 847.5 | 2.78 | 322.5 | 288 |
| 76 | `PC-OPL:NUK174` | 49716 | 559.2 | 2.78 | 0.5 | 5 |
| 77 | `NES:SMS_GENS` | 44100 | 631.3 | 2.78 | 0 | 431 |
| 78 | `NES:SMS_M036` | 44100 | 633.6 | 2.79 | 0 | 429 |
| 79 | `NES:SMS_DARC` | 44100 | 636 | 2.81 | 0 | 433 |
| 80 | `NES:SMS_PICO` | 44100 | 640.5 | 2.82 | 0 | 430 |
| 81 | `NES:SMS_GPGX` | 44100 | 641 | 2.83 | 0 | 427 |
| 82 | `NES:DMG_TGBD` | 44100 | 642.8 | 2.83 | 372 | 1307 |
| 83 | `NES:SMS_GWEN` | 44100 | 644.2 | 2.84 | 0 | 427 |
| 84 | `MD:PICO` | 44100 | 646 | 2.85 | 0 | 117 |
| 85 | `NES:SMS_BLST` | 44100 | 661 | 2.92 | 0 | 427 |
| 86 | `NES:PNES` | 48000 | 608.2 | 2.92 | 0 | 401 |
| 87 | `NES:SMS_DEGA` | 44100 | 679.2 | 3.00 | 0 | 486 |
| 88 | `NES:AY124` | 44100 | 679.8 | 3.00 | 0 | 217 |
| 89 | `NES:DMG` | 48000 | 628.8 | 3.02 | 383.8 | 274 |
| 90 | `MD:GWEN` | 44100 | 727.5 | 3.21 | 0 | 93 |
| 91 | `MD:GPGX` | 44100 | 741.1 | 3.27 | 0 | 109 |
| 92 | `NES:RSID16` | 44100 | 755 | 3.33 | 0 | 201 |
| 93 | `NES:DMGS` | 48000 | 708.4 | 3.40 | 563.1 | 404 |
| 94 | `MD:BLST` | 44100 | 853.4 | 3.76 | 0 | 91 |
| 95 | `NES:SNESS` | 44100 | 856 | 3.77 | 0 | 982 |
| 96 | `NES:SNES` | 32000 | 1195.1 | 3.82 | 319.4 | 333 |
| 97 | `NES:SNES_PSPC3` | 32000 | 1220.4 | 3.90 | 319.4 | 335 |
| 98 | `NES:SNES_S916` | 32000 | 1223.4 | 3.92 | 319.4 | 777 |
| 99 | `NES:SNES_ZS15` | 32000 | 1227.7 | 3.93 | 319.4 | 335 |
| 100 | `NES:SNES_SNDS` | 32000 | 1228.6 | 3.93 | 319.4 | 400 |
| 101 | `NES:SNES_S9X1` | 32000 | 1230.7 | 3.94 | 319.4 | 459 |
| 102 | `NES:SNES_ZS151` | 32000 | 1238.7 | 3.96 | 319.4 | 404 |
| 103 | `NES:SNES_S951` | 32000 | 1282.8 | 4.11 | 319.4 | 690 |
| 104 | `SMS:SMS-FM` | 44100 | 1003.8 | 4.43 | 0 | 143 |
| 105 | `NES:SNES_OSPC` | 32000 | 1401.4 | 4.48 | 319.4 | 398 |
| 106 | `NES:MD` | 44100 | 1024.1 | 4.52 | 0 | 466 |
| 107 | `NES:SMSFM` | 44100 | 1071.9 | 4.73 | 0 | 566 |
| 108 | `SMS:M036FM` | 44100 | 1077.2 | 4.75 | 0 | 151 |
| 109 | `NES:WMSS` | 44100 | 1081.5 | 4.77 | 0 | 1948 |
| 110 | `SMS:E055` | 44100 | 1137.1 | 5.01 | 0 | 560 |
| 111 | `NES:MDS` | 44100 | 1254 | 5.53 | 0 | 723 |
| 112 | `NES:SMSFM_M036FM` | 44100 | 1295.5 | 5.71 | 0 | 679 |
| 113 | `NES:SMSFM_E055` | 44100 | 1336.3 | 5.89 | 0 | 1088 |
| 114 | `NES:MD_GENS` | 44100 | 1589.6 | 7.01 | 0 | 1036 |
| 115 | `NES:MD_PICO` | 44100 | 1662.9 | 7.33 | 0 | 583 |
| 116 | `NES:MD_M036` | 44100 | 1694.4 | 7.47 | 0 | 574 |
| 117 | `NES:MD_GWEN` | 44100 | 1771.4 | 7.81 | 0 | 559 |
| 118 | `NES:MD_GPGX` | 44100 | 1780.6 | 7.85 | 0 | 576 |
| 119 | `NES:MD_BLST` | 44100 | 1930.6 | 8.51 | 0 | 557 |

Notes on the table:
- **Allocations.** Almost everything allocates nothing in steady state (13 B/s is the harness). Exceptions: the "Q"/speed-hack NES family (LQ, WF, SPD, SPD2, EIL, MNES, QLQ, QLOW, QLQ2, QN) allocate about 200 KB/s through the default `IAPU.ReadSamples` (they hand out a fresh array per drain); the Game Boy and SNES bridge cores 290-580 KB/s; the PC OPL engines 0.5 KB/s (the rig's delegate). For a GC-shy Nano build those cores would need an allocation-free drain.
- **Native vs bridge.** The same chip costs 3-15x more as a NES bridge core (`NES:SMS` 2.7 % vs `SMS:SMS` 0.77 %; `NES:MD_GPGX` 7.9 % vs `MD:GPGX` 3.3 %; `NES:DMG_GNUB` 2.7 % vs `GB:GNUB` 0.23 %): the bridge keeps a NES APU as its front and replays voices. For Nano, only a native chip is worth considering.
- **What each needs around it.** NES: nothing (the IAPU steps on its own; Nano already mirrors `NesApuInstrument`). Game Boy: a 512 Hz frame-sequencer tick (trivial; already in the rig). SMS: nothing. Genesis: the unit's Z80 can stay in reset, but the unit's band-limited resampler always runs. SNES: an SPC700 running driver code plus a boot handshake and ARAM upload (the CPU dominates SNES cost: 1.1-2.0 % even with the driver idle in `BRA $`). PC: an OPL engine needs nothing; the ymfm card needs a clock source.
- **Not measured:** the PC speaker, Covox, Tandy (DOSBox 0.72), Sound Blaster DSP and Gravis Ultrasound need a `PcMachine` (PIT/DMA) around them, which is far from a minimal setup; the speaker and Covox are 1-bit / 8-bit DAC latches that would be lighter than anything here but are not musically useful synths on their own. WideMess has no separate sound class (it is the SFC unit, its enhanced bridge `NES:WMSS` is in the table). `SNES:HLE` is silent by design and `MD-STUB` is a stand-in. The Game Gear ids are the same classes as the SMS ones.

## Would the ranking hold in the browser (Nano is JavaScript)?

Nano is not C# at all: `Nano/src/chip.js` is a hand transcription of `APU_FIX` + `StreamResampler`. Its README measures it at 20-25 ms of CPU per audio second in Node and Edge (2-2.5 %), which matches this bench's 2.17 % for `NES:FIX` under .NET JIT: for this kind of tight integer code, JS JIT and .NET JIT land in the same place. `docs/projects/round5-nano.md` measured the C# compiled to WebAssembly at 42 ms/s with AOT (about 2x JS) and 650 ms/s interpreted (no AOT).

So the ranking should hold in the browser for the same reason it holds here: it is algorithmic, not a JIT artefact. The cheap chips are cheap because they do work per output sample (or per register change) and skip cycles in between (GNUB, MGB, LOW, HI, the OPL engines), the expensive ones because they simulate the chip's clock (FIX ticks 1.79 M times a second; the SPC700 runs a CPU; the FM chips compute four operators per voice per sample). A JS port of `APU_GNUB` or `APU_LOW` should therefore land near 0.2-0.5 %, about 5-10x below Nano's present chip. Not verified by measurement in a browser (not attempted: no quick path, there is no JS port of those cores). The WASM build was not measured either.

## Caveats

- One tune (every channel on, a note change every 125 ms). A chip whose cost depends on activity (event-driven mixers like GNUB) could rise with dense register traffic such as DPCM or fast arpeggios; the cycle-ticking cores (FIX) cost the same whatever plays.
- The SNES rig holds its notes (the 420-byte driver does not change pitch); its cost is dominated by the SPC700 anyway.
- Host slicing differs per family (NES 32 CPU cycles a step as the plugin does, GB 16 T-cycles, SMS 16 Z80 clocks, Genesis/SNES one `RunTo` per ms); each is the chip's natural minimal drive, but a coarser slice would make the GB/SMS rigs cheaper still.
- State sizes are an estimate from a reflection walk; compare them within an order of magnitude, not to the kilobyte.
