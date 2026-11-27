#!/bin/bash
# ab.sh STATE FRAMES ROUNDS: interleaved base vs new --frametimes, one heavy slot
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh; S=/c/BrokenNes-work/sc98
claim_slot "scspeed ab"
for r in $(seq $3); do for b in base bin; do
 /c/BrokenNes-work/x86speed_$b/BrokenNes.Workshop.exe --x86boot --machine WIN98-P166 --hdd $S/hd_r7c.img --hdd2 $S/hd_sc_d.img --cd C:/BrokenNes-work/x86_cds/starcraft_xp_cd1.iso --loadstate $1 --frames $2 --frametimes 2>&1 | grep "^frametimes" | sed "s/^/$b r$r: /" | cut -c1-140
done; done
release_slot
