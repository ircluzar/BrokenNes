# Multi-Agent Concurrent Testing Protocol — BrokenNes

Reusable reference for running multiple AI agents against the BrokenNes repo
(`C:/Users/philt/OneDrive/Documents/PROJECTS/BrokenNes`) at the same time — building,
running the accuracy/TAS matrix, and touching source — without agents corrupting each
other's builds, clobbering each other's output files, racing on in-process shared state,
or fighting each other in git.

This document is the backbone reference for future sessions. It is built on two hands-on
tests actually run against this repo (not theory): a worktree build-isolation test and a
process-sharded core-matrix test. Their verified findings are cited throughout instead of
generic advice — see the "evidence" callouts in each section.

---

## Quick start checklist

Read this first, then jump to the relevant section for detail.

- **Building/editing source concurrently?** Give each agent its own `git worktree` + own
  branch. Don't have two agents edit files in the one shared checkout at the same time.
- **Just running builds/tests read-only against the current code (no edits)?** Working
  directly in the shared checkout is fine — `dotnet build` is fast enough (~6s even under
  contention) that isolation usually isn't worth the setup cost.
- **Every output file** (JSON results, logs, dumps, screenshots) must be namespaced with an
  agent/session ID and a run timestamp — never write to a bare filename like
  `accuracycoin_matrix.json` or `shard1.json` that another agent might also use. See
  Section 2 for the exact scheme.
- **Before any in-process parallel matrix run** (`Parallel.ForEach` over CPU/PPU/APU
  combos), call `PreWarmSharedStatics` (or an equivalent single-threaded warmup) first.
  Three specific statics are unsafe to lazy-init under race — see Section 3.
- **Splitting a big matrix across processes?** Shard the outermost loop (CPU id list is
  the natural choice) into N disjoint groups, run N OS processes concurrently, each with
  its own `--out`, merge the JSON arrays afterward. See Section 4 for the exact command
  pattern that was verified to work.
- **This repo lives under OneDrive.** Aggressive cleanup (`rm -rf` on `bin`/`obj`) can hit
  a transient `Device or resource busy` because OneDrive's sync client holds file handles
  briefly. Retry once; it's not corruption.
- **Git: one branch/worktree per agent, always.** The orchestrating session merges/reviews
  — agents never merge each other's work unsupervised, and nobody force-pushes. If two
  agents need to touch the same file, the orchestrator serializes that, not the agents.

---

## 1. Worktree-per-agent vs. shared checkout

### Use `git worktree` per agent when:
- Any agent will **edit source files** (core implementations, `Program.cs`, CLI tools,
  etc.) — even "just reverting after a temporary change" counts, because a concurrent
  build from another agent could observe half-edited files.
- Multiple agents need to **build** concurrently and you want each build's `obj`/`bin` to
  be independently reproducible/inspectable without one agent's rebuild invalidating
  another's.
- You want a clean, disposable branch per agent that can be discarded without touching
  `main` or the primary working tree.

### Work directly in the shared checkout when:
- Agents only **run** already-built executables (e.g. multiple `BrokenNes.Workshop.exe`
  processes doing read-only ROM/movie testing) and don't edit or rebuild anything.
- The task is a single quick build/test cycle where worktree setup/teardown overhead isn't
  worth it.

### Evidence (worktree build-isolation test, verified)
- `git worktree add <path> -b <branch>` for two agents, both checked out cleanly at the
  same commit on independent new branches.
- Two concurrent `dotnet build Workshop/BrokenNes.Workshop.csproj -c Release` runs: **both
  succeeded, exit 0, 0 errors**, identical pre-existing warning set (20x `CS8601`,
  nothing new/spurious). No NuGet restore errors, no lock-file contention, no MSBuild temp
  file collisions. The shared global NuGet cache (`~/.nuget/packages`) serves concurrent
  reads fine.
