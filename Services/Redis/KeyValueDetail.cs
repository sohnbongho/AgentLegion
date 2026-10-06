namespace AgentLegion.Services.Redis;

public enum RedisKeyType
{
    None,
    String,
    List,
    Hash,
    Set,
    SortedSet,
    Stream,
}

public sealed record KeyDetailOptions(int CollectionLimit = 1000, int StreamCount = 50);

public sealed record KeyValueDetail(
    int DbIndex,
    string Key,
    RedisKeyType Type,
    TimeSpan? Ttl,
    long? MemoryBytes,
    long TotalCount,
    bool Truncated,
    RedisValuePayload Payload);

public abstract record RedisValuePayload;

public sealed record StringPayload(string Value, bool IsLikelyJson) : RedisValuePayload;

public sealed record ListPayload(IReadOnlyList<string> Items) : RedisValuePayload;

public sealed record HashPayload(IReadOnlyList<KeyValuePair<string, string>> Entries) : RedisValuePayload;

public sealed record SetPayload(IReadOnlyList<string> Members) : RedisValuePayload;

public sealed record SortedSetPayload(IReadOnlyList<SortedSetEntryDto> Entries) : RedisValuePayload;

public sealed record SortedSetEntryDto(string Member, double Score);

public sealed record StreamPayload(IReadOnlyList<StreamEntryDto> Entries) : RedisValuePayload;

public sealed record StreamEntryDto(string Id, IReadOnlyList<KeyValuePair<string, string>> Fields);

public sealed record EmptyPayload : RedisValuePayload;
