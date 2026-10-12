using BestStories.Api.Application;
using BestStories.Api.Domain;

namespace BestStories.Api.UnitTests.TestSupport;

/// <summary>
/// An in-memory <see cref="IHackerNewsClient"/> that counts requests, tracks item-request
/// concurrency and can hold best-story list requests until the test releases them.
/// </summary>
internal sealed class FakeHackerNewsClient : IHackerNewsClient
{
    private TaskCompletionSource? _listGate;
    private TaskCompletionSource _listRequested = NewSignal();
    private int _listRequests;
    private int _storyRequests;
    private int _activeStoryRequests;
    private int _maximumConcurrentStoryRequests;

    public IReadOnlyList<int> Ids { get; set; } = [1];

    /// <summary>Creates the story for an ID. Return <c>null</c> for a deleted story or throw to fail the item.</summary>
    public Func<int, Story?> StoryFactory { get; set; } = id => TestStories.Create(id);

    public Exception? ListFailure { get; set; }

    public TimeSpan StoryDelay { get; set; } = TimeSpan.Zero;

    public int ListRequests => Volatile.Read(ref _listRequests);

    public int StoryRequests => Volatile.Read(ref _storyRequests);

    public int MaximumConcurrentStoryRequests => Volatile.Read(ref _maximumConcurrentStoryRequests);

    /// <summary>Completes when a list request starts after the last call to <see cref="HoldListResponses"/>.</summary>
    public Task ListRequested => _listRequested.Task;

    public void HoldListResponses()
    {
        _listRequested = NewSignal();
        _listGate = NewSignal();
    }

    public void ReleaseListResponses() => _listGate?.TrySetResult();

    public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listRequests);
        _listRequested.TrySetResult();

        if (_listGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (ListFailure is { } failure)
        {
            throw failure;
        }

        return Ids;
    }

    public async Task<Story?> GetStoryAsync(int id, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _storyRequests);
        UpdateMaximumConcurrency(Interlocked.Increment(ref _activeStoryRequests));

        try
        {
            if (StoryDelay > TimeSpan.Zero)
            {
                await Task.Delay(StoryDelay, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            return StoryFactory(id);
        }
        finally
        {
            Interlocked.Decrement(ref _activeStoryRequests);
        }
    }

    private void UpdateMaximumConcurrency(int active)
    {
        int previous;
        do
        {
            previous = Volatile.Read(ref _maximumConcurrentStoryRequests);
            if (active <= previous)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maximumConcurrentStoryRequests, active, previous) != previous);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
