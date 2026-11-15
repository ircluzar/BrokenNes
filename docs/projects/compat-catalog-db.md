# Great Compatibility Catalog: database

P0 scope items 1 (grade store) and 8 (title lists) of `compat-catalog-plan.md`. Code: `BrokenDev/tools/catalog/`
(`catalog.py` library + CLI, `titles.py` title-list builder, `smb_stress.py` concurrency check). Python 3.9+, standard
library only; `titles.py` also uses 7-Zip (`C:\Program Files\7-Zip\7z.exe`) for the Master System `.7z` set.

## Where it lives

`T:\BrokenDev\Catalog\` (T: is `\\STORAGE\Work`, an SMB share every worker box can map). Override with
`BROKENNES_CATALOG` (a worker that maps the share elsewhere sets it, e.g. `\\STORAGE\Work\BrokenDev\Catalog`). Each
worker sets `BROKENNES_WORKER` to its own id (defaults to the host name).

```
Catalog\
  records\<console>\<sha1[0:2]>\<sha1>.json   one record per ROM (the source of truth)
  log\<worker>.jsonl                          append-only change log, one file per worker
  index\<console>.jsonl, index\catalog.csv,   rebuilt by `catalog.py index` (fast reads, dashboards, BrokenDev view)
  index\summary.json
  cache\scan.json                             titles.py hash cache (path|member|size|mtime -> hashes)
  reports\titles-<date>.json                  title-list build reports (counts, skips)
  screenshots\<console>\<sha1>\...            checkpoint screenshots (suggested layout; path stored in the record)
  tas\<console>\<sha1>\...                    TAS movies (.bk2 / .fm2)
```

Commercial ROM data never goes in git; this tree is only on T:.

## Why one JSON file per record (not SQLite)

SQLite's own documentation says its file locking is unreliable on network filesystems, and 8 machines writing one
`.db` over SMB is the case it warns about (corruption, not just contention). One file per record instead gives:

- **no shared hot file**: two workers only ever contend when they grade the same ROM, which the work queue avoids;
- **atomic replace**: a write goes to a temp file in the same folder, then `os.replace` (one SMB rename), so a reader
  sees the old record or the new one, never half a file;
- **per-record lock** for read-modify-write: `<sha1>.json.lock` created with `O_CREAT|O_EXCL` (atomic on SMB),
  stale after 120 s (a crashed worker cannot block a record for long). `rev` increases by one per write and
  `--expect-rev` gives optimistic checks on top;
- **append-only per-worker logs**, so the history of who changed what never needs a lock;
- an **index rebuilt by one box** (this one) for queries; queries can also scan records live (`--live`).

Measured: 8 parallel writers x 10 appends each to one record kept all 80 notes and ended at the exact `rev`
(local disk, and the same test over T: with `smb_stress.py`).

## Record schema (`brokennes-catalog/v1`)

| Field | Type | Meaning |
|---|---|---|
| `schema` | string | `brokennes-catalog/v1` |
| `console` | `nes snes gb gbc sms gg md wms` | the movie console keys (`Workshop/Movies/MovieMachine.cs`) |
| `sha1` | hex40 | **BrokenNes's own game id hash**: NES = SHA-1 of PRG+CHR (`NES.ComputeGameIdentity`, no header/trainer); every other console = SHA-1 of the whole file as loaded (`ConsoleSessions.Sha1`) |
| `title` | string | display title (GoodTools/No-Intro name without tags) |
| `region` | string | region tag of the kept dump (`U`, `JU`, `USA, Europe`, ...) |
| `dump` | object | `fileSha1`, `crc32`, `md5`, `size`, `goodName`, `verified` (`[!]`), `revision`, `alternatives` (other dumps of the title), NES: `mapper`, `prgKiB`, `chrKiB`, `headerSignature`; SNES: `copierHeader`; GB: `cgbFlag`, `sgb`; GG: `smsModeOnGameGear` |
| `file` | object | `path` (on X:), `member` (inside a zip/7z, else empty), `name`, `size` |
| `grade` | `Not tested Nothing Broken Jank OK Certified` | BrokenNes's scale; Certified = matches the reference frame-for-frame on the accuracy core + a golden case |
| `tier` | 0-3 | 0 not played, T1 boot/title/first minutes, T2 first level TAS, T3 full TAS |
| `notes` | string | agent notes; `--append-notes` adds a `[time worker]` line under the lock |
| `tags` | list | problem tags: `picture sound timing crash input mapper save` |
| `screenshots` | string | folder of checkpoint screenshots |
| `tas` | string | TAS movie path |
| `reference` | object | reference comparison, e.g. `{"emulator":"Mesen 2.1.1","frames":3600,"diff":0.0,"verdict":"match"}` |
| `core` | string | core set tested, e.g. `CPU_FIX/PPU_FIX/APU_FIX` |
| `brokennesVersion` | string | BrokenNes build tested (commit) |
| `gameDbId` | string | id in `Windows/Data/default-db.json` when it differs from `<system>_<sha1>` (NES: `nes_<sha1>`) |
| `revisionOf` | sha1 | set on a kept extra revision: the main dump's sha1 |
| `extra` | object | free-form per-lane data |
| `created`, `updated` | UTC ISO | timestamps |
| `worker` | string | last writer |
| `rev` | int | write counter |
| `history` | list | one entry per grade/tier change: `at, worker, grade, tier, core` |

## CLI

```
python BrokenDev\tools\catalog\catalog.py query --console nes --grade "Not tested"
python BrokenDev\tools\catalog\catalog.py update --console nes --sha1 <sha1> --grade Jank --tier 1 \
    --add-tags picture --append-notes "status bar shakes on level 2" --core CPU_FIX/PPU_FIX/APU_FIX --version <commit>
