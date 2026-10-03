<#
.SYNOPSIS
  AgentLegion - manage multiple Claude Code agents running in WSL, one git branch per job.

.EXAMPLE
  .\legion.ps1 init -Repo git@github.com:me/proj.git -Distro Ubuntu
  .\legion.ps1 add job1
  .\legion.ps1 add job2 -Branch feature/login
  .\legion.ps1 start-all
  .\legion.ps1 run job1 -Prompt "README 오타 수정하고 커밋해줘"
  .\legion.ps1 status
#>
param(
  [Parameter(Position = 0)][string]$Command = 'help',
  [Parameter(Position = 1)][string]$Name,
  [string]$Repo,
  [string]$Branch,
  [string]$Distro,
  [string]$Root,
  [string]$Prompt,
  [int]$TimeoutSec = 600
)

$ErrorActionPreference = 'Stop'
$ConfigPath = Join-Path $PSScriptRoot 'legion.json'

function Assert-Safe($value, $pattern, $what) {
  if ($value -notmatch $pattern) { throw "Invalid $what`: '$value'" }
}

function Get-Config {
  if (-not (Test-Path $ConfigPath)) { throw "legion.json not found. Run: .\legion.ps1 init -Repo <url>" }
  Get-Content $ConfigPath -Raw | ConvertFrom-Json
}

function Wsl-Args($cfg) { if ($cfg.distro) { @('-d', $cfg.distro) } else { @() } }

# Run a bash script inside WSL. The script is base64-encoded to avoid any quoting issues.
function Invoke-Wsl($cfg, [string]$script, [switch]$NoThrow) {
  $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($script -replace "`r", '')))
  & wsl.exe @(Wsl-Args $cfg) -- bash -lc "echo $b64 | base64 -d | bash -l"
  if ($LASTEXITCODE -ne 0 -and -not $NoThrow) { throw "WSL command failed (exit $LASTEXITCODE)" }
}

function Get-JobBranch($cfg, $name) {
  if ($Branch) { $Branch } else { "agent/$name" }
}

function Cmd-Init {
  Assert-Safe $Repo '^\S+$' 'repo'
  $root = if ($Root) { $Root } else { '~/agentjobs' }
  Assert-Safe $root '^[~/A-Za-z0-9_.-]+$' 'root'
  if ($Distro) { Assert-Safe $Distro '^[A-Za-z0-9_.-]+$' 'distro' }
  [ordered]@{ distro = $Distro; jobsRoot = $root; repo = $Repo; claudeCmd = 'claude' } |
    ConvertTo-Json | Set-Content $ConfigPath -Encoding UTF8
  Write-Host "Wrote $ConfigPath"
}

function Cmd-Add($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  $br = Get-JobBranch $cfg $Name
  Assert-Safe $br '^[A-Za-z0-9._/-]+$' 'branch'
  $repo = $cfg.repo -replace "'", "'\''"
  Invoke-Wsl $cfg @"
set -e
mkdir -p $($cfg.jobsRoot)
cd $($cfg.jobsRoot)
[ -d $Name/.git ] || git clone '$repo' $Name
cd $Name
git checkout $br 2>/dev/null || git checkout -b $br
echo "$Name -> `$(git rev-parse --abbrev-ref HEAD)"
"@
}

function Cmd-List($cfg) {
  Invoke-Wsl $cfg @"
cd $($cfg.jobsRoot) 2>/dev/null || { echo 'no jobs yet'; exit 0; }
printf '%-12s %-30s %s\n' JOB BRANCH CHANGES
for d in */; do
  d=`${d%/}
  [ -d "`$d/.git" ] || continue
  b=`$(git -C "`$d" rev-parse --abbrev-ref HEAD)
  n=`$(git -C "`$d" status --porcelain | wc -l)
  printf '%-12s %-30s %s\n' "`$d" "`$b" "`$n"
done
"@
}

