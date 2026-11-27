#!/bin/bash
# run.sh K : run segment K from st_s(K-1) (st_tvz0 for K=1), shots segK*.png
cd /c/BrokenNes-work/scbot; . ./env.sh; K=$1; ST=st_s$((K-1)).bin; [ $K = 1 ] && ST=st_tvz0.bin
python -I seg.py $K one seg$K.steps
SLOT_OWNER=scbot claim_slot seg$K; t0=$(date +%s)
X86_NONIC=1 timeout 3000 $G "${SCB[@]}" --loadstate $ST --stepsfile seg$K.steps --frames 0 --shot seg$K.png > seg$K.log 2>&1
release_slot; echo "seg$K rc=$? $(( $(date +%s)-t0 )) s"; grep step seg$K.log | tail -2
