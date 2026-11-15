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

## First build (2026-11-14, `T:\BrokenDev\Catalog\reports\titles-2026-11-15.json`)

| Console | Titles = records (Not tested) | Dumps considered |
|---|---|---|
| NES | 752 | 752 |
| SNES (U + Japan folders) | 865 | 866 |
| Game Boy | 682 | 697 |
| Game Boy Color (CGB flag) | 272 | 282 |
| Master System | 364 | 415 |
| Game Gear | 375 | 438 |
| Genesis | 804 | 809 |
| **Total** | **4,114** | |

29 of the 31 `default-db.json` NES entries match a record id exactly (the other 2 are not in the set; one is the
SMW NES port). Skipped: 219 un-tagged files in `GameBoy Color` (DX/colour hacks), 185 GG / 113 Genesis / 37 GB
flagged dumps, 18 Genesis 32X images. The inventory's SNES "2,122 top level" was wrong: the SNES top level holds only
subfolders. Writing 4,114 records over T: took about an hour (about 1 s per locked write to `\STORAGE`).

## Browse view (P1, `brokendev.ps1 catalog view`)

`BrokenDev/tools/catalog/view.py` + `view.html`: a local, read-only web UI on `http://127.0.0.1:8790/` (standard
library Python; `brokendev.ps1 catalog view [-NoBrowser] [-Stop]`, or `python view.py [--port N] [--root DIR]`).

- one card per console: grade bar, titles, tested %, OK / Certified / Jank / Broken counts, T1-T3 counts, TAS and
  reference-match counts; click a card to list that console;
- the list: title, region, grade, tier, tags, reference verdict, the start of the notes, last update; sortable columns;
  filters for grade, tier, tag, reference (match / differs / none), has TAS, has screenshots; search over title, sha1,
  region, notes and db id;
- the detail pane reads the **record file itself** (never stale): dump, core, build, reference, notes, grade history,
  the screenshots folder as thumbnails, TAS files as download links. Files are served only from inside the catalog root.

Lists come from `index/<console>.jsonl`, so they are as fresh as the last `catalog.py index`. The index rows now also
carry `screenshots`, `tas`, `reference`, `brokennesVersion` and the first 300 characters of `notes`.

## Progress export (`reports/progress.json`)

Written by `catalog.py index` (every rebuild) and by `catalog.py progress` / `brokendev.ps1 catalog progress` (from the
current index). Atomic replace, so a dashboard poller never reads half a file. The same object is served live by the
view at `/api/summary`.

```
{
 "schema": "brokennes-catalog-progress/v1",
 "built": "2026-11-15T13:32:32Z",          UTC, when computed
 "by": "<worker>",
 "consoles": {
  "nes": {
   "name": "NES", "titles": 752,
   "grades":  {"Not tested": n, "Nothing": n, "Broken": n, "Jank": n, "OK": n, "Certified": n},
   "tiers":   {"0": n, "1": n, "2": n, "3": n},
   "tags":    {"picture": n, "sound": n, "timing": n, "crash": n, "input": n, "mapper": n, "save": n},
   "reference": {"match": n, "differs": n, "none": n},   from reference.verdict ("match" / anything else / empty)
   "withTas": n, "withScreenshots": n,
   "tested": n,         titles - Not tested
   "playable": n,       OK + Certified
   "certified": n,
   "testedPct": 0.0, "playablePct": 0.0,                 one decimal, of titles
   "lastGraded": "ISO" newest `updated` among graded records, "" when none
  }, ...                only consoles that have an index
 },
 "total": {"titles": n, "tested": n, "playable": n, "certified": n, "testedPct": 0.0, "playablePct": 0.0}
}
```
