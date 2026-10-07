using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentLegion.Services.Wsl;

/// <summary>
/// The output of one log command, kept in chunks of lines so a live render only re-sends the chunk that grew
/// (a tail -f of thousands of lines would otherwise push the whole text on every update).
/// </summary>
public sealed class LogOutput
{
    public const int MaxLines = 5000;
    private const int ChunkLines = 200;
    private const int MaxLineChars = 4000;

    private readonly List<Chunk> _chunks = new();
    private readonly object _gate = new();
    private int _nextId;

    public int Lines { get; private set; }

    /// <summary>Lines dropped from the front to stay under <see cref="MaxLines"/>.</summary>
    public int Dropped { get; private set; }

    public void Add(string line)
    {
        if (line.Length > MaxLineChars) line = line[..MaxLineChars] + " …";
        lock (_gate)
        {
            var last = _chunks.Count > 0 ? _chunks[^1] : null;
            if (last is null || last.Lines >= ChunkLines)
            {
                last = new Chunk(_nextId++);
                _chunks.Add(last);
            }
            last.Append(line);
            Lines++;
            while (Lines > MaxLines && _chunks.Count > 1)
            {
                Lines -= _chunks[0].Lines;
                Dropped += _chunks[0].Lines;
                _chunks.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<(int Id, string Text)> Snapshot()
    {
        lock (_gate)
        {
            return _chunks.Select(c => (c.Id, c.Text)).ToList();
        }
    }

    private sealed class Chunk
    {
        private readonly StringBuilder _sb = new();
        private string? _text;

        public Chunk(int id) => Id = id;

        public int Id { get; }
        public int Lines { get; private set; }
        public string Text => _text ??= _sb.ToString();

        public void Append(string line)
        {
            _sb.Append(line).Append('\n');
            Lines++;
            _text = null;
        }
    }
}

/// <summary>One command run against a log file (or its folder) and how it ended.</summary>
public sealed class LogRun
{
    public LogRun(string distro, string command, string script, string dir, string? file, bool cp949)
    {
        Distro = distro;
        Command = command;
        Script = script;
        Dir = dir;
        File = file;
        Cp949 = cp949;
    }

    /// <summary>What the user typed.</summary>
    public string Command { get; }

    /// <summary>What bash runs: the command with "$F" added after its first part.</summary>
    public string Script { get; }

    public string Distro { get; }
    public string Dir { get; }
    public string? File { get; }
    public bool Cp949 { get; }
    public LogOutput Output { get; } = new();
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime? EndedAt { get; private set; }
    public int? ExitCode { get; private set; }
    public bool Stopped { get; internal set; }
    public bool Running => EndedAt is null;
    public TimeSpan Elapsed => (EndedAt ?? DateTime.Now) - StartedAt;

    internal int? Pid { get; set; }
    internal Process? Process { get; set; }

    internal void Finish(int exitCode)
    {
        ExitCode = exitCode;
        EndedAt = DateTime.Now;
    }
}

/// <summary>
/// Runs shell commands (grep, sed, tail -f, head, awk, pipes ...) on a log file inside WSL and streams their output.
/// The file is read in its own encoding: for EUC-KR the command itself is converted to CP949 so a Korean grep pattern
/// matches the file's bytes, tools run under LC_ALL=C (bytes, no UTF-8 validation), and the output is decoded as CP949.
/// Nothing converts the file, so tail on a large log stays a seek. One command at a time per browser circuit.
/// </summary>
public sealed class LogCommandRunner : IDisposable
{
    private const string PidMarker = "@@AL_PID ";
    private const int MaxPendingBytes = 1024 * 1024;
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex FileRef = new(@"\$F(?![A-Za-z0-9_])|\$\{F\}", RegexOptions.Compiled);
    private static readonly Encoding Cp949 = Encoding.GetEncoding(949);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _disposed;

    public LogCommandRunner(ILogger logger) => _logger = logger;

    public LogRun? Current { get; private set; }

    /// <summary>New output lines, or a run started / ended. Raised from worker threads, often.</summary>
    public event Action? Output;

    /// <summary>
    /// Adds the file after the first command of a pipeline, so "grep -n ERROR | tail -50" reads the file:
    /// grep -n ERROR "$F" | tail -50. A command that names $F itself is left as it is.
    /// </summary>
    public static string WithFile(string command)
    {
        if (FileRef.IsMatch(command)) return command;
        var end = FirstCommandEnd(command);
        var rest = command[end..].TrimStart();
        return command[..end].TrimEnd() + " \"$F\"" + (rest.Length > 0 ? " " + rest : "");
    }

    // The first unquoted | ; or & outside $( ) — but not the & of a redirect like 2>&1.
    private static int FirstCommandEnd(string s)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '\\': i++; break;
                case '\'' or '"' or '`': quote = c; break;
                case '(': depth++; break;
                case ')': if (depth > 0) depth--; break;
                case '|' or ';' when depth == 0:
                    return i;
                case '&' when depth == 0 && (i == 0 || (s[i - 1] != '>' && s[i - 1] != '<')):
                    return i;
            }
        }
        return s.Length;
    }

