namespace BestStories.Api.Application;

/// <summary>Ensures only one instance refreshes the shared snapshot at a time.</summary>
public interface IDistributedRefreshLock
{
    /// <summary>
    /// Tries to take the lock without waiting. Returns a lease to dispose when the refresh is
    /// finished, or <c>null</c> when another holder has the lock.
    /// </summary>
    Task<IRefreshLease?> TryAcquireAsync(CancellationToken cancellationToken);
}

/// <summary>A held refresh lock.</summary>
/// <remarks>
/// Disposing the lease releases the lock. Disposal never throws: a lease that cannot be released
/// expires on its own, and a failure to release must not turn a completed refresh into a failed one.
/// </remarks>
public interface IRefreshLease : IAsyncDisposable
{
    /// <summary>
    /// Cancelled when the holder can no longer be sure it owns the lock, for example because the
    /// lease expired or another instance took it over. The refresh should stop so that two
    /// instances do not refresh at once.
    /// </summary>
    CancellationToken LeaseLost { get; }
}
