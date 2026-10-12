using BestStories.Api.Domain;

namespace BestStories.Api.Application;

/// <summary>The upstream source of best stories.</summary>
public interface IHackerNewsClient
{
    /// <summary>
    /// Gets the IDs of the current best stories, distinct and in upstream order, capped at
    /// <see cref="StoryLimits.MaximumCount"/>.
    /// </summary>
    Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a story, or <c>null</c> when it has been deleted or is dead.
    /// </summary>
    /// <exception cref="InvalidDataException">The upstream item is malformed.</exception>
    Task<Story?> GetStoryAsync(int id, CancellationToken cancellationToken);
}
