# isols.py ISO [get PATH OUT]: list an ISO9660 image (primary names) or extract a file
import sys, struct
f=open(sys.argv[1],'rb'); S=2048
def rd(l,n): f.seek(l*S); return f.read(n)
pvd=rd(16,S); root=pvd[156:190]
def walk(lba,size,path):
    d=rd(lba,size); i=0
    while i<len(d):
        L=d[i]
        if L==0: i=(i//S+1)*S; continue
        e=d[i:i+L]; el,sz=struct.unpack_from('<I',e,2)[0],struct.unpack_from('<I',e,10)[0]; fl=e[25]; nl=e[32]; nm=e[33:33+nl]
        if nm not in (b'\0',b'\1'):
            n=path+'/'+nm.decode('latin1').split(';')[0]
            if fl&2: yield from walk(el,sz,n)
            else: yield n,el,sz
        i+=L
for n,l,s in walk(struct.unpack_from('<I',root,2)[0],struct.unpack_from('<I',root,10)[0],''):
    if len(sys.argv)>3 and sys.argv[2]=='get':
        if n.upper()==sys.argv[3].upper(): open(sys.argv[4],'wb').write(rd(l,s)); print('got',n,s)
    else: print(n,s)
