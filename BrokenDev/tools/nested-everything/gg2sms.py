"""Nested Everything: Game Gear -> Master System.

    python gg2sms.py convert <in.gg> <out.sms> [--profile p.json] [--sites trace.json ...] [--set key=value ...] [--report r.json]
    python gg2sms.py trace <in.gg|out.sms> <trace.json> [--console gg|sms] [--frames N] [--input spec] [--workshop exe]
    python gg2sms.py feedback <in.gg> <out.sms> [--profile p.json] [--rounds N] ...   (trace the original, convert, trace the result, repeat)
    python gg2sms.py manifest

What the converter does (docs/projects/nested-everything-research.md, section 1):
  1. Static pass: every OUT (n),A / IN A,(n) to the GG-only ports $00-$06 and the VDP ports $BE/$BF in code it can prove
     (a recursive descent from the vectors and from every instruction the dynamic pass saw run).
  2. Dynamic pass (Workshop --sms-run --gg-sites, BrokenNes's own GG core): the sites the static pass cannot see (OUT (C),r,
     OTIR/OUTI loops, code it did not reach), which data-port sites wrote colour RAM, the jump targets, the RAM the game never changes.
  3. Patches: link/stereo port writes become NOPs, link reads become LD A,<idle value> (same length, no runtime); Start/region
     reads, colour writes, control writes and the C-port forms jump to trampolines that call the runtime (gg2sms_runtime.py) placed
     in free space of the always-mapped bank 0. Pause (NMI at $0066) gives Start. The header becomes an SMS export header with a
     fresh checksum.
Exit codes: 0 converted, 1 converted with unhandled sites (see the report), 2 usage, 3 cannot convert.
"""
import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import z80                      # noqa: E402
import gg2sms_runtime as rt     # noqa: E402

PIPELINE = os.path.join(HERE, '..', '..', 'projects', 'nested-gg2sms', 'pipeline.json')


def load_manifest():
    with open(PIPELINE, encoding='utf-8') as f:
        return json.load(f)


def default_settings():
    return {s['id']: s['default'] for s in load_manifest()['settings']}


# ============================================================================================== ROM model

class Rom:
    def __init__(self, data):
        self.b = bytearray(data)
        self.n = len(data)

    def header(self):
        for off in (0x7FF0, 0x3FF0, 0x1FF0):
            if off + 16 <= self.n and self.b[off:off + 8] == b'TMR SEGA':
                return off
        return None


def off_of(addr, from_off, from_slot, fixed1):
    """ROM offset of CPU address addr reached from code at from_off running in slot from_slot (None: unknown bank)."""
    if addr >= 0xC000:
        return None
    slot = addr >> 14
    if slot == 0:
        return addr
    if slot == 1 and fixed1:
        return addr
    if slot == from_slot and from_off is not None:
        return (from_off & ~0x3FFF) | (addr & 0x3FFF)
    return None


def cpu_of(off, slot):
    return slot << 14 | (off & 0x3FFF)


# ============================================================================================== analysis

class Analysis:
    def __init__(self, rom, traces):
        self.rom = rom
        self.code = {}             # instruction start offset -> slot
        self.targets = set()       # offsets something jumps to
        self.sites = []            # dynamic sites (merged)
        self.ram_changed = set()
        self.slot_pages = set()
        self.traced = bool(traces)
        for t in traces:
            for off, slot in t['code']:
                self.code[off] = slot
            self.targets.update(t['targets'])
            self.ram_changed.update(t['ramChanged'])
            self.slot_pages.update(t['slotPages'])
            self.sites += t['sites']
        self.read = set()          # ROM bytes the traced runs read as data
        for t in traces:
            for a0, a1 in t.get('dataRead', []):
                self.read.update(range(a0, a1))
        self.dyn_code = dict(self.code)
        pages1 = {p.split(',')[1] for p in self.slot_pages}
        pages0 = {p.split(',')[0] for p in self.slot_pages}
        self.fixed0 = pages0 <= {'0'}
        self.fixed1 = pages1 <= {'1'} if self.slot_pages else (rom.n <= 0x8000)
        self.descend()

    def descend(self):
        b = self.rom.b
        work = [(v, 0) for v in (0x0000, 0x0038)]
        work += [(o, s) for o, s in self.dyn_code.items()]
        seen = set()
        while work:
            off, slot = work.pop()
            while off is not None and 0 <= off < self.rom.n and (off, slot) not in seen:
                seen.add((off, slot))
                if off not in self.code:
                    self.code[off] = slot
                n = z80.length(b, off)
                if off + n > self.rom.n:
                    break
                pc = cpu_of(off, slot)
                falls, tg, kind = z80.flow(b, off, pc)
                for t in tg:
                    to = off_of(t, off, slot, self.fixed1)
                    if to is not None:
                        self.targets.add(to)
                        work.append((to, t >> 14 if t < 0xC000 else slot))
                if not falls:
                    break
                off += n

    def ram_slot(self, need, prefer):
        if prefer is not None:
            return prefer
        def free(a):
            return all(x not in self.ram_changed for x in range(a, a + need))
        for a in range(0xDFF0, 0xDFFC - need + 1):          # between the usual stack top and the mapper registers
            if free(a):
                return a
        for a in range(0xDFEF - need, 0xC000, -1):
            if free(a):
                return a
        return None


# ============================================================================================== patch planning

GG_PORTS = range(0x00, 0x07)
SIMPLE = {0x06, 0x0E, 0x16, 0x1E, 0x26, 0x2E, 0x3E, 0x01, 0x11, 0x21, 0xAF, 0x3A, 0x2A} | set(range(0x78, 0x80)) | {0x04, 0x05, 0x0C, 0x0D, 0x14, 0x15, 0x1C, 0x1D, 0x24, 0x25, 0x2C, 0x2D, 0x3C, 0x3D, 0x23, 0x2B, 0x13, 0x1B, 0x03, 0x0B}


def classify_static(rom, an):
    """Every OUT (n),A / IN A,(n) instruction start in proven code that touches a GG port or the VDP data/control port."""
    out = []
    b = rom.b
    for off, slot in an.code.items():
        if off + 1 >= rom.n:
            continue
        op, p = b[off], b[off + 1]
        if op == 0xD3 and (p in GG_PORTS or 0x80 <= p < 0xC0):
            out.append({'off': off, 'slot': slot, 'form': 'out_n', 'port': p})
        elif op == 0xDB and (p in GG_PORTS or 0x80 <= p < 0xC0 and p & 1):
            out.append({'off': off, 'slot': slot, 'form': 'in_n', 'port': p})
    return out


OC_REG = {0x79: 'a', 0x41: 'b', 0x49: 'c', 0x51: 'd', 0x59: 'e', 0x61: 'h', 0x69: 'l', 0x71: '0'}
BLOCK = {0xA3: 'h_outi', 0xB3: 'h_otir', 0xAB: 'h_outd', 0xBB: 'h_otdr'}


def slot_of(an, o):
    return an.code.get(o, 0)


