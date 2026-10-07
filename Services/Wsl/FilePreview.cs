namespace AgentLegion.Services.Wsl;

public enum PreviewKind
{
    Markdown,
    PlantUml,
    Html,
    Text,
    Binary,
    TooLarge,
}

/// <param name="Rendered">Markdown: HTML document; PlantUML: SVG; Html: the file itself.</param>
/// <param name="Source">The file text (null for binary / too large files).</param>
/// <param name="Notice">Shown above the preview, e.g. why a diagram could not be rendered.</param>
public sealed record FilePreview(
    string LinuxPath, PreviewKind Kind, long Size, DateTime Modified,
    string? Rendered, string? Source, string? Notice = null);
