using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Services;

public sealed class StoryCacheHealthCheck(
    IStoryCache storyCache,
    IOptions<StoryCacheOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await storyCache.GetSnapshotAsync(cancellationToken);
        var result = !options.Value.AutomaticRefresh ||
            snapshot is not null
            ? HealthCheckResult.Healthy("The initial story snapshot is available.")
            : HealthCheckResult.Unhealthy("The initial story snapshot is not available yet.");

        return result;
    }
}
