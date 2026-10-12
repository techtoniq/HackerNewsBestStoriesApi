using System.Net;
using BestStories.Api.Domain;
using BestStories.Api.Integrations.HackerNews.V0;
using BestStories.Api.UnitTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace BestStories.Api.UnitTests;

public sealed class HackerNewsV0ClientTests
{
    [Fact]
    public async Task GivenACompleteHackerNewsStory_WhenStoryDetailsAreFetched_ThenStoryFieldsAreMapped()
    {
        // Given
        const string json = """
            {
              "id": 42,
              "title": "A story",
              "url": "https://example.com/story",
              "by": "author",
              "time": 1570887781,
              "score": 1716,
              "descendants": 572
            }
            """;
        var handler = StubHttpMessageHandler.Json(json);
        var client = CreateClient(handler);

        // When
        var story = await client.GetStoryAsync(42, TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(story);
        Assert.Equal(42, story.Id);
        Assert.Equal("A story", story.Title);
        Assert.Equal("https://example.com/story", story.Url);
        Assert.Equal("author", story.PostedBy);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1570887781), story.PostedAt);
        Assert.Equal(1716, story.Score);
        Assert.Equal(572, story.CommentCount);
        Assert.Equal(new Uri(HackerNewsV0Client.ApiBaseAddress, "item/42.json"), Assert.Single(handler.RequestedUris));
    }

    [Fact]
    public async Task GivenAStoryWithoutUrlOrCommentCount_WhenStoryDetailsAreFetched_ThenItemUrlAndZeroCommentsAreUsed()
    {
        // Given
        const string json = """{"id":42,"title":"Ask HN","by":"author","time":1570887781,"score":12}""";
        var client = CreateClient(StubHttpMessageHandler.Json(json));

        // When
        var story = await client.GetStoryAsync(42, TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(story);
        Assert.Equal("https://news.ycombinator.com/item?id=42", story.Url);
        Assert.Equal(0, story.CommentCount);
    }

    [Fact]
    public async Task GivenDuplicateAndNonPositiveIds_WhenTheListIsFetched_ThenOnlyDistinctPositiveIdsAreReturnedInOrder()
    {
        // Given
        var handler = StubHttpMessageHandler.Json("[42, 0, 7, -1, 42]");
        var client = CreateClient(handler);

        // When
        var ids = await client.GetBestStoryIdsAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([42, 7], ids);
        Assert.Equal(new Uri(HackerNewsV0Client.ApiBaseAddress, "beststories.json"), Assert.Single(handler.RequestedUris));
    }

    [Fact]
    public async Task GivenMoreIdsThanTheMaximum_WhenTheListIsFetched_ThenTheFirstMaximumIdsAreReturned()
    {
        // Given
        var json = $"[{string.Join(",", Enumerable.Range(1, StoryLimits.MaximumCount + 1))}]";
        var client = CreateClient(StubHttpMessageHandler.Json(json));

        // When
        var ids = await client.GetBestStoryIdsAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(Enumerable.Range(1, StoryLimits.MaximumCount), ids);
    }

    [Fact]
    public async Task GivenANullListResponse_WhenTheListIsFetched_ThenInvalidDataIsReported()
    {
        // Given
        var client = CreateClient(StubHttpMessageHandler.Json("null"));

        // When
        var exception = await Record.ExceptionAsync(
            () => client.GetBestStoryIdsAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    [Theory]
    [InlineData("""{"id":41,"title":"A story","by":"author","time":1570887781,"score":12}""")]
    [InlineData("""{"id":42,"title":"A story","by":"author","score":12}""")]
    [InlineData("""{"id":42,"by":"author","time":1570887781,"score":12}""")]
    [InlineData("""{"id":42,"title":"A story","by":"author","time":9223372036854775807,"score":12}""")]
    [InlineData("null")]
    public async Task GivenUnexpectedIncompleteOrInvalidDetails_WhenStoryIsFetched_ThenInvalidDataIsReported(string json)
    {
        // Given
        var client = CreateClient(StubHttpMessageHandler.Json(json));

        // When
        var exception = await Record.ExceptionAsync(
            () => client.GetStoryAsync(42, TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    [Theory]
    [InlineData("""{"id":42,"deleted":true}""")]
    [InlineData("""{"id":42,"dead":true}""")]
    public async Task GivenADeletedOrDeadStory_WhenStoryDetailsAreFetched_ThenNoStoryIsReturned(string json)
    {
        // Given
        var client = CreateClient(StubHttpMessageHandler.Json(json));

        // When
        var story = await client.GetStoryAsync(42, TestContext.Current.CancellationToken);

        // Then
        Assert.Null(story);
    }

    [Fact]
    public async Task GivenTheUpstreamReturnsAnError_WhenStoryDetailsAreFetched_ThenTheHttpFailureIsPropagated()
    {
        // Given
        var client = CreateClient(StubHttpMessageHandler.Json("{}", HttpStatusCode.BadGateway));

        // When
        var exception = await Record.ExceptionAsync(
            () => client.GetStoryAsync(42, TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<HttpRequestException>(exception);
    }

    private static HackerNewsV0Client CreateClient(StubHttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = HackerNewsV0Client.ApiBaseAddress },
            NullLogger<HackerNewsV0Client>.Instance);
}
