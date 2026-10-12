using BestStories.Api.Models;

namespace BestStories.Api.Services;

public interface IStoryCache
{
    Task<StoryCacheSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken);

    Task SetSnapshotAsync(IReadOnlyList<StoryResponse> stories, CancellationToken cancellationToken);
}