def plan(rom, an, settings, profile):
    """Decide what to do with every site. Returns (actions, report entries)."""
    dyn = {}
    for s in an.sites:
        if s['off'] < 0:
            continue
        d = dyn.setdefault(s['off'], {'ports': set(), 'cram': 0, 'vram': 0, 'count': 0, 'write': s['write']})
        d['ports'].add(s['port']); d['cram'] += s['cram']; d['vram'] += s['vram']; d['count'] += s['count']
    ram_sites = sorted({(s['pc'], s['port']) for s in an.sites if s['off'] < 0 and (s['port'] in GG_PORTS or s['cram'])})
    static = {s['off']: s for s in classify_static(rom, an)}
    b = rom.b
    actions = {}
    unhandled = []
    forced = {int(k, 0): v for k, v in profile.get('sites', {}).items()}
    for off in sorted(set(static) | set(dyn)):
        if off in forced and forced[off] == 'leave':
            continue
        slot = an.code.get(off, static.get(off, {}).get('slot', 0))
        op = b[off]
        d = dyn.get(off)
        if op in (0xD3, 0xDB):
            p = b[off + 1]
            if op == 0xD3 and p in GG_PORTS:
                actions[off] = ('bytes', bytes([0x00, 0x00]), f'OUT (${p:02X}),A -> NOP NOP')
            elif op == 0xDB and p in GG_PORTS and p != 0:
                actions[off] = ('bytes', bytes([0x3E, rt.IDLE[p]]), f'IN A,(${p:02X}) -> LD A,${rt.IDLE[p]:02X}')
            elif op == 0xDB and p == 0:
                if settings['start'] == 'none':
                    base = 0xC0 if settings['region'] == 'export' else 0x80
                    actions[off] = ('bytes', bytes([0x3E, base]), 'IN A,($00) -> LD A,base (no Start)')
                else:
                    actions[off] = ('call', 'h_p0', 'IN A,($00) -> h_p0', slot)
            elif op == 0xDB and 0x80 <= p < 0xC0 and p & 1:
                if settings.get('latch') == 'status':
                    actions[off] = ('call', 'h_sr', f'IN A,(${p:02X}) -> h_sr (control latch)', slot)
            elif op == 0xD3 and 0x80 <= p < 0xC0 and p & 1:
                actions[off] = ('call', 'h_bf', f'OUT (${p:02X}),A -> h_bf', slot)
            elif op == 0xD3 and 0x80 <= p < 0xC0:
                cram = d['cram'] if d else 0
                if forced.get(off) == 'patch' or cram or (d is None and settings['unseen_data_writes'] == 'patch') \
                        or (d is not None and not cram and settings['vram_only_data_writes'] == 'patch'):
                    actions[off] = ('call', 'h_be', f'OUT (${p:02X}),A -> h_be ({"colour RAM seen" if cram else "not seen writing colour RAM" if d else "never ran"})', slot)
        elif op == 0xED and d is not None:
            o2 = b[off + 1]
            needs = any(p in GG_PORTS or p == 0xBF for p in d['ports']) or d['cram'] or settings['vram_only_data_writes'] == 'patch'
            if not needs:
                continue
            if o2 in OC_REG:
                actions[off] = ('call', 'oc_' + OC_REG[o2], f'OUT (C),{OC_REG[o2]} -> dispatch (ports {sorted(d["ports"])})', slot)
            elif o2 in BLOCK:
                actions[off] = ('call', BLOCK[o2], f'{BLOCK[o2][2:].upper()} -> dispatch (ports {sorted(d["ports"])})', slot)
            elif o2 == 0x78:
                actions[off] = ('call', 'h_inca', 'IN A,(C) -> h_inca', slot)
            else:
                unhandled.append({'off': off, 'why': f'ED {o2:02X} on GG ports {sorted(d["ports"])}: no handler'})
        elif d is not None:
            unhandled.append({'off': off, 'why': f'opcode {op:02X} at a GG port site'})
    chain = sorted(o for o, a in actions.items() if a[0] == 'call' and a[1] in BLOCK.values())
    for o in chain:                        # forward only, and at most 64 from the end (colour RAM is 64 bytes): the VRAM part stays fast
        for step in (2,):
            q = o + step
            while 0 <= q < rom.n - 1 and q not in actions and b[q] == 0xED and b[q + 1] == b[o + 1] and (q - o) < 128:
                actions[q] = ('call', BLOCK[b[q + 1]], f'{BLOCK[b[q + 1]][2:].upper()} -> dispatch (unrolled chain with a colour-RAM site)', slot_of(an, o))
                an.code.setdefault(q, an.code.get(o, 0))
                q += step
    ram_bytes = {(s['pc'], s['port']): s['bytes'] for s in an.sites if s['off'] < 0}
    deltas = set()                          # copy offsets (ROM source - RAM address) the unique matches prove
    hits_of = {}
    for pc, port in ram_sites:
        pat = bytes.fromhex(ram_bytes.get((pc, port), ''))
        if port in GG_PORTS and port != 0 and len(pat) == 3 and pat[0] == 0xD3:
            hits_of[pc] = [i for i in range(rom.n - 2) if b[i:i + 3] == pat and i not in an.code]
            if len(hits_of[pc]) == 1:
                deltas.add(hits_of[pc][0] - pc)
    # copy offsets voted by every RAM site whose 3 traced bytes occur in the ROM (code copied whole: one offset wins)
    votes, bb = {}, bytes(b)
    for (pc, port), hexb in ram_bytes.items():
        pat = bytes.fromhex(hexb)
        if len(pat) == 3:
            i = bb.find(pat)
            while i >= 0:
                if i not in an.code:
                    votes[i - pc] = votes.get(i - pc, 0) + 1
                i = bb.find(pat, i + 1)
    ram_srcs = set()
    for dl, nv in votes.items():
        if nv >= 3:
            deltas.add(dl)
    # VDP writes in code copied to RAM: the ROM source gets the same 2-byte RST + index as a ROM site (the index rides in the copy)
    ram_vdp = sorted({(s['pc'], s['port']) for s in an.sites if s['off'] < 0 and s['write'] and 0x80 <= s['port'] < 0xC0})
    cram_ram = any(s['cram'] for s in an.sites if s['off'] < 0)
    handled = set()
    for pc, port in ram_vdp if cram_ram else []:
        pat = bytes.fromhex(ram_bytes.get((pc, port), ''))
        h = None
        if len(pat) >= 2 and pat[0] == 0xD3 and 0x80 <= pat[1] < 0xC0:
            h = 'h_bf' if pat[1] & 1 else 'h_be'
        elif len(pat) >= 2 and pat[0] == 0xED and pat[1] in BLOCK:
            h = BLOCK[pat[1]]
        elif len(pat) >= 2 and pat[0] == 0xED and pat[1] in OC_REG:
            h = 'oc_' + OC_REG[pat[1]]
        if h is None:
            continue
        hits = [x for x in (pc + dl for dl in deltas) if 0 <= x < rom.n - 2 and b[x:x + len(pat)] == pat]
        if len(hits) == 1:
            actions[hits[0]] = ('call', h, f'{h} for code copied to RAM ${pc:04X} (in the ROM source)', 0)
            ram_srcs.add(hits[0]); handled.add(pc)
    for pc, port in ram_sites:
        if pc in handled:
            continue
        # code the game copied to RAM: a link / stereo write is patched in the ROM bytes it was copied from, when they are unique
        pat = bytes.fromhex(ram_bytes.get((pc, port), ''))
        if port in GG_PORTS and port != 0 and len(pat) == 3 and pat[0] == 0xD3:
            hits = hits_of.get(pc, [])
            if len(hits) > 1:
                hits = [h for h in hits if h - pc in deltas]
            if len(hits) == 1:
                actions[hits[0]] = ('bytes', bytes([0x00, 0x00]), f'OUT (${port:02X}),A copied to RAM ${pc:04X} -> NOP NOP (in the ROM source)')
                continue
        unhandled.append({'off': None, 'pc': pc, 'why': f'code in RAM touches port ${port:02X} (cannot be patched in ROM)'})
    an.ram_srcs = ram_srcs
    an.ram_handled = handled
    return actions, unhandled


