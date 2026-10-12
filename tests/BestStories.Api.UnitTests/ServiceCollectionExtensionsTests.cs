using BestStories.Api.Configuration;
using BestStories.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BestStories.Api.UnitTests;

public sealed class ServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("StoryCache:RefreshIntervalSeconds", "0", "RefreshIntervalSeconds", false)]
    [InlineData("StoryCache:CacheTtlSeconds", "0", "CacheTtlSeconds", false)]
    [InlineData("StoryCache:MaxConcurrentRequests", "0", "MaxConcurrentRequests", false)]
    [InlineData("DistributedCache:Provider", "Unknown", "Provider", true)]
    [InlineData("DistributedCache:Provider", "Redis", "RedisConnectionString", true)]
    public void GivenInvalidConfiguration_WhenOptionsAreValidated_ThenValidationFails(
        string key,
        string value,
        string expectedValidationMessage,
        bool distributedCacheOptions)
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            [key] = value
        });

        // When
        var exception = Assert.Throws<OptionsValidationException>(
            () => ValidateOptions(services, distributedCacheOptions));

        // Then
        Assert.Contains(expectedValidationMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GivenARedisLeaseShorterThanTheMaximumRefreshDuration_WhenOptionsAreValidated_ThenValidationFails()
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["DistributedCache:Provider"] = "Redis",
            ["DistributedCache:RedisConnectionString"] = "localhost:6379",
            ["DistributedCache:LockLeaseSeconds"] = "900"
        });

        // When
        var exception = Assert.Throws<OptionsValidationException>(
            () => ValidateOptions(services, distributedCacheOptions: true));

        // Then
        Assert.Contains("maximum bounded upstream refresh duration", exception.Message);
    }

    private static ServiceProvider CreateServices(IReadOnlyDictionary<string, string?> overrides)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["StoryCache:AutomaticRefresh"] = "false"
        };

        foreach (var (key, value) in overrides)
        {
            configuration[key] = value;
        }

        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(settings);
        services.AddBestStories(settings);
        return services.BuildServiceProvider();
    }

    private static void ValidateOptions(ServiceProvider services, bool distributedCacheOptions)
    {
        if (distributedCacheOptions)
        {
            _ = services.GetRequiredService<IOptions<DistributedCacheOptions>>().Value;
        }
        else
        {
            _ = services.GetRequiredService<IOptions<StoryCacheOptions>>().Value;
        }
    }
}
