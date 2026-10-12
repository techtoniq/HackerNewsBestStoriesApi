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

    public static readonly Histogram<double> RefreshDuration =
        Meter.CreateHistogram<double>("beststories.cache.refresh.duration", "ms");

    public static readonly Histogram<int> StoriesPerRefresh =
        Meter.CreateHistogram<int>("beststories.cache.refresh.stories", "{story}");

    internal static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);
}
