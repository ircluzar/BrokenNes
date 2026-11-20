#!/bin/bash
# [UAT/n64] Deterministic oracle (cer64plus golden.sh): load a save state in RunN64, run <secs> of wall time, record audio + frame hashes.
# Usage: golden.sh <state> <secs> <outdir> [extra env...]
. "$(dirname "$0")/lib/n64env.sh"
STATE=$1; SECS=$2; OUT=$(realpath -m "$3"); shift 3
cd "$N64_BIN" || exit 2
printf '{"useInterpreter":true,"romFile":"%s"}' "$(cygpath -m "$N64_SM64_ROM")" > config.json
rm -rf telemetry; mkdir -p telemetry "$OUT"
env "$@" COR64_LOAD="st/$STATE" COR64_WAV=300 COR64_FRAMEHASH_EVERY=10 COR64_AUDIO_OUT=0 COR64_NO_HLE=1 timeout "$SECS" ./RunN64.exe > "$OUT/console.txt" 2>&1
cp telemetry/audio.wav telemetry/framehash.txt telemetry/telemetry.jsonl "$OUT/" 2>/dev/null
python -I - "$OUT" <<'PY'
import json,sys
rows=[json.loads(l) for l in open(sys.argv[1]+'/telemetry.jsonl') if l.startswith('{"t"')]
s=[r['speedPct'] for r in rows if r['t']>=10]
print('speedPct(t>=10): %.2f  frames hashed: %d'%(sum(s)/max(1,len(s)), sum(1 for _ in open(sys.argv[1]+'/framehash.txt'))))
PY
