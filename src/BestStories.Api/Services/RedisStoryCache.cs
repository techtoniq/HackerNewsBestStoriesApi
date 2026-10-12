using System.Text.Json;
using BestStories.Api.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Services;

public sealed class RedisStoryCache(
    IDistributedCache distributedCache) : IStoryCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Key = "best-stories:snapshot";

    public async Task<StoryCacheSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var payload = await distributedCache.GetStringAsync(Key, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoryCacheSnapshot>(payload, JsonOptions)
                ?? throw new InvalidDataException("The distributed story-cache snapshot was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The distributed story-cache snapshot was invalid.", exception);
        }
    }

    public async Task SetSnapshotAsync(
        IReadOnlyList<StoryResponse> stories,
        CancellationToken cancellationToken)
    {
        var orderedStories = stories.OrderByDescending(story => story.Score).ToArray();
        var snapshot = new StoryCacheSnapshot(orderedStories, DateTimeOffset.UtcNow);
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);

        await distributedCache.SetStringAsync(
            Key,
            payload,
            new DistributedCacheEntryOptions(),
            cancellationToken);
    }
}
