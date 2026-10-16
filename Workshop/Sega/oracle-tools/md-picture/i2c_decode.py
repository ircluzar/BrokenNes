"""python i2c_decode.py <cart-trace.txt> [acclaim|sega] [how many transactions to print]

Decodes a game's EEPROM bit stream into transactions (START ... STOP, the bytes the master sent, 9 bits each with the acknowledge slot). The input is the 'cart' lines of
`BrokenNes.Workshop --md-run ROM --frames N --pad-script default --quiet --trace t.txt --trace-cart-only` (keep them with `grep -a " cart " t.txt`; a minute of play is tens of
millions of lines for a game that polls). `acclaim`: SCL is bit 0 of the byte at $200000, SDA bit 0 of $200001; `sega`: SDA bit 0 and SCL bit 1 of $200001. When SDA and SCL change
in one write the clock edge is applied first on a fall and last on a rise (the rule of MdEeprom.Drive). A '(A1 ...)' transaction is a read; the length in bits tells how many bytes."""
import re, os, sys, collections
sp = os.path.dirname(os.path.abspath(__file__))
fn = sys.argv[1]
wiring = sys.argv[2] if len(sys.argv) > 2 else 'acclaim'
pat = re.compile(r'^f(\d+) l\d+ cart write \$([0-9A-F]{6})(u|-)(l|-) = ([0-9A-F]{4})')
scl, sda = 1, 1
cur = None
tx = []
hist = collections.Counter()
nlines = 0
for line in open(fn, errors='replace'):
    m = pat.match(line)
    if not m: continue
    fr = int(m.group(1)); a = m.group(2); hi, lo = m.group(3) == 'u', m.group(4) == 'l'
    v = int(m.group(5), 16)
    if a != '200000': continue
    nscl, nsda = scl, sda
    if wiring == 'acclaim':
        if hi: nscl = (v >> 8) & 1
        if lo: nsda = v & 1
    else:
        if lo: nsda = v & 1; nscl = (v >> 1) & 1
    # simultaneous change: SCL falling first
    if nsda != sda and nscl != scl:
        if nscl == 0:
            order = [('scl', nscl), ('sda', nsda)]
        else:
            order = [('sda', nsda), ('scl', nscl)]
    elif nsda != sda: order = [('sda', nsda)]
    elif nscl != scl: order = [('scl', nscl)]
    else: order = []
    for k, x in order:
        if k == 'sda':
            if scl == 1:
                if sda == 1 and x == 0:
                    cur = {'fr': fr, 'bits': []}; tx.append(cur)
                elif sda == 0 and x == 1:
                    cur = None
            sda = x
        else:
            if x == 1 and cur is not None: cur['bits'].append(sda)
            scl = x
print('transactions', len(tx))
def bytes_of(b):
    return [int(''.join(map(str, b[i:i+8])), 2) for i in range(0, len(b) - 7, 9)]
n = 0
lens = collections.Counter()
for t in tx:
    by = bytes_of(t['bits'])
    if not by: continue
    lens[(by[0], len(t['bits']))] += 1
    if n < int(sys.argv[3]) if len(sys.argv) > 3 else 40:
        print('f%d %s (%d bits)' % (t['fr'], ' '.join('%02X' % x for x in by[:14]), len(t['bits'])))
        n += 1
print('first byte / bit-length histogram (top):', lens.most_common(14))
