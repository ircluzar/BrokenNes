#!/bin/bash
# nav/batch.sh <ids file> <script or "own"> <suffix dir>: serial runs; copies results to runs_v2_nav_<suffix>/<id>
CAT=$(cd "$(dirname "$0")/.." && pwd)
while read -r id; do [ -z "$id" ] && continue
  S="$2"; [ "$S" = own ] && S="nav/$id.txt"
  bash "$CAT/nav/run.sh" "$id" "$S"; mkdir -p "$CAT/runs_v2_nav_$3"; rm -rf "$CAT/runs_v2_nav_$3/$id"; mv "$CAT/runs_v2_nav/$id" "$CAT/runs_v2_nav_$3/$id"
done < "$1"
