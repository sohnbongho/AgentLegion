AgentLegion - 배포 패키지
==========================

여러 Claude Code 에이전트를 job 단위로 나눠 돌리는 로컬 웹 앱입니다. (WSL / Windows PowerShell 지원)

필요한 것
---------
- Windows 10/11 (패키지를 만든 아키텍처와 같아야 함. 기본: x64)
- .NET 설치는 필요 없습니다 (자체 포함 패키지). `deploy.bat /fd`로 만든 패키지라면 ASP.NET Core 7 런타임이 필요합니다.
- git (Windows 쪽 job을 쓰려면 Windows용 Git, WSL job은 WSL 안의 git)
- Claude Code (`claude`)  - WSL job은 WSL 안에, Windows job은 Windows에 설치하고 한 번 로그인해 두세요.
- WSL job을 쓸 때만 WSL 배포판 (예: Ubuntu)
- (선택) 에디터 버튼용 `code`(VS Code 또는 Cursor)가 PATH에 있으면 됩니다.

실행
----
1. 압축을 풀고 `run.bat`을 실행합니다. (브라우저가 자동으로 열립니다. 기본 주소 http://localhost:5165)
   다른 포트: `run.bat 6000`
2. 처음에는 Settings에서 WSL 배포판, jobs 폴더, 기본 repo(필요하면 Windows jobs 폴더)를 저장합니다.
3. Jobs에서 job을 추가하고, 사이드바 Agents에서 job을 눌러 터미널을 엽니다.
   자세한 사용법은 첫 화면(Dashboard)과 README.md를 참고하세요.
4. 끝낼 때는 실행 창을 닫거나 Ctrl+C. 앱을 끄면 실행 중인 모든 job 세션도 같이 종료됩니다(대화 기록은 남아서 다음에 이어집니다).

폴더 구성
---------
- AgentLegion.exe 와 dll  : 앱 본체
- legion.ps1             : job을 관리하는 스크립트 (앱이 호출합니다. 이 폴더에서 옮기지 마세요)
- run.bat                : 실행기

설정과 기록의 위치
------------------
설정(legion.json), 이름/폴더를 바꾼 job 정보(jobs.json), 로그(logs\)는 이 폴더가 아니라
  %LOCALAPPDATA%\AgentLegion   (예: C:\Users\<이름>\AppData\Local\AgentLegion)
에 저장됩니다. 그래서 패키지에는 들어 있지 않고, 압축을 푼 위치나 실행 방법(run.bat, exe 직접 실행)과
상관없이 이 PC에서는 항상 같은 설정을 씁니다. 새 버전으로 교체할 때 이 폴더는 건드리지 마세요.
설정을 처음부터 다시 하려면 위 폴더를 지우면 됩니다. 위치를 바꾸려면 환경 변수 AGENTLEGION_HOME을 지정하세요.

문제 해결
---------
- 점검: PowerShell에서 `.\legion.ps1 doctor` (Windows job은 `.\legion.ps1 doctor -Target windows`)
- 인터넷에서 받은 zip이라 실행이 막히면: PowerShell에서 `Get-ChildItem -Recurse | Unblock-File`
- 포트가 이미 사용 중이면 `run.bat 6000`처럼 다른 포트를 지정하세요.
- 앱은 localhost에서만 열립니다. 터미널은 WSL/PowerShell 셸 접근과 같으므로 외부에 노출하지 마세요.
