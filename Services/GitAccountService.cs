using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentLegion.Services
{
    /// <summary>The account git has stored for a host in each environment; null = none (or it could not be read).</summary>
    public record GitAccountState(string? WslUser, string? WindowsUser, string? WindowsNote);

    /// <summary>
    /// The HTTPS login git uses for the default repo's host (clone / pull / push of every job). The token goes straight
    /// into git's own credential helper through stdin - it is never written to legion.json or put on a command line.
    /// </summary>
    public class GitAccountService
    {
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(45);

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

        /// <summary>The repo URL when it is reached over HTTP(S); null for ssh / scp-style / local paths (no login needed here).</summary>
        public static Uri? HttpsRemote(string? repo) =>
            Uri.TryCreate(repo?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u : null;

        // git credential's input: the host only (credential.useHttpPath is off by default), ending with a blank line
        private static string CredentialInput(Uri remote, string? user = null, string? password = null)
        {
            var sb = new StringBuilder($"protocol={remote.Scheme}\nhost={remote.Authority}\n");
            if (user is not null) sb.Append($"username={user}\n");
            if (password is not null) sb.Append($"password={password}\n");
            return sb.Append('\n').ToString();
        }

        public async Task<GitAccountState> GetStateAsync(Uri remote)
        {
            var wsl = StoredUserAsync(true, remote);
            var win = StoredUserAsync(false, remote);
            var (w, n) = (await wsl, await win);
            return new GitAccountState(w.User, n.User, n.Note);
        }

        private async Task<(string? User, string? Note)> StoredUserAsync(bool wsl, Uri remote)
        {
            var r = await RunGitAsync(wsl, new[] { "credential", "fill" }, CredentialInput(remote), CallTimeout);
            if (r.Exit == NotInstalled) return (null, "Windows에 git이 없습니다");
            if (r.Exit != 0) return (null, null);
            // only the user name is read; the password line is dropped here
            var user = r.Out.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("username="))?["username=".Length..];
            return (string.IsNullOrEmpty(user) ? null : user, null);
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
                if (stored.User == user) lines.Add($"{label}: {user} 계정을 저장했습니다.");
                else
                {
                    lines.Add($"{label}: 저장 명령은 성공했지만 다시 읽히지 않습니다 (credential helper: {helper.Out.Trim()}).");
                    ok = false;
                }
            }
            return new CommandResult(ok, string.Join("\n", lines));
        }

        /// <summary>Forgets the stored login for the repo's host in both environments.</summary>
        public async Task<CommandResult> DeleteAsync(Uri remote)
        {
            var lines = new List<string>();
            foreach (var wsl in new[] { true, false })
            {
                var r = await RunGitAsync(wsl, new[] { "credential", "reject" }, CredentialInput(remote), CallTimeout);
                if (r.Exit == NotInstalled) continue;
                lines.Add($"{(wsl ? "WSL" : "Windows")}: {(r.Exit == 0 ? "삭제했습니다." : "삭제하지 못했습니다: " + FirstLine(r.Err))}");
            }
            return new CommandResult(true, string.Join("\n", lines));
        }

        /// <summary>Contacts the repo with the stored login (git ls-remote), from WSL and from Windows when it has git.</summary>
        public async Task<CommandResult> TestAsync(Uri remote)
        {
            var lines = new List<string>();
            var ok = true;
            foreach (var wsl in new[] { true, false })
            {
                var r = await RunGitAsync(wsl, new[] { "ls-remote", remote.OriginalString, "HEAD" }, null, TestTimeout);
                if (r.Exit == NotInstalled) continue;
                var label = wsl ? "WSL" : "Windows";
                if (r.Exit == 0) lines.Add($"{label}: 접속했습니다.");
                else
                {
                    ok = false;
                    var err = string.Join(" / ", r.Err.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));
                    lines.Add($"{label}: 실패 - {(LooksLikeAuthFailure(r.Err) ? "로그인 실패 (계정이 없거나 토큰이 만료됨). " : "")}{err}");
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
