#!/bin/bash
# [UAT/n64] envab: interleaved speed A/B of env settings on one game (cold-boot bench, hashes printed so a change in behaviour shows).
# usage: UAT/n64/envab.sh "<archive in N64_LIBRARY_DIR | sm64>" "<arm env A>" "<arm env B>" ...   (arm "-" = no extra env)
# REPS (default 3) rounds of every arm in turn, FR frames (default 3600). One line per run: AB <game> <arm> realtime=% video= audio=
. "$(dirname "$0")/lib/n64env.sh"
. "$N64_UAT/lib/probe_lock.sh"; probe_lock
A="$1"; shift; SEVENZ=${SEVENZ:-"/c/Program Files/7-Zip/7z.exe"}
ID=$(echo "$A" | sed -E 's/\.(7z|zip|z64|v64|n64)$//; s/[^A-Za-z0-9]+/_/g; s/^_|_$//g' | cut -c1-40)
TMP="$N64_WORK/envab/$ID"; rm -rf "$TMP"; mkdir -p "$TMP/x"
if [ "$A" = "sm64" ]; then ROM="$N64_SM64_ROM"; else
  "$SEVENZ" e -y -o"$(cygpath -w "$TMP/x")" "$(cygpath -w "$N64_LIBRARY_DIR/$A")" >/dev/null 2>&1
  SRC=""; for pat in '\(U\).*\[!\]' '\(E\).*\[!\]' '\[!\]'; do
    SRC=$(ls "$TMP/x"/* | grep -iE '\.(z64|v64|n64)$' | grep -viE 'kiosk|demo|beta|proto' | grep -E "$pat" | head -1); [ -n "$SRC" ] && break; done
  [ -n "$SRC" ] || SRC=$(ls -S "$TMP/x"/* | grep -iE '\.(z64|v64|n64)$' | head -1)
  python -I "$N64_UAT/catalog/conv.py" "$(cygpath -m "$SRC")" "$(cygpath -m "$TMP/rom.z64")" >/dev/null 2>&1; rm -rf "$TMP/x"; ROM="$TMP/rom.z64"
fi
cd "$N64_BIN" || exit 2
for r in $(seq 1 ${REPS:-3}); do
  for arm in "$@"; do
    e=""; [ "$arm" = "-" ] || e="$arm"
    o=$(env $e COR64_VI_SPEEDUP=1 COR64_PIF_PAK=controller PROBE_LOWEND=1 DOTNET_EnableAVX512F=0 timeout 900 ./SessionProbe.exe "$(cygpath -m "$ROM")" bench boot ${FR:-3600} 2>/dev/null)
    echo "AB $ID [$arm] r$r $(echo "$o" | grep -aoE 'realtime=[0-9]+%' | head -1) $(echo "$o" | grep -aoE 'video=[0-9A-F]+ audio=[0-9A-F]+' | head -1)"
  done
done
rm -rf "$TMP"
