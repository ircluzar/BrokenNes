#!/usr/bin/env bash
# flatpublish.sh - Build and prepare a flat, static deployable folder for the Blazor WASM app.
# Produces ./flatpublish containing everything needed to host the app from index.html on any static server.
# Usage: ./scripts/flatpublish.sh [--zip] [--verify] [--exclude-roms] [--no-aot] [--dev] [--trim]
#   --zip     : additionally create flatpublish.zip archive of the output.
#   --verify  : skip publish, just rebuild flat dir from last publish.
#   --exclude-roms : omit bundled ROMs.
#   --no-aot  : disable AOT (default).
#   --dev     : publish Debug, no trimming, no AOT.
#   --trim    : enable trimming (requires LinkerConfig safety roots).
#
# Requirements: bash, dotnet CLI.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

CSProj="BrokenNes.csproj"
if [[ ! -f $CSProj ]]; then
  echo "Error: $CSProj not found in $ROOT_DIR" >&2
  exit 1
fi

# Extract Target Framework Moniker (TFM) from the csproj (simple grep; assumes single <TargetFramework>)
TFM=$(grep -oE '<TargetFramework>[^<]+' "$CSProj" | sed 's/<TargetFramework>//')
if [[ -z "${TFM}" ]]; then
  echo "Error: Could not determine target framework from $CSProj" >&2
  exit 1
fi

echo "==> Target Framework: $TFM"

OUT_DIR="bin/Release/${TFM}/publish" # will be overridden for dev mode after arg parsing
FLAT_DIR="flatpublish"
ZIP_FLAG=false
VERIFY_ONLY=false
EXCLUDE_ROMS=false
NO_AOT=true
TRIM=false
DEV_MODE=true # emulate standard 'dotnet build' (no AOT, no trimming, debug config) but still flatten output

if [[ ${#} -gt 0 ]]; then
  for arg in "$@"; do
    case "$arg" in
      --zip) ZIP_FLAG=true ;;
      --verify) VERIFY_ONLY=true ;;
      --exclude-roms) EXCLUDE_ROMS=true ;;
  --no-aot) NO_AOT=true ;;
  --trim) TRIM=true ;;
  --dev) DEV_MODE=true ;;
  *) echo "Unknown argument: $arg" >&2; echo "Supported: --zip --verify --exclude-roms --no-aot --dev --trim"; exit 2 ;;
    esac
  done
fi

if $DEV_MODE; then
  OUT_DIR="bin/Debug/${TFM}/publish"
else
  OUT_DIR="bin/Release/${TFM}/publish"
fi

if ! $VERIFY_ONLY; then
  # Clean previous publish output to avoid stale leftover files (e.g., removed ROMs)
  if [[ -d "$OUT_DIR" ]]; then
    echo "==> Cleaning previous publish output ($OUT_DIR)"
    rm -rf "$OUT_DIR"
  fi
  if $DEV_MODE; then
    echo "==> Publishing (DEV MODE: Debug, no AOT, no trimming) ..."
    dotnet publish "$CSProj" -c Debug -o "$OUT_DIR" -p:RunAOTCompilation=false -p:DisableAot=true -p:PublishTrimmed=false -p:TrimMode=copyused >/dev/null
  elif $NO_AOT; then
    echo "==> Publishing (Release, AOT DISABLED) ..."
    if $TRIM; then
      dotnet publish "$CSProj" -c Release -o "$OUT_DIR" -p:RunAOTCompilation=false -p:DisableAot=true -p:PublishTrimmed=true -p:TrimMode=link -p:TrimmerRootDescriptor=LinkerConfig.xml >/dev/null
    else
      dotnet publish "$CSProj" -c Release -o "$OUT_DIR" -p:RunAOTCompilation=false -p:DisableAot=true -p:PublishTrimmed=false >/dev/null
    fi
  else
    echo "==> Publishing (AOT/Release) ..."
    if $TRIM; then
      dotnet publish "$CSProj" -c Release -o "$OUT_DIR" -p:PublishTrimmed=true -p:TrimMode=link -p:TrimmerRootDescriptor=LinkerConfig.xml >/dev/null
    else
      dotnet publish "$CSProj" -c Release -o "$OUT_DIR" -p:PublishTrimmed=false >/dev/null
    fi
  fi
