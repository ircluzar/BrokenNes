# Compat fleet: worker install, job queue, operator guide

> **2026-11-27 NAS repoint.** The NAS (T:, X:) is dead. Wherever this doc says `T:\BrokenDev\CompatQueue`, `CompatFleet` or `CompatCatalog`, the scripts now default to `C:\BrokenNes-work\compat-fleet\{queue,fleet,catalog-inbox}` (override with env `BROKENNES_COMPAT_HOME` or `-QueueRoot/-FleetRoot/-CatalogInbox`; switching to the new NAS = set that one variable). `-EmulatorHome` follows `BROKENDEV_HOME` (`C:\BrokenDev\env`). `install-worker.ps1` no longer maps drives unless `-MapShares`. **Not working without a share:** a worker on another PC cannot read the fleet/queue folders on this PC; no share is created. Either use the fleet's own sync (x86sync.py style) or wait for the new NAS. A box that cannot see `FleetRoot`/`QueueRoot` now stops with a message. ROM paths `X:\EMULATION\...` (job inputs, test-dryrun.ps1) still need a ROM drive at that letter; not repointed.

P0 scope item 6 of the Great Compatibility Catalog ([compat-catalog-plan.md](compat-catalog-plan.md)): one script installs or
updates a worker box; a job queue on the shares hands the boxes games to check; results flow into the catalog.
Code: `tools/compat-fleet/`.

## How it fits together

```
 this PC (coordinator)                      \\STORAGE (the NAS: already exists)            i3 box x7
 ---------------------                      -------------------------------------          ---------
 stage-release.ps1  --builds-->  T:\BrokenDev\CompatFleet\releases\<ver>\  --install-worker.ps1-->  C:\CompatWorker\versions\<ver>
 queue.ps1 enqueue  --------->  T:\BrokenDev\CompatQueue\jobs\pending\   <--worker.ps1 claims--   runner (1-2 slots)
 queue.ps1 serve (reap+ingest)  T:\BrokenDev\CompatQueue\results\        <--result records-----   tiers\T1.ps1 -> Workshop
          '-> T:\BrokenDev\CompatCatalog\inbox\<console>\  (the catalog lane imports from here)
 ROMs: X:\EMULATION (\\STORAGE\Software, read only)  <------------------------------ read by every box
```

X: and T: are not local drives of this PC: they are the NAS shares `\\STORAGE\Software` and `\\STORAGE\Work`. The queue and the
fleet bundle live on T:, so **no Windows share has to be created on this PC and no inbound firewall rule opened here**. The boxes
talk only to the NAS (SMB, TCP 445 outbound, which Windows allows by default). This PC only has to be on for `queue.ps1 serve`
(reaping and ingesting); the boxes keep working through the queue if it is off.

## Parts

| File | Runs on | What |
|---|---|---|
| `stage-release.ps1` | this PC | `dotnet publish` Workshop + MCP server **self-contained win-x64** (no .NET on the boxes), copies Mesen 2.1.1 + BizHawk 2.11.1 once from `T:\BrokenDev\env\emulators`, writes `manifest.json` (sha256 of every file), `-Pin` sets `current.json`. Version = commit hash (`-dirty` if the tree has changes). ~255 MB per release + 215 MB emulators (once). |
| `install-worker.ps1` | the box (copied to the fleet root) | maps X:/T: if missing, copies the pinned release + emulators, checks every sha256, writes `C:\CompatWorker\worker.json`, registers the box, optional scheduled task. Update = rerun. Keeps the two newest versions (rollback: `-Version <old>`). |
| `worker.ps1` | the box | the agent runner: claims jobs, runs up to `slots` (1-2) at once in child processes, heartbeats, kills a run over its tier timeout, validates and returns results. Survives share hiccups (logs and backs off). `workers\<box>.stop` asks it to finish and exit. |
| `tiers\T1.ps1` | the box | the scripted boot check (no model). T2/T3 runners plug in as `tiers\T2.ps1` / `tiers\T3.ps1` (agent-loop lane); until then a T2 job fails with "no T2 runner installed". |
| `CompatQueue.psm1` | both | the queue itself (claim, lease, heartbeat, complete, fail/retry, reaper, result validation, status). |
| `queue.ps1` | this PC | `init`, `enqueue`, `status`, `reap`, `ingest`, `serve`, `stop-box`, `retry-failed`. |
| `test-dryrun.ps1` | this PC | the whole flow in a scratch folder with two simulated boxes; exit 0 = all checks pass. |

All of it runs on Windows PowerShell 5.1 (what a fresh Windows box has) and on PowerShell 7.

## The queue (`T:\BrokenDev\CompatQueue`)

Plain files, on purpose: nothing to keep running, every state visible in Explorer, and a claim is an atomic rename on the NAS
(two boxes racing for one job: exactly one rename wins).

