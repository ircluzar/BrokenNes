"""Cross-check converted GG -> SMS ROMs on BrokenDev's other emulators (Mesen 2 and BizHawk Genesis Plus GX in SMS mode).

    python gg2sms_crosscheck.py <results.json> --work <harness work dir> [--emu mesen,bizhawk-gpgx-sms] [--only substr] [--out xc.json]

For every converted game of a harness run (gg2sms_test.py), the converted ROM runs on each emulator with the harness's SMS pad script
(moved by the boot lag the harness measured), pictures at the checkpoints; each picture is compared (centre 160x144, 2-bit levels) with
the original on BrokenNes's GG core (the harness's reference) and with the converted ROM on BrokenNes's SMS core. Names and numbers only.
"""
import argparse
import json
import os
import subprocess
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gg2sms_test as T      # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('results'); ap.add_argument('--work', required=True)
    ap.add_argument('--emu', default='mesen,bizhawk-gpgx-sms'); ap.add_argument('--only'); ap.add_argument('--out')
    a = ap.parse_args()
    res = json.load(open(a.results, encoding='utf-8'))
    out = []
    for g in res:
        if g.get('grade') in (None, 'Build fails') or 'cmp' not in g:
            continue
        if a.only and a.only.lower() not in (g['id'] + g.get('name', '')).lower():
            continue
        gw = os.path.join(a.work, g['id'])
        rom = os.path.join(gw, g['id'] + '.sms')
        rep = json.load(open(os.path.join(gw, 'report.json')))
        table = T.gg2sms.rt.colour_table(rep['settings'].get('colours', 'round'))
        start = rep['settings'].get('start', 'both')
        lag = g['cmp'].get('lag', 0) or 0
        # buttons 1+2 for GG Start (the runtime takes both when start=both): the emulators' Lua pads have no Pause button
        if start not in ('both', 'buttons12'):
            print(f"{g['id']}: start={start}, skipped (needs buttons 1+2)"); continue
        script = T.shift_script(T.SCRIPT.replace('Start', 'A+B'), lag)
        shots = sorted({f + lag + d for f in T.CHECKS for d in (-1, 0, 1)})
        row = {'id': g['id'], 'name': g.get('name', g['id']), 'harnessGrade': g['grade'], 'lag': lag, 'emus': {}}
        for emu in a.emu.split(','):
            xo = os.path.join(gw, 'xc_' + emu)
            r = subprocess.run(['powershell', '-NoProfile', '-File', os.path.join(HERE, 'gg2sms_crosscheck.ps1'), '-Rom', rom, '-Console', 'sms',
                                '-Emu', emu, '-Out', xo, '-Frames', str(max(shots) + 2), '-Shots', ','.join(map(str, shots)), '-Pad', script],
                               capture_output=True, text=True, timeout=3600)
            vs_gg, vs_bn = [], []
            for f in T.CHECKS:
                ga = os.path.join(gw, f'{g["id"]}_gg_f{f}.png')
                bn = os.path.join(gw, f'{g["id"]}_sms_f{f + lag}.png')
                best_gg = best_bn = None
                for d in (-1, 0, 1):
                    p = os.path.join(xo, f'shot_{f + lag + d:05d}.png')
                    if not os.path.exists(p):
                        continue
                    sm = T.to_levels_sms(T.level_img(p, True, table))
                    if os.path.exists(ga):
                        m = float((T.to_levels_gg(T.level_img(ga, False, table), table) == sm).all(axis=2).mean())
                        best_gg = m if best_gg is None else max(best_gg, m)
                    if os.path.exists(bn):
                        m = float((T.to_levels_sms(T.level_img(bn, True, table)) == sm).all(axis=2).mean())
                        best_bn = m if best_bn is None else max(best_bn, m)
                vs_gg.append(None if best_gg is None else round(best_gg, 4))
                vs_bn.append(None if best_bn is None else round(best_bn, 4))
            row['emus'][emu] = {'ran': r.returncode == 0, 'vsGg': vs_gg, 'vsBrokenNesSms': vs_bn}
            print(f"{g['id'][:30]:<30} {emu:<18} vsGG={vs_gg} vsBrokenNes={vs_bn}", flush=True)
        out.append(row)
    if a.out:
        json.dump(out, open(a.out, 'w', encoding='utf-8'), indent=1)
    return 0


if __name__ == '__main__':
    sys.exit(main())
