namespace BestStories.Api.Application;

/// <summary>Ensures only one instance refreshes the shared snapshot at a time.</summary>
public interface IDistributedRefreshLock
{
    /// <summary>
    /// Tries to take the lock without waiting. Returns a lease to dispose when the refresh is
    /// finished, or <c>null</c> when another holder has the lock.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
}
