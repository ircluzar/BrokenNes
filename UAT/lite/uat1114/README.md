# Lite UAT harness (headless Edge, CDP)

This drives BrokenNes Lite in a **headless Edge** with background throttling turned off. Touches are real
`Input.dispatchTouchEvent` events and keys are real `Input.dispatchKeyEvent` events. It needs Node 22+ and no
packages, and it never touches the user's own Edge profile: every run gets a fresh `--user-data-dir` under `--out`.
The PIDs it starts go into `<out>/pids.txt`, and each run kills only its own PID.

## Setup

```
# 1. Builds. Use a worktree so the shared WebLite/bin and obj folders stay untouched.
#    Release needs EnableWasmAot=true. A plain -c Release publish is NOT AOT.
dotnet publish WebLite/BrokenNes.Lite.csproj -c Release -p:EnableWasmAot=true -o <scratch>/liteaot
dotnet publish WebLite/BrokenNes.Lite.csproj -c Debug -o <scratch>/litedbg
# 2. Serve each build, plus a ROM folder at /roms/.
node serve.mjs <scratch>/liteaot/wwwroot <romDir> 5098
node serve.mjs <scratch>/litedbg/wwwroot <romDir> 5097
# 3. Phases
node run.mjs --base http://127.0.0.1:5098 --out <outDir> --build aot --phase boot      # every ROM: boot, fps, audio level, keyboard play, screenshots
node run.mjs ... --phase speed [--frames 400]   # the page's own FrameTick in a timed loop (does not depend on the animation clock)
node run.mjs ... --phase cores [--max N]        # every core in every slot's menu, hot-switched mid-game
node run.mjs ... --phase touch                  # phone portrait and landscape: layout, every button, multi-touch, slide, mouse, game reaction
node run.mjs ... --phase state                  # save, move, load; load after a core switch; load after a page reload
node run.mjs ... --phase battery                # VRUN pause+Select save, plus SRAM games: IndexedDB keys before and after
node run.mjs ... --phase misc --romdir <romDir> # file picker, drag and drop, ROM list and core choice across a reload, fullscreen
```

Use `--roms nes,snes` or `--roms <file>` to narrow a run. `--port` sets the DevTools port (default 9333).

## ROM folder

The ROM names are listed in `ROMS` at the top of `run.mjs`. Our own games come from `T:\VRUN_Demo` and the GB
test ROMs from `Windows\Resources\gb-test-roms`. The `c_*` names are commercial ROMs copied from `X:\EMULATION`
into the scratch ROM folder. **Never commit them or their screenshots.**

## Instrumentation

`instrument.js` is evaluated in the test page only. It wraps `nesInterop.presentFrame/drawFrame/playAudio` to
count frames and measure the RMS level of the audio buffers. It also reads `window.nesAudioCtx.state`.
It does not change behaviour.
