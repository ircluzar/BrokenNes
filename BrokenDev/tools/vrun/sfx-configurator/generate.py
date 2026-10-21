#!/usr/bin/env python3
"""
Regenerates the VRUN SFX Configurator from the CURRENT game.

The configurator was a one-off web page (2026-08-24): every SFX clip rendered by tools/render_sfx_wav.py, a catalogue of the game's
sound events with the clip each one plays, a dropdown per event to try another clip, and Copy / Download JSON of the choices, which
were then applied to src/game/main.fab by hand (37 rebindings that August). This script rebuilds that same page (template.html is the
August page with its data taken out) from what the game is today:

  clips   tools/render_sfx_wav.py is run into <out>/wav; its embedded copies of the clip bytes and of the PERIOD tables are checked
          against engine/audio/audio_engine.fab and src/game/music_data.fab, and a difference is shown on the page (the WAVs would
          not be what the game plays).
  events  one row per trigger call site in src/game/main.fab (trigger_fire_sfx, trigger_hit_sfx, trigger_sfx_sq2_*,
          trigger_sfx_noise_*): its enclosing function, line and clip. events.json ties sites to the August event names; a site
          no event names is named from its function. August events with no call site today are listed under "Not in today's game".

Applying the exported JSON stays a manual step (or Claude's), exactly as before: this page never writes to the game.

    python generate.py --vrun <VRUN repository> --out <folder>      writes <folder>/index.html, events.json and wav/

Exit code 0 = page written (warnings, if any, are on the page and printed), 2 = the inputs could not be read.
"""
import argparse
import base64
import datetime
import html
import importlib.util
import json
import re
import subprocess
import sys
import wave
from pathlib import Path

HERE = Path(__file__).resolve().parent
RX_CALL = re.compile(r'\b(trigger_fire_sfx|trigger_hit_sfx|trigger_sfx_(?:sq2|noise)_\w+)\s*\(')
RX_FN = re.compile(r'^(fn|mode|nmi|irq)\s+(\w+)')
RX_ARRAY = re.compile(r'\[\]\s+sfx_(\w+)\s*\n\s*U\[\d+\]\(([^)]*)\)')


def clip_of(call):
    if call == 'trigger_fire_sfx':
        return 'sq2_fire'
    if call == 'trigger_hit_sfx':
        return 'noise_hit'
    return call[len('trigger_sfx_'):]


def code_text(line):
    return line.split('//')[0].rstrip()


def scan_sites(main_fab, markers):
    """Every trigger call site: line, function, clip, the nearest section marker above it (same function), the 8 code lines before."""
    lines = main_fab.read_text(encoding='utf-8').split('\n')
    rx_markers = [re.compile(m) for m in markers]
    sites = []
    fn = None
    fn_start = 0
    for i, raw in enumerate(lines):
        m = RX_FN.match(raw)
        if m:
            fn, fn_start = m.group(2), i
            continue
        code = code_text(raw)
        for c in RX_CALL.finditer(code):
            prev, section = [], ''
            for j in range(i - 1, fn_start, -1):
                t = code_text(lines[j]).strip()
                if not t:
                    continue
                if len(prev) < 8:
                    prev.append(t)
                if not section and any(r.search(t) for r in rx_markers):
                    section = t
                if section and len(prev) >= 8:
                    break
            sites.append({'line': i + 1, 'fn': fn or '', 'call': c.group(1), 'clip': clip_of(c.group(1)),
                          'section': section, 'prev': prev, 'code': code.strip()})
    return sites


def rule_matches(rule, site):
    if isinstance(rule, list):
        return any(rule_matches(r, site) for r in rule)
    if not re.fullmatch('(?:' + rule.get('fn', '') + ')', site['fn']):
        return False
    if rule.get('clip') and rule['clip'] != site['clip']:
        return False
    if 'section' in rule and not re.search(rule['section'], site['section']):
        return False
    if rule.get('prev') and not any(re.search(rule['prev'], p) for p in site['prev']):
        return False
    if rule.get('last') and not (site['prev'] and re.search(rule['last'], site['prev'][0])):
        return False
    return True


def humanize(fn):
    return fn.replace('_', ' ').strip().capitalize()


def load_render_module(path):
    spec = importlib.util.spec_from_file_location('render_sfx_wav', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)   # the script only renders under __main__
    return mod


