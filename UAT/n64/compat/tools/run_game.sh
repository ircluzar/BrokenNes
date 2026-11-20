#!/bin/bash
# [UAT/n64] compat/tools/run_game.sh <id> [script]  ->  $N64_WORK/compat/<id>/<timestamp>/ (report.json, PNGs, probe.log)
# Runs SessionProbe's compat mode on one game. Safe to run several at once: every run has its own folder and the probe
# runs with that folder as its working directory (no shared config.json, telemetry/ or temp files).
# Env:
#   BUILD=<dir with SessionProbe.exe>  the build to test (default N64_BIN, <Cor64Root>/bin from UAT/n64/build.sh)
#   RESULTS=<dir>                      results root (default $N64_WORK/compat)
#   COMPAT_TWICE=1                     rerun the script in a fresh session and compare hashes (determinism)
#   COR64_RDP=software|parallel        renderer, as for the probe (default Auto)
#   COR64_VI_SPEEDUP                   default 1 = real video timing for certification (user decision 2026-11-11)
#   TAG=<text>                         appended to the timestamp folder name
#   SPEED=1                            after the run, if the script wrote "savestate speed" (a steady gameplay point),
#                                      bench it on the low-end profile (PROBE_LOWEND=1 DOTNET_EnableAVX512F=0, real-timing
#                                      build SPEED_BUILD, default <BUILD>/../bin_rt, made by lowend.sh) for SPEED_FRAMES
#                                      (600) frames after a 120-frame warm-up; adds "speed" to report.json (speed.log)
set -e
C=$(cd "$(dirname "$0")/.." && pwd); ID="$1"
. "$C/../lib/n64env.sh"
[ -n "$ID" ] || { echo "usage: run_game.sh <id> [script]" >&2; exit 2; }
SCRIPT=${2:-"$C/scripts/$ID.txt"}; [ -f "$SCRIPT" ] || SCRIPT="$C/scripts/_boot.txt"
BUILD=${BUILD:-"$N64_BIN"}; EXE="$BUILD/SessionProbe.exe"
[ -x "$EXE" ] || { echo "no SessionProbe.exe in $BUILD" >&2; exit 2; }
TS=$(date +%Y%m%d_%H%M%S)_$$${TAG:+_$TAG}
OUT="${RESULTS:-$N64_WORK/compat}/$ID/$TS"; mkdir -p "$OUT"
"$C/tools/get_rom.sh" "$ID" > "$OUT/header.txt" 2>&1 || { cat "$OUT/header.txt"; exit 2; }
ROM=$(grep -a "^rom=" "$OUT/header.txt" | head -1 | cut -d= -f2-)
cp "$SCRIPT" "$OUT/script.txt"
echo "$EXE" > "$OUT/build.txt"; (cd "$BUILD/.." && git rev-parse --short HEAD 2>/dev/null >> "$OUT/build.txt") || true
W() { cygpath -m "$1"; }
cd "$OUT"; rc=0
export COR64_VI_SPEEDUP=${COR64_VI_SPEEDUP:-1}
# COR64_COMPAT_LOG=1: record unimplemented-feature use (logging only, emulation unchanged)
# COR64_VI_SPEEDUP=1 (default here, lane scripts_speed): real VI period (1.5625M CPU ticks per field). The default build's
# FASTER_VI hack gives the CPU 1/11 of that, so CPU-heavy games run their logic at a fraction of real pace (sf64's
# launch cutscene took 2+ minutes); scripts are timed for real pace. Equal to a Cor64FasterVi=false build (bin_rt).
COR64_VI_SPEEDUP=${COR64_VI_SPEEDUP:-1} COMPAT_DEADLINE_SECONDS=$(( ${TIMEOUT:-1800} - 30 )) COR64_COMPAT_LOG=1 timeout ${TIMEOUT:-1800} "$EXE" "$ROM" compat "$(W "$SCRIPT")" "$(W "$OUT")" > probe.log 2>&1 || rc=$?
echo "rc=$rc" >> probe.log
tail -1 probe.log | grep -q "^rc=0" && grep "^COMPAT" probe.log || { echo "$ID: probe exit $rc (see $OUT/probe.log)"; grep "^COMPAT" probe.log || true; }
if [ "$SPEED" = "1" ] && [ -f "$OUT/speed.state" ]; then
  SB=${SPEED_BUILD:-"$N64_BIN_RT"}
  if [ -x "$SB/SessionProbe.exe" ]; then
    mkdir -p "$OUT/speed_run"; ( cd "$OUT/speed_run" && PROBE_LOWEND=1 DOTNET_EnableAVX512F=0 timeout 900 "$SB/SessionProbe.exe" "$ROM" bench "$(W "$OUT/speed.state")" ${SPEED_FRAMES:-600} > "$OUT/speed.log" 2>&1 ) || true
    python -I "$C/tools/speed_merge.py" "$OUT" ${SPEED_FRAMES:-600} "$SB" || true
  else echo "SPEED: no SessionProbe.exe in $SB (run UAT/n64/build.sh)"; fi
fi
echo "$OUT"