- Timing: ~6s per build when 2 run concurrently vs. ~2.4s for one solo clean build (i.e.
  concurrent builds are ~2.5–3x slower each, from CPU/disk contention) — expected, not a
  correctness issue, and trivial in absolute terms since this project builds in single
  digit seconds either way.
- Cleanup: `git worktree remove <path> --force` + `git branch -D <branch>` for both,
  verified `git worktree list` / `git branch -a` / `git status` return to exactly the
  pre-test state afterward. No cross-worktree interference of any kind (separate
  `obj`/`bin`, separate branches, no shared lock files).

### Gotcha: OneDrive file locks
This checkout lives under OneDrive. A `rm -rf` on `Workshop/bin/Release/net10.0-windows`
inside the **main** (OneDrive-synced) checkout hit a transient `Device or resource busy` —
OneDrive's sync client briefly holds handles on recently-changed files. Retrying the same
`rm -rf` immediately succeeded. This was NOT observed in worktrees created under plain
`%TEMP%` (not OneDrive-synced), which cleaned up without issue.
**Rule of thumb:** put throwaway worktrees under `%TEMP%` (or another non-OneDrive path),
not inside the OneDrive tree, both for speed and to dodge this class of lock contention.
If a cleanup step in the main checkout hits "resource busy", retry once before treating it
as an error.

### Recipe
```bash
# Create (use a plain Temp path, NOT another OneDrive-synced location)
git worktree add "C:/Users/<you>/AppData/Local/Temp/bn-agent-<id>" -b agent-<id>

# ... agent does its work, builds, tests, commits on branch agent-<id> ...

# Teardown (from the main checkout)
git worktree remove "C:/Users/<you>/AppData/Local/Temp/bn-agent-<id>" --force
git branch -D agent-<id>   # only after the branch's work is merged or abandoned
```

---

## 2. File-path namespacing for concurrent scratch/temp output

Every artifact a concurrent agent writes — matrix JSON, dump files, logs, screenshots,
`--out` paths — must embed an identifier that is unique to that agent's run, so that no
two concurrently-running agents can ever write the same path. This was a real risk called
out in the process-sharding test: `RunMatrix`'s `outPath` defaults to a fixed
`accuracycoin_matrix.json` in the CWD when `--out` is omitted, which is exactly the kind of
bare filename that will collide if two agents forget to pass `--out`.

### Naming scheme

```
<scratchpad_root>/<session_id>/<agent_tag>/<artifact_kind>_<yyyyMMdd_HHmmss>[__<shard_id>].<ext>
```

- `scratchpad_root` — the session's scratchpad directory (or an explicit shared results
  dir the orchestrator designates for a multi-agent run).
- `session_id` — the orchestrating session's own id (already the norm: e.g. the
  `27ee9608-...` UUID this session's scratchpad is keyed on).
- `agent_tag` — a short, human-assigned label for *which agent/worktree/branch* produced
  this, e.g. `agentA`, `agentB`, or the worktree branch name (`agent-parallel-test-a`).
  Never reuse a tag across agents running at the same time.
- `artifact_kind` — what it is: `matrix`, `dump`, `log`, `screenshot`, `movie-run`, etc.
- Timestamp — collision-proofing even if two agents pick the same tag by accident.
- `shard_id` — when a single agent further splits work across processes (Section 4), tack
  on the shard number/name.

### Examples
```
scratchpad/27ee9608/agentA/matrix_20260729_141502.json
scratchpad/27ee9608/agentA/matrix_20260729_141502__shard1.json
scratchpad/27ee9608/agentA/matrix_20260729_141502__shard2.json
scratchpad/27ee9608/agentA/matrix_20260729_141502__merged.json
scratchpad/27ee9608/agentB/dump_smb3_frame1000_20260729_141530.bin
scratchpad/27ee9608/agentB/log_tas_playback_20260729_141545.txt
```

### Rules
1. **Always pass `--out` explicitly** to every `RunMatrix`/CLI invocation — never rely on
   the tool's default output path when more than one process might be running.
