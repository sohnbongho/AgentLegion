using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentLegion.Services
{
    public enum ServerAction { Build, Deploy, Run, Stop }

    /// <summary>tmux session state of the game server: the session exists, how many windows it has, how many wind processes run.</summary>
    public record ServerStatus(bool Running, int Windows, int WindProcesses);

    /// <summary>One Build / Deploy / Run / Stop of a job: its output so far and how it ended.</summary>
    public sealed class ServerOpRun
    {
        private const int MaxLines = 3000;
        private readonly List<string> _lines = new();
        private readonly object _gate = new();
        private int _dropped;

        public ServerOpRun(string job, ServerAction action, string? detail)
        {
            Job = job;
            Action = action;
            Detail = detail;
        }

        public string Job { get; }
        public ServerAction Action { get; }
        /// <summary>What the action works on, e.g. the deploy folder or the build command.</summary>
        public string? Detail { get; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public DateTime? EndedAt { get; private set; }
        public int? ExitCode { get; private set; }
        public bool Cancelled { get; internal set; }
        public bool Running => EndedAt is null;
        public bool Ok => ExitCode == 0 && !Cancelled;
        public TimeSpan Elapsed => (EndedAt ?? DateTime.Now) - StartedAt;

        /// <summary>The bash wrapper's pid (= process group of the build), so Cancel can stop make and its children.</summary>
        internal int? Pid { get; set; }
        internal Process? Process { get; set; }

        internal void Add(string line)
        {
            lock (_gate)
            {
                _lines.Add(line);
                if (_lines.Count > MaxLines)
                {
                    _lines.RemoveRange(0, _lines.Count - MaxLines);
                    _dropped++;
                }
            }
        }

        /// <summary>Ends the run; the last log line says how it ended and when.</summary>
        internal void Finish(int exitCode)
        {
            ExitCode = exitCode;
            EndedAt = DateTime.Now;
            var how = Cancelled ? "취소됨" : exitCode == 0 ? "완료" : $"실패 (exit {exitCode})";
            Add($"── {Action} {how} · {EndedAt:yyyy-MM-dd HH:mm:ss} ({FormatElapsed(Elapsed)}) ──");
        }

        public static string FormatElapsed(TimeSpan t) =>
            t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{t.TotalSeconds:0.0}초";

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    var head = _dropped > 0 ? $"… (앞부분 생략, 마지막 {MaxLines}줄만 표시)\n" : "";
                    return head + string.Join('\n', _lines);
                }
            }
        }
    }

    /// <summary>
    /// Builds a WSL job (make in its folder), deploys its server binary, and starts / stops the game server in a tmux
    /// session the way run_gameserver.sh / stop_wind_server.sh do. Runs wsl.exe directly (not legion.ps1) so a build's output streams line by line and
    /// non-ASCII compiler messages arrive as UTF-8. One action per job at a time; Deploy / Run / Stop touch the shared
    /// server, so only one of those runs at a time across all jobs.
    /// </summary>
    public sealed class ServerOpsService
    {
        private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private const string PidMarker = "@@AL_PID ";

        private readonly LegionService _legion;
        private readonly ILogger<ServerOpsService> _logger;
        private readonly Dictionary<string, ServerOpRun> _runs = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private ServerOpRun? _serverRun; // the Deploy / Run / Stop in progress

        public ServerOpsService(LegionService legion, ILogger<ServerOpsService> logger)
        {
            _legion = legion;
            _logger = logger;
        }

        /// <summary>Raised with the job name when its run starts, prints output or ends (from background threads).</summary>
        public event Action<string>? Changed;

        /// <summary>Raised when a run ends (from a background thread).</summary>
        public event Action<ServerOpRun>? Finished;

        /// <summary>The job's latest run (running or finished), or null.</summary>
        public ServerOpRun? Get(string job)
        {
            lock (_gate) return _runs.TryGetValue(job, out var r) ? r : null;
        }

        public void Clear(string job)
        {
            lock (_gate)
            {
                if (_runs.TryGetValue(job, out var r) && !r.Running) _runs.Remove(job);
            }
            Changed?.Invoke(job);
        }

        // ------------------------------------------------------------------------------------------
        // Deploy targets: each job may deploy to its own folder; the rest use deployRoot from Settings.
        // ------------------------------------------------------------------------------------------
        private string DeployTargetsPath => Path.Combine(_legion.DataDir, "deploy-targets.json");

        /// <summary>The job's own deploy folder, or null if it uses the default.</summary>
        public string? GetOwnDeployTarget(string job)
        {
            try
            {
                if (!File.Exists(DeployTargetsPath)) return null;
                var root = JsonNode.Parse(File.ReadAllText(DeployTargetsPath)) as JsonObject;
                var hit = root?.FirstOrDefault(p => string.Equals(p.Key, job, StringComparison.OrdinalIgnoreCase));
                return hit?.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }

        public string DefaultDeployTarget => _legion.LoadConfig()?.DeployRoot ?? LegionConfig.DefaultDeployRoot;

        public string BuildCmd => _legion.LoadConfig()?.BuildCmd ?? LegionConfig.DefaultBuildCmd;

        public string GetDeployTarget(string job) => GetOwnDeployTarget(job) ?? DefaultDeployTarget;

        /// <summary>Saves the job's deploy folder; blank or the default removes the job's own entry.</summary>
        public CommandResult SaveDeployTarget(string job, string? target)
        {
            var value = target?.Trim();
            if (!string.IsNullOrEmpty(value) && !LegionService.IsValidWslPath(value))
                return new CommandResult(false, $"배포 경로가 올바르지 않습니다: {value} (절대 경로 또는 ~/..., 공백 불가)");
            try
            {
                var root = File.Exists(DeployTargetsPath) ? JsonNode.Parse(File.ReadAllText(DeployTargetsPath)) as JsonObject : null;
                root ??= new JsonObject();
                foreach (var key in root.Select(p => p.Key).Where(k => string.Equals(k, job, StringComparison.OrdinalIgnoreCase)).ToList())
                    root.Remove(key);
                if (!string.IsNullOrEmpty(value) && value != DefaultDeployTarget) root[job] = value;
                File.WriteAllText(DeployTargetsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return new CommandResult(true, "");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new CommandResult(false, $"배포 경로를 저장하지 못했습니다: {ex.Message}");
            }
        }

        /// <summary>A renamed job keeps its deploy folder.</summary>
        public void RenameJob(string oldName, string newName)
        {
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;
            if (GetOwnDeployTarget(oldName) is not { } target) return;
            SaveDeployTarget(oldName, null);
            SaveDeployTarget(newName, target);
        }

        // ------------------------------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------------------------------

        /// <summary>Starts the action in the background; returns an error message if it cannot start.</summary>
        public string? Start(JobInfo job, ServerAction action)
        {
            if (job.IsWindows) return "Build / Deploy / Run / Stop은 WSL job에서만 쓸 수 있습니다.";
            var cfg = _legion.LoadConfig();
            if (cfg is null) return "legion.json이 없습니다. 먼저 Settings를 저장하세요.";
            if (cfg.Distro is { } d && !Regex.IsMatch(d, "^[A-Za-z0-9_.-]+$")) return $"WSL 배포판 이름이 올바르지 않습니다: {d}";

            var folder = string.IsNullOrWhiteSpace(job.Path) ? $"{cfg.JobsRoot.TrimEnd('/')}/{job.Job}" : job.Path;
            var dest = GetDeployTarget(job.Job);
            if (action is ServerAction.Deploy or ServerAction.Run && !LegionService.IsValidWslPath(dest))
                return $"배포 경로가 올바르지 않습니다: {dest}";

            var env = new Dictionary<string, string>
            {
                ["AL_JOB_DIR"] = folder,
                ["AL_DEST"] = dest,
                ["AL_FILE"] = LegionConfig.DeployFile,
                ["AL_BUILD_CMD"] = cfg.BuildCmd,
                ["AL_SESSION"] = cfg.ServerSession,
            };
            var (script, detail) = action switch
            {
                ServerAction.Build => (BuildScript, $"{folder} · {cfg.BuildCmd}"),
                ServerAction.Deploy => (DeployScript, $"{folder}/{LegionConfig.DeployFile} → {dest}"),
                ServerAction.Run => (RunScript, $"{dest} · tmux {cfg.ServerSession}"),
                _ => (StopScript, $"tmux {cfg.ServerSession}"),
            };

            var run = new ServerOpRun(job.Job, action, detail);
            lock (_gate)
            {
                if (_runs.TryGetValue(job.Job, out var cur) && cur.Running)
                    return $"{job.Job}에서 {cur.Action}이(가) 진행 중입니다.";
                if (action != ServerAction.Build && _serverRun is { Running: true } other)
                    return $"{other.Job}에서 {other.Action}이(가) 진행 중입니다. 끝난 뒤 다시 시도하세요.";
                _runs[job.Job] = run;
                if (action != ServerAction.Build) _serverRun = run;
            }

            _ = Task.Run(() => ExecuteAsync(run, cfg.Distro, script, env));
            Changed?.Invoke(job.Job);
            return null;
        }

        /// <summary>Stops a running Build: TERM to its process group inside WSL, then the wsl.exe process if it is still there.</summary>
        public async Task CancelAsync(string job)
        {
            var run = Get(job);
            if (run is not { Running: true }) return;
            run.Cancelled = true;
            run.Add("■ 취소 요청…");
            Changed?.Invoke(job);

            if (run.Pid is { } pid)
                await RunWslAsync(_legion.LoadConfig()?.Distro, $"kill -TERM -- -{pid} 2>/dev/null", null, TimeSpan.FromSeconds(15));

            var proc = run.Process;
            if (proc is null) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            }
            catch (InvalidOperationException) { /* already disposed */ }
        }

        /// <summary>Whether the server's tmux session is up; null if WSL could not be asked.</summary>
        public async Task<ServerStatus?> GetStatusAsync()
        {
            var cfg = _legion.LoadConfig();
            if (cfg is null) return null;
            var (ok, output) = await RunWslAsync(cfg.Distro, StatusScript,
                new Dictionary<string, string> { ["AL_SESSION"] = cfg.ServerSession }, TimeSpan.FromSeconds(15));
            if (!ok) return null;
            var f = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3) return null;
            return new ServerStatus(f[0] == "running", int.TryParse(f[1], out var w) ? w : 0, int.TryParse(f[2], out var p) ? p : 0);
        }

        private async Task ExecuteAsync(ServerOpRun run, string? distro, string script, Dictionary<string, string> env)
        {
            Process? proc = null;
            try
            {
                proc = Process.Start(WslPsi(distro, script, env))!;
                run.Process = proc;
                var stdout = PumpAsync(run, proc.StandardOutput);
                var stderr = PumpAsync(run, proc.StandardError);
                await Task.WhenAll(stdout, stderr);
                await proc.WaitForExitAsync();
                run.Finish(proc.ExitCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Action} for {Job} failed", run.Action, run.Job);
                run.Add($"실행 실패: {ex.Message}");
                run.Finish(-1);
            }
            finally
            {
                run.Process = null;
                proc?.Dispose();
            }
            Changed?.Invoke(run.Job);
            Finished?.Invoke(run);
        }

        private async Task PumpAsync(ServerOpRun run, StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (line.StartsWith(PidMarker, StringComparison.Ordinal))
                {
                    if (int.TryParse(line.AsSpan(PidMarker.Length), out var pid)) run.Pid = pid;
                    continue;
                }
                run.Add(Ansi.Replace(line, ""));
                Changed?.Invoke(run.Job);
            }
        }

        private static async Task<(bool Ok, string Output)> RunWslAsync(string? distro, string script,
            Dictionary<string, string>? env, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            Process? proc = null;
            try
            {
                proc = Process.Start(WslPsi(distro, script, env ?? new()))!;
                var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                return (proc.ExitCode == 0, (await stdout) + (await stderr));
            }
            catch (Exception ex)
            {
                try { proc?.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return (false, ex.Message);
            }
            finally
            {
                proc?.Dispose();
            }
        }

        // A login shell, so make, tmux and the user's PATH are there. Values travel as environment variables
        // (WSLENV) so paths and commands need no quoting on the wsl.exe command line.
        private static ProcessStartInfo WslPsi(string? distro, string script, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrWhiteSpace(distro)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro); }
            foreach (var a in new[] { "-e", "bash", "-lc", ScriptPrelude + script }) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env) psi.Environment[k] = v;
            if (env.Count > 0) psi.Environment["WSLENV"] = string.Join(':', env.Keys.Select(k => k + "/u"));
            return psi;
        }

        // ~ and ~/x arrive literally in the environment variables; expand them like the shell would.
        private const string ScriptPrelude = """
            expand() { case "$1" in "~") printf '%s' "$HOME" ;; "~/"*) printf '%s' "$HOME/${1#\~/}" ;; *) printf '%s' "$1" ;; esac; }

            """;

        // make runs in its own session (setsid: pid = process group) so Cancel can stop it together with its -j children.
        private const string BuildScript = """
            JOB_DIR=$(expand "$AL_JOB_DIR")
            cd "$JOB_DIR" 2>/dev/null || { echo "job folder not found: $JOB_DIR" >&2; exit 1; }
            echo "\$ cd $PWD && $AL_BUILD_CMD"
            setsid bash -c "$AL_BUILD_CMD" &
            pid=$!
            echo "@@AL_PID $pid"
            trap 'kill -TERM -- -$pid 2>/dev/null' TERM HUP INT
            wait $pid; rc=$?
            while kill -0 $pid 2>/dev/null; do wait $pid; rc=$?; done
            [ $rc -eq 0 ] && [ -f "$AL_FILE" ] && ls -al "$AL_FILE"
            exit $rc
            """;

        // Like deploy_gameServer.sh, but from the job folder. Copy to a temp file and rename over the target: works while
        // the server is running (cp onto a running binary fails with "Text file busy") and replaces a symlink such as
        // ~/wind/data/wind -> ../serverBinary/... instead of writing through it into serverBinary.
        private const string DeployScript = """
            JOB_DIR=$(expand "$AL_JOB_DIR"); DEST=$(expand "$AL_DEST")
            SRC="$JOB_DIR/$AL_FILE"
            [ -f "$SRC" ] || { echo "no built binary: $SRC - run Build first" >&2; exit 1; }
            [ -d "$DEST" ] || { echo "deploy folder not found: $DEST" >&2; exit 1; }
            echo "location: $DEST"
            echo "source:   $SRC"
            git -C "$JOB_DIR" log -1 --format='commit:   %h %s (%cr)' 2>/dev/null
            if [ -L "$DEST/$AL_FILE" ]; then
              echo "note: $DEST/$AL_FILE was a symlink -> $(readlink "$DEST/$AL_FILE"); the link is replaced, its target is left alone"
            fi
            TMP="$DEST/.$AL_FILE.deploy.$$"
            trap 'rm -f "$TMP"' EXIT
            cp "$SRC" "$TMP" && chmod +x "$TMP" && mv -f "$TMP" "$DEST/$AL_FILE" || exit 1
            ls -al "$DEST/$AL_FILE"
            sha1sum "$SRC" "$DEST/$AL_FILE" | awk '{ printf "sha1:     %.8s  %s\n", $1, $2 }'
            if tmux has-session -t "$AL_SESSION" 2>/dev/null; then
              echo "note: tmux session '$AL_SESSION' is running - Stop and Run to load the new binary"
            fi
            """;

        // Starts the servers the way run_gameserver.sh does, in the deploy folder: one tmux window per server, each running
        // in the background of the window's shell with its output teed into logs/. The shells get SIGHUP when the session
        // is killed and pass it on to their jobs, so stop_wind_server.sh run by hand stops these too. send-keys never
        // fails, so check what actually came up: every pane shows bash, so list the processes instead.
        // wind renames its process to WindServer once it is up, so both names are matched.
        private const string RunScript = """
            DEST=$(expand "$AL_DEST")
            [ -d "$DEST" ] || { echo "server folder not found: $DEST" >&2; exit 1; }
            [ -f "$DEST/$AL_FILE" ] || { echo "no server binary: $DEST/$AL_FILE - Deploy first" >&2; exit 1; }
            if tmux has-session -t "$AL_SESSION" 2>/dev/null; then
              echo "tmux session '$AL_SESSION' is already running - Stop it first" >&2; exit 1
            fi
            cd "$DEST" || exit 1
            echo "location: $PWD"
            mkdir -p logs && rm -f logs/*
            chmod +x "$AL_FILE"
            ls -al "$AL_FILE"

            # wind prints EUC-KR: convert each line to UTF-8 before tee (iconv would hold the output until the server exits)
            TO_UTF8="perl -MEncode -pe 'BEGIN{\$|=1} \$_=encode(\"UTF-8\", decode(\"cp949\", \$_))'"

            # start <window> <command> [raw]: opens the window and runs the command in the background of its shell,
            # output (converted to UTF-8 unless raw) into logs/log.<window>
            start() {
              local w pipe="| $TO_UTF8 |"
              [ "$3" = raw ] && pipe="|"
              if tmux has-session -t "$AL_SESSION" 2>/dev/null; then
                # "session:" = the next free index in the session; a bare "wind" would match the window wind0 instead
                w=$(tmux new-window -P -F '#{window_id}' -t "$AL_SESSION:" -n "$1" -c "$PWD")
              else
                w=$(tmux new-session -d -P -F '#{window_id}' -s "$AL_SESSION" -n "$1" -c "$PWD")
              fi
              if [ -z "$w" ]; then
                # the session was not there before Run: drop the half-started one so Run can be tried again
                echo "could not open tmux window $1 - stopping the servers started so far" >&2
                tmux kill-session -t "$AL_SESSION" 2>/dev/null
                exit 1
              fi
              echo "[$1] $2"
              tmux send-keys -t "$w" "$2 2>&1 $pipe tee ./logs/log.$1 &" C-m
            }

            start wind0   "./wind -P -d 0 wind0"     # master
            start wind1   "./wind -P -d 0 wind1"     # cache sync (Redis <-> DB)
            start wind2   "./wind -P -d 0 wind2"     # login
            start session "./sessionServer -d 0 --metrics=true --logoutput=console wind1000" raw
            start wind11  "./wind -P -D -d 0 wind11" # local (game play)
            start wind12  "./wind -P -d 0 wind12"

            sleep 2
            if ! tmux has-session -t "$AL_SESSION" 2>/dev/null; then
              echo "tmux session '$AL_SESSION' is not running after the start" >&2; exit 1
            fi
            echo "--- tmux session '$AL_SESSION': $(tmux list-windows -t "$AL_SESSION" | wc -l) windows ---"
            pgrep -a -x 'wind|WindServer'; pgrep -a -x sessionServer
            exit 0
            """;

        // Like stop_wind_server.sh: kill the tmux session; the servers end with their shells.
        private const string StopScript = """
            if ! tmux has-session -t "$AL_SESSION" 2>/dev/null; then
              echo "tmux session '$AL_SESSION' does not exist."; exit 0
            fi
            echo "Stopping tmux session '$AL_SESSION'..."
            tmux kill-session -t "$AL_SESSION" || exit 1
            for i in 1 2 3 4 5; do
              pgrep -x 'wind|WindServer|sessionServer' >/dev/null || break
              sleep 1
            done
            echo "tmux session '$AL_SESSION' stopped."
            if pgrep -x 'wind|WindServer|sessionServer' >/dev/null; then
              echo "still running:"; pgrep -a -x 'wind|WindServer|sessionServer'
            fi
            exit 0
            """;

        // "running|stopped <windows> <wind processes>"
        private const string StatusScript = """
            if tmux has-session -t "$AL_SESSION" 2>/dev/null; then
              printf 'running %s ' "$(tmux list-windows -t "$AL_SESSION" 2>/dev/null | wc -l)"
            else
              printf 'stopped 0 '
            fi
            pgrep -c -x 'wind|WindServer' || true
            """;
    }
}
