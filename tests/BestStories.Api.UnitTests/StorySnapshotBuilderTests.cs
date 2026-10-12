using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BestStories.Api.UnitTests;

public sealed class StorySnapshotBuilderTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public async Task GivenStoriesWithDifferentScores_WhenTheSnapshotIsBuilt_ThenStoriesAreOrderedByScoreThenId()
    {
        // Given
        var client = new FakeHackerNewsClient
        {
            Ids = [1, 3, 2],
            StoryFactory = id => TestStories.Create(id, score: id == 1 ? 10 : 30)
        };

        // When
        var snapshot = await CreateBuilder(client).BuildAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([2, 3, 1], snapshot.Stories.Select(story => story.Id));
        Assert.Equal(_timeProvider.GetUtcNow(), snapshot.PublishedAt);
    }

    [Fact]
    public async Task GivenADeletedStory_WhenTheSnapshotIsBuilt_ThenItIsLeftOut()
    {
        // Given
        var client = new FakeHackerNewsClient
        {
            Ids = [1, 2, 3],
            StoryFactory = id => id == 2 ? null : TestStories.Create(id)
        };

        // When
        var snapshot = await CreateBuilder(client).BuildAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([3, 1], snapshot.Stories.Select(story => story.Id));
    }

    [Fact]
    public async Task GivenFailuresWithinTheAllowedRatio_WhenTheSnapshotIsBuilt_ThenTheFailedStoriesAreLeftOut()
    {
        // Given
        var client = new FakeHackerNewsClient
        {
            Ids = Enumerable.Range(1, 20).ToArray(),
            StoryFactory = id => id is 5 or 6
                ? throw new HttpRequestException("Upstream failure.")
                : TestStories.Create(id)
        };

        // When
        var snapshot = await CreateBuilder(client, minimumSuccessRatio: 0.9)
            .BuildAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(18, snapshot.Stories.Count);
        Assert.DoesNotContain(snapshot.Stories, story => story.Id is 5 or 6);
    }

    [Fact]
    public async Task GivenMoreFailuresThanAllowed_WhenTheSnapshotIsBuilt_ThenItFailsWithoutRequestingEveryStory()
    {
        // Given
        var client = new FakeHackerNewsClient
        {
            Ids = Enumerable.Range(1, 100).ToArray(),
            StoryFactory = _ => throw new HttpRequestException("Upstream failure.")
        };

        // When
        var exception = await Record.ExceptionAsync(
            () => CreateBuilder(client, minimumSuccessRatio: 0.95, maxConcurrentRequests: 2)
                .BuildAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
        Assert.InRange(client.StoryRequests, 6, 20);
    }

    [Fact]
    public async Task GivenMoreStoriesThanTheConcurrencyLimit_WhenTheSnapshotIsBuilt_ThenItemRequestsStayBounded()
    {
        // Given
        var client = new FakeHackerNewsClient
        {
            Ids = Enumerable.Range(1, 8).ToArray(),
            StoryDelay = TimeSpan.FromMilliseconds(25)
        };

        // When
        await CreateBuilder(client, maxConcurrentRequests: 2).BuildAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(8, client.StoryRequests);
        Assert.Equal(2, client.MaximumConcurrentStoryRequests);
    }

    [Fact]
    public async Task GivenAnEmptyIdList_WhenTheSnapshotIsBuilt_ThenInvalidDataIsReported()
    {
        // Given
        var client = new FakeHackerNewsClient { Ids = [] };

        // When
        var exception = await Record.ExceptionAsync(
            () => CreateBuilder(client).BuildAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    [Fact]
    public async Task GivenACancelledToken_WhenTheSnapshotIsBuilt_ThenCancellationIsPropagated()
    {
        // Given
        var client = new FakeHackerNewsClient { Ids = Enumerable.Range(1, 10).ToArray() };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // When
        var exception = await Record.ExceptionAsync(
            () => CreateBuilder(client).BuildAsync(cancellation.Token));

        // Then
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    [Theory]
    [InlineData(500, 0.95, 25)]
    [InlineData(10, 0.9, 1)]
    [InlineData(20, 1.0, 0)]
    [InlineData(3, 0.5, 1)]
    public void GivenAnItemCountAndRatio_WhenTheFailureAllowanceIsCalculated_ThenItRoundsDown(
        int itemCount,
        double ratio,
        int expected)
    {
        // When
        var allowance = StorySnapshotBuilder.MaximumFailures(itemCount, ratio);

        // Then
        Assert.Equal(expected, allowance);
    }

    private StorySnapshotBuilder CreateBuilder(
        FakeHackerNewsClient client,
        double minimumSuccessRatio = 1.0,
        int maxConcurrentRequests = 4) =>
        new(
            client,
            Options.Create(new StoryCacheOptions
            {
                MinimumSuccessRatio = minimumSuccessRatio,
                MaxConcurrentRequests = maxConcurrentRequests
            }),
            _timeProvider,
            NullLogger<StorySnapshotBuilder>.Instance);
}
