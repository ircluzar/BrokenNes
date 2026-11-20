#!/bin/bash
# [UAT/n64] ab30: cold-boot bench of every compat/games.tsv game (40) on the default build: video/audio hash + realtime %.
# Hashes must equal goldens/ab30_expected.tsv (made at the cer64 merge candidate); realtime is informational (noisy).
# usage: UAT/n64/ab30.sh [out file]   FR=frames (default 1800). ROMs via compat/tools/get_rom.sh (N64_ROM_DIR / N64_LIBRARY_DIR).
# Prints one line per game and a final "AB30 PASS n/n" or "AB30 FAIL". UPDATE=1 rewrites the expected file (after an approved change).
. "$(dirname "$0")/lib/n64env.sh"
OUTF=${1:-$N64_WORK/ab30/ab30_$(date +%Y%m%d_%H%M%S).txt}; mkdir -p "$(dirname "$OUTF")"; : > "$OUTF"
EXP="$N64_GOLD/ab30_expected.tsv"; FAIL=0; n=0; ok=0
cd "$N64_BIN" || exit 2
for id in $(cut -f1 "$N64_UAT/compat/games.tsv" | tail -n +2 | tr -d '\r'); do
  rom=$("$N64_UAT/compat/tools/get_rom.sh" "$id" 2>&1 | grep -a "^rom=" | cut -d= -f2-)
  [ -n "$rom" ] || { echo "$id NO-ROM" | tee -a "$OUTF"; FAIL=1; n=$((n+1)); continue; }
  bash "$N64_UAT/wait_free.sh"
  o=$(env COR64_VI_SPEEDUP=1 COR64_PIF_PAK=controller PROBE_LOWEND=1 DOTNET_EnableAVX512F=0 timeout 600 ./SessionProbe.exe "$rom" bench boot ${FR:-1800} 2>/dev/null)
  h=$(echo "$o" | grep -aoE 'video=[0-9A-F]+ audio=[0-9A-F]+' | head -1); rt=$(echo "$o" | grep -aoE 'realtime=[0-9]+%' | head -1)
  want=$(awk -F'\t' -v id="$id" '$1==id {print $2}' "$EXP" 2>/dev/null | tr -d '\r'); n=$((n+1))
  if [ "$UPDATE" = "1" ]; then v=new; elif [ -n "$h" ] && [ "$h" = "$want" ]; then v=ok; ok=$((ok+1)); else v="MISMATCH(want $want)"; FAIL=1; fi
  printf '%s\t%s\t%s\t%s\n' "$id" "$h" "$rt" "$v" | tee -a "$OUTF"
done
if [ "$UPDATE" = "1" ]; then printf 'id\thashes\n' > "$EXP"; awk -F'\t' '{print $1"\t"$2}' "$OUTF" >> "$EXP"; echo "AB30 UPDATED $EXP"; exit 0; fi
[ $FAIL = 0 ] && echo "AB30 PASS $ok/$n" || { echo "AB30 FAIL ($ok/$n match)"; exit 1; }
