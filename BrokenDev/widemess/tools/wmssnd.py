"""wmssnd.py: the WideMess devkit's sound converter (BrokenDev/widemess, docs/projects/widemess-devkit.md).

WAV -> samples for the WideMess sound chip (docs/projects/widemess-spec.md section 4), MIDI -> songs for the devkit's sound driver
(driver/wmsdrv.asm), and the whole sound RAM image the driver needs, as an upload table for the main CPU's sound RAM port.

Modes
  python wmssnd.py build <wms.json> <outdir> --driver <wmsdrv.bin> [--snesbrr <snesbrr.exe>]
        every sound of a project: <outdir>/snd.asm, snd.h, snd_*.bin, snd.json
  python wmssnd.py pcm <wav> <out.pcm> [--rate 32000] [--loop SAMPLE]      a WAV as 16-bit PCM (mono, little-endian, whole groups of 4)
  python wmssnd.py brr <wav> <out.brr> --snesbrr <exe> [--rate 32000] [--loop SAMPLE]   a WAV as BRR (PVSnesLib's snesbrr encodes it)
  python wmssnd.py midi <mid>                                                 what a MIDI file holds (channels, notes, polyphony, length)

The build mode's wms.json section:
  "sound": {
    "samples": [ { "name": "bass", "wav": "audio/bass.wav", "format": "pcm16", "rate": 32000, "root": 36, "loop": 1024,
                   "adsr": ["8F", "E0"] },
                 { "name": "laser", "wav": "audio/laser.wav", "format": "brr", "rate": 16000 } ],
    "songs":   [ { "name": "theme", "mid": "audio/theme.mid", "instruments": { "0": "bass", "1": "lead" }, "volume": 0.5,
                   "loop": true } ],
    "effects": [ { "name": "shot", "sample": "laser", "note": 72, "voice": 0, "volume": 110, "pan": 64 } ]
  }
A sample's "root" is the MIDI note it sounds at its own rate (default 60). "loop" is the sample (frame) it loops from; without it the
sample plays once. Songs: every MIDI channel that has notes must be named in "instruments" (or listed in "ignore"), with a sample, or
with a drum kit, { "36": "kick", "38": "snare" } (MIDI note -> sample, each played at its own pitch); notes become the
driver's 24 channels (voices 8-31), at most 24 at once; time is quantised to the driver's tick (160 a second). Effects play one note
of a sample on one voice: voices 0-7 play BRR only (the S-DSP's own voices, samples below 64 KB), voices 8-31 anything.
Nothing is dropped silently: too many notes at once, a pitch above 4x, a sample that does not fit, a MIDI channel without an
instrument: each is an error naming what to change.
"""
import json
import os
import struct
import subprocess
import sys
import tempfile
import wave

import numpy as np

# ---- the driver's layout (driver/wmsdrv.asm's constants)
DRIVER = 0x0200
DRIVER_MAX = 0x1000 - DRIVER
BANK = 0x1000
BANK_END = 0x7F00
SNES_DIR = 0x7F00
BRR_LOW = 0x8000
SILENT = 0xFF00
BRR_LOW_END = SILENT
WIDE_DIR = 0x10000
HIGH = 0x11000
RAM = 0x100000
TICK_DIVIDER = 50                 # timer 0 at 8 kHz / 50 = 160 ticks a second
TICK_RATE = 8000.0 / TICK_DIVIDER
MAX_CH = 24
PIECE = 32768


class SndError(Exception):
    pass


# ------------------------------------------------------------------------------------------------ WAV

def read_wav(path):
    """(mono float array -1..1, rate). 8-, 16-, 24- or 32-bit PCM WAV; channels are averaged."""
    with wave.open(path, 'rb') as w:
        ch, width, rate, n = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(n)
    if width == 1:
        a = (np.frombuffer(raw, dtype=np.uint8).astype(np.float64) - 128) / 128
    elif width == 2:
        a = np.frombuffer(raw, dtype='<i2').astype(np.float64) / 32768
    elif width == 3:
        b = np.frombuffer(raw, dtype=np.uint8).reshape(-1, 3)
        v = (b[:, 0].astype(np.int32) | b[:, 1].astype(np.int32) << 8 | b[:, 2].astype(np.int32) << 16)
        v[v >= 1 << 23] -= 1 << 24
        a = v.astype(np.float64) / (1 << 23)
    elif width == 4:
        a = np.frombuffer(raw, dtype='<i4').astype(np.float64) / (1 << 31)
    else:
        raise SndError(f'{path}: {8 * width}-bit samples (8, 16, 24 or 32-bit PCM WAV only)')
    a = a.reshape(-1, ch).mean(axis=1)
    return a, rate


