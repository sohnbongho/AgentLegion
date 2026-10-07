using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentLegion.Services.Wsl;

public enum LogEncoding
{
    Auto,
    EucKr,
    Utf8,
}

/// <summary>Per-circuit state of the Logs tab: the folder tree, the selected log, its encoding and the command run on it.</summary>
public sealed class LogBrowserState : FileTreeState
{
    public const string DefaultCommand = "tail -n 500";
    private const int MaxHistory = 30;
    private const int SniffBytes = 64 * 1024;

    private readonly LogCommandRunner _runner;
    private readonly LegionService _legion;

    public LogBrowserState(WslFileService files, WslInfoService wsl, LogBrowserMemory memory, LegionService legion,
        ILogger<LogBrowserState> logger)
        : base(files, wsl, memory, logger)
    {
        _legion = legion;
        _runner = new LogCommandRunner(logger);
    }

    protected override string LoadDefaultRoot() => _legion.LoadConfig()?.LogRoot ?? LegionConfig.DefaultLogRoot;

    public LogCommandRunner Runner => _runner;

    public LogEncoding Encoding { get; private set; } = LogEncoding.Auto;

    public bool Wrap { get; private set; }

    /// <summary>Commands run before, newest first.</summary>
    public List<string> History { get; private set; } = new();

    /// <summary>The text in the command box.</summary>
    public string Command { get; set; } = DefaultCommand;

    /// <summary>true: the command reads the selected file ("$F" is added); false: it runs as typed in the folder.</summary>
    public bool UseFile { get; set; } = true;

    /// <summary>Size and time of the selected file, read again after each command.</summary>
    public FileInfo? SelectedInfo { get; private set; }

    /// <summary>What "자동" found for the selected file: true = EUC-KR, false = UTF-8.</summary>
    public bool? DetectedCp949 { get; private set; }

    public string? DetectNote { get; private set; }

    protected override void OnRestore(FileBrowserSnapshot? saved)
    {
        if (Enum.TryParse<LogEncoding>(saved?.Encoding, out var enc)) Encoding = enc;
        History = saved?.History?.Take(MaxHistory).ToList() ?? new List<string>();
        Wrap = saved?.Wrap ?? false;
    }

    protected override FileBrowserSnapshot Snapshot() =>
        base.Snapshot() with { Encoding = Encoding.ToString(), History = History, Wrap = Wrap };

    // A click shows the end of the log: these files run to tens of MB, far past what a preview can hold.
    protected override Task OnSelectedAsync(FileNode file)
    {
        Detect(file.LinuxPath);
        Command = DefaultCommand;
        UseFile = true;
        return RunAsync(remember: false);
    }

    /// <summary>Runs the command box on the selected file (or in the folder).</summary>
    public async Task RunAsync(bool remember = true)
    {
        if (Distro is null || Root is null) return;
        var command = Command.Trim();
        if (command.Length == 0) return;
        if (remember)
        {
            History.Remove(command);
            History.Insert(0, command);
            if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
            Remember();
        }

        var file = UseFile ? SelectedPath : null;
        var dir = file is null ? Root.LinuxPath : file[..Math.Max(1, file.LastIndexOf('/'))];
        if (file is not null && DetectedCp949 is null) Detect(file);
        await _runner.StartAsync(Distro, dir, file, command, IsCp949);
        RefreshInfo();
        Raise();
    }

    public Task StopAsync() => _runner.StopAsync();

    public void ClearOutput() => _runner.Clear();

    /// <summary>What "자동" resolves to for the selected file.</summary>
    public bool IsCp949OnAuto => DetectedCp949 ?? true;

    /// <summary>The encoding the next command uses.</summary>
    public bool IsCp949 => Encoding switch
    {
        LogEncoding.EucKr => true,
        LogEncoding.Utf8 => false,
        _ => IsCp949OnAuto,
    };

    /// <summary>Changing the encoding runs the last command again, so its output is read the new way.</summary>
    public async Task SetEncodingAsync(LogEncoding encoding)
    {
        Encoding = encoding;
        Remember();
        Raise();
        if (_runner.Current is { } last)
        {
            Command = last.Command;
            UseFile = last.File is not null;
            await RunAsync(remember: false);
        }
    }

    public void SetWrap(bool wrap)
    {
        Wrap = wrap;
        Remember();
        Raise();
    }

    public void RefreshInfo()
    {
        if (Distro is null || SelectedPath is null)
        {
            SelectedInfo = null;
            return;
        }
        try
        {
            var info = new FileInfo(WslFileService.ToUncPath(Distro, SelectedPath));
            SelectedInfo = info.Exists ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SelectedInfo = null;
        }
    }

    // Looks at the start and the end of the file: non-ASCII text that is valid UTF-8 is UTF-8, anything else EUC-KR.
    // A file with only ASCII so far is taken as EUC-KR, the usual encoding of these logs, so a Korean grep still matches.
    private void Detect(string linuxPath)
    {
        DetectedCp949 = null;
        DetectNote = null;
        try
        {
            using var fs = new FileStream(WslFileService.ToUncPath(Distro!, linuxPath), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var samples = new List<byte[]> { Read(fs, 0, SniffBytes, fromLineEnd: false) };
            if (fs.Length > SniffBytes * 2) samples.Add(Read(fs, fs.Length - SniffBytes, SniffBytes, fromLineEnd: true));

            var nonAscii = samples.Where(s => s.Any(b => b >= 0x80)).ToList();
            if (nonAscii.Count == 0)
            {
                DetectedCp949 = true;
                DetectNote = "ASCII만 있어 EUC-KR로 봅니다";
            }
            else if (nonAscii.All(IsUtf8))
            {
                DetectedCp949 = false;
                DetectNote = "UTF-8로 감지";
            }
            else
            {
                DetectedCp949 = true;
                DetectNote = "EUC-KR로 감지";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectNote = "인코딩을 확인하지 못해 EUC-KR로 봅니다";
        }
    }

    // Whole lines only: a sample cut in the middle of a character would not decode.
    private static byte[] Read(FileStream fs, long offset, int count, bool fromLineEnd)
    {
        fs.Position = offset;
        var buf = new byte[count];
        var n = fs.Read(buf, 0, count);
        var span = buf.AsSpan(0, n);
        if (fromLineEnd)
        {
            var first = span.IndexOf((byte)'\n');
            span = first >= 0 ? span[(first + 1)..] : span;
        }
        if (n == count)
        {
            var last = span.LastIndexOf((byte)'\n');
            if (last >= 0) span = span[..(last + 1)];
        }
        return span.ToArray();
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public override void Dispose() => _runner.Dispose();
}
