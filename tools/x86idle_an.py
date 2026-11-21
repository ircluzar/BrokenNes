import sys, capstone
from capstone import x86 as X
f=sys.argv[1]; ins={}; edges=[]
for ln in open(f):
    t=ln.split()
    if t[0]=='cycle': hdr=dict(zip(t[0::2],map(int,t[1::2])))
    elif t[0]=='i': ins[int(t[1],16)]=int(t[2])
    elif t[0]=='e': edges.append((int(t[1],16),int(t[2],16),int(t[3]),int(t[4]),bytes.fromhex(t[5]) if len(t)>5 else b''))
tot=hdr['cycle']; otheridle=hdr['idlenow']-hdr['idle0']-hdr['haltidle']
clock=tot+hdr['idlenow']-hdr['idle0']
md={16:capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_16),32:capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_32)}
for m in md.values(): m.detail=True
def classify(fr,to,bits,code):
    # straight-line body to..fr inclusive; pure = no mem write, no OUT, no call/int, no string store
    why=[]; body=[]
    for i in md[bits].disasm(code,to):
        if i.address>fr: break
        body.append(f"{i.mnemonic} {i.op_str}")
        mn=i.mnemonic
        if mn.startswith(('out','call','int','stos','movs','push','ins','iret','ret','cli','sti','lock','xchg','cmpxchg','popf','lgdt','mov cr')): why.append(mn); continue
        if mn.startswith('rep'): why.append(mn); continue
        try:
            _,w=i.regs_access()
        except Exception: w=[]
        for op in i.operands:
            if op.type==X.X86_OP_MEM and (op.access & capstone.CS_AC_WRITE): why.append('memw:'+mn)
        if mn.startswith('in'): pass
    return (not why), body, why
loops=[]
for fr,to,cnt,bits,code in edges:
    cyc=sum(v for a,v in ins.items() if to<=a<=fr)
    loops.append((cyc,fr,to,cnt,bits,code))
loops.sort(reverse=True)
# avoid double counting nested loops: take top 20 by cycles, mark overlap
pure_cyc=0; seen=set(); out=[]
for cyc,fr,to,cnt,bits,code in loops[:20]:
    pure,body,why=classify(fr,to,bits,code)
    rng=set(a for a in ins if to<=a<=fr); new=sum(ins[a] for a in rng-seen); seen|=rng
    if pure: pure_cyc+=new
    out.append(f"{cyc/clock*100:6.2f}% excl {new/clock*100:5.2f}% {'PURE' if pure else 'side'} {to:08x}-{fr:08x} it={cnt} {';'.join(body)[:160]} {('['+','.join(why[:3])+']') if why else ''}")
print(f"{f}: guest clock {clock}  instr {tot}  HLT {hdr['haltidle']/clock*100:.2f}%  otherwait {otheridle/clock*100:.2f}%  spin {hdr['spin']/clock*100:.2f}% ({hdr['spinhits']} hits)  pure-loop(top20) {pure_cyc/clock*100:.2f}%")
print("  idle total (HLT+spin+pure loops) = %.2f%%"%((hdr['haltidle']+hdr['spin']+pure_cyc)/clock*100))
for o in out: print("  "+o)
