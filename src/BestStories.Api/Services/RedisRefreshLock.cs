using StackExchange.Redis;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Services;

public sealed class RedisRefreshLock(
    IConnectionMultiplexer connectionMultiplexer,
    IOptions<DistributedCacheOptions> options,
    ILogger<RedisRefreshLock> logger) : IDistributedRefreshLock
{
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();
    private readonly string _key = $"{options.Value.InstanceName}best-stories:refresh-lock";
    private readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(options.Value.LockLeaseSeconds);

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownerToken = Guid.NewGuid().ToString("N");
        var acquired = await _database.StringSetAsync(
            _key,
            ownerToken,
            _leaseDuration,
            When.NotExists);

        return acquired ? new Lease(_database, _key, ownerToken, logger) : null;
    }

    private sealed class Lease(
        IDatabase database,
        string key,
        string ownerToken,
        ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await database.ScriptEvaluateAsync(
                    ReleaseScript,
                    [key],
                    [ownerToken]);
            }
            catch (RedisException exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not release the distributed story refresh lock; it will expire automatically.");
            }
        }
    }
}
