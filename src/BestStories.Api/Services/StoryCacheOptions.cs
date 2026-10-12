namespace BestStories.Api.Services;

public sealed class StoryCacheOptions
{
    public bool AutomaticRefresh { get; set; } = true;

    public int RefreshIntervalSeconds { get; set; } = 300;

    public int CacheTtlSeconds { get; set; } = 300;

    public int MaxConcurrentRequests { get; set; } = 8;
}
