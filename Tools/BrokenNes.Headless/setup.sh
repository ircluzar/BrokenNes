#!/usr/bin/env bash
# One-shot dev environment setup for headless core development (Linux/macOS).
#  - installs the .NET 10 SDK into ~/.dotnet if `dotnet` with a 10.x SDK isn't available
#  - fetches the public NES test ROM collection into ./testroms (git-ignored)
#  - builds the harness in Release
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  if [ ! -x "$HOME/.dotnet/dotnet" ] || ! "$HOME/.dotnet/dotnet" --list-sdks | grep -q '^10\.'; then
    echo ">> installing .NET 10 SDK to ~/.dotnet"
    tmp="$(mktemp -d)"
    curl -sSL -o "$tmp/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
    bash "$tmp/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"
    rm -rf "$tmp"
  fi
  export PATH="$HOME/.dotnet:$PATH"
  echo ">> add to your shell: export PATH=\"\$HOME/.dotnet:\$PATH\""
fi

if [ ! -d "$here/testroms/src" ]; then
  echo ">> fetching test ROMs (christopherpow/nes-test-roms)"
  mkdir -p "$here/testroms"
  git clone -q --depth 1 https://github.com/christopherpow/nes-test-roms.git "$here/testroms/src"
fi

echo ">> building harness"
dotnet build "$here/BrokenNes.Headless.csproj" -c Release | grep -E "error|Build succeeded|Warn" || true
echo ">> done. try: $here/nesh cores"