# ============================================================================================== trampolines

def cover(rom, an, off, plen, protected):
    """The [start, end) byte range of whole instructions around off that a 3-byte JP can replace, or None.
    Instructions after the first may not be jump targets; nothing in the range may be another patched site or protected."""
    b = rom.b
    # forward: start at the site
    end = off + plen
    ok = True
    while end - off < 3:
        if end in an.targets or end not in an.code or end in protected:
            ok = False
            break
        end += z80.length(b, end)
    if ok:
        return off, end
    # backward: whole instructions ending exactly at the site, the site itself not a target
    if off in an.targets:
        return None
    for back in range(1, 8):
        st = off - back
        if st in an.code and st not in protected:
            # walk forward from st through known instruction starts to the site
            q, good = st, True
            while q < off:
                if q != st and (q in an.targets or q not in an.code or q in protected):
                    good = False
                    break
                q += z80.length(b, q)
            if good and q == off and off + plen - st >= 3:
                return st, off + plen
    return None


def relocate(b, i, n, pc_old, asm):
    """Copy the instruction at b[i] (n bytes, executed at pc_old) into asm, fixing PC-relative branches."""
    op = b[i]
    falls, tg, kind = z80.flow(b, i, pc_old)
    if kind == 'jr':
        t = tg[0]
        if op == 0x18:
            asm.op_w(0xC3, t)
        else:
            asm.op_w({0x20: 0xC2, 0x28: 0xCA, 0x30: 0xD2, 0x38: 0xDA}[op], t)
    elif kind == 'djnz':
        asm.db(0x10, 0x03); asm.op_w(0xC3, 0); jp_fall = asm.here() - 2  # djnz +3 / jp <after> / jp <target>
        asm.op_w(0xC3, tg[0])
        return jp_fall
    else:
        asm.db(b[i:i + n])
    return None


# ============================================================================================== convert

def fix_header(rom, report):
    h = rom.header()
    if h is None:
        report['header'] = 'no TMR SEGA header: left alone (an SMS BIOS will not boot it; flash carts and emulators do)'
        return
    size_nib = rom.b[h + 15] & 0x0F
    old_region = rom.b[h + 15] >> 4
    rom.b[h + 15] = 0x40 | size_nib
    sizes = {0xA: 0x1FF0, 0xB: 0x3FF0, 0xC: 0x7FF0, 0xD: 0xBFF0, 0xE: 0xFFFF, 0xF: 0x1FFFF, 0x0: 0x3FFFF, 0x1: 0x7FFFF, 0x2: 0xFFFFF}
    last = min(sizes.get(size_nib, 0x7FF0), rom.n - 1)
    s = 0
    for i in range(0, last + 1):
        if 0x7FF0 <= i <= 0x7FFF:
            continue
        s += rom.b[i]
    s &= 0xFFFF
    rom.b[h + 10] = s & 0xFF; rom.b[h + 11] = s >> 8
    report['header'] = f'region {old_region} -> 4 (SMS export), checksum ${s:04X} over $0000-${last:X}'


def free_runs(rom, an, lo, hi, protected, margin=2):
    """Runs of filler bytes ($FF, then $00) in [lo, hi) that nothing runs or jumps into: [(size, start, fill)], biggest first."""
    b = rom.b
    out = []
    hi = min(hi, rom.n)
    i = lo
    while i < hi:
        if b[i] in (0xFF, 0x00):
            fill, j = b[i], i
            while j < hi and b[j] == fill and j not in an.code and j not in an.targets and j not in protected and j not in an.read:
                j += 1
            st, en = i + margin, j - margin
            if en - st >= 16:
                out.append((en - st, st, fill))
            i = max(j, i + 1)
        else:
            i += 1
    out.sort(key=lambda r: (r[2] != 0xFF, -r[0]))     # $FF padding first, then the biggest
    return out


def free_rsts(rom, an):
    """RST vectors ($08-$30) the game never uses: no RST to them, nothing runs or jumps into their first 3 bytes."""
    b = rom.b
    used = {b[o] & 0x38 for o in an.code if o < rom.n and b[o] & 0xC7 == 0xC7}
    out = []
    for v in range(0x08, 0x38, 8):
        if v in used:
            continue
        if any(x in an.code or x in an.targets for x in range(v, v + 3)):
            continue
        if any(b[x] not in (0xFF, 0x00) for x in range(v, v + 3)) or len({b[x] for x in range(v, v + 3)}) > 1:
            continue                       # code nobody was seen running: an RST the traces did not reach may still enter it
        out.append(v)
    return out


def rst_calls(rom, an, v):
    """Every RST v instruction a wider descent finds: from all known code, and carrying on after RST instructions (the main descent
    stops there, in case inline data follows). Only RST v's offsets are kept, so a wrong guess costs a spare table entry, never a patch."""
    b, op = rom.b, 0xC7 | v
    found = {}
    seen = set()
    work = list(an.code.items())
    while work:
        off, slot = work.pop()
        steps = 0
        while 0 <= off < rom.n and (off, slot) not in seen and steps < 4096:
            seen.add((off, slot)); steps += 1
            n = z80.length(b, off)
            if off + n > rom.n:
                break
            if b[off] == op:
                found[off] = slot
            falls, tg, kind = z80.flow(b, off, cpu_of(off, slot))
            for t in tg:
                to = off_of(t, off, slot, an.fixed1)
                if to is not None and kind != 'rst':
                    work.append((to, t >> 14 if t < 0xC000 else slot))
            if kind == 'rst':
                falls = True
            if not falls:
                break
            off += n
    return found


def pick_hook(rom, an, traces, protected, forced=None):
    """A used RST vector to share with the game: (vector, cover start, cover end, times entered in the traces), the least entered first
    ($38, the interrupt, last). Its first whole instructions (>= 3 bytes, no jump target or patched site among them) move into the runtime."""
    runs = [0] * 0x40
    for t in traces:
        for i, c in enumerate(t.get('vectorRuns', [])[:0x40]):
            runs[i] += c
    calls = {v: len(rst_calls(rom, an, v)) for v in range(0x08, 0x38, 8)}     # each known call is a table entry every site scans past
    cands = [forced] if forced is not None else sorted(range(0x08, 0x38, 8), key=lambda v: (calls[v], runs[v]))
    for v in cands:
        if v in protected or (v not in an.code and v not in an.targets):
            continue                       # never entered: free_rsts would have offered it unless its bytes run as other code
        c = cover(rom, an, v, z80.length(rom.b, v), protected - set(range(v, v + z80.length(rom.b, v))))
        if c is None or c[0] != v:
            continue
        q, ok = v, True
        while q < c[1]:
            kind = z80.flow(rom.b, q, q)[2]
            if kind in ('rst', 'ind') and q + z80.length(rom.b, q) < c[1]:
                ok = False                 # nothing after an RST / JP (HL) may be moved: it may be inline data or never fall through
            q += z80.length(rom.b, q)
        if ok:
            return v, c[0], c[1], runs[v]
    return None


