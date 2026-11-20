#!/bin/bash
# nav/try.sh <id> <step file> [tag]: prefix nav/pre/<id>.txt (minus savestate) + step -> run at VISP 11; results runs_v2_nav_try/<id>
D=$(cd "$(dirname "$0")" && pwd); id=$1
{ grep -v '^savestate' "$D/pre/$id.txt"; cat "$2"; } > "$D/_try_$id.txt"
bash "$D/run.sh" "$id" "nav/_try_$id.txt"; mkdir -p "$D/../runs_v2_nav_try"; rm -rf "$D/../runs_v2_nav_try/$id"; mv "$D/../runs_v2_nav/$id" "$D/../runs_v2_nav_try/$id"
