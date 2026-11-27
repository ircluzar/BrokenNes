import sys,struct
def load(f):
    d={}
    for l in open(f):
        if l.startswith('dump '):
            a,b=l[5:].split(':',1); t=b.split()
            if '??' in t: continue
            d[int(a,16)]=bytes(int(x,16) for x in t)
    return d
a,b,c=[load(f) for f in sys.argv[1:4]]
for k in sorted(set(a)&set(b)&set(c)):
    for o in range(0,16,4):
        x,y,z=[struct.unpack_from('<I',m[k],o)[0] for m in (a,b,c)]
        if 50<y-x<500 and abs((z-y)-(y-x))<=6: print(hex(k+o),x,y,z,y-x,z-y)
