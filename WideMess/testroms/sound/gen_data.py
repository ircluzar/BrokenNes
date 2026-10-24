# Generates the data of the WideMess sound test ROM (sound.asm for the 65816, spc.asm for the SPC700), docs/projects/widemess-spec.md section 4:
#   spc_samples.inc (SPC700) the S-DSP's directory and BRR sine for voices 0-7 at $0200
#   spc_steps.inc (SPC700) the step table: for each step, the DSP register writes
#                 ($00-$7F the S-DSP, $80-$FF the second page) the SPC700 program makes when the 65816 sends that step number
#   cpu_defs.inc, cpu_tables.inc  (65816) the DMA jobs that fill the sound RAM through the port $21F0-$21F3, the stream ring's rewrite, the read-back region
#   sram.bin      the bytes those jobs send: 24 samples (12 x 16-bit PCM, 12 x BRR) at 20-bit addresses, a one-shot PCM sample, two stream
#                 rings, the new voices' directory
#   ../bin/sound.json  the manifest Workshop --wms-test measures against: every voice's frequency, format, address, level, and the steps
# Usage: python -I gen_data.py <out_dir> <bin_dir>
import json, math, os, sys, zlib

out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
bindir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(out, '..', 'bin')

RATE = 32000
PERIOD = 32                      # every sine sample holds one cycle in 32 samples: frequency = pitch x 32000 / (4096 x 32)
GAIN_DIRECT = 0x7F               # GAIN direct mode, envelope 0x7F0
SOLO_L, SOLO_R = 0x60, 0x30      # solo steps: left 96/128, right 48/128 (the pan is checked too)
ALL_BASE = 32                    # the 32-voice step: VOL = 32 x 3000 / the sample's amplitude, so every voice comes out at about the same level
MVOL = 0x7F
WDIR = 0xFF000                   # the new voices' directory: the last 4 KB of the 1 MB
SDSP_DIR, SDSP_BRR = 0x0200, 0x0204

def pitch_for(f, period=PERIOD): return int(round(f * 4096 * period / RATE))
def freq_of(p, period=PERIOD): return p * RATE / (4096 * period)

def brr_encode(values, shift, loop=True):
    """filter-0 BRR, 16 samples a block; returns (bytes, the values the DSP decodes)."""
    assert len(values) % 16 == 0
    data, decoded = bytearray(), []
    blocks = len(values) // 16
    for b in range(blocks):
        flags = (3 if loop else 1) if b == blocks - 1 else 0
        data.append(shift << 4 | flags)
        nyb = []
        for v in values[b * 16:(b + 1) * 16]:
            n = max(-8, min(7, int(round(v / (1 << shift)))))
            nyb.append(n); decoded.append(n << shift)
        for i in range(0, 16, 2): data.append((nyb[i] & 15) << 4 | (nyb[i + 1] & 15))
    return bytes(data), decoded

def fundamental(values):
    n = len(values)
    re = sum(v * math.cos(2 * math.pi * i / n) for i, v in enumerate(values))
    im = sum(v * math.sin(2 * math.pi * i / n) for i, v in enumerate(values))
    return 2 * math.hypot(re, im) / n

def sine(amp, period, phase, count=None):
    count = count or period
    return [int(round(amp * math.sin(2 * math.pi * i / period + phase))) for i in range(count)]

def pcm_bytes(values): return b''.join(int(v).to_bytes(2, 'little', signed=True) for v in values)

# ------------------------------------------------------------------ the 32 voices
voices = []
# S-DSP voices 0-7: one BRR sine in the SPC700's 64 KB (shift 10, nibbles up to 7)
low_brr, low_dec = brr_encode(sine(7168, PERIOD, 0.0), 10)
for v in range(8):
    p = pitch_for(200 + 100 * v)
    voices.append(dict(voice=v, kind='s-dsp brr', address=SDSP_BRR, srcn=0, pitch=p, freq=freq_of(p), fund=fundamental(low_dec)))

