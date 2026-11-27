#!/bin/bash
# r.sh NAME ARGS...: claim heavy slot, run x86boot from my build (copied), out NAME.txt/png
cd /c/BrokenNes-work/scspeed; . /c/BrokenNes-work/sc98/slot.sh
name=$1; shift
claim_slot "scspeed $name"
/c/BrokenNes-work/x86speed_bin/BrokenNes.Workshop.exe --x86boot --machine WIN98-P166 --shot $name.png "$@" > $name.txt 2>&1; rc=$?
release_slot; exit $rc
