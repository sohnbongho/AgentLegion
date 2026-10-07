# AgentLegion

여러 Claude Code 에이전트를 **job 단위**로 나눠 돌리는 로컬 웹 앱(+ CLI)입니다.
job마다 독립된 폴더(git clone)와 전용 브랜치 `agent/<job>`를 가지므로 에이전트들의 작업이 서로 섞이지 않습니다.
job은 **WSL**에서 돌릴 수도, **Windows PowerShell**에서 돌릴 수도 있습니다(예: Unity 작업).

- 브라우저에서 job별 터미널(claude)을 열고, 다른 메뉴로 이동해도 세션은 계속 실행됩니다.
- 다시 열면 마지막 대화를 이어서 시작합니다(`claude --resume`).
- 진행 중 / 응답 대기 상태, 브랜치·repo·폴더·토큰 사용량을 한눈에 봅니다.
- 에디터(VS Code/Cursor)와 일반 터미널을 job 폴더에서 바로 엽니다.
- job마다 **Claude 이름**(`claude --name`)을 붙여, Claude 세션끼리 이름으로 메시지를 주고받을 수 있습니다.

## 목차
1. [요구사항](#요구사항) · 2. [빠른 시작](#빠른-시작) · 3. [웹 UI 기능](#웹-ui-기능) · 4. [설정 파일](#설정-파일)
5. [CLI](#cli-legionps1) · 6. [다른 PC에 배포하기](#다른-pc에-배포하기) · 7. [문제 해결](#문제-해결) · 8. [보안](#보안)

## 요구사항
- Windows 10/11
- `git`, Claude Code(`claude`) — **WSL job**이면 WSL 배포판 안에, **Windows job**이면 Windows에 설치하고 한 번 로그인해 둡니다.
- WSL job을 쓸 때만 WSL 배포판(예: Ubuntu). 토큰 집계는 WSL에 `python3`이 필요합니다(Windows job은 불필요).
- Windows Terminal(`wt.exe`)은 선택입니다(없으면 일반 콘솔 창으로 대체).
- 개발/빌드: Visual Studio 또는 .NET SDK (프로젝트는 net7.0).

## 빠른 시작
```powershell
dotnet run          # 또는 Visual Studio에서 실행 → http://localhost:5165
```
1. **Settings**에서 WSL 배포판, jobs 폴더(기본 `~/agentjobs`), 기본 repo를 저장합니다. Windows job을 쓸 거면 *Jobs root (Windows PowerShell)*도 지정합니다(기본 `%USERPROFILE%\agentjobs`).
2. **Jobs**에서 이름과 환경(*WSL* / *Windows PowerShell*)을 고르고 **Add**합니다. 브랜치·repo는 비우면 기본값(`agent/<이름>`, 기본 repo)을 씁니다.
3. 사이드바 **Agents**에서 job을 누르면 그 폴더에서 `claude`가 실행되는 터미널이 열립니다.

첫 화면(**Dashboard**)에도 같은 안내가 있습니다.

## 웹 UI 기능

### 화면 구성
| 화면 | 내용 |
|---|---|
| Dashboard | 사용법 안내 |
| Jobs | job 추가, 목록(Claude 이름·환경·브랜치·변경 수·세션 상태), Open / VS Code / Terminal / Edit / Diff / Push / Remove, (WSL job) 서버 Build / Deploy / Stop / Run, 서버 상태 칩 |
| job 화면 (`/jobs/<job>`) | claude 터미널 + `Claude · <이름>` 배지 + 상단바 정보 + 같은 도구 버튼 + (WSL job) 서버 Build / Deploy / Stop / Run |
| Redis (`/redis`) | Redis 연결(Host·Port·DB·Password)과 키 트리 탐색·값 확인을 한 화면에서. 값은 EUC-KR로 디코딩 |
| Settings | WSL 배포판, jobs 폴더, 기본 repo, Windows jobs 폴더, job 터미널 글꼴·크기, 서버 빌드·배포(기본 배포 경로·빌드 명령·스크립트 폴더) |
| 사이드바 | Agents 목록(상태 점·라벨·`PS`·`WSL` 태그·변경 수), 하단에 WSL 상태 |

### job 환경: WSL / Windows PowerShell
- **WSL job**: `<jobs 폴더>/<이름>`에 clone, WSL 안에서 `claude` 실행.
- **Windows job**: `<Windows jobs 폴더>\<이름>`에 clone(긴 경로 허용), Windows에서 `powershell.exe` → `claude` 실행.
  사이드바에 `PS` 태그, 목록에 `PowerShell` 배지가 붙습니다(WSL job은 `WSL`). 새 폴더에서 처음 실행하면 claude의 "폴더를 신뢰하시겠습니까?"가 터미널에 나타나므로 직접 답합니다.
- **기존 폴더 사용**: Add job에서 Path를 지정하면 clone하지 않고 그 폴더에서 `git pull --ff-only`만 한 뒤 job으로 등록합니다. Branch를 비워두면 현재 브랜치를 유지합니다. jobs 폴더 밖의 폴더는 Remove해도 지워지지 않고 목록에서만 빠집니다.
- job 이름은 두 환경 전체에서 유일해야 합니다.

### 세션
- **계속 실행**: 세션은 서버가 소유합니다. 다른 메뉴로 이동하거나 탭을 닫아도 계속 실행되고, 다시 열면 화면이 복원됩니다. Stop / Start 버튼으로 종료·재시작합니다.
- **이어서 작업**: job을 시작하면 그 폴더의 **가장 최근 Claude 대화**를 `claude --resume <세션 ID>`로 이어 엽니다. 헤더에 `이어서 · <ID 앞 8자>` 배지가 보이고, **New session** 버튼(Jobs 목록과 job 화면 모두, 실행 중에도)으로 새 대화를 시작할 수 있습니다. 실행 중인 세션은 확인을 거쳐 종료한 뒤 시작하며, 이전 대화는 Claude 기록에 남습니다.
  대화 기록은 디스크(`~/.claude/projects/<폴더>/`)에 남으므로 앱을 껐다 켜도 이어집니다. 메시지 없이 열었다 닫은 기록은 건너뜁니다.
- **상태 표시**(사이드바/Jobs/job 헤더): `진행 중` / `응답 대기` / `중지`.
  Claude Code가 터미널 제목(OSC 0)으로 내보내는 상태를 읽습니다 — 작업 중 `◐`/`◑` 교대, 대기·질문 중 `✳`. 화면 갱신 방식이나 탭을 열어 두었는지와 무관합니다.
  전환 기록은 `logs/session-state.log`에 남습니다.
- 웹 서버를 종료하면 모든 세션도 종료됩니다.

### Claude 이름 (세션 간 메시지)
- 세션은 `claude --name <Claude 이름>`으로 시작합니다. 같은 PC의 다른 Claude 세션은 이 이름으로 메시지를 보내거나(예: `@api-worker 스키마 바꿨으니 테스트 다시 돌려줘`),
  "api-worker가 끝나면 알려줘"처럼 **그 세션이 응답 대기가 되면 알림**을 받을 수 있습니다(Claude Code의 cross-session messaging, 별도 설정 없음).
  필요 버전: WSL·Linux v2.1.224+, Windows 네이티브 v2.1.234+, 끝나면 알림은 v2.1.236+. [문서](https://code.claude.com/docs/en/cross-session-messaging)
- WSL job과 Windows job은 claude 설정 폴더(`~/.claude`)가 서로 달라, **환경이 다른 세션끼리(WSL ↔ Windows) 메시지가 전달되는지는 확인되지 않았습니다.** 의존하기 전에 직접 시험해 보세요.
- Add job / Edit의 **Claude name**에서 정합니다. 비우면 job 이름을 씁니다. 영문·숫자·`_` `.` `-`, 64자 이하이며 job끼리 겹칠 수 없습니다(대소문자 무시).
- 이름은 세션을 시작할 때 적용되므로 **세션을 Stop한 뒤** 바꿉니다. 실행 중인 세션에서 `/rename`으로 바꾼 이름은 AgentLegion에 반영되지 않습니다.
- 표시: Jobs 목록의 *Claude name* 열, job 화면의 `Claude · <이름>` 배지, 사이드바 항목의 툴팁.

### 상단바 (job 화면)
job 이름 · git 브랜치 · **origin repo**(인증 정보 제거) · 폴더 · (Windows job은 "Windows PowerShell" / WSL job은 배포판·WSL 버전·실행 상태) · **누적 토큰**(input + output + cache write, cache read는 별도). 20초마다 갱신합니다.
WSL 칩과 사이드바 하단에 마우스를 올리면 WSL 패키지 버전·OS·커널·사용자@호스트가 보입니다. 꺼져 있는 배포판은 깨우지 않고 목록 정보만 보여 줍니다.

### job 도구 버튼
- **VS Code**: job 폴더를 에디터로 엽니다. 항상 **Windows 쪽에서** 실행합니다 — Windows job은 `code .`, WSL job은 `code --remote wsl+<배포판> <WSL 경로>`(Remote-WSL).
  그래서 WSL에서 Windows 프로그램을 실행하지 못하는 환경(interop 고장)에서도 동작합니다. 에디터에 WSL 확장이 필요합니다. `cursor` 등은 `codeCmd`로 지정합니다.
- **Terminal**: job 폴더에서 **일반 셸**(WSL 셸 / PowerShell)을 Windows Terminal 새 탭으로 엽니다. claude 세션에는 영향이 없습니다.
- **Edit**: **Name, Branch, Repo, 작업 경로**를 고칩니다. 바뀐 항목만, 검증이 모두 끝난 뒤에 적용됩니다.
  - *Name* — 표시용 이름만 바뀝니다(폴더와 대화 기록은 그대로). 기본 규칙(`<루트>/<이름>`)과 달라진 job은 `jobs.json`에 기록됩니다.
  - *Branch* — 있으면 전환, 없으면 현재 커밋에서 새로 만듭니다. 커밋하지 않은 변경이 있으면 거부합니다.
  - *Repo* — origin URL을 바꿉니다.
  - *작업 경로* — 폴더를 새 경로로 **이동**합니다(다른 드라이브면 복사 후 삭제). 대화 기록도 함께 옮겨 resume과 토큰 집계가 유지됩니다. 대상이 비어 있지 않으면 거부합니다.
  - 이름·브랜치·경로는 **세션을 먼저 Stop**해야 바꿀 수 있습니다(Repo는 실행 중에도 가능).
- **Diff / Push / Remove**: 변경 확인, job 브랜치 push, 삭제. 삭제는 git 상태(미커밋/미push)를 검사하지 않고 바로 지우며, 루트 밖으로 옮긴 job은 폴더를 지우지 않고 목록에서만 뺍니다.
  병합은 CLI의 `merge`를 씁니다.

### 서버 빌드 · 배포 · 실행 (WSL job)
job 화면 터미널 위의 줄과 Jobs 목록의 **Server** 열에 있습니다(Windows job에는 없음). `~/script/local`의 서버 스크립트를 job 단위로 쓰도록 옮긴 것입니다. Jobs 목록의 Stop은 게임 서버 중지이고, Actions 열의 Stop(claude 세션 종료)과는 다릅니다. 배포 경로는 job 화면에서 바꾸며, Jobs 목록의 Deploy/Run도 그 경로를 씁니다(버튼에 마우스를 올리면 보임).

| 버튼 | 동작 |
|---|---|
| **Build** | job 폴더에서 빌드 명령(기본 `make`)을 실행합니다. wind 서버의 `make`는 `make all` = clean 후 전체 빌드이며, 바뀐 파일만 빌드하려면 Settings에서 `make wind`로 바꿉니다. 실행 중에는 **취소**로 make와 자식 프로세스를 함께 멈춥니다. |
| **배포 경로** | 이 job의 Deploy 대상이자 Run의 서버 폴더. 기본은 Settings의 *기본 배포 경로*(`~/wind/data`)이고, 칸을 고치면(Enter/포커스 이동) 이 job에만 저장됩니다. **기본값** 버튼으로 되돌립니다. |
| **Deploy** | `deploy_gameServer.sh`처럼 `wind`를 배포 경로로 복사하되, 원본은 `~/wind/server`가 아닌 **이 job 폴더**입니다. 임시 파일로 복사한 뒤 이름을 바꿔 교체하므로 서버가 실행 중이어도 되고(`Text file busy` 없음), 대상이 `../serverBinary/...`를 가리키는 심볼릭 링크면 **링크만 실제 파일로 바뀌고 serverBinary 쪽은 덮어쓰지 않습니다.** 끝나면 원본·대상의 sha1을 보여 줍니다. |
| **Stop** | `stop_wind_server.sh` 실행(tmux 세션 종료). |
| **Run** | `run_gameserver.sh`를 실행하되 `WIND_HOME`을 배포 경로로 바꾸고 마지막 `tmux attach`는 뺍니다. 세션이 이미 있으면 거부하고, 끝나면 tmux 창 수와 wind/sessionServer 프로세스를 보여 줍니다. 서버 화면은 WSL 터미널에서 `attach_wind_server.sh`로 봅니다. |

- 상태 칩(job 화면 왼쪽, Jobs 제목 옆): tmux 세션(`wind`)과 wind 프로세스 수를 20초마다, 그리고 Deploy / Run / Stop이 끝날 때마다 확인합니다(클릭하면 즉시). wind는 뜬 뒤 프로세스 이름을 `WindServer`로 바꾸므로 두 이름을 모두 셉니다. 세션은 있는데 wind가 없으면 `tmux 세션만 있음`으로 표시합니다.
- 출력은 아래 패널(Jobs 목록에서는 표 아래)에 실시간으로 나오고, 다른 화면에 갔다 와도 이어서 보입니다. 한 job에서는 한 번에 하나만, Deploy / Run / Stop은 서버를 공유하므로 모든 job을 통틀어 하나씩만 실행됩니다.
- 스크립트 폴더·tmux 세션 이름·빌드 명령은 Settings에서 바꿉니다. job별 배포 경로는 `deploy-targets.json`에 저장되고, Edit로 이름을 바꿔도 따라갑니다.

### Logs (로그 분석)
왼쪽 메뉴 **Tools → Logs**. Files와 같은 트리에서 폴더를 열고(처음엔 Settings의 *기본 로그 폴더*, 기본 `~/wind/data_local/logs`) 로그 파일을 클릭하면 마지막 500줄(`tail -n 500`)을 보여 줍니다. 수십 MB 로그도 끝부분만 읽으므로 바로 열립니다. 열어 둔 폴더·파일·인코딩·명령 기록은 `logs.json`에 저장되어 다음에 그대로 열리며, Files 탭과는 따로 기억합니다. Settings에서 기본 로그 폴더를 바꾸면 다음에 Logs 탭을 열 때 그 폴더가 열리고, 트리의 🏠 버튼도 그 폴더로 갑니다.

- **명령 칸**: `grep`, `sed`, `head`, `tail -f`, `awk`, `wc`, 파이프(`|`) 등 셸 명령을 그대로 씁니다. 선택한 파일은 **첫 명령 뒤에 자동으로 붙습니다**(`grep -n 실패 | tail -n 50` → `grep -n 실패 "$F" | tail -n 50`). 다른 위치에 넣으려면 `$F`를 직접 쓰고, *선택 파일에 실행*을 끄면 파일을 붙이지 않고 폴더에서 그대로 실행합니다(예: `grep -l ERROR log.*`). Enter로 실행하며, 이전 명령은 칸의 자동 완성으로 다시 고릅니다.
- **빠른 버튼**: 끝 500줄, `tail -f`(실시간), 처음 200줄, `! 경고`(레벨 표시가 `!`인 줄), 줄 수, grep…/sed 범위…(칸에 채워 주기만 함).
- **tail -f**: 새 줄이 바로 나타납니다. **중지**를 누르거나, 다른 명령·파일로 바꾸거나, 다른 탭으로 가면 WSL 안의 프로세스(파이프 전체)가 함께 종료됩니다. `tail -f | grep 패턴`도 줄 단위로 바로 나옵니다(grep·sed를 line-buffered로 실행).
- **인코딩**: *자동* / EUC-KR / UTF-8. 자동은 파일 앞뒤를 보고 UTF-8이 아니면 EUC-KR로 정합니다(ASCII만 있으면 EUC-KR). EUC-KR이면 **명령 자체를 CP949로 바꿔 실행**하므로 `grep 캐릭터`처럼 한글 패턴이 EUC-KR 로그에서도 맞고, 출력은 CP949로 읽어 표시합니다. 파일을 변환하지 않으므로 큰 로그의 `tail`도 빠릅니다. 인코딩을 바꾸면 마지막 명령을 다시 실행합니다.
- 출력은 최근 5,000줄까지 보관합니다(넘으면 앞부분 생략 표시). grep이 아무것도 찾지 못하면(exit 1) `일치 없음`으로 표시합니다.

### 터미널 복사/붙여넣기
- 마우스로 선택 → `Ctrl+C` 복사(선택이 없으면 평소처럼 **중단 신호**). `Ctrl+Shift+C`는 항상 복사만 합니다.
- 붙여넣기 `Ctrl+V` / `Ctrl+Shift+V`.
- **오른쪽 클릭**: 선택이 있으면 복사(선택 해제), 없으면 붙여넣기. 처음 붙여넣을 때 브라우저가 클립보드 읽기 권한을 물을 수 있고, 거부하면 `Ctrl+V`를 쓰라는 안내가 뜹니다. `Shift`+오른쪽 클릭은 브라우저 기본 메뉴입니다.
- `Ctrl+V`는 텍스트 붙여넣기에 쓰이므로, claude의 클립보드 **이미지** 붙여넣기 단축키는 환경에 따라 다른 키를 써야 합니다.

## 설정 파일
사용자 폴더 **`%LOCALAPPDATA%\AgentLegion`**(예: `C:\Users\<이름>\AppData\Local\AgentLegion`)에 저장됩니다.
exe를 어디에 두고 어떻게 실행하든(Visual Studio, `bin`의 exe, 배포 패키지) 같은 PC에서는 같은 설정을 씁니다.
위치를 바꾸려면 환경 변수 `AGENTLEGION_HOME`을 지정하세요. 이전 버전처럼 `legion.ps1` 옆에 있던 `legion.json`/`jobs.json`은 처음 실행할 때 **복사**됩니다(원본은 그대로 둠).

| 파일 | 내용 |
|---|---|
| `legion.json` | 환경 설정(Settings 화면이 저장) |
| `jobs.json` | 이름/폴더가 기본 규칙과 다른 job만 기록(`edit`이 관리) |
| `claude-names.json` | job 이름과 다른 Claude 이름을 가진 job만 기록(`{ "job1": "api-worker" }`, `add`/`edit`이 관리) |
| `deploy-targets.json` | 기본 배포 경로와 다른 경로를 쓰는 job만 기록(`{ "job1": "~/wind/data_local" }`, job 화면의 배포 경로 칸이 관리) |
| `files.json` / `logs.json` | Files / Logs 탭에서 마지막으로 연 폴더·파일(Logs는 인코딩·명령 기록·줄바꿈도) |
| `logs/session-state.log` | 세션 상태 전환 기록(상태 표시가 이상할 때 원인 확인용) |

`legion.json` 키:

| 키 | 기본 | 설명 |
|---|---|---|
| `distro` | (기본 배포판) | WSL 배포판 이름 |
| `jobsRoot` | `~/agentjobs` | WSL job 폴더 |
| `repo` | — | 기본 repo (URL 또는 절대경로, `~` 불가) |
| `claudeCmd` | `claude` | WSL에서 실행할 명령 또는 절대 경로 (끝에 ` --resume <id>`가 붙을 수 있음). Settings에서 지정 가능 |
| `windowsJobsRoot` | `%USERPROFILE%\agentjobs` | Windows job 폴더 |
| `windowsClaudeCmd` | `claude` | Windows에서 실행할 명령 또는 절대 경로. Settings에서 지정 가능 |
| `stateDetection` | `title` | `activity`로 바꾸면 "출력이 계속 나오면 진행 중"으로 판정(Claude가 아닌 프로그램용) |
| `resumeLastSession` | `true` | `false`면 항상 새 대화로 시작 |
| `codeCmd` | `code` | 에디터 명령 (`cursor`, `code-insiders` …) |
| `deployRoot` | `~/wind/data` | WSL job의 기본 배포 경로(Deploy 대상, Run의 `WIND_HOME`). Settings에서 지정 |
| `buildCmd` | `make` | Build가 job 폴더에서 실행하는 명령. Settings에서 지정 |
| `serverScriptDir` | `~/script/local` | `stop_wind_server.sh` / `run_gameserver.sh`가 있는 폴더. Settings에서 지정 |
| `serverSession` | `wind` | 서버 스크립트가 쓰는 tmux 세션 이름(상태 확인·중복 실행 검사용). Settings에서 지정 |
| `logRoot` | `~/wind/data_local/logs` | Logs 탭의 기본 폴더(🏠 버튼). Settings에서 지정 |

## CLI (`legion.ps1`)
웹 UI가 내부에서 쓰는 스크립트이며 직접 실행할 수도 있습니다. `-Target windows`를 주지 않는 명령은 job이 있는 환경을 자동으로 찾습니다.
```powershell
.\legion.ps1 init -Repo git@github.com:me/proj.git -Distro Ubuntu [-Root ~/agentjobs] [-WindowsRoot D:\jobs]
.\legion.ps1 add job1 [-Branch feature/x] [-Repo <url>] [-Target windows] [-ClaudeName api-worker]   # 기본 브랜치 agent/<job>
.\legion.ps1 add job2 -Path D:\work\repo [-Branch b] [-Target windows]   # 기존 clone 사용: clone 없이 git pull만
.\legion.ps1 status [-Json]                  # 모든 job: 환경, 브랜치, 미커밋 변경 수, repo, 폴더
.\legion.ps1 edit job1 [-NewName n] [-Branch b] [-Repo url] [-NewPath dir] [-ClaudeName c | -ClearClaudeName]
.\legion.ps1 start job1 | start-all          # Windows Terminal 새 탭(제목 = Claude 이름)에서 claude --name <Claude 이름> 실행
.\legion.ps1 run job1 -Prompt "..." [-TimeoutSec 600]   # 비대화형 claude -p
.\legion.ps1 diff job1 [-Base main]          # base 대비 커밋/변경 요약
.\legion.ps1 push job1                       # job 브랜치 push
.\legion.ps1 merge job1 [-Base main] [-Push] # job 브랜치를 --no-ff 병합 (-Push 시 base push)
.\legion.ps1 usage job1 [-Json]              # 토큰 사용량
.\legion.ps1 last-session job1 [-Json]       # 마지막 Claude 대화 ID
.\legion.ps1 code job1 | shell job1          # 에디터 / 일반 터미널 열기
.\legion.ps1 doctor [-Target windows]        # WSL·Windows, git, claude, repo, 인증 점검
.\legion.ps1 remove job1                     # 삭제 (git 상태 검사 없이 바로 삭제)
```
실행 정책 오류 시: `powershell -ExecutionPolicy Bypass -File .\legion.ps1 ...`

## 다른 PC에 배포하기
`deploy.bat`이 빌드 결과를 다른 Windows PC에서 바로 실행할 수 있는 패키지로 만듭니다.
```bat
deploy.bat                  :: 자체 포함 win-x64 (받는 PC에 .NET 설치 불필요) → dist\AgentLegion + dist\AgentLegion-win-x64-self-contained.zip
deploy.bat /fd              :: framework-dependent (작음, 받는 PC에 ASP.NET Core 7 런타임 필요)
deploy.bat /rid win-arm64   :: 다른 아키텍처
deploy.bat /nozip           :: zip 생략
```
- 소스를 임시 폴더로 복사해 publish하므로 Visual Studio의 `bin`/`obj`를 건드리지 않습니다. `dist\`는 git에서 제외됩니다.
- 패키지에는 `AgentLegion.exe`, `legion.ps1`, `run.bat`, `README-DEPLOY.txt`가 들어갑니다. 설정은 패키지가 아니라 받는 PC의 `%LOCALAPPDATA%\AgentLegion`에 저장되므로 **개인 설정은 패키지에 들어가지 않습니다.**
- 받는 PC: 압축을 풀고 `run.bat`(포트 변경 `run.bat 6000`)을 실행 → Settings 저장. git과 Claude Code(WSL job이면 WSL 안에, Windows job이면 Windows에)가 필요합니다.
- 새 버전으로 교체해도 설정은 그대로입니다(패키지 폴더를 통째로 바꿔도 됩니다). 같은 PC에서 개발 빌드와 패키지를 번갈아 실행해도 같은 설정을 씁니다.

## 문제 해결
- **환경 점검**: `.\legion.ps1 doctor` (Windows job은 `-Target windows`).
- **상태가 항상 "응답 대기"**: 제목 기반 판정이 꺼진 환경(`CLAUDE_CODE_DISABLE_TERMINAL_TITLE`, 상태 접두어를 쓰지 않는 설정)일 수 있습니다. 시작 후 20초 동안 Claude의 제목이 오지 않으면 자동으로 출력 활동 방식으로 전환되며, `logs/session-state.log`에서 원인을 볼 수 있습니다.
- **VS Code 버튼이 안 열림**: `code`가 Windows PATH에 있어야 하고(에디터의 *Shell Command: Install 'code' command in PATH*), WSL job은 에디터의 WSL 확장이 필요합니다.
- **WSL에서 `.exe`를 실행하지 못함**(`Exec format error`): `systemd=true` 환경에서 `WSLInterop` 등록이 빠진 경우입니다. AgentLegion의 에디터 버튼은 영향이 없지만 WSL 터미널의 `code .` 등에는 영향이 있습니다.
  고치려면 `sudo sh -c 'echo :WSLInterop:M::MZ::/init:PF > /usr/lib/binfmt.d/WSLInterop.conf'` 후 `sudo systemctl restart systemd-binfmt`.
- **폴더를 옮긴 job을 resume**: 대화 기록은 함께 옮겨지지만 이어지지 않으면 *New session*으로 시작하세요.
- **Unity가 열고 있는 폴더**는 삭제·이동이 실패할 수 있습니다(메시지에 이유가 표시됩니다).

## 보안
터미널은 WSL/PowerShell 셸 접근과 같습니다. 서버는 `localhost`에만 바인딩하며(기본 `http://localhost:5165`), 외부에 노출하지 마세요.
