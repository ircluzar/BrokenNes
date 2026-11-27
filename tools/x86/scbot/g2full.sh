#!/bin/bash
# g2full.sh TAG STEPS : full bot run from st_sbmenu on Garage2, player-view MP4 (48 kHz) + end shot
cd /c/BrokenNes-work/g2
./g2run.sh run garage2 $1 scbot2 "--x86boot --machine WIN98-P166 --hdd {D}\sc98\hd_scpairC_256.img --hdd2 {D}\sc98\hd_scpairD.img --cd {D}\sc98\starcraft_xp_cd1.iso --apu SB16_D072 --loadstate {D}\sc98\st_sbmenu.bin --frames 300 --stepsfile {O}/in/$(basename $2) --mp4 {O}/$1.mp4 --mp4rate 48000 --shotat 1=x.png --shot {O}/end.png --savestate {O}/end.bin" /c/BrokenNes-work/scbot/$2
ls -la results/$1 | grep -v step
