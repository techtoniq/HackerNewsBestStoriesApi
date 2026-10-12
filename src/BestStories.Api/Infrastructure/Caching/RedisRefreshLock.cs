using BestStories.Api.Application;
using BestStories.Api.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BestStories.Api.Infrastructure.Caching;

/// <summary>
/// A Redis lease (<c>SET NX PX</c>) that only one instance can hold. The holder renews the lease
/// every third of its duration while the refresh runs, so the lease can be short: if the holder
/// crashes, another instance can take over within one lease period.
/// </summary>
/// <remarks>
/// <para>
/// Renewal and release both check an owner token, so an instance whose lease has expired can
/// never extend or delete a lease that another instance has since acquired.
/// </para>
/// <para>
/// The holder signals <see cref="IRefreshLease.LeaseLost"/> when a renewal finds that another
/// owner holds the key, or when renewals have failed for a full lease period (the key has then
/// expired, whether or not Redis could tell us). Renewal and release failures of any kind,
/// including <see cref="RedisTimeoutException"/> (which is not a <see cref="RedisException"/>),
/// are logged and never escape: a lock problem must not fail a refresh that has already published.
/// </para>
/// </remarks>
public sealed partial class RedisRefreshLock(
    IConnectionMultiplexer connectionMultiplexer,
    IOptions<DistributedCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<RedisRefreshLock> logger) : IDistributedRefreshLock
{
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            return 0
        end
        """;

    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();
    private readonly IOptions<DistributedCacheOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger _logger = logger;

    internal string Key { get; } = $"{options.Value.InstanceName}best-stories:refresh-lock";

    public async Task<IRefreshLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var leaseDuration = _options.Value.LockLease;
        var ownerToken = Guid.NewGuid().ToString("N");

        // Measured from before the request, so the lease is never assumed to last longer than it does.
        var requestedAt = _timeProvider.GetTimestamp();
        var acquired = await _database.StringSetAsync(Key, ownerToken, leaseDuration, When.NotExists);

        return acquired
            ? new Lease(this, ownerToken, leaseDuration, requestedAt)
            : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not renew the story refresh lock; retrying.")]
    private static partial void LogRenewalFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The story refresh lock is held by another owner; abandoning the refresh.")]
    private static partial void LogLeaseTakenOver(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The story refresh lock could not be renewed within its lease period; abandoning the refresh.")]
    private static partial void LogLeaseExpired(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not release the story refresh lock; it will expire automatically.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);

    private sealed class Lease : IRefreshLease
    {
        private readonly RedisRefreshLock _owner;
        private readonly string _ownerToken;
        private readonly TimeSpan _leaseDuration;
        private readonly CancellationTokenSource _stopRenewal = new();
        private readonly CancellationTokenSource _lost = new();
        private readonly Task _renewal;
        private long _confirmedAtTimestamp;
        private int _disposed;

        public Lease(RedisRefreshLock owner, string ownerToken, TimeSpan leaseDuration, long acquiredAtTimestamp)
        {
            _owner = owner;
            _ownerToken = ownerToken;
            _leaseDuration = leaseDuration;
            _confirmedAtTimestamp = acquiredAtTimestamp;
            LeaseLost = _lost.Token;
            _renewal = RenewUntilStoppedAsync();
        }

        public CancellationToken LeaseLost { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await _stopRenewal.CancelAsync();
                await _renewal;
            }
            catch (Exception exception)
            {
                LogRenewalFailed(_owner._logger, exception);
            }

            try
            {
                await _owner._database.ScriptEvaluateAsync(ReleaseScript, [_owner.Key], [_ownerToken]);
            }
            catch (Exception exception)
            {
                LogReleaseFailed(_owner._logger, exception);
            }
            finally
            {
                _stopRenewal.Dispose();
                _lost.Dispose();
            }
        }

        private async Task RenewUntilStoppedAsync()
        {
            using var timer = new PeriodicTimer(_leaseDuration / 3, _owner._timeProvider);
            try
            {
                while (await timer.WaitForNextTickAsync(_stopRenewal.Token))
                {
                    var requestedAt = _owner._timeProvider.GetTimestamp();
                    try
                    {
                        var renewed = (long)await _owner._database.ScriptEvaluateAsync(
                            RenewScript,
                            [_owner.Key],
                            [_ownerToken, (long)_leaseDuration.TotalMilliseconds]);

                        if (renewed == 0)
                        {
                            LogLeaseTakenOver(_owner._logger);
                            await _lost.CancelAsync();
                            return;
                        }

                        _confirmedAtTimestamp = requestedAt;
                    }
                    catch (Exception exception) when (!_stopRenewal.IsCancellationRequested)
                    {
                        LogRenewalFailed(_owner._logger, exception);

                        if (_owner._timeProvider.GetElapsedTime(_confirmedAtTimestamp) >= _leaseDuration)
                        {
                            LogLeaseExpired(_owner._logger);
                            await _lost.CancelAsync();
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_stopRenewal.IsCancellationRequested)
            {
                // The lease is being released.
            }
        }
    }
}
