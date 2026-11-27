# scbot hand-off (StarCraft 1v1 scripted win)

Goal: scripted Terran beats Computer Zerg on Bottleneck, deterministic replay x2, save .rep, render --mp4.

- Start state: st_tvz0.bin (in-game, menu via menu.py). Segments in seg.py (SEGS list), helpers sclib.py.
- Run a segment on Garage2: ./g2seg.sh K TAG  (bin scbot1 = worktree C:\BrokenNes-work\wt-scbot, branch x86-scbot 1fbefa80).
- x86boot tokens: {until:57F0D8>=N,MAX} (minerals), {peek:ADDR}; --dumplin LO-HI=FILE.
- Memory: minerals[p] 0x57F0D8, start locs 0x58D708 (us 3808,2384; enemy 288,2416 -> minimap screen 15,422).
- Traps: cursor starts (320,240) in game; never touch screen edges; multi-SCV selection hides build menu (click portrait 184,412);
  build via buttons (Build 522,453, Depot 569,375, Barracks 522,410); cost taken on arrival (wait 350 fr);
  empty group + 'm' = Move mode -> prefix {esc}; minimap left-click moves camera unless 'a' active; use F2/Shift+F2.
- Lost twice (11:41 defeat; second defeat with 1408 minerals unspent). Next: verified rax spots (cheat-test run only),
  4+ rax, bunker, supply read from memory, waves of 12.
