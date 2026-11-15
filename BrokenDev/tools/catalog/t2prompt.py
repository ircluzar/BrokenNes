"""Fills the compat catalog's T2 player prompt for one game (docs/projects/compat-catalog/prompt-t2-player-bridge.md + the console playbook).

    python t2prompt.py --console nes --sha1 <sha1> [--rom PATH] [--title T] [--max-steps 60] [--out FILE]

Every {{field}} is filled from the catalog record and the playbook; an unfilled field is an error (the NES pilot's agents got
{{max_steps}}, {{known}} and {{engine_hint}} raw). Prints the prompt (or writes --out).
"""
import argparse, os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
DOCS = os.path.join(REPO, "docs", "projects", "compat-catalog")
REF = {"md": "BizHawk 2.11.1 (Genplus-gx)"}


def norm(t):
    t = re.sub(r"\(.*?\)|\[.*?\]", "", t.lower())
    t = re.sub(r"^(the|a)\s+|,\s*(the|a)$", "", t.strip())
    return re.sub(r"[^a-z0-9]+", " ", t).strip()


def engine_hint(playbook, title):
    """The playbook lines naming this game: its RAM watch row and its menu recipe, quoted for the agent."""
    n = norm(title)
    hits = []
    for line in playbook.splitlines():
        m = re.match(r"\|\s*([^|]+?)\s*\|", line) or re.match(r"[-*]\s+\*\*(.+?)\*\*", line)
        if not m:
            continue
        names = [norm(x) for x in re.split(r"[/;]| and ", re.sub(r"\(.*?\)", "", m.group(1)))]
        if any(x and (x == n or (len(x) > 5 and (x in n or n in x))) for x in names):
            hits.append(f"the playbook has a menu recipe for {m.group(1)} (Per-game menu recipes): follow it" if line.lstrip().startswith(("-", "*"))
                        else "RAM watch row: " + line.strip())
    return " / ".join(hits) if hits else "no playbook entry for this game: picture only (at most 5 search_memory calls if a value would help)"


def known(r):
    if r is None:
        return "no catalog record"
    bits = [f"catalog grade {r.get('grade')} (tier {r.get('tier')})"]
    t1 = (r.get("extra") or {}).get("t1")
    if t1:
        bits.append("T1 ran" + (f" ({t1.get('version')})" if t1.get("version") else ""))
    notes = (r.get("notes") or "").strip().replace("\n", " ")
    if notes:
        bits.append("notes: " + (notes if len(notes) <= 500 else "..." + notes[-500:]))
    return "; ".join(bits)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--console", required=True); ap.add_argument("--sha1", required=True)
    ap.add_argument("--rom"); ap.add_argument("--title"); ap.add_argument("--region")
    ap.add_argument("--max-steps", type=int, default=60); ap.add_argument("--max-frames", type=int, default=4000)
    ap.add_argument("--menu-steps", type=int, default=30); ap.add_argument("--max-calls", type=int, default=110)
    ap.add_argument("--bridge", default=os.path.join(HERE, "bridge", "mcp.mjs"))
    ap.add_argument("--shots-root", default="C:/BrokenNes-work/cc_shots")
    ap.add_argument("--movie-root", default="T:/BrokenDev/Catalog/tas")
    ap.add_argument("--out")
    a = ap.parse_args(argv)
    r = catalog.get(a.console, a.sha1)
    rom = a.rom or (r or {}).get("file", {}).get("path")
    if not rom:
        sys.exit("no ROM path: pass --rom")
    title = a.title or (r or {}).get("title") or a.sha1
    fwd = lambda p: p.replace("\\", "/")
    pb_path = os.path.join(DOCS, f"playbook-{a.console}.md")
    playbook = open(pb_path, encoding="utf-8").read() if os.path.exists(pb_path) else ""
    board = (r or {}).get("dump", {}).get("mapper", "?")
    f = {
        "title": title, "region": a.region or (r or {}).get("region") or "?", "console": a.console, "sha1": a.sha1,
        "board": str(board), "rom_path": rom, "rom_path_fwd": fwd(rom), "known": known(r), "engine_hint": engine_hint(playbook, title),
        "movie_path": f"{a.movie_root}/{a.console}/{a.sha1}.bnm", "max_steps": a.max_steps, "max_frames": a.max_frames,
        "menu_steps": a.menu_steps, "max_calls": a.max_calls, "bridge": fwd(a.bridge), "shots": f"{a.shots_root}/{a.sha1}",
        "catalog_py": fwd(os.path.join(HERE, "catalog.py")), "ref_emulator": REF.get(a.console, "Mesen 2.1.1"),
    }
    text = open(os.path.join(DOCS, "prompt-t2-player-bridge.md"), encoding="utf-8").read()
    text = re.sub(r"\{\{(\w+)\}\}", lambda m: str(f[m.group(1)]) if m.group(1) in f else m.group(0), text)
    left = sorted(set(re.findall(r"\{\{\w+\}\}", text)))
    if left:
        sys.exit(f"unfilled fields: {left}")
    text += "\n" + playbook
    if a.out:
        open(a.out, "w", encoding="utf-8").write(text)
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
