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

rm ./logs/*

chmod +x wind
ls -al wind

#  wind0  │ MasterServerConfi │  │ - 
#  Master Server  전체 서버 관리, 유저 라우팅, 로컬서버 풀 관리
tmux new-session -d -s "$SESSION"
tmux send-keys -t "$SESSION:0" "./wind -P -d 0 wind0 2>&1 | $TO_UTF8 | tee ./logs/log.wind0 &" C-m

#  wind1  │ CacheSyncServerConfig  │ -  │ CacheSync Server  Redis ↔ DB 간 캐릭터 데이터 동기화
tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:1" "./wind -P -d 0 wind1 2>&1 | $TO_UTF8 | tee ./logs/log.wind1 &" C-m

# wind2  │ LoginServerConfig     │ 2│ - │ Login Server  클라이언트 로그인, 캐릭터 선택 처리
tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:2" "./wind -P -d 0 wind2 2>&1 | $TO_UTF8 | tee ./logs/log.wind2 &" C-m

## 세션 서버 시작
tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:3" "./sessionServer -d 0 --metrics=true --logoutput=console wind1000 2>&1 | tee ./logs/log.session &" C-m

#wind11 │ LocalServerConfig     │ 11       │ -          │ Local Server  실제 게임 플레이 처리 (월드, 전투, 이동 등)
tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:4" "./wind -P -D -d 0 wind11 2>&1 | $TO_UTF8 | tee ./logs/log.wind11 &" C-m
#tmux send-keys -t "$SESSION:4" "./wind -P -d 0 wind11 2>&1 | tee ./logs/log.wind11 &" C-m
#tmux send-keys -t "$SESSION:4" "./wind -d 0 wind11 2>&1 | tee ./logs/log.wind11 &" C-m
#tmux send-keys -t "$SESSION:4" "./wind -P -D -d 0 wind11 2>&1 | tee ./logs/log.wind11 &" C-m

tmux new-window -t "$SESSION"
tmux send-keys -t "$SESSION:5" "./wind -P -d 0 wind12 2>&1 | $TO_UTF8 | tee ./logs/log.wind12 &" C-m

tmux attach -t "$SESSION_NAME"

