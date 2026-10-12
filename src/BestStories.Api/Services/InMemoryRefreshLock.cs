namespace BestStories.Api.Services;

public sealed class InMemoryRefreshLock : IDistributedRefreshLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAsyncDisposable? lease = _semaphore.Wait(0) ? new Lease(_semaphore) : null;
        return Task.FromResult(lease);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
