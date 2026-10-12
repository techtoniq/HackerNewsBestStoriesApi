namespace BestStories.Api.Services;

public interface IDistributedRefreshLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
}
