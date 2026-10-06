namespace AgentLegion.Services.Redis;

public enum KeyNodeKind
{
    DbRoot,
    Folder,
    Leaf,
}

public sealed class KeyNode
{
    public KeyNodeKind Kind { get; init; }

    public int DbIndex { get; init; }

    public string Segment { get; init; } = "";

    public string FullKey { get; init; } = "";

    public List<KeyNode> Children { get; } = new();

    public bool IsLoaded { get; set; }

    public bool TruncatedAtLimit { get; set; }

    public long DbSize { get; set; }

    public string Path => Kind == KeyNodeKind.DbRoot
        ? $"{DbIndex}|"
        : $"{DbIndex}|{FullKey}";
}