```
queue.json            settings: leaseTimeoutSec (300), maxAttempts (3), tierTimeoutSec {T1 600, T2 3600, T3 43200}
jobs\pending\  jobs\claimed\  jobs\done\  jobs\failed\      one <id>.json per job, moved between states
lease\<id>.json       who holds a claimed job + last beat (renewed every heartbeat, 30 s)
workers\<box>.json    box registration: slots, version, running jobs, last beat
results\<id>.json     result records waiting for ingest;  ingested\  the ones already delivered
logs\<box>.log        each box's log
```

- **Job** = ROM + console + tier. Id `<priority>-<tier>-<sha1 first 16>`; boxes take jobs in name order, so a lower priority
  number goes first. Enqueuing the same ROM + tier again is a no-op unless `-Force` (the catalog is keyed by ROM hash).
- **Timeout**: the T1 runner kills the emulator at the tier timeout and reports `Broken` (tags `timing`, `crash`); the worker
  kills a runner 60 s past it; the reaper (`queue.ps1 reap` / `serve`) hands back any job whose lease has not been renewed for
  `leaseTimeoutSec` (box died, network gone) or that has been claimed for over twice its tier timeout.
- **Retry**: a failed attempt (no result, invalid result, reaped) goes back to pending; after `maxAttempts` it lands in
  `jobs\failed` with its history. `queue.ps1 retry-failed` resets them.
- **Validation**: a result record is checked (schema, job id, ROM sha1, grade from the BrokenNes scale) before the job counts as
  done; a box whose lease was reaped while it ran has its late result dropped (the job belongs to someone else now).
- **Per-box concurrency**: `-Slots 1` or `2` at install (an i3-7100T has 2 cores / 4 threads; a T1 NES run uses one core at
  ~200-400 fps). Start with 2 for T1; T2/T3 agent plays are probably 1 per box (decided in the pilot).

## Catalog interface (for the catalog lane)

When this was written the catalog lane's schema doc was not ready, so the hand-off is a folder: `queue.ps1 ingest` copies every
validated record to `T:\BrokenDev\CompatCatalog\inbox\<console>\<rom sha1>.<tier>.<job id>.json` (override with `-CatalogInbox`);
the catalog importer owns everything after that (merge into the db, pick the best record per ROM, feed BrokenNes's
`default-db.json`). Delivering a record twice is harmless (same name, same content). If the catalog lane settles on a different
field name, map it in the importer or change `Test-CompatResult` + `tiers\T1.ps1` together.

### Result record (`compat-result/1`)

```json
{
  "schema": "compat-result/1", "jobId": "5-t1-dfb068f72e9eb44e", "tier": "T1", "console": "nes",
  "rom": { "path": "X:\\EMULATION\\...\\1942 (JU) [!].nes", "name": "1942 (JU) [!].nes", "sha1": "dfb068f7..." },
  "grade": "NotTested",                 // Nothing | Broken | Jank | OK | Certified | NotTested
  "tags": [],                           // picture, sound, timing, crash, input, mapper/board, save
  "core": { "description": "NES | CPU FIX | PPU FIX | APU FIX", "version": "8026e1e1fd" },
  "t1": { "verdict": "pass", "why": "...", "frames": 1800, "exit": 0, "timedOut": false, "seconds": 9.1, "fps": 210,
          "finalHash": "0361C6485E1F432D", "finalColors": 6, "audioPeak": -1, "distinctCheckpoints": 5, "respondsToInput": true,
          "input": "600:Start,...", "checkpoints": [ { "frame": 300, "file": "..._f00300.png", "sha1": "..." } ], "stderrTail": "" },
  "notes": "", "tas": null, "reference": null,      // T2/T3 and the reference lane fill these
  "artifacts": "C:\\CompatWorker\\work\\<id>\\a1", "box": "BOX3", "finishedAt": "2026-11-14T22:26:42Z"
}
```

What T1 can decide alone: `Broken` (core threw, runner hung), `Nothing` (unsupported board, picture blank or frozen at every
checkpoint), otherwise `NotTested` with `t1.verdict` `pass` or `warn` (`warn`: no checkpoint after Start differs from the title,
or silent the whole run). T1 never claims OK or Jank: that needs the reference comparison (scope item 5) or an agent (T2).
NES runs `--romtest` on FIX/FIX/FIX; the other consoles run `--console-run` on their catalog default cores.
Checkpoint pictures stay on the box (`artifacts`): commercial-game screenshots are never committed to git.

## Operator guide: setting up a box

Everything below is for **you** to run; the scripts were only tested in dry-run mode on this PC, and no share, drive map, task or
system setting was created by the agent.

### Once, on this PC

