using BestStories.Api.Domain;

namespace BestStories.Api.Application;

public interface IStoryProvider
{
    /// <summary>
    /// Gets a snapshot that is within the configured maximum staleness, refreshing it first
    /// when necessary.
    /// </summary>
    /// <exception cref="StoryDataUnavailableException">No acceptable snapshot is available.</exception>
    Task<StorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs a scheduled refresh. It is skipped when another instance holds the refresh lock or
    /// has published a snapshot within the current refresh interval.
    /// </summary>
    /// <exception cref="StoryDataUnavailableException">The refresh failed.</exception>
    Task RefreshAsync(CancellationToken cancellationToken);
}
