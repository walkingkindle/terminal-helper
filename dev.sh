#!/usr/bin/env bash
# Runs the API (http://localhost:5273) and the Angular dev server (http://localhost:4280) together.
# Open http://localhost:4280; /api calls are proxied to the API. Ctrl+C stops both.
set -euo pipefail
cd "$(dirname "$0")"

API_PORT=5273
WEB_PORT=4280

# Best effort: which process listens on a port (ss on Linux, lsof on macOS, nothing on Git Bash).
pid_on() {
  if command -v ss >/dev/null; then
    ss -ltnpH "sport = :$1" 2>/dev/null | grep -o 'pid=[0-9]*' | head -1 | cut -d= -f2
  elif command -v lsof >/dev/null; then
    lsof -tiTCP:"$1" -sTCP:LISTEN 2>/dev/null | head -1
  fi
}

# Starting over an earlier run fails in confusing ways, so stop with a clear message instead.
for port in $API_PORT $WEB_PORT; do
  if curl -s -o /dev/null --max-time 2 "http://localhost:$port"; then
    pid=$(pid_on "$port")
    echo "Port $port is already in use${pid:+ by pid $pid ($(ps -o args= -p "$pid" | cut -c1-60))}." >&2
    echo "Probably an earlier run that is still going. Stop it${pid:+ (kill $pid)} and run ./dev.sh again." >&2
    exit 1
  fi
done

if [[ -z "${ANTHROPIC_API_KEY:-}" ]] &&
  ! dotnet user-secrets list --project server/TerminalHelper.Api 2>/dev/null | grep -q '^Anthropic:ApiKey'; then
  echo "Note: no Anthropic API key is set, so questions will fail until you add one:" >&2
  echo '  dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..." --project server/TerminalHelper.Api' >&2
fi

[[ -d client/node_modules ]] || npm --prefix client ci

# Ctrl+C, a failed start or a closed terminal: take every child process down with this script
# (the script itself ignores that TERM so it exits quietly with the real status).
trap 'trap "" TERM; kill 0' EXIT

echo "Starting the API on http://localhost:$API_PORT ..."
dotnet run --project server/TerminalHelper.Api &
api=$!
until curl -sf -o /dev/null --max-time 2 "http://localhost:$API_PORT/api/health"; do
  kill -0 "$api" 2>/dev/null || { echo "The API didn't start; see the errors above." >&2; exit 1; }
  sleep 0.5
done

echo "API is up. Starting the web app: open http://localhost:$WEB_PORT"
npm --prefix client start
