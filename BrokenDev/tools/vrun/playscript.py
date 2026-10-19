"""Deterministic long-play input scripts for the VRUN family (BrokenNes --input / Mesen tracer format).

    python playscript.py <profile> <frames> [--seed N] [--start F] [--save-every F]

Output: "frame:Buttons,frame:,..." on stdout (a step holds until the next one; an empty set releases).

Profiles (button names are the emulator's own pad names):
  nes     VRUN CTW on the NES: A jump, B fire, D-pad move/aim, Start pause/confirm, Select (paused) = save.
  nested  the same game on the SNES through Project Nested: NES A = SNES B, NES B = SNES Y.
  svrun   Super VRUN (classic scheme): jump A/B, fire Y/X, D-pad move/aim, Start confirm/pause.

The play is a seeded random walk over plausible actions (run right/left, jump, fire while running, aim up and
fire, stand and fire), biased to the right (rooms are left to right), with a pause -> (save) -> unpause every
--save-every frames. It is coverage, not skill: deaths happen, and VRUN's permadeath sends the player back to the
title, where the next Start starts a new run.
"""
import random
import sys

PROFILES = {
    "nes": {"jump": "A", "fire": "B", "save": "Select", "first_start": 150},
    "nested": {"jump": "B", "fire": "Y", "save": "Select", "first_start": 240},
    "svrun": {"jump": "B", "fire": "Y", "save": "Select", "first_start": 300},
}


def main():
    a = sys.argv[1:]
    prof = PROFILES[a[0]]
    total = int(a[1])
    seed = int(a[a.index("--seed") + 1]) if "--seed" in a else 66066
    start = int(a[a.index("--start") + 1]) if "--start" in a else prof["first_start"]
    save_every = int(a[a.index("--save-every") + 1]) if "--save-every" in a else 2400
    rng = random.Random(seed)
    J, F = prof["jump"], prof["fire"]
    steps = []

    def add(f, b):
        if f >= total:
            return
        if steps and steps[-1][0] >= f:
            raise SystemExit("steps out of order at %d" % f)
        steps.append((f, b))

    f = start
    if "--play-only" not in a:
        add(f, "Start"); add(f + 6, "")
        if a[0] == "svrun":
            # Super VRUN: a story sequence follows NEW GAME; Start skips it.
            add(f + 220, "Start"); add(f + 226, "")
            f += 226
        f += 270  # the room fades in
    next_save = f + save_every
    actions = [
        (5, lambda: "Right"), (3, lambda: "Right+" + J), (4, lambda: "Right+" + F), (2, lambda: "Left"),
        (1, lambda: "Left+" + J), (2, lambda: "Left+" + F), (1, lambda: "Up+" + F), (1, lambda: F),
        (1, lambda: J), (1, lambda: ""), (1, lambda: "Down+" + F),
    ]
    weights = [w for w, _ in actions]
    while f < total - 60:
        if f >= next_save:
            # pause, optionally save (VRUN: Select while paused), unpause
            add(f, "Start"); add(f + 6, "")
            if prof["save"]:
                add(f + 40, prof["save"]); add(f + 46, "")
            add(f + 100, "Start"); add(f + 106, "")
            f += 140
            next_save = f + save_every
            continue
        act = rng.choices(actions, weights)[0][1]()
        dur = rng.randint(20, 120)
        if act.endswith("+" + J) or act == J:
            # a jump is a press, not a hold: tap the button, keep the direction
            base = act[: -len("+" + J)] if act != J else ""
            add(f, act); add(f + 10, base)
        else:
            add(f, act)
        f += dur
        if rng.random() < 0.3:
            add(f, ""); f += rng.randint(4, 20)
    print(",".join("%d:%s" % s for s in steps))


if __name__ == "__main__":
    main()