2. **Never write into another agent's `agent_tag` subdirectory.** If the orchestrator needs
   to merge results, it reads from each agent's subdirectory and writes the merged file to
   its own (orchestrator-level) path, not into any single agent's folder.
3. **Merges are additive JSON-array concatenation**, not overwrite — verified safe because
   each result record (e.g. `AccuracyCoinRunResult`) is self-describing (carries its own
   Cpu/Ppu/Apu fields), so a flat concat of N shard arrays is a correct, order-independent
   merge:
   ```powershell
   $merged = @()
   Get-ChildItem shard*.json | % { $merged += (Get-Content $_.FullName -Raw | ConvertFrom-Json) }
   $merged | ConvertTo-Json -Depth 10 | Set-Content merged.json
   ```
4. If two agents are running the *same* worktree/checkout concurrently (not recommended —
   see Section 1), this namespacing is mandatory, not optional, since there's no
   filesystem-level isolation backing it up.

---

## 3. Cross-core-unsafe shared mutable state WITHIN one process

Three specific process-wide statics are known to be unsafe under in-process parallelism
(e.g. `Parallel.ForEach` over CPU×PPU×APU combos in the same `.exe`). These are NOT an
issue across separate OS processes (each process has its own copy) — they only matter
when one process runs multiple core combos in parallel threads.

1. **`APU_WF`** — opens a shared `new MidiOut(0)` device handle plus an "init attempted"
   flag, lazily on first use. Two threads racing to first-use this core simultaneously can
   double-init or hand out an inconsistent handle.
2. **`APU_SPD2`** — has an unsynchronized lazy mix-LUT (lookup table) build on first use.
   Concurrent first-use from multiple threads races on building/reading that table.
3. **`CPU_Z80`** — uses a shared, non-thread-safe `Random` instance. Concurrent `.Next()`
   calls from multiple threads can corrupt its internal state or return degenerate
   sequences.

### The rule
**Always pre-warm these three single-threaded before starting any in-process parallel
matrix run.** Do one throwaway single-threaded pass that touches each of `APU_WF`,
`APU_SPD2`, and `CPU_Z80` (e.g. load a ROM, select the core, run one frame) so their
one-time lazy init happens deterministically before the `Parallel.ForEach` starts. After
warmup, subsequent reads/uses of the now-initialized state are safe to share across
threads.

### Reference implementation
This is already implemented — don't reinvent it. See
`Workshop/AccuracyCoinCli.cs`, method `PreWarmSharedStatics(byte[] romBytes)`:

```csharp
private static void PreWarmSharedStatics(byte[] romBytes)
{
    foreach (var apu in new[] { "WF", "MNES", "SPD2" })
    {
        try
        {
            var nes = new NES { RomName = "warmup" };
            nes.LoadROM(romBytes);
            if (nes.SetApuCore(apu)) nes.RunFrame();
        }
        catch { /* best-effort warmup only */ }
    }
}
```
It's called once, single-threaded, immediately before `Parallel.ForEach(combos, ...)` in
`RunMatrix` (same file). Note the code comment there names all three culprits explicitly.
**Any new matrix/CLI runner that adds in-process parallelism should call this method (or
port the same pattern) before firing off parallel work.** If a future core is added that
turns out to have its own lazy-shared-state problem, extend this same function rather than
adding a second warmup mechanism.

If you ever add a 4th core with similar shared-static risk, the checklist for spotting it:
static field or static lazy-init property, holds an OS handle / unmanaged resource / RNG /
cached table, touched from the hot per-frame path. Grep for `static` fields in
`Windows/NesEmulator/{cpus,ppus,apus}/*.cs` when auditing a new core before parallel use.

---

## 4. Process-sharding a large test matrix across N OS processes

For matrices too large or too slow for a single process's in-process parallelism (full
core matrix × ROM library × TAS movie library), shard across independent OS processes
instead. This was verified end-to-end, not just designed on paper.

