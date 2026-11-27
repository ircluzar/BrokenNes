#!/bin/bash
# tog.sh FRAMES ENVSPEC...: for each "VAR=1[,VAR2=1]" run JITSYNC from st_sbingame2 FRAMES frames, print the Cycle at the end
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh; P=/c/BrokenNes-work/sc98/pair; F=$1; shift
claim_slot "scspeed tog"
for spec in "$@"; do
  envs=$(echo "$spec" | tr ',' ' ')
  env X86_NONIC=1 X86_JITSYNC=1 $envs /c/BrokenNes-work/x86speed_bin2/BrokenNes.Workshop.exe --x86boot --machine WIN98-P166 --hdd $P/hd_scpairC_256.img --hdd2 $P/hd_scpairD.img --cd C:/BrokenNes-work/x86_cds/starcraft_xp_cd1.iso --apu SB16_D072 --loadstate $P/st_sbingame2.bin --frames $F --savestate tog.bin > /dev/null 2>&1
  echo "$spec $(od -A n -t d8 -j 250 -N 8 tog.bin)"
done
release_slot; rm -f tog.bin
