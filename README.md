# AgentLegion

여러 Claude Code 에이전트를 **job 단위**로 나눠 돌리는 로컬 웹 앱(+ CLI)입니다.
job마다 독립된 폴더(git clone)와 전용 브랜치 `agent/<job>`를 가지므로 에이전트들의 작업이 서로 섞이지 않습니다.
job은 **WSL**에서 돌릴 수도, **Windows PowerShell**에서 돌릴 수도 있습니다(예: Unity 작업).

- 브라우저에서 job별 터미널(claude)을 열고, 다른 메뉴로 이동해도 세션은 계속 실행됩니다.
- 다시 열면 마지막 대화를 이어서 시작합니다(`claude --resume`).
- 진행 중 / 응답 대기 상태, 브랜치·repo·폴더·토큰 사용량을 한눈에 봅니다.
- 에디터(VS Code/Cursor)와 일반 터미널을 job 폴더에서 바로 엽니다.

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
| Jobs | job 추가, 목록(환경·브랜치·변경 수·세션 상태), Open / VS Code / Terminal / Edit / Diff / Push / Remove |
| job 화면 (`/jobs/<job>`) | claude 터미널 + 상단바 정보 + 같은 도구 버튼 |
| Settings | WSL 배포판, jobs 폴더, 기본 repo, Windows jobs 폴더 |
| 사이드바 | Agents 목록(상태 점·라벨·`PS` 태그·변경 수), 하단에 WSL 상태 |

### job 환경: WSL / Windows PowerShell
- **WSL job**: `<jobs 폴더>/<이름>`에 clone, WSL 안에서 `claude` 실행.
- **Windows job**: `<Windows jobs 폴더>\<이름>`에 clone(긴 경로 허용), Windows에서 `powershell.exe` → `claude` 실행.
  사이드바에 `PS` 태그, 목록에 `PowerShell` 배지가 붙습니다. 새 폴더에서 처음 실행하면 claude의 "폴더를 신뢰하시겠습니까?"가 터미널에 나타나므로 직접 답합니다.
- job 이름은 두 환경 전체에서 유일해야 합니다.

### 세션
- **계속 실행**: 세션은 서버가 소유합니다. 다른 메뉴로 이동하거나 탭을 닫아도 계속 실행되고, 다시 열면 화면이 복원됩니다. Stop / Start 버튼으로 종료·재시작합니다.
- **이어서 작업**: job을 시작하면 그 폴더의 **가장 최근 Claude 대화**를 `claude --resume <세션 ID>`로 이어 엽니다. 헤더에 `이어서 · <ID 앞 8자>` 배지가 보이고, **New session** 버튼(Jobs 목록과 job 화면 모두, 실행 중에도)으로 새 대화를 시작할 수 있습니다. 실행 중인 세션은 확인을 거쳐 종료한 뒤 시작하며, 이전 대화는 Claude 기록에 남습니다.
  대화 기록은 디스크(`~/.claude/projects/<폴더>/`)에 남으므로 앱을 껐다 켜도 이어집니다. 메시지 없이 열었다 닫은 기록은 건너뜁니다.
- **상태 표시**(사이드바/Jobs/job 헤더): `진행 중` / `응답 대기` / `중지`.
  Claude Code가 터미널 제목(OSC 0)으로 내보내는 상태를 읽습니다 — 작업 중 `◐`/`◑` 교대, 대기·질문 중 `✳`. 화면 갱신 방식이나 탭을 열어 두었는지와 무관합니다.
  전환 기록은 `logs/session-state.log`에 남습니다.
- 웹 서버를 종료하면 모든 세션도 종료됩니다.

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
- **Diff / Push / Remove**: 변경 확인, job 브랜치 push, 삭제. 삭제는 미커밋/미push 작업이 있으면 거부하며, 루트 밖으로 옮긴 job은 폴더를 지우지 않고 목록에서만 뺍니다.
  병합은 CLI의 `merge`를 씁니다.

