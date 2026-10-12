using BestStories.Api.Domain;

namespace BestStories.Api.Application;

/// <summary>Stores the latest published snapshot, locally or shared between instances.</summary>
public interface IStoryCache
{
    Task<StorySnapshot?> GetSnapshotAsync(CancellationToken cancellationToken);

    Task SetSnapshotAsync(StorySnapshot snapshot, CancellationToken cancellationToken);
}
