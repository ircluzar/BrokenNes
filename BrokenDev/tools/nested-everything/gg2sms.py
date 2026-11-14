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
        elif op == 0xDB and p in GG_PORTS:
            out.append({'off': off, 'slot': slot, 'form': 'in_n', 'port': p})
    return out


OC_REG = {0x79: 'a', 0x41: 'b', 0x49: 'c', 0x51: 'd', 0x59: 'e', 0x61: 'h', 0x69: 'l', 0x71: '0'}
BLOCK = {0xA3: 'h_outi', 0xB3: 'h_otir', 0xAB: 'h_outd', 0xBB: 'h_otdr'}


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
    for pc, port in ram_sites:
        unhandled.append({'off': None, 'pc': pc, 'why': f'code in RAM touches port ${port:02X} (cannot be patched in ROM)'})
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


def find_free(rom, an, need, protected):
    """A run of filler bytes ($FF, or $00 when allowed) in bank 0 that nothing runs or jumps into."""
    b = rom.b
    best = None
    for fill in (0xFF, 0x00):
        i = 0x0400
        while i < min(0x4000, rom.n):
            if b[i] == fill:
                j = i
                while j < min(0x4000, rom.n) and b[j] == fill and j not in an.code and j not in an.targets and j not in protected:
                    j += 1
                lo, hi = i + 16, j - 16                       # keep a margin: the bytes next to a run may be read as data
                if hi - lo >= need and (best is None or hi - lo > best[1] - best[0]):
                    best = (lo, hi)
                i = max(j, i + 1)
            else:
                i += 1
        if best:
            return best[0], fill
    return None, None


