using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using BestStories.Api.Endpoints;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Configuration;

public static class ApiServiceCollectionExtensions
{
    public const string StoriesRateLimitPolicy = "stories";

    /// <summary>Registers error handling, rate limiting and the OpenAPI document.</summary>
    public static IServiceCollection AddBestStoriesApi(this IServiceCollection services)
    {
        services.AddExceptionHandler<SafeExceptionHandler>();
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions.TryAdd(
                    "traceId",
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier));

        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteRateLimitRejectionAsync;

            // Partitioned by client address. Behind a reverse proxy, configure forwarded headers
            // so that RemoteIpAddress is the client rather than the proxy.
            limiter.AddPolicy(StoriesRateLimitPolicy, httpContext =>
            {
                var settings = httpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitingOptions>>().Value;

                return RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitLimit,
                        Window = settings.Window,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        services.AddOpenApi();

        return services;
    }

    private static async ValueTask WriteRateLimitRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests.",
            detail: "The request rate limit was exceeded.")
            .ExecuteAsync(context.HttpContext);
    }
}
