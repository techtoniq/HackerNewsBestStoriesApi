using System.Globalization;
using System.Text.Json.Serialization;
using BestStories.Api.Application;
using BestStories.Api.Domain;

namespace BestStories.Api.Integrations.HackerNews.V0;

/// <summary>Reads best stories from version 0 of the Hacker News Firebase API.</summary>
/// <remarks>
/// Transient failures, timeouts and circuit breaking are handled by the resilience pipeline
/// registered on the typed <see cref="HttpClient"/>, not here.
/// </remarks>
public sealed partial class HackerNewsV0Client(
    HttpClient httpClient,
    ILogger<HackerNewsV0Client> logger) : IHackerNewsClient
{
    public static readonly Uri ApiBaseAddress = new("https://hacker-news.firebaseio.com/v0/");

    public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await httpClient.GetFromJsonAsync<int[]>("beststories.json", cancellationToken)
            ?? throw new InvalidDataException("Hacker News returned an empty best-stories response.");

        var validIds = ids.Where(id => id > 0).Distinct().ToArray();
        if (validIds.Length < ids.Length)
        {
            LogInvalidOrDuplicateIds(logger, ids.Length - validIds.Length);
        }

        if (validIds.Length > StoryLimits.MaximumCount)
        {
            LogListTruncated(logger, validIds.Length, StoryLimits.MaximumCount);
            validIds = validIds[..StoryLimits.MaximumCount];
        }

        return validIds;
    }

    public async Task<Story?> GetStoryAsync(int id, CancellationToken cancellationToken)
    {
        var item = await httpClient.GetFromJsonAsync<HackerNewsV0Item>(
                string.Create(CultureInfo.InvariantCulture, $"item/{id}.json"),
                cancellationToken)
            ?? throw new InvalidDataException($"Hacker News returned no item for story {id}.");

        if (item.Id != id)
        {
            throw new InvalidDataException($"Hacker News returned an unexpected item for story {id}.");
        }

        if (item.Deleted || item.Dead)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(item.Title) ||
            string.IsNullOrWhiteSpace(item.By) ||
            item.Time is null ||
            item.Score is null)
        {
            throw new InvalidDataException($"Hacker News returned incomplete details for story {id}.");
        }

        DateTimeOffset postedAt;
        try
        {
            postedAt = DateTimeOffset.FromUnixTimeSeconds(item.Time.Value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException($"Hacker News returned an invalid time for story {id}.", exception);
        }

        var url = GetWebUrl(item.Url, id);

        return new Story(
            id,
            item.Title,
            url,
            item.By,
            postedAt,
            item.Score.Value,
            item.Descendants ?? 0);
    }

    /// <summary>
    /// Returns the story's own URL when it is an absolute <c>http</c> or <c>https</c> URL, and
    /// otherwise its Hacker News item page. Anything else (<c>javascript:</c>, <c>data:</c>, relative
    /// or malformed values) would be unsafe for clients that render <c>uri</c> as a link.
    /// </summary>
    private string GetWebUrl(string? url, int id)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ItemPageUrl(id);
        }

        // The original string is returned unchanged; only its scheme and shape are checked.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return url;
        }

        LogUnsafeUrl(logger, id);
        return ItemPageUrl(id);
    }

    private static string ItemPageUrl(int id) =>
        string.Create(CultureInfo.InvariantCulture, $"https://news.ycombinator.com/item?id={id}");

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Story {StoryId} has a URL that is not an absolute HTTP or HTTPS URL; using its Hacker News page instead.")]
    private static partial void LogUnsafeUrl(ILogger logger, int storyId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ignored {IgnoredCount} non-positive or duplicate IDs in the Hacker News best-stories list.")]
    private static partial void LogInvalidOrDuplicateIds(ILogger logger, int ignoredCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Hacker News returned {ReturnedCount} best-story IDs; using the first {MaximumCount}.")]
    private static partial void LogListTruncated(ILogger logger, int returnedCount, int maximumCount);

    private sealed class HackerNewsV0Item
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("by")]
        public string? By { get; init; }

        [JsonPropertyName("time")]
        public long? Time { get; init; }

        [JsonPropertyName("score")]
        public int? Score { get; init; }

        [JsonPropertyName("descendants")]
        public int? Descendants { get; init; }

        [JsonPropertyName("deleted")]
        public bool Deleted { get; init; }

        [JsonPropertyName("dead")]
        public bool Dead { get; init; }
    }
}
