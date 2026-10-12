using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BestStories.Api.Application;

public static class StoryTelemetry
{
    public const string ActivitySourceName = "BestStories.Api";
    public const string MeterName = "BestStories.Api";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);

    /// <summary>Snapshot reads, tagged <c>result</c> = hit, stale or miss.</summary>
    public static readonly Counter<long> CacheReads =
        Meter.CreateCounter<long>("beststories.cache.reads");

    /// <summary>Completed refreshes, tagged <c>result</c> = success or failure, and <c>trigger</c>.</summary>
    public static readonly Counter<long> Refreshes =
        Meter.CreateCounter<long>("beststories.cache.refreshes");

    /// <summary>Refreshes avoided because one was in flight or another instance had just published.</summary>
    public static readonly Counter<long> CoalescedRefreshes =
        Meter.CreateCounter<long>("beststories.cache.refreshes.coalesced");

    /// <summary>Requests that failed fast because a recent refresh failed.</summary>
    public static readonly Counter<long> CooldownRejections =
        Meter.CreateCounter<long>("beststories.cache.refreshes.cooldown_rejections");

    /// <summary>Item requests that failed during a refresh that still published a snapshot.</summary>
    public static readonly Counter<long> SkippedItems =
        Meter.CreateCounter<long>("beststories.cache.refresh.skipped_items", "{story}");

    /// <summary>Refreshes abandoned because they ran past the refresh timeout, tagged <c>trigger</c>.</summary>
    public static readonly Counter<long> RefreshTimeouts =
        Meter.CreateCounter<long>("beststories.cache.refresh.timeouts");

    /// <summary>On-demand requests that stopped waiting for a refresh and returned 503.</summary>
    public static readonly Counter<long> OnDemandWaitTimeouts =
        Meter.CreateCounter<long>("beststories.cache.refresh.wait_timeouts");

    public static readonly Histogram<double> RefreshDuration =
        Meter.CreateHistogram<double>("beststories.cache.refresh.duration", "ms");

    public static readonly Histogram<int> StoriesPerRefresh =
        Meter.CreateHistogram<int>("beststories.cache.refresh.stories", "{story}");

    private const long NoSnapshot = long.MinValue;

    private static long _latestPublishedAtUnixMilliseconds = NoSnapshot;

    /// <summary>
    /// The age, in seconds, of the newest snapshot this instance has seen. Alert on this rather
    /// than on readiness: an old snapshot is an upstream problem that every instance shares, so
    /// taking instances out of rotation for it does not help.
    /// </summary>
    public static readonly ObservableGauge<double> SnapshotAge =
        Meter.CreateObservableGauge(
            "beststories.cache.snapshot.age",
            ObserveSnapshotAge,
            unit: "s",
            description: "Age of the newest story snapshot seen by this instance.");

    /// <summary>Records the publication time of a snapshot that has been read or published.</summary>
    internal static void ObserveSnapshot(DateTimeOffset publishedAt)
    {
        var observed = publishedAt.ToUnixTimeMilliseconds();
        var current = Interlocked.Read(ref _latestPublishedAtUnixMilliseconds);
        while (observed > current)
        {
            var previous = Interlocked.CompareExchange(ref _latestPublishedAtUnixMilliseconds, observed, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private static IEnumerable<Measurement<double>> ObserveSnapshotAge()
    {
        var publishedAt = Interlocked.Read(ref _latestPublishedAtUnixMilliseconds);
        if (publishedAt == NoSnapshot)
        {
            return [];
        }

        var ageMilliseconds = Math.Max(0, TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds() - publishedAt);
        return [new Measurement<double>(ageMilliseconds / 1000d)];
    }

    internal static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);
}
