using BestStories.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BestStories.Api.UnitTests;

public sealed class OptionsValidationTests
{
    [Theory]
    [InlineData("RefreshIntervalSeconds", "0")]
    [InlineData("CacheTtlSeconds", "0")]
    [InlineData("MaxConcurrentRequests", "0")]
    [InlineData("MaxConcurrentRequests", "65")]
    [InlineData("MinimumSuccessRatio", "0")]
    [InlineData("MinimumSuccessRatio", "1.5")]
    [InlineData("FailureCooldownSeconds", "-1")]
    [InlineData("MaxStalenessSeconds", "60")]
    [InlineData("RefreshTimeoutSeconds", "0")]
    [InlineData("OnDemandWaitSeconds", "0")]
    public void GivenAnInvalidStoryCacheSetting_WhenOptionsAreValidated_ThenValidationNamesTheSetting(
        string setting,
        string value)
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            [$"StoryCache:{setting}"] = value
        });

        // When
        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<StoryCacheOptions>>().Value);

        // Then
        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LockLeaseSeconds", "4")]
    [InlineData("LockWaitSeconds", "0")]
    [InlineData("LockPollMilliseconds", "0")]
    [InlineData("LockPollMilliseconds", "10000")]
    [InlineData("LocalCacheMilliseconds", "-1")]
    [InlineData("InstanceName", " ")]
    public void GivenAnInvalidDistributedCacheSetting_WhenOptionsAreValidated_ThenValidationNamesTheSetting(
        string setting,
        string value)
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            [$"DistributedCache:{setting}"] = value
        });

        // When
        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<DistributedCacheOptions>>().Value);

        // Then
        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GivenRedisWithoutAConnectionString_WhenOptionsAreValidated_ThenTheConnectionStringIsRequired()
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["DistributedCache:Provider"] = "Redis"
        });

        // When
        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<DistributedCacheOptions>>().Value);

        // Then
        Assert.Contains("RedisConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GivenAnUnknownCacheProvider_WhenServicesAreRegistered_ThenConfigurationIsRejected()
    {
        // When
        var exception = Record.Exception(() =>
        {
            using var services = CreateServices(new Dictionary<string, string?>
            {
                ["DistributedCache:Provider"] = "Unknown"
            });
            _ = services.GetRequiredService<IOptions<DistributedCacheOptions>>().Value;
        });

        // Then
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Theory]
    [InlineData("PermitLimit", "0")]
    [InlineData("WindowSeconds", "0")]
    public void GivenAnInvalidRateLimitSetting_WhenOptionsAreValidated_ThenValidationNamesTheSetting(
        string setting,
        string value)
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            [$"RateLimiting:{setting}"] = value
        });

        // When
        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<RateLimitingOptions>>().Value);

        // Then
        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KnownProxies:0", "not-an-address")]
    [InlineData("KnownNetworks:0", "10.0.0.0")]
    [InlineData("KnownNetworks:0", "10.0.0.0/33")]
    [InlineData("ForwardLimit", "0")]
    public void GivenAnInvalidForwardedHeadersSetting_WhenOptionsAreValidated_ThenValidationNamesTheSetting(
        string setting,
        string value)
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            [$"ForwardedHeaders:{setting}"] = value
        });

        // When
        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value);

        // Then
        Assert.Contains(setting.Split(':')[0], exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GivenTrustedProxies_WhenForwardedHeaderOptionsAreBuilt_ThenOnlyThoseProxiesAreAddedToTheDefaults()
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.4",
            ["ForwardedHeaders:KnownNetworks:0"] = "10.1.0.0/16",
            ["ForwardedHeaders:ForwardLimit"] = "2"
        });

        // When
        var options = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;

        // Then
        Assert.Contains(System.Net.IPAddress.Parse("10.0.0.4"), options.KnownProxies);
        Assert.Contains(System.Net.IPNetwork.Parse("10.1.0.0/16"), options.KnownIPNetworks);
        Assert.Equal(2, options.ForwardLimit);
        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
    }

    [Fact]
    public void GivenTheDefaultConfiguration_WhenOptionsAreValidated_ThenTheyAreValid()
    {
        // Given
        using var services = CreateServices(new Dictionary<string, string?>());

        // When
        var exception = Record.Exception(() =>
        {
            _ = services.GetRequiredService<IOptions<StoryCacheOptions>>().Value;
            _ = services.GetRequiredService<IOptions<DistributedCacheOptions>>().Value;
            _ = services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
            _ = services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value;
        });

        // Then
        Assert.Null(exception);
    }

    private static ServiceProvider CreateServices(IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddBestStories(configuration);
        services.AddBestStoriesApi();
        return services.BuildServiceProvider();
    }
}
