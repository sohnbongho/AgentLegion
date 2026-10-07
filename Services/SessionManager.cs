using System.Text.RegularExpressions;

namespace AgentLegion.Services
{
    /// <summary>
    /// Owns one long-lived terminal session per job. Sessions belong to the server, not to a page,
    /// so they keep running while the user navigates elsewhere in the UI.
    /// </summary>
    public sealed class SessionManager : IDisposable
    {
        private static readonly Regex JobName = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
        private static readonly Regex DistroName = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
        private static readonly Regex RootPath = new("^[~/A-Za-z0-9_.-]+$", RegexOptions.Compiled);
        // a WSL job folder is passed to `wsl --cd` unquoted, so it must not contain spaces or shell characters
        private static readonly Regex WslFolder = new("^[~/A-Za-z0-9_.@+-]+$", RegexOptions.Compiled);

        private readonly LegionService _legion;
        private readonly object _gate = new();
        private readonly Dictionary<string, PtySession> _sessions = new();

        // job -> the Claude conversation its current session was started from (null = a fresh conversation)
        private readonly Dictionary<string, string?> _resumed = new();
        private static readonly Regex SessionId = new("^[0-9a-fA-F-]{8,64}$", RegexOptions.Compiled);

        // job -> the name its current session was started with (`claude --name`); the same rule as legion.ps1
        private readonly Dictionary<string, string> _names = new();
        private static readonly Regex ClaudeName = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

        private readonly Dictionary<string, SessionState> _lastStates = new();
        private readonly Timer _poll;

