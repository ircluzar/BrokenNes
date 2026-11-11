# One Nano per console (2026-11-10)

User request: "one nano player for each console. use the non-janky core that takes the least amount of resources just like we did with the nano gb",
and a song player for Nano NES. Order: NES player, SMS/GG, Genesis, SNES, DOS. Model: Nano GB (`Nano/gb`, `docs/projects/nano-lightest-apu.md`).

## Status

| Nano | chip | library + MIDI player | parity vs C# | plugin chip | flp converter | commit |
|---|---|---|---|---|---|---|
| NES (`Nano/`) | APU_FIX (unchanged) | 0.2.0: song player, `nano-nes-sidecar` | 77/77 + 18/18 fuzz bit-exact vs current Core (the drift was fixed by 628e7aa8) | the existing FIX chip (Nano NES = FIX sample for sample) | `Nano/tools/flp2nano` (NES Direct mode) | 1c10c02a |
| SMS / GG (`Nano/sms`) | stock `Sn76489` | done | 28/28 bit-exact, kernel bit-exact, 14/14 mutants; C# twin 10/10 + plugin-way MIDI 6/6 | `NANOSMS` = chip 76 (selftest nanosms PASS) | `Nano/sms/tools/flp2nano` (19 checks) | 4dda0a32, a64f7a7e + flp |
| Genesis (`Nano/genesis`) | **stock `MD` (YM2612 + PSG)**, picked 2026-11-10 by the interleaved run below | done (32 presets) | 26/26 traces + C# twin 10/10 + MIDI 6/6 bit-exact | `NANOMD` = chip 88 (selftest nanomore PASS) | `Nano/genesis/tools/flp2nano` (21 checks) | 2026-11-11 |
| SNES | **stock `SFC` S-DSP** (S-DSP alone, no SPC700), picked 2026-11-10 | not started | | | | |
| DOS | **`M036` (MAME 0.36 OPL2)** + PC speaker: the user's choice 2026-11-10 ("the lightest", quirks accepted) over the YMFM pick below | not started | | | | |

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

## The interleaved A/B run (2026-11-10, the pick)

`tools/apu-bench -interleave 40 -seconds 0.5 -only <ids>` (new mode): the candidates of one console are set up once in one process, then 40 rounds,
each timing every candidate once over 0.5 s of emulated audio in a rotating order (reversed every other round), so a busy moment of the shared
machine hits every candidate alike. Three passes per console, the consoles alternating between passes. Figures: CPU ms per emulated audio second
(at the rig's native rate); "min" = the pass's minimum, "won" = rounds in which it was the lightest.

| id | min, 3 passes | median of minima | medians | won (of 40) | |
|---|---|---|---|---|---|
| `MD:MD` | 37.8 / 33.9 / 33.5 | **33.9** | 42.5 / 42.6 / 47.8 | 18 / 18 / 12 | the stock unit: **pick** |
| `MD:GENS` | 40.7 / 34.3 / 34.8 | 34.8 | 46.0 / 45.1 / 56.1 | 5 / 8 / 6 | foreign, runs beside the stock chips |
| `MD:MDS` | 38.1 / 35.8 / 32.7 | 35.8 | 43.6 / 42.7 / 46.5 | 11 / 13 / 16 | the stock unit + idle extra sets (= MD) |
| `MD:M036` | 34.8 / 37.4 / 36.7 | 36.7 | 46.4 / 47.3 / 51.8 | 6 / 1 / 4 | foreign, MAME at 22 kHz, zero-order hold |
| `MD:PICO` | 48.7 / 46.7 / 40.5 | 46.7 | | 0 / 0 / 1 | |
| `MD:GPGX`, `GWEN`, `BLST` | 47-67 | 52.9-60.7 | | 0-1 | |
| `SNES:SFC` | 14.3 / 17.5 / 16.8 | **16.8** | 19.6 / 20.8 / 19.7 | 26 / 20 / 27 | the stock unit: **pick** |
| `SNES:S916` | 17.0 / 19.9 / 15.9 | 17.0 | 20.9 / 22.4 / 21.4 | 8 / 2 / 7 | |
| `SNES:PSPC3` | 17.3 / 18.0 / 15.6 | 17.3 | 21.5 / 23.6 / 21.4 | 2 / 8 / 4 | |
| `SNES:S9X1`, `SNDS`, `ZS15`, `ZS151`, `S951` | 17-22 | 19.2-21.2 | | 0-6 | |
| `SNES:OSPC` | 27.5-32.4 | 31.0 | | 0 | |
| `PC-OPL:D072` | 5.1 / 5.8 / 4.7 | 5.1 | 6.7 / 7.1 / 6.3 | 28 / 20 / 31 | MAME fmopl 0.70 **at 22 kHz**: excluded (half rate) |
| `PC-OPL:M036` | 5.4 / 6.4 / 5.5 | 5.5 | 7.7 / 7.7 / 7.6 | 5 / 9 / 3 | harvested art port (MAME 0.36 fmopl, bugs kept) |
| `PC-OPL:D074F` | 5.3 / 6.7 / 6.2 | 6.2 | 7.5 / 7.5 / 7.3 | 7 / 11 / 6 | harvested art port (DBOPL: approximate envelopes, writes per 1 ms block, x2) |
| `PC-OPL:D074C` | 8.5 / 9.7 / 8.5 | 8.5 | | 0 | harvested art port |
| `PC-OPL:YMFM` | 11.4 / 10.4 / 10.1 | **10.4** | 13.4 / 13.1 / 13.2 | 0 | the card's own (ymfm-derived) OPL2: **pick** |
| `PC-OPL:NUK174` | 41.8-50.4 | 43.8 | | 0 | Nuked OPL3: exact, 4x the cost |

Readings:
* **The rankings flip between passes** inside the leading group of each console (Genesis: MD / GENS / MDS / M036 within 10 %; SNES: SFC / S916 / PSPC3;
  DOS: M036 / D074F). So the pick is by the median of the minima, and the rounds won agree with it for Genesis and SNES.
* Genesis and SNES: the stock unit is the lightest by the median of minima AND the most often lightest round by round; it is also the accurate one,
  and every foreign Genesis unit runs the stock YM2612 + PSG underneath and only replaces the output (the stock chip is their floor by construction).
  For SNES the table times whole units (SPC700 + DSP); Nano SNES runs the S-DSP alone, which is the stock `DSP_SFC`.
* DOS: by the median of minima `D072` < `M036` < `D074F` < `D074C` < `YMFM`, but all four lighter engines are harvested "art instrument" ports with
  their flaws kept on purpose (Bn2Consoles / x86 harvest: D072 at 22 kHz; D074F's linear-ish envelopes, best-guess attacks and 1 ms write blocks; M036
  and D074C as shipped in 1999-2010), i.e. janky by design, as the SMS harvest units excluded for Nano SMS. The lightest non-janky engine is the card's
  own `YMFM` OPL2 (about 1 % of one core at 44.1 kHz, 2x M036). **Decision for the user to overrule:** if the lightest at any price is wanted, M036 (half
  the CPU) is the one to take instead.
