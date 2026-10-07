using System.Text.Json;

namespace AgentLegion.Services.Wsl;

public sealed record FileBrowserSnapshot(string? Root, string? Selected, bool ShowHidden);

/// <summary>
/// Remembers per distro which folder was open and which file was selected (files.json in the data folder),
/// so the Files tab reopens where it was left after the app or the browser restarts.
/// </summary>
public sealed class FileBrowserMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public FileBrowserMemory(LegionService legion) => _path = Path.Combine(legion.DataDir, "files.json");

    public FileBrowserSnapshot? Load(string distro)
    {
        lock (_lock)
        {
            return ReadAll().TryGetValue(distro, out var s) ? s : null;
        }
    }

    public void Save(string distro, FileBrowserSnapshot snapshot)
    {
        lock (_lock)
        {
            var all = ReadAll();
            all[distro] = snapshot;
            try { File.WriteAllText(_path, JsonSerializer.Serialize(all, JsonOptions)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only a convenience */ }
        }
    }

    private Dictionary<string, FileBrowserSnapshot> ReadAll()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, FileBrowserSnapshot>>(File.ReadAllText(_path))
                   ?? new Dictionary<string, FileBrowserSnapshot>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, FileBrowserSnapshot>();
        }
    }
}