def fab_array(text, name):
    m = re.search(r'ct\s+U\[\d+\]\s+' + name + r'\s*=\s*U\[\d+\]\(([^)]*)\)', text)
    return [int(v) for v in m.group(1).split(',')] if m else None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--vrun', required=True, help='the VRUN repository (sys0/simulations/VRUN)')
    ap.add_argument('--out', required=True, help='folder for index.html, events.json and wav/')
    args = ap.parse_args()
    vrun = Path(args.vrun).resolve()
    out = Path(args.out).resolve()
    main_fab = vrun / 'src' / 'game' / 'main.fab'
    engine_fab = vrun / 'engine' / 'audio' / 'audio_engine.fab'
    music_fab = vrun / 'src' / 'game' / 'music_data.fab'
    renderer = vrun / 'tools' / 'render_sfx_wav.py'
    for p in (main_fab, engine_fab, renderer, HERE / 'template.html', HERE / 'events.json'):
        if not p.is_file():
            print(f'generate: missing {p}', file=sys.stderr)
            return 2
    cat = json.loads((HERE / 'events.json').read_text(encoding='utf-8'))
    warnings = []

    # ---- clips: render them, and check the renderer's copies against the game -------------------------------------------
    wav_dir = out / 'wav'
    wav_dir.mkdir(parents=True, exist_ok=True)
    for old in wav_dir.glob('*.wav'):
        old.unlink()
    r = subprocess.run([sys.executable, str(renderer), '--out-dir', str(wav_dir)], cwd=str(vrun), capture_output=True, text=True, timeout=300)
    sys.stdout.write(r.stdout)
    if r.returncode != 0:
        print(f'generate: render_sfx_wav.py failed (exit {r.returncode}):\n{r.stderr}', file=sys.stderr)
        return 2
    mod = load_render_module(renderer)
    engine_arrays = {m.group(1): [int(v) for v in m.group(2).split(',')] for m in RX_ARRAY.finditer(engine_fab.read_text(encoding='utf-8'))}
    clips, audio = {}, {}
    for fam, table in (('sq2', mod.SQ2_CLIPS), ('noise', mod.NOISE_CLIPS)):
        for name, data in table.items():
            cid = f'{fam}_{name}'
            wav = wav_dir / f'{cid}.wav'
            with wave.open(str(wav), 'rb') as w:
                dur = w.getnframes() / float(w.getframerate())
            clips[cid] = {'fam': fam, 'label': name, 'seconds': round(dur, 3)}
            audio[cid] = base64.b64encode(wav.read_bytes()).decode('ascii')
            game = engine_arrays.get(name)
            if game is None:
                warnings.append(f'clip {cid}: render_sfx_wav.py renders it, but engine/audio/audio_engine.fab has no sfx_{name} array')
            elif list(data) != game:
                warnings.append(f'clip {cid}: render_sfx_wav.py\'s copy of the bytes differs from audio_engine.fab (the WAV is not what the game plays): '
                                f'renderer {list(data)} vs game {game}. Update SQ2_CLIPS/NOISE_CLIPS in tools/render_sfx_wav.py')
    for name in engine_arrays:
        if f'sq2_{name}' not in clips and f'noise_{name}' not in clips:
            warnings.append(f'audio_engine.fab has sfx_{name}, which render_sfx_wav.py does not render (add it to SQ2_CLIPS or NOISE_CLIPS)')
    if music_fab.is_file():
        mtext = music_fab.read_text(encoding='utf-8')
        for tname in ('PERIOD_LO', 'PERIOD_HI'):
            game = fab_array(mtext, tname)
            if game is not None and list(getattr(mod, tname)) != game:
                warnings.append(f'{tname}: render_sfx_wav.py\'s copy differs from src/game/music_data.fab (the pitches of the square clips would be off)')
    else:
        warnings.append('src/game/music_data.fab not found (build the game once): the renderer\'s PERIOD tables were not checked')

    # ---- events: today's call sites, named from the catalogue ------------------------------------------------------------
    sites = scan_sites(main_fab, cat['sectionMarkers'])
    events_by_rule = {e['id']: [] for e in cat['events']}
    unowned = []
    for s in sites:
        owner = next((e for e in cat['events'] if rule_matches(e.get('match', {}), s)), None)
        if owner:
            events_by_rule[owner['id']].append(s)
        else:
            unowned.append(s)
        if s['clip'] not in clips:
            warnings.append(f'main.fab:{s["line"]} calls {s["call"]}() but no clip {s["clip"]} was rendered')
    replaced = {r_ for e in cat['events'] if events_by_rule[e['id']] for r_ in e.get('replaces', [])}

    def row(eid, category, name, desc, s):
        return {'id': eid, 'category': category, 'name': name, 'desc': desc,
                'loc': f'main.fab:{s["line"]} in {s["fn"]}()', 'current': s['clip'] if s['clip'] in clips else None,
                'fn': s['fn'], 'line': s['line'], 'code': s['code']}

    events = []
    for e in cat['events']:
        owned = events_by_rule[e['id']]
        for k, s in enumerate(owned):
            name = e['name'] + (f' (call {k + 1} of {len(owned)})' if len(owned) > 1 else '')
            desc = e['desc'] + (f' [since {e["since"]}]' if e.get('since') else '')
            events.append(row(e['id'] if k == 0 else f'{e["id"]}_{k + 1}', e['category'], name, desc, s))
    per_fn = {}
    for s in unowned:
        per_fn.setdefault(s['fn'], []).append(s)
    for fn, group in per_fn.items():
        f = cat.get('functions', {}).get(fn)
        for k, s in enumerate(group):
            base = f['name'] if f else f'{humanize(fn)}'
            name = base + (f' (call {k + 1} of {len(group)})' if len(group) > 1 else '')
            desc = f['desc'] if f else f'{fn}() plays it (a call site the 2026-08 catalogue did not have)'
            events.append(row(f'{fn}_{k + 1}', f['category'] if f else 'Other', name, desc, s))
    missing = []
    for e in cat['events']:
        if events_by_rule[e['id']] or e['id'] in replaced or not e.get('was'):
            continue
        missing.append(e['id'])
        rules = e['match'] if isinstance(e['match'], list) else [e['match']]
        fns = ' / '.join(r.get('fn', '?') + '()' for r in rules)
        events.append({'id': e['id'], 'category': "Not in today's game", 'name': e['name'],
                       'desc': f'{e["desc"]} - no call site in today\'s main.fab ({fns} plays no clip, or is gone)',
                       'loc': f'no call site today (was {e["was"]} on 2026-08-24)', 'current': None})

    # ---- the page -----------------------------------------------------------------------------------------------------------
    commit = ''
    try:
        g = subprocess.run(['git', '-C', str(vrun), 'log', '-1', '--format=%h %cs', '--', 'src/game/main.fab'], capture_output=True, text=True, timeout=30)
        dirty = subprocess.run(['git', '-C', str(vrun), 'status', '--porcelain', '--', 'src/game/main.fab'], capture_output=True, text=True, timeout=30)
        commit = g.stdout.strip() + (' + uncommitted changes' if dirty.stdout.strip() else '')
    except (OSError, subprocess.SubprocessError):
        commit = '(git not available)'
    now = datetime.datetime.now().strftime('%Y-%m-%d %H:%M')
    n_sites = len(sites)
    summary = (f'{n_sites} trigger call sites in main.fab ({sum(len(v) for v in events_by_rule.values())} named from the 2026-08 catalogue, '
               f'{len(unowned)} named from their function), {len(missing)} catalogue event(s) not in today\'s game, {len(clips)} clips')
    banner = ''
    if warnings:
        items = ''.join(f'<li>{html.escape(w)}</li>' for w in warnings)
        banner = ('<div style="border:1px solid var(--warn);background:var(--warn-dim);color:var(--text);padding:10px 14px;border-radius:4px;'
                  'margin:0 0 16px;font-size:12px"><b style="color:var(--warn)">Check before trusting the sounds:</b><ul style="margin:6px 0 0 18px;padding:0">'
                  + items + '</ul></div>')
    about = ('<p><b style="color:var(--text)">This copy.</b> Regenerated by BrokenDev (<code>brokendev.ps1 tool vrun-sfx-configurator</code>) on '
             + html.escape(now) + ' from <code>src/game/main.fab</code> at ' + html.escape(commit or '?') + ': ' + html.escape(summary)
             + '. Each event row is one call site (file, line, function); the dropdown tries another clip. Export the JSON and hand it back '
             'to apply the changes to main.fab, as in August: the page never writes to the game.</p>')
    footer = ('Generated ' + html.escape(now) + ' &middot; PCM16 mono 44.1kHz &middot; clips: <code>tools/render_sfx_wav.py</code> &middot; events: '
              '<code>src/game/main.fab</code> ' + html.escape(commit or ''))
    order = list(cat['categories'])
    for e in events:
        if e['category'] not in order:
            order.insert(len(order) - 1, e['category'])
    page = (HERE / 'template.html').read_text(encoding='utf-8')

    def js(v):
        return json.dumps(v, ensure_ascii=False).replace('</', '<\\/')

    replacements = {
        '/*__CLIPS__*/': js({k: {'fam': v['fam'], 'label': v['label']} for k, v in clips.items()}),
        '/*__EVENTS__*/': js([{k: e[k] for k in ('id', 'category', 'name', 'desc', 'loc', 'current')} for e in events]),
        '/*__AUDIO_DATA__*/': js(audio),
        '/*__CATEGORY_ORDER__*/': js(order),
        '__N_CLIPS__': str(len(clips)),
        '__WARNINGS__': banner,
        '__ABOUT_GENERATED__': about,
        '__FOOTER__': footer,
    }
    for k, v in replacements.items():
        if k not in page:
            print(f'generate: the template lacks {k}', file=sys.stderr)
            return 2
        page = page.replace(k, v)
    out.mkdir(parents=True, exist_ok=True)
    (out / 'index.html').write_text(page, encoding='utf-8', newline='\n')
    (out / 'events.json').write_text(json.dumps({'generatedAt': now, 'mainFab': str(main_fab), 'commit': commit, 'summary': summary,
                                                 'warnings': warnings, 'clips': clips, 'events': events, 'sites': sites}, indent=2),
                                     encoding='utf-8', newline='\n')
    print(f'generate: {summary}')
    for w in warnings:
        print(f'generate: WARNING {w}')
    print(f'generate: wrote {out / "index.html"}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
