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

        private readonly LegionService _legion;
        private readonly object _gate = new();
        private readonly Dictionary<string, PtySession> _sessions = new();

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

        /// <summary>Returns the live session for the job, starting one if there is none.</summary>
        public PtySession GetOrStart(string job, int cols, int rows)
        {
            PtySession session;
            lock (_gate)
            {
                if (_sessions.TryGetValue(job, out var existing) && existing.IsRunning) return existing;
                existing?.Dispose();
                var cfg = _legion.LoadConfig() ?? throw new InvalidOperationException("Not configured. Set up Settings first.");
                var (commandLine, workingDirectory) = BuildLaunch(job, cfg);
                session = PtySession.Start(commandLine, cols, rows, cfg.StateDetection, workingDirectory);
                _sessions[job] = session;
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
                if (!_sessions.Remove(job, out s)) return;
            }
            s.Dispose();
            Changed?.Invoke();
        }

        /// <summary>
        /// A job lives in one environment. A folder in the Windows jobs root makes it a Windows PowerShell job;
        /// otherwise it is a WSL job (the same rule legion.ps1 uses).
        /// </summary>
        private static (string CommandLine, string? WorkingDirectory) BuildLaunch(string job, LegionConfig cfg)
        {
            if (!JobName.IsMatch(job)) throw new ArgumentException($"Invalid job name: '{job}'");

            var winDir = Path.Combine(cfg.ResolvedWindowsRoot, job);
            if (Directory.Exists(Path.Combine(winDir, ".git")))
            {
                // Native Windows: PowerShell runs claude in the job folder; the session ends when claude exits.
                // The command comes from the local config file (like claudeCmd for WSL).
                var cmd = cfg.WindowsClaudeCmd.Replace("\"", "\\\"");
                return ($"powershell.exe -NoLogo -Command \"{cmd}\"", winDir);
            }

            return (BuildWslCommandLine(job, cfg), null);
        }

        private static string BuildWslCommandLine(string job, LegionConfig cfg)
        {
            if (!RootPath.IsMatch(cfg.JobsRoot)) throw new InvalidOperationException($"Invalid jobs root: '{cfg.JobsRoot}'");

            var distro = "";
            if (!string.IsNullOrWhiteSpace(cfg.Distro))
            {
                if (!DistroName.IsMatch(cfg.Distro)) throw new InvalidOperationException($"Invalid distro: '{cfg.Distro}'");
                distro = $"-d {cfg.Distro} ";
            }

            // claudeCmd comes from the local config file and runs inside a login shell, like `legion.ps1 start`.
            return $"wsl.exe {distro}--cd {cfg.JobsRoot}/{job} -- bash -lc \"{cfg.ClaudeCmd.Replace("\"", "\\\"")}\"";
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
