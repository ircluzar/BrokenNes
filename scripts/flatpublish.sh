#!/usr/bin/env bash
# flatpublish.sh - Build and prepare a flat, static deployable folder for the Blazor WASM app.
# Produces ./flatpublish containing everything needed to host the app from index.html on any static server.
# Usage: ./scripts/flatpublish.sh [--zip] [--verify] [--exclude-roms] [--no-aot] [--dev] [--trim]
#   --zip     : additionally create flatpublish.zip archive of the output.
#   --verify  : skip publish, just rebuild flat dir from last publish.
#   --exclude-roms : omit bundled ROMs (.nes files) from the flatpublish output.
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

# Optionally remove bundled ROMs
if $EXCLUDE_ROMS; then
  echo "==> Removing bundled ROMs (.nes files) due to --exclude-roms"
  ROM_COUNT=$(find "$FLAT_DIR" -type f -name '*.nes' | wc -l | tr -d ' ')
  if (( ROM_COUNT > 0 )); then
    find "$FLAT_DIR" -type f -name '*.nes' -delete || true
    echo "  removed: $ROM_COUNT ROM file(s)"
  else
    echo "  none found"
  fi
fi


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
  "lib/soundfont.js"
  "lib/mnesSf2.js"
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

# Verify TTS (meSpeak) assets and speak.js shim
tts_assets=(
  "speak.js"
  "lib/mespeak/mespeak.js"
  "lib/mespeak/mespeak_config.json"
  "lib/mespeak/voices/en/en-us.json"
)
echo "==> Verifying TTS (meSpeak) assets..."
for asset in "${tts_assets[@]}"; do
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
  "lib/mnesSf2.js"
  "lib/soundfont.js"
  "lib/nesInterop.js"
  "speak.js"
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

