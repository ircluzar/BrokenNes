#!/bin/bash
# catalog/nav/run.sh <id> [script]: run catalog/nav/<id>.txt (default) at the user default COR64_VI_SPEEDUP=11 -> catalog/runs_v2_nav/<id>
# Waits for >16 GB free RAM; one emulator at a time (run serially). ROM deleted after (run_v2.sh).
CAT=$(cd "$(dirname "$0")/.." && pwd); ID="$1"; S="${2:-nav/$ID.txt}"
A=$(while read -r a; do i=$(echo "$a" | sed -E 's/\.(7z|zip|z64|v64|n64)$//; s/[^A-Za-z0-9]+/_/g; s/^_|_$//g' | cut -c1-48); [ "$i" = "$ID" ] && echo "$a"; done < "$CAT/list.txt" | head -1)
[ -n "$A" ] || { echo "no archive for $ID"; exit 2; }
while :; do f=$(powershell -NoProfile -c "[int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB)"); [ "${f%%[^0-9]*}" -gt 16 ] && break; sleep 20; done
SCRIPT="$S" VISP=${VISP:-11} DL=${DL:-225} RUNSUFFIX=_nav bash "$CAT/run_v2.sh" "$A"
