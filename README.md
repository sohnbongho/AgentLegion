# AgentLegion

Windows에서 WSL 위의 여러 Claude Code 에이전트를 관리하는 워크스페이스 도구.
각 job은 `~/agentjobs/<job>` 에 독립 clone + 전용 git branch(`agent/<job>`)를 가진다.

## 요구사항
- Windows 10/11, WSL(배포판 안에 `git`, `claude` 설치 및 로그인 완료)
- Windows Terminal(`wt.exe`, 없으면 일반 창으로 대체)

## 사용
```powershell
.\legion.ps1 init -Repo git@github.com:me/proj.git -Distro Ubuntu
.\legion.ps1 add job1                       # ~/agentjobs/job1, branch agent/job1
.\legion.ps1 add job2 -Branch feature/login
.\legion.ps1 add job3 -Repo git@github.com:me/other.git   # job별 다른 repo
.\legion.ps1 status
.\legion.ps1 start job1                     # 새 탭에서 claude 실행
.\legion.ps1 start-all
.\legion.ps1 run job2 -Prompt "테스트 고치고 커밋해줘"
.\legion.ps1 run job2 -Prompt "..." -TimeoutSec 300   # 기본 600초, 초과 시 오류
.\legion.ps1 diff job1                      # base 대비 커밋/변경 요약
.\legion.ps1 push job1                      # job branch를 origin에 push
.\legion.ps1 merge job1 [-Push]             # job branch를 main에 --no-ff 병합 (-Push 시 main push)
.\legion.ps1 doctor                         # WSL/git/claude/repo/인증 점검
.\legion.ps1 remove job1 [-Force]           # 미커밋/미push 작업이 있으면 -Force 필요
```

## 웹 UI (Blazor)
`dotnet run`으로 실행. 처음에는 **Settings**에서 WSL 배포판, jobs 루트, 기본 repo를 저장한다.

- **Jobs**: job 추가/삭제, Diff, Push, 세션 상태 확인
- **사이드바 Agents**: job을 추가하면 메뉴가 하나씩 생긴다(초록 점 = 세션 실행 중, 노란 숫자 = 미커밋 변경 수)
- **job 메뉴(`/jobs/<job>`)**: 해당 job 폴더에서 WSL `claude`가 실행되는 터미널(xterm.js + ConPTY).
  세션은 서버가 소유하므로 다른 메뉴로 이동하거나 탭을 닫아도 계속 실행되고, 다시 열면 화면이 복원된다.
  Stop/Start 버튼으로 종료·재시작하며, 웹 서버를 종료하면 모든 세션이 종료된다.

- **상단바**: job 페이지에서 현재 job, git branch, 폴더, 누적 토큰 사용량을 표시한다(20초마다 갱신).
  토큰은 WSL의 `~/.claude/projects/<폴더>/*.jsonl` 대화 기록을 합산한 값이며(`legion.ps1 usage <job>`, WSL에 `python3` 필요),
  표시값은 input + output + cache write이고 cache read는 따로 보여 준다.

- **상태 표시**(사이드바/Jobs/터미널 헤더): `진행 중` / `응답 대기` / `중지`.
  Claude Code가 터미널 제목(OSC 0)으로 내보내는 상태를 읽는다: 작업 중에는 `◐`/`◑`가 교대로, 대기·질문 중에는 `✳`가 붙는다.
  화면 갱신 방식이나 탭을 열어 두었는지와 무관하게 동작한다. 전환 기록은 `logs/session-state.log`에 남는다.
  `legion.json`의 `"stateDetection": "activity"`로 바꾸면 Claude가 아닌 프로그램용으로 "출력이 계속 나오면 진행 중"으로 판정한다.
  (제목이 꺼진 환경 — `CLAUDE_CODE_DISABLE_TERMINAL_TITLE`, 상태 접두어를 쓰지 않는 설정 — 에서는 항상 `응답 대기`로 보일 수 있다.)

- **WSL 정보**: 사이드바 하단(배포판 이름, WSL 버전, 실행 상태)과 job 상단바 칩에 표시되고, 마우스를 올리면 WSL 패키지 버전·OS·커널·사용자@호스트가 보인다.
  30초마다 갱신한다. 상세 정보는 **이미 실행 중인 배포판에서만** 조회하므로, 꺼져 있는 배포판을 깨우지 않는다.

- **Windows PowerShell job**(예: Unity 작업): job을 추가할 때 Environment에서 `Windows PowerShell`을 고르면, WSL이 아니라 **Windows에서
  `powershell.exe` → `claude`** 로 실행된다. 폴더는 `%USERPROFILE%\agentjobs\<job>`(Settings의 *Jobs root (Windows PowerShell)*로 변경)에
  clone되고, 같은 방식으로 `agent/<job>` 브랜치를 쓴다. 사이드바에는 `PS` 태그, 목록에는 `PowerShell` 배지가 붙는다.
  Windows에 `git`과 `claude`가 설치되어 있어야 하며(`legion.ps1 doctor -Target windows`로 점검), 새 폴더에서 처음 실행하면
  claude의 "이 폴더를 신뢰하시겠습니까?" 확인이 터미널에 나타난다. job 이름은 WSL/Windows 전체에서 유일해야 한다.
  CLI: `.\legion.ps1 add unity1 -Target windows -Repo <url>` (그 외 명령은 job이 있는 환경을 자동으로 찾는다).
  Windows job의 토큰 사용량은 `%USERPROFILE%\.claude\projects`에서 집계한다(python 불필요).

> 보안: 터미널은 WSL 셸 접근과 같다. 서버는 `localhost`에만 바인딩해서 쓰고, 외부에 노출하지 말 것.

실행 정책 오류 시: `powershell -ExecutionPolicy Bypass -File .\legion.ps1 ...`

설정은 `legion.json`(gitignore 대상)에 저장된다.
