@echo off
rem Build a package of AgentLegion that runs on another Windows machine.
rem
rem   deploy.bat                    self-contained win-x64 (no .NET needed there)   -> dist\AgentLegion
rem   deploy.bat /fd                framework-dependent (small; needs the ASP.NET Core 7 runtime there)
rem   deploy.bat /rid win-arm64     another runtime identifier
rem   deploy.bat /nozip             do not create the zip
rem
rem The sources are copied to a temporary folder and published from there, so Visual Studio's bin\ and obj\ are
rem never touched. Personal settings (legion.json, jobs.json, logs) are not packaged.
setlocal EnableExtensions

set "ROOT=%~dp0"
set "RID=win-x64"
set "SELF=true"
set "ZIP=1"

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="/fd" (
  set "SELF=false"
) else if /i "%~1"=="/nozip" (
  set "ZIP=0"
) else if /i "%~1"=="/rid" (
  set "RID=%~2"
  shift
) else (
  echo Unknown option: %~1
  echo Usage: deploy.bat [/fd] [/rid win-x64^|win-arm64] [/nozip]
  exit /b 2
)
shift
goto parse
:parsed

where dotnet >nul 2>&1
if errorlevel 1 (
  echo The .NET SDK was not found. Install it first: https://dotnet.microsoft.com/download
  exit /b 1
)

set "DIST=%ROOT%dist"
set "OUT=%DIST%\AgentLegion"
set "STAGE=%DIST%\_build"
if "%SELF%"=="true" (set "KIND=self-contained") else (set "KIND=framework-dependent")

echo.
echo === AgentLegion deploy: %KIND%, %RID% ===

if exist "%OUT%" rd /s /q "%OUT%"
if exist "%STAGE%" rd /s /q "%STAGE%"
mkdir "%STAGE%" || exit /b 1

echo [1/4] Copying sources (bin, obj, .git, settings and logs are left out)...
robocopy "%ROOT%." "%STAGE%" /E /XD bin obj .vs .git .claude dist logs deploy /XF legion.json jobs.json *.user /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo Copying the sources failed.
  exit /b 1
)

echo [2/4] dotnet publish (Release)...
pushd "%STAGE%"
if "%SELF%"=="true" (
  dotnet publish AgentLegion.csproj -c Release -r %RID% --self-contained true -o "%OUT%" -p:DebugType=none --nologo -v q
) else (
  dotnet publish AgentLegion.csproj -c Release -r %RID% --no-self-contained -o "%OUT%" -p:DebugType=none --nologo -v q
)
set "PUBLISH_RC=%ERRORLEVEL%"
popd
if not "%PUBLISH_RC%"=="0" (
  echo Publish failed ^(exit %PUBLISH_RC%^). A self-contained build needs to download the runtime pack once; check the network.
  exit /b %PUBLISH_RC%
)
if not exist "%OUT%\AgentLegion.exe" (
  echo Publish finished but AgentLegion.exe is missing.
  exit /b 1
)

echo [3/4] Adding legion.ps1, run.bat and the guide...
copy /y "%ROOT%legion.ps1" "%OUT%\" >nul || exit /b 1
copy /y "%ROOT%README.md" "%OUT%\" >nul
rem run.bat is written with CRLF whatever the source file uses (cmd needs CRLF for labels and goto)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = [IO.File]::ReadAllText('%ROOT%deploy\run.bat') -replace '\r?\n', ([string][char]13 + [char]10); [IO.File]::WriteAllText('%OUT%\run.bat', $t, [Text.Encoding]::ASCII)" || exit /b 1
copy /y "%ROOT%deploy\README-DEPLOY.txt" "%OUT%\" >nul || exit /b 1
rd /s /q "%STAGE%"

if "%ZIP%"=="1" (
  echo [4/4] Creating the zip...
  set "ZIPFILE=%DIST%\AgentLegion-%RID%-%KIND%.zip"
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%DIST%\AgentLegion-%RID%-%KIND%.zip' -Force" || exit /b 1
) else (
  echo [4/4] Skipping the zip.
)

echo.
echo Done.
echo   Folder : %OUT%
if "%ZIP%"=="1" echo   Zip    : %DIST%\AgentLegion-%RID%-%KIND%.zip
echo.
echo On the other PC: unzip, run run.bat, then open Settings in the browser.
echo It needs git and Claude Code installed ^(and WSL for WSL jobs^). See README-DEPLOY.txt in the package.
exit /b 0
