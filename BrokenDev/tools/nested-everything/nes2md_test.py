"""Nested NesGen harness: the NES game on BrokenNes's NES FIX cores vs the translated ROM on BrokenNes's Genesis core.

    python nes2md_test.py <game.nes> --work <dir> [--workshop exe] [--results results.json]

Runs both headless with the same scripted input (NES A = Genesis C, B = B, Select = A, Start = Start), then reports:
  * boot: the Genesis run ends without a dispatch miss / address error,
  * pictures: at each check frame, the NES picture (top 8 lines cropped, as the Genesis shows it) vs the Genesis picture:
    'shape' = share of pixels where both are background-black or both are not, 'best' = the same against the Genesis frames
    +-30 around it (the translated game boots a little later), so the timing offset does not hide a correct picture,
  * input response: the Genesis picture with the input script vs without it, after the first Start,
  * sound: Genesis RMS dB per second (the NES side comes from the MCP record_sound, not a CLI),
  * speed: NMIs run vs VInts, lost VInts (lag), lines per NMI, from the runtime's counters in work RAM,
  * cost: with --set count=on, 6502 instructions per NMI, and 68000 cycles per 6502 instruction.
The ROMs it makes stay in --work (commercial games never go in git)."""
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
import nes2md  # noqa: E402

SCRIPT_NES = '120:Start,126:,400:Right,480:,500:Start,506:,600:Right,680:,700:A,706:,800:Left,880:'
SCRIPT_MD = SCRIPT_NES.replace(':A,', ':C,')
CHECKS = [300, 500, 700, 900]
FRAMES = 1000
LINE_CYCLES = 3420 / 7          # 68000 cycles per line (NTSC)


def g(ram, off, n=4):
    return int.from_bytes(ram[0x8000 + off:0x8000 + off + n], 'big')


def pic(path, nes=False):
    im = np.asarray(Image.open(path).convert('RGB'), dtype=np.int32)
    if nes:
        im = im[8:232]
    return im


def shape(a, b):
    h = min(a.shape[0], b.shape[0])
    return float(((a[:h].sum(2) > 60) == (b[:h].sum(2) > 60)).mean())


