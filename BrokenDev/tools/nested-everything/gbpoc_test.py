"""Nested GB proof-of-concept harness: convert GB test ROMs, run the result headless on BrokenNes, measure.

    python gbpoc_test.py gg   <rom.gb> [<rom.gb> ...] [--work dir] [--frames N] [--json out.json]
    python gbpoc_test.py snes <rom.gb> ...

For each ROM:
  1. the original on BrokenNes's GB core (Workshop --gbrun --pcs): serial text, the PC it ends on (blargg tests end
     in 'jr $'), then a second run with --stop-pc at that PC for the GB instruction count and frame
  2. convert (gb2gg / gb2snes, with the executed PCs as feedback)
  3. the converted ROM headless (gg: Workshop --sms-run --watch-pc <end> --battery-out; the serial text is the
     runtime's log in cart RAM $BC00; snes: Workshop --snesrun, the runtime's result block in WRAM)
  4. numbers: target cycles to the end / GB instructions to the end, frame ratio
"""
import argparse, json, os, re, subprocess, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sm83


def run(args, timeout=900):
    r = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    return r.stdout + r.stderr


def gb_reference(exe, rom, work, frames):
    tag = os.path.splitext(os.path.basename(rom))[0].replace(' ', '_').replace(',', '_')
    pcs = os.path.join(work, tag + '.pcs')
    out = run([exe, '--gbrun', '--rom', rom, '--frames', str(frames), '--pcs', pcs])
    m = re.search(r'PC=\$([0-9A-F]{4}) instr=([\d,]+)', out)
    end = int(m.group(1), 16)
    ser = re.search(r'serial: (.*)', out)
    out2 = run([exe, '--gbrun', '--rom', rom, '--frames', str(frames), '--stop-pc', '%04X' % end])
    m2 = re.search(r'stop-pc \$[0-9A-F]{4}: frame=(\d+) instr=(\d+)', out2)
    return {'tag': tag, 'pcs': pcs, 'end_pc': end, 'serial': ser.group(1).strip() if ser else '',
            'gb_frame': int(m2.group(1)), 'gb_instructions': int(m2.group(2))}


def gg_case(exe, rom, work, frames):
    ref = gb_reference(exe, rom, work, frames)
    out_rom = os.path.join(work, ref['tag'] + '.gg')
    for f in (out_rom, out_rom + '.json'):
        if os.path.exists(f): os.remove(f)
    conv = run([sys.executable, os.path.join(HERE, 'gb2gg.py'), 'convert', rom, out_rom, '--pcs', ref['pcs'],
                '--report', out_rom + '.json'])
    if not os.path.exists(out_rom):
        raise SystemExit('gb2gg failed: ' + conv[-1500:])
    sav = os.path.join(work, ref['tag'] + '.gg.sav')
    if os.path.exists(sav): os.remove(sav)
    o = run([exe, '--sms-run', out_rom, '--console', 'gg', '--frames', str(frames * 8), '--watch-pc', '%04X' % ref['end_pc'],
             '--out', work, '--tag', ref['tag'] + '_gg', '--battery-out', sav])
    watch = os.path.join(work, ref['tag'] + '_gg.watch.txt')
    res = dict(ref)
    res['convert'] = json.load(open(out_rom + '.json')).get('sites') if os.path.exists(out_rom + '.json') else conv[-400:]
    if os.path.exists(watch) and open(watch).read().strip():
        f = open(watch).readline().split()
        res['target_frame'] = int(f[0]); res['target_cycles'] = int(f[2])
        res['cycles_per_gb_instruction'] = round(res['target_cycles'] / ref['gb_instructions'], 2)
        res['frame_ratio'] = round(res['target_frame'] / max(1, ref['gb_frame']), 2)
    else:
        res['target_frame'] = None
    if os.path.exists(sav):
        b = open(sav, 'rb').read()
        log = b[0x3C00:0x3C00 + b[0x3CFF]] if len(b) >= 0x4000 else b''
        res['target_serial'] = log.decode('latin-1').replace('\n', ' | ').strip()
    res['passed'] = 'Passed' in res.get('target_serial', '') and 'Passed' in ref['serial']
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('target', choices=['gg', 'snes'])
    ap.add_argument('roms', nargs='+')
    ap.add_argument('--work', default=os.path.join(os.environ.get('TEMP', '.'), 'nested_gb_poc'))
    ap.add_argument('--frames', type=int, default=600)
    ap.add_argument('--workshop')
    ap.add_argument('--json')
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)
    exe = sm83.workshop_exe(a.workshop)
    results = []
    for rom in a.roms:
        if a.target == 'gg':
            r = gg_case(exe, rom, a.work, a.frames)
        else:
            import gb2snes
            r = gb2snes.test_case(exe, rom, a.work, a.frames, gb_reference, run)
        results.append(r)
        print(json.dumps({k: r.get(k) for k in ('tag', 'passed', 'serial', 'target_serial', 'gb_instructions', 'gb_frame',
                                                 'target_cycles', 'target_frame', 'cycles_per_gb_instruction', 'frame_ratio')}))
    if a.json:
        json.dump(results, open(a.json, 'w'), indent=1)
    sys.exit(0 if all(r['passed'] for r in results) else 1)


if __name__ == '__main__':
    main()
