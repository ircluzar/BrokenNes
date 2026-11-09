# Pitch bends on the other consoles' music (plan only)

Date: 2026-11-09. Source: the NES evaluation (VRUN CTW, branch `audio-bends` merged into
`vrun-history-export`). Nothing here is built yet.

## What the NES proved

- One converter (`engine/audio/converter/Bends.cs`) turns MIDI pitch-bend events (RPN 0 range,
  CC121 reset) into two opcodes per channel: `pitch` (a held bend = a new period, exact) and `slide`
  (a moving bend = a per-frame period delta, within a tolerance in cents). Notes never move; bends are
  what gets thinned when a channel is busy. Off by default per song (`"bends": true` in the roster).
- Cost on the NES: a bend decoder is ~200 bytes bigger per channel, 5 bytes of RAM per bending
  channel, and roughly +100 cycles per bending channel per frame on average (worst ~1,000 per frame
  with all three tonal channels sliding every frame).
- Lesson: the cycle cost is small, but it lands in the NMI, and the heaviest game frames (boss heal
  wrap, room sweep) had no slack. Ship bends song by song, and measure the game's busiest scenes with
  the song playing, not the sound test.
- Lesson: the bigger music data moved unrelated code to other banks. On banked consoles check where
  the linker put things after adding bend data.

The converter half (MIDI -> per-frame pitch curve -> pitch/slide events with a tolerance) is shared by
every port below; only the opcode encoding, the period table and the driver differ.

## Per console

### SNES - Super VRUN (SPC700 driver)
- Driver: bends run on the SPC700, not the 65816, so they cost the game no CPU time at all.
  The S-DSP pitch is a 14-bit linear rate per voice (`VxPITCH`), so a slide is a plain 16-bit add per
  tick; add a `pitch` and `slide` command to the sequence format and a 16-bit pitch accumulator per voice.
- Converter: reuse the NES curve; convert cents to the DSP rate (rate = 0x1000 * 2^(semitones/12) for a
  sample tuned at its base note) instead of NES periods. Tolerance can drop to ~2 cents.
- Cost: ~8-10 bytes ARAM per voice, a few hundred bytes of driver code, SPC cycles only.
  Main risk: ARAM space next to the samples. Cheapest of all ports.

### Game Boy - Vaults of Obsolete Code
- Driver: periods are 11-bit (`NRx3/NRx4`) on pulse 1, pulse 2 and wave; writing NRx3 alone changes the
  pitch without retriggering (NRx4 only when the high bits change, with bit 7 clear). Same `pitch`/`slide`
  opcodes as the NES; the frequency curve is inverted (period = 2048 - 131072/f), so slides on the
  NES-style period are non-linear in pitch - keep the converter's tolerance check on the GB period table.
- Cost: like the NES (~100 cycles per bending channel per frame on a 70,224-cycle frame, so relatively
  smaller), ~200 bytes ROM per channel decoder, 5 bytes WRAM per channel. Check the frame budget of the
  busiest Vaults scenes (and the SGB path) with a bending song, as on the NES.

### SMS / Game Gear (SN76489) - S1/S2 ports
- Driver: 10-bit tone periods on 3 channels, same shape as the NES square period (period ~ 1/f).
  The existing NES opcodes map almost 1:1; write the low nibble + high byte only when they change.
- Cost: Z80 ~4x the cycles per byte of work of the 6502 at 3.58 MHz on a 59,736-cycle frame: about the
  same fraction of the frame as on the NES. The S2 speed round just took the 4-enemy lag to 9/1000 frames,
  so measure the heaviest rooms with the song before enabling; music already runs in VInt.
- Noise channel: no pitch; drop its bends like the NES converter does.

### Genesis / Mega Drive (YM2612 + PSG) - S3 port
- Driver: the YM2612 pitch is F-number (11 bits) + block (octave) per channel; a slide across an octave
  boundary must re-normalise F-number/block. PSG channels are the SMS case above.
  If the driver is on the Z80, the 68K pays nothing; if music runs on the 68K in VInt, ~100-200 cycles
  per bending channel per frame on a 128,000-cycle frame: negligible.
- Cost: driver code ~0.5 KB, 3-4 bytes per channel. Main risk is YM write timing (busy flag waits),
  so batch the F-number writes (high byte first, then low) once per frame per channel.

### DOS AdLib (OPL2) - Vaults DOS, Super VRUN DOS
- Driver: OPL2 F-number (10 bits) + block per channel, the same as the YM2612 case. Each pitch change is
  two register writes (A0+ch, B0+ch with key-on kept), and OPL2 needs a delay after every write
  (~3.3 us after the address, ~23 us after the data on real chips), so a frame with 9 sliding channels
  costs ~250 us of waits. Limit bends to the channels that use them (the NES "used" rule).
- Speaker (PC speaker) fallback: one PIT channel; bends work with a single divisor write per frame.
- Cost: CPU trivial on a 386+; the risk is real-hardware OPL timing on slow machines (the user tests on
  real hardware), so measure on the user's machine or in 86Box with a cycle-accurate OPL.
- MIDI (MPU-401/GM) path, if added later: pass pitch-bend messages straight through (no conversion).

### Windows 95 ports
- If the music is MIDI out (winmm `midiOutShortMsg`): bends are free - send the MIDI's own pitch-bend
  messages (and the RPN range) as they are. Nothing to convert.
- If it is our own synth or an OPL emulation into a wave buffer: apply the bend as a per-sample or
  per-block pitch multiplier in the mixer; cost is CPU time in the audio thread, negligible.
- Cost: none worth measuring; decide which audio path the Win95 agents picked first.

## Order and effort (suggested)

1. Converter: split `Bends.cs` into the shared curve/tolerance part and a per-console period/opcode
   back end (NES, GB, SN76489, OPN/OPL F-number+block, SNES DSP rate). Reuse `convertertests bends`
   scenarios with a decoder model per back end. Medium.
2. SNES (no CPU cost) and Win95 MIDI (pass-through): cheapest wins.
3. GB, then SMS/GG, then Genesis/DOS (F-number+block back end shared by OPN and OPL).
4. On every console: per-song opt-in, and a busiest-scenes frame measurement with the song playing
   before a gameplay song ships with bends (the NES `gameplay_headroom.sh` pattern).
