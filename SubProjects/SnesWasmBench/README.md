# SnesWasmBench

Runs the SFC (SNES) cores in the browser under WASM with the same workload and hashing as
`Workshop --snesbench --preset smw`. That gives real target-platform speed, and a byte-for-byte
check that WASM output matches the desktop goldens (`Workshop/SnesGolden/*.json`, one checkpoint
every 250 frames).

```
dotnet publish SubProjects/SnesWasmBench -c Release -o <dir>                          # interpreter
dotnet publish SubProjects/SnesWasmBench -c Release -p:EnableWasmAot=true -o <dir>    # AOT
```

Copy a ROM into `<dir>/wwwroot/roms/smw.smc` (never into this folder: `wwwroot/roms/` is
gitignored on purpose), then serve `<dir>/wwwroot` with `node serve.mjs <dir>/wwwroot 5021` and
open `http://localhost:5021/?frames=1000&auto=1`. The page prints each 250-frame checkpoint with
that window's fps, then the overall fps.

The Claude preview tool reads `.claude/launch.json` from the main checkout; add a config there
that runs `node SubProjects/SnesWasmBench/serve.mjs <dir>/wwwroot <port>` to drive it from the
browser pane.

The `snes-wasm-aot` launch entry serves `SubProjects/_publish/snes-wasm-aot/wwwroot` (port 5022).
`SubProjects/_publish/` is gitignored, so an AOT publish with a ROM in it can live there safely:

```
dotnet publish SubProjects/SnesWasmBench -c Release -p:EnableWasmAot=true -o SubProjects/_publish/snes-wasm-aot
```
