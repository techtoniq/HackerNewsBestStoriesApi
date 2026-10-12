using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.UnitTests.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BestStories.Api.UnitTests;

public sealed class StoryCacheHealthCheckTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly InMemoryStoryCache _cache = new();

    [Fact]
    public async Task GivenAutomaticRefreshWithoutSnapshot_WhenReadinessIsChecked_ThenUnhealthyIsReported()
    {
        // Given
        var healthCheck = CreateHealthCheck(automaticRefresh: true);

        // When
        var result = await CheckAsync(healthCheck);

        // Then
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task GivenAutomaticRefreshWithAStaleButAcceptableSnapshot_WhenReadinessIsChecked_ThenHealthyIsReported()
    {
        // Given
        await PublishAsync(age: TimeSpan.FromSeconds(1000));
        var healthCheck = CreateHealthCheck(automaticRefresh: true);

        // When
        var result = await CheckAsync(healthCheck);

        // Then
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task GivenAutomaticRefreshWithASnapshotBeyondMaxStaleness_WhenReadinessIsChecked_ThenUnhealthyIsReported()
    {
        // Given
        await PublishAsync(age: TimeSpan.FromSeconds(1801));
        var healthCheck = CreateHealthCheck(automaticRefresh: true);

        // When
        var result = await CheckAsync(healthCheck);

        // Then
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task GivenOnDemandRefreshWithoutSnapshot_WhenReadinessIsChecked_ThenHealthyIsReported()
    {
        // Given
        var healthCheck = CreateHealthCheck(automaticRefresh: false);

        // When
        var result = await CheckAsync(healthCheck);

        // Then
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private StoryCacheHealthCheck CreateHealthCheck(bool automaticRefresh) =>
        new(
            _cache,
            Options.Create(new StoryCacheOptions
            {
                AutomaticRefresh = automaticRefresh,
                MaxStalenessSeconds = 1800
            }),
            _timeProvider);

    private static Task<HealthCheckResult> CheckAsync(StoryCacheHealthCheck healthCheck) =>
        healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private Task PublishAsync(TimeSpan age) =>
        _cache.SetSnapshotAsync(
            TestStories.Snapshot(_timeProvider.GetUtcNow() - age, 1),
            TestContext.Current.CancellationToken);
}
