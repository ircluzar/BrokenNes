# Sega track A: Master System, Game Gear, SG-1000, milestone A2

Written 2026-10-04 at the end of milestone A2. Branch `worktree-agent-a14218e0629f0b1fe`. It continues `sega-track-a.md` (milestone A1: board, VDP, sessions, the first 18 games). What was built, what was
measured, what was decided, what remains. Provenance: `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md`, section 1e (the YM2413).

## What exists now (A2)

| Part | Where |
|---|---|
| YM2413 (OPLL): 9 melodic channels + 5 rhythm voices, 18 slots, tables, LFO, noise, instrument ROM; a port of emu2413 v1.6.2 (MIT) **with the corrections the black-box oracle forced** (below) | `Windows/NesEmulator/sega/Ym2413.cs`, `Ym2413Tables.cs` |
| Rate converter for it (emu2413's windowed-sinc converter, push model, no allocation) | `sega/Ym2413Resampler.cs` |
| The Japanese console's sound unit: SN76489 + YM2413, ports `$F0` (address) `$F1` (data) `$F2` (audio control), the real mix, hub voices (4 PSG + 14 FM), a complete state | `sega/SmsFmSound.cs`; menu id `SMS-FM` in `sega/SegaSoundFactories.cs`, listed in the Master System APU menu (`systems/ConsoleSystems.cs`), not in the Game Gear's |
| The verifier against Nuked-OPLL, sample for sample | `Workshop/Sega/FmOracleTestCli.cs` (`--fm-oracle-test`), our own driver `Workshop/Sega/oracle/opll_oracle.c`, `build-oracle-opll.ps1` |
| `--smstest` suites added: `fm`, `state`, `pal` | `Workshop/Sega/FmSuite.cs`, `SmsSuitesFm.cs` (probe programs), `StateSuite.cs`, `PalSuite.cs` |
| Probe generators for the oracle: `$F2` / FM port probes, interrupt sleds, VRAM read sweeps, SG-1000 TMS programs | `SmsSuitesFm.cs`, `IrqProbes.cs`, `VdpSlotProbes.cs`, `Sg1000Progs.cs` (`--smstest --write-fm-probes / --write-irq-probes / --write-vdp-probes / --write-sg-programs dir`) |
| `--sms-run` additions: `--apu`, `--fm-regs-at`; Mesen driver `-Overscan`; the full-frame capture cut to the picture height; a second oracle run (`--mesen-b`) | `Workshop/Sega/SmsRunCli.cs`, `mesen/mesen-capture.ps1` |

## The YM2413

**What it is.** emu2413's structure (18 slots, a 19-bit phase generator, a 1,024-entry log-sine table, a 7-bit envelope in 0.375 dB steps, rate scaling, the tremolo and vibrato tables, a 23-bit noise generator) ported to C#
with every value of the exponential table, the quarter sine, the tremolo pattern and the instrument ROM taken by script (no digit retyped). The output scale is the chip's own: a full-volume sine peaks at +2043 / -2042 (the
oracle's emu2413 state dump shows +2043 / -2037 over 141 sampled instants), nine channels and the five percussion voices are summed by `Calc()`.

**The oracle.** Nuked-OPLL (die-derived, GPL: never read, only run) through our own driver, `opll_oracle.exe`, which calls its public reset / write / clock functions and logs what each of the 18 OPLL clocks of a
sample carries. The alignment (one sample of output latency; channels 7-9 two; the clock at which each write lands) was measured, not assumed, and is documented in the header of `FmOracleTestCli.cs`.

**What differs from emu2413 and why** (each found by the oracle comparison; all are in the ledger, section 1e): integer key-scale levels (a table, not emu2413's), the silent half of the half-sine reads negative zero,
a key-on at maximum attenuation starts the attack at once, the LFO starts one sample late, the envelope clock has per-phase offsets (attack and decay/sustain/release, by rate group), five of the fifteen built-in
instruments carry other bytes than emu2413's (4 flute, 8 organ, 11 harpsichord, 13 synthesizer bass, 14 acoustic bass: found by fitting the user instrument to the oracle's output, 99.0 to 100.0% of 10,131 samples equal
on three pitches each), masked channels read zero, a rhythm-mode change clears the channel outputs, the AM phase wraps at 13,440.

**What "match" means, and the numbers** (`BrokenNes.Workshop.exe --fm-oracle-test`, 173 checks, about 12 s, exit 0): *exact* = every compared sample equal; *bounded* = SNR in dB, the largest single difference in the oracle's
9-bit units (full scale 256) and the share of exactly equal samples stay inside the numbers the group states.
- structure 11 (the channel clock map, rhythm clocks, per-channel key-on latency): exact.
- oscillator 77 (multipliers, feedback 0-6, the four waveforms, total level, volume, key-scale level for every block and level code, vibrato, tremolo on all nine channels over whole periods): exact; the chaotic corner (full-level modulator with feedback 7, or total level 0/1 with feedback 5) is reported as information only (exact on some channels).
- lfo 2, instruments 15 (all fifteen: level error per 128-sample window at most 2 dB, mean at most 0.5 dB, 85% or more samples exact; ten are 99.8 to 100% exact), envelope 54 (attack 15..3, decay, release, sustain flag, key-scale rate at two blocks: SNR at least 26 dB, largest error at most 30 units), rhythm 5 (bass drum 98.4% exact, tom about 99% until key-off, snare / cymbal / hi-hat level within 0.4 dB), pitch 9.
- **Not matched, said plainly:** the noise voices (snare, cymbal, hi-hat) have the right level but not the oracle's noise sequence (no relation found; level within 0.4 dB); the tom's release differs; the chaotic corner above; envelope step timing at the fastest rates differs by up to one step.

**Proof the verifier can fail:** `--fm-oracle-test --prove` injects a fault per group (pitch registers shifted, key-scale levels off, vibrato table, a wrong instrument byte, wrong rates, a percussion key on the wrong slot, the write latency moved one sample) and demands failures:
oscillator 77, lfo 2, instruments 15, envelope 54, rhythm 1, pitch 9, structure 10 failed under their faults.

**On the board** (`--smstest --only fm`, 39 checks, poison 16 of 32 fail): detection and ports, the register file, pitch, 3 dB volume steps, the full-scale peak, the audio-control gating, rhythm, the hub's voices, an APU swap in a running game, the unit's state round trip bit for bit, no allocation while running, speed, and the seven oracle groups above run again from `--smstest` (skipped, not counted as passed, when the oracle executable is missing).

**The `$F2` port and how a game finds the chip (measured on Mesen 2.1.1, with and without its FM chip, by probe programs; `SmsSuitesFm.cs`):**
- With the console's I/O chip **off** (bit 2 of `$3E`) and the unit present, a read of `$F2` returns what was written to it, masked to bits 0-1 (all 256 values written and read back); at power-on it reads 0. `$F0`, `$F1` read `$FF`.
- With the I/O chip **on**, or without the unit, `$F0` and `$F2` read as `$DC` and `$F1` as `$DD` (the pad mirrors), whatever was written.
- Phantasy Star's detection (I/O chip off, write 0 and 1 to `$F2` seven times, read each back, compare with the written value) depends on exactly this, and the A1 board answered the read-back regardless of `$3E` (it would have failed the oracle's probe, and a game that reads the pads through a mirror got the audio control instead). Fixed in `BOARD_SMS.ReadPortCore`.
- Writes: `$F2` stores bits 0-1 (the oracle: value & 3; with no FM chip, nothing).

