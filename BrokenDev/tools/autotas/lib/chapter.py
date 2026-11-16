"""Game-agnostic AutoTAS chapter helpers (one chapter = one segment movie + one milestone state).

Game knowledge comes from games/<game>/ as data (milestones.json, macros.json, dossier.json);
nothing game-specific lives here.

    from lib.bridge import Bridge
    from lib import chapter, game
    g = game.load("smb3")
    with Bridge(pc="CORAL") as b:
        c = chapter.Chapter(b, pkg_dir, "1-1", rom)            # opens the ROM
        c.begin(from_state=prev_raw_state)                     # load_state path + record start
        c.macro(g, "enter_level")                              # a named input macro from macros.json
        hit = c.play_until(g.milestone("1-1"), buttons=["Right","B"], max_frames=4000)
        c.finish()                                             # mark + state/<ch>.raw.state + seg/<ch>.bnm
"""
import os, json, subprocess

MCP_EXE = os.environ.get("BN_MCP_EXE", r"C:\BrokenNes-work\cc_pilot\mcp\bin\BrokenNes.Mcp\release\BrokenNes.Mcp.exe")


def seg_path(pkg, ch): return os.path.join(pkg, "seg", ch + ".bnm")
def raw_state_path(pkg, ch): return os.path.join(pkg, "state", ch + ".raw.state")
def clean_state_path(pkg, ch): return os.path.join(pkg, "state", ch + ".clean.state")
def thumb_path(pkg, ch): return os.path.join(pkg, "thumb", ch + ".png")


class Chapter:
    def __init__(self, bridge, pkg_dir, ch, rom, ram=None):
        self.b, self.pkg, self.ch, self.ram = bridge, pkg_dir, ch, list(ram or [])
        self.session = bridge.call("open_rom", path=rom)["session"]
        self.frames = 0
        self.last = None

    def begin(self, from_state=None):
        """Starts the segment: from power-on, or from a state file (the previous chapter's raw state)."""
        if from_state:
            self.b.call("load_state", session=self.session, path=from_state)
        self.b.call("record", session=self.session, action="start", text=self.ch)

    def step(self, buttons=(), frames=15, until=None, max_frames=None, milestones=None, scale=0):
        r = self.b.call("play_step", session=self.session, buttons=list(buttons), frames=frames, until=until,
                        max_frames=max_frames, milestones=milestones, scale=scale, ram=self.ram or None)
        self.frames += int(r.get("framesRun", 0) or 0)
        self.last = r
        return r

    def macro(self, game, name):
        """Plays a named input macro (a list of {buttons, frames} or {until, max_frames}) from the game's macros.json."""
        for s in game.macro(name):
            self.step(s.get("buttons", []), s.get("frames", 15), s.get("until"), s.get("max_frames"))

    def play_until(self, milestones, buttons=(), max_frames=600):
        """Holds buttons until one of the milestones (\"id=expr\" strings) is true; returns its id or None."""
        r = self.step(buttons, max_frames, milestones=list(milestones))
        return (r.get("milestone") or {}).get("id") if r.get("stoppedBy") == "milestone" else None

    def save_sub_state(self, name):
        """A retry point inside the chapter (state file next to the package, not part of it)."""
        p = os.path.join(self.pkg, "work", f"{self.ch}.{name}.state")
        os.makedirs(os.path.dirname(p), exist_ok=True)
        self.b.call("save_state", session=self.session, path=p)
        return p

    def finish(self, text=None, thumb=True):
        """Marks the milestone, writes state/<ch>.raw.state and seg/<ch>.bnm (+ thumbnail), closes the session."""
        for d in ("seg", "state", "thumb"):
            os.makedirs(os.path.join(self.pkg, d), exist_ok=True)
        mk = self.b.call("record", session=self.session, action="mark", text=text or ("milestone " + self.ch),
                         state_path=raw_state_path(self.pkg, self.ch))
        if thumb:
            try: self.b.screenshot(self.session, thumb_path(self.pkg, self.ch))
            except Exception: pass
        st = self.b.call("record", session=self.session, action="stop", path=seg_path(self.pkg, self.ch))
        self.b.call("close_session", session=self.session)
        return {"mark": mk, "stop": st}

    def abandon(self):
        try: self.b.call("close_session", session=self.session)
        except Exception: pass


def cli(*args):
    """Runs BrokenNes.Mcp.exe --autotas <stitch|verify|info> ... and returns its JSON."""
    r = subprocess.run([MCP_EXE, *args], capture_output=True, text=True, timeout=1800)
    try:
        return json.loads(r.stdout.strip().splitlines()[-1])
    except Exception:
        raise RuntimeError(r.stdout + r.stderr)


def stitch(pkg): return cli("--autotas", "stitch", "--package", pkg)
def verify(pkg, rom): return cli("--autotas", "verify", "--package", pkg, "--rom", rom)
