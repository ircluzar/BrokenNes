"""Loads a game's intel folder games/<game>/ (data only): milestones.json, macros.json, cleanup.json, dossier.json."""
import os, json

GAMES = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "games")


class Game:
    def __init__(self, name, root=GAMES):
        self.name, self.dir = name, os.path.join(root, name)
        self.milestones = self._j("milestones.json", {"milestones": []})["milestones"]
        self.macros = self._j("macros.json", {"macros": {}})["macros"]
        self.dossier = self._j("dossier.json", {"variables": []})
        self.cleanup = self._j("cleanup.json", {"rules": []})["rules"]

    def _j(self, f, d):
        p = os.path.join(self.dir, f)
        return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else d

    def milestone(self, *ids):
        """\"id=expr\" strings for play_step milestones (the chapter's own, plus any fail conditions such as death)."""
        by = {m["id"]: m for m in self.milestones}
        return [f'{i}={by[i]["until"]}' for i in ids]

    def macro(self, name):
        return self.macros[name]["steps"]

    def ram(self, priority=2):
        """name=[$addr] read expressions for the dossier's variables up to that priority (1 = most important)."""
        return [f'{v["name"]}={v["expr"]}' for v in self.dossier.get("variables", []) if v.get("priority", 3) <= priority]


def load(name): return Game(name)
