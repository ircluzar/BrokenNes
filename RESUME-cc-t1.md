# cc-t1 resume note (Great Compatibility Catalog P3/T1)
- Code: BrokenDev/tools/compat-t1/t1.py (driver) + mesen_t1.lua (reference run).
- Workshop build: C:\BrokenNes-work\cc_t1_bin (dotnet build Workshop -c Release -o there, under a heavy slot).
- Sweep: `python BrokenDev/tools/compat-t1/t1.py run --console nes` (resumes: skips records with extra.t1.version == T1_VERSION).
- Report: `python BrokenDev/tools/compat-t1/t1.py report --console nes`. Local log C:\BrokenNes-work\cc_t1_run\work\t1-nes.jsonl.
- 2026-11-15: full NES sweep launched (T1_VERSION t1-2), log C:\BrokenNes-work\cc_t1_run\sweep.log. To resume: rerun the same command.
- Grading: OK = boot/title/first ~8 s after Start match Mesen (palette-blind PicDiff <= 0.03); later blind-play divergence is
  recorded as extra.t1.playDiverges, not a defect. Jank = close/different right after Start or blank in play; Broken = crash/hang/freeze/title differs;
  Nothing = unsupported mapper or blank while Mesen shows a picture; Not tested = no reference pictures or both blank.
