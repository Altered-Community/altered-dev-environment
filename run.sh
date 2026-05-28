#!/usr/bin/env bash
# Launch the Altered local dev environment (Aspire AppHost).
# Checks prerequisites (.NET SDK, Aspire CLI, Docker) and offers to install the
# missing ones for you. Usage: ./run.sh [extra aspire args]
set -euo pipefail
cd "$(dirname "$0")"

confirm_yes() { # $1 = question
  read -r -p "$1 [Y/n] " ans
  [[ -z "$ans" || "$ans" =~ ^([yY]|yes|[oO]|oui)$ ]]
}

# --- .NET 10 SDK ---
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET SDK is not installed."
  if confirm_yes "Install the .NET 10 SDK now (official dotnet-install script, into ~/.dotnet)?"; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
    export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
    echo 'Add this to your shell profile to make it permanent: export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"'
  fi
  command -v dotnet >/dev/null 2>&1 || { echo "The .NET SDK is still not available. Open a new terminal (or fix PATH) and re-run ./run.sh." >&2; exit 1; }
fi

# --- Aspire CLI ---
if ! command -v aspire >/dev/null 2>&1; then
  echo "The Aspire CLI is not installed."
  if confirm_yes "Install it now (dotnet tool install -g aspire.cli)?"; then
    dotnet tool install -g aspire.cli
    export PATH="$HOME/.dotnet/tools:$PATH"
  fi
  command -v aspire >/dev/null 2>&1 || { echo "The 'aspire' CLI is still not available. Open a new terminal (or fix PATH) and re-run ./run.sh." >&2; exit 1; }
fi

# --- Docker ---
if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is not installed."
  if [[ "$(uname -s)" == "Linux" ]] && confirm_yes "Install Docker Engine now (get.docker.com, needs sudo)?"; then
    curl -fsSL https://get.docker.com | sudo sh
    echo "Docker installed. You may need to add your user to the 'docker' group (sudo usermod -aG docker \$USER) and re-login, then re-run ./run.sh."
    exit 0
  fi
  echo "Docker is required. Install Docker (Desktop on macOS/Windows, Engine on Linux) and re-run ./run.sh." >&2
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  echo "Docker is installed but not running. Start Docker, then re-run ./run.sh." >&2
  exit 1
fi

echo "Starting Altered dev environment (Aspire). First run builds the auth + decks images and installs PHP deps; it can take a few minutes."

# Open the Aspire dashboard automatically once it's ready. The CLI logs the
# dashboard login URL (with its one-time token) to ~/.aspire/logs/cli_*.log;
# a background watcher finds it and opens the browser, leaving the interactive
# AppHost in the foreground.
open_dashboard() {
  local log_dir="$HOME/.aspire/logs" deadline url log opener
  deadline=$(( $(date +%s) + 600 ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    log=$(ls -t "$log_dir"/cli_*.log 2>/dev/null | head -1)
    if [ -n "$log" ]; then
      url=$(grep -hoE 'Login to the dashboard at https?://[^ ]+' "$log" 2>/dev/null | tail -1 | sed 's/.*at //')
      if [ -n "$url" ]; then
        if command -v xdg-open >/dev/null 2>&1; then xdg-open "$url" >/dev/null 2>&1 || true
        elif command -v open >/dev/null 2>&1; then open "$url" >/dev/null 2>&1 || true; fi
        return
      fi
    fi
    sleep 0.8
  done
}
open_dashboard &
opener_pid=$!
trap 'kill "$opener_pid" 2>/dev/null || true' EXIT

aspire run "$@"
