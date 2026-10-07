using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.Extensions.Logging;

namespace AgentLegion.Services.Wsl;

/// <summary>
/// Reads a WSL distribution's files through its \\wsl.localhost share (plain System.IO, no wsl.exe per call)
/// and turns .md / .puml / .html files into previews.
/// </summary>
public sealed class WslFileService
{
    public const long MaxPreviewBytes = 2 * 1024 * 1024;
    private const int SniffBytes = 8 * 1024;
    private static readonly TimeSpan PlantUmlTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex DistroName = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    private static readonly HashSet<string> MarkdownExt = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown" };
    private static readonly HashSet<string> PlantUmlExt = new(StringComparer.OrdinalIgnoreCase) { ".puml", ".plantuml", ".pu", ".iuml", ".wsd" };
    private static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    // dot folders worth seeing even with hidden files off (agent settings, skills, plans)
    private static readonly HashSet<string> AlwaysShown = new(StringComparer.Ordinal) { ".claude" };

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly ILogger<WslFileService> _logger;
    private readonly SemaphoreSlim _jarLock = new(1, 1);
    private readonly Dictionary<string, string> _plantUmlJars = new(StringComparer.OrdinalIgnoreCase);

    public WslFileService(ILogger<WslFileService> logger) => _logger = logger;

    public static bool HasRichPreview(string ext) =>
        MarkdownExt.Contains(ext) || PlantUmlExt.Contains(ext) || HtmlExt.Contains(ext);

    public static bool IsValidDistro(string? distro) => distro is not null && DistroName.IsMatch(distro);

    public static string ToUncPath(string distro, string linuxPath) =>
        $@"\\wsl.localhost\{distro}" + linuxPath.Replace('/', '\\');

    /// <summary>"/a/b/../c/" -> "/a/c"; relative paths are taken from <paramref name="home"/>, "~" is the home folder.</summary>
    public static string NormalizePath(string? input, string home)
    {
        var p = (input ?? "").Trim().Replace('\\', '/');
        if (p.Length == 0 || p == "~") p = home;
        else if (p.StartsWith("~/")) p = home.TrimEnd('/') + p[1..];
        else if (!p.StartsWith('/')) p = home.TrimEnd('/') + "/" + p;

        var parts = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }

