#!/bin/bash
# bis2.sh BIN TAG [env...]: hd_r7 cold boot, Yes to the restart prompt, second boot; prints the pixel at 10,10 after each
# NOTE: the hd_r7.img path below is on T: (dead NAS). Repoint to a local copy before running.
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh; B=$1; T=$2
claim_slot "scspeed bis2 $T"
$B --x86boot --machine WIN98-P166 --hdd /t/BrokenDev/Archive/win98_m7_c/hd_r7.img --step "@7000=>\n" --step "@8000=>" --frames 1 --shot b2_$T.png > b2_$T.txt 2>&1
release_slot
python -c "from PIL import Image
for s in (0,1):
  im=Image.open('C:/BrokenNes-work/scspeed/b2_${T}_step%d.png'%s).convert('RGB'); print('$T', s, im.getpixel((10,10)))"
