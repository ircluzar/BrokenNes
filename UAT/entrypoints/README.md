# Entrypoint smoke tests

One script per shipped entrypoint. Each exits 0 on pass / 1 on fail, prints human-readable
`[PASS]/[FAIL]` lines, and ends with ONE JSON result line (`entrypoint`, `pass`, `checks`, `notes`,
`seconds`). Run under `pwsh -File`. Build/publish output goes to
`%LOCALAPPDATA%\VRUN_Nes_Dev_work\uat_desktop|uat_web`, never into the repo. The header comment of each
script lists exactly what is and is not verified.

| Script | Entrypoint | Typical time |
|---|---|---|
| `desktop-smoke.ps1` | `Windows\BrokenNes.Windows.csproj` (WinForms app), driven through `UAT\lib\ApiClient.ps1` | ~15 s warm build, +~60 s cold build |
| `web-smoke.ps1` | `WebLite\BrokenNes.Lite.csproj` (Blazor WASM), served by `python -m http.server`, driven in headless Edge/Chrome over the DevTools protocol | ~25 s warm publish, a few minutes cold |
| `lite-sega.ps1` | `WebLite\BrokenNes.Lite.csproj` AOT build with the Master System, Game Gear and Genesis, driven by `UAT\lite\test-lite-sega.mjs` in a real Edge over the DevTools protocol (node 22+; the speed table is `UAT\lite\bench-lite.mjs`, `-Bench`) | AOT publish ~6 min, suite ~15 min |
| `menu-race-desktop-smoke.ps1` | the desktop app (`-Exe`), 4 starts with scratch config / saves / bugs folders: a Game Boy Color ROM POSTed to `/api/emulator/load-rom` the moment the discovery file appears, then again; no error dialog or error in the log, the load after the window is up, the game running. Negative control: the unfixed build (311aec8) fails 3 rounds of 4 | ~1 min |
| `audio-device-desktop-smoke.ps1` | the desktop app (`-Exe`) with the test hook `BROKENNES_TEST_AUDIO_DEVICE_FILE` ("none" = Windows has no playback device): start-up with none (silent, no dialog, title note, ~60 fps), a device appearing (opened without a restart), lost mid-game (silent, no dialog, still ~60 fps) and back (reopened), each with a control; the real device at start-up. Negative control: the unfixed build fails | ~1.5 min |
| `audio-pacing-desktop-smoke.ps1` | the desktop app (`-Exe`, `-Workshop`) on the machine's REAL default device, NES (VRUN) and Game Boy (the VOC sound-effects test ROM), 60 s each after a warm-up, volume 10 %: fps against the console's own rate (within 1 %), device underruns in steady state (0), the rate the device really takes sound at (within 1.5 % of 44100), the queue (<= 150 ms), and a WASAPI process loopback of our PID only (`UAT/lib/AudioLoopback.cs`: the user's other windows are left out) whose dropouts (0.5-30 ms of digital silence inside sound) are compared with the headless Workshop render of the same ROM. Negative control: `BROKENNES_AUDIO_LEGACY=1` (the old WinMM output + audio-driven pacing) must reproduce the drop (~44 fps, the device taking ~73 % of real time, ~17 dropouts a second) | ~6 min |
| `core-roster-desktop.ps1` | the desktop app (`-Exe`), three runs with throw-away config folders: an older config.json's retired `DMGX` picks moved to `DMGS` and written back, the negative control (an unknown id falls back to FIX), the menus the API serves (stock / enhanced order and labels), an old id through `POST /api/console/core` | ~1 min |

```powershell
pwsh -File UAT\entrypoints\lite-sega.ps1 [-Publish <dir>] [-Roms <dir>] [-Only rom,saves] [-Bench]   # the Lite Sega suite (publishes AOT unless -Publish)
pwsh -File UAT\entrypoints\desktop-smoke.ps1                 # builds, runs VRUN (mapper 30, FIX cores) 8 s
pwsh -File UAT\entrypoints\desktop-smoke.ps1 -Exe <path>     # skip the build
pwsh -File UAT\entrypoints\web-smoke.ps1                     # non-AOT publish, boots /?rom=vrun.nes
pwsh -File UAT\entrypoints\web-smoke.ps1 -SkipPublish        # reuse last publish
pwsh -File UAT\entrypoints\web-smoke.ps1 -Aot                # needs wasm-tools workload; slow
```

## Caveats worth knowing

- Desktop: kills nothing by name (README rule zero). It only closes the PID it started, gracefully;
  a forced kill counts as FAIL. The emulator's NES audio is NOT exposed by the HTTP API
  (`/api/audio/*` is the game's music engine, `/api/apu/channels` is hard-coded), so audio is measured
  from the Windows Core Audio session meter for that PID (reported "unverified", not failed, if no
  session exists).
- Desktop: `POST /api/cores/apply` persists to `%APPDATA%\BrokenNes\config.json` (shared by every build on this
  machine), so the script always applies FIX/FIX/FIX. At the end it closes the window **while that request is being
  POSTed in a loop**: closing with an API request in flight (one that marshals onto the UI thread) used to deadlock
  `OnFormClosing`, because the host's shutdown waited for the request and the request waited for the UI thread (fixed
  2026-10-01 by stopping the server off the UI thread; this check keeps it fixed).
- Web: audio is not verified (headless browser, no sample tap). The picture check rejects both a blank
  canvas and the "no signal" static-noise screen (>200 colours). The bundled `WebLite\wwwroot\vrun.nes`
  is a different file from the VRUN build used by the desktop smoke.
