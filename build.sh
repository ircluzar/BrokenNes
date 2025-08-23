#!/usr/bin/env bash
# build.sh - Kill any running dotnet, run the app, and open it in the browser
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

# Kill any existing dotnet processes (ignore errors if none)
if pgrep dotnet >/dev/null 2>&1; then
  echo "Killing existing dotnet processes..."
  pkill -9 dotnet || true
fi

# Start the app
LOG_FILE="${TMPDIR:-/tmp}/brokennes-dotnet.log"
: > "$LOG_FILE"

echo "Starting dotnet run (logging to $LOG_FILE)..."
( dotnet run >>"$LOG_FILE" 2>&1 ) &
DOTNET_PID=$!

# Target URL (override with URL env var if needed)
URL="${URL:-http://localhost:5011}"

# Wait for the server to respond
echo "Waiting for $URL ..."
READY=0
for _ in $(seq 1 30); do
  if curl -fsS "$URL" >/dev/null 2>&1; then
    READY=1
    break
  fi
  sleep 1
done

# Open in Microsoft Edge if present; otherwise default browser
if [[ "$READY" -eq 1 ]]; then
  if command -v open >/dev/null 2>&1; then
    if [[ -d "/Applications/Microsoft Edge.app" ]]; then
      echo "Opening in Microsoft Edge: $URL"
      open -a "Microsoft Edge" "$URL" || true
    else
      echo "Opening in default browser: $URL"
      open "$URL" || true
    fi
  fi
else
  echo "Server did not respond in time; you can check logs at $LOG_FILE"
fi

echo "dotnet run PID: $DOTNET_PID"
wait $DOTNET_PID
