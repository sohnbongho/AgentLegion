using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentLegion.Services
{
    public record WslInfo(
        string Distro, bool Running, int Version, bool IsDefault,
        string? WslVersion, string? Kernel, string? Os, string? User, string? Host)
    {
        public string Summary => $"{Distro} · WSL{Version}";
    }

    /// <summary>
    /// Describes the WSL distribution the app is configured to use. Listing never starts a distro:
    /// details that need a shell (kernel, OS, user) are only queried while the distro is already running.
    /// </summary>
    public class WslInfoService
    {
        private static readonly Regex ListLine = new(@"^\s*(\*)?\s*(\S+)\s+(\S+)\s+(\d)\s*$", RegexOptions.Compiled);
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

        private readonly LegionService _legion;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Dictionary<string, (string? Kernel, string? Os, string? User, string? Host)> _details = new();
        private string? _wslVersion;
        private bool _wslVersionLoaded;

        public WslInfoService(LegionService legion) => _legion = legion;

        public WslInfo? Current { get; private set; }

        public event Action? Changed;

        public async Task RefreshAsync()
        {
            await _lock.WaitAsync();
            WslInfo? next;
            try { next = await QueryAsync(); }
            finally { _lock.Release(); }

            if (next != Current)
            {
                Current = next;
                Changed?.Invoke();
            }
        }

        private async Task<WslInfo?> QueryAsync()
        {
            var list = await RunAsync(new[] { "-l", "-v" }, Encoding.Unicode);
            if (list is null) return null;

            var running = (await RunAsync(new[] { "-l", "--running", "-q" }, Encoding.Unicode) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Replace("\0", "").Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rows = new List<(string Name, bool IsDefault, int Version)>();
            foreach (var raw in list.Split('\n'))
            {
                var m = ListLine.Match(raw.Replace("\0", "").TrimEnd('\r'));
                if (m.Success) rows.Add((m.Groups[2].Value, m.Groups[1].Success, int.Parse(m.Groups[4].Value)));
            }

            var wanted = _legion.LoadConfig()?.Distro;
            var row = string.IsNullOrWhiteSpace(wanted)
                ? rows.FirstOrDefault(r => r.IsDefault)
                : rows.FirstOrDefault(r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (row.Name is null) return null;

            if (!_wslVersionLoaded)
            {
                _wslVersionLoaded = true;
                var v = await RunAsync(new[] { "--version" }, Encoding.Unicode);
                _wslVersion = FirstValue(v);
            }

            var isRunning = running.Contains(row.Name);
            (string? Kernel, string? Os, string? User, string? Host) d = default;
            if (isRunning && !_details.TryGetValue(row.Name, out d))
            {
                d = await QueryDetailsAsync(row.Name);
                if (d.Kernel is not null) _details[row.Name] = d;
            }

            return new WslInfo(row.Name, isRunning, row.Version, row.IsDefault, _wslVersion, d.Kernel, d.Os, d.User, d.Host);
        }

        // Only called for a distro that is already running, so this does not start anything.
        private async Task<(string?, string?, string?, string?)> QueryDetailsAsync(string distro)
        {
            if (!Regex.IsMatch(distro, "^[A-Za-z0-9_.-]+$")) return default;
            var text = await RunAsync(new[] { "-d", distro, "--", "sh", "-c", "whoami; uname -r; hostname; grep PRETTY_NAME /etc/os-release" }, Encoding.UTF8);
            if (text is null) return default;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? At(int i) => i < lines.Length ? lines[i] : null;
            var os = At(3)?.Split('=', 2).LastOrDefault()?.Trim('"');
            return (At(1), os, At(0), At(2));
        }

        // "WSL version: 2.1.5.0" (label is localized) -> "2.1.5.0"
        private static string? FirstValue(string? text)
        {
            var first = text?.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var i = first?.IndexOf(':') ?? -1;
            return i >= 0 ? first![(i + 1)..].Trim() : null;
        }

        private static async Task<string?> RunAsync(string[] args, Encoding encoding)
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var cts = new CancellationTokenSource(CallTimeout);
            Process? proc = null;
            try
            {
                proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
                _ = proc.StandardError.ReadToEndAsync(cts.Token); // drained so it can never block the process
                await proc.WaitForExitAsync(cts.Token);
                return proc.ExitCode == 0 ? await stdout : null;
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
    }
}