        public SessionManager(LegionService legion)
        {
            _legion = legion;
            _poll = new Timer(_ => PollStates(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public SessionState GetState(string job)
        {
            lock (_gate) return _sessions.TryGetValue(job, out var s) ? s.State : SessionState.Stopped;
        }

        // State depends on output timing, so it has to be sampled; notify only on actual transitions.
        private void PollStates()
        {
            var transitions = new List<string>();
            lock (_gate)
            {
                var now = _sessions.ToDictionary(kv => kv.Key, kv => kv.Value.State);
                foreach (var (job, state) in now)
                {
                    var before = _lastStates.TryGetValue(job, out var b) ? b : SessionState.Stopped;
                    if (before != state)
                        transitions.Add($"{job} {before}->{state} | last activity: {_sessions[job].LastActivityPreview}");
                }
                var changed = transitions.Count > 0 || _lastStates.Keys.Except(now.Keys).Any();
                if (!changed) return;
                _lastStates.Clear();
                foreach (var kv in now) _lastStates[kv.Key] = kv.Value;
            }
            LogTransitions(transitions);
            Changed?.Invoke();
        }

        // Small diagnostics file so a misleading state can be traced to the output that caused it.
        private void LogTransitions(List<string> lines)
        {
            if (lines.Count == 0) return;
            try
            {
                var dir = Path.Combine(_legion.DataDir, "logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "session-state.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllLines(path, lines.Select(l => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {l}"));
            }
            catch (IOException) { /* diagnostics must never break the UI */ }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Raised when any session starts, ends or changes state (used by the sidebar).</summary>
        public event Action? Changed;

        public bool IsRunning(string job)
        {
            lock (_gate) return _sessions.TryGetValue(job, out var s) && s.IsRunning;
        }

        public PtySession? Get(string job)
        {
            lock (_gate) return _sessions.GetValueOrDefault(job);
        }

        /// <summary>The conversation the job's current session was resumed from, or null for a fresh one.</summary>
        public string? ResumedId(string job)
        {
            lock (_gate) return _resumed.GetValueOrDefault(job);
        }

        /// <summary>The name the job's running session was started with, or null when it is not running.</summary>
        public string? SessionName(string job)
        {
            lock (_gate) return _sessions.TryGetValue(job, out var s) && s.IsRunning ? _names.GetValueOrDefault(job) : null;
        }

        // Other Claude sessions on this PC address the session by this name (cross-session messaging). It goes on the
        // command line, so validate it.
        private static string NameArg(string name)
        {
            if (!ClaudeName.IsMatch(name)) throw new InvalidOperationException($"Invalid Claude name: '{name}'");
            return $" --name {name}";
        }

        // `claude --resume <id>` continues that conversation; the id comes from a file name on disk, so validate it.
        private static string ResumeArg(string? id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            if (!SessionId.IsMatch(id)) throw new ArgumentException($"Invalid session id: '{id}'");
            return $" --resume {id}";
        }

        /// <summary>
        /// Returns the live session for the job, starting one if there is none.
        /// <paramref name="resumeId"/> continues that Claude conversation instead of starting a new one.
        /// </summary>
        public PtySession GetOrStart(string job, int cols, int rows, string? resumeId = null)
        {
            PtySession session;
            lock (_gate)
            {
                if (_sessions.TryGetValue(job, out var existing) && existing.IsRunning) return existing;
                existing?.Dispose();
                var cfg = _legion.LoadConfig() ?? throw new InvalidOperationException("Not configured. Set up Settings first.");
                var name = _legion.GetClaudeName(job);
                var (commandLine, workingDirectory) = BuildLaunch(job, cfg, resumeId, name, _legion.GetRegisteredJob(job));
                session = PtySession.Start(commandLine, cols, rows, cfg.StateDetection, workingDirectory);
                _sessions[job] = session;
                _resumed[job] = resumeId;
                _names[job] = name;
            }
            session.Exited += () => Changed?.Invoke();
            Changed?.Invoke();
            return session;
        }

        public void Stop(string job)
        {
            PtySession? s;
            lock (_gate)
            {
                _resumed.Remove(job);
                _names.Remove(job);
                if (!_sessions.Remove(job, out s)) return;
            }
            s.Dispose();
            Changed?.Invoke();
        }

        /// <summary>
        /// A job lives in one environment (the same rule legion.ps1 uses): jobs.json says so for a job renamed or
        /// moved by `edit`; otherwise a folder in the Windows jobs root makes it a Windows PowerShell job and
        /// anything else is a WSL job in the WSL jobs root.
        /// </summary>
        private static (string CommandLine, string? WorkingDirectory) BuildLaunch(
            string job, LegionConfig cfg, string? resumeId, string claudeName, RegisteredJob? registered)
        {
            if (!JobName.IsMatch(job)) throw new ArgumentException($"Invalid job name: '{job}'");

            var winDir = registered is { Env: "windows" } ? registered.Path : Path.Combine(cfg.ResolvedWindowsRoot, job);
            var isWindows = registered is { Env: "windows" } || (registered is null && Directory.Exists(Path.Combine(winDir, ".git")));
            if (isWindows)
            {
                if (!Directory.Exists(winDir)) throw new InvalidOperationException($"The job folder was not found: {winDir}");
                // Native Windows: PowerShell runs claude in the job folder; the session ends when claude exits.
                // The command comes from the local config file (like claudeCmd for WSL).
                var cmd = (cfg.WindowsClaudeCmd + ResumeArg(resumeId) + NameArg(claudeName)).Replace("\"", "\\\"");
                return ($"powershell.exe -NoLogo -Command \"{cmd}\"", winDir);
            }

            var wslDir = registered is { Env: "wsl" } ? registered.Path : null;
            return (BuildWslCommandLine(job, cfg, resumeId, claudeName, wslDir), null);
        }

        private static string BuildWslCommandLine(string job, LegionConfig cfg, string? resumeId, string claudeName, string? folder)
        {
            if (!RootPath.IsMatch(cfg.JobsRoot)) throw new InvalidOperationException($"Invalid jobs root: '{cfg.JobsRoot}'");
            folder ??= $"{cfg.JobsRoot}/{job}";
            if (!WslFolder.IsMatch(folder)) throw new InvalidOperationException($"Invalid job folder: '{folder}'");

            var distro = "";
            if (!string.IsNullOrWhiteSpace(cfg.Distro))
            {
                if (!DistroName.IsMatch(cfg.Distro)) throw new InvalidOperationException($"Invalid distro: '{cfg.Distro}'");
                distro = $"-d {cfg.Distro} ";
            }

            // claudeCmd comes from the local config file and runs inside a login shell, like `legion.ps1 start`.
            return $"wsl.exe {distro}--cd {folder} -- bash -lc \"{(cfg.ClaudeCmd + ResumeArg(resumeId) + NameArg(claudeName)).Replace("\"", "\\\"")}\"";
        }

        public void Dispose()
        {
            _poll.Dispose();
            PtySession[] all;
            lock (_gate)
            {
                all = _sessions.Values.ToArray();
                _sessions.Clear();
            }
            foreach (var s in all) s.Dispose();
        }
    }
}
