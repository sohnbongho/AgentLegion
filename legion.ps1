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
  [string]$Prompt
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
function Invoke-Wsl($cfg, [string]$script) {
  $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($script -replace "`r", '')))
  & wsl.exe @(Wsl-Args $cfg) -- bash -lc "echo $b64 | base64 -d | bash -l"
  if ($LASTEXITCODE -ne 0) { throw "WSL command failed (exit $LASTEXITCODE)" }
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
  Invoke-Wsl $cfg "cd $($cfg.jobsRoot)/$Name && $($cfg.claudeCmd) -p '$p'"
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
  'remove'    { Cmd-Remove (Get-Config) }
  default {
    @'
AgentLegion commands:
  init -Repo <url> [-Distro <name>] [-Root ~/agentjobs]   write legion.json
  add <job> [-Branch <name>]    clone repo to <root>/<job>, checkout branch (default agent/<job>)
  list | status                 show jobs, branches, uncommitted changes
  start <job>                   open Windows Terminal tab running claude in that job
  start-all                     open a tab for every job
  run <job> -Prompt "<text>"    non-interactive claude -p in that job
  remove <job>                  delete job folder
'@ | Write-Host
  }
}
