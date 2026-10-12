using System.Text.Json;
using BestStories.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BestStories.Api.UnitTests;

public sealed class SafeExceptionHandlerTests
{
    [Fact]
    public async Task GivenAnUnhandledException_WhenTheHandlerProcessesIt_ThenGenericProblemDetailsAreReturned()
    {
        // Given
        const string sensitiveMessage = "C:\\internal\\platform\\private-stack-trace";
        var handler = new SafeExceptionHandler(NullLogger<SafeExceptionHandler>.Instance);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        // When
        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException(sensitiveMessage),
            TestContext.Current.CancellationToken);

        // Then
        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        responseBody.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            responseBody,
            cancellationToken: TestContext.Current.CancellationToken);
        var response = document.RootElement.GetRawText();
        Assert.Contains("An unexpected error occurred.", response);
        Assert.Contains("The request could not be completed.", response);
        Assert.DoesNotContain(sensitiveMessage, response);
        Assert.DoesNotContain("stack", response, StringComparison.OrdinalIgnoreCase);
    }
}
