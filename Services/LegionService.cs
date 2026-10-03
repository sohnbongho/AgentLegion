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

    /// <summary>A job renamed or moved by `edit`: its environment ("wsl" / "windows") and absolute folder.</summary>
    public record RegisteredJob(string Env, string Path);

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
        private readonly string _dataDir;

        public LegionService(IWebHostEnvironment env, IConfiguration config)
        {
            _scriptPath = config["Legion:ScriptPath"] ?? FindScript(env.ContentRootPath);
            _dataDir = ResolveDataDir(config);
            MigrateLegacyData();
        }

        /// <summary>
        /// Finds legion.ps1 however the exe was started: the working directory (Visual Studio, `dotnet run`, run.bat),
        /// the exe's own folder (double-clicked, started from another folder), or the project folder above bin\Debug.
        /// </summary>
        private static string FindScript(string contentRoot)
        {
            foreach (var dir in new[] { contentRoot, AppContext.BaseDirectory })
            {
                var p = Path.Combine(dir, "legion.ps1");
                if (File.Exists(p)) return p;
            }
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 6 && d?.Parent is not null; i++)
            {
                d = d.Parent;
                var p = Path.Combine(d.FullName, "legion.ps1");
                if (File.Exists(p)) return p;
            }
            return Path.Combine(contentRoot, "legion.ps1");
        }

        /// <summary>
        /// Where settings (legion.json), the job registry (jobs.json) and logs live: one per-user folder, so the data
        /// does not depend on which exe or folder the app is started from. AGENTLEGION_HOME (or Legion:DataDir) overrides it.
        /// </summary>
        private static string ResolveDataDir(IConfiguration config)
        {
            var configured = config["Legion:DataDir"] ?? Environment.GetEnvironmentVariable("AGENTLEGION_HOME");
            var dir = !string.IsNullOrWhiteSpace(configured)
                ? Environment.ExpandEnvironmentVariables(configured)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentLegion");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // Older versions kept the files next to legion.ps1: copy them over once (never overwrite, never delete).
        private void MigrateLegacyData()
        {
            var legacy = Path.GetDirectoryName(_scriptPath);
            if (legacy is null || SamePath(legacy, _dataDir)) return;
            foreach (var name in new[] { "legion.json", "jobs.json" })
            {
                var from = Path.Combine(legacy, name);
                var to = Path.Combine(_dataDir, name);
                if (!File.Exists(from) || File.Exists(to)) continue;
                try { File.Copy(from, to); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* legion.ps1 retries the copy */ }
            }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

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

        private string ConfigPath => Path.Combine(_dataDir, "legion.json");

        public bool IsConfigured => File.Exists(ConfigPath);

        /// <summary>Per-user data folder: legion.json, jobs.json and logs.</summary>
        public string DataDir => _dataDir;

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

        /// <summary>Runs `code .` in the job folder (WSL jobs through WSL, Windows jobs on Windows).</summary>
        public Task<CommandResult> OpenEditorAsync(string job) => RunAsync(NetworkTimeout, "code", job);

        private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(15); // moving a big project across drives

        /// <summary>
        /// Edits a job; null/blank arguments are left unchanged. Name only relabels the job, Path moves its folder.
        /// </summary>
        public Task<CommandResult> EditJobAsync(string job, string? newName, string? branch, string? repo, string? newPath)
        {
            var args = new List<string> { "edit", job };
            void Add(string flag, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) args.AddRange(new[] { flag, value.Trim() });
            }
            Add("-NewName", newName);
            Add("-Branch", branch);
            Add("-Repo", repo);
            Add("-NewPath", newPath);
            return RunAsync(MoveTimeout, args.ToArray());
        }

        /// <summary>
        /// Where a job that was renamed or moved by `edit` lives (jobs.json), or null for a job in the default layout.
        /// </summary>
        public RegisteredJob? GetRegisteredJob(string job)
        {
            try
            {
                var file = Path.Combine(DataDir, "jobs.json");
                if (!File.Exists(file)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(p.Name, job, StringComparison.OrdinalIgnoreCase)) continue;
                    var env = p.Value.GetProperty("env").GetString();
                    var path = p.Value.GetProperty("path").GetString();
                    return env is null || path is null ? null : new RegisteredJob(env, path);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>Opens a plain shell (WSL or PowerShell, not claude) in the job folder in a Windows Terminal tab.</summary>
        public Task<CommandResult> OpenTerminalAsync(string job) => RunAsync(DefaultTimeout, "shell", job);

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
            // the script and this service must use the same data folder, also when it was set through Legion:DataDir
            psi.Environment["AGENTLEGION_HOME"] = _dataDir;

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
