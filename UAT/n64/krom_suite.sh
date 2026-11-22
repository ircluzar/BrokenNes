#!/bin/bash
# [UAT/n64] krom RSP suite (cer64plus krom_suite.sh): run every ROM under N64_KROM_DIR that has a reference PNG in
# goldens/krom (same relative path), grade the last frame by the PASS/FAIL column. Usage: krom_suite.sh [secs] [outdir]
. "$(dirname "$0")/lib/n64env.sh"
SECS=${1:-12}; OUT=$(realpath -m "${2:-$N64_WORK/krom_rsp}"); mkdir -p "$OUT"
[ -d "$N64_KROM_DIR" ] || { echo "no krom folder (set N64_KROM_DIR to krom's RSPTest folder)"; exit 2; }
cd "$N64_KROM_DIR"
for rom in $(find . -iname "*.N64" | sort); do
  rel=${rom#./}; name=$(basename "$rom" .N64); ref="$N64_GOLD/krom/${rel%.N64}.png"
  [ -f "$ref" ] || continue
  EVENTS=0 SHOT=2 env $KROM_ENV "$N64_UAT/run_rom.sh" "$N64_KROM_DIR/$rel" "$SECS" "$OUT/$name.png" >/dev/null 2>&1
  res=$(python -I "$N64_UAT/png_diff.py" "$OUT/$name.png" "$ref" "$name")
  # [n64-revive] NO-IMAGE = no frame within the wall-clock SECS (a loaded HD 630 fleet box): retry once with 3x the time
  # (KROM_RETRY=0 off). A real failure still shows as FAIL; only a missing picture is retried.
  if [ "${KROM_RETRY:-1}" != 0 ] && echo "$res" | grep -q "NO-IMAGE"; then
    rm -f "$OUT/$name.png"
    EVENTS=0 SHOT=2 env $KROM_ENV "$N64_UAT/run_rom.sh" "$N64_KROM_DIR/$rel" "$((SECS * 3))" "$OUT/$name.png" >/dev/null 2>&1
    res=$(python -I "$N64_UAT/png_diff.py" "$OUT/$name.png" "$ref" "$name"); echo "$res" | grep -q " PASS " && res="$res (retry)"
  fi
  echo "$res"
done
