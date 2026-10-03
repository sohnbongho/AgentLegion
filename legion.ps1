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

function Win-Dir($cfg, $name) { Join-Path (Get-WinRoot $cfg) $name }

function Test-WinJob($cfg, $name) { Test-Path -LiteralPath (Join-Path (Win-Dir $cfg $name) '.git') }

# A job lives in exactly one environment; a Windows folder wins, otherwise it is a WSL job.
function Get-JobTarget($cfg, $name) {
  if ($Target) { return $Target }
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

  if ($Target -eq 'windows') { Win-Add $cfg $br $repoUrl; return }

  if (Test-WinJob $cfg $Name) { throw "a Windows job named '$Name' already exists; job names are shared across environments" }
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

# Jobs from both environments. A broken WSL setup must not hide Windows jobs.
function Get-JobList($cfg) {
  $jobs = New-Object System.Collections.Generic.List[object]

  $winRoot = Get-WinRoot $cfg
  if (Test-Path -LiteralPath $winRoot) {
    foreach ($d in Get-ChildItem -LiteralPath $winRoot -Directory) {
      if (-not (Test-Path -LiteralPath (Join-Path $d.FullName '.git'))) { continue }
      $b = (Git-WinQuiet $d.FullName @('rev-parse', '--abbrev-ref', 'HEAD') | Select-Object -First 1)
      $n = @(Git-WinQuiet $d.FullName @('status', '--porcelain')).Count
      $r = (Git-WinQuiet $d.FullName @('remote', 'get-url', 'origin') | Select-Object -First 1)
      $jobs.Add([pscustomobject]@{
        Job = $d.Name; Env = 'windows'; Branch = "$b"; Changes = [int]$n
        Repo = ("$r" -replace '://[^/@]*@', '://'); Path = $d.FullName
      })
    }
  }

  try {
    $lines = Invoke-Wsl $cfg @"
cd $($cfg.jobsRoot) 2>/dev/null || exit 0
for d in */; do
  d=`${d%/}
  [ -d "`$d/.git" ] || continue
  b=`$(git -C "`$d" rev-parse --abbrev-ref HEAD)
  n=`$(git -C "`$d" status --porcelain | wc -l)
  r=`$(git -C "`$d" remote get-url origin 2>/dev/null || true)
  printf '%s\t%s\t%s\t%s\n' "`$d" "`$b" "`$n" "`$r"
done
"@
    foreach ($l in @($lines | Where-Object { $_ })) {
      $f = $l -split "`t"
      # drop credentials embedded in the URL (https://user:token@host/...) before they reach any UI
      $repo = if ($f.Count -gt 3) { $f[3] -replace '://[^/@]*@', '://' } else { '' }
      if ($jobs | Where-Object { $_.Job -eq $f[0] }) { continue } # Windows folder wins on a name clash
      $jobs.Add([pscustomobject]@{
        Job = $f[0]; Env = 'wsl'; Branch = $f[1]; Changes = [int]$f[2]; Repo = $repo; Path = "$($cfg.jobsRoot)/$($f[0])"
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
  if ((Get-JobTarget $cfg $Name) -eq 'windows') { Win-Run $cfg; return }
  $p = $Prompt -replace "'", "'\''"
  Invoke-Wsl $cfg "cd $($cfg.jobsRoot)/$Name && timeout $TimeoutSec $($cfg.claudeCmd) -p '$p' < /dev/null" -NoThrow
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
cd $($cfg.jobsRoot)/$Name 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
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
python3 /tmp/legion_usage.py $($cfg.jobsRoot)/$Name
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
cd $($cfg.jobsRoot)/$Name 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
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

  $dir = "$($cfg.jobsRoot)/$Name"
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
  $script = "DIR=$($cfg.jobsRoot)/$Name`n" + @'
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

function Cmd-Remove($cfg) {
  Assert-Safe $Name '^[A-Za-z0-9_-]+$' 'job name'
  if ((Get-JobTarget $cfg $Name) -eq 'windows') { Win-Remove $cfg; return }
  if (-not $Force) {
    # refuse if there are uncommitted changes or commits not present on any remote branch
    Invoke-Wsl $cfg @"
cd $($cfg.jobsRoot)/$Name 2>/dev/null || { echo "job not found: $Name" >&2; exit 1; }
[ -z "`$(git status --porcelain)" ] || { echo "uncommitted changes in $Name (use -Force to delete anyway)" >&2; exit 1; }
[ -z "`$(git log --branches --not --remotes --oneline)" ] || { echo "unpushed commits in $Name (push/merge them or use -Force)" >&2; exit 1; }
"@
  }
  Invoke-Wsl $cfg "rm -rf $($cfg.jobsRoot)/$Name"
  Write-Host "Removed $Name"
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
  if (-not $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to delete outside the jobs root: $full" }
  & cmd.exe /c rd /s /q "$full"
  if (Test-Path -LiteralPath $full) { throw "could not fully remove $full (is a program such as Unity using it?)" }
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
  doctor [-Target windows]      check WSL / Windows, git, claude, repo access, claude auth
  remove <job> [-Force]         delete job folder (refuses if uncommitted/unpushed work)
'@ | Write-Host
  }
}
} catch {
  [Console]::Error.WriteLine($_.Exception.Message)
  exit 1
}
