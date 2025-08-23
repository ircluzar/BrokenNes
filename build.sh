#!/usr/bin/env bash
# Wrapper: delegate to scripts/build.sh after the scripts/ move
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$REPO_ROOT/scripts/build.sh" "$@"

