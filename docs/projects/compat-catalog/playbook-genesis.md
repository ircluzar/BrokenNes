# Playbook: Sega Genesis / Mega Drive

Accuracy core: "Genesis (stock)" (VDP, YM2612 ymfm + PSG). Reference: BizHawk Genplus-gx; it takes 20-90 s to start: wait up to 120 s
before counting the open as failed. Offset ~2. Scores are "close" more often than on the Mesen consoles: a median of ~0.03 (Sonic 1) is a
match. Not dry-run yet.

## Controls
6-button pad by default: `Up Down Left Right A B C Start X Y Z Mode`. Press X/Y/Z/Mode only in games that use 6 buttons (Street Fighter II',
Mortal Kombat 3); some 3-button-era games misread a 6-button pad: if controls act odd, note it, tag `input`. Conventions: B or C = jump,
A = special, Start = pause. Mouse, Menacer, Activator: give-up rule 4.

## Starting a game
- The SEGA logo is 60-200 frames (no TMSS screen). Title, then Start (5 frames); often an OPTIONS / START menu where Start picks START.
- EA games: the EA logo is ~300 frames; Start skips it.
- Sports games: accept defaults with Start / C, 30 frames apart.

## Progress RAM ($FF0000-$FFFFFF; community addresses, check against the picture)
| Game | Watches |
|---|---|
| Sonic the Hedgehog / Sonic 2 | `lives=[$FFFE12]`, `zone=[$FFFE10]`, `act=[$FFFE11]`, `rings={$FFFE20}` |
| Unknown | 5-call search on the lives digit, else picture only |

## Steps and findings
- Sonic-speed games: 10-20 frame steps; long holds only on open ground.
- Interlace mode 2 (Sonic 2 two-player, Combat Cars) is a known open gap: grade Jank, tag `picture`, do not spend steps on it.
- Shadow/highlight, window plane, mid-frame CRAM (Sonic's water): a wrong band the reference lacks = Jank `picture`.
- No debugger on the Genesis yet (breakpoints, trace): play_step, read_memory and record work.
