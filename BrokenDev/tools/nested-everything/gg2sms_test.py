"""Grade GG -> SMS conversions on BrokenNes's own cores (headless Workshop --sms-run).

    python gg2sms_test.py <roster.json> --work <dir> [--only substr] [--jobs 2] [--results results.json] [--workshop exe]
    python gg2sms_test.py --rom <in.gg> --work <dir>          (one game)

For each game: gg2sms.py feedback (trace the original on the GG core, convert, trace the result on the SMS core), then the
original on the GG core and the converted ROM on the SMS core with the same pad script (GG Start -> SMS Pause), pictures at the
checkpoints (the SMS picture's centre 160x144 crop, both reduced to the SMS's 2-bit colour levels with the converter's table,
the best of the converted frame -1/0/+1 to allow for the runtime's few cycles) and the sound (per-frame loudness of the mono mix).

Grades (the Nested climb's): Perfect (every checkpoint identical, sound alike), Playable (>= 97 % of pixels, sound alike),
Glitchy (it runs and answers the pad, pictures differ), Boots only (a picture, but it never changes after the boot / the pad
does nothing), Black screen, Build fails. The grade is the harness's first opinion; a person looking at the pictures is the judge.
Results hold names and numbers only: ROMs, converted ROMs and pictures stay in --work (never in git).
"""
import argparse
import concurrent.futures as cf
import json
import os
import subprocess
import sys
import wave

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gg2sms           # noqa: E402

CHECKS = [300, 900, 1500, 2100, 2700, 3300]
SCRIPT = ("120:Start,126:,300:Start,306:,500:A,506:,700:Start,706:,900:A,906:,1000:Right,1300:Right+A,1320:Right,1600:Left+B,"
          "1620:Left,1900:Start,1906:,2000:Start,2006:,2200:Right+A,2400:Up,2500:Down,2600:B,2700:Right,3000:A,3006:")
FRAMES = 3400
HOLD = 12                       # frames each tap is held on the SMS side (hold_longer)


def shift_script(script, lag):
    if not lag or not script:
        return script
    out = []
    for item in script.split(','):
        f, _, keys = item.partition(':')
        out.append(f'{int(f) + lag}:{keys}')
    return ','.join(out)


def hold_longer(script, hold):
    """The converted game drops a frame now and then (the runtime's cost), so a 6-frame tap can land between its pad reads or
    before a menu is ready: on the SMS side every tap is held `hold` frames (one press, one edge, still released before the next)."""
    if not script or hold <= 6:
        return script
    items = [(int(f), k) for f, _, k in (x.partition(':') for x in script.split(','))]
    out = []
    for i, (f, k) in enumerate(items):
        if k == '' and i and items[i - 1][1] and f - items[i - 1][0] <= 6:
            nxt = items[i + 1][0] if i + 1 < len(items) else 1 << 30
            f = min(items[i - 1][0] + hold, nxt - 1)
        out.append(f'{f}:{k}')
    return ','.join(out)


DRIFT = 4                       # checkpoint pictures of the converted game: the best of +-DRIFT frames (its own lag frames add up)
LAG_MAX, LAG_FRAMES = 120, 240


def boot_lag(exe, rom_gg, rom_sms, work, table):
    """How many frames later the converted game reaches the same pictures with no input (the runtime is slower than the GG
    ports were: a VRAM clear through the colour check can cost seconds at boot). The SMS pad script and checkpoints are moved by it,
    so a Start pressed at frame 120 lands in the same place of the game."""
    d = os.path.join(work, 'lag'); os.makedirs(d, exist_ok=True)
    gf = list(range(30, LAG_FRAMES, 6))
    sf = list(range(30, LAG_FRAMES + LAG_MAX + 1, 2))
    for rom, con, fr in ((rom_gg, 'gg', gf), (rom_sms, 'sms', sf)):
        subprocess.run([exe, '--sms-run', rom, '--console', con, '--frames', str(max(fr) + 1), '--png-at', ','.join(map(str, fr)),
                        '--out', d, '--tag', con], capture_output=True, text=True, timeout=1800)
    try:
        g = {f: to_levels_gg(level_img(os.path.join(d, f'gg_f{f}.png'), False, table), table) for f in gf}
        sm = {f: to_levels_sms(level_img(os.path.join(d, f'sms_f{f}.png'), True, table)) for f in sf}
    except Exception:
        return 0
    live = [f for f in gf if len(np.unique(g[f].reshape(-1, 3), axis=0)) > 1]
    if not live:
        return 0
    best = (-1, 0)
    for lag in range(0, LAG_MAX + 1, 2):
        ms = [float((g[f] == sm[f + lag]).all(axis=2).mean()) for f in live if f + lag in sm]
        if ms:
            m = float(np.mean(ms))
            if m > best[0] + 0.01:          # the smallest lag that is clearly better
                best = (m, lag)
    return best[1]


