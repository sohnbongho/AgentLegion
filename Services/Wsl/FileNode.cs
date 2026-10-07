namespace AgentLegion.Services.Wsl;

public enum FileNodeKind
{
    Directory,
    File,
}

public sealed class FileNode
{
    public FileNodeKind Kind { get; init; }

    public string Name { get; init; } = "";

    /// <summary>Canonical Linux path inside the distro, e.g. /home/user/notes.md.</summary>
    public string LinuxPath { get; init; } = "";

    public long Size { get; init; }

    public DateTime Modified { get; init; }

    public List<FileNode> Children { get; } = new();

    public bool IsLoaded { get; set; }

    /// <summary>Why this directory could not be listed (permission denied, ...).</summary>
    public string? Error { get; set; }

    public string Extension => Kind == FileNodeKind.File ? Path.GetExtension(Name).ToLowerInvariant() : "";
}