def resample(a, rate, to):
    if rate == to or len(a) == 0:
        return a
    n = max(1, int(round(len(a) * to / rate)))
    x = np.arange(n) * (rate / to)
    return np.interp(x, np.arange(len(a)), a)


def to_pcm16(a, loop=None):
    """Signed 16-bit little-endian, whole groups of 4 samples, peak kept below 32,700 (the spec: the interpolation's partial sum
    wraps within a few steps of full scale). Returns (bytes, loop sample or None)."""
    peak = float(np.max(np.abs(a))) if len(a) else 0
    if peak * 32767 > 32700:
        a = a * (32700 / (peak * 32767))
    v = np.clip(np.round(a * 32767), -32700, 32700).astype('<i2')
    if loop is not None:
        loop = (int(loop) // 4) * 4
        if loop >= len(v):
            raise SndError(f'loop point {loop} is past the end ({len(v)} samples)')
    pad = (-len(v)) % 4
    if pad:
        v = np.concatenate([v, np.zeros(pad, dtype='<i2')])
    return v.tobytes(), loop


def to_brr(a, rate, loop, snesbrr):
    """BRR through PVSnesLib's snesbrr. Returns (bytes, loop block offset in bytes or None)."""
    if not snesbrr or not os.path.exists(snesbrr):
        raise SndError(f'BRR needs PVSnesLib\'s snesbrr.exe (looked for {snesbrr})')
    peak = float(np.max(np.abs(a))) if len(a) else 0
    if peak > 0.98:
        a = a * (0.98 / peak)
    v = np.clip(np.round(a * 32767), -32768, 32767).astype('<i2')
    if loop is not None:
        loop = (int(loop) // 16) * 16
    pad = (-len(v)) % 16
    if pad:
        v = np.concatenate([v, np.zeros(pad, dtype='<i2')])
    with tempfile.TemporaryDirectory() as td:
        wav = os.path.join(td, 'in.wav')
        brr = os.path.join(td, 'out.brr')
        with wave.open(wav, 'wb') as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes(v.tobytes())
        cmd = [snesbrr, '-e'] + (['-l', str(loop)] if loop is not None else []) + [wav, brr]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0 or not os.path.exists(brr):
            raise SndError(f'snesbrr failed: {r.stdout} {r.stderr}')
        data = open(brr, 'rb').read()
    if len(data) % 9 == 2:          # a loop header (2 bytes) in front of the blocks
        data = data[2:]
    if len(data) % 9:
        raise SndError(f'snesbrr wrote {len(data)} bytes, not whole 9-byte blocks')
    return data, (loop // 16) * 9 if loop is not None else None


# ------------------------------------------------------------------------------------------------ MIDI

def read_var(d, i):
    v = 0
    while True:
        b = d[i]
        i += 1
        v = (v << 7) | (b & 0x7F)
        if not b & 0x80:
            return v, i


def read_midi(path):
    """Notes (start tick, end tick, channel, note, velocity, cc7, pan), the tempo map [(tick, us per beat)], the division."""
    d = open(path, 'rb').read()
    if d[:4] != b'MThd':
        raise SndError(f'{path}: not a standard MIDI file')
    hl, fmt, ntr, div = struct.unpack('>IHHH', d[4:14])
    if div & 0x8000:
        raise SndError(f'{path}: SMPTE time division is not supported (use ticks per beat)')
    i = 8 + hl
    notes, tempos = [], [(0, 500000)]
    for _ in range(ntr):
        if d[i:i + 4] != b'MTrk':
            raise SndError(f'{path}: a track chunk is missing')
        ln = struct.unpack('>I', d[i + 4:i + 8])[0]
        j, end = i + 8, i + 8 + ln
        t, status = 0, 0
        on = {}
        cc7 = [100] * 16
        pan = [64] * 16
        while j < end:
            dt, j = read_var(d, j)
            t += dt
            b = d[j]
            if b & 0x80:
                status = b
                j += 1
            if status == 0xFF:
                typ = d[j]
                ml, j = read_var(d, j + 1)
                if typ == 0x51 and ml == 3:
                    tempos.append((t, d[j] << 16 | d[j + 1] << 8 | d[j + 2]))
                j += ml
                if typ == 0x2F:
                    break
                continue
            if status in (0xF0, 0xF7):
                ml, j = read_var(d, j)
                j += ml
                continue
            hi, ch = status & 0xF0, status & 0x0F
            if hi in (0xC0, 0xD0):
                j += 1
                continue
            a1, a2 = d[j], d[j + 1]
            j += 2
            if hi == 0x90 and a2 > 0:
                if (ch, a1) in on:      # a note struck again before its end: end the first
                    s, v, c7, pn = on.pop((ch, a1))
                    notes.append((s, t, ch, a1, v, c7, pn))
                on[(ch, a1)] = (t, a2, cc7[ch], pan[ch])
            elif hi == 0x80 or (hi == 0x90 and a2 == 0):
                if (ch, a1) in on:
                    s, v, c7, pn = on.pop((ch, a1))
                    notes.append((s, t, ch, a1, v, c7, pn))
            elif hi == 0xB0:
                if a1 == 7:
                    cc7[ch] = a2
                elif a1 == 10:
                    pan[ch] = a2
        for (ch, n), (s, v, c7, pn) in on.items():     # notes never released end with the track
            notes.append((s, t, ch, n, v, c7, pn))
        i = end
    tempos.sort()
    return notes, tempos, div


def tick_to_seconds(tempos, div):
    pts = []
    sec, last_t, last_us = 0.0, 0, 500000
    for t, us in tempos:
        sec += (t - last_t) * last_us / 1e6 / div
        pts.append((t, sec, us))
        last_t, last_us = t, us

    def f(tick):
        k = 0
        for idx, (t, s, us) in enumerate(pts):
            if t <= tick:
                k = idx
        t, s, us = pts[k]
        return s + (tick - t) * us / 1e6 / div
    return f


# ------------------------------------------------------------------------------------------------ the build

def pitch_of(rate, root, note):
    return int(round(4096 * rate / 32000 * 2 ** ((note - root) / 12)))


def hexbyte(v, default):
    if v is None:
        return default
    return int(str(v), 16) if isinstance(v, str) else int(v)


def c_name(n):
    if not n.replace('_', 'a').isalnum() or not n[0].isalpha():
        raise SndError(f"name '{n}' must be letters, digits and _ (it names C symbols)")
    return n.lower()


def emit_wait(out, op, delta):
    """op ($80 rest, $83 wait, or a note's two bytes) with delta ticks, longer waits continued with $83."""
    first = min(delta, 255)
    out += op + bytes([first])
    delta -= first
    while delta > 0:
        k = min(delta, 255)
        out += bytes([0x83, k])
        delta -= k


def build_song(song, samples, root):
    who = c_name(song['name'])
    notes, tempos, div = read_midi(os.path.join(root, song['mid']))
    sec = tick_to_seconds(tempos, div)
    inst_map = {int(k): v for k, v in song.get('instruments', {}).items()}
    ignore = set(int(c) for c in song.get('ignore', []))
    volume = float(song.get('volume', 0.5))
    transpose = int(song.get('transpose', 0))
    events = []
    for s, e, ch, n, v, c7, pn in notes:
        if ch in ignore:
            continue
        if ch not in inst_map:
            raise SndError(f'{who}: MIDI channel {ch} has notes but no instrument (add it to "instruments", or to "ignore")')
        name = inst_map[ch]
        play = n + transpose
        if isinstance(name, dict):      # a drum kit: MIDI note -> sample, played at the sample's own pitch
            kit = name
            name = kit.get(str(n))
            if name is None:
                raise SndError(f'{who}: channel {ch} plays note {n}, which its kit does not name (kit notes: {sorted(kit)})')
            if name not in samples:
                raise SndError(f'{who}: channel {ch}\'s kit names "{name}", which is not a sample')
            play = samples[name]['root']
        if name not in samples:
            raise SndError(f'{who}: channel {ch}\'s instrument "{name}" is not a sample')
        a = int(round(sec(s) * TICK_RATE))
        b = max(a + 1, int(round(sec(e) * TICK_RATE)))
        amp = v / 127 * c7 / 127 * volume
        vol = min(127, int(round(127 * amp)))
        l = min(127, int(round(vol * min(1.0, (127 - pn) / 63.5))))
        r = min(127, int(round(vol * min(1.0, pn / 63.5))))
        events.append((a, b, name, play, l, r))
    if not events:
        raise SndError(f'{who}: the MIDI file has no notes')
    events.sort()
    length = max(b for a, b, *_ in events)
    if song.get('length') is not None:
        length = max(length, int(round(float(song['length']) * TICK_RATE)))
    # channels: the lowest one free when the note starts (the one that last played the same instrument first)
    busy = [0] * MAX_CH
    last_inst = [None] * MAX_CH
    per_ch = [[] for _ in range(MAX_CH)]
    for ev in events:
        a, b, name = ev[0], ev[1], ev[2]
        free = [c for c in range(MAX_CH) if busy[c] <= a]
        if not free:
            raise SndError(f'{who}: more than {MAX_CH} notes sound at {a / TICK_RATE:.2f} s (the driver has 24 channels)')
        same = [c for c in free if last_inst[c] == name]
        c = same[0] if same else free[0]
        busy[c] = b
        last_inst[c] = name
        per_ch[c].append(ev)
    used = [c for c in range(MAX_CH) if per_ch[c]]
    nch = max(used) + 1
    # pitch table
    ptab, pindex = [], {}
    for ev in events:
        key = (ev[2], ev[3])
        if key not in pindex:
            smp = samples[ev[2]]
            p = pitch_of(smp['rate'], smp['root'], ev[3])
            if p > 0x3FFF:
                raise SndError(f'{who}: note {ev[3]} on "{ev[2]}" needs pitch {p / 4096:.2f}x; the DSP plays up to 4x '
                               f'(raise the sample\'s rate or root, or transpose the song)')
            if len(ptab) >= 128:
                raise SndError(f'{who}: more than 128 distinct notes and instruments')
            pindex[key] = len(ptab)
            ptab.append(p)
    streams = []
    for c in range(nch):
        out = bytearray()
        timeline = []
        notes_c = per_ch[c]
        for k, (a, b, name, n, l, r) in enumerate(notes_c):
            timeline.append((a, 'on', (name, n, l, r)))
            nxt = notes_c[k + 1][0] if k + 1 < len(notes_c) else None
            if nxt is None or b < nxt:
                timeline.append((b, 'off', None))
        cur_inst, cur_vol = None, None
        first = timeline[0][0] if timeline else length
        if first > 0:
            emit_wait(out, bytes([0x83]), first)      # silence before the channel's first note
        for k, (t, op, data) in enumerate(timeline):
            nt = timeline[k + 1][0] if k + 1 < len(timeline) else length
            delta = max(1, nt - t) if k + 1 < len(timeline) else max(0, nt - t)
            if op == 'on':
                name, n, l, r = data
                if name != cur_inst:
                    out += bytes([0x81, samples[name]['index']])
                    cur_inst = name
                if (l, r) != cur_vol:
                    out += bytes([0x82, l, r])
                    cur_vol = (l, r)
                if delta == 0:
                    delta = 1
                emit_wait(out, bytes([pindex[(name, n)]]), delta)
            else:
                if delta == 0:
                    out += bytes([0x84])
                else:
                    emit_wait(out, bytes([0x80]), delta)
        out += bytes([0x85 if song.get('loop', True) else 0x86])
        streams.append(bytes(out))
    return dict(name=who, nch=nch, ptab=ptab, streams=streams, length=length, notes=len(events),
                peak=max(sum(1 for e in events if e[0] <= t < e[1]) for t in sorted(set(e[0] for e in events))))


def build(cfg_path, outdir, driver_path, snesbrr):
    cfg = json.load(open(cfg_path, encoding='utf-8'))
    root = os.path.dirname(os.path.abspath(cfg_path))
    snd = cfg.get('sound') or {}
    os.makedirs(outdir, exist_ok=True)
    driver = open(driver_path, 'rb').read()
    if len(driver) > DRIVER_MAX:
        raise SndError(f'the driver is {len(driver)} bytes; it has {DRIVER_MAX} at ${DRIVER:04X}')

    # ---- samples
    samples = {}
    low_brr, high = bytearray(), bytearray()
    for i, s in enumerate(snd.get('samples', [])):
        who = c_name(s['name'])
        if who in samples:
            raise SndError(f"two samples are called '{who}'")
        if i >= 256:
            raise SndError('more than 256 samples (the directory has 256 entries)')
        a, rate0 = read_wav(os.path.join(root, s['wav']))
        rate = int(s.get('rate', rate0))
        a = resample(a, rate0, rate)
        fmt = s.get('format', 'pcm16')
        loop = s.get('loop')
        rec = dict(index=i, name=who, rate=rate, root=int(s.get('root', 60)), format=fmt,
                   adsr1=hexbyte((s.get('adsr') or [None, None])[0], 0x8F), adsr2=hexbyte((s.get('adsr') or [None, None])[1], 0xE0),
                   gain=hexbyte(s.get('gain'), 0))
        if fmt == 'pcm16':
            data, lp = to_pcm16(a, loop)
            start = HIGH + len(high)
            if start + len(data) > RAM:
                raise SndError(f'{who}: the samples need more than the 1 MB of sound RAM')
            high += data
            rec.update(start=start, end=start + len(data), loop=(start + lp * 2) if lp is not None else start, loops=lp is not None,
                       bytes=len(data), frames=len(data) // 2)
        elif fmt == 'brr':
            data, lp = to_brr(a, rate, loop, snesbrr)
            if BRR_LOW + len(low_brr) + len(data) <= BRR_LOW_END:
                start = BRR_LOW + len(low_brr)
                low_brr += data
                rec['low'] = True
            else:
                start = HIGH + len(high)
                high += data
                rec['low'] = False
            rec.update(start=start, end=start + len(data), loop=(start + lp) if lp is not None else start, loops=lp is not None,
                       bytes=len(data), frames=len(data) // 9 * 16)
        else:
            raise SndError(f"{who}: format '{fmt}' must be pcm16 or brr")
        if rec['end'] > RAM:
            raise SndError(f'{who}: the samples need more than the 1 MB of sound RAM')
        samples[who] = rec

    # ---- songs and effects into the bank
    songs = [build_song(sg, samples, root) for sg in snd.get('songs', [])]
    effects = []
    next_brr_voice, next_wide_voice = 0, 31
    for e in snd.get('effects', []):
        who = c_name(e['name'])
        if e['sample'] not in samples:
            raise SndError(f"effect {who}: no sample '{e['sample']}'")
        smp = samples[e['sample']]
        voice = e.get('voice')
        if voice is None:
            if smp['format'] == 'brr' and smp.get('low'):
                voice = next_brr_voice
                next_brr_voice = (next_brr_voice + 1) % 8
            else:
                voice = next_wide_voice
                next_wide_voice = 31 if next_wide_voice <= 8 else next_wide_voice - 1
        voice = int(voice)
        if not 0 <= voice < 32:
            raise SndError(f'effect {who}: voice {voice} must be 0-31')
        if voice < 8 and not (smp['format'] == 'brr' and smp.get('low')):
            raise SndError(f"effect {who}: voice {voice} is one of the S-DSP's own, which play BRR samples below 64 KB only "
                           f"('{smp['name']}' is {smp['format']}{'' if smp['format'] == 'pcm16' else ' above 64 KB'}): use voice 8-31")
        p = pitch_of(smp['rate'], smp['root'], int(e.get('note', smp['root'])))
        if p > 0x3FFF:
            raise SndError(f'effect {who}: pitch {p / 4096:.2f}x is above 4x')
        vol = int(e.get('volume', 110))
        pn = int(e.get('pan', 64))
        l = min(127, int(round(vol * min(1.0, (127 - pn) / 63.5))))
        r = min(127, int(round(vol * min(1.0, pn / 63.5))))
        effects.append(dict(name=who, srcn=smp['index'], voice=voice, pitch=p, l=l, r=r, adsr1=smp['adsr1'], adsr2=smp['adsr2']))

    bank = bytearray(8)
    def at():
        return BANK + len(bank)
    song_tab_addr = at()
    bank += bytes(2 * len(songs))
    inst_addr = at()
    for name, smp in sorted(samples.items(), key=lambda kv: kv[1]['index']):
        bank += bytes([smp['index'], smp['adsr1'], smp['adsr2'], smp['gain']])
    eff_addr = at()
    for e in effects:
        bank += bytes([e['srcn'], e['voice'], e['pitch'] & 0xFF, e['pitch'] >> 8, e['l'], e['r'], e['adsr1'], e['adsr2']])
    for k, sg in enumerate(songs):
        ptab_addr = at()
        for p in sg['ptab']:
            bank += struct.pack('<H', p)
        stream_addrs = []
        for st in sg['streams']:
            stream_addrs.append(at())
            bank += st
        header = at()
        bank += bytes([sg['nch'], TICK_DIVIDER]) + struct.pack('<H', ptab_addr)
        for sa in stream_addrs:
            bank += struct.pack('<HH', sa, sa)
        struct.pack_into('<H', bank, song_tab_addr - BANK + 2 * k, header)
        sg['header'] = header
    struct.pack_into('<BBHHH', bank, 0, len(songs), len(effects), song_tab_addr, eff_addr, inst_addr)
    if BANK + len(bank) > BANK_END:
        raise SndError(f'the songs and effects take {len(bank):,} bytes; the bank has {BANK_END - BANK:,} (${BANK:04X}-${BANK_END - 1:04X})')

    # ---- directories
    snes_dir = bytearray()
    for i in range(64):
        smp = next((s for s in samples.values() if s['index'] == i and s['format'] == 'brr' and s.get('low')), None)
        if smp:
            snes_dir += struct.pack('<HH', smp['start'], smp['loop'])
        else:
            snes_dir += struct.pack('<HH', SILENT, SILENT)
    silent = bytes([0x01] + [0] * 8)      # a BRR block: end, no loop
    wide_dir = bytearray(16 * 256)
    for smp in samples.values():
        o = 16 * smp['index']
        fmt = (1 if smp['format'] == 'pcm16' else 0) | (2 if smp['format'] == 'pcm16' and smp['loops'] else 0)
        wide_dir[o:o + 3] = smp['start'].to_bytes(3, 'little')
        wide_dir[o + 3] = fmt
        wide_dir[o + 4:o + 7] = smp['loop'].to_bytes(3, 'little')
        wide_dir[o + 8:o + 11] = smp['end'].to_bytes(3, 'little')
    for i in range(256):
        if not any(s['index'] == i for s in samples.values()):
            o = 16 * i
            wide_dir[o:o + 3] = SILENT.to_bytes(3, 'little')
            wide_dir[o + 4:o + 7] = SILENT.to_bytes(3, 'little')

    # ---- the uploads (kind 6: sound RAM), in pieces of at most 32 KB
    blobs, uploads = [], []

    def add(sym, data, dest):
        for i in range(0, len(data), PIECE):
            part = bytes(data[i:i + PIECE])
            s = f'{sym}_{i // PIECE}'
            blobs.append((s, part))
            uploads.append((dest + i, s, len(part)))
    add('snd_driver', driver, DRIVER)
    add('snd_bank', bank, BANK)
    add('snd_snesdir', snes_dir, SNES_DIR)
    if low_brr:
        add('snd_brr', low_brr, BRR_LOW)
    add('snd_silent', silent, SILENT)
    add('snd_widedir', wide_dir, WIDE_DIR)
    if high:
        add('snd_samples', high, HIGH)
    for sym, data in blobs:
        open(os.path.join(outdir, sym + '.bin'), 'wb').write(data)

    asm = ['; snd.asm: written by wmssnd.py from ' + os.path.basename(cfg_path) + '; do not edit.', '.include "hdr.asm"', '']
    for sym, data in blobs:
        asm += [f'.SECTION ".{sym}" SUPERFREE', f'{sym}:', f'.INCBIN "gen/{sym}.bin"', '.ENDS', '']
    asm += ['.SECTION ".snd_uploads" SUPERFREE', 'snd_uploads:']
    for dest, sym, size in uploads:
        asm += ['  .db 6, 0', f'  .dl ${dest:06X}', '  .db 0', f'  .dl {sym}', '  .db 0', f'  .dw {size}']
    asm += ['  .db 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0', '.ENDS', '']
    open(os.path.join(outdir, 'snd.asm'), 'w', newline='\n').write('\n'.join(asm))

    h = ['/* snd.h: written by wmssnd.py from ' + os.path.basename(cfg_path) + '; do not edit. */', '#ifndef SND_H', '#define SND_H',
         '#include "wmssound.h"', '', '/* the sound RAM image: the driver, the songs, the directories and every sample */',
         'extern u8 snd_uploads[];', '#define sndInit() wmsSoundStart(snd_uploads)', '']
    for k, sg in enumerate(songs):
        h.append(f"#define SND_SONG_{sg['name'].upper()} {k}   /* {sg['nch']} channels, {sg['notes']} notes, "
                 f"{sg['length'] / TICK_RATE:.1f} s, up to {sg['peak']} at once */")
    for k, e in enumerate(effects):
        h.append(f"#define SND_SFX_{e['name'].upper()} {k}   /* voice {e['voice']} */")
    for smp in samples.values():
        h.append(f"#define SND_SAMPLE_{smp['name'].upper()} {smp['index']}   /* {smp['format']}, {smp['rate']} Hz, {smp['bytes']:,} bytes at ${smp['start']:05X} */")
    h += ['', '#endif', '']
    open(os.path.join(outdir, 'snd.h'), 'w', newline='\n').write('\n'.join(h))

    report = dict(samples=list(samples.values()), songs=[{k: v for k, v in sg.items() if k not in ('streams', 'ptab')} for sg in songs],
                  effects=effects, bankBytes=len(bank), brrLowBytes=len(low_brr), highBytes=len(high),
                  soundRamEnd=HIGH + len(high), rom=sum(len(d) for _, d in blobs))
    json.dump(report, open(os.path.join(outdir, 'snd.json'), 'w'), indent=1)
    song_list = ', '.join('%s: %d channels' % (sg['name'], sg['nch']) for sg in songs)
    print(f"wmssnd: {len(samples)} samples ({len(low_brr):,} bytes of BRR below 64 KB, {len(high):,} above), {len(songs)} songs "
          f"({song_list}), {len(effects)} effects, bank {len(bank):,} bytes, sound RAM to ${HIGH + len(high):05X}")


def opt(argv, name, default=None):
    return argv[argv.index(name) + 1] if name in argv else default


def main(argv):
    if len(argv) < 2 or argv[0] not in ('build', 'pcm', 'brr', 'midi'):
        print(__doc__)
        return 2
    try:
        if argv[0] == 'build':
            drv = opt(argv, '--driver')
            if not drv:
                raise SndError('build needs --driver <wmsdrv.bin> (wmsbuild.ps1 assembles it)')
            build(argv[1], argv[2], drv, opt(argv, '--snesbrr'))
        elif argv[0] in ('pcm', 'brr'):
            a, rate0 = read_wav(argv[1])
            rate = int(opt(argv, '--rate', rate0))
            a = resample(a, rate0, rate)
            loop = opt(argv, '--loop')
            loop = int(loop) if loop is not None else None
            if argv[0] == 'pcm':
                data, lp = to_pcm16(a, loop)
            else:
                data, lp = to_brr(a, rate, loop, opt(argv, '--snesbrr'))
            open(argv[2], 'wb').write(data)
            print(f'wmssnd: {argv[1]} -> {argv[2]}: {len(data):,} bytes ({argv[0]}, {rate} Hz' + (f', loop at {lp}' if lp is not None else '') + ')')
        else:
            notes, tempos, div = read_midi(argv[1])
            sec = tick_to_seconds(tempos, div)
            chans = sorted(set(n[2] for n in notes))
            starts = sorted(set(n[0] for n in notes))
            peak = max((sum(1 for n in notes if n[0] <= t < n[1]) for t in starts), default=0)
            print(f'{argv[1]}: {len(notes)} notes on MIDI channels {chans}, {sec(max((n[1] for n in notes), default=0)):.1f} s, '
                  f'at most {peak} at once, {len(tempos) - 1} tempo changes')
    except (SndError, OSError, KeyError, ValueError, wave.Error) as e:
        print(f'wmssnd: ERROR: {e}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
