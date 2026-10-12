using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace BestStories.Api.UnitTests.TestSupport;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<Uri> RequestedUris { get; } = new();

    public static StubHttpMessageHandler Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedUris.Enqueue(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}
