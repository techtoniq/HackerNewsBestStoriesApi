using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BestStories.Api.Endpoints;

public static class HealthEndpoints
{
    public const string ReadinessTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealthResponse
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadinessTag),
            ResponseWriter = WriteHealthResponse
        });

        return endpoints;
    }

    /// <summary>
    /// <c>Unhealthy</c> returns <c>503</c>. <c>Degraded</c> returns <c>200</c> so the instance stays in
    /// rotation, with the status in the body for anyone looking.
    /// </summary>
    private static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        if (report.Status == HealthStatus.Unhealthy)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable.",
                detail: "The service is not ready to receive requests.")
                .ExecuteAsync(context);
        }

        var status = report.Status == HealthStatus.Degraded ? "degraded" : "healthy";
        return Results.Json(new { status }).ExecuteAsync(context);
    }
}