_PTR3 = {0x01, 0x11, 0x21, 0x31, 0x22, 0x2A, 0x32, 0x3A, 0xC2, 0xC3, 0xCA, 0xD2, 0xDA, 0xE2, 0xEA, 0xF2, 0xFA,
         0xC4, 0xCC, 0xCD, 0xD4, 0xDC, 0xE4, 0xEC, 0xF4, 0xFC}


def unread_runs(rom, an, traces, lo, hi, protected, skip, margin=8, least=16):
    """[(size, start)] in [lo, hi): bytes no traced run executed or read as data, outside every instruction the static pass proved and
    every padding run already offered, and with no 16-bit immediate of the game's code pointing into them. Biggest first."""
    if not any(t.get('dataRead') is not None for t in traces):
        return []
    b = rom.b
    used = bytearray(rom.n)
    for o in an.code:
        for x in range(o, min(rom.n, o + z80.length(b, o))):
            used[x] = 1
    for t in traces:
        for a0, a1 in t.get('dataRead', []):
            used[a0:a1] = b'\x01' * (a1 - a0)
    for x in list(an.targets) + list(protected):
        if 0 <= x < rom.n:
            used[x] = 1
    for s, e in skip:
        used[s:e] = b'\x01' * (e - s)
    ptr = set()
    for o in an.code:
        op = b[o] if o < rom.n else 0
        if op in _PTR3 and o + 2 < rom.n:
            ptr.add(b[o + 1] | b[o + 2] << 8)
        elif op in (0xDD, 0xFD, 0xED) and o + 3 < rom.n and (b[o + 1] in (0x21, 0x22, 0x2A) or op == 0xED and b[o + 1] in z80._ED4):
            ptr.add(b[o + 2] | b[o + 3] << 8)
    out = []
    i = lo
    hi = min(hi, rom.n)
    while i < hi:
        if used[i]:
            i += 1
            continue
        j = i
        while j < hi and not used[j]:
            j += 1
        st, en = i + margin, j - margin
        if en - st >= least and not any(i <= p < j for p in ptr):
            out.append((en - st, st))
        i = j
    out.sort(key=lambda r: -r[0])
    return out


def place(pieces, runs):
    """[(base, run index)] per piece, or None: an exact search (biggest piece first) when there are few pieces, else first fit."""
    order = sorted(range(len(pieces)), key=lambda i: -len(pieces[i].code))
    left = [[r[0], r[1]] for r in runs]
    spots = [None] * len(pieces)
    budget = [20000]

    def dfs(k):
        if k == len(order):
            return True
        budget[0] -= 1
        if budget[0] < 0:
            return False
        i = order[k]; n = len(pieces[i].code)
        tried = set()
        for r, (sz, st) in enumerate(left):
            if sz < n or sz in tried:
                continue
            tried.add(sz)                  # runs with the same room left are interchangeable
            spots[i] = (st, r); left[r] = [sz - n, st + n]
            if dfs(k + 1):
                return True
            left[r] = [sz, st]
        return False
    if dfs(0):
        return spots
    return None


def convert(data, settings, profile, traces):
    """JP trampolines when every site can have one (no lookup, nothing else slowed down); a hooked RST only when they cannot."""
    if profile.get('hook') or profile.get('rst'):
        return convert_once(data, settings, profile, traces, True)
    out, report, code = convert_once(data, settings, profile, traces, False)
    if out is not None and not any('no room for a jump' in u.get('why', '') for u in report['unhandled']):
        return out, report, code
    out2, report2, code2 = convert_once(data, settings, profile, traces, True)
    if out2 is None and out is not None:
        return out, report, code
    return out2, report2, code2


