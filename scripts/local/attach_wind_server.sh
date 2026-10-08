#!/bin/bash
set -euo pipefail

SESSION_NAME="wind"

if ! tmux has-session -t "$SESSION_NAME" 2>/dev/null; then
  echo "tmux session '$SESSION_NAME' is not running."
  echo "run: ./run_wind_tmux.sh"
  exit 1
fi

tmux attach -t "$SESSION_NAME"

