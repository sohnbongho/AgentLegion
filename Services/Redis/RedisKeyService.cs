using System.Text;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace AgentLegion.Services.Redis;

public sealed class RedisKeyService
{
    private const int ScanPageSize = 500;
    private const int JsonDetectMaxBytes = 1_048_576;

    private static readonly Encoding EucKr = Encoding.GetEncoding("euc-kr");

    private static string Decode(RedisValue v)
    {
        if (v.IsNull) return "";
        var bytes = (byte[]?)v;
        return bytes is null ? "" : EucKr.GetString(bytes);
    }

    private readonly RedisConnectionService _conn;
    private readonly ILogger<RedisKeyService> _logger;

    public RedisKeyService(RedisConnectionService conn, ILogger<RedisKeyService> logger)
    {
        _conn = conn;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<string> Keys, bool Truncated)> ScanKeysAsync(
        int db,
        string? matchPattern,
        int limit,
        CancellationToken ct)
    {
        var server = _conn.GetServer();
        var pattern = string.IsNullOrWhiteSpace(matchPattern) ? "*" : matchPattern;

        var keys = new List<string>(Math.Min(limit, 1024));
        var truncated = false;

        await foreach (var key in server.KeysAsync(database: db, pattern: pattern, pageSize: ScanPageSize)
                           .WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (keys.Count >= limit)
            {
                truncated = true;
                break;
            }
            keys.Add(key!);
        }

        return (keys, truncated);
    }

    public async Task<KeyValueDetail> GetKeyDetailAsync(
        int db,
        string key,
        KeyDetailOptions options,
        CancellationToken ct)
    {
        var database = _conn.GetDatabase(db);
        var redisKey = (RedisKey)key;

        var typeRaw = await database.KeyTypeAsync(redisKey).WaitAsync(ct).ConfigureAwait(false);
        var type = Map(typeRaw);
        var ttl = await database.KeyTimeToLiveAsync(redisKey).WaitAsync(ct).ConfigureAwait(false);
        var memory = await TryGetMemoryUsageAsync(database, redisKey, ct).ConfigureAwait(false);

        if (type == RedisKeyType.None)
        {
            return new KeyValueDetail(db, key, type, ttl, memory, 0, false, new EmptyPayload());
        }

        var (totalCount, truncated, payload) = type switch
        {
            RedisKeyType.String => await ReadStringAsync(database, redisKey, ct).ConfigureAwait(false),
            RedisKeyType.List => await ReadListAsync(database, redisKey, options.CollectionLimit, ct).ConfigureAwait(false),
            RedisKeyType.Hash => await ReadHashAsync(database, redisKey, options.CollectionLimit, ct).ConfigureAwait(false),
            RedisKeyType.Set => await ReadSetAsync(database, redisKey, options.CollectionLimit, ct).ConfigureAwait(false),
            RedisKeyType.SortedSet => await ReadSortedSetAsync(database, redisKey, options.CollectionLimit, ct).ConfigureAwait(false),
            RedisKeyType.Stream => await ReadStreamAsync(database, redisKey, options.StreamCount, ct).ConfigureAwait(false),
            _ => ((long)0, false, (RedisValuePayload)new EmptyPayload()),
        };

        return new KeyValueDetail(db, key, type, ttl, memory, totalCount, truncated, payload);
    }

    private static RedisKeyType Map(RedisType type) => type switch
    {
        RedisType.String => RedisKeyType.String,
        RedisType.List => RedisKeyType.List,
        RedisType.Hash => RedisKeyType.Hash,
        RedisType.Set => RedisKeyType.Set,
        RedisType.SortedSet => RedisKeyType.SortedSet,
        RedisType.Stream => RedisKeyType.Stream,
        _ => RedisKeyType.None,
    };

