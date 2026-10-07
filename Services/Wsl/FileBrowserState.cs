using Microsoft.Extensions.Logging;

namespace AgentLegion.Services.Wsl;

/// <summary>Per-circuit state of the WSL file browser: the root folder, expanded folders and the selected file.</summary>
public sealed class FileBrowserState : IDisposable
{
    private readonly WslFileService _files;
    private readonly WslInfoService _wsl;
    private readonly ILogger<FileBrowserState> _logger;
    private CancellationTokenSource _previewOp = new();

    public FileBrowserState(WslFileService files, WslInfoService wsl, ILogger<FileBrowserState> logger)
    {
        _files = files;
        _wsl = wsl;
        _logger = logger;
    }

    public string? Distro { get; private set; }

    public string Home { get; private set; } = "/";

    public FileNode? Root { get; private set; }

    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);

    public bool ShowHidden { get; set; }

    public string? SelectedPath { get; private set; }

    public FilePreview? Preview { get; private set; }

    public bool IsInitialized { get; private set; }

    public bool IsBusy { get; private set; }

    public bool PreviewLoading { get; private set; }

    public string? LastError { get; private set; }

    public string? PreviewError { get; private set; }

    /// <summary>The distro exists but is stopped; reading its share would start it.</summary>
    public bool DistroStopped { get; private set; }

    public event Action? Changed;

    /// <param name="startDistro">true when the user asked to open the files even though the distro is stopped.</param>
    public async Task InitializeAsync(bool startDistro = false)
    {
        if (IsInitialized || IsBusy) return;
        IsBusy = true;
        LastError = null;
        Raise();
        try
        {
            if (_wsl.Current is null) await _wsl.RefreshAsync();
            var info = _wsl.Current;
            if (info is null || !WslFileService.IsValidDistro(info.Distro))
            {
                LastError = "WSL 배포판을 찾지 못했습니다. Settings에서 배포판을 확인하세요.";
                return;
            }

            Distro = info.Distro;
            DistroStopped = !info.Running && !startDistro;
            if (DistroStopped) return;

            // the user is only known while the distro runs
            Home = info.User switch { null => "/", "root" => "/root", var u => $"/home/{u}" };
            IsInitialized = true;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
        await OpenRootAsync(Home);
    }

    public async Task OpenRootAsync(string? path)
    {
        if (Distro is null) return;
        var linuxPath = WslFileService.NormalizePath(path, Home);
        var name = linuxPath == "/" ? "/" : linuxPath[(linuxPath.LastIndexOf('/') + 1)..];
        var root = new FileNode { Kind = FileNodeKind.Directory, Name = name, LinuxPath = linuxPath };
        Root = root;
        Expanded.Clear();
        Expanded.Add(linuxPath);
        await LoadChildrenAsync(root);
    }

    public Task ReloadAsync() => Root is null ? Task.CompletedTask : ReloadKeepingExpandedAsync();

    private async Task ReloadKeepingExpandedAsync()
    {
        var expanded = Expanded.ToHashSet(StringComparer.Ordinal);
        var root = new FileNode { Kind = FileNodeKind.Directory, Name = Root!.Name, LinuxPath = Root.LinuxPath };
        Root = root;
        await ReloadTreeAsync(root, expanded);
    }

    private async Task ReloadTreeAsync(FileNode dir, HashSet<string> expanded)
    {
        await LoadChildrenAsync(dir);
        foreach (var child in dir.Children.Where(c => c.Kind == FileNodeKind.Directory && expanded.Contains(c.LinuxPath)))
        {
            await ReloadTreeAsync(child, expanded);
        }
    }

    public bool IsExpanded(FileNode node) => Expanded.Contains(node.LinuxPath);

    public async Task ToggleAsync(FileNode dir)
    {
        if (!Expanded.Add(dir.LinuxPath))
        {
            Expanded.Remove(dir.LinuxPath);
            Raise();
            return;
        }
        Raise();
        if (!dir.IsLoaded) await LoadChildrenAsync(dir);
    }

    private async Task LoadChildrenAsync(FileNode dir)
    {
        IsBusy = true;
        LastError = null;
        Raise();
        try
        {
            var children = await _files.ListAsync(Distro!, dir.LinuxPath, ShowHidden);
            dir.Children.Clear();
            dir.Children.AddRange(children);
            dir.Error = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // one unreadable folder (/root, /proc/...) only marks that folder
            dir.Children.Clear();
            dir.Error = ex is UnauthorizedAccessException ? "권한 없음" : ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing {Path} failed", dir.LinuxPath);
            dir.Error = ex.Message;
        }
        finally
        {
            dir.IsLoaded = true;
            IsBusy = false;
            Raise();
        }
    }

    public async Task SelectAsync(FileNode file)
    {
        if (file.Kind != FileNodeKind.File || Distro is null) return;
        try { _previewOp.Cancel(); } catch { /* ignore */ }
        _previewOp.Dispose();
        _previewOp = new CancellationTokenSource();
        var ct = _previewOp.Token;

        SelectedPath = file.LinuxPath;
        Preview = null;
        PreviewError = null;
        PreviewLoading = true;
        Raise();
        try
        {
            var preview = await _files.PreviewAsync(Distro, file.LinuxPath, ct);
            if (!ct.IsCancellationRequested) Preview = preview;
        }
        catch (OperationCanceledException) { /* a newer selection won */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview of {Path} failed", file.LinuxPath);
            if (!ct.IsCancellationRequested) PreviewError = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                PreviewLoading = false;
                Raise();
            }
        }
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        try { _previewOp.Cancel(); } catch { /* ignore */ }
        _previewOp.Dispose();
    }
}
