namespace BestStories.Api.Services;

public sealed class DistributedCacheOptions
{
    public string Provider { get; set; } = "Memory";

    public string? RedisConnectionString { get; set; }

    public string InstanceName { get; set; } = "BestStories:";

    public int LockLeaseSeconds { get; set; } = 1800;

    public int LockWaitSeconds { get; set; } = 120;

    public int LockPollMilliseconds { get; set; } = 200;
}
