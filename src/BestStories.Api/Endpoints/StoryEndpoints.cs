using System.Globalization;
using BestStories.Api.Application;
using BestStories.Api.Configuration;
using BestStories.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace BestStories.Api.Endpoints;

public static class StoryEndpoints
{
    public static IEndpointRouteBuilder MapStoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/stories")
            .WithTags("Stories")
            .RequireRateLimiting(ApiServiceCollectionExtensions.StoriesRateLimitPolicy);

        group.MapGet("/best", GetBestStoriesAsync)
            .WithName("GetBestStories")
            .WithSummary("Gets the best Hacker News stories by score.")
            .WithDescription(
                $"Returns up to n stories in descending score order, with n between 1 and {StoryLimits.MaximumCount}. " +
                "Responses carry an ETag and Cache-Control so clients and intermediaries can cache them.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    internal static async Task<Results<Ok<StoryResponse[]>, StatusCodeHttpResult, ProblemHttpResult>> GetBestStoriesAsync(
        HttpContext context,
        IStoryProvider storyProvider,
        IOptions<StoryCacheOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetCount(context.Request.Query, out var count))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid story count.",
                detail: $"Provide exactly one n value between 1 and {StoryLimits.MaximumCount}.");
        }

        var settings = options.Value;
        StorySnapshot snapshot;
        try
        {
            snapshot = await storyProvider.GetSnapshotAsync(cancellationToken);
        }
        catch (StoryDataUnavailableException)
        {
            var retryAfterSeconds = Math.Max(1, settings.FailureCooldownSeconds);
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable.",
                detail: "The service is not ready to provide story data.");
        }

        var entityTag = CreateEntityTag(snapshot, count);
        SetCachingHeaders(context.Response, snapshot, entityTag, settings, timeProvider.GetUtcNow());

        if (MatchesIfNoneMatch(context.Request, entityTag))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        var stories = snapshot.Stories
            .Take(count)
            .Select(StoryResponse.FromStory)
            .ToArray();

        return TypedResults.Ok(stories);
    }

    private static bool TryGetCount(IQueryCollection query, out int count)
    {
        count = 0;
        return query.TryGetValue("n", out var values) &&
            values.Count == 1 &&
            int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) &&
            count is >= 1 and <= StoryLimits.MaximumCount;
    }

    /// <summary>
    /// The response body is fully determined by the snapshot and n, so together they make a
    /// strong validator.
    /// </summary>
    private static EntityTagHeaderValue CreateEntityTag(StorySnapshot snapshot, int count) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"\"{snapshot.PublishedAt.ToUnixTimeMilliseconds():x}-{count}\""));

    private static void SetCachingHeaders(
        HttpResponse response,
        StorySnapshot snapshot,
        EntityTagHeaderValue entityTag,
        StoryCacheOptions settings,
        DateTimeOffset now)
    {
        // Let clients cache for whatever remains of the snapshot's freshness, capped so that a
        // refresh shows up promptly. A stale snapshot is served with max-age=0.
        var remaining = settings.FreshFor - snapshot.AgeAt(now);
        var maxAgeSeconds = Math.Clamp((int)remaining.TotalSeconds, 0, settings.ClientCacheMaxAgeSeconds);

        var headers = response.GetTypedHeaders();
        headers.ETag = entityTag;
        headers.LastModified = snapshot.PublishedAt;
        headers.CacheControl = new CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromSeconds(maxAgeSeconds)
        };
    }

    private static bool MatchesIfNoneMatch(HttpRequest request, EntityTagHeaderValue entityTag)
    {
        var ifNoneMatch = request.GetTypedHeaders().IfNoneMatch;
        return ifNoneMatch.Any(candidate =>
            candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(entityTag, useStrongComparison: false));
    }
}
