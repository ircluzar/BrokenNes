#!/bin/bash
# bis2.sh BIN TAG [env...]: hd_r7 cold boot, Yes to the restart prompt, second boot; prints the pixel at 10,10 after each
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh; B=$1; T=$2
claim_slot "scspeed bis2 $T"
$B --x86boot --machine WIN98-P166 --hdd /c/BrokenNes-work/sc98/hd_r7b.img --step "@7000=>\n" --step "@8000=>" --frames 1 --shot b2_$T.png > b2_$T.txt 2>&1
release_slot
python -c "from PIL import Image
for s in (0,1):
  im=Image.open('C:/BrokenNes-work/scspeed/b2_${T}_step%d.png'%s).convert('RGB'); print('$T', s, im.getpixel((10,10)))"
