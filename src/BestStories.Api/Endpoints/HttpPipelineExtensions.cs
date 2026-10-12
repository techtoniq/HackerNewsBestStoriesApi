using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace BestStories.Api.Endpoints;

public static class HttpPipelineExtensions
{
    /// <summary>
    /// Adds <c>X-Content-Type-Options: nosniff</c> and, for responses that have not set their own
    /// caching policy, <c>Cache-Control: no-store</c>. The story endpoint sets an explicit
    /// cacheable policy; errors, health and other responses must not be cached.
    /// </summary>
    public static IApplicationBuilder UseApiResponseHeaders(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";

                if (context.Response.StatusCode >= StatusCodes.Status400BadRequest ||
                    !headers.ContainsKey(HeaderNames.CacheControl))
                {
                    headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            });

            await next(context);
        });

        return app;
    }

    /// <summary>Turns empty error responses (unknown routes, wrong methods) into Problem Details.</summary>
    public static IApplicationBuilder UseApiStatusCodePages(this IApplicationBuilder app)
    {
        app.UseStatusCodePages(async statusCodeContext =>
        {
            var context = statusCodeContext.HttpContext;
            var title = ReasonPhrases.GetReasonPhrase(context.Response.StatusCode);

            await Results.Problem(
                statusCode: context.Response.StatusCode,
                title: string.IsNullOrEmpty(title) ? "Request failed." : title,
                detail: "The request could not be completed.")
                .ExecuteAsync(context);
        });

        return app;
    }
}
