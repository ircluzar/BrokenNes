#!/bin/bash
# abx.sh LABEL ROUNDS ARGS...: interleaved base vs bin --frametimes with any x86boot args, one heavy slot
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh; L=$1; R=$2; shift 2
claim_slot "scspeed abx $L"
for r in $(seq $R); do for b in ${B1:-base} ${B2:-bin}; do
 /c/BrokenNes-work/x86speed_$b/BrokenNes.Workshop.exe --x86boot --machine WIN98-P166 "$@" --frametimes 2>&1 | grep "^frametimes" | sed "s/^/$L $b r$r: /" | cut -c1-120
done; done
release_slot
