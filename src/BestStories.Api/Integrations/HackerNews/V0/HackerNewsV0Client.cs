using System.Text.Json;
using System.Text.Json.Serialization;
using BestStories.Api.Models;
using BestStories.Api.Services;

namespace BestStories.Api.Integrations.HackerNews.V0;

public sealed class HackerNewsV0Client(HttpClient httpClient) : IHackerNewsClient
{
    public const int RequestTimeoutSeconds = 15;
    public static readonly Uri ApiBaseAddress = new("https://hacker-news.firebaseio.com/v0/");

    public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await httpClient.GetFromJsonAsync<int[]>("beststories.json", cancellationToken)
            ?? throw new InvalidDataException("Hacker News returned an empty best-stories response.");

        if (ids.Length > StoryLimits.MaximumCount || ids.Any(id => id <= 0))
        {
            throw new InvalidDataException("Hacker News returned an invalid best-stories list.");
        }

        return ids.Distinct().ToArray();
    }

    public async Task<StoryResponse?> GetStoryAsync(int id, CancellationToken cancellationToken)
    {
        var item = await httpClient.GetFromJsonAsync<HackerNewsV0Item>($"item/{id}.json", cancellationToken)
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

        DateTimeOffset time;
        try
        {
            time = DateTimeOffset.FromUnixTimeSeconds(item.Time.Value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException($"Hacker News returned an invalid time for story {id}.", exception);
        }

        var uri = string.IsNullOrWhiteSpace(item.Url)
            ? $"https://news.ycombinator.com/item?id={id}"
            : item.Url;

        return new StoryResponse(
            item.Title,
            uri,
            item.By,
            time,
            item.Score.Value,
            item.Descendants ?? 0);
    }

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
