using BestStories.Api.Application;
using BestStories.Api.Domain;

namespace BestStories.Api.Infrastructure.Caching;

/// <summary>A process-local snapshot store for single-instance deployments.</summary>
public sealed class InMemoryStoryCache : IStoryCache
{
    private StorySnapshot? _snapshot;

    public Task<StorySnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _snapshot));
    }

    public Task SetSnapshotAsync(StorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _snapshot, snapshot);
        return Task.CompletedTask;
    }
}
