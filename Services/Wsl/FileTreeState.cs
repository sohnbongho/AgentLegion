using Microsoft.Extensions.Logging;

namespace AgentLegion.Services.Wsl;

/// <summary>
/// Per-circuit state of a WSL folder tree: the distro, the root folder, expanded folders and the selected file.
/// Files and Logs each derive their own state, so the two tabs keep separate folders and selections.
/// </summary>
public abstract class FileTreeState : IDisposable
{
    protected readonly WslFileService Files;
    protected readonly ILogger Logger;
    private readonly WslInfoService _wsl;
    private readonly FileBrowserMemory _memory;

    protected FileTreeState(WslFileService files, WslInfoService wsl, FileBrowserMemory memory, ILogger logger)
    {
        Files = files;
        _wsl = wsl;
        _memory = memory;
        Logger = logger;
    }

    public string? Distro { get; private set; }

    public string Home { get; private set; } = "/";

    public FileNode? Root { get; private set; }

    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);

    public bool ShowHidden { get; private set; }

    public string? SelectedPath { get; private set; }

    public bool IsInitialized { get; private set; }

    public bool IsBusy { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Why "탐색기에서 열기" failed for the selected file; cleared on the next selection.</summary>
    public string? ExplorerError { get; private set; }

    /// <summary>The distro exists but is stopped; reading its share would start it.</summary>
    public bool DistroStopped { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// The folder opened on the first visit (no saved folder, or it is gone) and by the home button, as configured
    /// (e.g. "~/wind/data_local/logs"). Read again on each visit, so a change in Settings is picked up.
    /// </summary>
    public string DefaultRoot { get; private set; } = "/";

    protected virtual string LoadDefaultRoot() => Home;

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
            DefaultRoot = LoadDefaultRoot();
            IsInitialized = true;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
        await RestoreAsync();
    }

    /// <summary>
    /// Called when the page is shown again in the same browser tab: if the default folder was changed in Settings
    /// meanwhile, opens the new one.
    /// </summary>
    public async Task FollowDefaultRootAsync()
    {
        if (!IsInitialized || IsBusy) return;
        var current = LoadDefaultRoot();
        if (current == DefaultRoot) return;
        DefaultRoot = current;
        await OpenDefaultRootAsync();
    }

    public Task OpenDefaultRootAsync() =>
        OpenRootAsync(Files.DirectoryExists(Distro!, WslFileService.NormalizePath(DefaultRoot, Home)) ? DefaultRoot : Home);

    // Reopens the folder and file of the last visit; a file that no longer exists is simply not selected.
    // A default folder changed in Settings since then wins over the saved folder.
    private async Task RestoreAsync()
    {
        var saved = _memory.Load(Distro!);
        ShowHidden = saved?.ShowHidden ?? false;
        OnRestore(saved);
        if (saved?.Default is { } d && d != DefaultRoot) saved = saved with { Root = null, Selected = null };
        if (saved?.Root is { } r && Files.DirectoryExists(Distro!, r)) await OpenRootAsync(r);
        else await OpenDefaultRootAsync();

        if (saved?.Selected is not { } selected || !Files.FileExists(Distro!, selected)) return;
        var node = await RevealAsync(selected);
        if (node is not null) await SelectAsync(node);
    }

    /// <summary>Lets a derived state take its own saved settings before the tree opens.</summary>
    protected virtual void OnRestore(FileBrowserSnapshot? saved) { }

    /// <summary>Expands the folders between the root and <paramref name="linuxPath"/> and returns its node.</summary>
    private async Task<FileNode?> RevealAsync(string linuxPath)
    {
        var dir = Root;
        if (dir is null) return null;
        var prefix = dir.LinuxPath == "/" ? "/" : dir.LinuxPath + "/";
        if (!linuxPath.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var segments = linuxPath[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var child = dir.Children.FirstOrDefault(c => c.Name == segments[i]);
            if (child is null) return null; // hidden, or gone since the listing
            if (i == segments.Length - 1) return child.Kind == FileNodeKind.File ? child : null;
            if (child.Kind != FileNodeKind.Directory) return null;
            Expanded.Add(child.LinuxPath);
            if (!child.IsLoaded) await LoadChildrenAsync(child);
            dir = child;
        }
        return null;
    }

    public async Task SetShowHiddenAsync(bool show)
    {
        ShowHidden = show;
        Remember();
        await ReloadAsync();
    }

    protected void Remember()
    {
        if (Distro is not null) _memory.Save(Distro, Snapshot());
    }

    /// <summary>What is saved for the next visit; a derived state adds its own settings.</summary>
    protected virtual FileBrowserSnapshot Snapshot() => new(Root?.LinuxPath, SelectedPath, ShowHidden, Default: DefaultRoot);

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
        Remember();
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
            var children = await Files.ListAsync(Distro!, dir.LinuxPath, ShowHidden);
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
            Logger.LogWarning(ex, "Listing {Path} failed", dir.LinuxPath);
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
        SelectedPath = file.LinuxPath;
        ExplorerError = null;
        Remember();
        await OnSelectedAsync(file);
    }

    /// <summary>Shows the newly selected file (Files: a preview, Logs: its last lines).</summary>
    protected abstract Task OnSelectedAsync(FileNode file);

    public void RevealSelectedInExplorer()
    {
        if (Distro is null || SelectedPath is null) return;
        ExplorerError = Files.RevealInExplorer(Distro, SelectedPath) is { } error ? $"탐색기를 열지 못했습니다: {error}" : null;
        Raise();
    }

    protected void Raise() => Changed?.Invoke();

    public virtual void Dispose() { }
}
