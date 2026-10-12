using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BestStories.Api.IntegrationTests.TestSupport;

/// <summary>
/// Serves the Hacker News v0 routes the API uses, from in-memory data, as the primary handler
/// of the real typed HTTP client. Everything above the socket — the client, resilience
/// pipeline, snapshot builder, provider, cache and endpoints — runs for real.
/// </summary>
internal sealed partial class FakeHackerNewsApi
{
    public const long ExampleUnixTime = 1570887781;

    private readonly ConcurrentDictionary<int, string> _items = new();
    private int _listRequests;

    public IReadOnlyList<int> BestStoryIds { get; set; } = [];

    /// <summary>When set, every request waits for this task before responding.</summary>
    public Task? Gate { get; set; }

    public int ListRequests => Volatile.Read(ref _listRequests);

    public void AddStory(int id, string title, int score, string? url = null, int? descendants = null)
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["type"] = "story",
            ["title"] = title,
            ["by"] = "author",
            ["time"] = ExampleUnixTime,
            ["score"] = score
        };

        if (url is not null)
        {
            item["url"] = url;
        }

        if (descendants is not null)
        {
            item["descendants"] = descendants;
        }

        _items[id] = JsonSerializer.Serialize(item);
    }

    public void AddDeletedStory(int id) =>
        _items[id] = string.Create(CultureInfo.InvariantCulture, $$"""{"id":{{id}},"deleted":true}""");

    public HttpMessageHandler CreateHandler() => new Handler(this);

    [GeneratedRegex(@"/v0/item/(?<id>\d+)\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPath();

    private sealed class Handler(FakeHackerNewsApi api) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (api.Gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v0/beststories.json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref api._listRequests);
                return Json(JsonSerializer.Serialize(api.BestStoryIds));
            }

            var match = ItemPath().Match(path);
            if (match.Success)
            {
                var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
                return Json(api._items.TryGetValue(id, out var item) ? item : "null");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
