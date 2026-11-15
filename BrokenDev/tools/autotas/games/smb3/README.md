# SMB3 intel (AutoTAS)

Data only; the code is in `../../lib/`. Everything here is UNVERIFIED (`"verified": false`) until step 0 of the pilot
checks it in the emulator. When you confirm or fix a value, set its `verified: true` and say how in a note: later agents trust verified entries.

- `dossier.json`: RAM variables (name, play_step expression, format, priority 1 = show in the AutoTAS left stripe).
- `milestones.json`: one condition per chapter (`id`, `until`); `fail` conditions (death) to add to a step's milestones.
- `cleanup.json`: the clean-state rules.
- `macros.json`: named input sequences (`Chapter.macro(game, name)`).