def convert_once(data, settings, profile, traces, allow_hook):
    rom = Rom(data)
    report = {'settings': settings, 'patches': [], 'unhandled': [], 'warnings': []}
    h = rom.header()
    region = rom.b[h + 15] >> 4 if h is not None else None
    gg_io = any(s['port'] in GG_PORTS or s['cram'] for t in traces for s in t['sites'])
    if region in (3, 4) and (not traces or not gg_io):
        report['verdict'] = 'already an SMS game (header region %d%s): copied unchanged' % (region, ', no GG port use seen' if traces else '')
        return bytes(rom.b), report, 0
    if region in (3, 4):
        report['warnings'].append(f'the header says SMS (region {region}) but the game uses GG ports: converted anyway')
    if traces:                             # by trace, not header: which GG-only ports and colour writes the game really made
        report['ggPortsSeen'] = sorted({s['port'] for t in traces for s in t['sites'] if s['port'] in GG_PORTS})
        report['cramWritesSeen'] = sum(s['cram'] for t in traces for s in t['sites'])
        if not report['ggPortsSeen'] and not report['cramWritesSeen']:
            report['verdict'] = 'already an SMS game by trace (no GG-only port use, no colour RAM writes): copied unchanged'
            return bytes(rom.b), report, 0
        if not report['ggPortsSeen']:
            report['warnings'].append('no GG-only port use seen: only the colours need converting (the game may be an SMS game in a GG cartridge)')
    if settings['region'] == 'auto':
        settings = dict(settings, region='japan' if region == 5 else 'export')
        report['settings'] = settings
    an = Analysis(rom, traces)
    if not traces:
        report['warnings'].append('no dynamic trace: only statically proven sites are patched (run "feedback" for the rest)')
    if not an.fixed0:
        report['verdict'] = 'the game maps other banks into slot 0: the runtime has nowhere safe to live'
        return None, report, 3
    if SmsCodemasters(rom):
        report['warnings'].append('Codemasters mapper: the far layout (slot-2 borrowing through $FFFF) is not available')
    nvars = len(rt.vars_list(settings))
    ram = an.ram_slot(nvars, int(profile['ram'], 0) if profile.get('ram') else None)
    if ram is None:
        report['verdict'] = 'no RAM the game leaves alone for the runtime (set "ram" in the profile)'
        return None, report, 3
    report['ram'] = f'${ram:04X}-${ram + nvars - 1:04X}'
    actions, unhandled = plan(rom, an, settings, profile)
    report['unhandled'] += unhandled
    calls = sorted(o for o, a in actions.items() if a[0] == 'call')
    protected = set()
    for o in actions:
        protected.update(range(o, o + z80.length(rom.b, o)))

    # ---- how the sites reach the runtime: an RST vector + an index byte in place (2 bytes for 2). A vector the game never uses, or
    # one it uses, hooked: its first instructions move into the runtime, and the sites behind it are told apart by their return address.
    rsts = free_rsts(rom, an)
    forced_rst = profile.get('rst')
    if forced_rst is not None:
        rsts = [int(forced_rst, 0)] if forced_rst != 'none' else []
    if profile.get('hook'):
        rsts = []                          # test the hooked path even when a vector is free
    rst = rsts[0] if rsts else None
    hook = None
    if rst is None and forced_rst != 'none' and calls and allow_hook:
        hook = pick_hook(rom, an, traces, protected, int(profile['hook'], 0) if profile.get('hook') else None)
        if hook:
            rst = hook[0]
            report['hook'] = f'RST ${hook[0]:02X} (the game\'s, entered {hook[3]} times in the traces): ${hook[1]:04X}-${hook[2] - 1:04X} moved into the runtime'
    protected_v = set(range(rst, rst + 3)) if rst is not None and not hook else set(range(hook[1], hook[2])) if hook else set()
    nmi_ok = settings['start'] in ('pause', 'both') and all(x not in an.code and x not in an.targets for x in range(0x66, 0x69)) and not any(0x66 <= o < 0x69 for o in actions)
    if settings['start'] in ('pause', 'both') and not nmi_ok:
        report['warnings'].append('$0066 is game code: Pause cannot be used for Start (use start=buttons12)')
    if nmi_ok:
        protected.update(range(0x66, 0x69))
    protected |= protected_v
    tramps = []        # [start, end, {site: handler index}, init?]
    rc = cover(rom, an, 0, z80.length(rom.b, 0), protected - set(range(0, z80.length(rom.b, 0))))
    if rc:
        tramps.append([rc[0], rc[1], {}, 'init'])
        protected.update(range(rc[0], rc[1]))
    else:
        report['warnings'].append('reset code could not be hooked: the runtime RAM is not cleared at power-on')
    inplace = {}
    ramcalls = []
    for o in calls:
        _, handler, what, slot = actions[o]
        idx = rt.HANDLERS.index(handler)
        if rst is not None and z80.length(rom.b, o) == 2:
            inplace[o] = idx
            continue
        if o in getattr(an, 'ram_srcs', ()):
            ramcalls.append(o)
            continue
        n = z80.length(rom.b, o)
        c = cover(rom, an, o, n, protected - set(range(o, o + n)))
        if c is None and tramps and o not in an.targets:
            q, last = tramps[-1][1], tramps[-1]
            while q < o and q in an.code and q not in an.targets and q not in protected:
                q += z80.length(rom.b, q)
            if q == o and o - last[1] < 16:
                last[1] = o + n; last[2][o] = idx
                protected.update(range(o, o + n))
                continue
        if c is None:
            report['unhandled'].append({'off': o, 'why': f'{what}: no free RST and no room for a jump (jump targets around it)'})
            del actions[o]
            continue
        if handler == 'h_outi' and c[0] == o and c[1] == o + 4 and rom.b[o + 2:o + 4] == bytes([0x10, 0xFC]) and (o + 2) not in an.targets:
            idx = rt.HANDLERS.index('h_oidj')          # OUTI / DJNZ -2 in the trampoline: one call does the loop, the DJNZ is left out
            actions[o] = ('call', 'h_oidj', what.replace('OUTI -> dispatch', 'OUTI/DJNZ loop -> h_oidj'), slot)
        protected.update(range(c[0], c[1]))
        tramps.append([c[0], c[1], {o: idx}, None])
    # code copied to RAM, no RST: the site and the simple instructions before it become CALL t / NOPs in the ROM source; the
    # trampoline redoes them, calls the runtime and RETurns into the RAM copy (no jump back to a fixed address needed)
    rs = set(ramcalls)
    for o in ramcalls:
        if any(t[0] <= o < t[1] for t in tramps if t[3] == 'ram'):
            continue
        n = z80.length(rom.b, o)
        st, en, sites = o, o + n, {o: rt.HANDLERS.index(actions[o][1])}
        while en - st < 3:
            got = False
            for back in (1, 2, 3):
                q = st - back
                if q >= 0 and z80.length(rom.b, q) == back and (rom.b[q] in SIMPLE or q in rs) and q not in protected:
                    if q in rs:
                        sites[q] = rt.HANDLERS.index(actions[q][1])
                    st, got = q, True
                    break
            if not got:
                if en in rs or rom.b[en] in SIMPLE:
                    if en in rs:
                        sites[en] = rt.HANDLERS.index(actions[en][1])
                    en += z80.length(rom.b, en)
                else:
                    break
        if en - st < 3:
            report['unhandled'].append({'off': o, 'why': f'{actions[o][2]}: no simple instructions around it to make room for a CALL'})
            del actions[o]
            continue
        protected.update(range(st, en))
        tramps.append([st, en, sites, 'ram'])
    # code the game unpacks to RAM (profile "ram_code": {"entry": ROM offset of the CALL/JP into it, "frame": when it is there}):
    # its VDP writes get CALLs to trampolines written into the RAM copy by a fixup that runs in place of the entry jump
    ramfix, ram_used = None, set()
    rc_p = profile.get('ram_code')
    img = profile.get('_ram_image')
    if rc_p and img:
        e0 = int(rc_p['entry'], 0)
        if rom.b[e0] in (0xCD, 0xC3) and rom.b[e0 + 2] >= 0xC0:
            dest = rom.b[e0 + 1] | rom.b[e0 + 2] << 8
            hi = dest + int(rc_p.get('length', '0x400'), 0)
            mem = lambda a: img[(a - 0xC000) & 0x1FFF]
            def ilen(a):
                return z80.length(bytes(mem(a + k) for k in range(4)), 0)
            buf = bytes(mem(a) for a in range(dest, hi + 4))
            rtg, a, starts = set(), dest, set()    # jump targets and instruction starts inside the unpacked code (a linear sweep)
            while a < hi:
                starts.add(a)
                for t_ in z80.flow(buf, a - dest, a)[1]:
                    rtg.add(t_)
                a += z80.length(buf, a - dest)
            pcs = sorted({x['pc'] for x in an.sites if x['off'] < 0 and x['write'] and 0x80 <= x['port'] < 0xC0 and dest <= x['pc'] < hi
                          and x['pc'] not in getattr(an, 'ram_handled', ())
                          and (x['port'] & 1 or x['cram'] or settings['vram_only_data_writes'] == 'patch')})
            sites_h = {}
            for pc in pcs:
                o1, o2 = mem(pc), mem(pc + 1)
                h = ('h_bf' if o2 & 1 else 'h_be') if o1 == 0xD3 and 0x80 <= o2 < 0xC0 else BLOCK.get(o2) if o1 == 0xED and o2 in BLOCK else                     ('oc_' + OC_REG[o2]) if o1 == 0xED and o2 in OC_REG else None
                if h:
                    sites_h[pc] = h
            rt_list, edits, done_r = [], [], set()
            for pc in sorted(sites_h):
                if pc in done_r:
                    continue
                st, en, ss = pc, pc + ilen(pc), {pc: rt.HANDLERS.index(sites_h[pc])}
                while en - st < 3:
                    got = False
                    for back in (1, 2, 3):
                        q = st - back
                        if q in starts and ilen(q) == back and (mem(q) in SIMPLE) and q not in done_r and st not in rtg:
                            st, got = q, True
                            break
                    if not got:
                        if en in rtg:
                            break
                        if en in sites_h:
                            ss[en] = rt.HANDLERS.index(sites_h[en]); en += ilen(en)
                        elif mem(en) in SIMPLE:
                            en += ilen(en)
                        else:
                            break
                if en - st < 3:
                    report['unhandled'].append({'off': None, 'pc': pc, 'why': 'unpacked RAM code: no simple instructions around the site for a CALL'})
                    continue
                done_r.update(range(st, en))
                rt_list.append((st, en, ss))
            if rt_list:
                ramfix = {'entry': e0, 'dest': dest, 'tramps': rt_list, 'mem': mem, 'ilen': ilen}
                fixed = set(range(dest, hi)) & done_r
                report['unhandled'] = [u for u in report['unhandled'] if not (u.get('off') is None and u.get('pc') in fixed)]
                for st, en, ss in rt_list:
                    for pc, ix in ss.items():
                        report['patches'].append({'off': None, 'pc': pc, 'what': f'{rt.HANDLERS[ix]} in unpacked RAM code (fixup at the entry ${e0:05X})'})
                protected.update(range(e0, e0 + 3))
                ram_used.update(rt.HANDLERS[ix] for _, _, ss in rt_list for ix in ss.values())
        else:
            report['warnings'].append(f'ram_code entry ${e0:05X} is not a CALL/JP into RAM')
    # unrolled OUTI chains in place: each site's index says how many OUTIs are left, one call does them all (h_chain)
    outi = sorted(o for o in inplace if actions[o][1] == 'h_outi')
    runs_o, cur = [], []
    for o in outi:
        if cur and o == cur[-1] + 2:
            cur.append(o)
        else:
            if cur:
                runs_o.append(cur)
            cur = [o]
    if cur:
        runs_o.append(cur)
    for r in runs_o:
        if len(r) < 4:
            continue
        for i, o in enumerate(r):
            inplace[o] = rt.CHAIN + min(rt.CHAIN_MAX, len(r) - i)
            if len(r) - i > rt.CHAIN_MAX:
                inplace[o] = rt.CHAIN + rt.CHAIN_MAX     # (a longer chain: the call does the first CHAIN_MAX and returns into the rest)
            actions[o] = ('call', 'h_chain', actions[o][2].replace('OUTI -> dispatch', 'OUTI chain -> h_chain'), actions[o][3])
    # OUTI / DJNZ -2 (a colour upload loop): one call for the whole loop, the DJNZ becomes NOPs
    for o in sorted(inplace):
        if actions[o][1] == 'h_outi' and rom.b[o + 2:o + 4] == bytes([0x10, 0xFC]) and (o + 2) not in actions and (o + 2) not in an.targets:
            inplace[o] = rt.HANDLERS.index('h_oidj')
            actions[o] = ('call', 'h_oidj', actions[o][2].replace('OUTI -> dispatch', 'OUTI/DJNZ loop -> h_oidj'), actions[o][3])
            actions[o + 2] = ('bytes', bytes(2), 'DJNZ of an OUTI loop -> NOP NOP (h_oidj loops)')
    used = {actions[o][1] for o in actions if actions[o][0] == 'call'} | ram_used
    hookspec = None
    if hook:
        table = []                         # the game's own RST calls of the hooked vector (static and traced code)
        for o, sl in sorted(rst_calls(rom, an, hook[0]).items()):
            if o not in inplace:
                pc = cpu_of(o, an.code.get(o, sl)) + 1
                page = 0xFF if pc < 0x4000 or (pc < 0x8000 and an.fixed1) else o >> 14
                if table and table[-1][2] == page and table[-1][0] + table[-1][1] + 1 == pc:
                    table[-1][1] += 2
                else:
                    table.append([pc, 1, page])
        report['hookGameCalls'] = len(table)
        if len(table) > 255:
            report['verdict'] = f'{len(table)} known game calls of the hooked RST (the lookup holds 255)'
            return None, report, 3
        v0, v1 = hook[1], hook[2]

        def emit(asm, v0=v0, v1=v1):
            q, fixups = v0, []
            while q < v1:
                n = z80.length(rom.b, q)
                jf = relocate(rom.b, q, n, q, asm)
                if jf is not None:
                    fixups.append(jf)
                q += n
            for jf in fixups:
                asm.code[jf] = v1 & 0xFF; asm.code[jf + 1] = v1 >> 8
            asm.op_w(0xC3, v1)
        hookspec = {'emit': emit, 'table': table}

    far_bank_of = [None]

    def tramp_pieces():
        out = []
        for s, e, tsites, init in tramps:
            slot = an.code.get(s, 0)
            t = z80.Asm(); t.label(f't_{s:x}')
            q, fixups = s, []
            while q < e:
                n = z80.length(rom.b, q)
                if q in tsites:
                    t.op_w(0xCD, 'disp'); t.db(tsites[q])
                    if tsites[q] == rt.HANDLERS.index('h_oidj'):
                        n += 2                     # its DJNZ: done by the handler
                else:
                    if q == s and init == 'init':
                        rt.init_inline(t, ram, far_bank_of[0], nvars)
                    jf = relocate(rom.b, q, n, cpu_of(q, slot), t)
                    if jf is not None:
                        fixups.append(jf)
                q += n
            if init == 'ram':
                t.db(0xC9)
                out.append(t)
                continue
            back = cpu_of(e, slot)
            for jf in fixups:
                t.code[jf] = back & 0xFF; t.code[jf + 1] = back >> 8
            t.op_w(0xC3, back)
            out.append(t)
        if ramfix:
            fx = z80.Asm(); fx.label('rfix')
            for st, en, ss in ramfix['tramps']:
                t = z80.Asm(); t.label(f'tr_{st:x}')
                q = st
                while q < en:
                    n = ramfix['ilen'](q)
                    if q in ss:
                        t.op_w(0xCD, 'disp'); t.db(ss[q])
                    else:
                        t.db(bytes(ramfix['mem'](q + k) for k in range(n)))
                    q += n
                t.db(0xC9)
                out.append(t)
                fx.db(0x3E, 0xCD); fx.op_w(0x32, st); fx.op_w(0x21, f'tr_{st:x}'); fx.op_w(0x22, st + 1)
                if en - st > 3:
                    fx.db(0xAF)
                    for k in range(st + 3, en):
                        fx.op_w(0x32, k)
            fx.op_w(0xC3, ramfix['dest'])
            out.append(fx)
        return out

    # ---- where the runtime goes: near (all of it in always-mapped ROM) or far (a stub there, the handlers in a bank of their own).
    # Each piece goes wherever it fits: $FF/$00 padding first, then (with traces) bytes the game never ran or read.
    lo_fixed, hi_fixed = 0x0400, (0x7FF0 if an.fixed1 and rom.n > 0x4000 else 0x4000)
    fill_runs = free_runs(rom, an, 0x0400, 0x4000, protected)
    if hi_fixed > 0x4000:
        fill_runs += free_runs(rom, an, 0x4000, 0x7FF0, protected)
    fill_runs.sort(key=lambda r: (r[2] != 0xFF, -r[0]))
    runs = [(sz, st, 'ff' if f == 0xFF else '00') for sz, st, f in fill_runs]
    pad_runs = list(runs)
    run_sets = [[r for r in runs if r[2] == 'ff'], pad_runs]     # $FF padding, then $00 runs too (zeros are more often data)
    if profile.get('unread', 'use') != 'never':
        run_sets.append(pad_runs + [(sz, st, 'unread') for sz, st in unread_runs(rom, an, traces, lo_fixed, hi_fixed, protected, [(r[1], r[1] + r[0]) for r in fill_runs])])

    def keep(ps):
        return [p for p in ps if nmi_ok or 'nmi' not in p.labels]
    # padding first (near, then far: a bigger ROM is safer than bytes the traces only never saw read), then the never-read bytes
    spots, parts, far_off = None, None, None
    attempts = []                          # (runs, fast): padding with the fast path, padding without it, then never-read bytes
    for runs in run_sets:
        attempts += [(runs, f) for f in ((True, False) if profile.get("fast", True) else (False,))]
    for runs, fast in attempts:
        near = rt.build(settings, ram, used=used, hook=hookspec, fast=fast)
        probe = rt.build(settings, ram, far_bank=0, used=used, hook=hookspec, fast=fast)
        far_bank_of[0] = None
        pieces = keep(near['pieces']) + tramp_pieces()
        spots = place(pieces, runs)
        if spots is not None:
            parts = near
            break
        if SmsCodemasters(rom) or profile.get('layout') == 'near':
            continue
        far_bank_of[0] = 0                 # (same size as the real bank number)
        pieces = keep(probe['pieces']) + tramp_pieces()
        spots = place(pieces, runs)
        if spots is not None:
            new_n = 1 << max(16, (rom.n + 0x4000 - 1).bit_length())
            far_off = rom.n                # the runtime's bank is always a fresh one: its colour table sits at FAR_TABLE
            rom.b += bytes([0xFF]) * (new_n - rom.n)
            report['warnings'].append(f'ROM grown {rom.n // 1024} KB -> {new_n // 1024} KB for the runtime bank')
            rom.n = new_n
            parts = rt.build(settings, ram, far_bank=far_off >> 14, used=used, hook=hookspec, fast=fast)
            far_bank_of[0] = far_off >> 14
            pieces = keep(parts['pieces']) + tramp_pieces()
            break
    if spots is not None and not fast:
        report['warnings'].append('no room for the quick data-port path: every data-port site takes the full way (slower)')
    if spots is None:
        sizes = sorted((len(p.code) for p in rt.build(settings, ram, far_bank=0, used=used, hook=hookspec)['pieces'] + tramp_pieces()), reverse=True)
        report['verdict'] = (f'no room for the runtime in always-mapped ROM (far stub pieces {sizes}, {sum(sizes)} bytes; free runs '
                             f'{sorted((r[0] for r in runs), reverse=True)[:8]})')
        return None, report, 3
    kinds = {k for _, _, k in (runs[spots[i][1]] for i in range(len(pieces)))}
    if '00' in kinds:
        report['warnings'].append('runtime partly in a run of $00 bytes (no $FF padding big enough): check that the game does not read it as data')
    if 'unread' in kinds:
        report['warnings'].append('runtime partly in bytes the traced runs never ran or read (no padding big enough): a part of the game the traces did not reach could read them')
    far_base = 0x8000 | (far_off & 0x3FFF) if parts['far'] else None
    labels = {k: far_base + v for k, v in parts['far'].labels.items()} if parts['far'] else {}
    for p, (base, _) in zip(pieces, spots):
        labels.update({k: base + v for k, v in p.labels.items()})
    ranges = []
    for p, (base, _) in zip(pieces, spots):
        blob = p.link(base, labels)
        rom.b[base:base + len(blob)] = blob
        ranges.append([base, base + len(blob)])
    report['runtime'] = (('far' if parts['far'] else 'near') + f': {len(pieces)} pieces in always-mapped ROM ({sum(e - s for s, e in ranges)} bytes at '
                         + ', '.join(f'${s:04X}' for s, _ in ranges) + ')')
    if parts['far']:
        fblob = parts['far'].link(far_base, labels)
        rom.b[far_off:far_off + len(fblob)] = fblob
        assert len(fblob) <= rt.FAR_TABLE - 0x8000 and far_off & 0x3FFF == 0
        tb = (far_off & ~0x3FFF) + (rt.FAR_TABLE - 0x8000)
        rom.b[tb:tb + 4096] = rt.far_table(settings)
        ranges.append([far_off, far_off + len(fblob)])
        report['runtime'] += f', handlers in bank {far_off >> 14} at ${far_base:04X} ({len(fblob)} bytes)'
    report['runtimeRanges'] = ranges
    read = set()
    for t in traces:
        for a0, a1 in t.get('dataRead', []):
            read.update(range(a0, a1))
    hit = sum(1 for s, e in ranges for x in range(s, e) if x in read)
    if hit:
        report['warnings'].append(f'{hit} runtime bytes sit where the original game READ data in the traces')
    report['entry'] = (f'hooked RST ${rst:02X} + index byte, in place' if hook else f'RST ${rst:02X} + index byte, in place') if rst is not None else 'JP trampolines'
    if hook:
        t = labels['hk']
        rom.b[hook[1]] = 0xC3; rom.b[hook[1] + 1] = t & 0xFF; rom.b[hook[1] + 2] = t >> 8
        for k in range(hook[1] + 3, hook[2]):
            rom.b[k] = 0x00
    elif rst is not None:
        disp = labels['disp']
        rom.b[rst] = 0xC3; rom.b[rst + 1] = disp & 0xFF; rom.b[rst + 2] = disp >> 8
    for o, idx in sorted(inplace.items()):
        rom.b[o] = 0xC7 | rst; rom.b[o + 1] = idx
        report['patches'].append({'off': o, 'what': actions[o][2] + f' (RST ${rst:02X},{idx})'})
    for s, e, tsites, init in tramps:
        t = labels[f't_{s:x}']
        rom.b[s] = 0xCD if init == 'ram' else 0xC3; rom.b[s + 1] = t & 0xFF; rom.b[s + 2] = t >> 8
        for k in range(s + 3, e):
            rom.b[k] = 0x00
        if init == 'init':
            report['patches'].append({'off': s, 'what': 'reset -> init', 'cover': [s, e], 'tramp': t})
        for site in sorted(tsites):
            report['patches'].append({'off': site, 'what': actions[site][2], 'cover': [s, e], 'tramp': t})
    for o, a in sorted(actions.items()):
        if a[0] == 'bytes':
            rom.b[o:o + len(a[1])] = a[1]
            report['patches'].append({'off': o, 'what': a[2]})
    if ramfix:
        t = labels['rfix']
        rom.b[ramfix['entry'] + 1] = t & 0xFF; rom.b[ramfix['entry'] + 2] = t >> 8
        report['patches'].append({'off': ramfix['entry'], 'what': f'entry into unpacked RAM code -> fixup ${t:04X}, then ${ramfix["dest"]:04X}'})
    if nmi_ok:
        t = labels['nmi']
        rom.b[0x66] = 0xC3; rom.b[0x67] = t & 0xFF; rom.b[0x68] = t >> 8
        report['patches'].append({'off': 0x66, 'what': 'NMI (Pause) -> Start'})
    fix_header(rom, report)
    report['counts'] = {'patched': len(report['patches']), 'unhandled': len(report['unhandled']),
                        'staticCode': len(an.code), 'tracedCode': len(an.dyn_code), 'fixedSlot1': an.fixed1, 'freeRst': rsts}
    report['verdict'] = 'converted' + (' with unhandled sites' if report['unhandled'] else '')
    return bytes(rom.b), report, 1 if report['unhandled'] else 0