def run(exe, rom, console, tag, out, script, lag=0):
    shots = sorted({f + lag + d for f in CHECKS for d in range(-DRIFT, DRIFT + 1)})
    script = shift_script(script, lag)
    args = [exe, '--sms-run', rom, '--console', console, '--frames', str(FRAMES + lag), '--png-at', ','.join(map(str, shots)),
            '--out', out, '--tag', tag, '--wav', os.path.join(out, tag + '.wav')]
    if script:
        args += ['--input', script]
    if console == 'gg':
        args += ['--gg-mono']        # the GG's stereo port ignored: the sound compared is what the SMS can play (one mix, no panning)
    r = subprocess.run(args, capture_output=True, text=True, timeout=1800)
    return r.returncode, (r.stdout or '')[-600:]


def level_img(path, crop, table):
    im = np.asarray(Image.open(path).convert('RGB'), dtype=np.int32)
    if crop:
        im = im[24:168, 48:208]
    return im


def to_levels_gg(im, table):
    # GG output is 8-bit per channel from 4-bit levels (x * 17); reduce with the converter's table
    lv = np.clip((im + 8) // 17, 0, 15)
    return np.array(table)[lv]


def to_levels_sms(im):
    return np.clip((im + 42) // 85, 0, 3)


def wav_env(path):
    try:
        with wave.open(path) as w:
            n, ch, rate = w.getnframes(), w.getnchannels(), w.getframerate()
            a = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float64)
    except Exception:
        return None
    if ch > 1:
        a = a.reshape(-1, ch).mean(axis=1)
    per = max(1, rate // 60)
    k = len(a) // per
    return np.sqrt((a[:k * per].reshape(k, per) ** 2).mean(axis=1))


def compare(work, tag, table, lag=0):
    res = {'checks': [], 'lag': lag}
    for f in CHECKS:
        a = os.path.join(work, f'{tag}_gg_f{f}.png')
        if not os.path.exists(a):
            continue
        ga = to_levels_gg(level_img(a, False, table), table)
        best = None
        for d in range(-DRIFT, DRIFT + 1):
            b = os.path.join(work, f'{tag}_sms_f{f + lag + d}.png')
            if not os.path.exists(b):
                continue
            sb = to_levels_sms(level_img(b, True, table))
            m = float((ga == sb).all(axis=2).mean())
            if best is None or m > best[0]:
                best = (m, d)
        p0 = os.path.join(work, f'{tag}_sms_f{f + lag}.png')
        sb0 = to_levels_sms(level_img(p0, True, table)) if os.path.exists(p0) else None
        res['checks'].append({'frame': f, 'match': round(best[0], 4) if best else None, 'shift': best[1] if best else None,
                              'colours': int(len(np.unique(sb0.reshape(-1, 3), axis=0))) if sb0 is not None else 0,
                              'ggColours': int(len(np.unique(ga.reshape(-1, 3), axis=0)))})
    ea, eb = wav_env(os.path.join(work, f'{tag}_gg.wav')), wav_env(os.path.join(work, f'{tag}_sms.wav'))
    if eb is not None and lag:
        eb = eb[lag:]                          # the converted game's sound, from where its pictures line up
    if ea is not None and eb is not None:
        k = min(len(ea), len(eb))
        ea, eb = ea[:k], eb[:k]
        if ea.std() < 1 and eb.std() < 1:
            res['sound'] = 1.0 if abs(ea.mean() - eb.mean()) < 50 else 0.0
        elif ea.std() < 1 or eb.std() < 1:
            res['sound'] = 0.0
        else:                                  # the best of a few frames' shift (the two cores' sound buffers differ by a frame or two)
            best = None
            for d in range(-4, 5):
                x, y = (ea[:k - d], eb[d:]) if d >= 0 else (ea[-d:], eb[:k + d])
                if len(x) > 60 and x.std() > 0 and y.std() > 0:
                    c = float(np.corrcoef(x, y)[0, 1])
                    if best is None or c > best[0]:
                        best = (c, d)
            res['sound'] = round(best[0], 3) if best else 0.0
            res['soundShift'] = best[1] if best else None
        res['loud'] = [round(float(ea.mean()), 1), round(float(eb.mean()), 1)]
    return res


def frames_hash(work, tag, side, lag=0):
    hs = []
    for f in CHECKS:
        p = os.path.join(work, f'{tag}_{side}_f{f + (lag if side == "sms" else 0)}.png')
        if os.path.exists(p):
            im = np.asarray(Image.open(p).convert('RGB'))
            if side == 'sms':
                im = im[24:168, 48:208]
            hs.append(hash(im.tobytes()))
    return hs


def grade(r):
    if r.get('build') != 0 and r.get('build') != 1:
        return 'Build fails'
    cs = r['cmp']['checks']
    if not cs or all(c['colours'] <= 1 for c in cs):
        return 'Black screen'
    moves_sms = len(set(r['hashSms'])) > 1
    moves_gg = len(set(r['hashGg'])) > 1
    if moves_gg and not moves_sms:
        return 'Boots only'
    ms = [c['match'] or 0 for c in cs]
    snd = r['cmp'].get('sound', 1.0)
    if min(ms) >= 0.999 and snd >= 0.95:
        return 'Perfect'
    if np.mean(ms) >= 0.97 and snd >= 0.8:
        return 'Playable'
    return 'Glitchy'


def one(game, work, exe):
    tag = game['id']
    gw = os.path.join(work, tag)
    os.makedirs(gw, exist_ok=True)
    out_sms = os.path.join(gw, tag + '.sms')
    prof = game.get('profile')
    args = [sys.executable, os.path.join(HERE, 'gg2sms.py'), 'feedback', game['rom'], out_sms, '--work', os.path.join(gw, 'fb'),
            '--report', os.path.join(gw, 'report.json'), '--workshop', exe]
    if prof:
        args += ['--profile', prof]
    r = subprocess.run(args, capture_output=True, text=True, timeout=3600)
    res = {'id': tag, 'name': game.get('name', tag), 'build': r.returncode, 'convert': (r.stdout or r.stderr).strip().splitlines()[:1]}
    try:
        rep = json.load(open(os.path.join(gw, 'report.json')))
        res['patches'] = len(rep.get('patches', [])); res['unhandled'] = len(rep.get('unhandled', [])); res['residual'] = len(rep.get('residual', []))
        res['verdict'] = rep.get('verdict'); res['warnings'] = rep.get('warnings', [])
        table = rep['settings'].get('colour_table') or gg2sms.rt.colour_table(rep['settings'].get('colours', 'round'))
        start = rep['settings'].get('start', 'both')
    except Exception as e:
        res['grade'] = 'Build fails'; res['error'] = str(e)
        return res
    if r.returncode not in (0, 1) or not os.path.exists(out_sms):
        res['grade'] = 'Build fails'
        return res
    pause_ok = start in ('pause', 'both') and not any('Pause cannot be used' in w for w in res.get('warnings', []))
    sms_script = hold_longer(SCRIPT.replace('Start', 'Select' if pause_ok else 'A+B'), HOLD)
    res['smsStart'] = 'Pause' if pause_ok else 'buttons 1+2'
    res['runGg'] = run(exe, game['rom'], 'gg', tag + '_gg', gw, SCRIPT)[0]
    lag = boot_lag(exe, game['rom'], out_sms, gw, table)
    res['runSms'] = run(exe, out_sms, 'sms', tag + '_sms', gw, sms_script, lag)[0]
    res['cmp'] = compare(gw, tag, table, lag)
    res['hashGg'] = frames_hash(gw, tag, 'gg'); res['hashSms'] = frames_hash(gw, tag, 'sms', lag)
    # the SMS path: some GG games carry their SMS code too (full 256-wide layouts once they think they run on an SMS). Reference
    # for those: the original cartridge run as an SMS (as a GG-as-SMS flash cart would), whole frames.
    res['runSmsRef'] = run(exe, game['rom'], 'sms', tag + '_ref', gw, sms_script)[0]
    ref = []
    for f in CHECKS:
        pa = os.path.join(gw, f'{tag}_ref_f{f}.png')
        best = None
        for d in range(-DRIFT, DRIFT + 1):
            pb = os.path.join(gw, f'{tag}_sms_f{f + lag + d}.png')
            if os.path.exists(pa) and os.path.exists(pb):
                x, y = to_levels_sms(level_img(pa, False, table)), to_levels_sms(level_img(pb, False, table))
                h = min(x.shape[0], y.shape[0])
                m = float((x[:h] == y[:h]).all(axis=2).mean())
                best = m if best is None else max(best, m)
        ref.append(None if best is None else round(best, 4))
    res['cmp']['vsSmsRef'] = ref
    gg_m = np.mean([c['match'] or 0 for c in res['cmp']['checks']]) if res['cmp']['checks'] else 0
    ref_m = np.mean([x or 0 for x in ref]) if ref else 0
    ref_live = any(len(np.unique(np.asarray(Image.open(os.path.join(gw, f'{tag}_ref_f{f}.png')).convert('RGB')).reshape(-1, 3), axis=0)) > 1
                   for f in CHECKS if os.path.exists(os.path.join(gw, f'{tag}_ref_f{f}.png')))
    res['path'] = 'sms' if ref_live and ref_m > gg_m + 0.2 else 'gg'
    if res['path'] == 'sms':                  # graded against the SMS run of the same cartridge
        for c, m in zip(res['cmp']['checks'], ref):
            c['match'] = m
        ea, eb = wav_env(os.path.join(gw, f'{tag}_ref.wav')), wav_env(os.path.join(gw, f'{tag}_sms.wav'))
        if ea is not None and eb is not None:
            eb = eb[lag:]; k = min(len(ea), len(eb)); ea, eb = ea[:k], eb[:k]
            best = None
            for d in range(-4, 5):
                x, y = (ea[:k - d], eb[d:]) if d >= 0 else (ea[-d:], eb[:k + d])
                if len(x) > 60 and x.std() > 0 and y.std() > 0:
                    c2 = float(np.corrcoef(x, y)[0, 1]); best = c2 if best is None else max(best, c2)
            if best is not None:
                res['cmp']['sound'] = round(best, 3)
    res['grade'] = grade(res)
    del res['hashGg'], res['hashSms']
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('roster', nargs='?')
    ap.add_argument('--rom'); ap.add_argument('--work', required=True); ap.add_argument('--only'); ap.add_argument('--jobs', type=int, default=2)
    ap.add_argument('--results'); ap.add_argument('--workshop'); ap.add_argument('--resume', action='store_true')
    a = ap.parse_args()
    exe = gg2sms.workshop_exe(a.workshop)
    if a.rom:
        games = [{'id': os.path.splitext(os.path.basename(a.rom))[0].replace(' ', '_'), 'rom': a.rom}]
    else:
        ro = json.load(open(a.roster, encoding='utf-8'))
        games = []
        for g in ro['games']:
            rom = g['rom'] if os.path.isabs(g['rom']) else os.path.join(ro['folder'], g['rom'])
            gg = dict(g, rom=rom)
            if g.get('profile') and not os.path.isabs(g['profile']):
                gg['profile'] = os.path.join(os.path.dirname(os.path.abspath(a.roster)), g['profile'])
            games.append(gg)
    if a.only:
        games = [g for g in games if a.only.lower() in (g['id'] + g.get('name', '')).lower()]
    games = [g for g in games if os.path.exists(g['rom']) or print('missing ROM: ' + g['rom'])]
    done = []
    if a.resume and a.results and os.path.exists(a.results):   # carry on a long run: the games already graded are kept, not run again
        done = json.load(open(a.results, encoding='utf-8'))
        ids = {r['id'] for r in done}
        games = [g for g in games if g['id'] not in ids]
    os.makedirs(a.work, exist_ok=True)
    results = list(done)
    with cf.ThreadPoolExecutor(max_workers=max(1, min(2, a.jobs))) as ex:
        for res in ex.map(lambda g: one(g, a.work, exe), games):
            results.append(res)
            if a.results:                      # after every game: a long run that stops early keeps what it graded
                with open(a.results, 'w', encoding='utf-8') as f:
                    json.dump(results, f, indent=1)
            ms = [c['match'] for c in res.get('cmp', {}).get('checks', [])]
            print(f"{res['grade']:<12} {res['name'][:44]:<44} match={ms} sound={res.get('cmp', {}).get('sound')} patches={res.get('patches')} "
                  f"unhandled={res.get('unhandled')} residual={res.get('residual')} lag={res.get('cmp', {}).get('lag')} path={res.get('path')} vsSmsRef={res.get('cmp', {}).get('vsSmsRef')}", flush=True)
    if a.results:
        with open(a.results, 'w', encoding='utf-8') as f:
            json.dump(results, f, indent=1)
    return 0


if __name__ == '__main__':
    sys.exit(main())
