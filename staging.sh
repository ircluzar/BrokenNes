#!/usr/bin/env bash
# Wrapper: delegate to scripts/staging.sh after the scripts/ move
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$REPO_ROOT/scripts/staging.sh" "$@"

