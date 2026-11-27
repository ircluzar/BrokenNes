#!/bin/bash
# g2seg.sh K [TAG] : run segment K on Garage2 from local st_s(K-1).bin; results -> st_sK.bin + segK*.png here
cd /c/BrokenNes-work/scbot; K=$1; TAG=${2:-scbot_s$K_$(date +%H%M%S)}; ST=st_s$((K-1)).bin; [ $K = 1 ] && ST=st_tvz0.bin
SAVEDIR=C:/REMOTE_WORK/runs/$TAG python -I seg.py $K one seg$K.steps || exit 1
cd /c/BrokenNes-work/g2
./g2run.sh run garage2 $TAG scbot1 "--x86boot --machine WIN98-P166 --hdd {D}\sc98\hd_scpairC_256.img --hdd2 {D}\sc98\hd_scpairD.img --cd {D}\sc98\starcraft_xp_cd1.iso --apu SB16_D072 --loadstate {O}/in/$ST --stepsfile {O}/in/seg$K.steps --frames 0 --shot {O}\seg$K.png" /c/BrokenNes-work/scbot/$ST /c/BrokenNes-work/scbot/seg$K.steps
R=results/$TAG; ls $R | head -3
cp $R/st_s$K.bin /c/BrokenNes-work/scbot/ 2>/dev/null; cp $R/seg$K*.png /c/BrokenNes-work/scbot/ 2>/dev/null; cp $R/run.txt /c/BrokenNes-work/scbot/seg$K.log
echo "seg$K done $(ls -la /c/BrokenNes-work/scbot/st_s$K.bin 2>&1)"
