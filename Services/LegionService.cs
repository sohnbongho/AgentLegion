using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentLegion.Services
{
    public record JobInfo(string Job, string Branch, int Changes);

    public record JobsResult(IReadOnlyList<JobInfo> Jobs, string? Error);

    /// <summary>Thin wrapper that runs legion.ps1 and parses its -Json output.</summary>
    public class LegionService
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly string _scriptPath;

        public LegionService(IWebHostEnvironment env, IConfiguration config)
        {
            _scriptPath = config["Legion:ScriptPath"] ?? Path.Combine(env.ContentRootPath, "legion.ps1");
        }

        public async Task<JobsResult> GetJobsAsync()
        {
            if (!File.Exists(_scriptPath))
                return new JobsResult(Array.Empty<JobInfo>(), $"legion.ps1 not found: {_scriptPath}");

            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _scriptPath, "status", "-Json" })
                psi.ArgumentList.Add(a);

            try
            {
                using var proc = Process.Start(psi)!;
                using var cts = new CancellationTokenSource(Timeout);
                var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);

                if (proc.ExitCode != 0)
                    return new JobsResult(Array.Empty<JobInfo>(), FirstLine(await stderr) ?? $"legion.ps1 exited with {proc.ExitCode}");

                var jobs = JsonSerializer.Deserialize<List<JobInfo>>(await stdout, JsonOptions) ?? new();
                return new JobsResult(jobs, null);
            }
            catch (OperationCanceledException)
            {
                return new JobsResult(Array.Empty<JobInfo>(), $"legion.ps1 timed out after {Timeout.TotalSeconds:0}s");
            }
            catch (Exception ex)
            {
                return new JobsResult(Array.Empty<JobInfo>(), ex.Message);
            }
        }

        private static string? FirstLine(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }
}