# new voices 8-31: even = 16-bit PCM, odd = BRR. Each its own sample at its own 20-bit address. Voices 8-22 sit at $x2000 (x = 1..15) and 23-31 at
# $xA000 (x = 1..9): the low 16 bits repeat, so a voice that lost its top address bits would play the wrong bytes, and every sample has its own level.
brr_levels = [(9, 5), (9, 6), (9, 7), (10, 4), (10, 5), (10, 6), (10, 7), (11, 4), (11, 5), (9, 4), (10, 3), (11, 3)]
blobs = []          # (sound address, bytes)
for v in range(8, 32):
    addr = (v - 7) << 16 | 0x2000 if v <= 22 else (v - 22) << 16 | 0xA000
    p = pitch_for(200 + 100 * v)
    phase = 0.7 * v
    if v % 2 == 0:
        amp = 3000 + 250 * (v - 8)
        vals = sine(amp, PERIOD, phase)
        data, dec, fmt = pcm_bytes(vals), vals, 1 | 2           # PCM, looping
        end = addr + len(data)
        kind = 'pcm'
    else:
        shift, k = brr_levels[(v - 9) // 2]
        data, dec = brr_encode(sine(k * (1 << shift), PERIOD, phase), shift)
        fmt, end, kind = 0, 0, 'brr'
    blobs.append((addr, data))
    voices.append(dict(voice=v, kind=kind, address=addr, srcn=v, pitch=p, freq=freq_of(p), fund=fundamental(dec), format=fmt, end=end,
                       crc=zlib.crc32(data), length=len(data)))

# the other samples of the new voices
ONESHOT_SRCN, RING_SRCN = 40, 41
ONESHOT_ADDR, ONESHOT_LEN = 0xB4000, 3200            # 3,200 samples = 100 ms at pitch $1000, plays once
RING_ADDR, RING_LEN = 0xC4000, 512                   # 512 samples (1 KB) looped: 16 cycles of a 1 kHz sine, later 32 cycles of 2 kHz
oneshot = pcm_bytes(sine(6000, PERIOD, 0.3, ONESHOT_LEN))
ring_a = pcm_bytes(sine(5000, 32, 0.0, RING_LEN))     # 1000 Hz at pitch $1000
ring_b = pcm_bytes(sine(5000, 16, 0.0, RING_LEN))     # 2000 Hz
blobs.append((ONESHOT_ADDR, oneshot))
blobs.append((RING_ADDR, ring_a))

# the directory: 16 bytes an entry (start, format, loop, -, end, -)
def entry(start, fmt, loop, end):
    e = bytearray(16)
    e[0:3] = (start & 0xFFFFF).to_bytes(3, 'little'); e[3] = fmt
    e[4:7] = (loop & 0xFFFFF).to_bytes(3, 'little')
    e[8:11] = (end & 0xFFFFF).to_bytes(3, 'little')
    return bytes(e)
directory = bytearray(16 * 42)
for vo in voices[8:]:
    directory[vo['srcn'] * 16:(vo['srcn'] + 1) * 16] = entry(vo['address'], vo['format'], vo['address'], vo['end'])
directory[ONESHOT_SRCN * 16:(ONESHOT_SRCN + 1) * 16] = entry(ONESHOT_ADDR, 1, ONESHOT_ADDR, ONESHOT_ADDR + len(oneshot))        # PCM, no loop
directory[RING_SRCN * 16:(RING_SRCN + 1) * 16] = entry(RING_ADDR, 1 | 2, RING_ADDR, RING_ADDR + len(ring_a))                     # PCM, loops
blobs.append((WDIR, bytes(directory)))

# sram.bin = every blob, then ring_b (sent later, by the stream step)
sram, jobs = bytearray(), []
for addr, data in blobs:
    jobs.append((addr, len(sram), len(data))); sram += data
ring_b_offset = len(sram); sram += ring_b
with open(os.path.join(out, 'sram.bin'), 'wb') as f: f.write(sram)

# ------------------------------------------------------------------ the steps (DSP register writes)
def bank_of(v): return (v - 8) // 8
def reg(v, r): return (r | ((v - 8) % 8) << 4 | 0x80) if v >= 8 else (r | v << 4)
WBANK, WKON, WPMON, WNON, WEON, WDIRL, WDIRH = 0x8C, 0xCC, 0xAD, 0xBD, 0xCD, 0xDD, 0xED
KON, NON, EON, PMON = 0x4C, 0x3D, 0x4D, 0x2D

class Writes(list):
    """A step's DSP register writes; WBANK is written only when the bank changes."""
    bank = None
    def append(self, item):
        if item[0] == WBANK:
            if item[1] == self.bank: return
            self.bank = item[1]
        super().append(item)
    def __iadd__(self, items):
        for i in items: self.append(i)
        return self

def sel(v, w):   # select v's bank before writing its registers
    if v >= 8: w.append((WBANK, bank_of(v)))

def voice_setup(v, w, pitch=None, srcn=None, vol=(0, 0)):
    vo = voices[v]
    sel(v, w)
    p = vo['pitch'] if pitch is None else pitch
    w += [(reg(v, 2), p & 0xFF), (reg(v, 3), p >> 8), (reg(v, 4), vo['srcn'] if srcn is None else srcn), (reg(v, 5), 0), (reg(v, 7), GAIN_DIRECT),
          (reg(v, 0), vol[0]), (reg(v, 1), vol[1])]

def key_on(vs, w):
    groups = {}
    for v in vs: groups.setdefault(-1 if v < 8 else bank_of(v), 0); groups[-1 if v < 8 else bank_of(v)] |= 1 << ((v if v < 8 else v - 8) % 8)
    for g, bits in sorted(groups.items()):
        if g < 0: w.append((KON, bits))
        else: w += [(WBANK, g), (WKON, bits)]

steps = []
# step 0 (init): echo buffer first (ESA, EDL), then FLG (echo writes on, unmuted, noise at 32 kHz), the globals, the directories, every voice
init = Writes(); init += [(0x6D, 0x80), (0x7D, 0x01), (0x2C, 0), (0x3C, 0), (0x0D, 0), (0x6C, 0x1F), (0x0C, MVOL), (0x1C, MVOL),
        (PMON, 0), (NON, 0), (EON, 0), (0x5D, SDSP_DIR >> 8)]
init += [(0x0F + 0x10 * i, 0x7F if i == 0 else 0) for i in range(8)]
init += [(WDIRL, (WDIR >> 8) & 0xFF), (WDIRH, WDIR >> 16)]
for b in range(3): init += [(WBANK, b), (WPMON, 0), (WNON, 0), (WEON, 0)]
for v in range(32): voice_setup(v, init)
init.append((WBANK, 0))
steps.append(dict(name='init', writes=init))

for v in range(8, 32):           # steps 1-24: each new voice alone
    w = Writes(); voice_setup(v, w, vol=(SOLO_L, SOLO_R)); key_on([v], w)
    steps.append(dict(name=f'solo {v}', writes=w, voices=[v]))
w = Writes()                     # step 25: all 32 voices at once, each at about the same level (their pitches and samples are the init's)
for v in range(32):
    vol = max(1, min(127, int(round(ALL_BASE * 3000 / voices[v]['fund']))))
    voices[v]['all_vol'] = vol
    sel(v, w); w += [(reg(v, 0), vol), (reg(v, 1), vol)]
key_on(range(32), w)
steps.append(dict(name='all 32', writes=w, voices=list(range(32))))
w = Writes(); voice_setup(9, w, pitch=0x1000, vol=(SOLO_L, SOLO_R)); key_on([9], w)                       # step 26: BRR at pitch 1.0 (voice 8's solo is PCM at 1.0)
steps.append(dict(name='brr at pitch 1.0', writes=w, voices=[9]))
w = Writes(); w += [(0x0C, 0), (0x1C, 0), (0x2C, 0x7F), (0x3C, 0x7F), (WBANK, 0), (WEON, 0x01)]; voice_setup(8, w, vol=(SOLO_L, SOLO_R)); key_on([8], w)   # step 27: voice 8 heard only through the echo
steps.append(dict(name='echo on', writes=w, voices=[8]))
w = Writes(); w += [(0x0C, 0), (0x1C, 0), (0x2C, 0x7F), (0x3C, 0x7F), (WBANK, 0), (WEON, 0x00)]; voice_setup(8, w, vol=(SOLO_L, SOLO_R)); key_on([8], w)   # step 28: the same without its EON bit
steps.append(dict(name='echo off', writes=w, voices=[8]))
PM_MOD_F, PM_CARRIER = 50, 9
w = Writes(); w += [(0x0C, MVOL), (0x1C, MVOL), (0x2C, 0), (0x3C, 0), (WBANK, 0), (WEON, 0)]                      # step 29: voice 9 pitch-modulated by voice 8 (silent, 50 Hz)
voice_setup(8, w, pitch=pitch_for(PM_MOD_F), vol=(0, 0)); voice_setup(9, w, vol=(SOLO_L, SOLO_R)); w += [(WBANK, 0), (WPMON, 0x02)]; key_on([8, 9], w)
steps.append(dict(name='pitch modulation', writes=w, voices=[9]))
w = Writes(); w += [(WBANK, 0), (WPMON, 0), (WNON, 0x04)]; voice_setup(10, w, vol=(SOLO_L, SOLO_R)); key_on([10], w)   # step 30: voice 10 plays the noise
steps.append(dict(name='noise', writes=w, voices=[10]))
w = Writes(); w += [(WBANK, 0), (WNON, 0)]; voice_setup(13, w, pitch=0x1000, srcn=ONESHOT_SRCN, vol=(SOLO_L, SOLO_R)); key_on([13], w)   # step 31: a PCM sample that plays once
steps.append(dict(name='one-shot', writes=w, voices=[13]))
w = Writes(); voice_setup(12, w, pitch=0x1000, srcn=RING_SRCN, vol=(SOLO_L, SOLO_R)); key_on([12], w)  # step 32: a ring the 65816 rewrites while it plays
steps.append(dict(name='stream', writes=w, voices=[12]))
steps.append(dict(name='off', writes=[]))                                                        # step 33: silence
STREAM_STEP, STREAM_FRAMES, STREAM_REWRITE_AT, STEP_FRAMES = 32, 14, 6, 12

with open(os.path.join(out, 'spc_samples.inc'), 'w', newline='\r\n') as f:
    f.write('; generated by gen_data.py: do not edit\n')
    f.write(f'.ORGA ${SDSP_DIR:04X}\n.SECTION "SdspSamples" FORCE\n')
    f.write(f'  .DW ${SDSP_BRR:04X}, ${SDSP_BRR:04X}   ; S-DSP directory entry 0: start, loop\n')
    f.write('  .DB ' + ', '.join(f'${b:02X}' for b in low_brr) + '\n.ENDS\n')

with open(os.path.join(out, 'spc_steps.inc'), 'w', newline='\r\n') as f:
    f.write('; generated by gen_data.py: do not edit (included at the end of the code section)\n')
    f.write('StepTable:\n')
    for i in range(len(steps)): f.write(f'  .DW Step{i}\n')
    for i, s in enumerate(steps):
        f.write(f'Step{i}:   ; {s["name"]}\n  .DW {len(s["writes"])}\n')
        for k in range(0, len(s['writes']), 8):
            f.write('  .DB ' + ', '.join(f'${a:02X}, ${b & 0xFF:02X}' for a, b in s['writes'][k:k + 8]) + '\n')

with open(os.path.join(out, 'cpu_defs.inc'), 'w', newline='\r\n') as f:
    f.write('; generated by gen_data.py: do not edit\n')
    f.write(f'.DEFINE JOB_COUNT {len(jobs)}\n.DEFINE STEP_COUNT {len(steps)}\n.DEFINE STEP_FRAMES {STEP_FRAMES}\n')
    f.write(f'.DEFINE STREAM_STEP {STREAM_STEP}\n.DEFINE STREAM_FRAMES {STREAM_FRAMES}\n.DEFINE STREAM_REWRITE_LEFT {STREAM_FRAMES - STREAM_REWRITE_AT}\n')
    f.write(f'.DEFINE RING_ADDR_L ${RING_ADDR & 0xFF:02X}\n.DEFINE RING_ADDR_M ${(RING_ADDR >> 8) & 0xFF:02X}\n.DEFINE RING_ADDR_H ${RING_ADDR >> 16:02X}\n')
    f.write(f'.DEFINE RING_LEN {len(ring_b)}\n.DEFINE RING_B_OFFSET {ring_b_offset}\n')
    f.write(f'.DEFINE READBACK_L ${WDIR & 0xFF:02X}\n.DEFINE READBACK_M ${(WDIR >> 8) & 0xFF:02X}\n.DEFINE READBACK_H ${WDIR >> 16:02X}\n.DEFINE READBACK_LEN {len(directory)}\n')
with open(os.path.join(out, 'cpu_tables.inc'), 'w', newline='\r\n') as f:
    f.write('; generated by gen_data.py: do not edit (included in the code section)\n')
    f.write('JobAddrLo:\n' + ''.join(f'  .DW ${a & 0xFFFF:04X}\n' for a, _, _ in jobs))
    f.write('JobAddrHi:\n' + ''.join(f'  .DW ${a >> 16:04X}\n' for a, _, _ in jobs))
    f.write('JobSrc:\n' + ''.join(f'  .DW SramData+{o}\n' for _, o, _ in jobs))
    f.write('JobLen:\n' + ''.join(f'  .DW {n}\n' for _, _, n in jobs))

manifest = dict(
    rate=RATE, period=PERIOD, gain_env=GAIN_DIRECT * 16, mvol=MVOL, solo=[SOLO_L, SOLO_R], wdir=WDIR,
    voices=voices, steps=[dict(index=i, name=s['name'], voices=s.get('voices', [])) for i, s in enumerate(steps)],
    step_frames=STEP_FRAMES, stream=dict(step=STREAM_STEP, frames=STREAM_FRAMES, rewrite_at=STREAM_REWRITE_AT, address=RING_ADDR, length=len(ring_a), before_hz=1000, after_hz=2000),
    oneshot=dict(address=ONESHOT_ADDR, samples=ONESHOT_LEN, srcn=ONESHOT_SRCN, voice=13),
    pm=dict(carrier=PM_CARRIER, mod_hz=freq_of(pitch_for(PM_MOD_F))),
    readback=dict(address=WDIR, length=len(directory), sum=sum(directory) & 0xFFFF),
    blobs=[dict(address=a, length=len(d), crc=zlib.crc32(d)) for a, d in blobs],
)
os.makedirs(bindir, exist_ok=True)
with open(os.path.join(bindir, 'sound.json'), 'w', newline='\n') as f: json.dump(manifest, f, indent=1)
assert all(len(x['writes']) < 0x8000 for x in steps)
print(f'sram.bin {len(sram)} bytes in {len(jobs)} jobs; {len(steps)} steps, the longest {max(len(s["writes"]) for s in steps)} writes; sound.json')
