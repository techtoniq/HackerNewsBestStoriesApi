using BestStories.Api.Integrations.HackerNews.V0;
using BestStories.Api.Models;
using BestStories.Api.Services;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using StackExchange.Redis;

namespace BestStories.Api.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBestStories(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configuredConcurrency = Math.Max(
            1,
            configuration.GetValue<int?>("StoryCache:MaxConcurrentRequests") ??
            new StoryCacheOptions().MaxConcurrentRequests);
        var minimumRefreshLockLeaseSeconds =
            HackerNewsV0Client.RequestTimeoutSeconds +
            (int)Math.Ceiling(
                StoryLimits.MaximumCount / (double)configuredConcurrency) *
            HackerNewsV0Client.RequestTimeoutSeconds;

        services
            .AddOptions<StoryCacheOptions>()
            .BindConfiguration("StoryCache")
            .Validate(options => options.RefreshIntervalSeconds > 0,
                "RefreshIntervalSeconds must be greater than zero.")
            .Validate(options => options.CacheTtlSeconds > 0,
                "CacheTtlSeconds must be greater than zero.")
            .Validate(options => options.MaxConcurrentRequests > 0,
                "MaxConcurrentRequests must be greater than zero.")
            .ValidateOnStart();

        services
            .AddOptions<DistributedCacheOptions>()
            .BindConfiguration("DistributedCache")
            .Validate(options =>
                    options.Provider.Equals("Memory", StringComparison.OrdinalIgnoreCase) ||
                    options.Provider.Equals("Redis", StringComparison.OrdinalIgnoreCase),
                "DistributedCache:Provider must be either Memory or Redis.")
            .Validate(options =>
                    options.Provider.Equals("Memory", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(options.RedisConnectionString),
                "DistributedCache:RedisConnectionString is required when Redis is selected.")
            .Validate(options => options.LockLeaseSeconds > 0,
                "LockLeaseSeconds must be greater than zero.")
            .Validate(options =>
                    options.Provider.Equals("Memory", StringComparison.OrdinalIgnoreCase) ||
                    options.LockLeaseSeconds > minimumRefreshLockLeaseSeconds,
                "Redis LockLeaseSeconds must exceed the maximum bounded upstream refresh duration.")
            .Validate(options => options.LockWaitSeconds > 0,
                "LockWaitSeconds must be greater than zero.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.InstanceName),
                "DistributedCache:InstanceName is required.")
            .Validate(options => options.LockPollMilliseconds > 0,
                "LockPollMilliseconds must be greater than zero.")
            .ValidateOnStart();

        services.AddHttpClient<IHackerNewsClient, HackerNewsV0Client>(client =>
        {
            client.BaseAddress = HackerNewsV0Client.ApiBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(HackerNewsV0Client.RequestTimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BestStoriesApi/1.0");
        });

        var cacheOptions = configuration
            .GetSection("DistributedCache")
            .Get<DistributedCacheOptions>() ?? new DistributedCacheOptions();

        if (cacheOptions.Provider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.InstanceName = cacheOptions.InstanceName;
            });
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(cacheOptions.RedisConnectionString!));
            services
                .AddOptions<RedisCacheOptions>()
                .Configure<IConnectionMultiplexer>((options, multiplexer) =>
                    options.ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer));
            services.AddSingleton<IStoryCache, RedisStoryCache>();
            services.AddSingleton<IDistributedRefreshLock, RedisRefreshLock>();
        }
        else
        {
            services.AddSingleton<IStoryCache, StoryCache>();
            services.AddSingleton<IDistributedRefreshLock, InMemoryRefreshLock>();
        }

        services.AddSingleton<IStoryProvider, StoryProvider>();
        services.AddSingleton<StoryRefreshService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<StoryRefreshService>());
        services
            .AddHealthChecks()
            .AddCheck<StoryCacheHealthCheck>("story-cache", tags: ["ready"]);

        return services;
    }
}
