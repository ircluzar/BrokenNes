#!/bin/bash
# abenv.sh ROUNDS "ENV-A" "ENV-B" ARGS...: interleaved runs of the same build with two env settings
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh; R=$1; EA=$2; EB=$3; shift 3
claim_slot "scspeed abenv"
for r in $(seq $R); do for e in "$EA" "$EB"; do
 env $e /c/BrokenNes-work/x86speed_bin/BrokenNes.Workshop.exe --x86boot --machine WIN98-P166 "$@" --frametimes 2>&1 | grep "^frametimes" | sed "s/^/[$e] r$r: /" | cut -c1-120
done; done
release_slot
