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
/// Renewal and release both check an owner token, so an instance whose lease has expired can
/// never extend or delete a lease that another instance has since acquired.
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

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var leaseDuration = _options.Value.LockLease;
        var ownerToken = Guid.NewGuid().ToString("N");
        var acquired = await _database.StringSetAsync(Key, ownerToken, leaseDuration, When.NotExists);

        return acquired
            ? new Lease(this, ownerToken, leaseDuration)
            : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not renew the story refresh lock; retrying.")]
    private static partial void LogRenewalFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The story refresh lock expired before it could be renewed; another instance may now refresh concurrently.")]
    private static partial void LogLeaseLost(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not release the story refresh lock; it will expire automatically.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);

    private sealed class Lease : IAsyncDisposable
    {
        private readonly RedisRefreshLock _owner;
        private readonly string _ownerToken;
        private readonly TimeSpan _leaseDuration;
        private readonly CancellationTokenSource _stopRenewal = new();
        private readonly Task _renewal;
        private int _disposed;

        public Lease(RedisRefreshLock owner, string ownerToken, TimeSpan leaseDuration)
        {
            _owner = owner;
            _ownerToken = ownerToken;
            _leaseDuration = leaseDuration;
            _renewal = RenewUntilStoppedAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _stopRenewal.CancelAsync();
            await _renewal;
            _stopRenewal.Dispose();

            try
            {
                await _owner._database.ScriptEvaluateAsync(ReleaseScript, [_owner.Key], [_ownerToken]);
            }
            catch (RedisException exception)
            {
                LogReleaseFailed(_owner._logger, exception);
            }
        }

        private async Task RenewUntilStoppedAsync()
        {
            using var timer = new PeriodicTimer(_leaseDuration / 3, _owner._timeProvider);
            try
            {
                while (await timer.WaitForNextTickAsync(_stopRenewal.Token))
                {
                    try
                    {
                        var renewed = (long)await _owner._database.ScriptEvaluateAsync(
                            RenewScript,
                            [_owner.Key],
                            [_ownerToken, (long)_leaseDuration.TotalMilliseconds]);

                        if (renewed == 0)
                        {
                            LogLeaseLost(_owner._logger);
                            return;
                        }
                    }
                    catch (RedisException exception)
                    {
                        LogRenewalFailed(_owner._logger, exception);
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
