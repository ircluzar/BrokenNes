#!/bin/bash
# [UAT/n64] Run any ROM in RunN64 for N seconds; copies the last framebuffer PNG to $3 (optional). (cer64plus run_rom.sh)
. "$(dirname "$0")/lib/n64env.sh"
ROM="$(realpath "$1")"; SECS=${2:-12}; OUT=$(realpath -m "${3:-}")
cd "$N64_BIN" || exit 2
printf '{"useInterpreter":true,"romFile":"%s"}' "$(cygpath -m "$ROM")" > config.json
rm -rf telemetry
COR64_EVENTLOG=${EVENTS:-1} COR64_AUDIO_OUT=0 COR64_WAV=${WAV:-0} COR64_NO_HLE=1 COR64_SHOT_EVERY=${SHOT:-5} timeout "$SECS" ./RunN64.exe > last_run_console.txt 2>&1
if [ -n "$3" ]; then L=$(ls telemetry/fb_*.png 2>/dev/null | tail -1); [ -n "$L" ] && cp "$L" "$OUT"; fi
