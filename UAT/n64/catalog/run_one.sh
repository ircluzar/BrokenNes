#!/bin/bash
# [UAT/n64] catalog smoke of one library archive (cer64 catalog/run_one.sh): catalog/run_one.sh "<archive name in N64_LIBRARY_DIR>"
# -> $N64_WORK/catalog/runs/<id>/ (report.json, 6 PNGs, probe.log). The ROM is extracted to $N64_WORK/catalog/roms/<id> and deleted after.
# Script: SCRIPT=<file under catalog/> (default nav/<id>.txt when present, else smoke2.txt, as cer64 catalog/run_v2.sh).
# PAK (default controller), DL deadline seconds (default 225), VISP (COR64_VI_SPEEDUP, default 1).
# Pass check (map, not a gate): probe rc=0 and error null (python -I ../compat/check_report.py <run dir>); class is set by eye
# from the PNGs (TITLE / INGAME ...), compare with catalog/CATALOG.tsv.
. "$(dirname "$0")/../lib/n64env.sh"
CAT=$(cd "$(dirname "$0")" && pwd); A="$1"; ROMDIR="$N64_LIBRARY_DIR"; SEVENZ=${SEVENZ:-"/c/Program Files/7-Zip/7z.exe"}
ID=$(echo "$A" | sed -E 's/\.(7z|zip|z64|v64|n64)$//; s/[^A-Za-z0-9]+/_/g; s/^_|_$//g' | cut -c1-48)
OUT="$N64_WORK/catalog/runs${RUNSUFFIX}/$ID"; rm -rf "$OUT"; mkdir -p "$OUT"; TMP="$N64_WORK/catalog/roms/$ID"; rm -rf "$TMP"; mkdir -p "$TMP/x"
echo "$A" > "$OUT/archive.txt"
case "$A" in
  *.7z|*.zip) "$SEVENZ" e -y -o"$(cygpath -w "$TMP/x")" "$(cygpath -w "$ROMDIR/$A")" >/dev/null 2>&1
    SRC=""; for pat in '\(U\).*\[!\]' '\(E\).*\[!\]' '\(J\).*\[!\]' '\[!\]'; do
      SRC=$(ls "$TMP/x"/* | grep -iE '\.(z64|v64|n64)$' | grep -viE 'kiosk|demo|beta|proto' | grep -E "$pat" | head -1); [ -n "$SRC" ] && break; done
    [ -n "$SRC" ] || SRC=$(ls "$TMP/x"/* | grep -iE '\.(z64|v64|n64)$' | grep -viE 'kiosk|demo|beta|proto' | grep -v '\[' | head -1)
    [ -n "$SRC" ] || SRC=$(ls -S "$TMP/x"/* | grep -iE '\.(z64|v64|n64|rom|bin)$' | grep -vE '\[(b|o|h|t|f)[0-9]*\]' | head -1)
    [ -n "$SRC" ] || SRC=$(ls -S "$TMP/x"/* | grep -iE '\.(z64|v64|n64|rom|bin)$' | head -1)
    [ -n "$SRC" ] || SRC=$(ls -S "$TMP/x"/* | head -1) ;;
  *) cp "$ROMDIR/$A" "$TMP/x/"; SRC="$TMP/x/$A" ;;
esac
basename "$SRC" > "$OUT/dump.txt"
python -I "$CAT/conv.py" "$(cygpath -m "$SRC")" "$(cygpath -m "$TMP/rom.z64")" > "$OUT/header.txt" 2>&1
rm -rf "$TMP/x"
SCR="$CAT/${SCRIPT:-nav/$ID.txt}"; [ -f "$SCR" ] || SCR="$CAT/smoke2.txt"; echo "${SCR#$CAT/}" > "$OUT/script.txt"
cd "$OUT"
COR64_PIF_PAK=${PAK:-controller} COR64_VI_SPEEDUP=${VISP:-1} COMPAT_DEADLINE_SECONDS=${DL:-225} COR64_COMPAT_LOG=1 timeout $((${DL:-225}+15)) "$N64_BIN/SessionProbe.exe" "$(cygpath -m "$TMP/rom.z64")" compat "$(cygpath -m "$SCR")" "$(cygpath -m "$OUT")" > probe.log 2>&1; echo "rc=$?" >> probe.log
rm -rf "$TMP" "$OUT"/*.state "$OUT"/DmaDumps
echo "$ID $(tail -1 probe.log)"
