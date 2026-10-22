#!/usr/bin/env bash
# Terminal Helper as a desktop app. Builds itself the first time, starts a private server on this computer
# and opens it in its own window. Close the window to quit.
#
#   ./terminal-helper.sh                    start; the first time, pick Claude, OpenAI or a local model in the window
#   ./terminal-helper.sh --api-key          paste an Anthropic or OpenAI key at a hidden prompt; it's saved
#   ./terminal-helper.sh --api-key sk-…     the same, but the key ends up in your shell history
#   ./terminal-helper.sh --browser          use a normal browser tab instead of an app window
#   ./terminal-helper.sh --port 4978        if port 4977 is taken
#   ./terminal-helper.sh --rebuild          build again even if nothing changed
#   ./terminal-helper.sh --install          add it to your desktop's app launcher (Linux)
#
# Building needs the .NET 10 SDK and Node.js 22+. Notes and the key are kept in the data folder:
# ~/.local/share/terminal-helper (Linux), ~/Library/Application Support/terminal-helper (macOS),
# %LOCALAPPDATA%\terminal-helper (Windows, run from Git Bash). Set TERMINAL_HELPER_DATA to use another one.
set -euo pipefail

here=$(cd "$(dirname "$(readlink -f "$0" 2>/dev/null || echo "$0")")" && pwd)
cd "$here"

usage() { sed -n '2,15p' "$0" | sed -E 's/^# ?//'; }

die() {
  echo "terminal-helper: $*" >&2
  # Started from the app launcher there's no terminal to read this, so say it on the desktop too.
  if [[ ! -t 2 ]] && command -v notify-send > /dev/null; then notify-send "Terminal Helper" "$*" || true; fi
  exit 1
}

port=${TERMINAL_HELPER_PORT:-4977}
window=app
key=""
ask_key=0
rebuild=0
install=0
while (($#)); do
  case $1 in
    --api-key)
      if [[ ${2:-} == sk-* ]]; then key=$2; shift; else ask_key=1; fi ;;
    --api-key=*) key=${1#*=} ;;
    --port) port=${2:?--port needs a number}; shift ;;
    --port=*) port=${1#*=} ;;
    --browser) window=tab ;;
    --rebuild) rebuild=1 ;;
    --install) install=1 ;;
    -h | --help) usage; exit 0 ;;
    *) echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
  esac
  shift
done
[[ $port =~ ^[0-9]+$ ]] || die "--port needs a number."
if ((ask_key)); then
  read -rsp "Paste your Anthropic or OpenAI API key (it won't show): " key < /dev/tty
  echo
fi
command -v curl > /dev/null || die "Needs curl."

# --- Where things live ---------------------------------------------------------------------------------
on_windows=0
case $(uname -s) in MINGW* | MSYS* | CYGWIN*) on_windows=1 ;; esac

default_data_dir() {
  case $(uname -s) in
    Darwin) echo "$HOME/Library/Application Support/terminal-helper" ;;
    MINGW* | MSYS* | CYGWIN*) echo "$(cygpath -u "${LOCALAPPDATA:?}")/terminal-helper" ;;
    *) echo "${XDG_DATA_HOME:-$HOME/.local/share}/terminal-helper" ;;
  esac
}
# Programs that aren't bash (dotnet, the browser) need C:\ paths on Windows.
native_path() { if ((on_windows)); then cygpath -w "$1"; else echo "$1"; fi; }

data=${TERMINAL_HELPER_DATA:-$(default_data_dir)}
[[ -d $data ]] || mkdir -p -m 700 "$data" # private when we create it; an existing folder keeps its permissions
build_dir=$here/.build
url=http://127.0.0.1:$port

if ((install)); then
  apps=${XDG_DATA_HOME:-$HOME/.local/share}/applications
  mkdir -p "$apps"
  cat > "$apps/terminal-helper.desktop" << EOF
[Desktop Entry]
Type=Application
Name=Terminal Helper
Comment=Terminal commands for your setup, and a board to pin them
Exec="$here/terminal-helper.sh"
Icon=$here/client/public/favicon.svg
Terminal=false
Categories=Development;Utility;
StartupWMClass=TerminalHelper
EOF
  echo "Added Terminal Helper to your app launcher ($apps/terminal-helper.desktop)."
  exit 0
fi

# --- Helpers ------------------------------------------------------------------------------------------
running() { curl -sf -o /dev/null --max-time 2 "$url/api/health"; }

save_key() {
  [[ -n $key ]] || return 0
  [[ $key =~ ^[A-Za-z0-9_-]+$ ]] || die "That doesn't look like an API key."
  # Anthropic keys start with sk-ant-; anything else is taken for an OpenAI key.
  local provider=openai name=OpenAI status
  [[ $key == sk-ant-* ]] && { provider=anthropic; name=Claude; }
  # Through stdin, so the key doesn't show up in the process list.
  status=$(printf '{"apiKey":"%s"}' "$key" | curl -s -o /dev/null -w '%{http_code}' -X PUT \
    -H 'Content-Type: application/json' --data-binary @- "$url/api/settings/providers/$provider")
  case $status in
    200) echo "$name API key saved; answers now come from $name." ;;
    422) echo "$name didn't accept that key; pick a provider in the window instead." >&2 ;;
    *) echo "Couldn't save the API key (HTTP $status)." >&2 ;;
  esac
}

