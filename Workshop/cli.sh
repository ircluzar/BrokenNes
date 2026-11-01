#!/usr/bin/env bash
# Workshop on Linux/macOS: builds the CLI flavour of BrokenNes.Workshop (-p:WorkshopCli=true,
# no WinForms) into bin-cli/ and runs it with the given mode, e.g.
#   Workshop/cli.sh --snestest --rom Windows/Resources/snes-test-roms/spctest/spctest.sfc
#   Workshop/cli.sh --apu-audio --rom game.nes --apu FMC --frames 600 --wav out.wav
# Set WORKSHOP_CLI_NOBUILD=1 to skip the (incremental) build step.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [ "${WORKSHOP_CLI_NOBUILD:-0}" != 1 ]; then
  out=$(dotnet build "$here/BrokenNes.Workshop.csproj" -c Release -p:WorkshopCli=true \
        -p:BaseOutputPath="$here/bin-cli/" -p:BaseIntermediateOutputPath="$here/obj-cli/" -v q -nologo 2>&1) \
    || { echo "$out" | grep -E " error " | sort -u >&2; exit 2; }
fi
exec dotnet "$here/bin-cli/Release/net10.0/BrokenNes.Workshop.dll" "$@"
