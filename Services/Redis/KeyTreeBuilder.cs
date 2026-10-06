
namespace AgentLegion.Services.Redis;

internal static class KeyTreeBuilder
{
    public static void PopulateChildren(
        KeyNode dbRoot,
        IEnumerable<string> keys,
        char separator = ':')
    {
        dbRoot.Children.Clear();

        var folderIndex = new Dictionary<string, KeyNode>(StringComparer.Ordinal);
        var db = dbRoot.DbIndex;

        foreach (var key in keys)
        {
            InsertKey(dbRoot, folderIndex, db, key, separator);
        }

        SortRecursive(dbRoot);
    }

    private static void InsertKey(
        KeyNode dbRoot,
        Dictionary<string, KeyNode> folderIndex,
        int db,
        string key,
        char separator)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var lastSep = key.LastIndexOf(separator);
        if (lastSep < 0)
        {
            dbRoot.Children.Add(new KeyNode
            {
                Kind = KeyNodeKind.Leaf,
                DbIndex = db,
                Segment = key,
                FullKey = key,
            });
            return;
        }

        var parentPrefix = key[..lastSep];
        var lastSegment = key[(lastSep + 1)..];
        var parent = EnsureFolder(dbRoot, folderIndex, db, parentPrefix, separator);
        parent.Children.Add(new KeyNode
        {
            Kind = KeyNodeKind.Leaf,
            DbIndex = db,
            Segment = lastSegment,
            FullKey = key,
        });
    }

    private static KeyNode EnsureFolder(
        KeyNode dbRoot,
        Dictionary<string, KeyNode> folderIndex,
        int db,
        string prefix,
        char separator)
    {
        if (folderIndex.TryGetValue(prefix, out var existing))
        {
            return existing;
        }

        var lastSep = prefix.LastIndexOf(separator);
        var parent = lastSep < 0
            ? dbRoot
            : EnsureFolder(dbRoot, folderIndex, db, prefix[..lastSep], separator);
        var segment = lastSep < 0 ? prefix : prefix[(lastSep + 1)..];

        var node = new KeyNode
        {
            Kind = KeyNodeKind.Folder,
            DbIndex = db,
            Segment = segment,
            FullKey = prefix,
            IsLoaded = true,
        };
        parent.Children.Add(node);
        folderIndex[prefix] = node;
        return node;
    }

    private static void SortRecursive(KeyNode node)
    {
        node.Children.Sort(Compare);
        foreach (var child in node.Children)
        {
            if (child.Kind == KeyNodeKind.Folder)
            {
                SortRecursive(child);
            }
        }
    }

    private static int Compare(KeyNode a, KeyNode b)
    {
        if (a.Kind != b.Kind)
        {
            return a.Kind == KeyNodeKind.Folder ? -1 : 1;
        }
        return string.Compare(a.Segment, b.Segment, StringComparison.OrdinalIgnoreCase);
    }
}