find_browser() {
  [[ -n ${TERMINAL_HELPER_BROWSER:-} ]] && { echo "$TERMINAL_HELPER_BROWSER"; return; }
  local candidate
  for candidate in chromium chromium-browser google-chrome google-chrome-stable brave brave-browser \
    microsoft-edge microsoft-edge-stable vivaldi vivaldi-stable; do
    command -v "$candidate" && return
  done
  for candidate in \
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
    "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser" \
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge" \
    "/Applications/Chromium.app/Contents/MacOS/Chromium" \
    "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" \
    "/c/Program Files/Google/Chrome/Application/chrome.exe" \
    "/c/Program Files/BraveSoftware/Brave-Browser/Application/brave.exe"; do
    [[ -x $candidate ]] && { echo "$candidate"; return; }
  done
  return 0
}

# Opens the window in the background; sets $window_pid when we can tell when it closes.
open_window() {
  window_pid=""
  local browser
  browser=$(find_browser)
  if [[ $window == app && -n $browser ]]; then
    # Its own browser profile: a separate window that isn't mixed into your normal browsing.
    "$browser" --app="$url" --user-data-dir="$(native_path "$data/window")" --class=TerminalHelper \
      --no-first-run --no-default-browser-check --window-size=1280,860 > /dev/null 2>&1 &
    window_pid=$!
  elif command -v xdg-open > /dev/null; then
    xdg-open "$url" > /dev/null 2>&1 &
  elif command -v open > /dev/null; then
    open "$url"
  elif ((on_windows)); then
    start "" "$url"
  else
    echo "Open $url in your browser."
  fi
}

# --- Already running? Just open another window ------------------------------------------------------
if running; then
  echo "Terminal Helper is already running; opening another window."
  save_key
  open_window
  exit 0
fi
if curl -s -o /dev/null --max-time 2 "$url"; then
  die "Port $port is used by another program. Try: ./terminal-helper.sh --port $((port + 1))"
fi

# --- Build (first run, and whenever the sources change) -----------------------------------------------
needs_build() {
  if ((rebuild)) || [[ ! -f $build_dir/.built ]]; then return 0; fi
  [[ -n $(find client/src client/public client/angular.json client/package-lock.json server/TerminalHelper.Api \
    -type f -newer "$build_dir/.built" -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/wwwroot/*' \
    -print -quit 2> /dev/null) ]]
}

build_step() {
  echo "  $1"
  shift
  "$@" >> "$log" 2>&1 || {
    tail -n 25 "$log" >&2
    die "The build failed; the full log is $log"
  }
}

command -v dotnet > /dev/null || die "Needs the .NET 10 SDK: https://dot.net/download"
if needs_build; then
  command -v npm > /dev/null || die "Building needs Node.js 22 or newer: https://nodejs.org"
  log=$data/build.log
  : > "$log"
  echo "Building Terminal Helper (the first time takes a minute)..."
  if [[ ! -d client/node_modules || client/package-lock.json -nt client/node_modules/.package-lock.json ]]; then
    build_step "Installing packages" npm --prefix client ci --no-audit --no-fund
  fi
  build_step "Building the app" env NG_CLI_ANALYTICS=false npm --prefix client run build
  rm -rf -- "${build_dir:?}"
  build_step "Building the server" dotnet publish server/TerminalHelper.Api -c Release -o "$build_dir" --nologo
  touch "$build_dir/.built"
fi

# --- Run ----------------------------------------------------------------------------------------------
(
  cd "$build_dir"
  TERMINAL_HELPER_DATA=$(native_path "$data") ASPNETCORE_URLS=$url exec dotnet TerminalHelper.Api.dll
) > "$data/server.log" 2>&1 &
server_pid=$!
# Whatever ends this script (window closed, Ctrl+C, logout) stops the server too.
trap 'kill "$server_pid" 2> /dev/null || true' EXIT

for _ in $(seq 1 80); do
  running && break
  kill -0 "$server_pid" 2> /dev/null || { tail -n 20 "$data/server.log" >&2; die "The server didn't start; see $data/server.log"; }
  sleep 0.25
done
running || die "The server didn't answer in time; see $data/server.log"

save_key
open_window
echo "Terminal Helper is running at $url. Close its window (or press Ctrl+C here) to quit."

if [[ -n $window_pid ]]; then
  wait "$window_pid" || true
  # If a window with this profile was already open, the browser handed the new window to it and the
  # process we started exited at once. Wait for that browser instead.
  if command -v pgrep > /dev/null; then
    while pgrep -f -- "--user-data-dir=$(native_path "$data/window")" > /dev/null; do sleep 2; done
  fi
else
  wait "$server_pid"
fi
