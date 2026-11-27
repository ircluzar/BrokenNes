#!/bin/bash
# det.sh FRAMES N: N default-JIT runs + 1 X86_NOJIT run from st_sbingame2; md5 of each savestate
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh; P=/c/BrokenNes-work/sc98/pair; F=$1; N=$2
claim_slot "scspeed det"
B=/c/BrokenNes-work/x86speed_bin2/BrokenNes.Workshop.exe
A="--x86boot --machine WIN98-P166 --hdd $P/hd_scpairC_256.img --hdd2 $P/hd_scpairD.img --cd C:/BrokenNes-work/x86_cds/starcraft_xp_cd1.iso --apu SB16_D072 --loadstate $P/st_sbingame2.bin --frames $F"
for i in $(seq $N); do X86_NONIC=1 $B $A --savestate det_j$i.bin >/dev/null 2>&1; echo "jit $i $(md5sum < det_j$i.bin)"; done
X86_NONIC=1 X86_NOJIT=1 $B $A --savestate det_n.bin >/dev/null 2>&1; echo "nojit $(md5sum < det_n.bin)"
release_slot; rm -f det_*.bin
