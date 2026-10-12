using Microsoft.AspNetCore.Diagnostics;

namespace BestStories.Api.Services;

public sealed class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "An unhandled exception occurred while processing a request.");

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred.",
            detail: "The request could not be completed.")
            .ExecuteAsync(httpContext);

        return true;
    }
}
