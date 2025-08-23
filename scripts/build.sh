#!/usr/bin/env bash
# build.sh - Kill any running dotnet, run the app, and open it in the browser
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")"/.. && pwd)"
cd "$REPO_ROOT"

# Kill any existing dotnet processes (ignore errors if none)
if pgrep dotnet >/dev/null 2>&1; then
  echo "Killing existing dotnet processes..."
  pkill -9 dotnet || true
fi

# Target URL (override with URL env var if needed)
URL="${URL:-http://localhost:5011}"

# Start a background readiness watcher that opens the browser once ready
(
  echo "Waiting for $URL ..."
  for _ in $(seq 1 60); do
    if curl -fsS "$URL" >/dev/null 2>&1; then
      if command -v open >/dev/null 2>&1; then
        if [[ -d "/Applications/Microsoft Edge.app" ]]; then
          echo "Opening in Microsoft Edge: $URL"
          open -a "Microsoft Edge" "$URL" || true
        else
          echo "Opening in default browser: $URL"
          open "$URL" || true
        fi
      fi
      exit 0
    fi
    sleep 1
  done
  echo "Server did not become ready in time (waited 60s)"
) &

# Run the app in the foreground so you can see build and runtime output
echo "Starting dotnet run (foreground)…"
exec dotnet run
