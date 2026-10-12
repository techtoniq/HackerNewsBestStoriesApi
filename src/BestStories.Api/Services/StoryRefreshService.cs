using Microsoft.Extensions.Options;

namespace BestStories.Api.Services;

public sealed class StoryRefreshService(
    IStoryProvider storyProvider,
    IOptions<StoryCacheOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutomaticRefresh)
        {
            return;
        }

        await RefreshSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.RefreshIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await storyProvider.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StoryDataUnavailableException)
        {
        }
    }
}