else
  echo "==> Skipping publish (verify-only mode)"
fi

WWWROOT_DIR="$OUT_DIR/wwwroot"

echo "==> Checking publish output layout... (OUT_DIR=$OUT_DIR)"
if [[ ! -d "$WWWROOT_DIR" ]]; then
  echo "Error: Expected $WWWROOT_DIR to exist after publish (or from previous run)." >&2
  echo "Debug info: contents of $(dirname "$OUT_DIR"):" >&2
  ls -1 $(dirname "$OUT_DIR") >&2 || true
  exit 1
fi
if [[ ! -f "$WWWROOT_DIR/index.html" ]]; then
  echo "Error: index.html not found in $WWWROOT_DIR (unexpected layout)." >&2
  exit 1
fi

# Clean old flatpublish
if [[ -d "$FLAT_DIR" ]]; then
  echo "==> Removing existing $FLAT_DIR"
  rm -rf "$FLAT_DIR"
fi

mkdir -p "$FLAT_DIR"

echo "==> Copying static site contents to $FLAT_DIR"
# Copy the contents (not the wwwroot directory itself) to flatten.
cp -R "$WWWROOT_DIR/." "$FLAT_DIR/"


# Optional size trimming (commented out). Uncomment to remove PDBs if present.
# find "$FLAT_DIR" -name '*.pdb' -delete

# Verify presence of key expected assets (helps detect mis-copy issues and aids user confidence)
echo "==> Verifying critical assets..."
missing=0
critical_assets=(
  "index.html"
  "lib/bootstrap/dist/css/bootstrap.min.css"
  "lib/bootstrap/dist/js/bootstrap.bundle.min.js"
)
for asset in "${critical_assets[@]}"; do
  if [[ ! -f "$FLAT_DIR/$asset" ]]; then
    echo "  MISSING: $asset"
    missing=1
  else
    echo "  found: $asset"
  fi
done

# Additional required assets for APU_WF (waveform SoundFont bridge) and APU_MNES (MNES FluidSynth bridge)
# These power the note-event SoundFont playback paths. We validate presence so a publish error is obvious.
apu_assets=(
  "soundfont.js"
  "mnesSf2.js"
  "sf2player/MNES.sf2"
  "sf2player/js-synthesizer.min.js"
  "sf2player/js-synthesizer.worklet.min.js"
  "sf2player/libfluidsynth-2.0.2.js"
)
echo "==> Verifying APU_WF / APU_MNES SoundFont assets..."
for asset in "${apu_assets[@]}"; do
  if [[ ! -f "$FLAT_DIR/$asset" ]]; then
    echo "  MISSING: $asset"
    missing=1
  else
    echo "  found: $asset"
  fi
done

# Opportunistic compression (Brotli + gzip) for large / frequently requested MNES assets if not already present.
# Skips if tools unavailable. Does not treat absence as fatal.
compress_candidates=(
  "mnesSf2.js"
  "soundfont.js"
  "nesInterop.js"
  "sf2player/js-synthesizer.min.js"
  "sf2player/js-synthesizer.worklet.min.js"
  "sf2player/libfluidsynth-2.0.2.js"
  "sf2player/MNES.sf2"
)
echo "==> Ensuring compressed (brotli/gzip) variants for core SoundFont assets (best-effort)"
for f in "${compress_candidates[@]}"; do
  src="$FLAT_DIR/$f"
  [[ -f "$src" ]] || continue
  # Brotli
  if command -v brotli >/dev/null 2>&1; then
    if [[ ! -f "$src.br" ]]; then
      echo "  brotli: $f"
      brotli -f -q 11 "$src" -o "$src.br" || echo "    (brotli failed for $f)"
    fi
  fi
  # Gzip
  if command -v gzip >/dev/null 2>&1; then
    if [[ ! -f "$src.gz" ]]; then
      echo "  gzip:   $f"
      gzip -c -9 "$src" > "$src.gz" || echo "    (gzip failed for $f)"
    fi
  fi
done

# Ensure base href is relative for subdirectory hosting
INDEX_HTML="$FLAT_DIR/index.html"
if grep -q "<base href=\"/\"" "$INDEX_HTML" 2>/dev/null; then
  echo "==> Rewriting <base href> to ./ for subfolder hosting"
  sed -i '' -e 's#<base href="/"#<base href="./"#' "$INDEX_HTML" || true
