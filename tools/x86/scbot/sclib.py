# StarCraft step-script helpers for x86boot --stepsfile (absolute mouse via homing; Win98 PS/2 = 2 px per count)
class S:
    def __init__(s): s.lines=[]; s.x=None; s.y=None
    def step(s, wait, keys): s.lines.append(f"@{wait}=>{keys}")
    def home(s):
        s.x=s.y=0; return "{m:-100,-100}"*5
    def mv(s, x, y):
        out = s.home() if s.x is None else ""
        dx=(x-s.x)//2; dy=(y-s.y)//2
        while dx or dy:
            cx=max(-50,min(50,dx)); cy=max(-50,min(50,dy)); out+=f"{{m:{cx},{cy}}}"; dx-=cx; dy-=cy
        s.x+=((x-s.x)//2)*2; s.y+=((y-s.y)//2)*2; return out
    def click(s, x, y, btn=1, hold=3):
        return s.mv(x,y)+f"{{m:0,0,{btn}}}{{wait:{hold}}}{{m:0,0,0}}"
    def rehome(s): s.x=None; return ""
    def save(s, p): s.lines.append(f"@1=>{{save:{p}}}")
    def write(s, p): open(p,"w").write("\n".join(s.lines)+"\n")
def _held(mv):
    return ''.join(('{m:%s,1}'%p[3:-1]) for p in mv.replace('}{','}|{').split('|') if p)
def box(s,x0,y0,x1,y1):
    return s.mv(x0,y0)+'{m:0,0,1}{wait:3}'+_held(s.mv(x1,y1))+'{wait:3}{m:0,0,0}{wait:2}'
def rclick(s,x,y): return s.click(x,y,btn=2)
S.box=box; S.rclick=rclick
