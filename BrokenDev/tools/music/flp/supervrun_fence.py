"""
supervrun_fence.py -- the second half of the Super VRUN fence (docs/projects/flp-music-pipeline.md section 8): what the GAME'S OWN converters say about a song made
from a .flp, added to the back end's fence (FruityHost flp-backend: <name>.fence.json, brokennes-fence v1) with the same levels (ok / note / over).

    python supervrun_fence.py <fence.json> <engine> <song.it> <sf2it report.json> --snes <SuperVRUN_dev/snes-dev/supervrun> [--md <SuperVRUN_MD_dev>] [--dos <SuperVRUN_DOS_dev>] [--sfxset GAME]
        -> rewrites <fence.json> with the converters' lines, prints the report; exit 0 (an OVER line is the composer's, not an error), 2 on bad arguments

Read-only on the game trees: it imports their converters (sf2it's report, check_modules.py, sfxaram.py's budget, itconv.py's voice assignment, oplconv.py's player) and runs them on the song.
"""
import argparse
import importlib.util
import json
import os
import sys


def load(path, name):
    sys.path.insert(0, os.path.dirname(path))
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('fence'); ap.add_argument('engine'); ap.add_argument('it'); ap.add_argument('report')
    ap.add_argument('--snes', required=True); ap.add_argument('--md'); ap.add_argument('--dos')
    ap.add_argument('--budget', type=int, default=0, help='sound RAM bytes free for a song (0 = from sfxaram.py when it says, else $FE00 - $1A00 = 58368)')
    a = ap.parse_args()
    fence = json.load(open(a.fence))
    lines = fence['lines']

    def add(rule, level, text):
        lines.append({'rule': rule, 'level': level, 'text': text})

    rep = json.load(open(a.report))
    # ---- sf2it.py (every engine: it makes the module all of them play) ----
    d = rep.get('dropped_notes', 0)
    if d:
        add('voices', 'over', 'sf2it.py dropped %d notes that found no SNESMOD channel (by MIDI channel: %s)' % (d, rep.get('dropped_by_midi_ch')))
    else:
        add('voices', 'ok', 'sf2it.py found a channel for all %d notes (8 SNESMOD channels)' % rep.get('midi_notes', 0))
    if rep.get('truncated_notes'):
        add('notes', 'over', 'sf2it.py shortened %d notes (a channel was needed for a newer one)' % rep['truncated_notes'])
    if rep.get('inaudible_dropped'):
        add('level', 'note', 'sf2it.py left out %d notes too quiet to hear' % rep['inaudible_dropped'])
    if rep.get('folded_unisons'):
        add('voices', 'note', 'sf2it.py folded %d exact unisons into one voice each' % rep['folded_unisons'])
    add('tempo', 'note' if rep.get('drift_ms', 0) > 5 else 'ok', 'tick grid %.3f Hz (IT tempo %d, %d ticks a beat): notes drift up to %.1f ms from FL' % (rep['tick_hz'], rep['it_tempo'], rep['ticks_per_beat'], rep.get('drift_ms', 0)))
    pats = rep.get('patterns', 0)
    add('patterns', 'over' if pats > 64 else 'ok', '%d patterns (SNESMOD and the game\'s pattern table hold 64%s)' % (pats, ': play past order 63 crashes the SPC' if pats > 64 else ''))
    ins = rep.get('instruments', 0)
    add('instruments', 'over' if ins > 64 else 'ok', '%d instruments/samples in the module (64 fit)' % ins)
    est = rep.get('est_module_bytes', 0)
    budget = a.budget or (0xFE00 - 0x1A00)
    if a.engine in ('supervrun-snes', 'supervrun-wms'):
        add('sound-ram', 'over' if est > budget else 'note' if est > budget * 0.8 else 'ok',
            'module about %d bytes (patterns %d + BRR samples %d) of %d for a song (sound RAM $1A00-$FE00, minus the effect set loaded with it: the build\'s sfxaram.py check is the last word)' % (est, rep.get('pattern_bytes', 0), rep.get('sample_brr_bytes', 0), budget))
    # ---- check_modules.py: the SNESMOD rules (every engine plays the same .it, the SNES build refuses a module that breaks them) ----
    cm = load(os.path.join(a.snes, 'tools', 'music', 'check_modules.py'), 'check_modules')
    errs = cm.check(a.it)
    for e in errs:
        add('module', 'over', 'check_modules.py: ' + e)
    if not errs:
        add('module', 'ok', 'check_modules.py: the module keeps the SNESMOD rules (instrument on every note, tempo 81-255, below octave 9, 64 instruments)')

    # ---- Genesis: itconv.py gives tonal IT channels FM 1-5 then PSG 1-3 and SILENTLY drops the rest ----
    if a.engine == 'supervrun-md' and a.md:
        ic = load(os.path.join(a.md, 'tools', 'itconv.py'), 'md_itconv')
        it = ic.ITFile(a.it)
        orders = [o for o in it.orders if o < 254]
        vm = ic.assign_voices(it, orders)
        tonal = {}
        for oi in orders:
            for r in it.patterns[oi]:
                for ci, e in enumerate(r):
                    if e and e[0] is not None and e[0] < 120 and e[1]:
                        s, _ = it.sample_of(e[1], e[0])
                        if s is not None and ic.sample_kind(it.samples[s].name) not in ('nzl', 'nzs', 'dpcm'):
                            tonal[ci] = tonal.get(ci, 0) + 1
        lost = {c: n for c, n in tonal.items() if c not in vm}
        if lost:
            add('voices', 'over', 'itconv.py: %d tonal IT channels have no Genesis voice (FM 1-5 + PSG 1-3): %d notes are not played' % (len(lost), sum(lost.values())))
        else:
            add('voices', 'ok', 'itconv.py: %d tonal IT channels -> %s' % (len(tonal), ', '.join('%s %d' % (k, v + 1) for k, v in sorted(vm.values()))))
        psg = [c for c, (k, v) in vm.items() if k == 'psg']
        if psg:
            add('timbre', 'note', 'itconv.py: IT channel%s %s play%s on the PSG (squares at 50%%, notes below 110 Hz an octave up)' % ('s' if len(psg) > 1 else '', ', '.join(str(c + 1) for c in psg), '' if len(psg) > 1 else 's'))

    # ---- DOS / Windows 95: oplconv.py's 7 tonal voices steal from the quietest; one noise, one percussion ----
    if a.engine in ('supervrun-dos', 'supervrun-95') and a.dos:
        oc = load(os.path.join(a.dos, 'tools', 'oplconv.py'), 'oplconv')
        it = oc.ITFile(a.it)
        p = oc.Player(it)
        steal = ticks = 0
        nz_over = top = 0
        for _ in p.run(0):
            ticks += 1
            want = nz = 0
            for ci in range(64):
                c = p.ch[ci]
                if c.smp is None or not c.on:
                    continue
                k = oc.sample_kind(it.samples[c.smp].name)
                am = oc.amp_of(it, p, c)
                if k in ('nzl', 'nzs'):
                    nz += 1
                elif k != 'dpcm' and am > 0:
                    want += 1
            top = max(top, want)
            if want > oc.TONAL:
                steal += 1
            if nz > 1:
                nz_over += 1
        add('voices', 'over' if steal else 'ok', ('oplconv.py: more than %d tonal notes at once on %d of %d ticks: the quietest are stolen' % (oc.TONAL, steal, ticks)) if steal else 'oplconv.py: at most %d tonal notes at once (%d voices; %d ticks)' % (top, oc.TONAL, ticks))
        if nz_over:
            add('noise', 'over', 'oplconv.py: two noises at once on %d ticks: the loudest sounds' % nz_over)

    json.dump(fence, open(a.fence, 'w'))
    c = {l: sum(1 for x in lines if x['level'] == l) for l in ('over', 'note', 'ok')}
    print('%s: %d over the limit, %d played differently, %d inside' % (fence['engine'], c['over'], c['note'], c['ok']))
    for lv in ('over', 'note', 'ok'):
        for x in lines:
            if x['level'] == lv:
                print('%s[%s] %s' % ({'over': 'OVER ', 'note': 'note ', 'ok': 'ok   '}[lv], x['rule'], x['text']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
