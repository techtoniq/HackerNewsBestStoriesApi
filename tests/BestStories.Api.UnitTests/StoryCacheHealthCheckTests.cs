using BestStories.Api.Models;
using BestStories.Api.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BestStories.Api.UnitTests;

public sealed class StoryCacheHealthCheckTests
{
    [Fact]
    public async Task GivenAutomaticRefreshWithoutSnapshot_WhenReadinessIsChecked_ThenUnhealthyIsReported()
    {
        // Given
        var healthCheck = new StoryCacheHealthCheck(
            new StoryCache(),
            Options.Create(new StoryCacheOptions { AutomaticRefresh = true }));

        // When
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task GivenAutomaticRefreshWithSnapshot_WhenReadinessIsChecked_ThenHealthyIsReported()
    {
        // Given
        var cache = new StoryCache();
        await cache.SetSnapshotAsync(
        [
            new StoryResponse("Story", "https://example.com", "author", DateTimeOffset.UtcNow, 1, 0)
        ],
            TestContext.Current.CancellationToken);
        var healthCheck = new StoryCacheHealthCheck(
            cache,
            Options.Create(new StoryCacheOptions { AutomaticRefresh = true }));

        // When
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task GivenOnDemandRefreshWithoutSnapshot_WhenReadinessIsChecked_ThenHealthyIsReported()
    {
        // Given
        var healthCheck = new StoryCacheHealthCheck(
            new StoryCache(),
            Options.Create(new StoryCacheOptions { AutomaticRefresh = false }));

        // When
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
