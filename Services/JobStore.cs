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
                Jobs = Ordered(r.Jobs, _legion.LoadConfig()?.JobOrder);
                Error = r.Error;
                Loaded = true;
            }
            finally
            {
                _refreshLock.Release();
            }
            Changed?.Invoke();
        }

        /// <summary>Jobs in the order saved in Settings; jobs not in it (new ones) follow by name.</summary>
        public static IReadOnlyList<JobInfo> Ordered(IReadOnlyList<JobInfo> jobs, IReadOnlyList<string>? order)
        {
            if (order is not { Count: > 0 }) return jobs;
            var rank = new Dictionary<string, int>();
            for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
            return jobs.OrderBy(j => rank.TryGetValue(j.Job, out var r) ? r : int.MaxValue).ToList(); // stable: the rest stay by name
        }
    }
}