**Against the oracle in real games** (register file of the YM2413 at fixed frames, Mesen with `EnableFmAudio` against `SMS-FM`, same input script, same start line; the file diff is every one of the 64 registers plus the audio control): Phantasy Star (Japan) 29 of 29 sampled frames to frame 1,500 identical (up to 27 registers non-zero); Shinobi (Japan) 12 of 12; Doki Doki Penguin Land 12 of 12; Fantasy Zone II (Japan) 12 of 12; `YM2413_TestRom.sms` driven by a pad script (instrument, tab, key notes): registers 35 of 35 and pictures 35 of 35 identical. Alex Kidd BMX Trial (Japan) agrees at 7 of 12 and then differs: Mesen's own runs of it differ from each other at those frames (three runs, run two differs from runs one and three at frames 600 to 1,000), so it says nothing about this emulator.
**Level statistics against the oracle** (the oracle's audio cannot be captured: its test runner has no audio output and its Lua API no audio call, so no recording exists; what it does expose is the chip's last output and each channel's output in its state dump): over 1,441 frame-end instants of each game, RMS of the chip's output sum, this emulator over the oracle: Phantasy Star (Japan) 1.010, Fantasy Zone II 0.999, Shinobi (Japan) 0.993, Doki Doki Penguin Land 0.974; per channel mostly 0.99 to 1.01 (a few quiet or decaying channels 0.85 to 1.09 from sampling the instant, not the level).

**Decisions.**
- *Which unit is the default.* `SMS` (the PSG only, the export console) stays the default; `SMS-FM` is selectable in the Master System's APU menu and is not offered for the Game Gear. Most of the library is export software and the Japanese FM games detect the unit themselves; no game is silently given another unit, and switching the APU in a running game keeps the PSG state and starts the FM chip silent (checked: SMS to SMS-FM and back, an unknown id refused). A per-game or per-region automatic choice would be a substitution the user did not ask for, so it is not done.
- *What the two audio-control bits do to the mix* (bit 0 FM on, bit 1 PSG off) is the documented convention. The oracle shows its audio control register but not its audio, so this is **not** compared with a recording.
- *The instrument ROM bytes of five instruments are the oracle's, not emu2413's*, because the oracle is the die-derived chip and the fitted bytes match it to 99-100%; emu2413's own values stay in the comments.
- *Gains.* `FmGain` and `PsgGain` default 1.0: the chips' own scales (a full-volume FM sine peaks at 2,043, a full-volume PSG tone at about 3,277 of 32,768). The relative level on the console was not calibrated against a recording (none can be made here).
- *Speed.* The FM unit with nine channels sounding runs 43x real time alone and allocates nothing; the whole machine (Z80, VDP, PSG, FM) runs the same program at 17x real time, target 8x.

**What Track C's YM2413 bridge and the plugin's chip need.** `ISmsSound.DescribeVoices` returns 18 voices for `SMS-FM`: the four PSG voices (tone 1-3, noise), then the 14 FM voices in this order: melodic channels 1-9 (`Pulse`), bass drum (`Triangle`), snare (`Noise`), tom (`Pulse`), cymbal (`Noise`), hi-hat (`Noise`); `Hz` is the carrier's frequency, `Level` the channel's present amplitude (0 when the FM output is off, `$F2` bit 0 clear). Register interface for a bridge that replays FM: `SmsFmSound.WriteFmAddress/WriteFmData` (the chip's `$F0` / `$F1`), `WriteAudioControl` (`$F2`), `Fm.WriteRegister(reg, value)`, `Fm.Register(i)` (the 64 registers), `Fm.Mask` and `MaskChannel / MaskHiHat / ...` to isolate stems, `Fm.ChannelOutput(v)` (per-voice output), `Fm.Operator(channel, carrier)` (phase, envelope, level, state), `Fm.ChannelHz`, `Fm.ChannelKeyOn`. The chip runs from the Z80 clock (one sample per 72 clocks: 49,716 Hz NTSC, 49,262 PAL).