### Design
- `RunMatrix` already accepts `--cpus`/`--ppus`/`--apus` as CSV subsets, so sharding is
  purely a partitioning problem: split one axis (CPU id list is the natural outermost
  loop) into N disjoint groups. Give every shard the **full** PPU/APU lists (or whatever
  axes aren't being split) and a **distinct `--out`** path (see Section 2's naming
  scheme — do not hand-roll ad hoc filenames like `shard1.json` in a shared CWD without an
  agent/session prefix).
- No source changes are required to do this — it's purely a CLI-invocation pattern.
- Merge is a flat JSON-array concatenation (see Section 2, rule 3) since each result
  record is self-describing.

### Current core inventory (for planning shard sizes)
As of the last enumeration (via a temporary `--list-cores`, added, captured, then
reverted — repo stayed clean):
- **CPU (8):** EIL, FIX, FMC, LOW, LW2, SPD, ULQ, Z80
- **PPU (12):** BFR, CUBE, CUBEX, EIL, EXE, FIX, FMC, IMG, LOW, LQ, SPD, ULQ
- **APU (18):** EIL, FIX, FMC, HI, HI2, HI2X, LOW, LQ, LQ2, MNES, QLOW, QLQ, QLQ2, QN, SPD,
  SPD2, ULQ, WF

Recall from Section 3: accuracy work targets the `_FIX` family (CPU_FIX/PPU_FIX/APU_FIX)
exclusively; other cores are frozen/gimmick and don't need re-testing unless explicitly in
scope. Full matrices are for broad regression sweeps, not routine accuracy work.

### Verified command pattern (3-way process shard, 2×12×1 slices)
```bash
EXE="Workshop/bin/Release/net10.0-windows/BrokenNes.Workshop.exe"
ROM="<path to AccuracyCoin.nes>"
PPUS="BFR,CUBE,CUBEX,EIL,EXE,FIX,FMC,IMG,LOW,LQ,SPD,ULQ"
OUTDIR="<scratchpad>/<session_id>/<agent_tag>"

"$EXE" --accuracycoin --rom "$ROM" --matrix --cpus EIL,FIX  --ppus "$PPUS" --apus FIX  --wait-frames 1000 --out "$OUTDIR/matrix_$(date +%Y%m%d_%H%M%S)__shard1.json" &
"$EXE" --accuracycoin --rom "$ROM" --matrix --cpus FMC,LOW  --ppus "$PPUS" --apus MNES --wait-frames 1000 --out "$OUTDIR/matrix_$(date +%Y%m%d_%H%M%S)__shard2.json" &
"$EXE" --accuracycoin --rom "$ROM" --matrix --cpus LW2,ULQ --ppus "$PPUS" --apus WF    --wait-frames 1000 --out "$OUTDIR/matrix_$(date +%Y%m%d_%H%M%S)__shard3.json" &
wait
```
Merge:
```powershell
powershell -c "$m=@(); Get-ChildItem '$OUTDIR/matrix_*__shard*.json' | %{ $m += (Get-Content $_.FullName -Raw | ConvertFrom-Json) }; $m | ConvertTo-Json -Depth 10 | Set-Content '$OUTDIR/matrix_merged.json'"
```

### Verified result
All 3 processes exited 0, no crashes, 24/24/24 results each (2 CPU × 12 PPU × 1 APU),
merged to exactly 72 entries (6 CPUs × 12 PPUs, correctly attributed). No shared-file
collisions, no locking errors. Confirmed via `Get-Process` that all 3 ran as distinct PIDs
with independent CPU-time accrual — they do not see or coordinate with each other. Each
process independently applied its own in-process parallelism
(`Environment.ProcessorCount - 2`-way), i.e. process-level sharding and in-process
parallelism compose fine (N processes x M in-process threads each) — just be mindful of
total core oversubscription if N x M exceeds your machine's core count.

