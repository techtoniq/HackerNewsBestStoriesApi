using BestStories.Api.Application;

namespace BestStories.Api.Infrastructure.Caching;

/// <summary>A process-local refresh lock for single-instance deployments and tests.</summary>
public sealed class InMemoryRefreshLock : IDistributedRefreshLock, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task<IRefreshLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IRefreshLease? lease = _semaphore.Wait(0, cancellationToken) ? new Lease(_semaphore) : null;
        return Task.FromResult(lease);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IRefreshLease
    {
        private int _released;

        /// <summary>A process-local lease cannot be lost while the process is running.</summary>
        public CancellationToken LeaseLost => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
