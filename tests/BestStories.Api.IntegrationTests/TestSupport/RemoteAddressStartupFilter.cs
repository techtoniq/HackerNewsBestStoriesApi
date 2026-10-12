using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace BestStories.Api.IntegrationTests.TestSupport;

/// <summary>
/// Sets the connection's remote address ahead of the application's pipeline. The in-memory test
/// server has no real connection, so this stands in for the proxy or client that connects.
/// </summary>
internal sealed class RemoteAddressStartupFilter(IPAddress remoteAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteAddress;
                return nextMiddleware(context);
            });
            next(app);
        };
}
