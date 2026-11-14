"""Grade SMS -> Genesis translations on BrokenNes's own cores (headless Workshop --sms-run / --md-run).

    python sms2md_test.py <roster.json> --work <dir> [--only substr] [--results results.json] [--workshop exe] [--bizhawk]
    python sms2md_test.py --rom <in.sms> --work <dir> [--profile p.json]

Per game: sms2md.py feedback (dynamic pass, translate), then
  1. boot: both sides without input; the boot delay = the Genesis frame whose 256x192 picture first matches the SMS picture
     at the profile's 'title_frame' (the translation runs slower, so its script is shifted by that delay);
  2. play: the profile's pad script on the SMS core, the same script shifted on the Genesis core (A -> B, B -> C, Select/Pause -> Start);
     pictures every frame, the sound, the runtime's counters (frames, interrupts, HALT frames, errors);
  3. compare: per SMS sample frame the best Genesis frame within the search window (picture match on the SMS's 2-bit levels,
     top-left 256x192 of the Genesis picture), the lag (Genesis frame - SMS frame - boot delay) over time, input response
     (frames from a press to the first picture change, both sides), per-frame loudness correlation, idle (HALT) frames per side.
Grades (the Nested climb's): Perfect, Playable (>= 97 % picture at the checkpoints, sound alike, lag <= 2 frames), Glitchy (runs and
answers the pad), Boots only, Black screen, Build fails. Results hold names and numbers only; ROMs/pictures stay in --work.
"""
import argparse
import json
import os
import subprocess
import sys
import wave

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sms2md  # noqa: E402

MDV = np.array([0, 2, 5, 7]) * 255 // 7
MAP = {'A': 'B', 'B': 'C', 'Select': 'Start'}


def env():
    e = dict(os.environ)
    return e


def run(args, timeout=3600):
    r = subprocess.run(args, capture_output=True, text=True, timeout=timeout, env=env())
    return r.returncode, (r.stdout or '') + (r.stderr or '')


def parse_script(s):
    ev = []
    for part in [p for p in s.split(',') if p]:
        f, b = part.split(':')
        ev.append((int(f), b))
    return ev


def md_script(s, shift):
    out = []
    for f, b in parse_script(s):
        bb = '+'.join(MAP.get(x, x) for x in b.split('+') if x)
        out.append('%d:%s' % (f + shift, bb))
    return ','.join(out)


