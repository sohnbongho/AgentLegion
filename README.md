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
.\legion.ps1 status
.\legion.ps1 start job1                     # 새 탭에서 claude 실행
.\legion.ps1 start-all
.\legion.ps1 run job2 -Prompt "테스트 고치고 커밋해줘"
.\legion.ps1 remove job1
```

실행 정책 오류 시: `powershell -ExecutionPolicy Bypass -File .\legion.ps1 ...`

설정은 `legion.json`(gitignore 대상)에 저장된다.
