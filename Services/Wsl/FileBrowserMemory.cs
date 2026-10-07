using System.Text.Json;

namespace AgentLegion.Services.Wsl;

/// <param name="Encoding">Logs only: the encoding picked for log files ("auto", "euc-kr", "utf-8").</param>
/// <param name="History">Logs only: the commands run most recently, newest first.</param>
/// <param name="Wrap">Logs only: whether long lines wrap.</param>
/// <param name="Default">The default folder when this was saved; a different one now means it was changed in Settings.</param>
public sealed record FileBrowserSnapshot(string? Root, string? Selected, bool ShowHidden,
    string? Encoding = null, List<string>? History = null, bool? Wrap = null, string? Default = null);

/// <summary>
/// Remembers per distro which folder was open and which file was selected (files.json in the data folder),
/// so the Files tab reopens where it was left after the app or the browser restarts.
/// </summary>
public class FileBrowserMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public FileBrowserMemory(LegionService legion) : this(legion, "files.json") { }

    protected FileBrowserMemory(LegionService legion, string fileName) => _path = Path.Combine(legion.DataDir, fileName);

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

/// <summary>The Logs tab's folder, file, encoding and command history (logs.json), kept apart from the Files tab's.</summary>
public sealed class LogBrowserMemory : FileBrowserMemory
{
    public LogBrowserMemory(LegionService legion) : base(legion, "logs.json") { }
}
