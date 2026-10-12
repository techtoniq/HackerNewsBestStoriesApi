using Microsoft.AspNetCore.Diagnostics;

namespace BestStories.Api.Endpoints;

/// <summary>
/// Logs unhandled exceptions and returns a generic 500 Problem Details response that carries the
/// trace ID for correlation but no exception details.
/// </summary>
public sealed partial class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        LogUnhandledException(logger, exception);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred.",
            detail: "The request could not be completed.")
            .ExecuteAsync(httpContext);

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "An unhandled exception occurred while processing a request.")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);
}