    public static string Combine(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    public Task<List<FileNode>> ListAsync(string distro, string linuxPath, bool showHidden, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var dir = new DirectoryInfo(ToUncPath(distro, linuxPath));
            var items = new List<FileNode>();
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (!showHidden && info.Name.StartsWith('.') && !AlwaysShown.Contains(info.Name)) continue;
                var isDir = info is DirectoryInfo;
                items.Add(new FileNode
                {
                    Kind = isDir ? FileNodeKind.Directory : FileNodeKind.File,
                    Name = info.Name,
                    LinuxPath = Combine(linuxPath, info.Name),
                    Size = info is FileInfo f ? SafeLength(f) : 0,
                    Modified = SafeModified(info),
                });
            }
            return items
                .OrderBy(n => n.Kind == FileNodeKind.Directory ? 0 : 1)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    public async Task<FilePreview> PreviewAsync(string distro, string linuxPath, CancellationToken ct = default)
    {
        var info = new FileInfo(ToUncPath(distro, linuxPath));
        var size = info.Length;
        var modified = info.LastWriteTime;
        var ext = info.Extension;

        if (size > MaxPreviewBytes)
            return new FilePreview(linuxPath, PreviewKind.TooLarge, size, modified, null, null);

        var bytes = await File.ReadAllBytesAsync(info.FullName, ct).ConfigureAwait(false);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, SniffBytes)) >= 0)
            return new FilePreview(linuxPath, PreviewKind.Binary, size, modified, null, null);

        var text = DecodeUtf8(bytes);
        if (MarkdownExt.Contains(ext))
            return new FilePreview(linuxPath, PreviewKind.Markdown, size, modified, RenderMarkdown(text, info.Name), text);
        if (HtmlExt.Contains(ext))
            return new FilePreview(linuxPath, PreviewKind.Html, size, modified, text, text);
        if (PlantUmlExt.Contains(ext))
        {
            var (svg, notice) = await RenderPlantUmlAsync(distro, linuxPath, text, ct).ConfigureAwait(false);
            return new FilePreview(linuxPath, PreviewKind.PlantUml, size, modified, svg, text, notice);
        }
        return new FilePreview(linuxPath, PreviewKind.Text, size, modified, null, text);
    }

    // The result is shown in a sandboxed iframe, so raw HTML inside the markdown cannot reach the app.
    private static string RenderMarkdown(string markdown, string title)
    {
        var body = Markdown.ToHtml(markdown, Pipeline);
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title><base target="_blank">
            <style>
              body { font-family: -apple-system, "Segoe UI", "Malgun Gothic", sans-serif; line-height: 1.6; color: #212529;
                     margin: 0; padding: 1rem 1.5rem; word-wrap: break-word; }
              h1, h2 { border-bottom: 1px solid #dee2e6; padding-bottom: .3em; }
              code { font-family: Consolas, "D2Coding", monospace; background: #f1f3f5; padding: .1em .3em; border-radius: 3px; font-size: .9em; }
              pre { background: #f6f8fa; padding: .8rem 1rem; border-radius: 6px; overflow: auto; }
              pre code { background: none; padding: 0; }
              table { border-collapse: collapse; margin: .5rem 0; }
              th, td { border: 1px solid #dee2e6; padding: .35rem .7rem; }
              th { background: #f8f9fa; }
              blockquote { margin: 0; padding: 0 1rem; color: #6c757d; border-left: .25rem solid #dee2e6; }
              img { max-width: 100%; }
              a { color: #0d6efd; }
            </style></head>
            <body>{{body}}</body></html>
            """;
    }

    /// <summary>Renders with the PlantUML jar inside the distro. Returns (svg, null) or (null, why not).</summary>
    private async Task<(string? Svg, string? Notice)> RenderPlantUmlAsync(string distro, string linuxPath, string source, CancellationToken ct)
    {
        var jar = await FindPlantUmlJarAsync(distro, ct).ConfigureAwait(false);
        if (jar is null)
            return (null, "WSL에서 plantuml.jar를 찾지 못해 소스를 표시합니다. (~/.vscode-server/extensions/jebbs.plantuml-*/, /usr/share/plantuml/, /opt/plantuml/)");

        var dir = linuxPath[..Math.Max(1, linuxPath.LastIndexOf('/'))];
        var psi = WslPsi(distro, KoreanFontSetup + "cd \"$PUML_DIR\" 2>/dev/null; exec java -Djava.awt.headless=true -jar \"$PUML_JAR\" -tsvg -pipe -charset UTF-8");
        psi.RedirectStandardInput = true;
        // Paths travel as environment variables so spaces and quotes survive the wsl.exe command line.
        psi.Environment["PUML_DIR"] = dir;
        psi.Environment["PUML_JAR"] = jar;
        psi.Environment["WSLENV"] = "PUML_DIR/u:PUML_JAR/u";

        var (ok, stdout, stderr) = await RunAsync(psi, source, ct).ConfigureAwait(false);
        // A syntax error exits non-zero but still prints an SVG that shows the error.
        var start = stdout.IndexOf("<svg", StringComparison.Ordinal);
        if (start >= 0) return (stdout[start..], null);
        return (null, ok ? "PlantUML이 SVG를 만들지 못했습니다." : $"PlantUML 실행 실패: {Trim(stderr)}");
    }

    // Distros often ship without a Hangul font, so Java measures Korean labels too narrow and they overlap their boxes.
    // Windows' Malgun Gothic is offered to this process only, through a fontconfig file under /tmp.
    private const string KoreanFontSetup =
        "d=/tmp/agentlegion-fonts; w=/mnt/c/Windows/Fonts; " +
        "if [ ! -f \"$d/fonts.conf\" ] && [ -f \"$w/malgun.ttf\" ]; then " +
        "mkdir -p \"$d\" && ln -sf \"$w/malgun.ttf\" \"$w/malgunbd.ttf\" \"$d/\" && " +
        "printf '<?xml version=\"1.0\"?><!DOCTYPE fontconfig SYSTEM \"fonts.dtd\"><fontconfig>" +
        "<include ignore_missing=\"yes\">/etc/fonts/fonts.conf</include><dir>%s</dir><cachedir>%s/cache</cachedir></fontconfig>' " +
        "\"$d\" \"$d\" > \"$d/fonts.conf\"; fi; " +
        "[ -f \"$d/fonts.conf\" ] && export FONTCONFIG_FILE=\"$d/fonts.conf\"; ";

    private async Task<string?> FindPlantUmlJarAsync(string distro, CancellationToken ct)
    {
        await _jarLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_plantUmlJars.TryGetValue(distro, out var cached)) return cached;
            var psi = WslPsi(distro,
                "ls -1d ~/.vscode-server/extensions/jebbs.plantuml-*/plantuml.jar /usr/share/plantuml/plantuml.jar /opt/plantuml/plantuml.jar 2>/dev/null | sort -V | tail -n 1");
            var (_, stdout, _) = await RunAsync(psi, null, ct).ConfigureAwait(false);
            var jar = stdout.Trim();
            if (jar.Length == 0) return null; // not cached: installing it later works without a restart
            _plantUmlJars[distro] = jar;
            return jar;
        }
        finally
        {
            _jarLock.Release();
        }
    }

    private static ProcessStartInfo WslPsi(string distro, string script)
    {
        var psi = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-d", distro, "-e", "sh", "-c", script }) psi.ArgumentList.Add(a);
        return psi;
    }

    private async Task<(bool Ok, string Stdout, string Stderr)> RunAsync(ProcessStartInfo psi, string? stdin, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PlantUmlTimeout);
        Process? proc = null;
        try
        {
            if (stdin is not null) psi.StandardInputEncoding = new UTF8Encoding(false);
            proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            if (stdin is not null)
            {
                await proc.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token).ConfigureAwait(false);
                proc.StandardInput.Close();
            }
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (proc.ExitCode == 0, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc?.Kill(true); } catch { /* already exited */ }
            return (false, "", "시간 초과");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "wsl.exe call failed");
            try { proc?.Kill(true); } catch { /* already exited */ }
            return (false, "", ex.Message);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; } catch { return 0; } // dangling symlink
    }

    private static DateTime SafeModified(FileSystemInfo i)
    {
        try { return i.LastWriteTime; } catch { return default; }
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 300 ? s : s[..300] + "...";
    }
}
