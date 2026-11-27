import sys; sys.path.insert(0,'.'); from sclib import S
s=S(); a=sys.argv
s.step(5, s.box(285,240,400,285)+'{wait:4}'+s.rclick(560,200))   # all SCVs mine
s.step(10, s.click(350,190)+'{ctrl+1}s')                         # CC -> group 1, train SCV
for i in range(int(a[2]) if len(a)>2 else 3): s.step(300, '1s')
s.step(20, s.mv(320,240))
s.write(a[1])
