using System.Text.Json;
using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.Domain;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Infrastructure.Caching;

/// <summary>
/// Shares the snapshot between instances through <see cref="IDistributedCache"/> (Redis in
/// production), with a short-lived local copy so that each request does not have to read and
/// deserialize the full snapshot from Redis.
/// </summary>
public sealed class RedisStoryCache(
    IDistributedCache distributedCache,
    IOptions<DistributedCacheOptions> options,
    TimeProvider timeProvider) : IStoryCache
{
    /// <summary>
    /// Versioned so that a deployment that changes the snapshot format does not read snapshots
    /// written by the previous version during a rolling update. Bump the suffix on any breaking
    /// change to <see cref="StorySnapshot"/> or <see cref="Story"/>.
    /// </summary>
    internal const string SnapshotKey = "best-stories:snapshot:v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private LocalCopy? _localCopy;

    public async Task<StorySnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var localCopy = Volatile.Read(ref _localCopy);
        if (localCopy is not null &&
            timeProvider.GetElapsedTime(localCopy.ReadAtTimestamp) < options.Value.LocalCacheDuration)
        {
            return localCopy.Snapshot;
        }

        var payload = await distributedCache.GetAsync(SnapshotKey, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        StorySnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<StorySnapshot>(payload, JsonOptions)
                ?? throw new InvalidDataException("The shared story snapshot was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The shared story snapshot was invalid.", exception);
        }

        Remember(snapshot);
        return snapshot;
    }

    public async Task SetSnapshotAsync(StorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        await distributedCache.SetAsync(SnapshotKey, payload, new DistributedCacheEntryOptions(), cancellationToken);
        Remember(snapshot);
    }

    private void Remember(StorySnapshot snapshot) =>
        Volatile.Write(ref _localCopy, new LocalCopy(snapshot, timeProvider.GetTimestamp()));

    private sealed record LocalCopy(StorySnapshot Snapshot, long ReadAtTimestamp);
}
