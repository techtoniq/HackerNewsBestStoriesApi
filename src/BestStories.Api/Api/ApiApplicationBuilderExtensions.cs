using Microsoft.AspNetCore.WebUtilities;

namespace BestStories.Api.Api;

public static class ApiApplicationBuilderExtensions
{
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.Remove("Server");
                context.Response.Headers.Remove("X-Powered-By");
                context.Response.Headers.Remove("X-AspNet-Version");
                context.Response.Headers.Remove("X-AspNetMvc-Version");
                context.Response.Headers.Remove("X-Runtime");
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Task.CompletedTask;
            });

            await next();
        });

        return app;
    }

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
