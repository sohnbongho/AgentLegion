using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentLegion.Services
{
    /// <summary>
    /// The HTTPS login git uses for a host (clone / pull / push), asked for when a command fails to log in. The token goes
    /// straight into git's own credential helper through stdin - it is never written to legion.json or put on a command line.
    /// </summary>
    public class GitAccountService
    {
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

        private readonly LegionService _legion;

        public GitAccountService(LegionService legion) => _legion = legion;

        /// <summary>
        /// Makes git fail at once instead of waiting for a username / password nobody can type (the app runs it hidden):
        /// terminal prompts off, also inside WSL, and no Git Credential Manager dialog on Windows.
        /// </summary>
        public static void DisableGitPrompts(ProcessStartInfo psi)
        {
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
            // Windows environment variables reach WSL only when WSLENV lists them
            psi.Environment.TryGetValue("WSLENV", out var wslenv);
            var names = (wslenv ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!names.Any(n => n.Split('/')[0] == "GIT_TERMINAL_PROMPT")) names.Add("GIT_TERMINAL_PROMPT");
            psi.Environment["WSLENV"] = string.Join(':', names);
        }

        private static readonly Regex AuthFailure = new(
            @"terminal prompts disabled|could not read (Username|Password)|Authentication failed|HTTP Basic: Access denied|" +
            @"Invalid username or password|returned error: 40[13]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool LooksLikeAuthFailure(string output) => AuthFailure.IsMatch(output);

        // git names the remote it could not log in to: "could not read Username for 'https://host'",
        // "Authentication failed for 'https://host/group/repo.git/'"
        private static readonly Regex FailedRemote = new(@"for '(https?://[^'\s]+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The HTTP(S) remote a failed command could not log in to, or null when it was not a login failure.</summary>
        public static Uri? FailedLoginRemote(string output) =>
            LooksLikeAuthFailure(output) && FailedRemote.Match(output) is { Success: true } m
            && Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var u) ? u : null;

        // git credential's input: the host only (credential.useHttpPath is off by default), ending with a blank line
        private static string CredentialInput(Uri remote, string? user = null, string? password = null)
        {
            var sb = new StringBuilder($"protocol={remote.Scheme}\nhost={remote.Authority}\n");
            if (user is not null) sb.Append($"username={user}\n");
            if (password is not null) sb.Append($"password={password}\n");
            return sb.Append('\n').ToString();
        }

        /// <summary>The user name git already has for the host (WSL first, then Windows), to prefill the login form.</summary>
        public async Task<string?> GetStoredUserAsync(Uri remote) =>
            await StoredUserAsync(true, remote) ?? await StoredUserAsync(false, remote);

        private async Task<string?> StoredUserAsync(bool wsl, Uri remote)
        {
            var r = await RunGitAsync(wsl, new[] { "credential", "fill" }, CredentialInput(remote), CallTimeout);
            if (r.Exit != 0) return null;
            // only the user name is read; the password line is dropped here
            var user = r.Out.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("username="))?["username=".Length..];
            return string.IsNullOrEmpty(user) ? null : user;
        }

        /// <summary>Replaces the stored login for the repo's host in WSL and, when Windows has git, in Windows too.</summary>
        public async Task<CommandResult> SaveAsync(Uri remote, string user, string token)
        {
            user = user.Trim();
            if (user.Length == 0 || token.Length == 0)
                return new CommandResult(false, "사용자 이름과 토큰(비밀번호)을 모두 입력하세요.");
            if (user.Any(char.IsControl) || token.Any(char.IsControl))
                return new CommandResult(false, "사용자 이름이나 토큰에 줄바꿈 같은 제어 문자를 쓸 수 없습니다.");

            var lines = new List<string>();
            var ok = true;
            foreach (var wsl in new[] { true, false })
            {
                var label = wsl ? "WSL" : "Windows";
                var helper = await RunGitAsync(wsl, new[] { "config", "--get", "credential.helper" }, null, CallTimeout);
                if (helper.Exit == NotInstalled)
                {
                    lines.Add($"{label}: git이 없어 건너뛰었습니다.");
                    continue;
                }
                if (helper.Exit != 0 || helper.Out.Trim().Length == 0)
                {
                    if (!wsl)
                    {
                        lines.Add($"{label}: credential helper가 설정되어 있지 않아 저장하지 않았습니다 (Git for Windows의 Git Credential Manager 권장).");
                        continue;
                    }
                    // without a helper `approve` silently stores nothing
                    var set = await RunGitAsync(true, new[] { "config", "--global", "credential.helper", "store" }, null, CallTimeout);
                    if (set.Exit != 0)
                    {
                        lines.Add($"{label}: credential helper를 설정하지 못했습니다: {FirstLine(set.Err)}");
                        ok = false;
                        continue;
                    }
                    lines.Add($"{label}: credential helper가 없어 store(~/.git-credentials)로 설정했습니다.");
                }
                // reject by host drops every account stored for it, so a changed user name leaves no stale entry behind
                await RunGitAsync(wsl, new[] { "credential", "reject" }, CredentialInput(remote), CallTimeout);
                var approve = await RunGitAsync(wsl, new[] { "credential", "approve" }, CredentialInput(remote, user, token), CallTimeout);
                if (approve.Exit != 0)
                {
                    lines.Add($"{label}: 저장하지 못했습니다: {FirstLine(approve.Err)}");
                    ok = false;
                    continue;
                }
                var stored = await StoredUserAsync(wsl, remote);
                if (stored == user) lines.Add($"{label}: {user} 계정을 저장했습니다.");
                else
                {
                    lines.Add($"{label}: 저장 명령은 성공했지만 다시 읽히지 않습니다 (credential helper: {helper.Out.Trim()}).");
                    ok = false;
                }
            }
            return new CommandResult(ok, string.Join("\n", lines));
        }

        private const int NotInstalled = -2;
        private const int TimedOut = -1;

        private static string FirstLine(string text) =>
            text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(no output)";

        /// <summary>Runs git in WSL (the configured distro) or Windows with prompts disabled; the input goes through stdin.</summary>
        private async Task<(int Exit, string Out, string Err)> RunGitAsync(bool wsl, string[] gitArgs, string? input, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo(wsl ? "wsl.exe" : "git")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (wsl)
            {
                if (_legion.LoadConfig()?.Distro is { Length: > 0 } distro)
                {
                    psi.ArgumentList.Add("-d");
                    psi.ArgumentList.Add(distro);
                }
                // -e: run git directly, no shell to re-parse a URL
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("git");
            }
            foreach (var a in gitArgs) psi.ArgumentList.Add(a);
            DisableGitPrompts(psi);

            using var cts = new CancellationTokenSource(timeout);
            Process? proc = null;
            try
            {
                proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
                if (input is not null) await proc.StandardInput.WriteAsync(input);
                proc.StandardInput.Close();
                await proc.WaitForExitAsync(cts.Token);
                return (proc.ExitCode, await stdout, await stderr);
            }
            catch (Win32Exception)
            {
                return (NotInstalled, "", "");
            }
            catch (OperationCanceledException)
            {
                try { proc?.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return (TimedOut, "", $"timed out after {timeout.TotalSeconds:0}s");
            }
            catch (IOException ex)
            {
                return (TimedOut, "", ex.Message);
            }
            finally
            {
                proc?.Dispose();
            }
        }
    }
}
