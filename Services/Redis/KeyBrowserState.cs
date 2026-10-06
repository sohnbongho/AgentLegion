using Microsoft.Extensions.Logging;

namespace AgentLegion.Services.Redis;

public sealed class KeyBrowserState : IDisposable
{
    private const int ScanLimit = 10_000;

    private readonly RedisConnectionService _conn;
    private readonly RedisKeyService _keys;
    private readonly ILogger<KeyBrowserState> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly List<KeyNode> _roots = new();
    private CancellationTokenSource _currentOp = new();
    private bool _initializing;

    public KeyBrowserState(
        RedisConnectionService conn,
        RedisKeyService keys,
        ILogger<KeyBrowserState> logger)
    {
        _conn = conn;
        _keys = keys;
        _logger = logger;
        _conn.StateChanged += OnConnectionStateChanged;
    }

    public IReadOnlyList<KeyNode> Roots => _roots;

    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);

    public string? SelectedNodePath { get; private set; }

    public KeyValueDetail? SelectedDetail { get; private set; }

    public string? FocusedItemLabel { get; private set; }

    public string? FocusedItemText { get; private set; }

    public string MatchPattern { get; set; } = "";

    public bool IsInitialized { get; private set; }

    public bool IsBusy { get; private set; }

    public string? LastError { get; private set; }

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_conn.IsConnected || IsInitialized || _initializing)
        {
            return;
        }

        var linkedCts = LinkAndReset(ct);
        _initializing = true;
        IsBusy = true;
        LastError = null;
        Raise();

        try
        {
            var dbs = await _conn.GetDatabaseIndexesAsync(linkedCts.Token).ConfigureAwait(false);
            var sizes = await _conn.GetAllDbSizesAsync(dbs, linkedCts.Token).ConfigureAwait(false);

            _roots.Clear();
            foreach (var db in dbs)
            {
                _roots.Add(new KeyNode
                {
                    Kind = KeyNodeKind.DbRoot,
                    DbIndex = db,
                    Segment = $"db{db}",
                    FullKey = "",
                    DbSize = sizes.TryGetValue(db, out var s) ? s : 0,
                });
            }
            IsInitialized = true;
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KeyBrowserState initialize failed");
            LastError = ex.Message;
        }
        finally
        {
            _initializing = false;
            IsBusy = false;
            Raise();
        }
    }

    public async Task EnsureDbLoadedAsync(int db, CancellationToken ct = default)
    {
        var root = _roots.FirstOrDefault(r => r.DbIndex == db);
        if (root is null || root.IsLoaded)
        {
            return;
        }
        await LoadDbInternalAsync(root, ct).ConfigureAwait(false);
    }

    public async Task ReloadDbAsync(int db, CancellationToken ct = default)
    {
        var root = _roots.FirstOrDefault(r => r.DbIndex == db);
        if (root is null)
        {
            return;
        }
        root.IsLoaded = false;
        root.Children.Clear();
        await LoadDbInternalAsync(root, ct).ConfigureAwait(false);
    }

    public async Task ReloadAllAsync(CancellationToken ct = default)
    {
        if (!_conn.IsConnected)
        {
            return;
        }

        var linkedCts = LinkAndReset(ct);
        IsBusy = true;
        LastError = null;
        Raise();
        try
        {
            var sizes = await _conn.GetAllDbSizesAsync(
                _roots.Select(r => r.DbIndex).ToList(), linkedCts.Token).ConfigureAwait(false);
            foreach (var root in _roots)
            {
                root.DbSize = sizes.TryGetValue(root.DbIndex, out var s) ? s : 0;
                root.IsLoaded = false;
                root.Children.Clear();
            }
            Expanded.Clear();
            SelectedNodePath = null;
            SelectedDetail = null;
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReloadAll failed");
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
    }

    private async Task LoadDbInternalAsync(KeyNode root, CancellationToken ct)
    {
        var linkedCts = LinkAndReset(ct);
        IsBusy = true;
        LastError = null;
        Raise();
        try
        {
            await _gate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            try
            {
                var (keys, truncated) = await _keys.ScanKeysAsync(
                    root.DbIndex, MatchPattern, ScanLimit, linkedCts.Token).ConfigureAwait(false);
                KeyTreeBuilder.PopulateChildren(root, keys);
                root.TruncatedAtLimit = truncated;
                root.IsLoaded = true;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SCAN failed for db {Db}", root.DbIndex);
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
    }

    public void FocusItem(string label, string text)
    {
        FocusedItemLabel = label;
        FocusedItemText = text;
        Raise();
    }

    public void ClearFocus()
    {
        FocusedItemLabel = null;
        FocusedItemText = null;
        Raise();
    }

    public void ToggleExpand(string nodePath)
    {
        if (!Expanded.Add(nodePath))
        {
            Expanded.Remove(nodePath);
        }
        Raise();
    }

    public bool IsExpanded(string nodePath) => Expanded.Contains(nodePath);

    public async Task SelectAsync(KeyNode leaf, CancellationToken ct = default)
    {
        if (leaf.Kind != KeyNodeKind.Leaf)
        {
            return;
        }
        var linkedCts = LinkAndReset(ct);
        SelectedNodePath = leaf.Path;
        SelectedDetail = null;
        FocusedItemLabel = null;
        FocusedItemText = null;
        IsBusy = true;
        LastError = null;
        Raise();
        try
        {
            var detail = await _keys.GetKeyDetailAsync(
                leaf.DbIndex, leaf.FullKey, new KeyDetailOptions(), linkedCts.Token).ConfigureAwait(false);
            SelectedDetail = detail;
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load detail for {Key}", leaf.FullKey);
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Raise();
        }
    }

    public void Clear()
    {
        try { _currentOp.Cancel(); } catch { /* ignore */ }
        _roots.Clear();
        Expanded.Clear();
        SelectedNodePath = null;
        SelectedDetail = null;
        FocusedItemLabel = null;
        FocusedItemText = null;
        IsInitialized = false;
        IsBusy = false;
        LastError = null;
        Raise();
    }

    private CancellationTokenSource LinkAndReset(CancellationToken external)
    {
        try { _currentOp.Cancel(); } catch { /* ignore */ }
        _currentOp.Dispose();
        _currentOp = CancellationTokenSource.CreateLinkedTokenSource(external);
        return _currentOp;
    }

    private void OnConnectionStateChanged()
    {
        if (!_conn.IsConnected)
        {
            Clear();
        }
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _conn.StateChanged -= OnConnectionStateChanged;
        try { _currentOp.Cancel(); } catch { /* ignore */ }
        _currentOp.Dispose();
        _gate.Dispose();
    }
}
