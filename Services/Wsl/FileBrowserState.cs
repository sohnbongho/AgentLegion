using Microsoft.Extensions.Logging;

namespace AgentLegion.Services.Wsl;

/// <summary>Per-circuit state of the WSL file browser: the folder tree plus the preview of the selected file.</summary>
public sealed class FileBrowserState : FileTreeState
{
    private CancellationTokenSource _previewOp = new();

    public FileBrowserState(WslFileService files, WslInfoService wsl, FileBrowserMemory memory, ILogger<FileBrowserState> logger)
        : base(files, wsl, memory, logger)
    {
    }

    public FilePreview? Preview { get; private set; }

    public bool PreviewLoading { get; private set; }

    public string? PreviewError { get; private set; }

    protected override async Task OnSelectedAsync(FileNode file)
    {
        try { _previewOp.Cancel(); } catch { /* ignore */ }
        _previewOp.Dispose();
        _previewOp = new CancellationTokenSource();
        var ct = _previewOp.Token;

        Preview = null;
        PreviewError = null;
        PreviewLoading = true;
        Raise();
        try
        {
            var preview = await Files.PreviewAsync(Distro!, file.LinuxPath, ct);
            if (!ct.IsCancellationRequested) Preview = preview;
        }
        catch (OperationCanceledException) { /* a newer selection won */ }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Preview of {Path} failed", file.LinuxPath);
            if (!ct.IsCancellationRequested) PreviewError = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                PreviewLoading = false;
                Raise();
            }
        }
    }

    public override void Dispose()
    {
        try { _previewOp.Cancel(); } catch { /* ignore */ }
        _previewOp.Dispose();
    }
}
