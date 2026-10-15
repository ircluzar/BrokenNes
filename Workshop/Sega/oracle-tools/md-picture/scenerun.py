"""Run a static-scene fuzz ROM (scenefuzz.py) on BlastEm and compare every scene with this emulator (Workshop --md-scenes).

    python scenerun.py NAME --family interlace2 --scenes 30 --seed 5 [--no-blastem] [--slot-set ...] [--only 3,4]

BlastEm is a window program: hold the oracle lock (Workshop\\Sega\\oracle-lock.ps1 -Acquire blastem -Owner b4a) around this script. The oracle's screenshots land in <root>\\out\\NAME\\s000.png ...;
our pictures and the differences (ours_N.png, theirs_N.png, diff_N.png) in <root>\\out\\NAME_cmp. Environment as oracle.py (BN_ORACLE_ROOT, BN_ARTIFACTS).
"""
import argparse, os, subprocess, sys, json
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import oracle, scenefuzz, random

W = oracle.ROOT


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('name')
    ap.add_argument('--family', default='all')
    ap.add_argument('--scenes', type=int, default=20)
    ap.add_argument('--seed', type=int, default=1)
    ap.add_argument('--start-index', type=int, default=0)
    ap.add_argument('--no-blastem', action='store_true')
    ap.add_argument('--slot-set', default=None)
    ap.add_argument('--only', default=None)
    ap.add_argument('--region', default=None)
    ap.add_argument('--build-only', action='store_true', help='only write the ROM and its scene list (for scenes-check.ps1)')
    ap.add_argument('--record', default=None, help='append the scenes that are pixel-identical with the oracle to this tsv (Workshop/MdGolden/md_scene_oracle.tsv); the set name is NAME')
    a = ap.parse_args()
    os.makedirs(os.path.join(W, 'roms'), exist_ok=True)
    rom = os.path.join(W, 'roms', a.name + '.bin')
    rng = random.Random(a.seed)
    scenes = [scenefuzz.make_scene(rng, a.start_index + i, a.family) for i in range(a.scenes)]
    scenefuzz.build(scenes, rom)
    meta = [{k: v for k, v in s.items() if k not in ('sat', 'hscroll', 'cram', 'vsram')} for s in scenes]
    json.dump(meta, open(os.path.splitext(rom)[0] + '.json', 'w'), indent=1)
    if a.build_only: print('wrote', rom); return
    out = os.path.join(W, 'out', a.name)
    if not a.no_blastem:
        script = 'w500,' + ','.join(['shot', 'k:Start', 'w350'] * a.scenes)
        got = oracle.blastem(rom, a.name, script, startup_ms=2500, cache=False)
        n = len([f for f in os.listdir(got) if f.startswith('s') and f.endswith('.png')])
        print('BlastEm screenshots: %d (wanted %d)' % (n, a.scenes))
    cmp_dir = os.path.join(W, 'out', a.name + '_cmp')
    cmd = [oracle.WORKSHOP, '--md-scenes', rom, '--count', str(a.scenes), '--shots', out, '--meta', os.path.splitext(rom)[0] + '.json', '--out', cmp_dir]
    if a.slot_set: cmd += ['--slot-set', a.slot_set]
    if a.only: cmd += ['--only', a.only]
    if a.record: cmd += ['--hash-out', a.record, '--set', a.name]
    if a.region: cmd += ['--region', a.region]
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    print(p.stdout + p.stderr)


if __name__ == '__main__':
    main()