fi

# Remove any leftover placeholder fragments like #[.{fingerprint}] if present
if grep -q '#[.{fingerprint}]' "$INDEX_HTML" 2>/dev/null; then
  echo "==> Cleaning placeholder fingerprint pattern"
  sed -i '' -e 's/#[.{fingerprint}]//g' "$INDEX_HTML" || true
fi

# Replace generic blazor.webassembly.js reference with hashed file (import maps don't alias plain <script src>)
hashed_blazor_js=$(basename $FLAT_DIR/_framework/blazor.webassembly.*.js 2>/dev/null | head -n1 || true)
if [[ -n "$hashed_blazor_js" ]]; then
  if grep -q 'blazor.webassembly.js' "$INDEX_HTML"; then
    echo "==> Ensuring script uses hashed $hashed_blazor_js"
    sed -i '' -E "s#src=\"(\.?/)?_framework/blazor\.webassembly\.js\"#src=\"./_framework/$hashed_blazor_js\"#" "$INDEX_HTML" || true
  fi
fi

# Build and inject importmap to map generic names to hashed ones (prevents 404 on dotnet.js)
blazor_hash=$hashed_blazor_js
dotnet_hash=$(basename $FLAT_DIR/_framework/dotnet.*.js 2>/dev/null | head -n1 || true)
runtime_hash=$(basename $FLAT_DIR/_framework/dotnet.runtime.*.js 2>/dev/null | head -n1 || true)
native_hash=$(basename $FLAT_DIR/_framework/dotnet.native.*.js 2>/dev/null | head -n1 || true)
if [[ -n "$dotnet_hash" && -n "$runtime_hash" && -n "$native_hash" && -n "$blazor_hash" ]]; then
  echo "==> Injecting importmap with hashed framework script mappings"
  importmap_content="<script type=\"importmap\">{\n  \"imports\": {\n    \"./_framework/blazor.webassembly.js\": \"./_framework/$blazor_hash\",\n    \"./_framework/dotnet.js\": \"./_framework/$dotnet_hash\",\n    \"./_framework/dotnet.runtime.js\": \"./_framework/$runtime_hash\",\n    \"./_framework/dotnet.native.js\": \"./_framework/$native_hash\"\n  }\n}</script>"
  if grep -q '<script type="importmap"></script>' "$INDEX_HTML"; then
    # Replace empty tag
    sed -i '' -e "s#<script type=\"importmap\"></script>#$importmap_content#" "$INDEX_HTML"
  elif ! grep -q 'type="importmap"' "$INDEX_HTML"; then
    # Insert before </head>
    sed -i '' -e "s#</head>#$importmap_content\n</head>#" "$INDEX_HTML"
  fi
fi

# Handle hashed blazor.webassembly.*.js (new .NET versions)
if compgen -G "$FLAT_DIR/_framework/blazor.webassembly.*.js" > /dev/null; then
  echo "  found: _framework/$(basename $(echo $FLAT_DIR/_framework/blazor.webassembly.*.js | cut -d' ' -f1))"
else
  echo "  MISSING: _framework/blazor.webassembly.*.js"
  missing=1
fi

if (( missing )); then
  echo "ERROR: One or more critical assets are missing in $FLAT_DIR" >&2
  exit 3
fi

# Sanity report
COUNT_FILES=$(find "$FLAT_DIR" -type f | wc -l | tr -d ' ')
SIZE_TOTAL=$(du -sh "$FLAT_DIR" | awk '{print $1}')

echo "==> flatpublish ready: $COUNT_FILES files, total size $SIZE_TOTAL"
echo "     Entry point: $FLAT_DIR/index.html"

if $ZIP_FLAG; then
  ZIP_NAME="flatpublish.zip"
  echo "==> Creating archive $ZIP_NAME"
  rm -f "$ZIP_NAME"
  (cd "$FLAT_DIR" && zip -qr "../$ZIP_NAME" .)
  echo "==> Archive created: $(du -h "$ZIP_NAME" | awk '{print $1}')"
fi

echo "Done. You can deploy the contents of $FLAT_DIR to any static host (S3, GitHub Pages, Netlify, etc.)"
