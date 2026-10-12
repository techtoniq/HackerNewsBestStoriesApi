using System.Collections.Concurrent;
using BestStories.Api.Configuration;
using BestStories.Api.Domain;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Application;

/// <summary>
/// Fetches the current best stories from Hacker News and builds a score-ordered snapshot.
/// </summary>
/// <remarks>
/// Individual item failures are tolerated up to <see cref="StoryCacheOptions.MinimumSuccessRatio"/>;
/// the failed items are left out of the snapshot. Once more items have failed than the ratio
/// allows, the remaining requests are cancelled so an upstream outage does not cost a full set
/// of item requests on every attempt.
/// </remarks>
public sealed partial class StorySnapshotBuilder(
    IHackerNewsClient hackerNewsClient,
    IOptions<StoryCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<StorySnapshotBuilder> logger)
{
    public async Task<StorySnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var ids = await hackerNewsClient.GetBestStoryIdsAsync(cancellationToken);
        if (ids.Count == 0)
        {
            throw new InvalidDataException("Hacker News returned no best-story IDs.");
        }

        var maximumFailures = MaximumFailures(ids.Count, settings.MinimumSuccessRatio);
        var stories = new ConcurrentBag<Story>();
        var failures = 0;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await Parallel.ForEachAsync(
                ids,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = settings.MaxConcurrentRequests,
                    CancellationToken = abort.Token
                },
                async (id, itemCancellationToken) =>
                {
                    if (abort.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        var story = await hackerNewsClient.GetStoryAsync(id, itemCancellationToken);
                        if (story is not null)
                        {
                            stories.Add(story);
                        }
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (abort.IsCancellationRequested)
                        {
                            // The refresh has already failed; requests still in flight end here
                            // without being counted, whatever exception their cancellation produced.
                            return;
                        }

                        LogItemFailed(logger, exception, id);
                        if (Interlocked.Increment(ref failures) > maximumFailures)
                        {
                            await abort.CancelAsync();
                        }
                    }
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The abort token was cancelled because too many items failed; reported below.
        }

        var failed = Volatile.Read(ref failures);
        if (failed > maximumFailures)
        {
            throw new InvalidDataException(
                $"Too many story requests failed: more than {maximumFailures} of {ids.Count} allowed.");
        }

        if (failed > 0)
        {
            StoryTelemetry.SkippedItems.Add(failed);
            LogItemsSkipped(logger, failed, ids.Count);
        }

        return StorySnapshot.Create(stories, timeProvider.GetUtcNow());
    }

    internal static int MaximumFailures(int itemCount, double minimumSuccessRatio)
    {
        // A small epsilon stops binary floating-point error (for example 1 - 0.9 = 0.0999…)
        // from rounding the allowance down by one.
        const double epsilon = 1e-9;
        return (int)Math.Floor((itemCount * (1 - minimumSuccessRatio)) + epsilon);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch Hacker News story {StoryId}.")]
    private static partial void LogItemFailed(ILogger logger, Exception exception, int storyId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Publishing the snapshot without {FailedCount} of {RequestedCount} stories that could not be fetched.")]
    private static partial void LogItemsSkipped(ILogger logger, int failedCount, int requestedCount);
}
