# regcheck.py BEFORE.img AFTER.img: patch BEFORE's hives with the delta to AFTER and check the CREG free-list invariants (RGKN free nodes, RGDB free chains + unused counts) Registry Checker relies on
import sys, struct; sys.path.insert(0, __import__("os").path.dirname(__import__("os").path.abspath(__file__)))
import fat32,creg
def check(d):
    rk=0x20; size=struct.unpack_from('<I',d,0x24)[0]; bad=[]
    f=struct.unpack_from('<I',d,rk+12)[0]; seen=0
    while f!=0xffffffff:
        n=struct.unpack_from('<III',d,rk+f)
        if n[0]!=0x80000000: bad.append('rgkn free node %x not free'%f); break
        f=n[2]; seen+=1
        if seen>100000: bad.append('rgkn loop'); break
    o=struct.unpack_from('<I',d,8)[0]; nb=0
    while o<len(d) and d[o:o+4]==b'RGDB':
        bsz,un=struct.unpack_from('<II',d,o+4); fo=struct.unpack_from('<I',d,o+16)[0]; tot=0
        while fo!=0xffffffff:
            rsz,kid,kb,nx=struct.unpack_from('<IHHI',d,o+fo)
            if kid!=0xffff: bad.append('blk %d chain hits used rec'%nb); break
            tot+=rsz; fo=nx
        p=o+0x20; allf=0
        while p<o+bsz:
            rsz,kid=struct.unpack_from('<IH',d,p)
            if rsz==0: bad.append('blk %d zero rec'%nb); break
            if kid==0xffff: allf+=rsz
            p+=rsz
        if p!=o+bsz: bad.append('blk %d recs end %x'%(nb,p-o))
        if not (tot==un==allf): bad.append('blk %d unused %d chain %d all %d'%(nb,un,tot,allf))
        o+=bsz; nb+=1
    if o!=len(d): bad.append('trailing bytes')
    if nb!=struct.unpack_from('<H',d,0x10)[0]: bad.append('block count')
    return bad
if __name__ != "__main__": raise SystemExit
A=fat32.Fat32(sys.argv[1]); B=fat32.Fat32(sys.argv[2])
for h in ['WINDOWS/SYSTEM.DAT','WINDOWS/USER.DAT']:
    da=A.read_file(h); print(h,'base',check(da)[:3])
    out=creg.Hive(da).apply(creg.diff(creg.Hive(da),creg.Hive(B.read_file(h))))
    print(h,'patched',check(out)[:5],'residual',len(creg.diff(creg.Hive(out),creg.Hive(B.read_file(h)))))
