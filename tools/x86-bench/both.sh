#!/bin/bash
# both.sh "CMD1" "CMD2" ... : runs DOS command lines (each > OUTn.TXT) in BrokenNes (x86 console) and DOSBox-X; *.COM/*.EXE here go on the disk.
cd /c/BrokenNes-work/x86_bench_out/t
B=${B:-/c/BrokenNes-work/x86_bench_out/ws_bin/BrokenNes.Workshop.exe}
. /c/BrokenNes-work/x86bench_slot.sh
BS='\'
auto="@ECHO OFF"$'\r\n'"SET DOSDIR=${BS}FREEDOS"$'\r\n'"SET PATH=%DOSDIR%${BS}BIN"$'\r\n'
dbx=''; gets=(); i=0
for c in "$@"; do
  i=$((i+1))
  auto+="$c > C:${BS}OUT$i.TXT"$'\r\n'
  dbx+="$c > OUT$i.TXT"$'\n'
  gets+=(--get "${BS}OUT$i.TXT=bn_OUT$i.TXT")
done
auto+="ECHO ALLDONE"$'\r\n'
printf '%s' "$auto" > auto.bat
puts=(); for f in *.COM *.EXE; do [ -f "$f" ] && puts+=(--put "$f=${BS}$f"); done
"$B" --x86disk --out run.img "${puts[@]}" --autoexec auto.bat >/dev/null
sed '/^\[autoexec\]/,$d' db.conf > dbr.conf; printf '[autoexec]\nmount c .\nc:\n%sexit\n' "$dbx" >> dbr.conf
rm -f bn_OUT*.TXT OUT*.TXT
take; trap give EXIT
"$B" --x86boot --hdd run.img --frames ${FRAMES:-30000} --until ALLDONE "${gets[@]}" $X86ARGS 2>&1 | grep -E "x86boot|Interrupt|STOP|Invalid"
[ -z "$NODBX" ] && timeout 300 /c/BrokenNes-tools/dos/dosbox-x/bin/x64/Release/dosbox-x.exe -defaultconf -conf dbr.conf -nopromptfolder -silent -exit >/dev/null 2>&1
give; trap - EXIT
for j in $(seq 1 $i); do tr -d '\r' < bn_OUT$j.TXT > bn$j.txt 2>/dev/null; tr -d '\r' < OUT$j.TXT > db$j.txt 2>/dev/null; echo "== $j: $(wc -l < bn$j.txt) vs $(wc -l < db$j.txt) lines, $(diff bn$j.txt db$j.txt | grep -c '^<') differ"; done
