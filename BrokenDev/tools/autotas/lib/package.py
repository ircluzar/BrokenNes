"""AutoTAS package (T:/BrokenDev/Catalog/tas/<console>/<game_id>/v<N>/) read/write, Python side.

Same autotas.json as Windows/NesEmulator/debug/movies/AutoTasPackage.cs (schema 1). Python adds and edits
chapters; frame ranges and keyframe hashes are filled in by `BrokenNes.Mcp.exe --autotas stitch` (chapter.stitch).
"""
import os, json

SCHEMA = 1
KINDS = {"level", "boss", "area", "map"}
STATUSES = {"none", "building", "pilot", "complete"}
CATALOG_TAS = r"T:\BrokenDev\Catalog\tas"


def version_dir(console, game_id, version, root=CATALOG_TAS):
    return os.path.join(root, console, game_id, f"v{version}")


class Package:
    def __init__(self, dir, manifest):
        self.dir, self.m = dir, manifest

    @classmethod
    def create(cls, dir, console, game_id, title, version=1, status="building"):
        m = {"schema": SCHEMA, "console": console, "game_id": game_id, "title": title, "rom_sha1": "", "rom_sha256": "",
             "version": version, "status": status, "full": "full.bnm", "milestones": "milestones.json", "cleanup": "cleanup.json",
             "chapters": []}
        p = cls(dir, m); p.save(); return p

    @classmethod
    def load(cls, dir):
        return cls(dir, json.load(open(os.path.join(dir, "autotas.json"), encoding="utf-8")))

    def save(self):
        self.validate()
        for d in ("seg", "state", "thumb", "notes"):
            os.makedirs(os.path.join(self.dir, d), exist_ok=True)
        f = os.path.join(self.dir, "autotas.json")
        with open(f + ".tmp", "w", encoding="utf-8") as o:
            json.dump(self.m, o, indent=2)
        os.replace(f + ".tmp", f)

    def validate(self):
        assert self.m["schema"] == SCHEMA and self.m["game_id"] and self.m["status"] in STATUSES
        seen = set()
        for c in self.m["chapters"]:
            assert c["id"] and " " not in c["id"] and c["id"] not in seen, "bad/duplicate chapter id " + c["id"]
            assert c["kind"] in KINDS, f'{c["id"]}: kind {c["kind"]}'
            seen.add(c["id"])

    def chapter(self, id):
        return next((c for c in self.m["chapters"] if c["id"] == id), None)

    def put_chapter(self, id, title, kind, milestone=None, quality=None, retries=0, accepted_by=None, notes=None, clean=False):
        """Adds or replaces a chapter (in order of first put). Segment/state paths follow the package layout."""
        c = self.chapter(id) or {}
        new = not c
        c.update({"id": id, "title": title, "kind": kind, "start_frame": c.get("start_frame", 0), "end_frame": c.get("end_frame", 0),
                  "segment": f"seg/{id}.bnm", "raw_state": f"state/{id}.raw.state", "milestone": milestone or id,
                  "retries": retries, "clean_seg": c.get("clean_seg", False), "needs_help": c.get("needs_help", False)})
        if clean: c["clean_state"] = f"state/{id}.clean.state"
        if os.path.exists(os.path.join(self.dir, "thumb", id + ".png")): c["thumb"] = f"thumb/{id}.png"
        if quality: c["quality"] = quality
        if accepted_by: c["accepted_by"] = accepted_by
        if notes is not None:
            os.makedirs(os.path.join(self.dir, "notes"), exist_ok=True)
            open(os.path.join(self.dir, "notes", id + ".md"), "w", encoding="utf-8").write(notes)
        if new:
            # a new chapter goes at the end: its frames start where the last ended (stitch recomputes them)
            end = self.m["chapters"][-1]["end_frame"] if self.m["chapters"] else 0
            c["start_frame"] = c["end_frame"] = end
            self.m["chapters"].append(c)
        return c

    def quality_avg(self):
        pts = {"A": 3, "B": 2, "C": 1}
        q = [pts[c["quality"]["score"]] for c in self.m["chapters"] if c.get("quality", {}).get("score") in pts]
        return round(sum(q) / len(q), 2) if q else None

# the catalog record field: BrokenDev/tools/catalog/catalog.py autotas-set --manifest <pkg>/autotas.json
