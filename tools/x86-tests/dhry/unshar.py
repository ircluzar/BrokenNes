import re,sys,os
src,out=sys.argv[1],sys.argv[2]; os.makedirs(out,exist_ok=True)
lines=open(src,encoding='latin1').read().split('\n'); i=0
while i<len(lines):
    m=re.match(r"sed >(\S+) <<'(//GO.SYSIN DD \S+)' 's/\^-//'",lines[i])
    if m:
        name,term=m.group(1),m.group(2); body=[]; i+=1
        while lines[i]!=term: body.append(lines[i][1:] if lines[i].startswith('-') else lines[i]); i+=1
        assert '/' not in name and '..' not in name
        open(os.path.join(out,name),'w',encoding='latin1',newline='\n').write('\n'.join(body)+'\n'); print(name,len(body))
    i+=1
