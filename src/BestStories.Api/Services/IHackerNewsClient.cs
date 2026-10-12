using BestStories.Api.Models;

namespace BestStories.Api.Services;

public interface IHackerNewsClient
{
    Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken);

    Task<StoryResponse?> GetStoryAsync(int id, CancellationToken cancellationToken);
}