def SmsCodemasters(rom):
    """Codemasters carts keep a second header with a checksum at $7FE0 (the mapper registers are ROM writes, no RAM mirror)."""
    if rom.n < 0x8000:
        return False
    b = rom.b
    chk = b[0x7FE6] | b[0x7FE7] << 8
    inv = b[0x7FE8] | b[0x7FE9] << 8
    return chk != 0 and chk + inv == 0x10000


# ============================================================================================== traces (BrokenNes headless)

def workshop_exe(arg=None):
    for c in (arg, os.environ.get('BROKENDEV_WORKSHOP'),
              os.path.join(HERE, '..', '..', '..', 'Workshop', 'bin', 'Release', 'net10.0-windows', 'BrokenNes.Workshop.exe')):
        if c and os.path.exists(c):
            return os.path.abspath(c)
    raise SystemExit('BrokenNes.Workshop.exe not found (build Workshop, or set BROKENDEV_WORKSHOP / --workshop)')


def run_trace(rom_path, out_json, console, frames, inputs, exe):
    args = [exe, '--sms-run', rom_path, '--console', console, '--frames', str(frames), '--gg-sites', out_json,
            '--out', os.path.dirname(out_json) or '.', '--tag', os.path.splitext(os.path.basename(out_json))[0]]
    if inputs:
        args += ['--input', inputs]
    r = subprocess.run(args, capture_output=True, text=True, timeout=1800)
    if not os.path.exists(out_json):
        raise SystemExit(f'trace failed ({r.returncode}): {r.stdout[-400:]} {r.stderr[-400:]}')
    with open(out_json) as f:
        return json.load(f)