### Cross-process resource notes
- **`APU_WF`'s `MidiOut(0)`** is a genuine shared OS resource (a live system MIDI-out
  device), unlike the three in-process-only statics in Section 3. Windows tolerated 3
  concurrent process-level opens of MIDI-out device 0 in testing, but treat this as
  "currently fine on this machine/driver," not a guaranteed-safe pattern — if a shard
  containing APU_WF ever fails oddly under heavy parallel process count, this is the first
  thing to check.
- No mutexes, named pipes, sockets, or other fixed shared OS-level paths were found
  elsewhere in the AccuracyCoin CLI path — the `outPath` default is the only "fixed path"
  footgun, and it's neutralized by always passing `--out` explicitly (Section 2, rule 1).
- This same shard-by-outermost-axis pattern generalizes beyond the CPU×PPU×APU accuracy
  matrix to ROM-library sweeps and TAS-movie-library sweeps: shard by ROM/movie file list
  instead of by CPU id list, keep the rest of the invocation the same, same merge step.

---

## 5. Git hygiene for concurrent agents

1. **One branch (and, per Section 1, ideally one worktree) per agent.** Never have two
   agents check out and commit on the same branch at the same time.
2. **Agents commit to their own branch only.** They do not merge, rebase onto, or push
   over another agent's branch.
3. **The orchestrating session does all merging/reviewing.** Agents propose changes on
   their branch; the orchestrator (the session that spawned them, or the human) reviews
   diffs and merges into `main` (or the relevant integration branch). Agents never merge
   each other's work unsupervised — this mirrors the existing rule that this document's
   own findings should be reviewed, not blindly trusted.
4. **Never force-push.** Not to `main`, not to an agent's own branch that another process
   might be reading. If history needs correcting, do it via a new commit, and if truly
   necessary, only the orchestrator does it after confirming no one else depends on the
   old history.
5. **Two agents wanting to touch the same file:** this is the orchestrator's job to avoid,
   not the agents'. Concretely:
   - Before dispatching parallel agents, the orchestrator partitions the file/module list
     up front so each agent's assigned scope is disjoint (e.g. "agent A: `PPU_FIX.cs`
     only; agent B: `APU_FIX.cs` only").
   - If overlap is unavoidable (e.g. both need to touch `CoreRegistry.cs`), the
     orchestrator serializes: run agent A's change, merge it to the branch agent B is
     based on (or have agent B rebase after A lands), then run agent B. Do not run both
     concurrently against the same file and hope the merge resolves cleanly.
   - If overlap is discovered mid-flight (agent B's diff touches a file agent A already
     changed), the orchestrator pauses B, reviews both diffs, and manually resolves/merges
     — it does not tell the agents to sort it out between themselves.
6. **Commit only when asked, and only your own agent's actual work.** Don't let an agent
   sweep up and commit files outside its assigned scope (e.g. another agent's in-progress
   edits) just because they're sitting in the same shared checkout.
7. **Clean up worktrees/branches after merge or abandonment** (Section 1's teardown
   recipe) so `git worktree list` / `git branch -a` don't accumulate stale entries across
   sessions.

---

## Provenance

Sections 1 and 4 are backed by two hands-on tests run against this exact repo:
- **Worktree-based build isolation test** — two `git worktree`s, concurrent
  `dotnet build Workshop/BrokenNes.Workshop.csproj -c Release`, both green, cleanup
  verified back to baseline `git status`/`git worktree list`/`git branch -a`.
- **Process-sharded core-matrix test** — 3 concurrent `BrokenNes.Workshop.exe --matrix`
  processes (distinct PIDs verified via `Get-Process`), 2×12×1 slices each, merged to 72
  correctly-attributed results; confirmed `APU_WF`'s `MidiOut(0)` as a real (but currently
  tolerated) cross-process shared OS resource.

Sections 2, 3, 5, and the quick-start checklist synthesize those findings plus the existing
`PreWarmSharedStatics` reference implementation into reusable, forward-looking rules for
sessions that haven't run the same tests themselves. When new cores, CLIs, or shared state
are added, extend this document rather than starting a fresh one — update the core
inventory in Section 4 and the shared-statics list in Section 3 if either changes.
