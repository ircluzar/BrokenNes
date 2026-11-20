#!/bin/bash
# [UAT/n64] SM64 bit-exact gate for the N64 core (cer64plus climb/gate.sh, run from a BrokenNes checkout).
# Checks: cold-boot hash, castle + title goldens (frames and audio), low-end castle video hash. KROM=1 adds the krom RSP suite.
# usage: UAT/n64/gate.sh ["env for all runs", e.g. COR64_X64=0]      BUILD=1 runs UAT/n64/build.sh first.
# Prints "boot ok" ... and a final "GATE PASS" (exit 0) only when everything matches. Deterministic, so load does not matter
# (but run one emulator at a time: see wait_free.sh). Output: $N64_WORK/gate/.
. "$(dirname "$0")/lib/n64env.sh"
. "$N64_UAT/lib/probe_lock.sh"; probe_lock
E=${1:-X=1}; FAIL=0; G="$N64_WORK/gate"; mkdir -p "$G"
[ "$BUILD" = "1" ] && { "$N64_UAT/build.sh" || exit 2; }
[ -f "$N64_SM64_ROM" ] || { echo "no SM64 ROM: set N64_SM64_ROM or sm64Rom in UAT/n64/n64.local.json ($N64_SM64_ROM)"; exit 2; }
for x in "$N64_BIN/SessionProbe.exe" "$N64_BIN/RunN64.exe" "$N64_BIN_RT/SessionProbe.exe" "$N64_BIN/st/castle.state" "$N64_BIN/st/title.state" "$N64_BIN_RT/st/castle.state"; do
  [ -f "$x" ] || { echo "missing $x (run UAT/n64/build.sh; states come from N64_STATE_DIR)"; exit 2; }; done
R=$(cygpath -m "$N64_SM64_ROM"); read -r BOOT_OK < "$N64_GOLD/boot.txt"; read -r LOW_OK < "$N64_GOLD/lowend_castle.txt"
b=$(cd "$N64_BIN" && env $E ./SessionProbe.exe "$R" bench boot 2400 2>/dev/null | grep -ao "video=[0-9A-F]* audio=[0-9A-F]*")
[ "$b" = "$BOOT_OK" ] && echo "boot ok" || { echo "BOOT MISMATCH: $b (want $BOOT_OK)"; FAIL=1; }
for g in castle title; do
  rm -rf "$G/golden_$g"; "$N64_UAT/golden.sh" $g.state 60 "$G/golden_$g" $E >/dev/null 2>&1
  out=$(python -I "$N64_UAT/golden_cmp.py" "$N64_GOLD/ref4_$g" "$G/golden_$g" 2>&1 | tail -2 | tr '\n' ' ')
  echo "$g: $out"; echo "$out" | grep -q "identical: True" && echo "$out" | grep -q "mismatched 0" || FAIL=1
done
c=$(cd "$N64_BIN_RT" && env PROBE_LOWEND=1 DOTNET_EnableAVX512F=0 $E ./SessionProbe.exe "$R" bench st/castle.state 600 2>/dev/null | grep -ao "video=[0-9A-F]*")
[ "$c" = "$LOW_OK" ] && echo "lowend castle ok" || { echo "LOWEND CASTLE MISMATCH: $c (want $LOW_OK)"; FAIL=1; }
if [ "$KROM" = "1" ]; then
  KROM_ENV="COR64_RSP_REC_AFTER=1 COR64_RSP_REC_BG=0 $E" "$N64_UAT/krom_suite.sh" 12 "$G/krom_rsp" > "$G/krom.txt" 2>&1
  np=$(grep -c " PASS " "$G/krom.txt"); want=$(grep -c . "$N64_GOLD/krom_expected_pass.txt")
  echo "krom RSP: $np pass ($want expected; the 2 DMAAlignment-SP fails are known)"
  grep -E " FAIL | NO-IMAGE" "$G/krom.txt" | grep -vi dmaalign && FAIL=1
  for t in $(cat "$N64_GOLD/krom_expected_pass.txt" | tr -d '\r'); do grep -q "^$t  *PASS" "$G/krom.txt" || { echo "krom: $t did not pass"; FAIL=1; }; done
fi
[ $FAIL = 0 ] && echo "GATE PASS" || { echo "GATE FAIL"; exit 1; }
