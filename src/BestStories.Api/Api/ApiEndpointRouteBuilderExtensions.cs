using System.Globalization;
using BestStories.Api.Models;
using BestStories.Api.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BestStories.Api.Api;

public static class ApiEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthResponse
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthResponse
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapStoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/stories")
            .WithTags("Stories");

        group.MapGet("/best", GetBestStories)
            .WithName("GetBestStories")
            .WithSummary("Gets the best Hacker News stories by score.")
            .WithDescription(
                $"Returns up to n stories, with n between 1 and {StoryLimits.MaximumCount}.");

        return endpoints;
    }

    private static async Task<IResult> GetBestStories(
        HttpRequest request,
        IStoryProvider storyProvider,
        CancellationToken cancellationToken)
    {
        if (!request.Query.TryGetValue("n", out var values) ||
            values.Count != 1 ||
            !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count is < 1 or > StoryLimits.MaximumCount)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid story count.",
                detail: $"Provide exactly one n value between 1 and {StoryLimits.MaximumCount}.");
        }

        try
        {
            var stories = await storyProvider.GetBestStoriesAsync(count, cancellationToken);
            return Results.Ok(stories);
        }
        catch (StoryDataUnavailableException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable.",
                detail: "The service is not ready to provide story data.");
        }
    }

    private static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        if (report.Status != HealthStatus.Healthy)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable.",
                detail: "The service is not ready to receive requests.")
                .ExecuteAsync(context);
        }

        return Results.Json(new { status = "healthy" }).ExecuteAsync(context);
    }
}