    /// <param name="file">The selected log file; null runs the command in <paramref name="dir"/> as typed.</param>
    public async Task StartAsync(string distro, string dir, string? file, string command, bool cp949)
    {
        await _startLock.WaitAsync();
        try
        {
            if (_disposed) return;
            await StopAsync();
            var script = file is null ? command : WithFile(command);
            var run = new LogRun(distro, command, script, dir, file, cp949);
            Current = run;
            _ = Task.Run(() => ExecuteAsync(run));
            Output?.Invoke();
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Stops the running command: TERM to its process group inside WSL, then the wsl.exe process.</summary>
    public async Task StopAsync()
    {
        var run = Current;
        if (run is not { Running: true }) return;
        run.Stopped = true;
        Output?.Invoke();

        if (run.Pid is { } pid) await KillGroupAsync(run.Distro, pid);

        var proc = run.Process;
        if (proc is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
        catch (InvalidOperationException) { /* already disposed */ }
    }

    public void Clear()
    {
        if (Current is { Running: true }) return;
        Current = null;
        Output?.Invoke();
    }

    private async Task ExecuteAsync(LogRun run)
    {
        Process? proc = null;
        try
        {
            proc = Process.Start(Psi(run))!;
            run.Process = proc;
            var stdout = PumpAsync(run, proc.StandardOutput.BaseStream, run.Cp949 ? Cp949 : Utf8);
            var stderr = PumpAsync(run, proc.StandardError.BaseStream, Utf8); // wsl.exe's own errors
            await Task.WhenAll(stdout, stderr);
            await proc.WaitForExitAsync();
            run.Finish(proc.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log command {Command} failed", run.Command);
            run.Output.Add($"실행 실패: {ex.Message}");
            run.Finish(-1);
        }
        finally
        {
            run.Process = null;
            proc?.Dispose();
        }
        Output?.Invoke();
    }

    // Splits raw bytes on \n and decodes each line by itself: one bad byte in an EUC-KR line stays in that line.
    private async Task PumpAsync(LogRun run, Stream stream, Encoding encoding)
    {
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pending.Write(buffer, start, i - start);
                AddLine(run, pending, encoding);
                start = i + 1;
            }
            pending.Write(buffer, start, read - start);
            if (pending.Length > MaxPendingBytes) AddLine(run, pending, encoding); // a binary file without newlines
            Output?.Invoke();
        }
        if (pending.Length > 0) AddLine(run, pending, encoding);
    }

    private static void AddLine(LogRun run, MemoryStream pending, Encoding encoding)
    {
        var line = encoding.GetString(pending.GetBuffer(), 0, (int)pending.Length).TrimEnd('\r');
        pending.SetLength(0);
        if (line.StartsWith(PidMarker, StringComparison.Ordinal) && run.Pid is null)
        {
            if (int.TryParse(line.AsSpan(PidMarker.Length), out var pid)) run.Pid = pid;
            return;
        }
        run.Output.Add(Ansi.Replace(line, ""));
    }

    private static ProcessStartInfo Psi(LogRun run)
    {
        var psi = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-d", run.Distro, "-e", "bash", "-c", Script }) psi.ArgumentList.Add(a);
        // values travel as environment variables so quotes and spaces in the command need no escaping
        psi.Environment["AL_DIR"] = run.Dir;
        psi.Environment["AL_FILE"] = run.File ?? "";
        psi.Environment["AL_CMD"] = run.Script;
        psi.Environment["AL_ENC"] = run.Cp949 ? "cp949" : "utf8";
        psi.Environment["WSLENV"] = "AL_DIR/u:AL_FILE/u:AL_CMD/u:AL_ENC/u";
        return psi;
    }

    // bash, not sh: dash rejects "kill -TERM -- -pgid" and the group would keep running
    private async Task KillGroupAsync(string distro, int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-d", distro, "-e", "bash", "-c", $"kill -TERM -- -{pid} 2>/dev/null" }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping log command {Pid} failed", pid);
        }
    }

    // The command runs in its own session (setsid: pid = process group) so Stop ends a whole pipeline such as
    // tail -f | grep. grep and sed are made line buffered: as the last stage of a pipe they would otherwise hold
    // tail -f output back in 4 KB blocks.
    private const string Script = """
        cd "$AL_DIR" 2>/dev/null || { echo "folder not found: $AL_DIR"; exit 1; }
        export F="$AL_FILE"
        if [ "$AL_ENC" = cp949 ]; then
          export LC_ALL=C
          AL_CMD=$(printf '%s' "$AL_CMD" | iconv -f UTF-8 -t CP949 -c)
        else
          export LC_ALL=C.UTF-8
        fi
        export AL_CMD
        grep() { command grep --line-buffered "$@"; }
        egrep() { command grep -E --line-buffered "$@"; }
        fgrep() { command grep -F --line-buffered "$@"; }
        sed() { command sed -u "$@"; }
        awk() { stdbuf -oL awk "$@"; }
        cut() { stdbuf -oL cut "$@"; }
        uniq() { stdbuf -oL uniq "$@"; }
        tr() { stdbuf -oL tr "$@"; }
        export -f grep egrep fgrep sed awk cut uniq tr
        setsid bash -c 'eval "$AL_CMD"' </dev/null 2>&1 &
        pid=$!
        echo "@@AL_PID $pid"
        trap 'kill -TERM -- -$pid 2>/dev/null' TERM HUP INT
        wait $pid; rc=$?
        while kill -0 $pid 2>/dev/null; do wait $pid; rc=$?; done
        exit $rc
        """;

    public void Dispose()
    {
        _disposed = true;
        _ = StopAsync();
    }
}
