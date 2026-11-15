# Playbook: Game Gear

Core and reference as the Master System ([playbook-sms.md](playbook-sms.md)). The screen is the 160x144 window at x 48, y 48 of Mesen's
256x240 (refstep crops it). P0 scores were "close" (0.04-0.11) on Sonic Blast and Sonic & Tails: on the GG, a steady score up to 0.12 is a
match; only a rise above it is a finding. Not dry-run yet.

## Controls
`Up Down Left Right`, 1, 2, and `Start` (the GG's own Start, used where the SMS used Pause). Gear-to-Gear link: give-up rule 4.

## Starting a game
- Start (5 frames) on the title. SMS-mode games (SMS ports) behave like the SMS: if Start does nothing, try 1.
- Many GG titles are SMS games with a cropped view: a HUD partly off screen is the game.

## Progress RAM ($C000-$DFFF)
Picture only, or a 5-call search on the lives digit.

## Findings
- 12-bit colour: a shade difference everywhere is the reference palette, not a bug.
- A band of wrong tiles at the window edge on our side only = Jank `picture`.