def rms_db(path):
    try:
        w = wave.open(path)
        r, c = w.getframerate(), w.getnchannels()
        a = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float) / 32768
    except Exception:
        return []
    if c > 1:
        a = a.reshape(-1, c).mean(1)
    return [round(20 * np.log10(np.sqrt((a[i * r:(i + 1) * r] ** 2).mean()) + 1e-9), 1) for i in range(len(a) // r)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('rom')
    ap.add_argument('--work', required=True)
    ap.add_argument('--workshop')
    ap.add_argument('--results')
    ap.add_argument('--profile')
    ap.add_argument('--feedback', action='store_true', help='run the feedback loop into --profile first')
    a = ap.parse_args()
    exe = nes2md.workshop_exe(a.workshop)
    w = os.path.abspath(a.work)
    os.makedirs(w, exist_ok=True)
    md, mdc = os.path.join(w, 'game.md'), os.path.join(w, 'game_count.md')
    if a.feedback:
        nes2md.feedback(a.rom, md, a.profile, {}, os.path.join(w, 'fb'), None, exe, frames=FRAMES, inputs=SCRIPT_MD, rounds=60)
    rep = nes2md.convert(a.rom, md, a.profile, {}, os.path.join(w, 'build'))
    nes2md.convert(a.rom, mdc, a.profile, {'count': 'on'}, os.path.join(w, 'build_count'))
    res = {'rom': os.path.basename(a.rom), 'insns': rep['insns'], 'out_sha256': rep['out_sha256']}

    shots = sorted({f + d for f in CHECKS for d in range(-30, 31, 2)})
    nd, mdir, mdn = (os.path.join(w, x) for x in ('nes', 'md', 'md_noinput'))
    for d in (nd, mdir, mdn):
        os.makedirs(d, exist_ok=True)
    subprocess.run([exe, '--romtest', '--rom', a.rom, '--frames', str(FRAMES), '--input', SCRIPT_NES,
                    '--png-at', ','.join(map(str, CHECKS)), '--png-dir', nd], capture_output=True, text=True, timeout=1800)
    r = subprocess.run([exe, '--md-run', md, '--frames', str(FRAMES), '--input', SCRIPT_MD, '--png-at', ','.join(map(str, shots)),
                        '--png-dir', mdir, '--wav', os.path.join(w, 'md.wav'), '--ram-out', os.path.join(w, 'md_ram.bin'), '--quiet'],
                       capture_output=True, text=True, timeout=1800)
    subprocess.run([exe, '--md-run', md, '--frames', str(FRAMES), '--input', '120:Start,126:', '--png-at', ','.join(map(str, CHECKS)),
                    '--png-dir', mdn, '--quiet'], capture_output=True, text=True, timeout=1800)
    ram = open(os.path.join(w, 'md_ram.bin'), 'rb').read()
    res['boot'] = {'miss': ram[0x8042] == 0xA5, 'missed_at': '%04X' % g(ram, 0x40, 2),
                   'address_error': 'address errors: 0' not in r.stdout}
    nesfiles = sorted(os.listdir(nd))
    checks = []
    for f in CHECKS:
        nf = [x for x in nesfiles if x.endswith('.png') and int(''.join(c for c in x if c.isdigit()) or -1) == f]
        mf = os.path.join(mdir, 'f%06d.png' % f)
        if not nf or not os.path.exists(mf):
            checks.append({'frame': f, 'missing': True})
            continue
        npic = pic(os.path.join(nd, nf[0]), True)
        best, bestf = 0, f
        for s in shots:
            if abs(s - f) <= 30 and os.path.exists(os.path.join(mdir, 'f%06d.png' % s)):
                v = shape(npic, pic(os.path.join(mdir, 'f%06d.png' % s)))
                if v > best:
                    best, bestf = v, s
        noin = os.path.join(mdn, 'f%06d.png' % f)
        resp = None
        if os.path.exists(noin):
            resp = float((pic(mf) != pic(noin)).any(2).mean())
        checks.append({'frame': f, 'shape': round(shape(npic, pic(mf)), 4), 'best': round(best, 4), 'best_at': bestf,
                       'input_changed_px': resp})
    res['checks'] = checks
    res['sound_md_rms_db_per_s'] = rms_db(os.path.join(w, 'md.wav'))
    vints, nmis = g(ram, 0x10), g(ram, 0x14)
    res['speed'] = {'frames_emulated': FRAMES, 'vints_taken': vints, 'lost_vints': FRAMES - vints, 'nmis': nmis,
                    'lines_per_nmi': round(g(ram, 0x2C) / max(1, nmis), 1), 'longest_nmi_lines': g(ram, 0x30, 2)}
    # cost: both builds, NMIs 600..1000
    mid = 600
    sub = {}
    for tag, rom in (('plain', md), ('count', mdc)):
        vals = []
        for fr in (mid, FRAMES):
            p = os.path.join(w, '%s_%d.bin' % (tag, fr))
            subprocess.run([exe, '--md-run', rom, '--frames', str(fr), '--input', SCRIPT_MD, '--ram-out', p, '--quiet'],
                           capture_output=True, text=True, timeout=1800)
            vals.append(open(p, 'rb').read())
        sub[tag] = vals
    n = g(sub['plain'][1], 0x14) - g(sub['plain'][0], 0x14)
    lines = (g(sub['plain'][1], 0x2C) - g(sub['plain'][0], 0x2C)) / max(1, n)
    ic = (g(sub['count'][1], 0x34) - g(sub['count'][0], 0x34)) / max(1, g(sub['count'][1], 0x14) - g(sub['count'][0], 0x14))
    res['cost'] = {'window': [mid, FRAMES], 'lines_per_nmi': round(lines, 1), 'frame_share': round(lines / 262, 3),
                   'insns_per_nmi': round(ic), 'cycles_per_6502_insn': round(lines * LINE_CYCLES / max(1, ic), 1)}
    best = [c.get('best', 0) for c in checks]
    if res['boot']['miss'] or res['boot']['address_error']:
        grade = 'Boots only' if any(b > 0.9 for b in best) else 'Black screen'
    elif all(b >= 0.97 for b in best) and res['speed']['lost_vints'] < FRAMES * 0.1:
        grade = 'Playable'
    elif all(b >= 0.9 for b in best):
        grade = 'Glitchy'
    else:
        grade = 'Boots only'
    res['grade'] = grade
    print(json.dumps(res, indent=1))
    if a.results:
        with open(a.results, 'w') as f:
            json.dump(res, f, indent=1)


if __name__ == '__main__':
    main()