def convert(data, settings, profile, traces):
    rom = Rom(data)
    report = {'settings': settings, 'patches': [], 'unhandled': [], 'warnings': []}
    h = rom.header()
    region = rom.b[h + 15] >> 4 if h is not None else None
    if region in (3, 4):
        report['verdict'] = 'already an SMS game (header region %d): copied unchanged' % region
        return bytes(rom.b), report, 0
    if settings['region'] == 'auto':
        settings = dict(settings, region='japan' if region == 5 else 'export')
        report['settings'] = settings
    an = Analysis(rom, traces)
    if not traces:
        report['warnings'].append('no dynamic trace: only statically proven sites are patched (run "feedback" for the rest)')
    if not an.fixed0:
        report['verdict'] = 'the game maps other banks into slot 0: the runtime has nowhere safe to live'
        return None, report, 3
    ram = an.ram_slot(len(rt.VARS), int(profile['ram'], 0) if profile.get('ram') else None)
    if ram is None:
        report['verdict'] = 'no RAM the game leaves alone for the runtime (set "ram" in the profile)'
        return None, report, 3
    report['ram'] = f'${ram:04X}-${ram + len(rt.VARS) - 1:04X}'
    actions, unhandled = plan(rom, an, settings, profile)
    report['unhandled'] += unhandled

    asm = rt.build(settings, ram)
    rt_len = len(asm.code)
    sites = sorted(o for o, a in actions.items() if a[0] == 'call')
    protected = set()
    for o, a in actions.items():
        n = z80.length(rom.b, o)
        protected.update(range(o, o + n))
    # NMI at $0066 and the reset trampoline at $0000
    tramps = []        # (start, end, site_off_or_None, handler)
    nmi_ok = settings['start'] in ('pause', 'both') and all(x not in an.dyn_code for x in range(0x66, 0x69)) and not any(0x66 <= o < 0x69 for o in actions)
    if settings['start'] in ('pause', 'both') and not nmi_ok:
        report['warnings'].append('$0066 is game code: Pause cannot be used for Start (use start=buttons12)')
    rc = cover(rom, an, 0, z80.length(rom.b, 0), protected - set(range(0, z80.length(rom.b, 0))))
    if rc:
        tramps.append([rc[0], rc[1], {}, 'init'])
        protected.update(range(rc[0], rc[1]))
    else:
        report['warnings'].append('reset code could not be hooked: the runtime RAM is not cleared at power-on')
    for o in sites:
        _, handler, what, slot = actions[o]
        n = z80.length(rom.b, o)
        c = cover(rom, an, o, n, protected - set(range(o, o + n)))
        if c is None and tramps and o not in an.targets:
            # join the previous trampoline when only plain instructions lie between its end and this site
            q, last = tramps[-1][1], tramps[-1]
            while q < o and q in an.code and q not in an.targets and q not in protected:
                q += z80.length(rom.b, q)
            if q == o and o - last[1] < 16:
                last[1] = o + n; last[2][o] = handler
                protected.update(range(o, o + n))
                continue
        if c is None:
            report['unhandled'].append({'off': o, 'why': f'{what}: no room for a jump (jump targets around it)'})
            del actions[o]
            continue
        protected.update(range(c[0], c[1]))
        tramps.append([c[0], c[1], {o: handler}, None])
    # size: the runtime + each trampoline (calls 3 + relocated bytes + 3 for the JP back, + slack for DJNZ / JR rewrites)
    need = rt_len + sum(3 + (e - s) * 2 + 6 for s, e, _, _ in tramps) + 8
    base, fill = find_free(rom, an, need, protected)
    if base is None:
        report['verdict'] = f'no {need} free bytes in bank 0 for the runtime'
        return None, report, 3
    if fill == 0x00:
        report['warnings'].append('runtime placed in a run of $00 bytes (no $FF padding big enough): check that the game does not read it as data')
    report['runtime'] = f'${base:04X} ({need} bytes reserved, {rt_len} runtime)'
    # assemble the trampolines after the runtime
    tasm = asm
    for s, e, tsites, init in tramps:
        slot = an.code.get(s, 0)
        tasm.label(f't_{s:x}')
        q = s
        fixups = []
        while q < e:
            n = z80.length(rom.b, q)
            if q in tsites:
                tasm.op_w(0xCD, tsites[q])
            else:
                if q == s and init == 'init':
                    tasm.op_w(0xCD, 'init')
                jf = relocate(rom.b, q, n, cpu_of(q, slot), tasm)
                if jf is not None:
                    fixups.append(jf)
            q += n
        back = cpu_of(e, slot)
        for jf in fixups:
            tasm.code[jf] = back & 0xFF; tasm.code[jf + 1] = back >> 8
        tasm.op_w(0xC3, back)
    blob = tasm.link(base)
    if len(blob) > need:
        report['verdict'] = f'internal: runtime grew past its reservation ({len(blob)} > {need})'
        return None, report, 3
    rom.b[base:base + len(blob)] = blob
    for s, e, tsites, init in tramps:
        t = tasm.addr(base, f't_{s:x}')
        rom.b[s] = 0xC3; rom.b[s + 1] = t & 0xFF; rom.b[s + 2] = t >> 8
        for k in range(s + 3, e):
            rom.b[k] = 0x00
        if init:
            report['patches'].append({'off': s, 'what': 'reset -> init', 'cover': [s, e], 'tramp': t})
        for site in sorted(tsites):
            report['patches'].append({'off': site, 'what': actions[site][2], 'cover': [s, e], 'tramp': t})
    for o, a in sorted(actions.items()):
        if a[0] == 'bytes':
            rom.b[o:o + len(a[1])] = a[1]
            report['patches'].append({'off': o, 'what': a[2]})
    if nmi_ok:
        t = tasm.addr(base, 'nmi')
        rom.b[0x66] = 0xC3; rom.b[0x67] = t & 0xFF; rom.b[0x68] = t >> 8
        report['patches'].append({'off': 0x66, 'what': 'NMI (Pause) -> Start'})
    fix_header(rom, report)
    report['counts'] = {'patched': len(report['patches']), 'unhandled': len(report['unhandled']),
                        'staticCode': len(an.code), 'tracedCode': len(an.dyn_code), 'fixedSlot1': an.fixed1}
    report['verdict'] = 'converted' + (' with unhandled sites' if report['unhandled'] else '')
    return bytes(rom.b), report, 1 if report['unhandled'] else 0


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


def residual(trace, report_runtime_range):
    """GG-only port use or colour writes still reaching the hardware in a converted ROM (outside the runtime)."""
    lo, hi = report_runtime_range
    out = []
    for s in trace['sites']:
        inside = s['off'] >= 0 and lo <= s['off'] < hi
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
    out, report, code = convert(data, settings, profile, traces)
    if out is not None:
        with open(a.out, 'wb') as f:
            f.write(out)
        rt0 = int(report['runtime'].split()[0][1:], 16)
        rng = (rt0, rt0 + int(report['runtime'].split('(')[1].split()[0]))
        res = []
        for i, inp in enumerate(profile_inputs(profile, manifest)):
            smsin = inp['input'].replace('Start', settings_start_button(settings))
            tr = run_trace(os.path.abspath(a.out), os.path.join(work, f'conv{i}.json'), 'sms', inp['frames'], smsin, exe)
            res += residual(tr, rng)
        report['residual'] = res
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
