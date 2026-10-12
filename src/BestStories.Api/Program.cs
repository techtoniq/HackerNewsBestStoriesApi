using BestStories.Api.Configuration;
using BestStories.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddBestStories(builder.Configuration);
builder.Services.AddBestStoriesApi();
builder.Services.AddBestStoriesObservability(builder.Configuration);

var app = builder.Build();

app.UseApiResponseHeaders();
app.UseExceptionHandler();
app.UseApiStatusCodePages();
app.UseRateLimiter();

app.MapOpenApi();
app.MapHealthEndpoints();
app.MapStoryEndpoints();

await app.RunAsync();

/// <summary>Exposed for <c>WebApplicationFactory</c> in the integration tests.</summary>
public partial class Program
{
}
