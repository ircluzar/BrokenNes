# Running BrokenNes jobs on the fleet

7 PCs (OPAL, CORAL, ZIRCON, SAPPHIRE, EMERALD, PEARL, DIAMOND): i3-7100T with 2 cores / 4 threads, 12-16 GB each (ZIRCON has about 8 GB free).
They are driven from this PC through the USB stick at `E:\fleet`. Read `E:\fleet\AGENTS.md` once: those are the owner's rules.
The fleet is claimed for project `brokennes`; do not claim it again or release it.

## Rules that bite
- The only way in is SSH from this PC. The PCs cannot see T:, X: or C: here. You push the inputs and pull the results back.
- Everything goes under `C:\brokennes` on the PCs (already recorded with `hold.py`). If you create anything outside it, record it:
  `cd E:\fleet; python hold.py record dirs <dir>` (or `procs <exe name>`).
- Do not download anything onto the PCs. Only send what is on the stick or was built here.
- Commercial ROMs go only into `C:\brokennes\...` on the PCs, never into git.
- Size each PC to **3 emulator jobs** (4 at most). That leaves CPU at about 77% and memory well under the limit. Each PC runs a
  watchdog that kills python/node/chrome when memory runs low. The fleet does not use this PC's heavy slots. Take a local slot
  (`C:\BrokenNes-work\heavy\slotN`) only for builds or emulator runs on THIS PC.
- Several agents can drive the fleet at the same time: they share the agent name `agent@PROCESS`. Job ids must be unique per PC,
  so prefix them with your lane (for example `nst_<tag>_<i>` or `nes_<tag>_<i>`).
- Python scripts that use the fleet: `sys.path.insert(0, r"E:\fleet")`, then `from jobs import put, job, poll, stop` and
  `from pull import pull, pull_all`.

