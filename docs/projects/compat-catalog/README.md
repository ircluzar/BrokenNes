# Great Compatibility Catalog: agent kit

What the T2/T3 agents (Haiku 5.5) are given and the rules they play by. Plan: [../compat-catalog-plan.md](../compat-catalog-plan.md)
(P3 swarm agents, P4 pilot). Tool reference: [../compat-agent-playbook.md](../compat-agent-playbook.md) (play_step, record, reference).
Grade store: [../compat-catalog-db.md](../compat-catalog-db.md).

| File | What |
|---|---|
| [tiers.md](tiers.md) | T1/T2/T3 budgets, grades and the evidence each needs, give-up rules, pilot measurements |
| [prompt-t2-player.md](prompt-t2-player.md) | T2 player prompt (first level, side by side, movie, report block) |
| [prompt-t3-runner.md](prompt-t3-runner.md) | T3 runner prompt (whole game, resumable across sessions and boxes) |
| [playbook-nes.md](playbook-nes.md) | NES (dry-run verified) |
| [playbook-snes.md](playbook-snes.md) | SNES |
| [playbook-genesis.md](playbook-genesis.md) | Genesis / Mega Drive |
| [playbook-sms.md](playbook-sms.md) | Master System |
| [playbook-gg.md](playbook-gg.md) | Game Gear |
| [playbook-gb.md](playbook-gb.md) | Game Boy / Color |
| [playbook-widemess.md](playbook-widemess.md) | WideMess (no reference: determinism + test ROMs) |
| [pilot-nes.md](pilot-nes.md) | the 55-game NES pilot (mappers, genres, expectations, sha1) |
| [dryrun-2026-11-15.md](dryrun-2026-11-15.md) | the T2 dry run on 3 NES games and what it changed |

The T2/T3 runner (P3, not built) sends: the prompt with the job's fields filled + the console's playbook. RAM addresses marked
"verified" were checked on BrokenNes; the rest are community addresses the agent checks against the picture first. Only the NES playbook
has been exercised; each other console needs its own 3-game dry run before its swarm.
