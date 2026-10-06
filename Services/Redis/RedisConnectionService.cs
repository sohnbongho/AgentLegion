using System.Net;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AgentLegion.Services.Redis;

public sealed class RedisConnectionService : IAsyncDisposable
{
    private readonly ILogger<RedisConnectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnectionMultiplexer? _mux;
    private int _database;

    public RedisConnectionService(ILogger<RedisConnectionService> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _mux?.IsConnected == true;

    public string? Endpoint { get; private set; }

    public string? LastError { get; private set; }

    public IConnectionMultiplexer? Multiplexer => _mux;

    public event Action? StateChanged;

    public async Task ConnectAsync(
        string host,
        int port,
        string? password,
        int db,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisposeMuxLocked().ConfigureAwait(false);

            var options = new ConfigurationOptions
            {
                EndPoints = { { host, port } },
                Password = string.IsNullOrEmpty(password) ? null : password,
                DefaultDatabase = db,
                AbortOnConnectFail = false,
                ConnectTimeout = 5000,
            };

            _logger.LogInformation("Connecting to Redis at {Host}:{Port} (db {Db})", host, port, db);

            try
            {
                _mux = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
                _database = db;
                Endpoint = $"{host}:{port} (db {db})";
                LastError = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis connection failed for {Host}:{Port}", host, port);
                _mux = null;
                Endpoint = null;
                LastError = ex.Message;
            }
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeMuxLocked().ConfigureAwait(false);
            Endpoint = null;
            LastError = null;
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    public IDatabase GetDatabase()
    {
        if (_mux is null)
        {
            throw new InvalidOperationException("Not connected to Redis.");
        }
        return _mux.GetDatabase(_database);
    }

    public IDatabase GetDatabase(int db)
    {
        if (_mux is null)
        {
            throw new InvalidOperationException("Not connected to Redis.");
        }
        return _mux.GetDatabase(db);
    }

    public IServer GetServer()
    {
        if (_mux is null)
        {
            throw new InvalidOperationException("Not connected to Redis.");
        }
        var endpoint = _mux.GetEndPoints().FirstOrDefault()
            ?? throw new InvalidOperationException("No Redis endpoints available.");
        return _mux.GetServer(endpoint);
    }

    public async Task<IReadOnlyList<int>> GetDatabaseIndexesAsync(CancellationToken ct = default)
    {
        if (_mux is null)
        {
            throw new InvalidOperationException("Not connected to Redis.");
        }

        try
        {
            var server = GetServer();
            var raw = await server.ConfigGetAsync("databases").WaitAsync(ct).ConfigureAwait(false);
            if (raw is { Length: > 0 } && int.TryParse(raw[0].Value, out var count) && count > 0)
            {
                return Enumerable.Range(0, count).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CONFIG GET databases failed, falling back to 0..15");
        }

        return Enumerable.Range(0, 16).ToList();
    }

    public async Task<IReadOnlyDictionary<int, long>> GetAllDbSizesAsync(
        IReadOnlyList<int> dbIndexes,
        CancellationToken ct = default)
    {
        if (_mux is null)
        {
            throw new InvalidOperationException("Not connected to Redis.");
        }

        var server = GetServer();
        var tasks = dbIndexes
            .Select(db => (Db: db, Task: server.DatabaseSizeAsync(db)))
            .ToList();

        var result = new Dictionary<int, long>(dbIndexes.Count);
        foreach (var (db, task) in tasks)
        {
            try
            {
                var size = await task.WaitAsync(ct).ConfigureAwait(false);
                result[db] = size;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DBSIZE failed for db {Db}", db);
                result[db] = 0;
            }
        }
        return result;
    }

    private async Task DisposeMuxLocked()
    {
        if (_mux is null)
        {
            return;
        }

        try
        {
            await _mux.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing existing Redis multiplexer");
        }

        await _mux.DisposeAsync().ConfigureAwait(false);
        _mux = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeMuxLocked().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
