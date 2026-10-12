using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BestStories.Api.IntegrationTests.TestSupport;

/// <summary>
/// Hosts the real application in memory with Hacker News replaced by <see cref="FakeHackerNewsApi"/>.
/// Defaults to on-demand refresh, the in-memory cache and a rate limit high enough not to
/// interfere; tests override settings as needed.
/// </summary>
internal sealed class BestStoriesApiFactory(
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    public FakeHackerNewsApi HackerNews { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var effectiveSettings = new Dictionary<string, string?>
        {
            ["StoryCache:AutomaticRefresh"] = "false",
            ["DistributedCache:Provider"] = "Memory",
            ["RateLimiting:PermitLimit"] = "1000"
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            effectiveSettings[key] = value;
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(effectiveSettings));

        builder.ConfigureTestServices(services =>
        {
            services.ConfigureHttpClientDefaults(http =>
                http.ConfigurePrimaryHttpMessageHandler(HackerNews.CreateHandler));
            configureServices?.Invoke(services);
        });
    }
}
