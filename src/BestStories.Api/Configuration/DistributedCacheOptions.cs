namespace BestStories.Api.Configuration;

public enum CacheProvider
{
    Memory,
    Redis
}

public sealed class DistributedCacheOptions
{
    public const string SectionName = "DistributedCache";

    public CacheProvider Provider { get; set; } = CacheProvider.Memory;

    public string? RedisConnectionString { get; set; }

    public string InstanceName { get; set; } = "BestStories:";

    /// <summary>
    /// The refresh-lock lease. The owner renews it every third of this period while a refresh
    /// runs, so it only needs to cover the time it takes to detect a crashed owner.
    /// </summary>
    public int LockLeaseSeconds { get; set; } = 30;

    /// <summary>How long an on-demand request waits for another instance's refresh.</summary>
    public int LockWaitSeconds { get; set; } = 10;

    public int LockPollMilliseconds { get; set; } = 200;

    /// <summary>
    /// How long each instance reuses a snapshot it has read from Redis before reading it again.
    /// Zero disables the local copy.
    /// </summary>
    public int LocalCacheMilliseconds { get; set; } = 1000;

    public TimeSpan LockLease => TimeSpan.FromSeconds(LockLeaseSeconds);

    public TimeSpan LockWait => TimeSpan.FromSeconds(LockWaitSeconds);

    public TimeSpan LockPoll => TimeSpan.FromMilliseconds(LockPollMilliseconds);

    public TimeSpan LocalCacheDuration => TimeSpan.FromMilliseconds(LocalCacheMilliseconds);
}