# Also compress meSpeak subtree (JS/JSON) and any bundled .nes files (best-effort)
if command -v brotli >/dev/null 2>&1 || command -v gzip >/dev/null 2>&1; then
  echo "==> Best-effort compression for meSpeak voices/config and ROMs"
  # meSpeak
  if [[ -d "$FLAT_DIR/lib/mespeak" ]]; then
    while IFS= read -r -d '' f; do
      [[ -f "$f" ]] || continue
      if command -v brotli >/dev/null 2>&1 && [[ ! -f "$f.br" ]]; then
        rel=${f#"$FLAT_DIR/"}; echo "  brotli: $rel"; brotli -f -q 11 "$f" -o "$f.br" || true
      fi
      if command -v gzip >/dev/null 2>&1 && [[ ! -f "$f.gz" ]]; then
        rel=${f#"$FLAT_DIR/"}; echo "  gzip:   $rel"; gzip -c -9 "$f" > "$f.gz" || true
      fi
    done < <(find "$FLAT_DIR/lib/mespeak" -type f \( -name '*.js' -o -name '*.json' \) -print0 2>/dev/null)
  fi
  # ROMs
  if [[ -d "$FLAT_DIR" ]]; then
    while IFS= read -r -d '' f; do
      [[ -f "$f" ]] || continue
      if command -v brotli >/dev/null 2>&1 && [[ ! -f "$f.br" ]]; then
        rel=${f#"$FLAT_DIR/"}; echo "  brotli: $rel"; brotli -f -q 9 "$f" -o "$f.br" || true
      fi
      if command -v gzip >/dev/null 2>&1 && [[ ! -f "$f.gz" ]]; then
        rel=${f#"$FLAT_DIR/"}; echo "  gzip:   $rel"; gzip -c -9 "$f" > "$f.gz" || true
      fi
    done < <(find "$FLAT_DIR" -type f -name '*.nes' -print0 2>/dev/null)
  fi
fi

# Optionally compress ONNX models as well (best-effort; improves CDN/static hosting if server serves pre-compressed files)
if [[ -d "$FLAT_DIR/models" ]]; then
  echo "==> Ensuring compressed variants for ONNX models (best-effort)"
  while IFS= read -r -d '' onnx; do
    # Brotli
    if command -v brotli >/dev/null 2>&1 && [[ ! -f "$onnx.br" ]]; then
      rel=${onnx#"$FLAT_DIR/"}
      echo "  brotli: ${rel}"
      brotli -f -q 9 "$onnx" -o "$onnx.br" || echo "    (brotli failed for ${rel})"
    fi
    # Gzip
    if command -v gzip >/dev/null 2>&1 && [[ ! -f "$onnx.gz" ]]; then
      rel=${onnx#"$FLAT_DIR/"}
      echo "  gzip:   ${rel}"
      gzip -c -9 "$onnx" > "$onnx.gz" || echo "    (gzip failed for ${rel})"
    fi
  done < <(find "$FLAT_DIR/models" -type f -name '*.onnx' -print0 2>/dev/null)
fi

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

# Verify ONNX models presence/count (non-fatal unless explicitly needed)
if [[ -d "$FLAT_DIR/models" ]]; then
  echo "==> Verifying ONNX models directory"
  MODEL_COUNT=$(find "$FLAT_DIR/models" -maxdepth 1 -type f -name '*.onnx' | wc -l | tr -d ' ')
  if (( MODEL_COUNT == 0 )); then
    echo "  MISSING: No .onnx models found under models/ (app features depending on Imagine will not work)"
  else
    echo "  found: $MODEL_COUNT ONNX model(s) under models/"
    if [[ -f "$FLAT_DIR/models/models.json" ]]; then
      echo "  found: models/models.json"
    else
      echo "  note: models/models.json not found (optional manifest)"
    fi
  fi
else
  echo "  note: models/ directory not present (skipping ONNX verification)"
fi

# (Home page CSS fallback will be added after route generation if needed)

# ---- Route Static Copies & Audio Asset Verification ----
# For static hosting (e.g., GitHub Pages, S3 without rewrite rules) we create per-route
# folders so direct navigation to /nes, /options, /deckbuilder works without server-side
# fallback. Each folder gets an index.html cloned from the root one with a modified
# <base href> so relative framework/script paths resolve to the root.
routes=(nes options deck-builder deckbuilder story continue cores input inputsettings)
echo "==> Generating static route entry points (${routes[*]})"
for r in "${routes[@]}"; do
  route_dir="$FLAT_DIR/$r"
  mkdir -p "$route_dir"
  cp "$INDEX_HTML" "$route_dir/index.html"
  # Adjust base href from ./ to ../ (only if present) so ./_framework resolves to root _framework
  if grep -q '<base href="\./"' "$route_dir/index.html" 2>/dev/null; then
    sed -i '' -e 's#<base href="\./"#<base href="../"#' "$route_dir/index.html" || true
  fi
done
# Home page CSS fallback (unconditional copy from source if exists + link insertion)
if [[ -f "Pages/Home.razor.css" ]]; then
  echo "==> Adding fallback home-page.css"
  mkdir -p "$FLAT_DIR/css"; cp "Pages/Home.razor.css" "$FLAT_DIR/css/home-page.css" || true
  for html in "$INDEX_HTML" \
              "$FLAT_DIR/nes/index.html" \
              "$FLAT_DIR/options/index.html" \
              "$FLAT_DIR/deckbuilder/index.html" \
              "$FLAT_DIR/deck-builder/index.html" \
              "$FLAT_DIR/story/index.html" \
              "$FLAT_DIR/continue/index.html" \
              "$FLAT_DIR/cores/index.html" \
              "$FLAT_DIR/input/index.html" \
              "$FLAT_DIR/inputsettings/index.html"; do
    [[ -f "$html" ]] || continue
    if ! grep -q 'home-page.css' "$html"; then
      sed -i '' -e 's#</head>#  <link rel="stylesheet" href="css/home-page.css" />\n</head>#' "$html" || true
    fi
  done
fi
# Also provide a 404.html fallback (GitHub Pages serves this for unknown routes allowing SPA to boot)
if [[ ! -f "$FLAT_DIR/404.html" ]]; then
  cp "$INDEX_HTML" "$FLAT_DIR/404.html"
fi


# Verify audio/media assets explicitly (added music / sfx). Mark missing but not fatal unless core media missing.
echo "==> Verifying media assets..."
media_missing=0
media_assets=(
  "music/TitleScreen.mp3"
  "music/Options.mp3"
  "music/DeckBuilder.mp3"
  "music/Story.mp3"
  "sfx/plates.m4a"
)
for a in "${media_assets[@]}"; do
  if [[ -f "$FLAT_DIR/$a" ]]; then
    echo "  found: $a"
  else
    echo "  MISSING: $a"
    media_missing=1
  fi
done
if (( media_missing )); then
  echo "WARNING: One or more media assets were not found. Continue anyway." >&2
fi

# Opportunistically compress (brotli/gzip) large audio files if not already compressed (best-effort)
echo "==> Ensuring compressed variants for media assets (best-effort)"
for a in "${media_assets[@]}"; do
  src="$FLAT_DIR/$a"; [[ -f "$src" ]] || continue
  # Skip if already smaller than 16KB (likely no win) or already have .br/.gz
  size=$(stat -f%z "$src" 2>/dev/null || echo 0)
  if (( size > 16384 )); then
    if command -v brotli >/dev/null 2>&1 && [[ ! -f "$src.br" ]]; then
      echo "  brotli: $a"
      brotli -f -q 5 "$src" -o "$src.br" || echo "    (brotli failed for $a)"
    fi
    if command -v gzip >/dev/null 2>&1 && [[ ! -f "$src.gz" ]]; then
      echo "  gzip:   $a"
      gzip -c -9 "$src" > "$src.gz" || echo "    (gzip failed for $a)"
    fi
  fi
done

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
