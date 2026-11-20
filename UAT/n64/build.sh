#!/bin/bash
# [UAT/n64] Build the N64 test binaries from the cor64 tree (Cor64Root, default third_party/cor64) the same way cer64 did
# (cer64plus build_cer64.sh + lowend.sh): <Cor64Root>/bin_rt = SessionProbe published with Cor64FasterVi=false (real video
# timing, the low-end check), then <Cor64Root>/bin = RunN64 (goldens, krom) + SessionProbe (boot hash, compat, ab30) on the
# default defines. Both folders are cor64's own output paths (ignored by third_party/cor64/.gitignore).
# Needs: .NET 10 SDK; native/prdp/build/Release/cor64_prdp.dll present in the cor64 tree (parallel-rdp; without it the
# renderer falls back to software and the boot hash differs). Usage: UAT/n64/build.sh   (about 1-2 min)
. "$(dirname "$0")/lib/n64env.sh"
[ -f "$COR64/src/SessionProbe/SessionProbe.csproj" ] || { echo "no cor64 tree at $COR64 (set Cor64Root, or copy it to third_party/cor64: see docs)"; exit 2; }
[ -f "$COR64/native/prdp/build/Release/cor64_prdp.dll" ] || echo "WARNING: $COR64/native/prdp/build/Release/cor64_prdp.dll missing: the goldens will not match"
cd "$COR64"
dotnet publish src/SessionProbe/SessionProbe.csproj -c Release -o bin_rt -p:Cor64FasterVi=false 2>&1 | grep -E " error |rror\(s\)" ; [ -x bin_rt/SessionProbe.exe ] || { echo "BUILD FAIL (bin_rt)"; exit 1; }
# the publish also writes the real-timing build into bin: rebuild the default build there (as build_cer64.sh does)
dotnet build src/RunN64/RunN64.csproj -c Release --no-incremental 2>&1 | grep -E " error |rror\(s\)"
dotnet build src/SessionProbe/SessionProbe.csproj -c Release 2>&1 | grep -E " error |rror\(s\)" | sed 's/^/[probe] /'
[ -x bin/SessionProbe.exe ] && [ -x bin/RunN64.exe ] || { echo "BUILD FAIL (bin)"; exit 1; }
for d in bin bin_rt; do mkdir -p $d/st; for s in castle title; do
  if [ -f "$N64_STATE_DIR/$s.state" ]; then cp "$N64_STATE_DIR/$s.state" $d/st/; else echo "WARNING: no $s.state in $N64_STATE_DIR"; fi; done; done
echo "BUILD OK: $COR64/bin $COR64/bin_rt"
