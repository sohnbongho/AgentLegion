#!/bin/bash

SESSION="wind"
WIND_HOME="${HOME}/wind/data_local"

# wind prints EUC-KR: convert each line to UTF-8 before tee (iconv would hold the output until the server exits)
TO_UTF8="perl -MEncode -pe 'BEGIN{\$|=1} \$_=encode(\"UTF-8\", decode(\"cp949\", \$_))'"

# 1) 세션이 있으면 실패
if tmux has-session -t "$SESSION" 2>/dev/null; then
	echo "[ERROR] wind is founded."
	exit 1
fi

echo "location: ${WIND_HOME}"

cd $WIND_HOME

ls -al wind
chmod +x wind

tmux new-session -d -s "$SESSION"
tmux send-keys -t "$SESSION:0" "./wind -P -d 0 wind0 2>&1 | $TO_UTF8 | tee ./logs/log.wind0 &" C-m

tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:1" "./wind -P -d 0 wind1 2>&1 | $TO_UTF8 | tee ./logs/log.wind1 &" C-m

tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:2" "./wind -P -D -d 0 wind2 2>&1 | $TO_UTF8 | tee ./logs/log.wind2 &" C-m

## 세션 서버 시작
tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:3" "./sessionServer -d 0 --metrics=true --logoutput=console wind1000 2>&1 | tee ./logs/log.session &" C-m

tmux attach -t "$SESSION_NAME"

