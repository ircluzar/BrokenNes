# ssagg.py speedscope.json [n]: self and inclusive time per frame, summed over the busiest thread (tools/x86-speed)
import json,sys,collections
d=json.load(open(sys.argv[1])); n=int(sys.argv[2]) if len(sys.argv)>2 else 40
fr=d['shared']['frames']
best=max(d['profiles'],key=lambda p:len(p.get('samples',p.get('events',[]))))
selfc=collections.Counter(); incl=collections.Counter(); tot=0
if best['type']=='sampled':
    for s,w in zip(best['samples'],best['weights']):
        tot+=w; ss=[f for f in s if fr[f]['name'] not in ('CPU_TIME','UNMANAGED_CODE_TIME')]; selfc[ss[-1] if ss else s[-1]]+=w
        for f in set(s): incl[f]+=w
else:
    st=[];last=None
    for e in best['events']:
        if last is not None and st:
            dt=e['at']-last; tot+=dt; ss=[f for f in st if fr[f]['name'] not in ('CPU_TIME','UNMANAGED_CODE_TIME')]; selfc[ss[-1] if ss else st[-1]]+=dt
            for f in set(st): incl[f]+=dt
        last=e['at']
        if e['type']=='O': st.append(e['frame'])
        else: st.pop()
print('total',tot)
for f,w in selfc.most_common(n): print(f"self {100*w/tot:5.1f}% incl {100*incl[f]/tot:5.1f}%  {fr[f]['name'][:150]}")
