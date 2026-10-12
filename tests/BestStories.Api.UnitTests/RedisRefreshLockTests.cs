using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace BestStories.Api.UnitTests;

/// <summary>
/// Failure handling of the Redis refresh lock. The happy paths run against a real Redis in the
/// component tests; these use <see cref="FakeRedis"/> to produce failures on demand.
/// </summary>
public sealed class RedisRefreshLockTests
{
    private const int LeaseSeconds = 30;
    private static readonly TimeSpan RenewalPeriod = TimeSpan.FromSeconds(LeaseSeconds / 3);

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeRedis _redis = new();

    [Fact]
    public async Task GivenARenewalTimesOut_WhenTheLeaseIsDisposed_ThenItIsReleasedWithoutThrowing()
    {
        // Given
        _redis.Renew = () => throw new TimeoutException("Simulated RedisTimeoutException.");
        var lease = await AcquireAsync();
        await AdvanceToRenewalAsync(1);

        // When
        var exception = await Record.ExceptionAsync(async () => await lease.DisposeAsync());

        // Then
        Assert.Null(exception);
        Assert.Equal(1, _redis.Releases);
    }

    [Fact]
    public async Task GivenAReleaseTimesOut_WhenTheLeaseIsDisposed_ThenNoExceptionEscapes()
    {
        // Given
        _redis.Release = () => throw new TimeoutException("Simulated RedisTimeoutException.");
        var lease = await AcquireAsync();

        // When
        var exception = await Record.ExceptionAsync(async () => await lease.DisposeAsync());

        // Then
        Assert.Null(exception);
        Assert.Equal(1, _redis.Releases);
    }

    [Fact]
    public async Task GivenARedisConnectionFailureOnRelease_WhenTheLeaseIsDisposed_ThenNoExceptionEscapes()
    {
        // Given
        _redis.Release = () => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Simulated.");
        var lease = await AcquireAsync();

        // When
        var exception = await Record.ExceptionAsync(async () => await lease.DisposeAsync());

        // Then
        Assert.Null(exception);
    }

    [Fact]
    public async Task GivenRenewalsKeepFailing_WhenAFullLeasePeriodPasses_ThenTheLeaseIsReportedLost()
    {
        // Given
        _redis.Renew = () => throw new TimeoutException("Simulated RedisTimeoutException.");
        await using var lease = await AcquireAsync();

        // When
        await AdvanceToRenewalAsync(1);
        await AdvanceToRenewalAsync(2);
        var lostBeforeTheLeaseRanOut = lease.LeaseLost.IsCancellationRequested;
        await AdvanceToRenewalAsync(3);

        // Then
        Assert.False(lostBeforeTheLeaseRanOut);
        await Eventually.HoldsAsync(() => lease.LeaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task GivenAnotherOwnerHoldsTheKey_WhenTheLeaseIsRenewed_ThenTheLeaseIsReportedLost()
    {
        // Given
        _redis.Renew = () => 0;
        await using var lease = await AcquireAsync();

        // When
        await AdvanceToRenewalAsync(1);

        // Then
        await Eventually.HoldsAsync(() => lease.LeaseLost.IsCancellationRequested);
    }

    [Fact]
    public async Task GivenSuccessfulRenewals_WhenSeveralLeasePeriodsPass_ThenTheLeaseIsKept()
    {
        // Given
        await using var lease = await AcquireAsync();

        // When
        for (var renewal = 1; renewal <= 6; renewal++)
        {
            await AdvanceToRenewalAsync(renewal);
        }

        // Then
        Assert.False(lease.LeaseLost.IsCancellationRequested);
    }

    private async Task<IRefreshLease> AcquireAsync()
    {
        var refreshLock = new RedisRefreshLock(
            _redis.Connection,
            Options.Create(new DistributedCacheOptions { LockLeaseSeconds = LeaseSeconds }),
            _timeProvider,
            NullLogger<RedisRefreshLock>.Instance);

        var lease = await refreshLock.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        return lease;
    }

    /// <summary>Advances the clock by one renewal period and waits for that renewal to be attempted.</summary>
    private async Task AdvanceToRenewalAsync(int expectedRenewals)
    {
        _timeProvider.Advance(RenewalPeriod);
        await Eventually.HoldsAsync(() => _redis.Renewals >= expectedRenewals);

        // Let the renewal loop finish handling the result before the next step.
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }
}
