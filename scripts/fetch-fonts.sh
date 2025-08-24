#!/usr/bin/env bash
set -euo pipefail

# Fetch Press Start 2P and Orbitron variable fonts from Google Fonts
# and save them into wwwroot/fonts with expected filenames.
#
# Usage:
#   bash scripts/fetch-fonts.sh

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)
FONTS_DIR="$REPO_ROOT/wwwroot/fonts"
mkdir -p "$FONTS_DIR"

ua='Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:109.0) Gecko/20100101 Firefox/117.0'

press_css=$(mktemp -t pressstart.XXXX.css)
orbit_css=$(mktemp -t orbitron.XXXX.css)

echo "Downloading Google Fonts CSS manifests..."
curl -fsSL -H "User-Agent: $ua" \
  'https://fonts.googleapis.com/css2?family=Press+Start+2P&display=swap' \
  -o "$press_css"
curl -fsSL -H "User-Agent: $ua" \
  'https://fonts.googleapis.com/css2?family=Orbitron:wght@400..900&display=swap' \
  -o "$orbit_css"

# Extract first available woff2 URL for each
press_url=$(grep -Eo 'https://[^ )]+\.woff2' "$press_css" | head -n 1 || true)
orbit_url=$(grep -Eo 'https://[^ )]+\.woff2' "$orbit_css" | head -n 1 || true)

if [[ -z "$press_url" ]]; then
  echo "Error: Could not find Press Start 2P .woff2 URL in CSS." >&2
  exit 1
fi
if [[ -z "$orbit_url" ]]; then
  echo "Error: Could not find Orbitron .woff2 URL in CSS." >&2
  exit 1
fi

echo "Fetching fonts..."
echo "- Press Start 2P: $press_url"
curl -fsSL "$press_url" -o "$FONTS_DIR/PressStart2P.woff2"

echo "- Orbitron (variable): $orbit_url"
curl -fsSL "$orbit_url" -o "$FONTS_DIR/Orbitron-VariableFont_wght.woff2"

echo "\nSaved files:"
ls -lh "$FONTS_DIR"

echo "\nDone. Ensure app.css has @font-face pointing to ../fonts/*.woff2 (already added)."