def ram_image(rom_path, work, frame, exe):
    """The game's 8 KB of work RAM at the end of a frame (the original on the GG core, no input): code it unpacked to RAM."""
    subprocess.run([exe, '--sms-run', rom_path, '--console', 'gg', '--frames', str(frame + 1), '--ram-at', str(frame),
                    '--out', work, '--tag', 'ramimg'], capture_output=True, text=True, timeout=1800)
    p = os.path.join(work, f'ramimg_f{frame}.ram.bin')
    if not os.path.exists(p):
        raise SystemExit('RAM image dump failed: ' + p)
    return open(p, 'rb').read()


def profile_inputs(profile, manifest):
    return profile.get('inputs') or manifest['defaultInputs']


def merge_settings(profile, sets):
    s = default_settings()
    s.update(profile.get('settings', {}))
    for kv in sets or []:
        k, v = kv.split('=', 1)
        if k not in s:
            raise SystemExit(f'unknown setting {k} (see: gg2sms.py manifest)')
        s[k] = int(v, 0) if isinstance(s[k], int) else v
    s['colour_table'] = rt.colour_table(s['colours'])
    return s


def residual(trace, ranges):
    """GG-only port use or colour writes still reaching the hardware in a converted ROM (outside the runtime)."""
    out = []
    for s in trace['sites']:
        inside = s['off'] >= 0 and any(lo <= s['off'] < hi for lo, hi in ranges)
        if inside:
            continue
        if s['port'] in GG_PORTS:
            out.append(s)
        elif s['port'] == 0xBE and s['cram']:
            out.append(s)
    return out


