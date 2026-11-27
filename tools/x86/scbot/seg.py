import sys, os; sys.path.insert(0,'.'); from sclib import S
E=chr(92)+'n'
def seg1(s):
    s.step(5, '{f10}')
    s.step(20, 'o')
    s.step(20, 'p')
    s.step(20, s.click(416,99)+'{wait:5}'+s.click(217,167)+'{wait:5}'+s.click(255,297))
    s.step(20, '{esc}')
    s.step(20, '')
    s.step(5, s.box(285,240,400,285)+'{wait:4}'+s.rclick(560,200))
    s.step(10, s.click(350,190)+'{ctrl+1}s')
    s.step(300,'')
MIN='57F0D8'
def U(n,mx=6000): return '{until:%s>=%d,%d}'%(MIN,n,mx)
def pick_scv(s):
    return '11'+s.box(470,120,545,270)+'{wait:4}'+s.click(184,412)+'{wait:6}'
def build(s,icon,x,y,cost):
    s.step(1, U(cost)+pick_scv(s)+s.click(522,453)+'{wait:8}'+s.click(*icon)+'{wait:8}'+s.click(x,y)+'{wait:4}')
    s.step(350, '')
def idle_to_min(s):   # SCVs standing under the CC -> minerals
    return '11'+s.box(285,240,400,295)+'{wait:4}'+s.rclick(560,200)+'{wait:4}'
DEPOT=(569,375); RAX=(522,410)
def seg2(s):
    build(s,DEPOT,150,110,100)
    s.step(1, U(50)+'1s')
    build(s,RAX,170,260,150)
    s.step(1, U(50)+'1s')
    s.step(200, idle_to_min(s))
    build(s,RAX,330,70,150)
    s.step(1, U(50)+'1s')
    s.step(300, idle_to_min(s))
def seg3(s):
    s.step(1, '11'+s.click(200,275)+'{wait:4}{ctrl+2}'+s.rclick(260,190)+'{wait:4}'+s.click(385,110)+'{wait:4}{ctrl+3}'+s.rclick(260,190)+'{wait:4}')
    for i in range(2):
        s.step(1, U(50)+'1s'); s.step(250,'')
    build(s,DEPOT,100,180,100)
    for i in range(2):
        s.step(1, U(50)+'1s'); s.step(250,'')
    s.step(600, '11'+s.box(100,40,450,310)+'{wait:4}'+s.rclick(560,200)+'{wait:4}')
    for i in range(4):
        s.step(1, U(50)+'2m'); s.step(1, U(50)+'3m'); s.step(150,'')
    build(s,DEPOT,250,40,100)
    for i in range(4):
        s.step(1, U(50)+'2m'); s.step(1, U(50)+'3m'); s.step(150,'')
ENEMY=(20,410)
def seg4(s):
    build(s,DEPOT,90,240,100)
    for i in range(3):
        s.step(1, U(50)+'2m'); s.step(1, U(50)+'3m'); s.step(150,'')
    s.step(1, '11'+s.box(180,110,310,225)+'{wait:4}{ctrl+4}a{wait:4}'+s.click(*ENEMY)+'{wait:4}')
    s.step(1, '2'+s.rclick(*ENEMY)+'{wait:4}3'+s.rclick(*ENEMY)+'{wait:4}')
    for i in range(12):
        s.step(1, U(50)+'2m'); s.step(1, U(50)+'3m'); s.step(250,'')
def seg5(s):
    build(s,DEPOT,440,285,100)
    build(s,DEPOT,110,40,100)
    for i in range(14):
        s.step(1, U(50)+'2m'); s.step(1, U(50)+'3m'); s.step(250,'')
EN2=(15,422)
def attack_all(s):
    return ('4a{wait:4}'+s.click(*EN2)+'{wait:4}11'+s.box(60,60,330,250)+'{wait:4}{shift+4}a{wait:4}'+s.click(*EN2)+'{wait:4}'
            +'2'+s.rclick(*EN2)+'{wait:4}3'+s.rclick(*EN2)+'{wait:4}')
def seg6(s):
    s.step(1, attack_all(s))
    build(s,RAX,95,300,150)
    build(s,RAX,450,40,150)
    for i in range(6):
        for k in range(4):
            s.step(1, U(50)+'2m'); s.step(1, U(50)+'3m'); s.step(250,'')
        s.step(1, attack_all(s))
RALLY=(260,190)
def produce(s, n, gap=200):
    for i in range(n):
        for g in '23': s.step(1, U(50)+g+'{wait:3}m')
        s.step(gap,'')
def wave(s, grp):
    # every marine near the rally point -> group, attack-move to the enemy start (minimap)
    return '{f2}{wait:4}'+s.box(150,110,330,240)+'{wait:4}{ctrl+'+grp+'}a{wait:6}'+s.click(*EN2)+'{wait:4}{f2}{wait:4}'
