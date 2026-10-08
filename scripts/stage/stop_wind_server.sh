#!/bin/bash
set -euo pipefail

SESSION_NAME="wind"

if ! tmux has-session -t "$SESSION_NAME" 2>/dev/null; then
  echo "tmux session '$SESSION_NAME' does not exist."
  exit 0
fi

echo "Stopping tmux session '$SESSION_NAME'..."
tmux kill-session -t "$SESSION_NAME"
echo "tmux session '$SESSION_NAME' stopped."

sleep 1

ss -ntpl
