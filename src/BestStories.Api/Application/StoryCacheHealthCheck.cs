using BestStories.Api.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Application;

/// <summary>
/// Readiness: whether this instance can handle requests, not whether the upstream is healthy.
/// </summary>
/// <remarks>
/// <para>
/// In automatic mode the instance is ready once a snapshot exists (its own initial load, or one
/// another instance published to the shared cache). In on-demand mode it is ready as soon as it
/// can reach the story cache, because the first request populates it. Reading the cache also
/// makes Redis availability part of readiness when Redis is configured.
/// </para>
/// <para>
/// A snapshot older than the maximum staleness reports <see cref="HealthStatus.Degraded"/>, which
/// keeps the instance in rotation. Staleness comes from the upstream and affects every instance
/// alike, so failing readiness for it would only replace the API's own <c>503</c> (with Problem
/// Details and <c>Retry-After</c>) with the load balancer's, and would stall rolling deployments
/// for as long as the upstream is down. Alert on the <c>beststories.cache.snapshot.age</c> metric instead.
/// </para>
/// </remarks>
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
            : HealthCheckResult.Degraded(
                "The story snapshot is older than the maximum staleness; story requests return 503 until it is refreshed.");
    }
}