def seg3b(s):
    s.step(1, '11{shift+f2}'+s.click(200,275)+'{wait:4}{ctrl+2}'+s.rclick(*RALLY)+'{wait:4}'+s.click(385,110)+'{wait:4}{ctrl+3}'+s.rclick(*RALLY)+'{wait:4}')
    s.step(1, U(50)+'1s'); s.step(250,'')
    build(s,RAX,100,180,150)
    s.step(1, U(50)+'1s'); s.step(250,'')
    build(s,DEPOT,250,40,100)
    s.step(600, '{f2}'+s.box(60,40,450,310)+'{wait:4}'+s.rclick(560,200)+'{wait:4}')
    produce(s, 5)
    build(s,DEPOT,110,40,100)
    produce(s, 5)
    build(s,DEPOT,440,285,100)
    produce(s, 3)
def seg4b(s):
    s.step(1, wave(s,'4'))
    for w in range(12):
        produce(s, 4, 250)
        if w % 3 == 1: build(s,DEPOT,[440,330,180,480][w//3 % 4],[230,20,20,120][w//3 % 4],100)
        s.step(1, wave(s,'6'))
        s.step(1, '4a{wait:6}'+s.click(*EN2)+'{wait:4}{f2}')
R1=(110,170); R2=(60,260); R3=(330,70)
RC={'2':(160,190),'3':(95,280),'5':(385,95)}
DEPOTS=[(250,40),(150,110),(40,140),(470,300),(420,260),(300,300),(20,40)]
def bld(s,g,icon,x,y,cost):
    s.step(1, U(cost)+g+'{wait:4}'+s.click(522,453)+'{wait:8}'+s.click(*icon)+'{wait:8}'+s.click(x,y)+'{wait:4}')
    s.step(350,'')
def idle(s):
    return '{f2}'+s.box(285,240,400,300)+'{wait:4}'+s.rclick(560,200)+'{wait:4}'
def seg2d(s):
    s.step(1, '11{shift+f2}'+s.box(470,120,545,270)+'{wait:4}'+s.click(184,412)+'{wait:4}{ctrl+7}'+s.box(470,120,545,270)+'{wait:4}'+s.click(220,412)+'{wait:4}{ctrl+8}')
    bld(s,'7',DEPOT,*DEPOTS[0],100)
    bld(s,'8',RAX,*R1,150)
    s.step(1, U(50)+'1s'); s.step(400, idle(s))
    bld(s,'7',RAX,*R3,150)
    s.step(1, U(50)+'1s'); s.step(400, idle(s))
    s.step(1, U(50)+'1s'); s.step(1200, idle(s))
    bld(s,'8',RAX,*R2,150)
    s.step(1800, idle(s))
    s.step(1, '{f2}'+''.join(s.click(*RC[g])+'{wait:4}{ctrl+'+g+'}'+s.rclick(*RALLY)+'{wait:4}' for g in '235'))
    s.step(300, idle(s))
def prod(s, n, gap=200):
    for i in range(n):
        for g in '235': s.step(1, U(50)+g+'{wait:3}m')
        s.step(gap,'')
def seg3d(s):
    di=1
    for r in range(int(os.environ.get('SCB_ROUNDS','4'))):
        prod(s,3); bld(s,'7' if di%2 else '8',DEPOT,*DEPOTS[di],100); di+=1
    s.step(1, '{f2}'+s.box(150,110,330,240)+'{wait:4}{ctrl+4}a{wait:6}'+s.click(*EN2)+'{wait:4}44')
    for w in range(14):
        for k in range(3):
            s.step(1, '{f2}'); prod(s,1,1); s.step(150,'44')
        if w % 2 == 0 and di < len(DEPOTS): s.step(1,'{f2}'); bld(s,'7' if di%2 else '8',DEPOT,*DEPOTS[di],100); di+=1
        s.step(1, '{f2}'+s.box(150,110,330,240)+'{wait:4}{ctrl+6}a{wait:6}'+s.click(*EN2)+'{wait:4}4a{wait:6}'+s.click(*EN2)+'{wait:4}44')
SEGS=[seg1,seg2d,seg3d]
if __name__=='__main__':
    k=int(sys.argv[1]); s=S(); s.x,s.y=320,240
    if sys.argv[2]!='all':
        tmp=S(); tmp.x,tmp.y=320,240
        for f in SEGS[:k-1]: f(tmp)
        s.x,s.y=tmp.x,tmp.y
    for i,f in enumerate(SEGS[:k] if sys.argv[2]=='all' else [SEGS[k-1]]): f(s); s.save(os.environ.get('SAVEDIR','C:/BrokenNes-work/scbot')+f'/st_s{SEGS.index(f)+1}.bin')
    s.write(sys.argv[3])