## What is on every PC (`C:\brokennes\kit`)
| path | what |
|---|---|
| `kit\workshop\BrokenNes.Workshop.exe` | Workshop, self-contained win-x64 (no .NET install), built from BrokenNes main 535f0748 (branch fleet-kit) |
| `kit\py\python.exe` | portable Python 3.11 with numpy and Pillow. Run it with `-I`. |
| `kit\pwsh\pwsh.exe` | PowerShell 7.6 (a copy of this PC's), needed by the Nested m30build.ps1 |
| `kit\mesen\Mesen.exe` | Mesen 2.1.1 (the T1 reference) |
| `kit\tools\compat-t1`, `kit\tools\catalog` | the catalog T1 checker and the catalog library |
| `C:\brokennes\nested\fork` | Nested fork `tools\` + `Project Nested\bin\Release` (injector), from nested-vrun-core |

To refresh the kit after a rebuild: `python C:\BrokenNes-work\fleet\t1fleet.py kit` (Workshop/py/mesen/t1)
or `python C:\BrokenNes-work\fleet\nestedfleet.py kit` (Nested fork; add `--pwsh` only for a new pwsh).
Do not refresh while other lanes' jobs are running on the same files: check with `poll` first.

## Nested compat sweep (tools/compat/sweep.py) on the fleet
```
cd E:\fleet
python C:\BrokenNes-work\fleet\nestedfleet.py run --tag <unique tag> --core <your core dir> [--roster <roster.json>] [--only a,b]
        [--per-pc 3] [--names OPAL,CORAL] [--sweep-args "--no-autoplay"] [--root <your local sweep root>] [--wait]
python C:\BrokenNes-work\fleet\nestedfleet.py poll --tag <tag>
python C:\BrokenNes-work\fleet\nestedfleet.py collect --tag <tag>     # pull + merge + grade.py
```
- It splits the roster over the PCs (`--per-pc` jobs each, `sweep.py --jobs 1` per job) and pushes each game's ROM and your core dir.
  Each job runs `sweep.py` there (build with its own ExeDir, the feedback rounds, `--snesrun`, `--nested-cert`).
  `collect` pulls every job's root and merges `snes\ meas\ logs\ profile\ feedback\`, `meas.json` and `builds.json` into `--root`
  (default `C:\BrokenNes-work\fleet\nested\<tag>`), then runs this PC's `grade.py`. You get the same `results.json` / `summary.json`
  as a local run, and each meas record gets a `fleetHost` field.
- The fork on the PCs is a copy. If your lane changed `tools/` (inputs.json, game profiles, pipeline) or rebuilt the injector,
  run `nestedfleet.py kit --fork <your fork checkout>` first. That copy is shared, so tell the other lanes, or give your lane its own copy
  (`put(<fork stage>, r"C:\brokennes\nested\fork_<lane>")` and change `RF\fork` in a copy of the script).
- Smoke (2026-11-15): balloon_fight + zelda_ii on OPAL while 3 T1 jobs ran: both built and measured in under 4 min, Playable,
  same lags as the local results.json.
- Not on the fleet yet: the golden test (`brokendev.ps1 nested test`). Run it here.

## Catalog T1 (NES) on the fleet
```
python C:\BrokenNes-work\fleet\t1fleet.py plan --console nes --per-pc 3 [--reverse] --tag <tag>   # pending titles -> shards -> jobs
python C:\BrokenNes-work\fleet\t1fleet.py poll --tag <tag>
python C:\BrokenNes-work\fleet\t1fleet.py pull --tag <tag> --ingest     # results -> catalog (t1.py ingest)
```
`t1.py export/work/ingest` (branch fleet-kit): `ingest` writes the same record form as `t1.py run`. It skips a record that already
has `t1-2`, so overlap with a local sweep is safe. Wired consoles: nes, snes, gb, gbc, sms, gg (Mesen 2.1.1) and md (BizHawk 2.11.1
Genplus-gx, `C:\brokennes\kit\bizhawk`). `t1restart.py <console> <tag>` restarts a tag's jobs from scratch.

## GG -> SMS wide roster (gg2sms_test.py) on the fleet
```
python C:\BrokenNes-work\fleet\gg2smsfleet.py kit [--ws <your Workshop bin dir>] [--tools <nested-everything dir>]
python C:\BrokenNes-work\fleet\gg2smsfleet.py run --tag <tag> [--roster ..] [--done <results already graded>] [--per-pc 3]
python C:\BrokenNes-work\fleet\gg2smsfleet.py poll --tag <tag>
python C:\BrokenNes-work\fleet\gg2smsfleet.py collect --tag <tag> [--out merged.json] [--pictures]
```
- **Application Control** ("WinError 4551 ... An Application Control policy has blocked this file") blocks some new unsigned exes
  and dlls, and which ones depends on the file and the PC. Even a self-contained exe was blocked on PEARL and SAPPHIRE, while the
  kit's Workshop runs everywhere. The reliable way to run your own Workshop build is to host its DLL with the Microsoft-signed
  `dotnet.exe` in the kit. Put the framework-dependent `dotnet build` bin on the PCs and call it through a one-line .cmd:
  `@"C:\brokennes\kit\dotnet\dotnet.exe" "C:\brokennes\<lane>\ws_fd\BrokenNes.Workshop.dll" %*` (example: `C:\brokennes\gg2sms\ws.cmd`).
  OPAL blocks BrokenNes.Workshop.dll even then, so on OPAL use a self-contained exe instead (`C:\brokennes\gg2sms\ws\...`).
  Test one command on EVERY PC before a sweep. Do not touch Defender or Application Control.
- The tools are laid out as `C:\brokennes\gg2sms\BrokenDev\tools\nested-everything` + `...\BrokenDev\projects\nested-gg2sms`,
  because gg2sms.py finds pipeline.json by a relative path.

## Your own job shapes
```python
import sys; sys.path.insert(0, r"E:\fleet")
from jobs import put, job, poll; from pull import pull; from fleet import boxes
b = boxes()["OPAL"]
put(r"C:\my\shard", r"C:\brokennes\work\mylane_1", ["OPAL"])                       # inputs (ROMs + a list) -> PC
job(b, "mylane_1", r"C:\brokennes\kit\py\python.exe -I script.py ...", r"C:\brokennes\work\mylane_1",
    {"PATH": r"C:\brokennes\kit\pwsh;C:\brokennes\kit\py;%PATH%"}, limit=6*3600, low=False)
poll(b, "mylane_1", 5)                                                              # (rc or None, log tail)
pull(b, r"C:\brokennes\work\mylane_1\out", r"C:\BrokenNes-work\mylane\fleet\OPAL")  # results -> here
```
- Make a job restartable: Windows Update can restart a PC at night. Write one result file per game and skip the ones already there.
- The job log is `C:\fleet\jobs\<id>.log` on the PC. Past `limit` seconds the job is ended (rc 124).
- Throughput (2026-11-15, 3 jobs per PC): NES T1 about 45-60 s per game per job, so about 90 games/hour per PC and about 600/hour
  across the fleet. Nested: about 1.5-2 min per game per job (build + measure), so about 100 games/hour per PC.

## Work-stealing queue (preferred for T1) and the other fleet tools (2026-11-16)
- `python C:\BrokenNes-work\fleet\fleetq.py --t1 <consoles> [--batch 6] [--minutes 3] [--deep 10] [--noref]`: one global backlog of small batches. Each PC
  keeps up to 6 jobs running (5 on 8 GB PCs), counting every other running FleetJob. Batches are exported ahead of time, and results are pulled and
  ingested (--force) as each batch ends. State: queue_state*.json (a restart skips finished batches). Log: queue*.out / queue.log.
- New PCs: `python provision_new.py <PC> ...` (kit, BizHawk, dotnet, pwsh, Nested fork + fork_x, MCP folders, gg2sms). Then `python hold.py record ...`.
- Nested: `nestedfleet.py run --fork-dir fork_x --env NESTED_FEEDBACK_ALL=1 --env "NESTED_EXTRA=NmiMode.AtVblank=True;NmiMode.AutoDetect=False"`.
  The fork copy must include `tools\m30pipe\core\pristine\Project_Nested.pristine.smc` (srm2calls needs it).
- Unstick sweep: `unstick\unstickfleet.py run|collect`. Fuzz: `stage_fuzz_run.cmd <seed>` on the PCs, then `fuzzcollect.py`. Run the fuzz at NORMAL priority:
  at BelowNormal on a full PC, bfuzz's wall-clock hang watchdog reports false hangs.
- MD reference: BizHawk needs `PreferredPlatformsForExtensions` (.bin -> GEN) in its config, or a "Choose a Platform" dialog blocks the run.
