import sys; sys.path.insert(0,'.'); from sclib import S
E=chr(92)+"n"
s=S()
s.step(60,"s"); s.step(120,"o"); s.step(200,"b"); s.step(30,"o"); s.step(30,"t"); s.step(30,E); s.step(60,E)
s.step(300, s.click(325,414))
def pick(x,y,ox,oy):
    a=s.mv(x,y)+'{m:0,0,1}{wait:10}'
    b=s.mv(ox,oy).replace('}','',0)
    b=''.join(('{m:%s,1}'%p[3:-1]) for p in b.replace('}{','}|{').split('|') if p)
    return a+b+'{wait:5}{m:0,0,0}'
s.step(600, pick(268,322,240,354))
s.step(30, pick(268,341,240,358))
s.step(30, s.click(516,392))
s.step(900, E)
s.step(60, '')
s.save('C:/BrokenNes-work/scbot/st_tvz0.bin')
s.write(sys.argv[1])