def main():
    ap = argparse.ArgumentParser(description='Nested Everything: Game Gear -> Master System')
    sub = ap.add_subparsers(dest='cmd', required=True)
    c = sub.add_parser('convert'); c.add_argument('inp'); c.add_argument('out')
    for p in (c,):
        p.add_argument('--profile'); p.add_argument('--sites', action='append', default=[]); p.add_argument('--set', action='append', default=[])
        p.add_argument('--report')
    t = sub.add_parser('trace'); t.add_argument('rom'); t.add_argument('out'); t.add_argument('--console', default='gg')
    t.add_argument('--frames', type=int, default=1800); t.add_argument('--input', default=''); t.add_argument('--workshop')
    fb = sub.add_parser('feedback'); fb.add_argument('inp'); fb.add_argument('out'); fb.add_argument('--profile'); fb.add_argument('--set', action='append', default=[])
    fb.add_argument('--work', default=None); fb.add_argument('--workshop'); fb.add_argument('--report'); fb.add_argument('--save-profile')
    sub.add_parser('manifest')
    a = ap.parse_args()
    if a.cmd == 'manifest':
        print(json.dumps(load_manifest(), indent=2)); return 0
    if a.cmd == 'trace':
        d = run_trace(a.rom, a.out, a.console, a.frames, a.input, workshop_exe(a.workshop))
        print(f"{len(d['sites'])} port sites, {len(d['code'])} instruction starts"); return 0
    profile = {}
    if a.profile:
        with open(a.profile, encoding='utf-8') as f:
            profile = json.load(f)
    settings = merge_settings(profile, a.set)
    data = open(a.inp, 'rb').read()
    if a.cmd == 'convert':
        traces = [json.load(open(p)) for p in a.sites]
        out, report, code = convert(data, settings, profile, traces)
        return finish(a, out, report, code)
    # feedback: trace the original with each input script, convert, trace the converted ROM, report residual GG port use
    manifest = load_manifest()
    exe = workshop_exe(a.workshop)
    work = a.work or os.path.splitext(a.out)[0] + '_work'
    os.makedirs(work, exist_ok=True)
    traces = []
    for i, inp in enumerate(profile_inputs(profile, manifest)):
        traces.append(run_trace(os.path.abspath(a.inp), os.path.join(work, f'orig{i}.json'), 'gg', inp['frames'], inp['input'], exe))
    if profile.get('ram_code'):
        profile['_ram_image'] = ram_image(os.path.abspath(a.inp), work, int(profile['ram_code'].get('frame', 60)), exe)
    for rnd in range(3):                   # residual GG port use in the converted game's own run becomes sites of the next round
        out, report, code = convert(data, settings, profile, traces)
        if out is None or not report.get('runtimeRanges'):
            break
        with open(a.out, 'wb') as f:
            f.write(out)
        rng = report.get('runtimeRanges', [])
        res = []
        for i, inp in enumerate(profile_inputs(profile, manifest)):
            smsin = inp['input'].replace('Start', settings_start_button(settings))
            tr = run_trace(os.path.abspath(a.out), os.path.join(work, f'conv{rnd}_{i}.json'), 'sms', inp['frames'], smsin, exe)
            res += residual(tr, rng)
        report['residual'] = res
        new = [r for r in res if r['off'] >= 0 and r['off'] not in {s2['off'] for t in traces for s2 in t['sites']}]
        if not new:
            break
        traces.append({'code': [[r['off'], r['pc'] >> 14 if r['pc'] < 0xC000 else 0] for r in new], 'targets': [], 'ramChanged': [],
                       'slotPages': [], 'sites': new, 'dataRead': []})
        report['warnings'].append(f'feedback round {rnd + 2}: {len(new)} sites only the converted game reached')
        if res:
            code = max(code, 1)
    if a.save_profile:
        prof = dict(profile)
        prof.setdefault('settings', {})
        prof['found'] = {'patched': [p['off'] for p in report.get('patches', [])], 'unhandled': report.get('unhandled', []),
                         'ram': report.get('ram'), 'runtime': report.get('runtime')}
        with open(a.save_profile, 'w', encoding='utf-8') as f:
            json.dump(prof, f, indent=2)
    return finish(a, out, report, code, write=False)


def settings_start_button(settings):
    return 'Select' if settings['start'] in ('pause', 'both') else 'A+B'


def finish(a, out, report, code, write=True):
    if out is not None and write:
        with open(a.out, 'wb') as f:
            f.write(out)
    rep = dict(report); rep['settings'] = {k: v for k, v in report['settings'].items() if k != 'colour_table'}
    if a.report:
        with open(a.report, 'w', encoding='utf-8') as f:
            json.dump(rep, f, indent=2)
    print(f"gg2sms: {report.get('verdict')}  patches={len(report.get('patches', []))} unhandled={len(report.get('unhandled', []))}"
          f" residual={len(report.get('residual', []))}  runtime={report.get('runtime')} ram={report.get('ram')}")
    for w in report.get('warnings', []):
        print('  warning: ' + w)
    for u in report.get('unhandled', [])[:20]:
        print('  unhandled: ', u)
    for r in report.get('residual', [])[:20]:
        print('  residual: ', r)
    return code


if __name__ == '__main__':
    sys.exit(main())
