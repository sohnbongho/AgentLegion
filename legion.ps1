<#
.SYNOPSIS
  AgentLegion - manage multiple Claude Code agents, one git branch per job.
  A job runs either in WSL (default) or natively in Windows PowerShell (e.g. for Unity work).

.EXAMPLE
  .\legion.ps1 init -Repo git@github.com:me/proj.git -Distro Ubuntu
  .\legion.ps1 add job1                                   # WSL job
  .\legion.ps1 add unity1 -Target windows -Repo <url>     # Windows PowerShell job
  .\legion.ps1 start-all
  .\legion.ps1 run job1 -Prompt "fix the README typo and commit"
  .\legion.ps1 status
#>
param(
  [Parameter(Position = 0)][string]$Command = 'help',
  [Parameter(Position = 1)][string]$Name,
  [string]$Repo,
  [string]$Branch,
  [string]$Distro,
  [string]$Root,
  [string]$WindowsRoot,
  [ValidateSet('', 'wsl', 'windows')][string]$Target = '',
  [string]$NewName,
  [string]$NewPath,
  [string]$Prompt,
  [int]$TimeoutSec = 600,
  [string]$Base,
  [switch]$Push,
  [switch]$Force,
  [switch]$Json
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

function Get-Opt($cfg, $name, $default) {
  $p = $cfg.PSObject.Properties[$name]
  if ($p -and $p.Value) { $p.Value } else { $default }
}

# ----------------------------------------------------------------------------------------------
# Job registry (jobs.json)
# ----------------------------------------------------------------------------------------------
# A job normally lives at <jobs root>/<name> and is found by scanning the root. jobs.json lists only the jobs
# whose name or folder differs from that layout (renamed or moved by `edit`):
#   { "alpha": { "env": "wsl", "path": "/home/me/agentjobs/job1" } }
$RegistryPath = Join-Path $PSScriptRoot 'jobs.json'

function Get-Registry {
  $h = @{}   # PowerShell hashtables compare keys case-insensitively
  if (Test-Path -LiteralPath $RegistryPath) {
    $raw = Get-Content -LiteralPath $RegistryPath -Raw
    if ($raw -and $raw.Trim()) {
      $o = $raw | ConvertFrom-Json
      foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = [pscustomobject]@{ env = $p.Value.env; path = $p.Value.path } }
    }
  }
  $h
}

function Save-Registry($h) {
  if ($h.Count -eq 0) {
    if (Test-Path -LiteralPath $RegistryPath) { [IO.File]::Delete($RegistryPath) }
    return
  }
  $o = [ordered]@{}
  foreach ($k in ($h.Keys | Sort-Object)) { $o[$k] = [ordered]@{ env = $h[$k].env; path = $h[$k].path } }
  $o | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RegistryPath -Encoding UTF8
}

function Get-RegEntry($name) {
  $r = Get-Registry
  if ($r.ContainsKey($name)) { $r[$name] }
}

