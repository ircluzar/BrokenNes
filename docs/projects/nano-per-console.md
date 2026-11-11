# One Nano per console (2026-11-10)

User request: "one nano player for each console. use the non-janky core that takes the least amount of resources just like we did with the nano gb",
and a song player for Nano NES. Order: NES player, SMS/GG, Genesis, SNES, DOS. Model: Nano GB (`Nano/gb`, `docs/projects/nano-lightest-apu.md`).

## Status

| Nano | chip | library + MIDI player | parity vs C# | plugin chip | flp converter | commit |
|---|---|---|---|---|---|---|
| NES (`Nano/`) | APU_FIX (unchanged) | 0.2.0: song player, `nano-nes-sidecar` | 77/77 + 18/18 fuzz bit-exact vs current Core (the drift was fixed by 628e7aa8) | the existing FIX chip (Nano NES = FIX sample for sample) | `Nano/tools/flp2nano` (NES Direct mode) | 1c10c02a |
| SMS / GG (`Nano/sms`) | stock `Sn76489` | done | 28/28 bit-exact, kernel bit-exact, 14/14 mutants; C# twin 10/10 + plugin-way MIDI 6/6 | `NANOSMS` = chip 76 (selftest nanosms PASS) | `Nano/sms/tools/flp2nano` (19 checks) | 4dda0a32, a64f7a7e + flp |
| Genesis | to pick (see below) | not started | | | | |
| SNES | to pick | not started | | | | |
| DOS | to pick | not started | | | | |

## Benchmarks run for the rest (apu-bench, 2026-11-10, 3 passes, -seconds 2 -runs 5)

The machine was heavily shared: the pass-to-pass spread was 1.1x to 11x, so only the best medians are worth reading, and the 2026-11-08 run
(`nano-lightest-apu.md`, quieter) is shown beside them. Raw CSVs were in the session's scratchpad (not kept).

| id | ns/sample best (11-10) | (11-08) | note |
|---|---|---|---|
| `MD:M036` | 1055 | 573 | MAME 0.36 fm.c YM2612 + sn76496 |
| `MD:PICO` | 1223 | 646 | PicoDrive 1.85: no SSG-EG, guessed DAC level (jank?) |
| `MD:GWEN` | 1371 | 728 | Gwenesis, MCU-trimmed, mono |
| `MD:GPGX` | 1379 | 741 | Genesis Plus GX 1.3.0 |
| `MD:GENS` | 1432 | 553 | Gens 2.10 (quick port) |
| `MD:MDS` | 1503 | 523 | stock unit + idle extra sets |
| `MD:MD` | 2591 | 523 | the stock unit (real Z80 held in reset, YM2612 + PSG, band-limited resampler) |
| `MD:BLST` | 2962 | 853 | BlastEm 0.3.1 |
| `SNES:S916` | 768 | 364 | |
| `SNES:S9X1` | 805 | 380 | |
| `SNES:PSPC3` | 823 | 373 | |
| `SNES:SNDS` | 890 | 392 | |
| `SNES:SFC` | 910 | 338 | the stock unit |
| `SNES:ZS15` | 926 | 393 | |
| `PC-OPL:D074F` | 275 | 105 | DOSBox 0.74 DBOPL ("fast": linear sine tables, block LFOs, 2x level) |
| `PC-OPL:YMFM` | 340 | 172 | the card's own ymfm-derived Opl2 |
| `PC-OPL:D074C` | 387 | 140 | Ken Silverman's ADLIBEMU |
| `PC-OPL:M036` | 439 | 107 | MAME 0.36 fmopl |
| `PC-OPL:D072` | 640 | 170 | MAME fmopl 0.70 at 22 kHz |

The two days disagree on the Genesis and SNES order (MD:MD is first on 11-08 and near last on 11-10), so **no pick is made for them yet**: the next
step is an interleaved A/B run (alternate the candidates in one process, many short rounds) when the machine is quieter. Notes for that pick:
* Genesis: foreign units may run beside the stock chips the way the SMS ones do (check `MdSoundUnitM036` etc. before trusting a lower number);
  the stock unit's cost is mostly its band-limited resampler, which a JS port could keep or replace.
* SNES: the SPC700 dominates every SNES unit (`nano-lightest-apu.md`); a Nano SNES would more likely run the S-DSP alone with the engine
  writing DSP registers and ARAM directly (no SPC700), which is a different benchmark from this table. BRR presets from the Runaway presets
  saved in .flp (`Music/Flp/FlpPresetSound.cs`).
* DOS: `D074F` is the lightest on both days and is DOSBox's default OPL2; `YMFM` is next and the accurate one. The PC speaker is not in the table
  (it needs a PcMachine) but is trivially cheap.
