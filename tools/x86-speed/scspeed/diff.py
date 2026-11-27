import sys,struct
def load(f):
    d={}
    for l in open(f):
        if l.startswith('dump '):
            a,b=l[5:].split(':',1); bs=bytes((0 if x=='??' else int(x,16)) for x in b.split()) 
            d[int(a,16)]=bs
    return d
a=load(sys.argv[1]); b=load(sys.argv[2])
ks=sorted(a)
ma=b''.join(a[k] for k in ks); mb=b''.join(b[k] for k in ks); base=ks[0]
for o in range(0,len(ma)-3,4):
    x=struct.unpack_from('<I',ma,o)[0]; y=struct.unpack_from('<I',mb,o)[0]
    if 100<=y-x<=400 and 50<x<10**7: print(hex(base+o),x,y,y-x)