# Name of the registry entry that points at this folder (a job renamed/moved by `edit`), or $null.
function Find-RegName($envName, [string]$path, $reg) {
  $want = $path.TrimEnd('\', '/')
  foreach ($k in $reg.Keys) {
    if ($reg[$k].env -eq $envName -and ([string]$reg[$k].path).TrimEnd('\', '/') -ieq $want) { return $k }
  }
}

# ----------------------------------------------------------------------------------------------
# WSL helpers
# ----------------------------------------------------------------------------------------------
function Wsl-Args($cfg) { if ($cfg.distro) { @('-d', $cfg.distro) } else { @() } }

# Run a bash script inside WSL. The script is base64-encoded to avoid any quoting issues.
function Invoke-Wsl($cfg, [string]$script, [switch]$NoThrow) {
  $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($script -replace "`r", '')))
  & wsl.exe @(Wsl-Args $cfg) -- bash -lc "echo $b64 | base64 -d | bash -l"
  if ($LASTEXITCODE -ne 0 -and -not $NoThrow) { throw "WSL command failed (exit $LASTEXITCODE)" }
}

# ----------------------------------------------------------------------------------------------
# Windows (native PowerShell) helpers
# ----------------------------------------------------------------------------------------------
function Get-WinRoot($cfg) {
  $r = Get-Opt $cfg 'windowsJobsRoot' (Join-Path $env:USERPROFILE 'agentjobs')
  [Environment]::ExpandEnvironmentVariables($r)
}

function Get-WinClaude($cfg) { Get-Opt $cfg 'windowsClaudeCmd' 'claude' }

# Where a job's folder is: the registry entry if there is one, otherwise <root>/<name>.
function Win-Dir($cfg, $name) {
  $e = Get-RegEntry $name
  if ($e -and $e.env -eq 'windows') { return $e.path }
  Join-Path (Get-WinRoot $cfg) $name
}

function Wsl-Dir($cfg, $name) {
  $e = Get-RegEntry $name
  if ($e -and $e.env -eq 'wsl') { return $e.path }
  "$($cfg.jobsRoot)/$name"
}

function Test-WinJob($cfg, $name) { Test-Path -LiteralPath (Join-Path (Win-Dir $cfg $name) '.git') }

# A job lives in exactly one environment: the registry says so; otherwise a Windows folder wins, else it is a WSL job.
function Get-JobTarget($cfg, $name) {
  if ($Target) { return $Target }
  $e = Get-RegEntry $name
  if ($e) { return $e.env }
  if (Test-WinJob $cfg $name) { 'windows' } else { 'wsl' }
}

# Run git in a Windows job folder. Native stderr is deliberately not redirected (PowerShell 5.1 would turn
# git's progress output into errors); only stdout is returned.
function Git-Win([string]$dir, [string[]]$gitArgs, [switch]$NoThrow) {
  $out = & git -C $dir @gitArgs
  if ($LASTEXITCODE -ne 0 -and -not $NoThrow) { throw "git $($gitArgs -join ' ') failed (exit $LASTEXITCODE)" }
  # emit nothing (not a lone $null) when git printed nothing, so @(...).Count is 0
  if ($null -ne $out) { $out }
}

# Run a block with errors silenced. With $ErrorActionPreference = 'Stop', redirecting a native command's stderr
# (2>$null, *>$null) would otherwise raise a terminating error on the first stderr line.
function Quiet([scriptblock]$block) {
  $old = $ErrorActionPreference
  $ErrorActionPreference = 'SilentlyContinue'
  try { & $block } finally { $ErrorActionPreference = $old }
}

# git in a Windows job folder whose stderr must stay out of our output (status -Json must be clean JSON)
function Git-WinQuiet([string]$dir, [string[]]$gitArgs) {
  Quiet { $o = & git -C $dir @gitArgs 2>$null; if ($LASTEXITCODE -eq 0 -and $null -ne $o) { $o } }
}

function Require-WinJob($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  $dir = Win-Dir $cfg $Name
  if (-not (Test-Path -LiteralPath (Join-Path $dir '.git'))) { throw "job not found: $Name" }
  $dir
}

# origin/HEAD, falling back to main
function Win-Base([string]$dir) {
  if ($Base) { Assert-Safe $Base '^[A-Za-z0-9._/-]+$' 'base'; return $Base }
  $b = Git-WinQuiet $dir @('symbolic-ref', '--short', 'refs/remotes/origin/HEAD') | Select-Object -First 1
  if ($b) { return ($b -replace '^origin/', '') }
  'main'
}

function Get-JobBranch($cfg, $name) {
  if ($Branch) { $Branch } else { "agent/$name" }
}

# ----------------------------------------------------------------------------------------------
# Commands
# ----------------------------------------------------------------------------------------------
function Cmd-Init {
  Assert-Safe $Repo '^\S+$' 'repo'
  $root = if ($Root) { $Root } else { '~/agentjobs' }
  Assert-Safe $root '^[~/A-Za-z0-9_.-]+$' 'root'
  if ($Distro) { Assert-Safe $Distro '^[A-Za-z0-9_.-]+$' 'distro' }
  if ($WindowsRoot) { Assert-Safe $WindowsRoot '^[A-Za-z0-9_%:\\/ .()-]+$' 'windows root' }

  $old = $null
  if (Test-Path $ConfigPath) { $old = Get-Content $ConfigPath -Raw | ConvertFrom-Json }
  $cfg = [ordered]@{ distro = $Distro; jobsRoot = $root; repo = $Repo }
  # keep hand-edited / previously saved settings when re-running init
  $cfg['claudeCmd'] = if ($old) { Get-Opt $old 'claudeCmd' 'claude' } else { 'claude' }
  if ($WindowsRoot) { $cfg['windowsJobsRoot'] = $WindowsRoot }
  elseif ($old -and $old.PSObject.Properties['windowsJobsRoot']) { $cfg['windowsJobsRoot'] = $old.windowsJobsRoot }
  foreach ($k in 'windowsClaudeCmd', 'stateDetection', 'resumeLastSession', 'codeCmd') {
    if ($old -and $old.PSObject.Properties[$k]) { $cfg[$k] = $old.$k }
  }
  $cfg | ConvertTo-Json | Set-Content $ConfigPath -Encoding UTF8
  Write-Host "Wrote $ConfigPath"
}

function Cmd-Add($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  $br = Get-JobBranch $cfg $Name
  Assert-Safe $br '^[A-Za-z0-9._/-]+$' 'branch'
  $repoUrl = if ($Repo) { $Repo } else { $cfg.repo }
  Assert-Safe $repoUrl '^\S+$' 'repo'

  # names are unique across both environments and across jobs renamed/moved by `edit`
  if ((Get-Registry).ContainsKey($Name)) { throw "a job named '$Name' already exists; job names are shared across environments" }

  if ($Target -eq 'windows') { Win-Add $cfg $br $repoUrl; return }

  if (Test-WinJob $cfg $Name) { throw "a Windows job named '$Name' already exists; job names are shared across environments" }
  Assert-FolderFree $cfg 'wsl' "$($cfg.jobsRoot)/$Name"
  $repo = $repoUrl -replace "'", "'\''"
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

function Win-Add($cfg, $br, $repoUrl) {
  # names are unique across environments (a WSL probe that fails is treated as "no such job")
  $wslHas = $false
  try {
    $wslHas = [bool](Quiet { Invoke-Wsl $cfg "test -d $($cfg.jobsRoot)/$Name" -NoThrow *> $null; $LASTEXITCODE -eq 0 })
  } catch { }
  if ($wslHas) { throw "a WSL job named '$Name' already exists; job names are shared across environments" }

  $root = Get-WinRoot $cfg
  New-Item -ItemType Directory -Force -Path $root | Out-Null
  $dir = Join-Path $root $Name
  Assert-FolderFree $cfg 'windows' $dir
  if (-not (Test-Path -LiteralPath (Join-Path $dir '.git'))) {
    if ((Test-Path -LiteralPath $dir) -and (Get-ChildItem -LiteralPath $dir -Force | Select-Object -First 1)) {
      throw "folder exists and is not a git repository: $dir"
    }
    # long paths matter for Unity projects
    & git clone -c core.longpaths=true $repoUrl $dir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)" }
  }
  $hasBranch = @(Git-WinQuiet $dir @('rev-parse', '--verify', '--quiet', "refs/heads/$br")).Count -gt 0
  if ($hasBranch) { Git-Win $dir @('checkout', $br) | Out-Null }
  else { Git-Win $dir @('checkout', '-b', $br) | Out-Null }
  Write-Host "$Name -> $(Git-Win $dir @('rev-parse', '--abbrev-ref', 'HEAD'))"
}

# A new job must not land on a folder that a renamed/moved job already uses (it would silently reuse it).
function Assert-FolderFree($cfg, [string]$envName, [string]$path) {
  $reg = Get-Registry
  $mine = @($reg.Keys | Where-Object { $reg[$_].env -eq $envName })
  if ($mine.Count -eq 0) { return }
  if ($envName -eq 'wsl') {
    # registry paths are absolute; the configured root may start with ~
    $path = (Invoke-Wsl $cfg "echo $path" | Where-Object { $_ } | Select-Object -First 1)
  }
  $owner = Find-RegName $envName "$path" $reg
  if ($owner) { throw "the folder '$path' is already used by the job '$owner'" }
}

# Jobs from both environments. A broken WSL setup must not hide Windows jobs.
# A folder shows up under its registry name if jobs.json points at it (renamed/moved job), else under its folder name.
function Get-JobList($cfg) {
  $reg = Get-Registry
  $jobs = New-Object System.Collections.Generic.List[object]
  $seen = @{}

  # ---- Windows: every git folder in the root, plus registered folders elsewhere ----
  $winPaths = New-Object System.Collections.Generic.List[string]
  $winRoot = Get-WinRoot $cfg
  if (Test-Path -LiteralPath $winRoot) {
    foreach ($d in Get-ChildItem -LiteralPath $winRoot -Directory) {
      if (Test-Path -LiteralPath (Join-Path $d.FullName '.git')) { $winPaths.Add($d.FullName) }
    }
  }
  foreach ($k in $reg.Keys) { if ($reg[$k].env -eq 'windows') { $winPaths.Add([string]$reg[$k].path) } }
  foreach ($p in $winPaths) {
    $key = 'windows|' + $p.TrimEnd('\').ToLowerInvariant()
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    $name = Find-RegName 'windows' $p $reg
    if (-not $name) { $name = Split-Path $p -Leaf }
    if ($jobs | Where-Object { $_.Job -eq $name }) { continue }
    if (-not (Test-Path -LiteralPath (Join-Path $p '.git'))) {
      $jobs.Add([pscustomobject]@{ Job = $name; Env = 'windows'; Branch = '(folder missing)'; Changes = 0; Repo = ''; Path = $p })
      continue
    }
    $b = (Git-WinQuiet $p @('rev-parse', '--abbrev-ref', 'HEAD') | Select-Object -First 1)
    $n = @(Git-WinQuiet $p @('status', '--porcelain')).Count
    $r = (Git-WinQuiet $p @('remote', 'get-url', 'origin') | Select-Object -First 1)
    $jobs.Add([pscustomobject]@{
      Job = $name; Env = 'windows'; Branch = "$b"; Changes = [int]$n
      Repo = ("$r" -replace '://[^/@]*@', '://'); Path = $p
    })
  }

  # ---- WSL: every git folder in the root, plus registered folders elsewhere ----
  $extra = @($reg.Keys | Where-Object { $reg[$_].env -eq 'wsl' } | ForEach-Object { [string]$reg[$_].path })
  foreach ($x in $extra) { Assert-Safe $x '^[~/A-Za-z0-9._@+-]+$' 'registered WSL folder' }
  try {
    $script = "ROOT=$($cfg.jobsRoot)`nEXTRA='$($extra -join ' ')'`n" + @'
emit() {
  d="$1"
  if [ ! -d "$d/.git" ]; then printf '%s\t%s\t%s\t%s\t%s\n' "$(basename "$d")" "(folder missing)" 0 "" "$d"; return; fi
  p=$(cd "$d" && pwd -P)
  b=$(git -C "$d" rev-parse --abbrev-ref HEAD)
  n=$(git -C "$d" status --porcelain | wc -l)
  r=$(git -C "$d" remote get-url origin 2>/dev/null || true)
  printf '%s\t%s\t%s\t%s\t%s\n' "$(basename "$p")" "$b" "$n" "$r" "$p"
}
if cd "$ROOT" 2>/dev/null; then
  for d in */; do d=${d%/}; [ -d "$d/.git" ] || continue; emit "$PWD/$d"; done
fi
for x in $EXTRA; do emit "$x"; done
'@
    $lines = Invoke-Wsl $cfg $script
    foreach ($l in @($lines | Where-Object { $_ })) {
      $f = $l -split "`t"
      $path = if ($f.Count -gt 4) { $f[4] } else { "$($cfg.jobsRoot)/$($f[0])" }
      $key = 'wsl|' + $path.TrimEnd('/')
      if ($seen.ContainsKey($key)) { continue }
      $seen[$key] = $true
      $name = Find-RegName 'wsl' $path $reg
      if (-not $name) { $name = $f[0] }
      # drop credentials embedded in the URL (https://user:token@host/...) before they reach any UI
      $repo = if ($f.Count -gt 3) { $f[3] -replace '://[^/@]*@', '://' } else { '' }
      if ($jobs | Where-Object { $_.Job -eq $name }) { continue } # Windows folder wins on a name clash
      $jobs.Add([pscustomobject]@{
        Job = $name; Env = 'wsl'; Branch = $f[1]; Changes = [int]$f[2]; Repo = $repo; Path = $path
      })
    }
  } catch {
    if ($jobs.Count -eq 0) { throw }
    [Console]::Error.WriteLine("warning: could not list WSL jobs: $($_.Exception.Message)")
  }
  @($jobs | Sort-Object Job)
}

function Cmd-List($cfg) {
  $jobs = @(Get-JobList $cfg)
  if ($Json) { ConvertTo-Json -InputObject $jobs -Compress }
  elseif ($jobs.Count -eq 0) { Write-Host 'no jobs yet' }
  else { $jobs | Select-Object Job, Env, Branch, Changes, Repo | Format-Table -AutoSize | Out-String | Write-Host }
}

function Start-Job-Tab($cfg, $name) {
  Assert-Safe $name '^[A-Za-z0-9_-]+$' 'job name'
  if ((Get-JobTarget $cfg $name) -eq 'windows') {
    $dir = Win-Dir $cfg $name
    $cmd = Get-WinClaude $cfg
    if (Get-Command wt.exe -ErrorAction SilentlyContinue) {
      # wt treats ';' as its own command separator
      Start-Process wt.exe -ArgumentList @('-w', '0', 'new-tab', '--title', $name, '-d', $dir,
        'powershell.exe', '-NoLogo', '-NoExit', '-Command', ($cmd -replace ';', '\;'))
    } else {
      Start-Process powershell.exe -WorkingDirectory $dir -ArgumentList @('-NoLogo', '-NoExit', '-Command', $cmd)
    }
    return
  }
  $dir = Wsl-Dir $cfg $name
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
  if ((Get-JobTarget $cfg $Name) -eq 'windows') { Win-Run $cfg; return }
  $p = $Prompt -replace "'", "'\''"
  Invoke-Wsl $cfg "cd $(Wsl-Dir $cfg $Name) && timeout $TimeoutSec $($cfg.claudeCmd) -p '$p' < /dev/null" -NoThrow
  if ($LASTEXITCODE -eq 124) {
    throw "claude timed out after ${TimeoutSec}s. If this is unexpected, run '.\legion.ps1 doctor' (auth may be expired; run 'claude' in WSL and /login)."
  }
  if ($LASTEXITCODE -ne 0) { throw "claude exited with code $LASTEXITCODE" }
}

# Non-interactive claude in a Windows job. The prompt travels in an environment variable so it never
# needs quoting; a timeout kills the whole process tree.
function Win-Run($cfg) {
  $dir = Require-WinJob $cfg
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = 'powershell.exe'
  $psi.Arguments = "-NoLogo -NoProfile -Command `"& { $(Get-WinClaude $cfg) -p `$env:LEGION_PROMPT }`""
  $psi.WorkingDirectory = $dir
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.EnvironmentVariables['LEGION_PROMPT'] = $Prompt
  $p = [System.Diagnostics.Process]::Start($psi)
  $p.StandardInput.Close()
  $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    & taskkill.exe /T /F /PID $p.Id *> $null
    throw "claude timed out after ${TimeoutSec}s. Run '.\legion.ps1 doctor -Target windows' (auth may be expired; run 'claude' in PowerShell and /login)."
  }
  $out.Result
  if ($p.ExitCode -ne 0) { throw "claude exited with code $($p.ExitCode): $($err.Result.Trim())" }
}

# bash snippet: cd into the job and resolve $BASE (origin/HEAD, falling back to main)
function Job-Prelude($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ($Base) { Assert-Safe $Base '^[A-Za-z0-9._/-]+$' 'base' }
  @"
set -e
cd $(Wsl-Dir $cfg $Name) 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
BR=`$(git rev-parse --abbrev-ref HEAD)
BASE='$Base'
[ -n "`$BASE" ] || BASE=`$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null | sed 's@^origin/@@')
[ -n "`$BASE" ] || BASE=main
"@
}

function Cmd-Diff($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ((Get-JobTarget $cfg $Name) -eq 'windows') {
    $dir = Require-WinJob $cfg
    $br = Git-Win $dir @('rev-parse', '--abbrev-ref', 'HEAD'); $base = Win-Base $dir
    "== $br vs $base =="
    Git-Win $dir @('status', '--short')
    Git-Win $dir @('log', '--oneline', "$base..$br") -NoThrow
    Git-Win $dir @('diff', '--stat', "$base...$br") -NoThrow
    return
  }
  Invoke-Wsl $cfg ((Job-Prelude $cfg) + @"

echo "== `$BR vs `$BASE =="
git status --short
git log --oneline `$BASE..`$BR 2>/dev/null || true
git diff --stat `$BASE...`$BR 2>/dev/null || true
"@)
}

function Cmd-Push($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ((Get-JobTarget $cfg $Name) -eq 'windows') {
    $dir = Require-WinJob $cfg
    $br = Git-Win $dir @('rev-parse', '--abbrev-ref', 'HEAD')
    Git-Win $dir @('push', '-u', 'origin', $br)
    return
  }
  Invoke-Wsl $cfg ((Job-Prelude $cfg) + "`ngit push -u origin `"`$BR`"")
}

# Merge the job branch into the base branch inside the job clone. Pushes base only with -Push.
function Cmd-Merge($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ((Get-JobTarget $cfg $Name) -eq 'windows') { Win-Merge $cfg; return }
  $pushCmd = if ($Push) { 'git push origin "$BASE"' } else { 'echo "(not pushed; add -Push to push $BASE)"' }
  Invoke-Wsl $cfg ((Job-Prelude $cfg) + @"

[ -z "`$(git status --porcelain)" ] || { echo "uncommitted changes in $Name; commit or stash first" >&2; exit 1; }
[ "`$BR" != "`$BASE" ] || { echo "$Name is already on `$BASE" >&2; exit 1; }
git fetch origin 2>/dev/null || true
git checkout "`$BASE"
git merge --ff-only "origin/`$BASE" 2>/dev/null || true
git merge --no-ff "`$BR" -m "Merge `$BR into `$BASE" || { git merge --abort; git checkout "`$BR"; echo "merge conflict; aborted" >&2; exit 1; }
$pushCmd
git checkout "`$BR"
"@)
}

function Win-Merge($cfg) {
  $dir = Require-WinJob $cfg
  $br = Git-Win $dir @('rev-parse', '--abbrev-ref', 'HEAD'); $base = Win-Base $dir
  if (@(Git-Win $dir @('status', '--porcelain')).Count -gt 0) { throw "uncommitted changes in $Name; commit or stash first" }
  if ($br -eq $base) { throw "$Name is already on $base" }
  Git-Win $dir @('fetch', 'origin') -NoThrow | Out-Null
  Git-Win $dir @('checkout', $base) | Out-Null
  Git-Win $dir @('merge', '--ff-only', "origin/$base") -NoThrow | Out-Null
  & git -C $dir merge --no-ff $br -m "Merge $br into $base"
  if ($LASTEXITCODE -ne 0) {
    & git -C $dir merge --abort
    & git -C $dir checkout $br
    throw 'merge conflict; aborted'
  }
  if ($Push) { Git-Win $dir @('push', 'origin', $base) } else { "(not pushed; add -Push to push $base)" }
  Git-Win $dir @('checkout', $br) | Out-Null
}

# Sum Claude Code token usage for a job from its transcripts (~/.claude/projects/<encoded cwd>/*.jsonl).
function Cmd-Usage($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ((Get-JobTarget $cfg $Name) -eq 'windows') { $raw = Win-Usage $cfg }
  else {
    $raw = Wsl-Usage $cfg
  }
  if ($Json) { $raw }
  else { $raw | ConvertFrom-Json | Format-List | Out-String | Write-Host }
}

function Wsl-Usage($cfg) {
  $py = @'
import glob, json, os, re, sys

job_dir = os.path.realpath(os.path.expanduser(sys.argv[1]))
base = os.environ.get("CLAUDE_CONFIG_DIR") or os.path.expanduser("~/.claude")
proj = os.path.join(base, "projects", re.sub(r"[^a-zA-Z0-9]", "-", job_dir))
fields = {"input": "input_tokens", "output": "output_tokens",
          "cacheCreate": "cache_creation_input_tokens", "cacheRead": "cache_read_input_tokens"}

# One assistant message is logged once per content block with the same id: keep the max per field.
by_id = {}
files = glob.glob(os.path.join(proj, "*.jsonl"))
for path in files:
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if '"usage"' not in line:
                continue
            try:
                msg = json.loads(line).get("message") or {}
            except ValueError:
                continue
            usage, mid = msg.get("usage"), msg.get("id")
            if not isinstance(usage, dict) or not mid:
                continue
            cur = by_id.setdefault(mid, {k: 0 for k in fields})
            for k, f in fields.items():
                cur[k] = max(cur[k], int(usage.get(f) or 0))

out = {k: sum(u[k] for u in by_id.values()) for k in fields}
out["messages"] = len(by_id)
out["sessions"] = len(files)
print(json.dumps(out))
'@
  $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($py -replace "`r", '')))
  $lines = Invoke-Wsl $cfg @"
command -v python3 >/dev/null || { echo 'python3 is required in WSL for usage stats' >&2; exit 1; }
echo $b64 | base64 -d > /tmp/legion_usage.py
python3 /tmp/legion_usage.py $(Wsl-Dir $cfg $Name)
"@
  ($lines | Where-Object { $_ } | Select-Object -Last 1)
}

# Same aggregation as the WSL version, done natively so Windows jobs need no python.
function Win-Usage($cfg) {
  $dir = (Resolve-Path -LiteralPath (Require-WinJob $cfg)).ProviderPath
  $claudeHome = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
  $proj = Join-Path (Join-Path $claudeHome 'projects') ($dir -replace '[^a-zA-Z0-9]', '-')
  $files = @()
  if (Test-Path -LiteralPath $proj) { $files = @(Get-ChildItem -LiteralPath $proj -Filter '*.jsonl' -File) }

  $fields = [ordered]@{ input = 'input_tokens'; output = 'output_tokens'; cacheCreate = 'cache_creation_input_tokens'; cacheRead = 'cache_read_input_tokens' }
  $byId = @{}
  foreach ($f in $files) {
    foreach ($line in [System.IO.File]::ReadLines($f.FullName, [Text.Encoding]::UTF8)) {
      if (-not $line.Contains('"usage"')) { continue }
      try { $msg = ($line | ConvertFrom-Json).message } catch { continue }
      if (-not $msg -or -not $msg.id -or -not $msg.usage) { continue }
      if (-not $byId.ContainsKey($msg.id)) { $byId[$msg.id] = @{ input = 0L; output = 0L; cacheCreate = 0L; cacheRead = 0L } }
      $cur = $byId[$msg.id]
      foreach ($k in $fields.Keys) {
        $v = [long]($msg.usage.($fields[$k]))
        if ($v -gt $cur[$k]) { $cur[$k] = $v }   # a message is logged once per content block: keep the max
      }
    }
  }
  $sum = @{ input = 0L; output = 0L; cacheCreate = 0L; cacheRead = 0L }
  foreach ($u in $byId.Values) { foreach ($k in $sum.Keys.Clone()) { $sum[$k] += $u[$k] } }
  [pscustomobject][ordered]@{
    input = $sum.input; output = $sum.output; cacheCreate = $sum.cacheCreate; cacheRead = $sum.cacheRead
    messages = $byId.Count; sessions = $files.Count
  } | ConvertTo-Json -Compress
}

# Open the job folder in the editor. The editor is always launched from Windows, so it works even when WSL
# cannot start Windows programs (broken interop, common with systemd=true):
#   Windows job: `code .` in the job folder
#   WSL job:     `code --remote wsl+<distro> <absolute WSL path>` (Remote-WSL window)
# The command name can be changed with "codeCmd" in legion.json (e.g. cursor).
function Cmd-Code($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  $code = Get-Opt $cfg 'codeCmd' 'code'
  Assert-Safe $code '^[A-Za-z0-9._-]+$' 'codeCmd'
  if (-not (Get-Command $code -ErrorAction SilentlyContinue)) {
    throw "'$code' was not found on the Windows PATH. In the editor run: Shell Command: Install '$code' command in PATH."
  }

  if ((Get-JobTarget $cfg $Name) -eq 'windows') {
    $dir = Require-WinJob $cfg
    Start-Editor $code @('.') $dir
    Write-Host "Opened $dir with $code"
    return
  }

  # resolve the real distro name and the absolute folder inside WSL (the configured root may start with ~)
  $lines = @(Invoke-Wsl $cfg @"
cd $(Wsl-Dir $cfg $Name) 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
echo "`$WSL_DISTRO_NAME"
pwd -P
"@ | Where-Object { $_ })
  if ($lines.Count -lt 2) { throw 'could not resolve the WSL folder of the job' }
  $distroName = "$($lines[0])".Trim(); $wslDir = "$($lines[1])".Trim()
  Assert-Safe $distroName '^[A-Za-z0-9._-]+$' 'distro'
  Assert-Safe $wslDir '^/[A-Za-z0-9._/@+-]+$' 'WSL folder'

  Start-Editor $code @('--remote', "wsl+$distroName", $wslDir) $PSScriptRoot
  Write-Host "Opened wsl+${distroName}:$wslDir with $code (Remote-WSL)"
}

# Launch the editor detached from this script. `code.cmd` stays alive as long as the editor window does and the
# editor would inherit our stdout/stderr pipes, so a caller reading them (the web UI) would wait forever and then
# kill the process tree - closing the window it just opened. Start-Process goes through ShellExecute, which does
# not pass those handles on. A launcher that fails exits within moments, so wait briefly to report that.
function Start-Editor([string]$code, [string[]]$codeArgs, [string]$workDir) {
  $p = Start-Process -FilePath cmd.exe -ArgumentList (@('/c', $code) + $codeArgs) `
        -WorkingDirectory $workDir -WindowStyle Hidden -PassThru
  if ($p.WaitForExit(3000) -and $p.ExitCode -ne 0) {
    throw "'$code $($codeArgs -join ' ')' failed (exit $($p.ExitCode)). For a WSL job the editor needs its WSL extension (Remote - WSL)."
  }
}

# Open a plain shell (not claude) in the job folder: a Windows Terminal tab when wt is available, otherwise
# a console window. WSL job -> a WSL shell, Windows job -> PowerShell. Started through Start-Process (ShellExecute)
# so the shell does not inherit our stdout/stderr pipes (see Start-Editor).
function Cmd-Shell($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  $wt = Get-Command wt -ErrorAction SilentlyContinue | Select-Object -First 1

  if ((Get-JobTarget $cfg $Name) -eq 'windows') {
    $dir = Require-WinJob $cfg
    if ($wt) {
      Start-Process -FilePath $wt.Source -ArgumentList @('-w', '0', 'new-tab', '--title', $Name, '-d', "`"$dir`"", 'powershell.exe', '-NoLogo')
    } else {
      Start-Process -FilePath powershell.exe -WorkingDirectory $dir -ArgumentList @('-NoLogo')
    }
    Write-Host "Opened a PowerShell terminal in $dir"
    return
  }

  $dir = Wsl-Dir $cfg $Name
  Invoke-Wsl $cfg @"
cd $dir 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
"@ | Out-Null
  $wslArgs = @(Wsl-Args $cfg) + @('--cd', $dir)
  if ($wt) {
    Start-Process -FilePath $wt.Source -ArgumentList (@('-w', '0', 'new-tab', '--title', $Name, 'wsl.exe') + $wslArgs)
  } else {
    Start-Process -FilePath wsl.exe -ArgumentList $wslArgs
  }
  Write-Host "Opened a WSL terminal in $dir"
}

# The job's most recent Claude conversation: <claude home>/projects/<encoded folder>/<session id>.jsonl.
# Used to start `claude --resume <id>` so a restart continues where the job left off. Prints JSON or null.
function Cmd-LastSession($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  $raw = if ((Get-JobTarget $cfg $Name) -eq 'windows') { Win-LastSession $cfg } else { Wsl-LastSession $cfg }
  if ($Json) { $raw } else { $raw | ConvertFrom-Json | Format-List | Out-String | Write-Host }
}

function Win-LastSession($cfg) {
  $dir = (Resolve-Path -LiteralPath (Require-WinJob $cfg)).ProviderPath
  $claudeHome = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
  $proj = Join-Path (Join-Path $claudeHome 'projects') ($dir -replace '[^a-zA-Z0-9]', '-')
  if (Test-Path -LiteralPath $proj) {
    # newest transcript that holds an actual user message (an opened-and-closed session leaves no conversation)
    foreach ($f in (Get-ChildItem -LiteralPath $proj -Filter '*.jsonl' -File | Sort-Object LastWriteTimeUtc -Descending)) {
      if (Select-String -LiteralPath $f.FullName -Pattern '"type"\s*:\s*"user"' -Quiet) {
        return ([pscustomobject]@{ id = $f.BaseName; modified = $f.LastWriteTimeUtc.ToString('o'); sizeKb = [int]($f.Length / 1KB) } | ConvertTo-Json -Compress)
      }
    }
  }
  'null'
}

function Wsl-LastSession($cfg) {
  $script = "DIR=$(Wsl-Dir $cfg $Name)`n" + @'
cd "$DIR" 2>/dev/null || { echo "job not found" >&2; exit 1; }
real=$(pwd -P)
base=${CLAUDE_CONFIG_DIR:-$HOME/.claude}
proj="$base/projects/$(printf '%s' "$real" | sed 's/[^a-zA-Z0-9]/-/g')"
for f in $(ls -t "$proj"/*.jsonl 2>/dev/null); do
  if grep -qE '"type"[[:space:]]*:[[:space:]]*"user"' "$f"; then
    printf '{"id":"%s","epoch":%s,"sizeKb":%s}\n' "$(basename "$f" .jsonl)" "$(stat -c %Y "$f")" "$(( $(stat -c %s "$f") / 1024 ))"
    exit 0
  fi
done
echo null
'@
  $line = (Invoke-Wsl $cfg $script | Where-Object { $_ } | Select-Object -Last 1)
  if (-not $line -or $line -eq 'null') { return 'null' }
  $o = $line | ConvertFrom-Json
  $mod = [DateTimeOffset]::FromUnixTimeSeconds([long]$o.epoch).UtcDateTime.ToString('o')
  [pscustomobject]@{ id = $o.id; modified = $mod; sizeKb = $o.sizeKb } | ConvertTo-Json -Compress
}

function Cmd-Doctor($cfg) {
  $state = @{ fail = 0 }
  $winConfigured = [bool]$cfg.PSObject.Properties['windowsJobsRoot']
  if ($Target -ne 'windows') {
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
  }
  if ($Target -eq 'windows' -or $winConfigured) {
    function WinCheck($label, [scriptblock]$test, $hint) {
      Write-Host -NoNewline ("{0,-28}" -f $label)
      $ok = $false
      try { $ok = [bool](& $test) } catch { $ok = $false }
      if ($ok) { Write-Host 'OK' -ForegroundColor Green }
      else { Write-Host "FAIL  $hint" -ForegroundColor Red; $state.fail++ }
    }
    WinCheck 'win: git installed'    { Get-Command git -ErrorAction Stop }    'install Git for Windows'
    WinCheck 'win: claude installed' { Get-Command (Get-WinClaude $cfg).Split(' ')[0] -ErrorAction Stop } 'install Claude Code for Windows'
    WinCheck 'win: jobs root writable' {
      $r = Get-WinRoot $cfg; New-Item -ItemType Directory -Force -Path $r | Out-Null
      $t = Join-Path $r '.legion-write-test'; Set-Content -LiteralPath $t 'x'; Remove-Item -LiteralPath $t -Force; $true
    } 'check permissions on the Windows jobs root'
    $repoUrl = $cfg.repo
    WinCheck 'win: repo reachable'   { & git ls-remote $repoUrl HEAD *> $null; $LASTEXITCODE -eq 0 } 'check URL / credentials in Windows git'
  }
  if ($state.fail) { throw "$($state.fail) check(s) failed" } else { Write-Host 'All checks passed.' }
}

# Edit a job. All inputs are validated first; then they are applied in this order: repo, branch, folder move, name.
#   -Repo     point origin at another URL
#   -Branch   switch to that branch (existing, else created from the current commit); needs a clean working tree
#   -NewPath  move the job folder (the Claude history for the folder moves along, so resume/token stats keep working)
#   -NewName  rename the job. Only the label changes: the folder and the Claude history stay where they are.
function Cmd-Edit($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ($NewName -ieq $Name) { $script:NewName = '' }
  if (-not ($NewName -or $Branch -or $Repo -or $NewPath)) { throw 'nothing to change: give -NewName, -Branch, -Repo and/or -NewPath' }
  $isWin = (Get-JobTarget $cfg $Name) -eq 'windows'
  $envName = if ($isWin) { 'windows' } else { 'wsl' }

  # ---- validate everything before touching anything ----
  if ($NewName) {
    Assert-Safe $NewName '^[A-Za-z0-9_-]+$' 'new job name'
    $taken = @(Get-JobList $cfg | ForEach-Object { $_.Job }) + @((Get-Registry).Keys)
    if ($taken -contains $NewName) { throw "a job named '$NewName' already exists" }
  }
  if ($Branch) { Assert-Safe $Branch '^[A-Za-z0-9._/-]+$' 'branch' }
  if ($Repo) { Assert-Safe $Repo '^\S+$' 'repo' }
  if ($NewPath) {
    if ($NewPath -match '\.\.') { throw "the folder must not contain '..'" }
    if ($isWin) { Assert-Safe $NewPath '^[A-Za-z]:\\[A-Za-z0-9_ .()\\-]+$' 'folder (an absolute Windows path such as D:\work\job1)' }
    else { Assert-Safe $NewPath '^(~|/)[A-Za-z0-9._/@+~-]*$' 'folder (an absolute WSL path such as /home/me/work/job1, no spaces)' }
  }

  $final = if ($isWin) { Edit-Windows $cfg } else { Edit-Wsl $cfg }

  # jobs.json only needs an entry when the name or the folder is no longer the default layout
  if ($NewName -or $NewPath) {
    $reg = Get-Registry
    if ($reg.ContainsKey($Name)) { $reg.Remove($Name) }
    $reg[$(if ($NewName) { $NewName } else { $Name })] = [pscustomobject]@{ env = $envName; path = $final }
    Save-Registry $reg
  }
  Write-Host ("Updated $Name" + $(if ($NewName) { " (now $NewName)" } else { '' }))
}

function Git-WinOk([string]$dir, [string[]]$gitArgs) {
  [bool](Quiet { & git -C $dir @gitArgs 2>$null | Out-Null; $LASTEXITCODE -eq 0 })
}

# Returns the job's final absolute folder.
function Edit-Windows($cfg) {
  $dir = (Resolve-Path -LiteralPath (Require-WinJob $cfg)).ProviderPath

  if ($Repo) {
    if (@(Git-WinQuiet $dir @('remote', 'get-url', 'origin')).Count -gt 0) { Git-Win $dir @('remote', 'set-url', 'origin', $Repo) | Out-Null }
    else { Git-Win $dir @('remote', 'add', 'origin', $Repo) | Out-Null }
    Write-Host ("repo -> " + ($Repo -replace '://[^/@]*@', '://'))   # never echo credentials into logs / the web UI
  }

  if ($Branch) {
    if (@(Git-Win $dir @('status', '--porcelain')).Count -gt 0) { throw "uncommitted changes in ${Name}: commit or stash them before switching branch" }
    # an existing local branch, else a remote one of that name (git sets up tracking), else a new branch from here
    if (-not (Git-WinOk $dir @('checkout', $Branch))) { Git-Win $dir @('checkout', '-b', $Branch) | Out-Null }
    Write-Host "branch -> $(Git-Win $dir @('rev-parse', '--abbrev-ref', 'HEAD'))"
  }

  $final = $dir
  if ($NewPath) {
    $np = [IO.Path]::GetFullPath($NewPath).TrimEnd('\')
    $cur = $dir.TrimEnd('\')
    if ($np -ine $cur) {
      if (($np + '\').StartsWith($cur + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'the new folder is inside the current one' }
      if (Test-Path -LiteralPath $np) {
        if (-not (Test-Path -LiteralPath $np -PathType Container) -or (Get-ChildItem -LiteralPath $np -Force | Select-Object -First 1)) {
          throw "the target already exists and is not an empty folder: $np"
        }
        [IO.Directory]::Delete($np)
      }
      $parent = Split-Path $np -Parent
      if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

      if ([IO.Path]::GetPathRoot($cur) -ieq [IO.Path]::GetPathRoot($np)) {
        [IO.Directory]::Move($cur, $np)   # same drive: a rename, instant
      } else {
        & robocopy.exe $cur $np /E /MOVE /R:1 /W:1 /NFL /NDL /NP /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "moving the folder to another drive failed (robocopy exit $LASTEXITCODE); it may be partly moved: $cur" }
        if (Test-Path -LiteralPath $cur) { & cmd.exe /c rd /s /q "$cur" }
      }

      # Claude keeps a job's conversations under a folder named after the job's path: move that along
      $claudeHome = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
      $projects = Join-Path $claudeHome 'projects'
      $histOld = Join-Path $projects ($cur -replace '[^a-zA-Z0-9]', '-')
      $histNew = Join-Path $projects ($np -replace '[^a-zA-Z0-9]', '-')
      if ((Test-Path -LiteralPath $histOld) -and -not (Test-Path -LiteralPath $histNew)) {
        [IO.Directory]::Move($histOld, $histNew)
        Write-Host 'Claude history moved with the folder'
      }
      $final = $np
      Write-Host "folder -> $np"
    }
  }
  $final
}

function Edit-Wsl($cfg) {
  $repoQ = if ($Repo) { $Repo -replace "'", "'\''" } else { '' }
  $branchQ = if ($Branch) { $Branch -replace "'", "'\''" } else { '' }
  $script = "OLD=$(Wsl-Dir $cfg $Name)`nREPO='$repoQ'`nBRANCH='$branchQ'`nNEWPATH=$NewPath`nNAME=$Name`n" + @'
cd "$OLD" 2>/dev/null || { echo "job not found: $NAME" >&2; exit 1; }
set -e
OLD=$(pwd -P)
if [ -n "$REPO" ]; then
  if git remote get-url origin >/dev/null 2>&1; then git remote set-url origin "$REPO"; else git remote add origin "$REPO"; fi
  echo "repo -> $(printf '%s' "$REPO" | sed -E 's#://[^/@]*@#://#')"   # never echo credentials
fi
if [ -n "$BRANCH" ]; then
  if [ -n "$(git status --porcelain)" ]; then echo "uncommitted changes in $NAME: commit or stash them before switching branch" >&2; exit 1; fi
  git checkout "$BRANCH" >/dev/null 2>&1 || git checkout -b "$BRANCH" >/dev/null 2>&1
  echo "branch -> $(git rev-parse --abbrev-ref HEAD)"
fi
NEW="$OLD"
if [ -n "$NEWPATH" ]; then
  case "$NEWPATH/" in "$OLD"/*) echo "the new folder is inside the current one" >&2; exit 1;; esac
  if [ -e "$NEWPATH" ]; then
    if [ -d "$NEWPATH" ] && [ -z "$(ls -A "$NEWPATH")" ]; then rmdir "$NEWPATH"
    else echo "the target already exists and is not an empty folder: $NEWPATH" >&2; exit 1; fi
  fi
  mkdir -p "$(dirname "$NEWPATH")"
  cd /
  mv "$OLD" "$NEWPATH"
  NEW=$(cd "$NEWPATH" && pwd -P)
  # Claude keeps a job's conversations under a folder named after the job's path: move that along
  projects="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/projects"
  e_old=$(printf '%s' "$OLD" | sed 's/[^a-zA-Z0-9]/-/g')
  e_new=$(printf '%s' "$NEW" | sed 's/[^a-zA-Z0-9]/-/g')
  if [ -d "$projects/$e_old" ] && [ ! -e "$projects/$e_new" ]; then mv "$projects/$e_old" "$projects/$e_new"; echo "Claude history moved with the folder"; fi
  echo "folder -> $NEW"
fi
echo "PATH=$NEW"
'@
  $lines = @(Invoke-Wsl $cfg $script | Where-Object { $_ })
  $final = $null
  foreach ($l in $lines) { if ("$l" -like 'PATH=*') { $final = "$l".Substring(5) } else { Write-Host $l } }
  if (-not $final) { throw 'the WSL side did not report the final folder' }
  $final
}

function Cmd-Remove($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ((Get-JobTarget $cfg $Name) -eq 'windows') { Win-Remove $cfg; return }
  if (-not $Force) {
    # refuse if there are uncommitted changes or commits not present on any remote branch
    Invoke-Wsl $cfg @"
cd $(Wsl-Dir $cfg $Name) 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
[ -z "`$(git status --porcelain)" ] || { echo "uncommitted changes in $Name (use -Force to delete anyway)" >&2; exit 1; }
[ -z "`$(git log --branches --not --remotes --oneline)" ] || { echo "unpushed commits in $Name (push/merge them or use -Force)" >&2; exit 1; }
"@
  }
  # Only a folder inside the jobs root is deleted. A job moved elsewhere by `edit` is just unregistered, so
  # remove can never wipe a folder the user pointed a job at.
  $result = Invoke-Wsl $cfg @"
ROOT=$($cfg.jobsRoot)
cd $(Wsl-Dir $cfg $Name) 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
real=`$(pwd -P)
root=`$(cd "`$ROOT" 2>/dev/null && pwd -P || echo /nonexistent)
case "`$real/" in
  "`$root"/*) cd / && rm -rf "`$real"; echo REMOVED ;;
  *) echo OUTSIDE ;;
esac
"@
  Remove-RegEntry $Name
  if (@($result) -contains 'OUTSIDE') { Write-Host "Unregistered $Name (its folder is outside the jobs root and was left in place)" }
  else { Write-Host "Removed $Name" }
}

function Remove-RegEntry($name) {
  $reg = Get-Registry
  if ($reg.ContainsKey($name)) { $reg.Remove($name); Save-Registry $reg }
}

function Win-Remove($cfg) {
  $dir = Require-WinJob $cfg
  if (-not $Force) {
    if (@(Git-Win $dir @('status', '--porcelain')).Count -gt 0) { throw "uncommitted changes in $Name (use -Force to delete anyway)" }
    if (@(Git-Win $dir @('log', '--branches', '--not', '--remotes', '--oneline')).Count -gt 0) {
      throw "unpushed commits in $Name (push/merge them or use -Force)"
    }
  }
  # rd removes junctions/links without following them and clears read-only files (git objects, Unity Library)
  $full = [IO.Path]::GetFullPath($dir)
  $rootFull = [IO.Path]::GetFullPath((Get-WinRoot $cfg)).TrimEnd('\')
  if (-not $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
    # a job moved elsewhere by `edit`: never delete a folder outside the jobs root, only forget the job
    Remove-RegEntry $Name
    Write-Host "Unregistered $Name (its folder $full is outside the jobs root and was left in place)"
    return
  }
  & cmd.exe /c rd /s /q "$full"
  if (Test-Path -LiteralPath $full) { throw "could not fully remove $full (is a program such as Unity using it?)" }
  Remove-RegEntry $Name
  Write-Host "Removed $Name"
}

try {
switch ($Command) {
  'init'      { Cmd-Init }
  'add'       { Cmd-Add (Get-Config) }
  { $_ -in 'list', 'status' } { Cmd-List (Get-Config) }
  'start'     { Start-Job-Tab (Get-Config) $Name }
  'start-all' {
    $cfg = Get-Config
    foreach ($j in @(Get-JobList $cfg)) { Start-Job-Tab $cfg $j.Job }
  }
  'run'       { Cmd-Run (Get-Config) }
  'doctor'    { Cmd-Doctor (Get-Config) }
  'usage'     { Cmd-Usage (Get-Config) }
  'last-session' { Cmd-LastSession (Get-Config) }
  'code'      { Cmd-Code (Get-Config) }
  'shell'     { Cmd-Shell (Get-Config) }
  'edit'      { Cmd-Edit (Get-Config) }
  'diff'      { Cmd-Diff (Get-Config) }
  'push'      { Cmd-Push (Get-Config) }
  'merge'     { Cmd-Merge (Get-Config) }
  'remove'    { Cmd-Remove (Get-Config) }
  default {
    @'
AgentLegion commands:
  init -Repo <url> [-Distro <name>] [-Root ~/agentjobs] [-WindowsRoot <dir>]   write legion.json
  add <job> [-Branch <name>] [-Repo <url>] [-Target wsl|windows]
                                clone the repo to <root>/<job> and check out agent/<job>
                                (-Target windows = a native Windows PowerShell job, default root %USERPROFILE%\agentjobs)
  list | status [-Json]         show jobs (both environments), branches, uncommitted changes
  start <job>                   open Windows Terminal tab running claude in that job
  start-all                     open a tab for every job
  run <job> -Prompt "<text>" [-TimeoutSec 600]   non-interactive claude -p in that job
  diff <job> [-Base main]       commits/changes of the job branch vs base
  push <job>                    push the job branch to origin
  merge <job> [-Base main] [-Push]  --no-ff merge job branch into base (local; -Push pushes base)
  usage <job> [-Json]           Claude token usage for the job (from ~/.claude transcripts)
  last-session <job> [-Json]    id of the job's most recent Claude conversation (for claude --resume)
  code <job>                    open the job folder in the editor (code .; WSL jobs via Remote-WSL; "codeCmd" in legion.json)
  shell <job>                   open a plain terminal (WSL shell or PowerShell) in the job folder, not claude
  edit <job> [-NewName n] [-Branch b] [-Repo url] [-NewPath dir]
                                rename the job (label only), switch branch, change origin, or move the job folder
  doctor [-Target windows]      check WSL / Windows, git, claude, repo access, claude auth
  remove <job> [-Force]         delete job folder (refuses if uncommitted/unpushed work)
'@ | Write-Host
  }
}
} catch {
  [Console]::Error.WriteLine($_.Exception.Message)
  exit 1
}
