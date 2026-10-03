using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentLegion.Services
{
    /// <param name="Repo">The job's origin URL (credentials already stripped), empty if it has no remote.</param>
    public record JobInfo(string Job, string Branch, int Changes, string? Repo = null, string Env = "wsl", string? Path = null)
    {
        public bool IsWindows => Env.Equals("windows", StringComparison.OrdinalIgnoreCase);
    }

    public record JobsResult(IReadOnlyList<JobInfo> Jobs, string? Error);

    /// <summary>A job's most recent Claude conversation (the id that `claude --resume` takes).</summary>
    public record LastSession(string Id, DateTime Modified, int SizeKb);

    public record CommandResult(bool Ok, string Output);

    /// <summary>Cumulative Claude token usage for a job, summed from its session transcripts.</summary>
    public record JobUsage(long Input, long Output, long CacheCreate, long CacheRead, int Messages, int Sessions)
    {
        /// <summary>Tokens that were actually sent or generated (cache reads excluded, they inflate the total).</summary>
        public long Billable => Input + Output + CacheCreate;
    }

    /// <param name="WindowsJobsRoot">Where Windows PowerShell jobs live; null = the default %USERPROFILE%\agentjobs.</param>
    public record LegionConfig(string? Distro, string JobsRoot, string Repo, string ClaudeCmd = "claude",
        SessionStateDetection StateDetection = SessionStateDetection.Title,
        string? WindowsJobsRoot = null, string WindowsClaudeCmd = "claude", bool ResumeLastSession = true)
    {
        public const string DefaultWindowsRoot = @"%USERPROFILE%\agentjobs";

        /// <summary>The Windows jobs root with environment variables expanded.</summary>
        public string ResolvedWindowsRoot =>
            Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(WindowsJobsRoot) ? DefaultWindowsRoot : WindowsJobsRoot);
    }

    /// <summary>Thin wrapper that runs legion.ps1 and parses its output.</summary>
    public class LegionService
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly string _scriptPath;

        public LegionService(IWebHostEnvironment env, IConfiguration config)
        {
            _scriptPath = config["Legion:ScriptPath"] ?? Path.Combine(env.ContentRootPath, "legion.ps1");
        }

        public async Task<JobsResult> GetJobsAsync()
        {
            var r = await RunAsync(DefaultTimeout, "status", "-Json");
            if (!r.Ok) return new JobsResult(Array.Empty<JobInfo>(), r.Output);
            try
            {
                var jobs = JsonSerializer.Deserialize<List<JobInfo>>(r.Output, JsonOptions) ?? new();
                return new JobsResult(jobs, null);
            }
            catch (JsonException ex)
            {
                return new JobsResult(Array.Empty<JobInfo>(), $"Unexpected output from legion.ps1: {ex.Message}");
            }
        }

        private string ConfigPath => Path.Combine(Path.GetDirectoryName(_scriptPath)!, "legion.json");

        public bool IsConfigured => File.Exists(ConfigPath);

        /// <summary>Folder next to legion.ps1; used for diagnostics logs.</summary>
        public string DataDir => Path.GetDirectoryName(_scriptPath)!;

        /// <summary>Reads legion.json, or null if it is missing or unreadable.</summary>
        public LegionConfig? LoadConfig()
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                var root = doc.RootElement;
                string? Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                // "title" (default) reads Claude Code's status title; "activity" treats flowing output as work
                var detection = string.Equals(Str("stateDetection"), "activity", StringComparison.OrdinalIgnoreCase)
                    ? SessionStateDetection.Activity
                    : SessionStateDetection.Title;
                return new LegionConfig(Str("distro"), Str("jobsRoot") ?? "~/agentjobs", Str("repo") ?? "", Str("claudeCmd") ?? "claude",
                    detection, Str("windowsJobsRoot"), Str("windowsClaudeCmd") ?? "claude",
                    !(root.TryGetProperty("resumeLastSession", out var rl) && rl.ValueKind == JsonValueKind.False));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public Task<CommandResult> SaveConfigAsync(string? distro, string jobsRoot, string repo, string? windowsRoot = null)
        {
            var args = new List<string> { "init", "-Repo", repo.Trim(), "-Root", jobsRoot.Trim() };
            if (!string.IsNullOrWhiteSpace(distro)) args.AddRange(new[] { "-Distro", distro.Trim() });
            if (!string.IsNullOrWhiteSpace(windowsRoot)) args.AddRange(new[] { "-WindowsRoot", windowsRoot.Trim() });
            return RunAsync(DefaultTimeout, args.ToArray());
        }

        /// <param name="windows">true = a native Windows PowerShell job (e.g. Unity), false = a WSL job.</param>
        public Task<CommandResult> AddJobAsync(string job, string? branch, string? repo, bool windows = false)
        {
            var args = new List<string> { "add", job.Trim() };
            if (!string.IsNullOrWhiteSpace(branch)) args.AddRange(new[] { "-Branch", branch.Trim() });
            if (!string.IsNullOrWhiteSpace(repo)) args.AddRange(new[] { "-Repo", repo.Trim() });
            if (windows) args.AddRange(new[] { "-Target", "windows" });
            return RunAsync(NetworkTimeout, args.ToArray());
        }

        /// <summary>Installed WSL distributions (wsl -l -q); empty if WSL is unavailable.</summary>
        public async Task<IReadOnlyList<string>> GetDistrosAsync()
        {
            var psi = new ProcessStartInfo("wsl.exe", "-l -q")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode, // wsl.exe prints UTF-16LE
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Process? proc = null;
            try
            {
                proc = Process.Start(psi)!;
                var text = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(l => l.Replace("\0", "").Trim())
                           .Where(l => l.Length > 0)
                           .ToList();
            }
            catch (Exception)
            {
                try { proc?.Kill(); } catch { /* already exited */ }
                return Array.Empty<string>();
            }
            finally
            {
                proc?.Dispose();
            }
        }

        public async Task<(JobUsage? Usage, string? Error)> GetUsageAsync(string job)
        {
            var r = await RunAsync(DefaultTimeout, "usage", job, "-Json");
            if (!r.Ok) return (null, r.Output);
            try
            {
                return (JsonSerializer.Deserialize<JobUsage>(r.Output, JsonOptions), null);
            }
            catch (JsonException ex)
            {
                return (null, $"Unexpected output from legion.ps1: {ex.Message}");
            }
        }

        /// <summary>The job's newest Claude conversation, or null if it never had one (or it cannot be read).</summary>
        public async Task<LastSession?> GetLastSessionAsync(string job)
        {
            var r = await RunAsync(DefaultTimeout, "last-session", job, "-Json");
            if (!r.Ok || string.IsNullOrWhiteSpace(r.Output) || r.Output.Trim() == "null") return null;
            try { return JsonSerializer.Deserialize<LastSession>(r.Output, JsonOptions); }
            catch (JsonException) { return null; }
        }

        /// <summary>Jobs root as configured (e.g. ~/agentjobs), for displaying a job's folder.</summary>
        public string JobsRoot => LoadConfig()?.JobsRoot ?? "~/agentjobs";

        public Task<CommandResult> StartAsync(string job) => RunAsync(DefaultTimeout, "start", job);

        public Task<CommandResult> DiffAsync(string job) => RunAsync(DefaultTimeout, "diff", job);

        public Task<CommandResult> PushAsync(string job) => RunAsync(NetworkTimeout, "push", job);

        /// <summary>Removes a job. Never forces: legion.ps1 refuses if work would be lost.</summary>
        public Task<CommandResult> RemoveAsync(string job) => RunAsync(DefaultTimeout, "remove", job);

        private async Task<CommandResult> RunAsync(TimeSpan timeout, params string[] args)
        {
            if (!File.Exists(_scriptPath))
                return new CommandResult(false, $"legion.ps1 not found: {_scriptPath}");

            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _scriptPath }.Concat(args))
                psi.ArgumentList.Add(a);

            using var cts = new CancellationTokenSource(timeout);
            Process? proc = null;
            try
            {
                proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);

                var (o, e) = (await stdout, await stderr);
                if (proc.ExitCode != 0)
                    return new CommandResult(false, FirstNonEmpty(e, o) ?? $"legion.ps1 exited with {proc.ExitCode}");

                // git writes progress to stderr even on success, so show both
                return new CommandResult(true, string.Join("\n", new[] { o.Trim(), e.Trim() }.Where(s => s.Length > 0)));
            }
            catch (OperationCanceledException)
            {
                try { proc?.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return new CommandResult(false, $"legion.ps1 timed out after {timeout.TotalSeconds:0}s");
            }
            catch (Exception ex)
            {
                return new CommandResult(false, ex.Message);
            }
            finally
            {
                proc?.Dispose();
            }
        }

        private static string? FirstNonEmpty(params string[] texts) =>
            texts.Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0);
    }
}
