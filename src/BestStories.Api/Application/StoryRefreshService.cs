using BestStories.Api.Configuration;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Application;

/// <summary>
/// In automatic mode, loads the snapshot at startup (retrying with exponential backoff until it
/// succeeds) and then refreshes it every <see cref="StoryCacheOptions.RefreshInterval"/>.
/// </summary>
public sealed partial class StoryRefreshService(
    IStoryProvider storyProvider,
    IOptions<StoryCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<StoryRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.AutomaticRefresh)
        {
            return;
        }

        try
        {
            await LoadInitialSnapshotAsync(settings, stoppingToken);

            using var timer = new PeriodicTimer(settings.RefreshInterval, timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TryRefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down.
        }
    }

    private async Task LoadInitialSnapshotAsync(StoryCacheOptions settings, CancellationToken stoppingToken)
    {
        var maximumDelay = TimeSpan.FromSeconds(settings.InitialLoadMaxRetryDelaySeconds);
        var delay = InitialRetryDelay < maximumDelay ? InitialRetryDelay : maximumDelay;

        while (!await TryRefreshAsync(stoppingToken))
        {
            LogInitialLoadRetry(logger, delay.TotalSeconds);
            await Task.Delay(delay, timeProvider, stoppingToken);

            var doubled = delay * 2;
            delay = doubled < maximumDelay ? doubled : maximumDelay;
        }
    }

    private async Task<bool> TryRefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            await storyProvider.RefreshAsync(stoppingToken);
            return true;
        }
        catch (StoryDataUnavailableException)
        {
            // The provider has already logged the failure and kept the previous snapshot.
            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The initial story load failed; retrying in {DelaySeconds} seconds.")]
    private static partial void LogInitialLoadRetry(ILogger logger, double delaySeconds);
}
