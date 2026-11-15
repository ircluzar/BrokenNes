#!/bin/bash
# loadtest.sh <PC> <N> <outdir>: N concurrent bridge sessions on one PC, 40 play_steps each (SMB1 + Mesen reference); prints step ms
cd "$(dirname "$0")"; PC=$1; N=$2; OUT=$3; mkdir -p "$OUT"
one() {
  local S P t0 t1
  S=$(node mcp.mjs start --pc $PC --max-steps 100); P=$(echo "$S" | sed 's/.*"port":\([0-9]*\).*/\1/')
  bn(){ node mcp.mjs --port $P --out "$OUT/shots$1" "${@:2}"; }
  t0=$(date +%s)
  bn $1 open_rom '{"path":"X:/EMULATION/Nintendo Entertainment System/Super Mario Bros. (JU) (PRG0) [!].nes","checks":true}' >/dev/null
  bn $1 reference '{"action":"open","align_frames":240}' >/dev/null
  bn $1 record '{"action":"start"}' >/dev/null
  for i in $(seq 1 40); do
    b=$(( i % 4 )); case $b in 0) B='["Start"]';; 1) B='["Right","B"]';; 2) B='["Right","A"]';; 3) B='[]';; esac
    bn $1 play_step "{\"buttons\":$B,\"frames\":20}" | grep -oE '"ms": [0-9]+' | tail -1 >> "$OUT/ms_$1.txt"
  done
  bn $1 record "{\"action\":\"stop\",\"path\":\"$OUT/t$1.bnm\"}" >/dev/null
  bn $1 close_session '{}' >/dev/null; node mcp.mjs --port $P stop >/dev/null
  t1=$(date +%s); echo "session $1: $((t1-t0)) s, mean step ms $(awk '{s+=$2;n++} END {print int(s/n)}' "$OUT/ms_$1.txt")"
}
for k in $(seq 1 $N); do one $k & done; wait
