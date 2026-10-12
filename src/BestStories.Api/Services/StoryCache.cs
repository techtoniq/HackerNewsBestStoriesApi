using System.Diagnostics;
using BestStories.Api.Models;

namespace BestStories.Api.Services;

public sealed class StoryCache : IStoryCache
{
    private CacheState? _snapshot;

    public Task<StoryCacheSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref _snapshot);
        return Task.FromResult<StoryCacheSnapshot?>(
            snapshot is null
                ? null
                : new StoryCacheSnapshot(
                    snapshot.Stories,
                    DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(snapshot.PublishedTimestamp)));
    }

    public Task SetSnapshotAsync(
        IReadOnlyList<StoryResponse> stories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sortedStories = Array.AsReadOnly(stories.OrderByDescending(story => story.Score).ToArray());
        Interlocked.Exchange(ref _snapshot, new CacheState(sortedStories, Stopwatch.GetTimestamp()));
        return Task.CompletedTask;
    }

    private sealed record CacheState(IReadOnlyList<StoryResponse> Stories, long PublishedTimestamp);
}
