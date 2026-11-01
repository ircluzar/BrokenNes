#!/usr/bin/env bash
# One-shot setup for working on BrokenNes from Linux/macOS (e.g. a cloud container):
#  1. .NET 10 SDK in ~/.dotnet (skipped if a 10.x SDK is already on PATH)
#  2. PowerShell 7 as a .NET global tool (UAT/*.ps1, BrokenDev scripts that don't call .exe files)
#  3. c-sp/game-boy-test-roms v7.0 into Windows/Resources/gb-test-roms, kept out of git through
#     .git/info/exclude like on Windows (per-ROM licenses; see THIRD_PARTY_NOTICES.md section 3)
#  4. the Workshop CLI build (Workshop/cli.sh) and the Windows projects compiled with
#     EnableWindowsTargeting, which checks they still build (they cannot run here)
# Afterwards: export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo ">> installing the .NET 10 SDK into ~/.dotnet"
  curl -sSL -o "$tmp/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
  bash "$tmp/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"
fi

if ! command -v pwsh >/dev/null 2>&1; then
  echo ">> installing PowerShell (dotnet tool)"
  dotnet tool install --global PowerShell >/dev/null
fi

roms="$repo/Windows/Resources/gb-test-roms"
if [ ! -d "$roms/blargg" ]; then
  echo ">> fetching game-boy-test-roms v7.0"
  curl -sSL -o "$tmp/gb.zip" https://github.com/c-sp/game-boy-test-roms/releases/download/v7.0/game-boy-test-roms-v7.0.zip
  mkdir -p "$roms"
  python3 -I -c 'import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$tmp/gb.zip" "$roms"
fi
grep -qx '/Windows/Resources/gb-test-roms/' "$repo/.git/info/exclude" 2>/dev/null \
  || echo '/Windows/Resources/gb-test-roms/' >> "$repo/.git/info/exclude"

echo ">> building the Workshop CLI"
"$repo/Workshop/cli.sh" --console-catalog >/dev/null 2>&1 || true
test -f "$repo/Workshop/bin-cli/Release/net10.0/BrokenNes.Workshop.dll" || { echo "Workshop CLI build failed; run Workshop/cli.sh to see errors"; exit 2; }

echo ">> compile-checking the Windows projects"
for p in Core/BrokenNes.Core.csproj Windows/BrokenNes.Windows.csproj Workshop/BrokenNes.Workshop.csproj; do
  printf '   %-40s ' "$p"
  dotnet build "$repo/$p" -p:EnableWindowsTargeting=true -v q -nologo 2>&1 | grep -qE ' error ' && echo FAILED || echo ok
done

echo ">> smoke test: SPC700 suite"
WORKSHOP_CLI_NOBUILD=1 "$repo/Workshop/cli.sh" --snestest --rom "$repo/Windows/Resources/snes-test-roms/spctest/spctest.sfc" | tail -1
