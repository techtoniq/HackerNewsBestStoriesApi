using System.Net;
using BestStories.Api.Integrations.HackerNews.V0;
using BestStories.Api.Models;
using Moq;
using Moq.Protected;

namespace BestStories.Api.UnitTests;

public sealed class HackerNewsV0ClientTests
{
    [Fact]
    public async Task GivenACompleteHackerNewsStory_WhenStoryDetailsAreFetched_ThenResponseFieldsAreMapped()
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
        Uri? requestedUri = null;
        var client = CreateClient(json, onRequest: uri => requestedUri = uri);
        var hackerNewsClient = new HackerNewsV0Client(client);

        // When
        var story = await hackerNewsClient.GetStoryAsync(42, TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(story);
        Assert.Equal("A story", story.Title);
        Assert.Equal("https://example.com/story", story.Uri);
        Assert.Equal("author", story.PostedBy);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1570887781), story.Time);
        Assert.Equal(1716, story.Score);
        Assert.Equal(572, story.CommentCount);
        Assert.Equal(new Uri(HackerNewsV0Client.ApiBaseAddress, "item/42.json"), requestedUri);
    }

    [Fact]
    public async Task GivenAStoryWithoutUrlOrCommentCount_WhenStoryDetailsAreFetched_ThenItemUrlAndZeroCommentsAreUsed()
    {
        // Given
        const string json = """
            {
              "id": 42,
              "title": "Ask HN",
              "by": "author",
              "time": 1570887781,
              "score": 12
            }
            """;
        var hackerNewsClient = new HackerNewsV0Client(CreateClient(json));

        // When
        var story = await hackerNewsClient.GetStoryAsync(42, TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(story);
        Assert.Equal("https://news.ycombinator.com/item?id=42", story.Uri);
        Assert.Equal(0, story.CommentCount);
    }

    [Fact]
    public async Task GivenDuplicateBestStoryIds_WhenTheListIsFetched_ThenOnlyDistinctIdsAreReturned()
    {
        // Given
        Uri? requestedUri = null;
        var hackerNewsClient = new HackerNewsV0Client(
            CreateClient("[42, 7, 42]", onRequest: uri => requestedUri = uri));

        // When
        var ids = await hackerNewsClient.GetBestStoryIdsAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([42, 7], ids);
        Assert.Equal(new Uri(HackerNewsV0Client.ApiBaseAddress, "beststories.json"), requestedUri);
    }

    [Theory]
    [InlineData("[0, 42]")]
    [InlineData("[-1, 42]")]
    public async Task GivenInvalidBestStoryIds_WhenTheListIsFetched_ThenInvalidDataIsReported(string json)
    {
        // Given
        var hackerNewsClient = new HackerNewsV0Client(CreateClient(json));

        // When
        var exception = await Record.ExceptionAsync(
            () => hackerNewsClient.GetBestStoryIdsAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    [Fact]
    public async Task GivenTooManyBestStoryIds_WhenTheListIsFetched_ThenInvalidDataIsReported()
    {
        // Given
        var json = $"[{string.Join(",", Enumerable.Range(1, StoryLimits.MaximumCount + 1))}]";
        var hackerNewsClient = new HackerNewsV0Client(CreateClient(json));

        // When
        var exception = await Record.ExceptionAsync(
            () => hackerNewsClient.GetBestStoryIdsAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    [Fact]
    public async Task GivenAnUnexpectedItemId_WhenStoryDetailsAreFetched_ThenInvalidDataIsReported()
    {
        // Given
        const string json = """
            {
              "id": 41,
              "title": "A story",
              "by": "author",
              "time": 1570887781,
              "score": 12
            }
            """;
        var hackerNewsClient = new HackerNewsV0Client(CreateClient(json));

        // When
        var exception = await Record.ExceptionAsync(
            () => hackerNewsClient.GetStoryAsync(42, TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    [Theory]
    [InlineData("""{"id":42,"title":"A story","by":"author","score":12}""")]
    [InlineData("""{"id":42,"title":"A story","by":"author","time":9223372036854775807,"score":12}""")]
    public async Task GivenIncompleteOrInvalidStoryDetails_WhenStoryIsFetched_ThenInvalidDataIsReported(
        string json)
    {
        // Given
        var hackerNewsClient = new HackerNewsV0Client(CreateClient(json));

        // When
        var exception = await Record.ExceptionAsync(
            () => hackerNewsClient.GetStoryAsync(42, TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<InvalidDataException>(exception);
    }

    [Theory]
    [InlineData("""{"id":42,"deleted":true}""")]
    [InlineData("""{"id":42,"dead":true}""")]
    public async Task GivenADeletedOrDeadStory_WhenStoryDetailsAreFetched_ThenNoStoryIsReturned(string json)
    {
        // Given
        var hackerNewsClient = new HackerNewsV0Client(CreateClient(json));

        // When
        var story = await hackerNewsClient.GetStoryAsync(42, TestContext.Current.CancellationToken);

        // Then
        Assert.Null(story);
    }

    [Fact]
    public async Task GivenTheUpstreamReturnsAnError_WhenStoryDetailsAreFetched_ThenTheHttpFailureIsPropagated()
    {
        // Given
        var hackerNewsClient = new HackerNewsV0Client(CreateClient("{}", HttpStatusCode.BadGateway));

        // When
        var exception = await Record.ExceptionAsync(
            () => hackerNewsClient.GetStoryAsync(42, TestContext.Current.CancellationToken));

        // Then
        Assert.IsType<HttpRequestException>(exception);
    }

    private static HttpClient CreateClient(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Action<Uri>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                onRequest?.Invoke(request.RequestUri!))
            .ReturnsAsync((HttpRequestMessage _, CancellationToken _) =>
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
                });

        return new HttpClient(handler.Object)
        {
            BaseAddress = HackerNewsV0Client.ApiBaseAddress
        };
    }
}