python BrokenDev\tools\catalog\catalog.py get --console nes --sha1 <sha1>
python BrokenDev\tools\catalog\catalog.py index          # this box, e.g. every few minutes during a swarm
python BrokenDev\tools\catalog\catalog.py export-db --db Windows\Data\default-db.json            # dry run + diff
python BrokenDev\tools\catalog\catalog.py export-db --db Windows\Data\default-db.json --write    # apply
python BrokenDev\tools\catalog\titles.py [--console nes ...] [--dry-run]
```

Library use: `import catalog; catalog.update("nes", sha1, {"grade": "OK", "tier": 1}, append_notes="...")`.

## Export to `default-db.json`

- Only graded records (not `Not tested`) are exported; `OK` and `Certified` become `Works` (the app's scale),
  Certified gets a `Certified <date>:` note like the 2026-09 campaign entries.
- **Nothing is ever dropped or reordered.** New games are appended with a `catalogUpdated` stamp.
- **Hand-graded entries** (no `catalogUpdated`) are never changed; the dry run lists each disagreement as `keep`
  so a person can decide (`--override-hand` replaces them).
- Entries an earlier export wrote change only when the record is newer than their `catalogUpdated`.

## Title lists (`titles.py`)

Sources (read only): `X:\EMULATION\Nintendo Entertainment System`, `Super Nintendo\Super Nintendo (U)` +
`Super Nintendo\Japan`, `GameBoy` (zips) + `GameBoy Color` (console decided by the CGB header flag), `Sega Master
System` (.7z), `Sega Game Gear`, `Sega Genesis`. Not scanned (later lanes): the SNES `Hacks`, `SMW Hacks`,
`Homebrew & Unlicensed`, `PAL to NTSC`, `Translations`, `NES PORTS`, `SPC700 Music`, `Satellaview`, `Unreleased`
folders. WideMess has no library (the user's own builds).

Rule: skip `[b] [h] [o] [p] [f] [x] [t] [T+/-]`, `(Hack) (PD)`, betas/protos/samples/demos, 32X images; in the
No-Intro style folders (GBC, SMS) a name with no region tag is a hack or colourisation and is skipped. Per title:
`[!]` first, then USA/World, Europe, Japan, others; then no `[a]`/`[c]` variant; then the highest revision. A second
dump is kept (`revisionOf`) only when `default-db.json` already grades it. Re-runs only add records, never touch an
existing one. Titles are grouped by name, so a game sold under different names per region stays two titles.