function Start-Job-Tab($cfg, $name) {
  Assert-Safe $name '^[A-Za-z0-9_-]+$' 'job name'
  $dir = "$($cfg.jobsRoot)/$name"
  $wslArgs = @(Wsl-Args $cfg) + @('--cd', $dir, '--', 'bash', '-lc', $cfg.claudeCmd)
  if (Get-Command wt.exe -ErrorAction SilentlyContinue) {
    Start-Process wt.exe -ArgumentList (@('-w', '0', 'new-tab', '--title', $name, 'wsl.exe') + $wslArgs)
  } else {
    Start-Process wsl.exe -ArgumentList $wslArgs
  }
}

function Cmd-Run($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if (-not $Prompt) { throw '-Prompt is required' }
  $p = $Prompt -replace "'", "'\''"
  Invoke-Wsl $cfg "cd $($cfg.jobsRoot)/$Name && timeout $TimeoutSec $($cfg.claudeCmd) -p '$p' < /dev/null" -NoThrow
  if ($LASTEXITCODE -eq 124) {
    throw "claude timed out after ${TimeoutSec}s. If this is unexpected, run '.\legion.ps1 doctor' (auth may be expired; run 'claude' in WSL and /login)."
  }
  if ($LASTEXITCODE -ne 0) { throw "claude exited with code $LASTEXITCODE" }
}

function Cmd-Doctor($cfg) {
  $state = @{ fail = 0 }
  function Check($label, $script, $hint) {
    Write-Host -NoNewline ("{0,-28}" -f $label)
    $out = Invoke-Wsl $cfg $script -NoThrow 2>&1
    $code = $LASTEXITCODE
    if ($code -eq 0) { Write-Host 'OK' -ForegroundColor Green }
    else { Write-Host "FAIL ($code)  $hint" -ForegroundColor Red; $state.fail++ }
  }
  Check 'WSL reachable'      'true'                              'distro name wrong or WSL not installed?'
  Check 'git installed'      'command -v git >/dev/null'         'apt install git'
  Check 'claude installed'   "command -v $($cfg.claudeCmd) >/dev/null" 'install Claude Code in WSL'
  Check 'jobs root writable' "mkdir -p $($cfg.jobsRoot) && test -w $($cfg.jobsRoot)" 'check permissions'
  $repo = $cfg.repo -replace "'", "'\''"
  Check 'repo reachable'     "git ls-remote '$repo' HEAD >/dev/null 2>&1" 'check URL / SSH key / credentials in WSL'
  Check 'claude auth (30s)'  "timeout 30 $($cfg.claudeCmd) -p 'Reply with PONG' < /dev/null >/dev/null 2>&1" "run 'claude' in WSL and /login again"
  if ($state.fail) { throw "$($state.fail) check(s) failed" } else { Write-Host 'All checks passed.' }
}

function Cmd-Remove($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  Invoke-Wsl $cfg "rm -rf $($cfg.jobsRoot)/$Name"
  Write-Host "Removed $Name"
}

switch ($Command) {
  'init'      { Cmd-Init }
  'add'       { Cmd-Add (Get-Config) }
  { $_ -in 'list', 'status' } { Cmd-List (Get-Config) }
  'start'     { Start-Job-Tab (Get-Config) $Name }
  'start-all' {
    $cfg = Get-Config
    $jobs = Invoke-Wsl $cfg "cd $($cfg.jobsRoot) && ls -d */ | tr -d /"
    foreach ($j in $jobs) { if ($j) { Start-Job-Tab $cfg $j } }
  }
  'run'       { Cmd-Run (Get-Config) }
  'doctor'    { Cmd-Doctor (Get-Config) }
  'remove'    { Cmd-Remove (Get-Config) }
  default {
    @'
AgentLegion commands:
  init -Repo <url> [-Distro <name>] [-Root ~/agentjobs]   write legion.json
  add <job> [-Branch <name>]    clone repo to <root>/<job>, checkout branch (default agent/<job>)
  list | status                 show jobs, branches, uncommitted changes
  start <job>                   open Windows Terminal tab running claude in that job
  start-all                     open a tab for every job
  run <job> -Prompt "<text>" [-TimeoutSec 600]   non-interactive claude -p in that job
  doctor                        check WSL, git, claude, repo access, claude auth
  remove <job>                  delete job folder
'@ | Write-Host
  }
}
