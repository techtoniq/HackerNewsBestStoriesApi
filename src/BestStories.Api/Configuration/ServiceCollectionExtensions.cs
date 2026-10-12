using BestStories.Api.Application;
using BestStories.Api.Endpoints;
using BestStories.Api.Infrastructure.Caching;
using BestStories.Api.Integrations.HackerNews.V0;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BestStories.Api.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers story retrieval, caching, refresh scheduling and readiness.</summary>
    public static IServiceCollection AddBestStories(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<StoryCacheOptions>()
            .BindConfiguration(StoryCacheOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StoryCacheOptions>, StoryCacheOptionsValidator>();

        services.AddOptions<DistributedCacheOptions>()
            .BindConfiguration(DistributedCacheOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DistributedCacheOptions>, DistributedCacheOptionsValidator>();

        AddHackerNewsClient(services);
        AddStoryCache(services, configuration);

        services.AddSingleton<StorySnapshotBuilder>();
        services.AddSingleton<IStoryProvider, StoryProvider>();
        services.AddHostedService<StoryRefreshService>();
        services
            .AddHealthChecks()
            .AddCheck<StoryCacheHealthCheck>("story-cache", tags: [HealthEndpoints.ReadinessTag]);

        return services;
    }

    private static void AddHackerNewsClient(IServiceCollection services)
    {
        services
            .AddHttpClient<IHackerNewsClient, HackerNewsV0Client>(client =>
            {
                client.BaseAddress = HackerNewsV0Client.ApiBaseAddress;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BestStoriesApi/1.0");
            })
            .AddStandardResilienceHandler(resilience =>
            {
                // Retries with exponential backoff and jitter for transient failures (5xx, 408,
                // 429, network errors), a per-attempt timeout, an overall timeout and a circuit
                // breaker that stops calling Hacker News while it is failing.
                resilience.Retry.MaxRetryAttempts = 2;
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
            });
    }

    private static void AddStoryCache(IServiceCollection services, IConfiguration configuration)
    {
        // The provider decides which services are registered, so it is read at registration
        // time; the full options object is still validated at startup.
        var cacheOptions = configuration
            .GetSection(DistributedCacheOptions.SectionName)
            .Get<DistributedCacheOptions>() ?? new DistributedCacheOptions();

        if (cacheOptions.Provider != CacheProvider.Redis)
        {
            services.AddSingleton<IStoryCache, InMemoryStoryCache>();
            services.AddSingleton<IDistributedRefreshLock, InMemoryRefreshLock>();
            return;
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redisConfiguration = ConfigurationOptions.Parse(cacheOptions.RedisConnectionString ?? string.Empty);

            // Start even if Redis is briefly unreachable and keep reconnecting in the
            // background; readiness reports the outage meanwhile.
            redisConfiguration.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redisConfiguration);
        });
        services.AddStackExchangeRedisCache(options => options.InstanceName = cacheOptions.InstanceName);
        services
            .AddOptions<RedisCacheOptions>()
            .Configure<IConnectionMultiplexer>((options, multiplexer) =>
                options.ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer));
        services.AddSingleton<IStoryCache, RedisStoryCache>();
        services.AddSingleton<IDistributedRefreshLock, RedisRefreshLock>();
    }
}
