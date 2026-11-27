#!/bin/bash
# gate: SST 80386, test386 f3/f4, battery pc (no scen/fuzz) on the X86-02 worktree; one heavy slot (heavylock.sh). CPUONLY=1: no battery
cd /c/BrokenNes-work/sc98; . ./slot.sh
W=C:/BrokenNes-work/wt-x86speed; G=C:/BrokenNes-work/scspeed/gbin; T=C:/BrokenNes-tools/x86/tests; O=C:/BrokenNes-work/scspeed/gate; mkdir -p $O
claim_slot gate
cd $W && dotnet build Workshop/BrokenNes.Workshop.csproj -c Release -o $G 2>&1 | grep -E " error |Build succ"
$G/BrokenNes.Workshop.exe --x86sst $T/sst80386 --out $O/sst.txt > $O/sst_stdout.txt 2>&1; tail -3 $O/sst_stdout.txt
for f in 3 4; do $G/BrokenNes.Workshop.exe --x86t386 $T/test386/test386_f$f.bin --gen $f --ee $T/test386/src/test386-EE-reference.txt > $O/t386_$f.txt 2>&1; echo "t386 f$f rc=$? $(tail -1 $O/t386_$f.txt)"; done
# Win98 cold boot to the desktop (the JIT trap SST/test386 miss): hd_ipx.img 12500 frames, a desktop screenshot is ~28 KB, a fatal-exception screen < 10 KB
$G/BrokenNes.Workshop.exe --x86boot --machine WIN98-P166 --hdd C:/BrokenNes-work/sc98/hd_ipx.img --mac 2 --frames 12500 --shot $O/w98boot.png > $O/w98boot.txt 2>&1; sz=$(stat -c %s $O/w98boot.png 2>/dev/null || echo 0); [ $sz -gt 20000 ] && echo "win98 boot: desktop ($sz bytes)" || echo "win98 boot: FAIL ($sz bytes, see $O/w98boot.png)"
if [ "$CPUONLY" != "1" ]; then BROKENNES_WORK="C:\BrokenNes-work\scspeed\bw" powershell -NoProfile -ExecutionPolicy Bypass -File $W/UAT/battery.ps1 -Consoles pc -Skip scenarios,fuzz > $O/battery.txt 2>&1; echo "battery rc=$?"; tail -8 $O/battery.txt; fi
cd /c/BrokenNes-work/sc98; release_slot
# 256-colour cold boot (the S3 8bpp desktop must survive a reboot): the StarCraft pair's C: at 8bpp, no NIC (its wizard), pixel (10,10) must be the 8bpp teal 0,130,130
claim_slot gate256
X86_NONIC=1 $G/BrokenNes.Workshop.exe --x86boot --machine WIN98-P166 --hdd C:/BrokenNes-work/sc98/pair/hd_scpairC_256.img --hdd2 C:/BrokenNes-work/sc98/pair/hd_scpairD.img --frames 8000 --shot $O/w98_256.png > $O/w98_256.txt 2>&1
python -c "from PIL import Image; p=Image.open('$O/w98_256.png').convert('RGB').getpixel((10,10)); print('win98 256-colour boot:', 'OK' if p==(0,130,130) else 'FAIL %s' % (p,))"
release_slot
# JIT cost parity (exact savestates): 300 frames of StarCraft in-game with the JIT (compiling on the CPU thread) and without must give the same savestate
claim_slot gatepar
P=C:/BrokenNes-work/sc98/pair; PA="--x86boot --machine WIN98-P166 --hdd $P/hd_scpairC_256.img --hdd2 $P/hd_scpairD.img --cd C:/BrokenNes-work/x86_cds/starcraft_xp_cd1.iso --apu SB16_D072 --loadstate $P/st_sbingame2.bin --frames 300"
X86_NONIC=1 X86_JITSYNC=1 $G/BrokenNes.Workshop.exe $PA --savestate $O/par_j.bin > /dev/null 2>&1; X86_NONIC=1 X86_NOJIT=1 $G/BrokenNes.Workshop.exe $PA --savestate $O/par_n.bin > /dev/null 2>&1
cmp -s $O/par_j.bin $O/par_n.bin && echo "jit parity: OK" || echo "jit parity: FAIL (JIT and X86_NOJIT savestates differ)"; rm -f $O/par_j.bin $O/par_n.bin
release_slot
