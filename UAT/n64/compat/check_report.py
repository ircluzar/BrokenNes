# [UAT/n64] Pass check for one compat run: python -I check_report.py <run dir> [--twice]
# PASS when the probe ended rc=0, error is null, no first-chance exceptions, every scripted checkpoint was reached and,
# with --twice (COMPAT_TWICE=1 runs), determinism.identical is true.
import json, sys, os
d = sys.argv[1]; twice = '--twice' in sys.argv
r = json.load(open(os.path.join(d, 'report.json')))
log = open(os.path.join(d, 'probe.log'), errors='replace').read().strip().splitlines()
bad = []
if not log or log[-1].strip() != 'rc=0': bad.append('probe ' + (log[-1] if log else 'no log'))
if r.get('error'): bad.append('error ' + str(r['error']))
if r.get('firstChanceExceptions'): bad.append('exceptions %d' % len(r['firstChanceExceptions']))
if twice:
    det = r.get('determinism') or {}
    if not (det.get('checked') and det.get('identical')): bad.append('determinism %s' % det)
print('%s %s frames=%s checkpoints=%d wallMs=%s %s' % ('PASS' if not bad else 'FAIL', r.get('title'), r.get('framesRun'),
      len(r.get('checkpoints') or []), r.get('wallMs'), '; '.join(bad)))
sys.exit(1 if bad else 0)
