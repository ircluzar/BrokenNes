# Known gaps

This file lists corners cut on purpose, plus gaps found along the way, so a later pass knows what is
left to do. It covers the whole repo; each initiative adds its entries under its own area.

- **Priority**
  - **P1:** breaks something a player or a build would hit.
  - **P2:** noticeable, or blocks a planned feature.
  - **P3:** polish.
- **Status**
  - `open`
  - `in progress`
  - `fixed (<commit>)`: fixed entries stay here as history.
- **Upkeep:** add or update an entry in the same commit as the shortcut or the fix.

## Foreign APU project (`docs/projects/foreign-apu-project.md`)

Alternate SNES and GB sound chips ported from old emulators: ZS15, S9X1, OSPC, GNUB, VBA. The user's
call was quick and dirty first, with a smoke test only.

| ID | Pri | Gap | Where / how to fix | Logged | Status |
|---|---|---|---|---|---|
| FA-1 | P1 | **Licensing is unresolved.** These are GPL, LGPL and non-commercial ports, a documented exception to the repo policy. Do not ship builds that include them outside personal/art use until this is decided. | Root `THIRD_PARTY_NOTICES.md` §2. The options are listed there: keep them as optional cores distributed separately, get permission from the authors, or replace them with clean-room cores that imitate the sound. | 2026-11-01, foreign APU MVP (`9cbfc3c1`…`6497e72a`) | open |
| FA-2 | P2 | **Nobody has listened to them.** The only check is a smoke test (loudness-envelope correlation against the stock chip); there are no bit-exact checks against the original programs. | Listen on Windows. Optionally build the originals in a sandbox (for example zsnes-asm2c `spcplay`) and compare the PCM. | 2026-11-01, foreign APU MVP | open |
| FA-3 | P2 | **The SNES cores do not save their engine state.** Save states hold the stock S-DSP only. A load or a unit swap re-feeds the registers and re-keys the voices, so held notes restart. | `snes/APU_ZS15.cs`, `APU_S9X1.cs`, `APU_OSPC.cs` and the engines. Add versioned engine state after `Dsp.SaveState` in `APU_SFC.State.cs` through a virtual hook, then serialize each engine's voice state. | 2026-11-01, `9cbfc3c1` | open |
| FA-4 | P2 | **GB swaps lose sound.** Swapping a running game to GNUB starts the engine fresh. VBA loads `APU_GB`'s register block, but notes stay silent until the game triggers them again. | `gb/APU_GNUB.cs`, `APU_VBA.cs` `LoadState`, `TrySwapCore` in `systems/ConsoleSessions.cs`. Apply the old chip's registers and re-trigger the channels that were on. | 2026-11-01, `5e7293c4` / `095c3676` | open |
| FA-5 | P2 | **No cards or progression.** The cores are only in the SNES and GB menus. The planned blind-bag-only card integration is not done. | `Windows/Data/default-db.json` + `Webmodules/shared/models/default-db.json`, `SvgFactory.cs` (both copies), `continue.js` `pickRandomBonusCards`. BrokenNes2's cards are NES-only today, so SNES/GB core cards need designing first. | 2026-11-01, foreign APU MVP | open |
| FA-6 | P3 | **GNUB and VBA are not `APU_GB`.** These skip them through `is` checks, without crashing: BrokenDev channel mute (`debug/DebugSound.cs`), the WAVE memory domain, the plugin's GbSnapshot, sampler and instrument push, and FruityHost's inject checks. | Extract the members those tools use (`Channel(n)` views, `WaveRam`, `NR50/51`, `DebugMute`) into an interface that the ported chips implement. | 2026-11-01, `5e7293c4` / `095c3676` | open |
| FA-7 | P3 | **The SNES cores run the stock S-DSP alongside the ported engine.** Games read ENDX, ENVX and OUTX from the stock DSP, not from the ported engine, so drivers that poll ENDX keep stock timing; this is less authentic. Each core also costs two DSPs' worth of CPU. | `snes/APU_SFC.cs` hooks. Let a unit answer `$F3` reads from its engine (a virtual `OnDspRegRead`), then see whether the stock DSP can stop mixing. | 2026-11-01, `9cbfc3c1` | open |
| FA-8 | P3 | **OSPC and S9X1 echo uses a private buffer, not sound RAM at ESA.** The stock DSP still writes its own echo into ARAM, so the two cannot share it. Games that read echo RAM back, or rely on echo overwriting sample data, behave like stock. | `snes/OspcDsp.cs`, `S9x1Dsp.cs`. Fixable together with FA-7, once the stock DSP stops writing echo. | 2026-11-01, `67d29109` / `99b8a7e5` | open |
| FA-9 | P3 | **DSP writes made by tools are not mirrored.** Corruption tools and debuggers call `Dsp.Write` directly, so they change the stock DSP and not the ported engine. Corrupting DSP registers therefore has no audible effect on these cores. | `snes/APU_SFC.cs`. Route tool writes through a method that also calls `OnDspRegWrite`, for example `corruption/MemoryDomains.cs` around line 240. | 2026-11-01, `9cbfc3c1` | open |
| FA-10 | P3 | **S9X1's noise is snes9x2005's replacement LFSR** (blargg's, from Snes9x 1.53), not the original 1.43 noise generator. | `snes/S9x1Dsp.cs`. Port the noise code from a real Snes9x 1.43 source tree. | 2026-11-01, `99b8a7e5` | open |
| FA-11 | P3 | **OSPC is from a 2003 snapshot.** The original 2002 tarball is not in the libopenspc history. | `snes/OspcDsp.cs`. Swap in the 2002 code if the tarball turns up (archive.org). | 2026-11-01, `67d29109` | open |
| FA-12 | P3 | **The ported cores are not on the WideMess console's sound menu.** Only the SNES menu lists ZS15, S9X1 and OSPC. | `systems/ConsoleSystems.cs`, WideMess APU list. Needs a decision on how a ported engine and the WideMess's extra voices coexist. | 2026-11-01, foreign APU MVP | open |
| FA-S-BLST-1 | P3 | BLST PSG writes land at host-buffer boundaries, not at their clock. | `md/MdSoundUnitBlst.cs`, `sega/SmsSoundBlst.cs` `OnPsgSamples`: queue the writes with timestamps. | 2026-11-01, `e3b41ed3` | open |
| FA-S-BLST-2 | P3 | BLST PSG has no negative half and is ~9 dB quieter than stock on SMS/GG (BlastEm's own level, kept). | `sega/SmsSoundBlst.cs`: an optional gain / DC offset if wanted. | 2026-11-01, `e3b41ed3` | open |
| FA-S-BLST-3 | P3 | BLST ignores Game Gear stereo, and a Z80 reset does not reset the BlastEm engines. | `SmsSoundBlst.OnStereoWrite`; `MdSoundUnit` needs a Z80-reset hook. | 2026-11-01, `e3b41ed3` | open |
| FA-S-M036FM-1 | P3 | M036FM engine state (OPL + translator) is not in save states; a load or a swap restarts it and held notes are lost until rewritten. | `sega/SmsFmSoundM036.cs` `OnReset`; serialize `Mame036Opl` / `Mame036Ym2413`. | 2026-11-01, `aa7bad36` | open |
| FA-S-M036FM-2 | P3 | M036FM FM is ~3x louder than stock SMS-FM (the original ignores volumes); the output scale (>>2) is a guess. | `SmsFmSoundM036.OutShift`. | 2026-11-01, `aa7bad36` | open |
| FA-S-M036FM-3 | P3 | M036FM is not in the stems / hub voices: they still come from the stock YM2413. | `sega/SmsFmSound.cs` stem path. | 2026-11-01, `aa7bad36` | open |
| FA-S-TOOL-1 | P3 | Before `aa7bad36`, `--sms-render` never enabled FM ($F2 bit 0): earlier SMS-FM reference renders were PSG only. Fixed in `aa7bad36`; ports smoke-tested against SMS-FM before then compared against PSG only. | `Workshop/Sega/SmsRenderCli.cs` | 2026-11-01, `aa7bad36` | fixed (`aa7bad36`) |
| FA-S-M036-1 | P3 | M036 YM2612: CSM auto key-on (timer A) and the timers are not ported (the stock chip keeps the timers for the game). | `sega/Mame036Opn2.cs`: port `INTERNAL_TIMER_A` / `CSMKeyControll`, driven from the stock timer-A overflow. | 2026-11-01, `2c32d62d` | open |
| FA-S-M036-2 | P3 | M036 PSG writes land at the next host drain, not at their exact time. | `Mame036Sn76496.FillHost`: queue the writes with timestamps (needs a time on `SmsSound.OnPsgWrite`). | 2026-11-01, `2c32d62d` | open |
| FA-S-M036-3 | P3 | M036 state load or swap re-feeds the registers only; envelopes and phases restart and notes resume at their next key-on. | `MdSoundUnitM036` / `SmsSoundM036` `OnReset`: save the engine state. | 2026-11-01, `2c32d62d` | open |
| FA-S-M036-4 | P3 | M036 is louder than stock (~1.6-1.9x AC RMS) and the PSG output carries a DC offset (the original's level, kept). | Scale in `OnFmSample` / `FillHost` if wanted. | 2026-11-01, `2c32d62d` | open |
| FA-S-GENS-1 | P3 | GENS on Game Gear ignores the stereo port (Gens' PSG is mono). | `SmsSoundGens`: apply the GG $06 masks per channel in `GensPsg.Update`. | 2026-11-01, `3ef3944a` | open |
| FA-S-GENS-2 | P3 | GENS (SMS/GG) places PSG writes at chunk granularity, not at their time. | A timed write hook in `SmsSound`, or queue the writes with the stock PSG's clock. | 2026-11-01, `3ef3944a` | open |
| FA-S-GENS-3 | P3 | GENS (Genesis) loses its YM/PSG state on a state load or swap: silent until the game writes again. | `MdSoundUnitGens.OnReset`: replay the stock `Ym2612` / `Sn76489` registers into the Gens chips. | 2026-11-01, `3ef3944a` | open |
| FA-S-GENS-4 | P3 | GENS clamps FM and PSG separately (Gens clamps their sum), halves Gens' level and adds about one frame of latency. | `MdSoundUnitGens` frame driver. | 2026-11-01, `3ef3944a` | open |
| FA-S-PICO-1 | P3 | PICO DAC is not tied to scanlines (PicoDrive fills it by line) and was never exercised: the test VGM has no DAC writes. | `md/MdSoundUnitPico.cs` `OnFmSample`; add a DAC test VGM and a line-based fill. | 2026-11-01, `050cbcca` | open |
| FA-S-PICO-2 | P3 | PICO after a state load or live swap: notes already playing go silent until the next key-on (the stock state has no key/envelope state to replay). | `MdSoundUnitPico.BuildYm`: replay the key state if the stock `Ym2612` exposes it. | 2026-11-01, `050cbcca` | open |
| FA-S-PICO-3 | P3 | PICO mixes FM and PSG separately (the shared 16-bit clip is lost); FM is up to one frame late against live DAC writes. | `md/MdSoundUnitPico.cs`. | 2026-11-01, `050cbcca` | open |
| FA-DARC-1 | P3 | DARC engines' own state is not in save states. | `APU_DARC` / `SmsSoundDarc`: serialize the engine. | 2026-11-01, `e2adddae` | open |
| FA-DARC-2 | P3 | DARC NES ignores PAL timing (NTSC 7,457-cycle sync and clock). | `APU_DARC`: take the region's CPU clock. | 2026-11-01, `e2adddae` | open |
| FA-DARC-3 | P3 | DARC output is unipolar with a large DC offset (the original's). | A DC blocker in the polish pass, if it clicks. | 2026-11-01, `e2adddae` | open |
| FA-NSTC-1 | P3 | NSTC engine state is not in save states; after a load it plays on until the next register write. | `apus/APU_NSTC.cs`: serialize the `NesticleSound` channels in GetState/SetState. | 2026-11-01, `4f1b056c` | open |
| FA-NSTC-2 | P3 | No upstream commit hash recorded for NESticle (GitHub API/HTML blocked; raw files from branch master). | Header of `apus/NesticleSound.cs`: pin the hash when reachable. | 2026-11-01, `4f1b056c` | open |
| FA-NSTC-3 | P3 | NSTC register writes take effect per sample, not at NESticle's per-tick block boundaries. | `apus/APU_NSTC.cs`: batch into ~367-sample blocks per frame if the sloppier timing is wanted. | 2026-11-01, `4f1b056c` | open |
| FA-S951-1 | P3 | S951 engine state (envelopes, sample positions, echo buffer) is not in save states; a load/swap re-feeds the DSP registers and re-keys the voices. | `snes/APU_S951.cs` `OnReset`; serialize the `S951Dsp` channels. | 2026-11-01, `b5da264b` | open |
| FA-S951-2 | P3 | S951 register writes take effect only at 533-frame block boundaries (one mix per frame). | `snes/APU_S951.cs`: render smaller blocks. | 2026-11-01, `b5da264b` | open |
| FA-S951-3 | P3 | S951: MixMono, the 8-bit output paths and the Settings toggles (ReverseStereo, FixFrequency, FakeMuteFix, non-interpolated) are not ported. | `snes/S951Dsp.cs`. | 2026-11-01, `b5da264b` | open |
| FA-INES-1 | P3 | INES engine state is not in save states (only APU_FIX's). | `APU_INES` GetState/SetState: add the `InfoNesApu` fields. | 2026-11-01, `a0b7ca52` | open |
| FA-INES-2 | P3 | INES frame render runs on a 29,781-cycle counter, not the PPU's scanline 241 (~0.16% more samples than 44,100/s). | `APU_INES`: hook the PPU frame event. | 2026-11-01, `a0b7ca52` | open |
| FA-INES-3 | P3 | InfoNES upstream commit SHA not recorded (API blocked). | Header of `apus/InfoNesApu.cs`. | 2026-11-01, `a0b7ca52` | open |
| FA-FCEU-1 | P3 | FCEU's VRC7 (on OPL2), 5B, VRC6, N163 and FDS sound are not ported (`mappers/vrc7snd.c` + `fmopl.c`, `69.c`, `24and26.c`, `19.c`, `fds.c`); BrokenNes2 has no mappers for them. | Port as `apus/FceuExpansion*.cs` once the mappers exist. | 2026-11-01, `a3847f3f` | open |
| FA-FCEU-2 | P3 | APU_FCEU's MMC5 sound is untested (no MMC5 ROM in the smoke test). | Render an MMC5 game, compare with FIX + `MMC5Audio`. | 2026-11-01, `a3847f3f` | open |
| FA-FCEU-3 | P3 | APU_FCEU: NTSC only; the channel mask honours only "all off"; after a mid-game switch a channel is silent until its registers are written again. | `apus/FceuApu.cs`: PAL tables; replay the base's registers into the engine on switch. | 2026-11-01, `a3847f3f` | open |
| FA-B3-1 | P3 | The DARC agent first put its id in `NesPpuFamily` instead of `NesApuFamily`; fixed at merge (the coordinator rebuilt both lines). Worth a check that every foreign NES id is in `NesApuFamily` only. | `systems/ConsoleSystems.cs` | 2026-11-01, merge of batch 3 wave 1 | fixed |
| FA-ZS151-1 | P3 | ZS151 engine state (envelopes, sample positions, echo buffer) is not saved; a state load re-keys the voices from the DSP registers. | `APU_ZS151.OnReset`; serialize the `Zs151Dsp` voice fields. | 2026-11-01, `33f7502b` | open |
| FA-ZS151-2 | P3 | ZS151: only the default path is ported (not the mono, non-interpolated, cubic and 8-tap mixers, low-pass filters, surround, reversed stereo). | `snes/Zs151Dsp.cs`: port the rest of `dspproc.asm` if options are wanted. | 2026-11-01, `33f7502b` | open |
| FA-ZS151-3 | P3 | ZS151 echo, noise and pitch modulation are not checked by ear (musictest does not use them). | Render an echo-heavy SPC and compare with ZSNES 1.51. | 2026-11-01, `33f7502b` | open |
| FA-ZS151-4 | P3 | ZS151 key-on timing is approximate (ZSNES's per-scanline key-on stepped every other sample; renders in 160-frame blocks). | `APU_ZS151.OnDspSample`: step it from the SNES scanline counter instead. | 2026-11-01, `33f7502b` | open |
| FA-DEGA-1 | P3 | DEGA writes land at chunk granularity, not inside the chunk. | `SmsSoundDega.OnPsgSamples`; needs write timestamps from the `SmsSound` hooks. | 2026-11-01, `b86d07ac` | open |
| FA-DEGA-2 | P3 | DEGA: the 44,100 Hz rate is assumed (Dega's frontend not checked); the non-enhanced mode is not on any menu; no upstream commit hash recorded. | `SmsSoundDega` (DegaRate, enhance flag). | 2026-11-01, `b86d07ac` | open |
| FA-DEGA-3 | P3 | DEGA Game Gear stereo path not smoke-tested (the test VGM has no stereo writes). | Render a GG VGM with port $06 writes. | 2026-11-01, `b86d07ac` | open |
| FA-TGBD-1 | P3 | TGBD is not an `APU_GB`: BrokenDev mute, the WAVE domain, the plugin sampler and instrument push skip it (as GNUB/VBA, FA-6). | `gb/APU_TGBD.cs`. | 2026-11-01, `e1bc5ab9` | open |
| FA-TGBD-2 | P3 | A swap to TGBD restarts the engine from the registers: notes stay silent until re-triggered. | `APU_TGBD.LoadState`: map the `APU_GB` channel state into `TgbdStat`. | 2026-11-01, `e1bc5ab9` | open |
| FA-TGBD-3 | P3 | TGBD: echo not ported, the low-pass filter fixed off (its default), dual-GB mode not modelled, DC offset ~1,450. | `gb/TgbdApu.cs`: expose b_lowpass / port the echo if wanted. | 2026-11-01, `e1bc5ab9` | open |
| FA-S916-1 | P3 | S916 engine state is not saved; a state load re-feeds the DSP registers and re-keys the voices. | `snes/APU_S916.cs` `OnReset`; serialize the `S916Dsp` channels. | 2026-11-01, `e7b8f2db` | open |
| FA-S916-2 | P3 | S916: only the default 16-bit stereo mix is ported (mono/8-bit paths and other rates left out). | `snes/S916Dsp.cs` RenderBlock/MixStereo. | 2026-11-01, `e7b8f2db` | open |
| FA-VBAM-1 | P3 | A core swap away from VBAM loses frequencies: the `APU_GB`-format block saves the masked NR13/14/23/24/33/34 values ($FF). | `APU_VBAM.SaveState`: expose the engine's shadow frequency registers and save those. | 2026-11-01, `7605d654` | open |
| FA-VBAM-2 | P3 | VBAM's SGB NR52 reset value ($F0) is unreachable: there is no SGB `GbModel`. | `VbamGbSound.Reset`: pick it once an SGB model exists. | 2026-11-01, `7605d654` | open |

## Linux / cloud dev setup (`Workshop/cli.sh`, `Workshop/setup-linux.sh`)

| ID | Pri | Gap | Where / how to fix | Logged | Status |
|---|---|---|---|---|---|
| LX-1 | P3 | **GDI+ does not exist on Linux.** Workshop modes that read or write PNGs through `System.Drawing` throw. Examples: the `--gbtest` groups `dmg-acid2` and `mealybug` (27 ROMs), `--mixlab` screenshots (work around with `--png-at 999999`), `MdRunCli`, `WideMessGuestSheet`. | Replace `Bitmap` in those paths with a small managed PNG encoder/decoder, for example in `Workshop/` shared helpers. | 2026-11-01, `260febe0` | open |
| LX-2 | P3 | **Most BrokenDev and UAT PowerShell scripts call Windows `.exe` paths** (`Workshop/bin/Release/net10.0-windows/...`). Only `UAT/wav-compare.ps1` has been checked on Linux. `BrokenDev/doctor.ps1` requires Windows. | Let the scripts find `Workshop/cli.sh` (or `bin-cli/.../BrokenNes.Workshop.dll`) when `$IsLinux`. | 2026-11-01, `260febe0` | open |
| LX-4 | P3 | **A tag called `main`** (an old commit, `86787591`, 2026-03-31) shares its name with the `main` branch. Tools that resolve `main` can pick the tag, and a cloud session's branch was once cut from it, so it started 956 commits behind. It does not change what GitHub visitors see (the default branch is the `main` branch). | With push access: `git fetch origin refs/tags/main:refs/tags/main`, then `git push origin refs/tags/main:refs/tags/v1-final-2026-03-31`, then `git push origin :refs/tags/main`. Cloud sessions can only push their own branch. | 2026-11-01, setup | open (do later) |
| LX-3 | P3 | **The Windows-only modes can't be checked here:** `WorkshopForm`, `--snes`, `--gb`, `--input-test` and the desktop app. Changes to them are compile-checked only (`-p:EnableWindowsTargeting=true`). | Needs a Windows runner (local or CI). | 2026-11-01, `260febe0` | open |
