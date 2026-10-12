using BestStories.Api.ComponentTests.TestSupport;
using BestStories.Api.Configuration;
using BestStories.Api.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BestStories.Api.ComponentTests;

public sealed class RedisRefreshLockTests(RedisFixture redis)
{
    private readonly string _instanceName = RedisFixture.NewInstanceName();

    [Fact]
    public async Task GivenAHeldLock_WhenAnotherInstanceTriesToAcquireIt_ThenItIsRefused()
    {
        // Given
        var first = CreateLock();
        var second = CreateLock();
        await using var held = await first.TryAcquireAsync(TestContext.Current.CancellationToken);

        // When
        var refused = await second.TryAcquireAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(held);
        Assert.Null(refused);
    }

    [Fact]
    public async Task GivenAReleasedLock_WhenAnotherInstanceTriesToAcquireIt_ThenItSucceeds()
    {
        // Given
        var first = CreateLock();
        var second = CreateLock();
        var held = await first.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(held);
        await held.DisposeAsync();

        // When
        await using var acquired = await second.TryAcquireAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(acquired);
    }

    [Fact]
    public async Task GivenAnAcquiredLock_WhenItIsInspected_ThenItExpiresWithinTheLease()
    {
        // Given
        var refreshLock = CreateLock(leaseSeconds: 30);

        // When
        await using var held = await refreshLock.TryAcquireAsync(TestContext.Current.CancellationToken);
        var timeToLive = await redis.Connection.GetDatabase().KeyTimeToLiveAsync(refreshLock.Key);

        // Then
        Assert.NotNull(held);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task GivenALongRefresh_WhenTheLeasePeriodPasses_ThenTheOwnerKeepsTheLock()
    {
        // Given
        var first = CreateLock(leaseSeconds: 1);
        var second = CreateLock(leaseSeconds: 1);
        await using var held = await first.TryAcquireAsync(TestContext.Current.CancellationToken);

        // When
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        var refused = await second.TryAcquireAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(held);
        Assert.Null(refused);
    }

    [Fact]
    public async Task GivenALeaseThatExpiredAndWasTakenOver_WhenTheOldOwnerReleases_ThenTheNewOwnersLockIsKept()
    {
        // Given
        var first = CreateLock();
        var second = CreateLock();
        var third = CreateLock();
        var expired = await first.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(expired);
        await redis.Connection.GetDatabase().KeyDeleteAsync(first.Key);
        await using var current = await second.TryAcquireAsync(TestContext.Current.CancellationToken);

        // When
        await expired.DisposeAsync();
        var refused = await third.TryAcquireAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(current);
        Assert.Null(refused);
    }

    private RedisRefreshLock CreateLock(int leaseSeconds = 30) =>
        new(
            redis.Connection,
            Options.Create(new DistributedCacheOptions
            {
                InstanceName = _instanceName,
                LockLeaseSeconds = leaseSeconds
            }),
            TimeProvider.System,
            NullLogger<RedisRefreshLock>.Instance);
}
