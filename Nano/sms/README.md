# Nano SMS

The Master System / Game Gear sound chip (SN76489) as a web synth and MIDI player: one JavaScript file, no dependencies, an AudioWorklet.
The third Nano, beside Nano NES (`Nano/`) and Nano GB (`Nano/gb`), with the same API.

```html
<script src="nano-sms.js"></script>
<script>
  let synth;
  button.onclick = async () => {
    synth ??= await BrokenNesNanoSMS.create();          // { gameGear: true } for per-channel stereo pan
    synth.play('coin');                                 // a sound effect
    synth.noteOn('C5', 1, { preset: 'lead', duration: 0.4 });
    synth.loadMidi(await (await fetch('song.mid')).arrayBuffer(), { sidecar, loop: true }).play();
  };
</script>
```

Demos: `demo/index.html` (keyboard, sound effects, MIDI drop zone, Game Gear switch), `demo/minimal.html`. Serve them over http (`node Nano/test/serve.mjs Nano`).

## Which chip, and why

**BrokenNes's own `Sn76489.cs` + `BandLimitedMixer.cs`** (the stock unit, menu id `SMS` / `GG`). Picked by benchmark (`tools/apu-bench`, 2026-11-10, three passes,
SMS ids, best-of medians, ns per 44.1 kHz sample on a machine shared with other agents, spread 1.1-11x between passes):

| unit | ns/sample (best) | worst | what it is |
|---|---|---|---|
| `SMS:PICO` | 258 | 2850 | PicoDrive's SN76496 **on top of** the stock PSG |
| `SMS:SMSS` | 286 | 613 | the stock unit plus idle extra sets (enhanced) |
| `SMS:GENS` | 296 | 343 | Gens 2.10's PSG on top of the stock PSG (quick port) |
| `SMS:M036` | 301 | 1221 | MAME 0.36's sn76496 on top of the stock PSG |
| `SMS:SMS` | 308 | 565 | **the stock unit: Sn76489 alone** |
| `SMS:GWEN` | 390 | 936 | Gwenesis, MCU-trimmed |
| `SMS:GPGX` | 450 | 911 | Genesis Plus GX 1.3.0 (Maxim's core) |
| `SMS:BLST` | 663 | 1158 | BlastEm 0.3.1 |

The first five are inside the noise of a loaded machine (2026-11-08's quieter run: SMSS 125, GENS 128, M036 131, PICO 136, SMS 174). They are not
really separate chips: every foreign unit **runs the stock `Sn76489` and replaces its output** (`SmsSound`'s hooks), so the stock chip is by
construction the floor under all of them; `SMSS` is the stock chip too. `SMSA`, `DARC`, `DEGA` were excluded as janky by design (per-frame
blocks, linear volume, sampled "enhanced" instruments). The stock chip is also the accurate one (SMS Power's documentation) and the one the
BrokenNes2 plugin's Master System console plays.

The JS port makes the same steps at the same chip clocks but jumps from one counter expiry to the next instead of ticking all 224 k PSG clocks
a second; the band-limited mixer (16 taps x 32 phases) is the same arithmetic in float32.

## Proof (sample for sample)

* `node Nano/sms/test/parity.mjs [--mutants]`: the kernel (512 float32) = Core's, bit for bit; 10 note scripts (Master System and Game Gear at
  8, 22.05, 44.1, 48, 96 kHz) and 18 random register streams (latch/data bytes, periods 0 and 1, noise rate 3, white/periodic, stereo, runs of
  1-5000 clocks) replayed through Core's `Sn76489` by `tools/NanoSmsParity`: **28/28 bit-exact, 985 k samples, 10 k writes; 14/14 mutants caught.**
* `node Nano/sms/test/engine.test.mjs`: pitch (within the 10-bit period's own rounding), attenuation steps, voice stealing, noise, Game Gear pan,
  every preset, the MIDI player, sidecars, lean build = full build (62 checks).
* `node Nano/sms/test/browser-test.mjs [--mutant 0..4]`: the worklet in headless Edge/Chrome = renderOffline bit for bit, workletUrl, mono,
  divide, loadMidi = renderMidi, sidecar, bad arguments, a real-time context (heard 439 Hz for A4); 10/10, the 5 wrapper mutants all caught.

## Budget (measured 2026-11-10, the shared dev machine)

| | |
|---|---|
| CPU | 4 voices, stereo, 48 kHz: **2.3 to 3.0 ms of CPU per second of audio** in Node (median of 7; one outlier run at 17 ms on the loaded box); the worklet 7.8 ms per audio second in headless Chrome (including the browser's own 0.8 ms). Nano NES is about 20-30 ms on the same measure, Nano GB about 2 |
| download | `nano-sms.js` 67.6 KB, 21.0 KB gzip; `nano-sms.lean.js` 53.6 KB, 16.5 KB gzip |
| memory | two 4096-float rings (32 KB) per chip; nothing allocated while playing |

## API

The same as Nano GB (`Nano/gb/README.md`): `create(options)`, `synth.noteOn / noteOff / play / bend / allOff / panic / setParam / addPreset /
loadMidi / connectMidi / onactivity / dispose`, `renderOffline`, `renderMidi`, `toAudioBuffer`, `parseMidi`, `presets`. Differences:

* `create({gameGear: true})`: the stereo port ($06) pans each channel left, both or right (`pan` on a note, a preset, or `setParam('pan', [4])`).
  Without it the chip is a Master System's: mono on both sides, pan ignored.
* Channels: 3 tones (square, no duty) and noise. No wave channel, no duty.
* The lowest note is about A2 (109 Hz, period 1023): lower notes stay there. The top notes are coarse (the 10-bit period: 14 cents off at C7).
* Presets (`src/presets.js`, ORDER = MIDI program number, append-only): lead, square, soft, pluck, organ, arp, minarp, bass, pad, echo, kick, tom,
  snare, hat, openhat, crash, buzz (periodic noise), laser, coin, jump, powerup, blip, explosion, hit. Noise: `noise: {rate 0..2, to, ms, white}`.
* Sidecar: `{format: 'nano-sms-sidecar', version: 1, channels: {'1': {preset, volume, pan, name}}, presets: {...}}`.

## Not done yet

* **The BrokenNes2 plugin chip** (a "Nano SMS" chip like Nano GB's `NANOGB`, a C# twin of `src/engine.js`): not built. Until then, the plugin's
  Master System chip `SMS` in Direct mode is the same SN76489 (the same Core class), but not the same instrument layer (presets, envelopes).
* **An FL project converter** (flp2nano for the Master System): not built; `Nano/tools/flp2nano` (NES) and `Nano/gb/tools/flp2nano` are the models.
* The tone-3-clocked noise (noise rate 3) is supported by the chip (and proven) but not offered by the engine.

## Credits

The chip is BrokenNes's own `Sn76489.cs` / `BandLimitedMixer.cs`, written from the SMS Power SN76489 documentation (Maxim) and the Game Gear
Hardware Reference Manual (`Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md`); `dist/NOTICE.txt` travels with the files.
