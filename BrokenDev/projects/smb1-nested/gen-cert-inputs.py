# Inputs for the SMB1 Nested certification, from the remake's own parity schedules (source/engine/parity/probe_common.lua):
# the user's recorded play (recorded_input.csv: presses only in t=170235-170984, so that window is replayed) and the
# deterministic fuzz1..fuzz14 generator (ported 1:1: block of 22 frames, seed i*7919). t=1 = the frame after T0.
import csv, json, sys
REC = r'C:/Users/philt/OneDrive/Documents/PROJECTS/SMB1_Remake_dev/source/engine/parity/recorded_input.csv'
def fuzz_rand(block, seed):
    x = (block * 1103515 + seed * 12345 + 7919) % 2147483647
    return (x * 48271) % 2147483647
def fuzz_frame(t, seed):
    B = 22; block = (t - 1) // B; r = fuzz_rand(block, seed); h = set()
    d = r % 5
    if d in (0, 1): h.add('right')
    elif d == 2: h.add('left')
    if (r // 5) % 2 == 0: h.add('b')
    if (r // 11) % 3 != 0:
        if (t - 1) % B < 3 + (r // 37) % 26: h.add('a')
    if (r // 101) % 7 == 0 and (t % B) > 14: h.add('down')
    return h
def rec_frames(lo=170200, hi=171000):
    out = []
    for r in csv.DictReader(open(REC)):
        t = int(r['t'])
        if lo <= t <= hi: out.append({k for k in ('up', 'down', 'left', 'right', 'a', 'b') if r[k] == '1'})
    return out
def script(frames, start, snes, pre):
    names = [('up', 'Up'), ('down', 'Down'), ('left', 'Left'), ('right', 'Right'), ('a', 'B' if snes else 'A'), ('b', 'Y' if snes else 'B')]
    parts = list(pre); last = None
    for i, h in enumerate(frames):
        cur = '+'.join(N for k, N in names if k in h)
        if cur != last: parts.append(f'{start + 1 + i}:{cur}'); last = cur
    parts.append(f'{start + 1 + len(frames)}:')
    return ','.join(parts)
scen = {'recorded': rec_frames()}
for i in range(1, 15): scen[f'fuzz{i}'] = [fuzz_frame(t, i * 7919) for t in range(1, 2401)]
nesT0 = 203
nes = {k: script(v, nesT0, False, ['5:Start', '31:']) for k, v in scen.items()}
json.dump({'nesT0': nesT0, 'scenarios': nes}, open('smb1_nes_inputs.json', 'w'), indent=1)
plans = [{'name': 'title', 'frames': 1500, 'input': '75:Start,101:'},
         {'name': 'verifyplay', 'frames': 3000, 'input': '240:Start,246:,600:Right,1300:Right+B,1360:Right,1500:Right+Y,1700:Right+B+Y,1760:Right+Y'}]
for k, v in scen.items():
    plans.append({'name': k, 'frames': 300 + len(v) + 120, 'input': script(v, 300, True, ['75:Start', '101:'])})
json.dump(plans, open('smb1-feedback-plans.json', 'w'), indent=1)
print(len(plans), 'plans;', {k: len(v) for k, v in nes.items()})
