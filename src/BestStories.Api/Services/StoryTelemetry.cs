using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BestStories.Api.Services;

public static class StoryTelemetry
{
    public const string ActivitySourceName = "BestStories.Api";
    public const string MeterName = "BestStories.Api";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Refreshes =
        Meter.CreateCounter<long>("beststories.cache.refreshes");

    public static readonly Counter<long> CoalescedRefreshes =
        Meter.CreateCounter<long>("beststories.cache.refreshes.coalesced");

    public static readonly Counter<long> CacheReads =
        Meter.CreateCounter<long>("beststories.cache.reads");

    public static readonly Histogram<double> RefreshDuration =
        Meter.CreateHistogram<double>("beststories.cache.refresh.duration", "ms");

    public static readonly Histogram<int> StoriesPerRefresh =
        Meter.CreateHistogram<int>("beststories.cache.refresh.stories", "{story}");
}
