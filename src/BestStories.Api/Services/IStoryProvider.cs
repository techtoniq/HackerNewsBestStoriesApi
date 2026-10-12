using BestStories.Api.Models;

namespace BestStories.Api.Services;

public interface IStoryProvider
{
    Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(
        int count,
        CancellationToken cancellationToken);

    Task RefreshAsync(CancellationToken cancellationToken);
}
