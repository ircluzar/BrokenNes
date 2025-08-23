#!/usr/bin/env bash
# staging.sh - Build and deploy to staging via FTP
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Run deploy
"$REPO_ROOT/scripts/push.sh" "$@"

# After successful deploy, open in Microsoft Edge (fallback to default browser)
URL="https://cc.r5x.cc/rabbitwine/brokennes"
if command -v open >/dev/null 2>&1; then
	if [[ -d "/Applications/Microsoft Edge.app" ]]; then
		echo "Opening in Microsoft Edge: $URL"
		open -a "Microsoft Edge" "$URL" || true
	else
		echo "Opening in default browser: $URL"
		open "$URL" || true
	fi
fi
