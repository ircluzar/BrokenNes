#!/bin/bash
# duo.sh NAME "ARGS A" "ARGS B": two x86boot processes (A hosts the UDP LAN on 127.0.0.1:PORT, B joins), both heavy slots; out NAME_a/NAME_b
cd /c/BrokenNes-work/sclan; . /c/BrokenNes-work/sc98/slot.sh
name=$1; A=$2; B=$3; PORT=${PORT:-27600}
claim_slot "sclan duo $name A"; S1=$SLOT; T1=$TOK
claim_slot "sclan duo $name B"; S2=$SLOT; T2=$TOK
X=C:/BrokenNes-work/scbug/gbin/BrokenNes.Workshop.exe
eval "$X --x86boot --machine WIN98-P166 --shot ${name}_a.png --lan host:$PORT $A" > ${name}_a.txt 2>&1 &
pa=$!; sleep ${DELAY:-3}
eval "$X --x86boot --machine WIN98-P166 --shot ${name}_b.png --lan join:127.0.0.1:$PORT $B" > ${name}_b.txt 2>&1
wait $pa
sh C:/BrokenNes-work/heavylock.sh release $S1 $T1; sh C:/BrokenNes-work/heavylock.sh release $S2 $T2
