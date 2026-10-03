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

        public SessionManager(LegionService legion) => _legion = legion;

        /// <summary>Raised when any session starts or ends (used by the sidebar to show status).</summary>
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
                session = PtySession.Start(BuildCommandLine(job), cols, rows);
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

        private string BuildCommandLine(string job)
        {
            if (!JobName.IsMatch(job)) throw new ArgumentException($"Invalid job name: '{job}'");
            var cfg = _legion.LoadConfig() ?? throw new InvalidOperationException("Not configured. Set up Settings first.");
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