### 터미널 복사/붙여넣기
- 마우스로 선택 → `Ctrl+C` 복사(선택이 없으면 평소처럼 **중단 신호**). `Ctrl+Shift+C`는 항상 복사만 합니다.
- 붙여넣기 `Ctrl+V` / `Ctrl+Shift+V`.
- **오른쪽 클릭**: 선택이 있으면 복사(선택 해제), 없으면 붙여넣기. 처음 붙여넣을 때 브라우저가 클립보드 읽기 권한을 물을 수 있고, 거부하면 `Ctrl+V`를 쓰라는 안내가 뜹니다. `Shift`+오른쪽 클릭은 브라우저 기본 메뉴입니다.
- `Ctrl+V`는 텍스트 붙여넣기에 쓰이므로, claude의 클립보드 **이미지** 붙여넣기 단축키는 환경에 따라 다른 키를 써야 합니다.

## 설정 파일
프로젝트 폴더(`legion.ps1`과 같은 위치)에 만들어지며 모두 git에서 제외됩니다.

| 파일 | 내용 |
|---|---|
| `legion.json` | 환경 설정(Settings 화면이 저장) |
| `jobs.json` | 이름/폴더가 기본 규칙과 다른 job만 기록(`edit`이 관리) |
| `logs/session-state.log` | 세션 상태 전환 기록(상태 표시가 이상할 때 원인 확인용) |

`legion.json` 키:

| 키 | 기본 | 설명 |
|---|---|---|
| `distro` | (기본 배포판) | WSL 배포판 이름 |
| `jobsRoot` | `~/agentjobs` | WSL job 폴더 |
| `repo` | — | 기본 repo (URL 또는 절대경로, `~` 불가) |
| `claudeCmd` | `claude` | WSL에서 실행할 명령 (끝에 ` --resume <id>`가 붙을 수 있음) |
| `windowsJobsRoot` | `%USERPROFILE%\agentjobs` | Windows job 폴더 |
| `windowsClaudeCmd` | `claude` | Windows에서 실행할 명령 |
| `stateDetection` | `title` | `activity`로 바꾸면 "출력이 계속 나오면 진행 중"으로 판정(Claude가 아닌 프로그램용) |
| `resumeLastSession` | `true` | `false`면 항상 새 대화로 시작 |
| `codeCmd` | `code` | 에디터 명령 (`cursor`, `code-insiders` …) |

## CLI (`legion.ps1`)
웹 UI가 내부에서 쓰는 스크립트이며 직접 실행할 수도 있습니다. `-Target windows`를 주지 않는 명령은 job이 있는 환경을 자동으로 찾습니다.
```powershell
.\legion.ps1 init -Repo git@github.com:me/proj.git -Distro Ubuntu [-Root ~/agentjobs] [-WindowsRoot D:\jobs]
.\legion.ps1 add job1 [-Branch feature/x] [-Repo <url>] [-Target windows]   # 기본 브랜치 agent/<job>
.\legion.ps1 status [-Json]                  # 모든 job: 환경, 브랜치, 미커밋 변경 수, repo, 폴더
.\legion.ps1 edit job1 [-NewName n] [-Branch b] [-Repo url] [-NewPath dir]
.\legion.ps1 start job1 | start-all          # Windows Terminal 새 탭에서 claude 실행
.\legion.ps1 run job1 -Prompt "..." [-TimeoutSec 600]   # 비대화형 claude -p
.\legion.ps1 diff job1 [-Base main]          # base 대비 커밋/변경 요약
.\legion.ps1 push job1                       # job 브랜치 push
.\legion.ps1 merge job1 [-Base main] [-Push] # job 브랜치를 --no-ff 병합 (-Push 시 base push)
.\legion.ps1 usage job1 [-Json]              # 토큰 사용량
.\legion.ps1 last-session job1 [-Json]       # 마지막 Claude 대화 ID
.\legion.ps1 code job1 | shell job1          # 에디터 / 일반 터미널 열기
.\legion.ps1 doctor [-Target windows]        # WSL·Windows, git, claude, repo, 인증 점검
.\legion.ps1 remove job1 [-Force]            # 삭제 (미커밋/미push 작업이 있으면 -Force 필요)
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
- 패키지에는 `AgentLegion.exe`, `legion.ps1`, `run.bat`, `README-DEPLOY.txt`가 들어갑니다. **개인 설정(`legion.json`, `jobs.json`, `logs`)은 넣지 않습니다.**
- 받는 PC: 압축을 풀고 `run.bat`(포트 변경 `run.bat 6000`)을 실행 → Settings 저장. git과 Claude Code(WSL job이면 WSL 안에, Windows job이면 Windows에)가 필요합니다.
- 새 버전으로 교체할 때는 설정 파일을 지우지 말고 나머지만 덮어쓰면 됩니다.

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
