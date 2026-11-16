#!/bin/bash
# dosbench.sh WORKSHOP_EXE OUTDIR [IPS...]: runs Dhrystone 2.1 (DHRY16/DHRY386, built by dhry/build.ps1) and TOPBENCH /i on the headless
# x86 console (FreeDOS disk, --x86boot) at each instruction rate (default 60000000 = the desktop console's), then compares the scores
# with real PCs: TopBench's own DATABASE.INI (scores measured on real hardware) via tbnear.py.
# Inputs come from C:\BrokenNes-tools\x86\tests (see SOURCES.md there). Machine time is the instruction clock, so scores are deterministic.
B=$1; OUT=$2; shift 2; IPS=${@:-60000000}
T=C:/BrokenNes-tools/x86/tests; HERE=$(cd "$(dirname "$0")" && pwd)
RUNS=${RUNS:-2000000}
mkdir -p "$OUT"; cd "$OUT"
BS='\'
printf '%s\r\n' "$RUNS" > N.TXT
printf '%s\r\n' "@ECHO OFF" "SET DOSDIR=${BS}FREEDOS" "SET PATH=%DOSDIR%${BS}BIN" \
  "DHRY16 < N.TXT > D16.TXT" "DHRY386 < N.TXT > D386.TXT" "TOPBENCH /i" "ECHO ALLDONE" > auto.bat
"$B" --x86disk --out bench.img --put "$T/dhrystone/build/DHRY16.EXE=${BS}DHRY16.EXE" --put "$T/dhrystone/build/DHRY386.EXE=${BS}DHRY386.EXE" \
  --put "N.TXT=${BS}N.TXT" --put "$T/topbench/x/TOPBENCH.EXE=${BS}TOPBENCH.EXE" --put "$T/topbench/x/DATABASE.INI=${BS}DATABASE.INI" \
  --autoexec auto.bat > /dev/null || exit 1
for ips in $IPS; do
  rm -f bn_*_$ips.TXT
  "$B" --x86boot --hdd bench.img --ips $ips --frames ${FRAMES:-60000} --until ALLDONE --get "${BS}D16.TXT=bn_D16_$ips.TXT" --get "${BS}D386.TXT=bn_D386_$ips.TXT" > boot_$ips.txt 2>&1
  grep -E "^x86boot" boot_$ips.txt
  for f in D16 D386; do echo "== $f @ $ips: $(tr -d '\r' < bn_${f}_$ips.TXT | grep -E 'Dhrystones per Second|Microseconds|too small')"; done
  echo "== TOPBENCH @ $ips (it writes to the screen, not stdout):"; grep -E "Score:|Memory:|addressing:|exercise:|adapter speed:" boot_$ips.txt
  py=${PY:-python}; $py -I "$HERE/tbnear.py" "$T/topbench/x/DATABASE.INI" boot_$ips.txt bn_D16_$ips.TXT bn_D386_$ips.TXT
done
