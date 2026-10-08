#!/bin/bash

SESSION="wind"
WIND_HOME="${HOME}/wind/data_local"

# 1) 세션이 있으면 실패
if tmux has-session -t "$SESSION" 2>/dev/null; then
	echo "[ERROR] wind is founded."
	exit 1
fi

echo "location:${WIND_HOME}"

cd $WIND_HOME

tmux new-session -d -s "$SESSION"
tmux send-keys -t "$SESSION:0" "./wind -P -d 0 wind0 > ./logs/log.wind0 2>&1 &" C-m

tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:1" "./wind -P -d 0 wind1 > ./logs/log.wind1 2>&1 &" C-m

tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:2" "./wind -P -d 0 wind2 > ./logs/log.wind2 2>&1 &" C-m

tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:3" "./wind -P -D -d 0 wind3 > ./logs/log.wind3 2>&1 &" C-m

## 세션 서버 시작
tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:4" "./sessionServer -d 0 --metrics=true --logoutput=console wind1000 > ./logs/log.session 2>&1 &" C-m

tmux attach -t "$SESSION_NAME"

