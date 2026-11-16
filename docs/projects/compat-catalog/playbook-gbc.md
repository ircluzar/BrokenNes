# Playbook: Game Boy Color

Everything in `playbook-gb.md` applies (controls, starting a game, RAM, findings, T2 bridge notes); this page adds the Color specifics.
Catalog console `gbc`: the game runs as a Game Boy Color (Mesen model GameboyColor). Dual-mode games (GB Compatible) are in the gbc list
when the catalog filed them there.

## Starting a game
- Start or A on the title (10 frames). Link's Awakening DX: Start on the title, then the file select, then a name grid (one direction press
  per step; END is bottom right).
- A "for Game Boy Color only" screen on our side = console detection fault: grade Not tested, say so.

## Findings
- Colours: the reference runs without colour correction (T2 server mcp3), so the same picture scores 0; a different palette (a whole tint)
  that the reference lacks is a real finding (Jank `picture`), but look at the compare pictures first.
- HDMA garbage during transitions that the reference lacks = Jank `picture`. Double-speed (CGB 2x CPU) slowdowns the reference lacks = `timing`.

## T2 bridge notes (smoke test 2026-11-16, CORAL)
- Link's Awakening DX (USA, Europe) (Rev 1): open_rom -> cores GB/GB/GB, align offset 13; title -> file -> name screen, score 0.000 for 10 steps.
- See `playbook-gb.md` "T2 bridge notes" for the compare (no border, equalised brightness).
