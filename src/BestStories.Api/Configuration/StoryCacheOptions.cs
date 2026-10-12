namespace BestStories.Api.Configuration;

public sealed class StoryCacheOptions
{
    public const string SectionName = "StoryCache";

    /// <summary>
    /// When <c>true</c>, a background service refreshes the snapshot every
    /// <see cref="RefreshIntervalSeconds"/>. When <c>false</c>, requests trigger refreshes once the
    /// snapshot is older than <see cref="CacheTtlSeconds"/>.
    /// </summary>
    public bool AutomaticRefresh { get; set; } = true;

    public int RefreshIntervalSeconds { get; set; } = 300;

    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>
    /// The oldest snapshot the API will serve. Between the freshness window and this limit, the
    /// last good snapshot is served while a refresh happens; beyond it, the API returns 503.
    /// </summary>
    public int MaxStalenessSeconds { get; set; } = 1800;

    /// <summary>The maximum number of concurrent item requests to Hacker News during a refresh.</summary>
    public int MaxConcurrentRequests { get; set; } = 8;

    /// <summary>
    /// The fraction of item requests that must succeed for a refresh to publish a new snapshot.
    /// Failed items are left out of that snapshot.
    /// </summary>
    public double MinimumSuccessRatio { get; set; } = 0.95;

    /// <summary>
    /// After a failed on-demand refresh, requests that need a refresh fail fast for this long
    /// instead of starting another upstream fetch.
    /// </summary>
    public int FailureCooldownSeconds { get; set; } = 30;

    /// <summary>
    /// The longest a single refresh may run before it is abandoned and counted as a failure. It
    /// bounds how long an upstream that is slow, rather than failing, can hold the refresh lock.
    /// </summary>
    public int RefreshTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How long an on-demand request waits for a refresh before returning <c>503</c>. The refresh
    /// itself carries on, so a later request can still be served from it.
    /// </summary>
    public int OnDemandWaitSeconds { get; set; } = 10;

    /// <summary>The longest wait between retries of the initial load in automatic mode.</summary>
    public int InitialLoadMaxRetryDelaySeconds { get; set; } = 30;

    /// <summary>The upper bound for the <c>Cache-Control: max-age</c> sent to clients.</summary>
    public int ClientCacheMaxAgeSeconds { get; set; } = 60;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(RefreshIntervalSeconds);

    public TimeSpan CacheTtl => TimeSpan.FromSeconds(CacheTtlSeconds);

    public TimeSpan MaxStaleness => TimeSpan.FromSeconds(MaxStalenessSeconds);

    public TimeSpan FailureCooldown => TimeSpan.FromSeconds(FailureCooldownSeconds);

    public TimeSpan RefreshTimeout => TimeSpan.FromSeconds(RefreshTimeoutSeconds);

    public TimeSpan OnDemandWait => TimeSpan.FromSeconds(OnDemandWaitSeconds);

    /// <summary>How long a snapshot counts as fresh in the configured refresh mode.</summary>
    public TimeSpan FreshFor => AutomaticRefresh ? RefreshInterval : CacheTtl;
}
