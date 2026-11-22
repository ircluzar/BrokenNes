# nano98

A standard minimalist Windows 98 SE "game seat" that boots straight into one game, plus `.n98` game packages.
Format and design: `docs/projects/nano98-format.md`.

**Personal copies only.** The seat base is built from the user's own Windows 98 SE disc, and packages hold the user's own
installed games. A base image (`seat_base.n98d`), its warm-boot state, and every `.n98` (above all one with an embedded base,
`base/C.n98d`) are for the owner's own use: never publish, upload, share or push them.

| tool | what |
|---|---|
| `build_seat.py` | the base in one command: Setup from the disc (or `--installed`), seat, slim, settle boot, pack, warm-boot state |
| `build_base.py` | stage 1: the setup disk + unattended answer file (pyfatfs venv) |
| `seat.py` | launcher install (shell), command text |
| `seat_look.py` | boot screen, black desktop, hidden pointer |
| `slim.py` | the cull + zero-fill + N98D pack |
| `pkg/n98.py` | capture / info / apply (schema 1) / cmd / `v2` (schema 1 -> 2, `--embed-base`) |
| `pkg/sparse.py` | N98D sparse disk container |
| `pkg/fat32.py` | FAT32 read/write, `mkfs` (the standard game drive), `MemDisk` |
| `launcher/` | N98LAUNCH.EXE (Open Watcom, `build.ps1`) |
