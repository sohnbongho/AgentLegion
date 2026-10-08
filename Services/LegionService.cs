using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentLegion.Services
{
    /// <param name="Repo">The job's origin URL (credentials already stripped), empty if it has no remote.</param>
    /// <param name="ClaudeName">The name claude runs under (`claude --name`); other Claude sessions address it by this name.</param>
    public record JobInfo(string Job, string Branch, int Changes, string? Repo = null, string Env = "wsl", string? Path = null,
        string? ClaudeName = null)
    {
        public bool IsWindows => Env.Equals("windows", StringComparison.OrdinalIgnoreCase);

        /// <summary>The Claude session name; a job without its own runs under its job name.</summary>
        public string SessionName => string.IsNullOrWhiteSpace(ClaudeName) ? Job : ClaudeName;
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
    /// <param name="TerminalFontFamily">Job terminal font (CSS font-family); null = the built-in monospace list.</param>
    /// <param name="DeployRoot">WSL folder a job's server binary is deployed to unless the job has its own (deploy-targets.json).</param>
    /// <param name="BuildCmd">Command a WSL job's Build button runs in the job folder.</param>
    /// <param name="ServerSession">tmux session Run starts the game server in and Stop kills.</param>
    /// <param name="LogRoot">Folder the Logs tab opens by default.</param>
    public record LegionConfig(string? Distro, string JobsRoot, string Repo, string ClaudeCmd = "claude",
        SessionStateDetection StateDetection = SessionStateDetection.Title,
        string? WindowsJobsRoot = null, string WindowsClaudeCmd = "claude", bool ResumeLastSession = true,
        string? TerminalFontFamily = null, int TerminalFontSize = LegionConfig.DefaultTerminalFontSize,
        string DeployRoot = LegionConfig.DefaultDeployRoot, string BuildCmd = LegionConfig.DefaultBuildCmd,
        string ServerSession = LegionConfig.DefaultServerSession,
        string LogRoot = LegionConfig.DefaultLogRoot, IReadOnlyList<string>? JobOrder = null)
    {
        public const int DefaultTerminalFontSize = 16;
        public const int MinTerminalFontSize = 8;
        public const int MaxTerminalFontSize = 40;
        public const string DefaultWindowsRoot = @"%USERPROFILE%\agentjobs";
        public const string DefaultDeployRoot = "~/wind/data";
        public const string DefaultBuildCmd = "make";
        public const string DefaultServerSession = "wind";
        public const string DefaultLogRoot = "~/wind/data_local/logs";
        public const string DeployFile = "wind";

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
                var fontSize = root.TryGetProperty("terminalFontSize", out var fs) && fs.ValueKind == JsonValueKind.Number && fs.TryGetInt32(out var n)
                    ? Math.Clamp(n, LegionConfig.MinTerminalFontSize, LegionConfig.MaxTerminalFontSize)
                    : LegionConfig.DefaultTerminalFontSize;
                var fontFamily = Str("terminalFontFamily")?.Trim();
                string Or(string name, string fallback) => Str(name)?.Trim() is { Length: > 0 } v ? v : fallback;
                return new LegionConfig(Str("distro"), Str("jobsRoot") ?? "~/agentjobs", Str("repo") ?? "", Str("claudeCmd") ?? "claude",
                    detection, Str("windowsJobsRoot"), Str("windowsClaudeCmd") ?? "claude",
                    !(root.TryGetProperty("resumeLastSession", out var rl) && rl.ValueKind == JsonValueKind.False),
                    string.IsNullOrEmpty(fontFamily) ? null : fontFamily, fontSize,
                    Or("deployRoot", LegionConfig.DefaultDeployRoot), Or("buildCmd", LegionConfig.DefaultBuildCmd),
                    Or("serverSession", LegionConfig.DefaultServerSession),
                    Or("logRoot", LegionConfig.DefaultLogRoot),
                    root.TryGetProperty("jobOrder", out var jo) && jo.ValueKind == JsonValueKind.Array
                        ? jo.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                        : null);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <param name="claudeCmd">Command (or absolute path) that starts claude in WSL; blank keeps the saved one.</param>
        /// <param name="windowsClaudeCmd">The same for Windows PowerShell jobs.</param>
        public Task<CommandResult> SaveConfigAsync(string? distro, string jobsRoot, string repo, string? windowsRoot = null,
            string? claudeCmd = null, string? windowsClaudeCmd = null)
        {
            var args = new List<string> { "init", "-Repo", repo.Trim(), "-Root", jobsRoot.Trim() };
            if (!string.IsNullOrWhiteSpace(distro)) args.AddRange(new[] { "-Distro", distro.Trim() });
            if (!string.IsNullOrWhiteSpace(windowsRoot)) args.AddRange(new[] { "-WindowsRoot", windowsRoot.Trim() });
            if (!string.IsNullOrWhiteSpace(claudeCmd)) args.AddRange(new[] { "-ClaudeCmd", claudeCmd.Trim() });
            if (!string.IsNullOrWhiteSpace(windowsClaudeCmd)) args.AddRange(new[] { "-WindowsClaudeCmd", windowsClaudeCmd.Trim() });
            return RunAsync(DefaultTimeout, args.ToArray());
        }

        /// <summary>
        /// Saves the job terminal font into legion.json (legion.ps1 does not use it; `init` keeps it).
        /// A blank family removes the setting so the built-in font list is used.
        /// </summary>
        public CommandResult SaveTerminalFont(string? family, int size) => UpdateConfig("터미널 글꼴", root =>
        {
            if (string.IsNullOrWhiteSpace(family)) root.Remove("terminalFontFamily");
            else root["terminalFontFamily"] = family.Trim();
            root["terminalFontSize"] = Math.Clamp(size, LegionConfig.MinTerminalFontSize, LegionConfig.MaxTerminalFontSize);
        });

        /// <summary>A WSL path the server scripts can take unquoted: absolute or ~/..., no spaces or shell characters.</summary>
        public static bool IsValidWslPath(string? path) =>
            path is not null && Regex.IsMatch(path.Trim(), @"^(~|~/[A-Za-z0-9_./@+-]*|/[A-Za-z0-9_./@+-]*)$");

        private static readonly Regex BuildCmdPattern = new(@"^[A-Za-z0-9_./=:@+ -]+$", RegexOptions.Compiled);

        /// <summary>
        /// Saves the Build / Deploy / Run settings into legion.json (legion.ps1 does not use them; `init` keeps them).
        /// Blank values fall back to the defaults.
        /// </summary>
        public CommandResult SaveServerSettings(string? deployRoot, string? buildCmd, string? session)
        {
            if (!string.IsNullOrWhiteSpace(deployRoot) && !IsValidWslPath(deployRoot))
                return new CommandResult(false, $"배포 경로가 올바르지 않습니다: {deployRoot} (절대 경로 또는 ~/..., 공백 불가)");
            if (!string.IsNullOrWhiteSpace(buildCmd) && !BuildCmdPattern.IsMatch(buildCmd.Trim()))
                return new CommandResult(false, $"빌드 명령에 쓸 수 없는 문자가 있습니다: {buildCmd} (따옴표, ;, |, & 등 불가)");
            if (!string.IsNullOrWhiteSpace(session) && !Regex.IsMatch(session.Trim(), "^[A-Za-z0-9_.-]+$"))
                return new CommandResult(false, $"tmux 세션 이름이 올바르지 않습니다: {session}");

            return UpdateConfig("서버 설정", root =>
            {
                void Set(string key, string? value, string fallback)
                {
                    if (string.IsNullOrWhiteSpace(value) || value.Trim() == fallback) root.Remove(key);
                    else root[key] = value.Trim();
                }
                Set("deployRoot", deployRoot, LegionConfig.DefaultDeployRoot);
                Set("buildCmd", buildCmd, LegionConfig.DefaultBuildCmd);
                root.Remove("serverScriptDir"); // no longer used: Run / Stop do not call the scripts in ~/script any more
                Set("serverSession", session, LegionConfig.DefaultServerSession);
            });
        }

        /// <summary>Saves the Logs tab's default folder; blank means the default. Only read through \\wsl.localhost, so spaces are fine.</summary>
        public CommandResult SaveLogRoot(string? logRoot)
        {
            var value = logRoot?.Trim();
            if (!string.IsNullOrEmpty(value) && !Regex.IsMatch(value, @"^(~|~/[^\x00-\x1f]*|/[^\x00-\x1f]*)$"))
                return new CommandResult(false, $"로그 폴더가 올바르지 않습니다: {logRoot} (절대 경로 또는 ~/...)");
            return UpdateConfig("로그 폴더", root =>
            {
                if (string.IsNullOrEmpty(value) || value == LegionConfig.DefaultLogRoot) root.Remove("logRoot");
                else root["logRoot"] = value;
            });
        }

        /// <summary>Saves the order of the job list (sidebar, Jobs page); an empty order means by name.</summary>
        public CommandResult SaveJobOrder(IEnumerable<string> jobs) => UpdateConfig("Job 순서", root =>
        {
            var order = new JsonArray(jobs.Select(j => (JsonNode?)JsonValue.Create(j)).ToArray());
            if (order.Count == 0) root.Remove("jobOrder");
            else root["jobOrder"] = order;
        });

        /// <summary>A renamed job keeps its place in the saved order.</summary>
        public CommandResult RenameInJobOrder(string oldName, string newName)
        {
            if (LoadConfig()?.JobOrder is not { } order || !order.Contains(oldName)) return new CommandResult(true, "");
            return SaveJobOrder(order.Select(j => j == oldName ? newName : j));
        }

        // Edits legion.json in place, keeping the keys this app does not know about.
        private CommandResult UpdateConfig(string what, Action<JsonObject> edit)
        {
            try
            {
                var root = File.Exists(ConfigPath) ? JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject : null;
                root ??= new JsonObject();
                edit(root);
                File.WriteAllText(ConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return new CommandResult(true, "");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new CommandResult(false, $"{what}을 저장하지 못했습니다: {ex.Message}");
            }
        }

        /// <param name="windows">true = a native Windows PowerShell job (e.g. Unity), false = a WSL job.</param>
        /// <param name="path">An existing clone to use: nothing is cloned, it is only pulled. Blank = clone into the jobs root.</param>
        /// <param name="claudeName">The Claude session name; blank = the job name.</param>
        /// <param name="onLine">Receives the add's output (steps, clone progress) line by line while it runs.</param>
        public Task<CommandResult> AddJobAsync(string job, string? branch, string? repo, bool windows = false, string? path = null,
            string? claudeName = null, Action<string>? onLine = null)
        {
            var args = new List<string> { "add", job.Trim() };
            if (!string.IsNullOrWhiteSpace(branch)) args.AddRange(new[] { "-Branch", branch.Trim() });
            if (!string.IsNullOrWhiteSpace(claudeName)) args.AddRange(new[] { "-ClaudeName", claudeName.Trim() });
            if (!string.IsNullOrWhiteSpace(path)) args.AddRange(new[] { "-Path", path.Trim() });
            else if (!string.IsNullOrWhiteSpace(repo)) args.AddRange(new[] { "-Repo", repo.Trim() });
            if (windows) args.AddRange(new[] { "-Target", "windows" });
            return RunAsync(NetworkTimeout, args.ToArray(), onLine);
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

        private static readonly Regex WslProgram = new("^[A-Za-z0-9_./~+-]+$", RegexOptions.Compiled);

        /// <summary>
        /// Where the WSL login shell finds the program that starts <paramref name="claudeCmd"/> (`command -v`),
        /// or null if it is not found. A /mnt/... result is the Windows claude reached through WSL's Windows PATH.
        /// </summary>
        public async Task<string?> FindWslProgramAsync(string? distro, string claudeCmd)
        {
            var program = claudeCmd.Trim().Split(' ', 2)[0];
            if (!WslProgram.IsMatch(program)) return null;
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrWhiteSpace(distro)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro.Trim()); }
            foreach (var a in new[] { "--", "bash", "-lc", $"command -v {program}" }) psi.ArgumentList.Add(a);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Process? proc = null;
            try
            {
                proc = Process.Start(psi)!;
                var text = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                var path = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                return proc.ExitCode == 0 && !string.IsNullOrEmpty(path) ? path : null;
            }
            catch (Exception)
            {
                try { proc?.Kill(); } catch { /* already exited */ }
                return null;
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
        /// <paramref name="clearClaudeName"/> drops the job's own Claude name so it runs under its job name again.
        /// </summary>
        public Task<CommandResult> EditJobAsync(string job, string? newName, string? branch, string? repo, string? newPath,
            string? claudeName = null, bool clearClaudeName = false)
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
            Add("-ClaudeName", claudeName);
            if (clearClaudeName && string.IsNullOrWhiteSpace(claudeName)) args.Add("-ClearClaudeName");
            return RunAsync(MoveTimeout, args.ToArray());
        }

        /// <summary>
        /// The name the job's claude session runs under (claude-names.json, written by legion.ps1), or the job name.
        /// </summary>
        public string GetClaudeName(string job)
        {
            try
            {
                var file = Path.Combine(DataDir, "claude-names.json");
                if (!File.Exists(file)) return job;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(p.Name, job, StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        return p.Value.GetString()!;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
            return job;
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

        /// <summary>Removes a job without checking for uncommitted or unpushed work (the UI asks for confirmation first).</summary>
        public Task<CommandResult> RemoveAsync(string job) => RunAsync(DefaultTimeout, "remove", job);

        // These open a terminal / editor window that inherits the environment: git there must still be able to ask.
        private static readonly HashSet<string> InteractiveCommands = new(StringComparer.OrdinalIgnoreCase) { "code", "shell", "start" };

        /// <param name="onLine">Receives each output line (stdout and stderr) as it is printed.</param>
        private async Task<CommandResult> RunAsync(TimeSpan timeout, string[] args, Action<string>? onLine = null)
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
            if (args.Length == 0 || !InteractiveCommands.Contains(args[0]))
                GitAccountService.DisableGitPrompts(psi);

            var stdout = new List<string>();
            var log = new List<string>(); // both streams in the order they arrived
            void Receive(bool isStdout, string? line)
            {
                if (line is null) return;
                line = line.TrimEnd(); // git pads progress lines with spaces
                lock (log)
                {
                    if (isStdout) stdout.Add(line);
                    AppendOutputLine(log, line);
                }
                onLine?.Invoke(line);
            }

            using var cts = new CancellationTokenSource(timeout);
            Process? proc = null;
            try
            {
                proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => Receive(true, e.Data);
                proc.ErrorDataReceived += (_, e) => Receive(false, e.Data);
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(cts.Token);

                string Text(List<string> lines) { lock (log) return string.Join("\n", lines).Trim(); }
                if (proc.ExitCode != 0)
                    return new CommandResult(false, WithAuthHint(Text(log) is { Length: > 0 } all ? all : $"legion.ps1 exited with {proc.ExitCode}"));

                // -Json output is parsed, so it must be stdout alone; otherwise show both streams in the order they
                // came (git writes progress to stderr even on success)
                return new CommandResult(true, args.Contains("-Json") ? Text(stdout) : Text(log));
            }
            catch (OperationCanceledException)
            {
                try { proc?.Kill(entireProcessTree: true); } catch { /* already exited */ }
                string partial;
                lock (log) partial = string.Join("\n", log).Trim();
                // keep what was printed: it shows the step that never finished
                var message = $"legion.ps1 timed out after {timeout.TotalSeconds:0}s";
                return new CommandResult(false, WithAuthHint(partial.Length > 0 ? $"{partial}\n{message}" : message));
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

        private Task<CommandResult> RunAsync(TimeSpan timeout, params string[] args) => RunAsync(timeout, args, null);

        // "Receiving objects:  12% (69/570)", "remote: Counting objects:   3% (18/570)"
        private static readonly Regex ProgressLine = new(@"^(.*?):\s+\d+% \(", RegexOptions.Compiled);

        /// <summary>
        /// Adds an output line, replacing the previous one when both are progress updates of the same step
        /// ("Receiving objects:  12% ..."), so a clone leaves one line per step instead of hundreds.
        /// </summary>
        public static void AppendOutputLine(List<string> lines, string line)
        {
            if (lines.Count > 0 && ProgressLine.Match(line) is { Success: true } m
                && ProgressLine.Match(lines[^1]) is { Success: true } prev && prev.Groups[1].Value == m.Groups[1].Value)
                lines[^1] = line;
            else
                lines.Add(line);
        }

        // git gives up instead of prompting (GIT_TERMINAL_PROMPT=0): say so in plain words (the Jobs page then asks for the login)
        private static string WithAuthHint(string output) =>
            GitAccountService.LooksLikeAuthFailure(output)
                ? output + "\n\n→ git 로그인에 실패했습니다 (저장된 계정이 없거나 토큰이 만료됨)."
                : output;
    }
}
