using BestStories.Api.Application;

namespace BestStories.Api.Infrastructure.Caching;

/// <summary>A process-local refresh lock for single-instance deployments and tests.</summary>
public sealed class InMemoryRefreshLock : IDistributedRefreshLock, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAsyncDisposable? lease = _semaphore.Wait(0, cancellationToken) ? new Lease(_semaphore) : null;
        return Task.FromResult(lease);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

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