    private async Task<long?> TryGetMemoryUsageAsync(IDatabase db, RedisKey key, CancellationToken ct)
    {
        try
        {
            var result = await db.ExecuteAsync("MEMORY", "USAGE", (string)key!).WaitAsync(ct).ConfigureAwait(false);
            if (result.IsNull)
            {
                return null;
            }
            return (long)result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MEMORY USAGE failed for key {Key}", (string)key!);
            return null;
        }
    }

    private static async Task<(long Total, bool Truncated, RedisValuePayload Payload)> ReadStringAsync(
        IDatabase db, RedisKey key, CancellationToken ct)
    {
        var value = await db.StringGetAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var s = Decode(value);
        var likelyJson = LooksLikeJson(s);
        return (s.Length, false, new StringPayload(s, likelyJson));
    }

    private static async Task<(long Total, bool Truncated, RedisValuePayload Payload)> ReadListAsync(
        IDatabase db, RedisKey key, int limit, CancellationToken ct)
    {
        var total = await db.ListLengthAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var stop = limit <= 0 ? -1 : Math.Min(total - 1, limit - 1);
        var slice = await db.ListRangeAsync(key, 0, stop).WaitAsync(ct).ConfigureAwait(false);
        var items = slice.Select(v => Decode(v)).ToList();
        return (total, total > items.Count, new ListPayload(items));
    }

    private static async Task<(long Total, bool Truncated, RedisValuePayload Payload)> ReadHashAsync(
        IDatabase db, RedisKey key, int limit, CancellationToken ct)
    {
        var total = await db.HashLengthAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var all = await db.HashGetAllAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var entries = all
            .OrderBy(e => Decode(e.Name), StringComparer.OrdinalIgnoreCase)
            .Take(limit > 0 ? limit : int.MaxValue)
            .Select(e => new KeyValuePair<string, string>(Decode(e.Name), Decode(e.Value)))
            .ToList();
        return (total, total > entries.Count, new HashPayload(entries));
    }

    private static async Task<(long Total, bool Truncated, RedisValuePayload Payload)> ReadSetAsync(
        IDatabase db, RedisKey key, int limit, CancellationToken ct)
    {
        var total = await db.SetLengthAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var all = await db.SetMembersAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var members = all
            .Select(v => Decode(v))
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Take(limit > 0 ? limit : int.MaxValue)
            .ToList();
        return (total, total > members.Count, new SetPayload(members));
    }

    private static async Task<(long Total, bool Truncated, RedisValuePayload Payload)> ReadSortedSetAsync(
        IDatabase db, RedisKey key, int limit, CancellationToken ct)
    {
        var total = await db.SortedSetLengthAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var stop = limit <= 0 ? -1 : Math.Min(total - 1, limit - 1);
        var slice = await db.SortedSetRangeByRankWithScoresAsync(key, 0, stop).WaitAsync(ct).ConfigureAwait(false);
        var entries = slice.Select(e => new SortedSetEntryDto(Decode(e.Element), e.Score)).ToList();
        return (total, total > entries.Count, new SortedSetPayload(entries));
    }

    private static async Task<(long Total, bool Truncated, RedisValuePayload Payload)> ReadStreamAsync(
        IDatabase db, RedisKey key, int count, CancellationToken ct)
    {
        var total = await db.StreamLengthAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var slice = await db.StreamRangeAsync(key, "-", "+", count, Order.Descending).WaitAsync(ct).ConfigureAwait(false);
        var entries = slice
            .Select(e => new StreamEntryDto(
                e.Id.ToString(),
                e.Values.Select(v => new KeyValuePair<string, string>(Decode(v.Name), Decode(v.Value))).ToList()))
            .ToList();
        return (total, total > entries.Count, new StreamPayload(entries));
    }

    private static bool LooksLikeJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > JsonDetectMaxBytes)
        {
            return false;
        }
        var first = -1;
        var last = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsWhiteSpace(s[i])) { first = i; break; }
        }
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(s[i])) { last = i; break; }
        }
        if (first < 0 || last < 0)
        {
            return false;
        }
        var a = s[first];
        var b = s[last];
        return (a == '{' && b == '}') || (a == '[' && b == ']');
    }
}