```powershell
cd <BrokenNes checkout>
pwsh -File tools\compat-fleet\stage-release.ps1 -Pin          # build + stage + pin (one heavy build, ~5 min)
pwsh -File tools\compat-fleet\queue.ps1 init                   # creates T:\BrokenDev\CompatQueue
pwsh -File tools\compat-fleet\queue.ps1 serve                  # leave running: reaps stale jobs + ingests results every 60 s
```

### NAS accounts (once)

The boxes need the NAS shares `\\STORAGE\Software` (as X:, **read only**) and `\\STORAGE\Work` (as T:, read/write for
`T:\BrokenDev\CompatQueue` and `T:\BrokenDev\CompatCatalog`; read for `T:\BrokenDev\CompatFleet`). Recommended: one NAS user for
the fleet (e.g. `compatfleet`) with read-only rights on Software and write rights only where listed. Create it in the NAS admin
UI. On each box store its password once (you type it, it goes to Windows Credential Manager):

```powershell
cmdkey /add:STORAGE /user:compatfleet /pass      # prompts for the password
```

### On each i3 box (Windows 10/11, 2 cores / 4 threads, 16 GB)

1. Sign in with the account that will run the worker (a normal user is enough).
2. Run, from a PowerShell window:
   ```powershell
   powershell -ExecutionPolicy Bypass -File \\STORAGE\Work\BrokenDev\CompatFleet\install-worker.ps1 -Slots 2 -RegisterTask
   ```
   It maps X: and T: (`net use ... /persistent:yes`) if they are not mapped yet, copies the pinned release, checks every sha256,
   registers the box and adds the scheduled task "BrokenNes Compat Worker" (starts at logon, restarts if it stops).
   Add `-Start` to run the worker right away in that window instead of waiting for the next logon.
3. Power settings so it keeps working: `powercfg /change standby-timeout-ac 0` and `powercfg /change hibernate-timeout-ac 0`
   (your call: it is a system setting). Windows Update active hours, if it should not reboot mid-run.
4. Optional automatic logon (so a reboot resumes work): Sysinternals Autologon, or run the task "whether the user is logged on
   or not" (Task Scheduler > task > Properties; that needs the account password and admin).

No firewall change is needed on the box either: it only makes outbound SMB connections to the NAS. Windows Defender may scan
every ROM the first time it is read; if that is slow, exclude `C:\CompatWorker` (your call, Windows Security > Exclusions).

### Daily use

```powershell
pwsh -File tools\compat-fleet\queue.ps1 enqueue -Console nes -Tier T1 -RomDir "X:\EMULATION\Nintendo Entertainment System" -Limit 50
pwsh -File tools\compat-fleet\queue.ps1 status        # jobs per state; each box, its slots, running jobs, beat age (LOST = no beat)
pwsh -File tools\compat-fleet\queue.ps1 stop-box -Box BOX3     # finish running jobs, then exit (delete workers\BOX3.stop to resume)
pwsh -File tools\compat-fleet\queue.ps1 retry-failed
```

Consoles: `nes snes gb gbc sms gg md` (N64 and x86 are refused). Update every box: stage + pin a new release, then on each box
rerun `install-worker.ps1` (or let the task restart pick it up after you rerun it) - the queue and results are untouched.

### Dry run (this PC, no setup)

```powershell
pwsh -File tools\compat-fleet\test-dryrun.ps1 -Scratch <empty scratch dir>       # add -SkipBuild -Version <staged> to reuse a build
```

Stages a release into the scratch folder, installs a simulated box (`-DryRun`: no drive maps, no task), enqueues six T1 boot
checks (NES x3 incl. an unsupported mapper 69, SNES, GB, Genesis), runs two simulated boxes with one slot each, ingests, and
checks: all jobs done, both boxes worked, idempotent enqueue, mapper 69 graded Nothing, the five others boot and react; then a
tier timeout (Broken + killed) and a killed box (its job reaped back to pending, the box shown LOST).
Last run 2026-11-14: all 12 checks pass, 6 T1 checks in ~45 s on two slots.

## Traps found while building it

- Windows PowerShell 5.1: `Move-Item -Force` does not replace an existing file (heartbeats failed): writes go through
  `[IO.File]::Replace`, whose backup argument must be `[NullString]::Value`, not `$null`.
- A Windows PowerShell child started from PowerShell 7 inherits PS7's `PSModulePath` and loses `Get-FileHash`: the worker cleans
  `PSModulePath` and the scripts hash with .NET directly.
- `Start-Process -PassThru` in 5.1 reports an empty `ExitCode` unless `.Handle` is read right after the start.
- ROM names are full of `[!]`: every path goes through `-LiteralPath` or .NET.
- `--romtest` with explicit cores on an unsupported mapper exits 3 (core not applied) before it says why; T1 probes once on the
  default cores to get "Mapper N is not supported".
- `--console-run` refuses NES ROMs (the NES runs on its own class): T1 uses `--romtest` for NES.
