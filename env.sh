#!/usr/bin/env bash
# Wrapper: source the env from scripts/ so "source ./env.sh" continues to work
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/scripts/env.sh"

