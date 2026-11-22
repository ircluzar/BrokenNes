#!/usr/bin/env bash
# Create an agent worktree WITHOUT the bundled ONNX models (saves ~0.5 GB per worktree).
# Builds pick the models up from BROKENNES_MODELS_DIR (default C:/BrokenNes-work/shared/models).
# Usage: bash tools/wt-new.sh <path> <new-branch> [start-point=refs/heads/main]
set -euo pipefail
path=$1; branch=$2; start=${3:-refs/heads/main}
git worktree add --no-checkout -b "$branch" "$path" "$start"
cd "$path"
git sparse-checkout set --no-cone '/*' '!/Windows/Data/*.onnx' '!/WebLite/wwwroot/models/*.onnx'
git checkout
models=${BROKENNES_MODELS_DIR:-C:/BrokenNes-work/shared/models}
[ -d "$models/Windows/Data" ] || echo "WARNING: shared models missing at $models (builds will lack the Imagine ONNX models)" >&2
echo "worktree $path on $branch (sparse: models from $models)"
