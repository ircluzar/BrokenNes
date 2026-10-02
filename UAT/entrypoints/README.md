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

```powershell
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