def sms_img(d, f):
    p = os.path.join(d, 's_f%d.png' % f)
    if not os.path.exists(p):
        return None
    a = np.asarray(Image.open(p).convert('RGB'), dtype=np.int32)
    return np.clip((a + 42) // 85, 0, 3)


def md_img(d, f):
    p = os.path.join(d, 'f%06d.png' % f)
    if not os.path.exists(p):
        return None
    a = np.asarray(Image.open(p).convert('RGB'), dtype=np.int32)[:192]
    return np.abs(a[..., None] - MDV[None, None, None, :]).argmin(-1)


def same(a, b):
    return float((a == b).all(-1).mean())


def loud(path):
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


def counters(ram):
    d = open(ram, 'rb').read()
    v = lambda a, n=4: int.from_bytes(d[a - 0xFF0000:a - 0xFF0000 + n], 'big')
    return {'frames': v(0xFFF070), 'irqs': v(0xFFF074), 'halts': v(0xFFF078), 'idleFrames': v(0xFFF08A), 'error': bool(v(0xFFF084, 1)),
            'errKind': v(0xFFF082, 2), 'errAddr': v(0xFFF080, 2)}


def test_game(g, work, exe, bizhawk=False):
    os.makedirs(work, exist_ok=True)
    res = {'id': g['id']}
    rom = g['rom']
    prof = g.get('profileData', {})
    out = os.path.join(work, g['id'] + '.md')
    a = [sys.executable, os.path.join(HERE, 'sms2md.py'), 'feedback', rom, out, '--work', os.path.join(work, 'build'),
         '--report', os.path.join(work, 'report.json'), '--workshop', exe]
    if g.get('profile'):
        a += ['--profile', g['profile']]
    code, log = run(a)
    if code or not os.path.exists(out):
        res.update(grade='Build fails', log=log[-600:])
        return res
    rep = json.load(open(os.path.join(work, 'report.json')))
    res['translation'] = {k: rep[k] for k in ('out_sha256', 'out_size', 'instructions', 'stats')}
    # 1. boot delay
    tf = prof.get('title_frame', 110)
    sb, mb = os.path.join(work, 'sboot'), os.path.join(work, 'mboot')
    os.makedirs(sb, exist_ok=True)
    os.makedirs(mb, exist_ok=True)
    run([exe, '--sms-run', rom, '--console', 'sms', '--frames', str(tf + 1), '--png-at', str(tf), '--out', sb, '--tag', 's'])
    bootn = tf * 8
    run([exe, '--md-run', out, '--frames', str(bootn), '--png-at', ','.join(str(x) for x in range(tf, bootn)), '--png-dir', mb, '--quiet'])
    ref = sms_img(sb, tf)
    delay, best = None, 0
    for f in range(tf, bootn):
        m = md_img(mb, f)
        if m is None or ref is None:
            continue
        sc = same(ref, m)
        if sc > best:
            best, delay = sc, f - tf
        if sc >= 0.995:
            break
    res['boot'] = {'titleFrame': tf, 'delay': delay, 'match': round(best, 4)}
    if delay is None or best < 0.5:
        _, lg = run([exe, '--md-run', out, '--frames', '600', '--ram-out', os.path.join(mb, 'ram.bin'), '--quiet'])
        c = counters(os.path.join(mb, 'ram.bin'))
        res.update(grade='Black screen' if best < 0.05 else 'Boots only', md=c)
        return res
    # 2. play
    frames = prof.get('frames', 1500)
    script = prof.get('input', '')
    sp, mp = os.path.join(work, 'splay'), os.path.join(work, 'mplay')
    os.makedirs(sp, exist_ok=True)
    os.makedirs(mp, exist_ok=True)
    allf = ','.join(str(x) for x in range(frames))
    run([exe, '--sms-run', rom, '--console', 'sms', '--frames', str(frames), '--png-at', allf, '--out', sp, '--tag', 's',
         '--wav', os.path.join(sp, 's.wav'), '--input', script, '--watch-pc', prof.get('halt_pc', '21DD')])
    mf = frames + delay
    run([exe, '--md-run', out, '--frames', str(mf), '--png-at', ','.join(str(x) for x in range(mf)), '--png-dir', mp,
         '--wav', os.path.join(mp, 'm.wav'), '--ram-out', os.path.join(mp, 'ram.bin'), '--input', md_script(script, delay), '--quiet'])
    c = counters(os.path.join(mp, 'ram.bin'))
    res['md'] = c
    wl = os.path.join(sp, 's.watch.txt')
    shalt = len({l.split()[0] for l in open(wl)}) if os.path.exists(wl) else None
    res['idle'] = {'smsHaltFrames': shalt, 'smsFrames': frames, 'mdHaltFrames': c['idleFrames'], 'mdFrames': mf}
    # 3. compare: sample every 10 SMS frames, search the Genesis window [f+delay-10, f+delay+120]
    lags, matches = [], []
    mcache = {}

    def mi(f):
        if f not in mcache:
            mcache[f] = md_img(mp, f)
        return mcache[f]
    for f in range(tf, frames, 10):
        s = sms_img(sp, f)
        if s is None:
            continue
        bs, bf = -1, None
        for g2 in range(max(0, f + delay - 10), min(mf, f + delay + 120)):
            m = mi(g2)
            if m is None:
                continue
            sc = same(s, m)
            if sc > bs:
                bs, bf = sc, g2
            if sc == 1.0:
                break
        matches.append(bs)
        lags.append(bf - f - delay if bf is not None else None)
    res['picture'] = {'samples': len(matches), 'meanMatch': round(float(np.mean(matches)), 4), 'minMatch': round(float(np.min(matches)), 4),
                      'exact': sum(1 for m in matches if m >= 0.999), 'lagStart': lags[0], 'lagEnd': lags[-1], 'lagMax': max(x for x in lags if x is not None)}
    # input response
    resp = []
    for f, b in parse_script(script):
        if not b:
            continue
        def first_change(img, base, lim):
            p0 = img(base - 1)
            for k in range(base, base + lim):
                p = img(k)
                if p is None or p0 is None:
                    return None
                if not (p == p0).all():
                    return k - base
            return None
        rs = first_change(lambda k: sms_img(sp, k), f, 30)
        rm = first_change(mi, f + delay, 60)
        resp.append({'frame': f, 'button': b, 'sms': rs, 'md': rm})
    res['input'] = resp
    ls, lm = loud(os.path.join(sp, 's.wav')), loud(os.path.join(mp, 'm.wav'))
    if ls is not None and lm is not None and len(lm) > delay:
        lm2 = lm[delay:delay + len(ls)]
        n = min(len(ls), len(lm2))
        corr = float(np.corrcoef(ls[:n], lm2[:n])[0, 1]) if n > 10 and ls[:n].std() > 0 and lm2[:n].std() > 0 else None
        res['sound'] = {'smsMeanRms': round(float(ls.mean()), 1), 'mdMeanRms': round(float(lm.mean()), 1), 'loudnessCorr': None if corr is None else round(corr, 3)}
    ok_sound = res.get('sound', {}).get('loudnessCorr') or 0
    pm = res['picture']
    answers = any(r['md'] is not None for r in resp)
    if c['error']:
        grade = 'Glitchy' if answers else 'Boots only'
    elif pm['exact'] == pm['samples'] and ok_sound > 0.95 and pm['lagMax'] <= 0:
        grade = 'Perfect'
    elif pm['meanMatch'] >= 0.97 and ok_sound > 0.8 and pm['lagMax'] <= 2:
        grade = 'Playable'
    elif answers:
        grade = 'Glitchy'
    else:
        grade = 'Boots only'
    res['grade'] = grade
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('roster', nargs='?')
    ap.add_argument('--rom')
    ap.add_argument('--profile')
    ap.add_argument('--work', required=True)
    ap.add_argument('--only')
    ap.add_argument('--results')
    ap.add_argument('--workshop')
    a = ap.parse_args()
    exe = sms2md.workshop_exe(a.workshop)
    games = []
    if a.rom:
        games.append({'id': os.path.splitext(os.path.basename(a.rom))[0], 'rom': a.rom, 'profile': a.profile})
    else:
        r = json.load(open(a.roster, encoding='utf-8'))
        for g in r['games']:
            if a.only and a.only not in g['id']:
                continue
            games.append(dict(g))
    out = []
    for g in games:
        pid = g.get('profile') or os.path.join(sms2md.PIPE, 'profiles', g['id'] + '.json')
        if os.path.exists(pid):
            g['profile'] = pid
            g['profileData'] = json.load(open(pid, encoding='utf-8'))
        else:
            g['profile'] = None
        if not os.path.exists(g['rom']):
            out.append({'id': g['id'], 'grade': 'missing ROM'})
            continue
        r = test_game(g, os.path.join(a.work, g['id']), exe)
        print(json.dumps(r, indent=1))
        out.append(r)
    if a.results:
        with open(a.results, 'w', encoding='utf-8') as f:
            json.dump({'pipeline': 'sms2md', 'games': out}, f, indent=1)


if __name__ == '__main__':
    main()
