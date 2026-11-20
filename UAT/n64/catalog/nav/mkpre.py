import sys
L=open('observe.txt').read().splitlines()
for spec in sys.argv[1:]:
    g,n=spec.split(':'); out=[]
    for l in L[1:]:
        out.append(l)
        if l=='screenshot o%02d'%int(n) or l=='screenshot o%d'%int(n): break
    out.append('savestate pre')
    open('pre/%s.txt'%g,'w').write('\n'.join(out)+'\n')
