# cc-t1 resume note (Great Compatibility Catalog P3/T1)
- Code: BrokenDev/tools/compat-t1/t1.py (driver) + mesen_t1.lua (reference run).
- Workshop build: C:\BrokenNes-work\cc_t1_bin (dotnet build Workshop -c Release -o there, under a heavy slot).
- Sweep: `python BrokenDev/tools/compat-t1/t1.py run --console nes` (resumes: skips records with extra.t1.version == T1_VERSION).
- Report: `python BrokenDev/tools/compat-t1/t1.py report --console nes`. Local log C:\BrokenNes-work\cc_t1_run\work\t1-nes.jsonl.
