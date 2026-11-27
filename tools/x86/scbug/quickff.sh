#!/bin/bash
# after a full green gate: rebase on main's newer non-overlapping commits, re-check sc+aoe jit parity + bug-test, fast-forward
cd C:/BrokenNes-work/wt-scbug && git rebase -q refs/heads/main || { echo "REBASE FAILED"; exit 1; }
git log --oneline -1
dotnet build Workshop/BrokenNes.Workshop.csproj -c Release -o C:/BrokenNes-work/scbug/gbinm -v q 2>&1 | grep -E " error " && { echo "BUILD FAILED"; exit 1; }
out=$(bash /c/BrokenNes-work/scbug/par2.sh); echo "$out"
echo "$out" | grep -q "sc jit parity OK" && echo "$out" | grep -q "aoe jit parity OK" && echo "$out" | grep -q "bug-test rc=0" || { echo "NOT MERGED"; exit 1; }
cd C:/Users/philt/OneDrive/Documents/PROJECTS/BrokenNes && OLD=$(git rev-parse refs/heads/main) && NEW=$(git -C C:/BrokenNes-work/wt-scbug rev-parse HEAD)
if git merge-base --is-ancestor $OLD $NEW; then git update-ref refs/heads/main $NEW $OLD && git diff --name-only $OLD $NEW | xargs git checkout HEAD -- && git log --oneline -3 refs/heads/main; else echo "MOVED again"; fi
