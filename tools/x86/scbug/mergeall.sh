#!/bin/bash
# rebase x86-scbug on main, g2 gate + local battery + bug-test (no speed) in parallel, fast-forward only when ALL pass
cd C:/BrokenNes-work/wt-scbug && git rebase -q refs/heads/main || { echo "REBASE FAILED"; exit 1; }
git log --oneline -1; TAG=scbug$(date +%H%M)
cd /c/BrokenNes-work/g2 && ./g2run.sh build garage2 $TAG C:/BrokenNes-work/wt-scbug 2>&1 | tail -1
(./g2run.sh gate garage2 ${TAG}_gate $TAG 2>&1 | grep -E "sst:|t386|win98|parity" | cut -c1-110 > /c/BrokenNes-work/scbug/gate_last.txt) &
gp=$!
cd /c/BrokenNes-work/scbug; dotnet build C:/BrokenNes-work/wt-scbug/Workshop/BrokenNes.Workshop.csproj -c Release -o C:/BrokenNes-work/scbug/gbinm -v q 2>&1 | grep -E " error "
. /c/BrokenNes-work/sc98/slot.sh; SLOT_OWNER=scbug claim_slot mergeall
BROKENNES_WORK="C:\BrokenNes-work\scbug\bw" powershell -NoProfile -ExecutionPolicy Bypass -File C:/BrokenNes-work/wt-scbug/UAT/battery.ps1 -Consoles pc -Skip scenarios,fuzz > battery_last.txt 2>&1; brc=$?
gbinm/BrokenNes.Workshop.exe --bug-test --only media,ring,bundle,places,games > bugtest_last.txt 2>&1; trc=$?
release_slot; wait $gp
echo "battery rc=$brc: $(grep -E 'x86 PC|Golden' battery_last.txt | tr '\n' ' ')"; echo "bug-test rc=$trc: $(grep 'bug tests:' bugtest_last.txt)"; cat gate_last.txt
ok=1; [ $brc -eq 0 ] || ok=0; [ $trc -eq 0 ] || ok=0
for w in "99.09%" "t386 f3 rc=0" "t386 f4 rc=0" "win98 boot: desktop" "256-colour boot: OK" "^jit parity: OK" "aoe jit parity: OK"; do grep -q "$w" gate_last.txt || { echo "gate missing: $w"; ok=0; }; done
[ $ok -eq 1 ] || { echo "NOT MERGED (a check failed)"; exit 1; }
cd C:/Users/philt/OneDrive/Documents/PROJECTS/BrokenNes && OLD=$(git rev-parse refs/heads/main) && NEW=$(git -C C:/BrokenNes-work/wt-scbug rev-parse HEAD)
if git merge-base --is-ancestor $OLD $NEW; then git update-ref refs/heads/main $NEW $OLD && git diff --name-only $OLD $NEW | xargs git checkout HEAD -- && git log --oneline -3 refs/heads/main; else echo "MOVED: main has $(git log --oneline $NEW..$OLD | wc -l) new commits"; git diff --name-only $(git merge-base $OLD $NEW) $OLD | head -20; fi
