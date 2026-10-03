namespace AgentLegion.Services
{
    /// <summary>
    /// Cached job list shared by the sidebar and the Jobs page. Listing shells out to WSL (~1-2s),
    /// so it is refreshed on demand rather than on every page render.
    /// </summary>
    public class JobStore
    {
        private readonly LegionService _legion;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public JobStore(LegionService legion) => _legion = legion;

        public IReadOnlyList<JobInfo> Jobs { get; private set; } = Array.Empty<JobInfo>();
        public string? Error { get; private set; }
        public bool Loaded { get; private set; }
        public bool IsConfigured => _legion.IsConfigured;

        public event Action? Changed;

        public Task EnsureLoadedAsync() => Loaded ? Task.CompletedTask : RefreshAsync();

        public async Task RefreshAsync()
        {
            if (!_legion.IsConfigured)
            {
                Jobs = Array.Empty<JobInfo>();
                Error = null;
                Loaded = false;
                Changed?.Invoke();
                return;
            }

            await _refreshLock.WaitAsync();
            try
            {
                var r = await _legion.GetJobsAsync();
                Jobs = r.Jobs;
                Error = r.Error;
                Loaded = true;
            }
            finally
            {
                _refreshLock.Release();
            }
            Changed?.Invoke();
        }
    }
}
