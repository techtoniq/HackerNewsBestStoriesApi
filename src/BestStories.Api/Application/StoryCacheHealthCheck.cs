using BestStories.Api.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Application;

/// <summary>
/// Readiness: in automatic mode the instance is ready while it holds a snapshot within the
/// maximum staleness. In on-demand mode it is ready as soon as it can reach the story cache,
/// because the first request populates it. Reading the cache also makes Redis availability part
/// of readiness when Redis is configured.
/// </summary>
public sealed class StoryCacheHealthCheck(
    IStoryCache storyCache,
    IOptions<StoryCacheOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var snapshot = await storyCache.GetSnapshotAsync(cancellationToken);

        if (!settings.AutomaticRefresh)
        {
            return HealthCheckResult.Healthy("The story cache is reachable.");
        }

        if (snapshot is null)
        {
            return HealthCheckResult.Unhealthy("The initial story snapshot is not available yet.");
        }

        return snapshot.AgeAt(timeProvider.GetUtcNow()) <= settings.MaxStaleness
            ? HealthCheckResult.Healthy("A story snapshot within the maximum staleness is available.")
            : HealthCheckResult.Unhealthy("The story snapshot is older than the maximum staleness.");
    }
}
