# Round 6, `inject`: pushing an edited instrument into the RUNNING game

Date: 2026-10-17. Domain: the option to put the edit of the Instruments tab into the game's own sound memory / chip (not only Runaway's private copy), so that when Instrument Runaway is switched off the game's own music plays it.
Brief: `docs/projects/round6-instruments-brief.md` ("Pushing into the running game (honest design)"). Branch `worktree-agent-a1810679592f7a835`, builds in `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_r6_inject`. Nothing pushed, nothing merged into `main`.
Everything below was measured on real games from the user's library (Super Mario World, Sonic the Hedgehog, The Legend of Zelda: Link's Awakening, Galactic Protector; never committed). **Nobody has heard any of it** (no sound card on the build machine): every sound claim is a measured PCM, FFT peak or hash.

## 1. Feasibility, per console (measured first, then built)

`FruityHost inject-probe snes|genesis` runs a game and reports what its sound driver does while it plays (a write tap on the S-DSP; the Genesis sound unit's port tap). The Game Boy and Master System numbers come from counting hook calls in a 3,600-frame run.

| Console / part of an instrument | What the driver does (measured) | What a push can make stick | Built as |
|---|---|---|---|
| **SNES** sample bytes (BRR) | Super Mario World, 4,300 frames: **no listed sample's start, length or bytes changed** (20 samples checked every 20 frames); the audio RAM outside the echo buffer did change 15 times (song and sequence data at frames 20-80, 960-980, 1840, 2040, 2960, 3240, 3500) | **sticks** until the game uploads a new sound set over that memory (not seen in 4,300 frames of SMW) | written into `APU_SFC.Aram` |
| SNES loop pointer (the directory's second word) | same as the bytes | **sticks**, same limit | written into `Aram` |
| SNES envelope ADSR1 / ADSR2 / GAIN | written before **23.2%** of key-ons (when a voice changes instrument); the value differs from the previous note's at 6.4% / 4.4% / 2.6% | cannot be written once (a voice is reassigned by the driver all the time) | **forced where the S-DSP reads it**, for every voice whose SRCN names the sample (`DSP_SFC.Overrides`) |
| SNES volumes L / R | written before **98.5%** of key-ons, plus 2,175 times while notes sound | not writable | forced as weights (127 = the driver's value, 0 silences, negative flips) |
| SNES pitch | written before **100%** of key-ons, plus 9,403 times while notes sound (pitch bends, vibrato) | not writable | forced as cents on top of the driver's value (so vibrato and bends go on) |
| SNES echo / noise / pitch modulation per voice | global registers the driver owns | not writable per sample | forced bits per sample; only the flags the edit changed are forced. Noise is only audible when the game itself set the DSP's noise clock (FLG): SMW did not, a noise-forced sample is a flat level there |
| **Genesis** FM patch (28 operator registers, $B0) | Sonic 1, 2,400 frames: the operator registers are written before **6.9%** of 931 key-ons (when a track changes voice), total levels before 14.6%, frequency before 100%. Between loads the chip's registers just hold the patch | a one-time register write is lost at the driver's next voice load | **substitution at key-on** (`Ym2612.Override`): while a channel holds the game's ORIGINAL patch (all but the carriers' total level), the edited patch is loaded first; every channel that holds it gets it |
| Genesis carriers' level | the driver adds the track volume to it | keep the volume | `TL = driver's + (edited - original)`, remembered per channel |
| Genesis pan / AMS / FMS | pan is the track's | only the groups the edit changed | replaced as groups |
| Genesis LFO ($22), shared | set rarely | with the patch | same substitution |
| **Genesis DAC** sounds | 307,936 DAC writes in 88 stretches: the Z80 driver decompresses a ROM sample and streams it | the sound is the stream | **stream substitution** (`OnDacWrite`): the first 12 samples of the sound are recognised, the edited samples are handed over from the 12th on at the driver's pace (an edited rate plays it faster or slower inside it, an edited loop wraps, past the edit silence). Not changeable: when the driver ends a sound, the first 11 samples |
| Genesis PSG | tone, volume, noise written continuously; the sampler does not play those registers either | **nothing to push** | refused with the reason |
| **Game Boy** wave RAM | Link's Awakening, 3,600 frames: the wave channel was triggered 252 times, the RAM changed 4 times (3 distinct waves, loaded when an instrument changes) | written now; the driver's next load puts its wave back | written into `APU_GB.WaveRam` **and** substituted at every wave-channel trigger while the RAM holds the original (`APU_GB.Override`) |
| Game Boy pulse duty / envelope, noise | written at every trigger | would need a trigger hook for the registers | **refused** (reason on screen); not built |
| **Master System / Game Gear** YM2413 user instrument (instrument 0, 8 registers) | Galactic Protector: 188 user-instrument key-ons in 3,600 frames with one register set (the game loads its instrument once); most FM games here use only the ROM instruments (Argos no Juujiken: 0 user key-ons) | one-time write is lost if the game reloads | substitution at key-on of a channel that plays instrument 0 while the 8 registers are the original (`Ym2413.Override`) |
| YM2413 ROM instruments | built into the chip | read-only | refused |
| Master System PSG, NES | PSG as above; a NES game has no instrument memory (its code writes the sound registers for every note) and the tab edits no NES instrument | none | refused with the reason |

Decisions that follow from the numbers: the brief's guess "ADSR/GAIN/volume/tuning are rewritten at every note" is **half true on SMW** (pitch and volume yes, the envelope only on instrument changes), but an envelope written into the registers would still be lost when the driver reassigns the voice, and a key-on override works for every driver, so the SNES uses the override
for all four. Sonic's FM patch is loaded on 7% of the notes: a substitution at key-on holds through reloads (measured: in a 300-frame window the hook loaded the edited patch at 1 key-on and found it already in place at 24).

## 2. What exists

| Piece | Where |
|---|---|
| the push library (no Emulator, no UI): records with the original bytes, `GamePush.Push/Remove/RemoveAll/Verify/Serialize/Restore`, the hook installer | `Plugin/BrokenNes2.Plugin/Instruments/GamePush.cs`, `GamePushSnes.cs`, `GamePushOpn.cs`, `GamePushOther.cs` |
| the project section `INJ1` (version, deflate, CRC-32) and `PushMutants` (test-only deliberate bugs) | `Instruments/GamePushFile.cs` |
| the hooks in the shared emulator tree (all `null` by default) | `Windows/NesEmulator/snes/DspVoiceOverrides.cs` + `DSP_SFC.cs`; `sega/Ym2612Override.cs` + `Ym2612.cs`; `sega/Ym2413Override.cs` + `Ym2413.cs`; `gb/IGbApuOverride.cs` + `APU_GB.cs` |
| the emulator's side: the lock, the corruption farm, the note, `INJ1` capture and restore | `Emulator.Inject.cs`; small additive edits in `Emulator.Rom.cs` (`NotePushesLost`, `DropInjectRestore`), `Emulator.State.cs` (`ApplyInjectRestore`), `Game.cs` (`CaptureSpc` carries the pushes into a new Runaway copy), `Bn2Plugin.cs` (the `INJ1` section, one hook route) |
| the handler `InstrumentPush.Handler` and the tab: `IsPushed` / `RemoveFromGame` / `Notice` (default interface members), the **Remove from game** button (id 1191), "Push again", the "in the game" mark, the standing note | `Instruments/InstrumentPushHandler.cs`, `Ui/Contracts.cs`, `Ui/InstrumentsPanel.cs`, `Ui/UiIds.cs`, small edits in `Instruments/InstrumentsUi.cs` |
| test hooks 320-334 | `Bn2Plugin.Inject.cs` |
| the checks | `Plugin/BrokenNes.FruityHost/Inject.*.cs`: verbs `inject-test` (no DLL) and `inject-probe`; the `inject` check of `selftest` (through the DLL) |

Edits outside my files (all small and additive, all named here): the four hook sites above in the shared emulator tree; `DSP_SFC.RunSample` now keeps the voice's values in locals (the default path computes exactly what it did; goldens below); `InstrumentsUi.cs` (the handler gets the clean instrument as well; the provider's three new members); `Contracts.cs`, `InstrumentsPanel.cs`, `UiIds.cs`, `Bn2Editor.Tabs.cs` (two text fields for the tests); the row of buttons on the tab: "Send to Presets" 128, "Push into the game" / "Push again" 152, "Remove from game" 142 wide (it was 170 / 190).
The change of the push button's behaviour in the existing `imodel-ui` check: the made-up sound memory of that test game is not in the game itself, so the push is refused with its reason (the check was "says not built yet").

## 3. How a push works, and what it tells the user

`Emulator.PushInstrument` (UI thread, `EmulatorHub.Gate` then `romLock`, Instrument Runaway must be on): the instrument must be the one in the frozen copy (else refused: "an edit did not reach it"); the library writes what the driver does not rewrite into the game's memory and installs the hook for what it does; the corruption farm is told (`Farm.NoteWrites` for every byte written; `Farm.Poison` with a reason for a hook, which a replay from power-on cannot repeat).
A push of an instrument that equals the game's own is refused ("nothing to push"), unless an earlier push exists, which is then taken out. Pushing again replaces. The status line carries, for example (SNES): *"Pushed sample 7: 200 sample bytes written into the game's audio RAM (kept until the game loads other sounds there); forced on every voice that plays sample 7 (the driver's own values are ignored for it): envelope $A4 $26 $BB; tuning +500 cents; volume weights 127/70; echo on. Remove from game undoes it."*

A new Runaway copy of a game with pushes carries the same overrides in its overlay (`SnesPusher.Carry`), so FL's notes and the game agree after "Runaway off, Runaway on".
**Remove from game** puts the bytes back where the game still holds the pushed ones (bytes the game has written over since are left alone and counted), removes the overrides, and puts the original patch / wave / instrument registers back on the channels that still hold the edit. It does not touch the Runaway copy (your edit stays in the tab).
A sound unit that was replaced (the SNES running on the HLE core, a swapped Genesis unit) loses its hooks: `Verify` says so on the tab. A game that is replaced (reload, Reset Console, a corruption started from a stockpile) takes its pushes with it: the tab says "N pushed instruments left the game when the game was replaced".

**Persistence.** The machine state (`GAM1`) already holds every byte a push wrote. `INJ1` (last section, so an older build skips it by its length) holds what no machine state holds: the table of overrides, the original bytes, and the Genesis hook's volume memory. On opening, the section is checked first (version, CRC-32, ranges: a damaged, cut or newer one is refused with its reason and shown on the tab; the project opens without the pushes); it is installed on the loading thread after the machine state is back; a project whose game state was not restored drops the pushes and says why.

## 4. Verification (every verifier shown able to fail)

`FruityHost inject-test` (no DLL, real games) and `selftest <dll> --only inject` (through the Native AOT DLL built the way `Plugin/build-dist.ps1` does).

### Library level, on the game's own audio (`inject-test`)

| check | what is measured | result |
|---|---|---|
| `snes-determinism`, `snes-identity` | a session loaded from the saved machine plays 191,685 frames of audio twice with the same hash; a table whose every sample has weights 127 (the identity) plays the same hash as no table | hash 259D0621790566C6 both |
| `snes-sample-bytes` | Randomise (strength 15%) pushed: the game's audio RAM holds the edited sample; the solo audio's spectral centroid moves | 871 Hz -> 3013 Hz |
| `snes-tuning+1200`, `-1200` | the strongest FFT peak of the game's own audio (all other samples muted by the test, same notes) | median x**2.000** (46 windows, 31 within 3%), x**0.500** (46 of 46) |
| `snes-tuning-control` | a push that does not touch the pitch | 46 of 46 windows within 1% |
| `snes-envelope-attack` | attack rate 1 forced | rms 6% of the unpushed |
| `snes-envelope-gain` | GAIN direct $30 forced: ENVX of the voices playing the sample | $30 in 658 of 675 observations (the rest are releases of notes that began before); without the push 8 of 687 |
| `snes-volume-weights` | left 0, right 127 | left rms 0.00000, right unchanged (0.00956 vs 0.00956) |
| `snes-noise-flag` | noise forced | the power at the sample's tone: 0.00% (SMW's noise clock is not set: a flat level) |
| `snes-farm-writes` | the bytes the push reports, replayed on the machine as it was, give the memory the push left | 538 writes, same |
| `snes-remove`, `snes-remove-overrides` | push (sample, tuning, envelope, weights) then remove: audio RAM hash back, no override, the next 360 frames of the game's audio | back, hash 259D0621790566C6 = untouched |
| `snes-replace`, `snes-nothing-to-push` | +1200 then +100 = a single +100 push; an unedited sample is refused | equal hash; refused |
| `snes-alloc` | two seconds of the S-DSP with a table in force | 0 bytes (the control sees a 4 KB array) |
| `snes-persist`, `snes-persist-damaged` | the reopened game plays the hash of the game that stayed open; the machine without the section plays another; remove works on the reopened game; 129 of 129 one-byte-flipped sections refused, newer and cut ones refused | 0DFF81D3761BB16D = 0DFF81D3761BB16D (without the section 149FD9984F73696E) |
| `genesis-fm-octave` | every operator multiple of FM 2 doubled: the strongest FFT peak of the game's own FM 2 stem | median x**2.000** (53 windows, 43 within 3%) |
| `genesis-fm-level` | carriers +24 on the total level (18 dB) | rms x0.147 (expected about 0.126) |
| `genesis-neutral-push` | AMS changed with the LFO off: the hook runs at every key-on and changes nothing audible | game audio hash identical |
| `genesis-remove`, `genesis-remove-registers` | remove at once = untouched audio; after 40 frames the edited patch is in the chip's registers of FM 2, after Remove they are the untouched game's | equal; equal |
| `genesis-persist`, `genesis-persist-volume-memory` | saved at once after the push; saved 60 frames later (the hook's volume memory must come back or the level change is added twice) | hash = the game that stayed open; the machine without the section plays the untouched game |
| `genesis-hook-patch` | on a bare chip: edited operators loaded, the driver's volume and pan kept, a volume change by the driver gets the edit's change on top, another channel with the same patch edited, a different patch left alone | all |
| `genesis-dac-stream` | the stream hook: first 11 samples unchanged, edited from the 12th, silence past the end, recognised again on the next play, other streams untouched, rate x2, a loop | all |
| `genesis-dac-amplitude` | every DAC sound found (12) pushed at half amplitude: the game's DAC stem | rms x0.51; 17 sounds recognised in the stream |
| `genesis-dac-tone` | every DAC sound replaced by a 1 kHz tone: power between 900 and 1100 Hz | 89x |
| `genesis-alloc` | 120 frames with the hook | the hook adds 0 bytes |
| `gb-wave-unit`, `gb-wave-game`, `gb-wave-remove` | bare unit: the trigger loads the edited wave and the unit then plays exactly what a unit handed that wave plays; Link's Awakening: the wave RAM held the edit at 600 of 600 frame starts, the game's audio differs; pushed and removed at once = untouched hash | all |
| `opll-user-unit`, `opll-user-game`, `opll-user-remove` | bare chip as above; Galactic Protector (86 user-instrument key-ons in the window): the audio differs; push+remove at once = untouched | all |

Deliberate bugs, each switched on in turn (`PushMutants`), each caught by the check named: the push writes no sample bytes (`snes-sample-bytes`, `snes-farm-writes`), drops the tuning (`snes-tuning+1200`: x1.000), the envelope (`snes-envelope-attack`, `snes-envelope-gain`), the weights (`snes-volume-weights`), the flags (`snes-noise-flag`), a table with weights off by one (`snes-identity`), remove leaves the bytes
(`snes-remove`) or the overrides (`snes-remove-overrides`, `genesis-remove`), the checksum is not checked (`snes-persist-damaged`: only 32 of 129 flipped sections refused), the reopened game gets no hooks (`snes-persist`, `genesis-persist`), the hook's volume memory is not restored (`genesis-persist-volume-memory`), no key-on loads the edited patch (`genesis-fm-octave`, `genesis-fm-level`), the driver's volume is replaced by the edit's absolute level
(`genesis-hook-patch`), the edited rate is ignored (`genesis-dac-stream`), the DAC stream is not substituted (`genesis-dac-amplitude`, `genesis-dac-tone`), remove leaves the edited registers in the chip (`genesis-remove-registers`), the Game Boy trigger / the YM2413 key-on does not substitute (`gb-wave-*`, `opll-user-*`).

### Through the DLL (`selftest --only inject`, 4 real games)

For each of Super Mario World, Sonic the Hedgehog, Link's Awakening and Galactic Protector: a base project at a place where music plays; control = two runs of the project give the same machine hash. Then: Runaway on, the model's edit applied to the working copy, **push through the plugin** (accepted, count 1, hooks present, the corruption farm poisoned and (SNES, Game Boy) told of memory writes, the game's own sound memory changed where bytes are written);
**Runaway off** and 240 (600 for the Master System) frames: the machine differs from the control; a **project saved with the push in place, reopened**: count 1, no note, the same sound memory, the same hooks, and after the same frames the machine hash equals the game that stayed open; the same project **without the `INJ1` section** plays another game (SNES, Genesis) and shows no push; a **damaged** and a **newer** section: no pushes, the note on the tab, the game still comes back at its frame and in Runaway;
**push + Remove at once**: sound memory as it was and the machine after the same frames equals the control; **Reset Console** takes the pushes with it and says so; **the deliberate bugs "the farm is not told" and "a reopened project gets no hooks"** are each caught; 40 blocks of the pushed game allocate **0 bytes** on the audio thread (the same game unpushed: 0).
**The tab, with real mouse messages** (a SNES test game given the made-up sound memory in itself as well): Remove with nothing pushed is refused ("not pushed"), Push of an unedited sample is refused ("nothing to push"), ten shift-clicks on Tuning's plus then Push says what it did and the list shows "in the game", Remove says "Removed sample 0" and the mark goes; the reopened project marks it and Remove works; a damaged section shows the note on the tab; two bugs ("Remove pushes", "the tab hides the note") are caught.

Unchanged by this work, run on the final tree (see section 5): the full `selftest` (the new `inject` check is the 56th), `--snesbench` goldens, `--snes-state-test`, `--md-test`, `--md-state-test`, `--md-golden`.

## 5. Neutrality of the hooks (the shared emulator tree)

(Filled in at the end of the work, see "Regression runs".)

## 6. Not tested

- **Real FL Studio.** The record of the `ui` agent applies: nothing was tried in FL. The Remove / Push buttons are driven by real mouse messages in the test host only.
- **Hearing it.** No sound card: pitch (FFT), amplitude (RMS, ENVX), spectrum and hashes only.
- **A game that reloads its sound set** while pushed (SMW's sample table did not change in 4,300 frames, so the SNES "the bytes stay" claim has no example of the opposite); the overrides stay, the sample bytes would be the game's again. The Remove message counts the bytes the game has written over.
- **The SNES on the HLE core, the mix lab's NES-on-SNES unit**: the push is refused ("a push needs the SFC audio unit"); not driven through the tab.
- **Games other than the four**: the checks pick the instrument by what the music plays most in a window, with a rig per console, but only these four games were run.
- **A Genesis DAC sound that the analyzer split into pieces** (Sonic has 12 "sounds", several of them the same drum cut at different places): each piece is pushed on its own by the test; a user pushing one piece gets that piece.
- **Game Boy Color, Game Gear** specifically (the code paths are the Game Boy's and the Master System's; Dr. Mario DX and the Addams Family were not run through a push).
- Pushing while two plugin instances share the emulator and edit at once.

## 7. Decisions and findings

- **Key-on override keyed on the live SRCN register, no state.** A latched voice index would have needed a new field in the DSP's saved state (goldens, the 147-test state suite); reading SRCN every sample is what the hardware does for the loop pointer anyway, and a saved machine plays the same with the same table.
- **Overrides are applied where the chip reads, never written into the driver's registers**, so a driver that reads them back sees its own values.
- **Patches are matched by content**, not by channel: the same voice on three channels is edited on all three, and pushing the same patch from another channel's tab replaces the earlier push (the message says so).
- **The original is kept by the first push** and survives a second push, a re-freeze and a reopened project (INJ1), so Remove works in all of them.
- **Edits that change nothing are refused or taken for removal** ("nothing to push"; an instrument edited back to the game's own removes the earlier push).
- **Finding outside my domain:** `--md-golden` reports Titan Overdrive 1 frame 1007 changed (hash C740C2A7D83CE55F) on `main` 7bd01f1 itself (a baseline build of main gives the identical result); it is the frame the hand-off guide already lists as chaotic. Not caused by this work.
- **Finding:** `Emulator.InstrumentNote` (imodel's note for a project whose overlay was damaged) is not shown anywhere in the UI; the new `Notice` member could carry it (one line in `EmulatorInstrumentProvider.Notice`).
- A project reopened during a test must have the instance that saved it disposed first (an Auto instance joins the existing emulator): the first version of the DLL check compared a machine with itself.

## 8. Open problems and next steps

1. Game Boy pulse and noise (a trigger hook for duty / envelope) and a PSG story (a fixed duty or noise rate as an overlay) are the gaps on the consoles that have instruments at all.
2. A Genesis DAC sound is recognised by its first 12 samples; two sounds that start with the same 12 samples cannot be told apart (the first one in the list wins).
3. First FL session: push, switch Runaway off, listen; the whole feature is unheard.
4. A "Remove all from game" button (the library has `RemoveAll`, tested through hook 329; no button).
5. The SNES key-on override could also carry a sample's directory entry (moving a sample to another address), which the model forbids today.
