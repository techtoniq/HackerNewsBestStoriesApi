using System.Net;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Configuration;

public sealed class StoryCacheOptionsValidator : IValidateOptions<StoryCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, StoryCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.RefreshIntervalSeconds <= 0)
        {
            failures.Add("StoryCache:RefreshIntervalSeconds must be greater than zero.");
        }

        if (options.CacheTtlSeconds <= 0)
        {
            failures.Add("StoryCache:CacheTtlSeconds must be greater than zero.");
        }

        if (options.MaxConcurrentRequests is < 1 or > 64)
        {
            failures.Add("StoryCache:MaxConcurrentRequests must be between 1 and 64.");
        }

        if (options.MinimumSuccessRatio is <= 0 or > 1)
        {
            failures.Add("StoryCache:MinimumSuccessRatio must be greater than 0 and at most 1.");
        }

        if (options.FailureCooldownSeconds < 0)
        {
            failures.Add("StoryCache:FailureCooldownSeconds must not be negative.");
        }

        if (options.RefreshTimeoutSeconds <= 0)
        {
            failures.Add("StoryCache:RefreshTimeoutSeconds must be greater than zero.");
        }

        if (options.OnDemandWaitSeconds <= 0)
        {
            failures.Add("StoryCache:OnDemandWaitSeconds must be greater than zero.");
        }

        if (options.InitialLoadMaxRetryDelaySeconds <= 0)
        {
            failures.Add("StoryCache:InitialLoadMaxRetryDelaySeconds must be greater than zero.");
        }

        if (options.ClientCacheMaxAgeSeconds < 0)
        {
            failures.Add("StoryCache:ClientCacheMaxAgeSeconds must not be negative.");
        }

        if (options.RefreshIntervalSeconds > 0 &&
            options.CacheTtlSeconds > 0 &&
            options.MaxStalenessSeconds < options.FreshFor.TotalSeconds)
        {
            failures.Add(
                "StoryCache:MaxStalenessSeconds must be at least the refresh interval (automatic refresh) " +
                "or the cache TTL (on-demand refresh).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class DistributedCacheOptionsValidator : IValidateOptions<DistributedCacheOptions>
{
    public ValidateOptionsResult Validate(string? name, DistributedCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (!Enum.IsDefined(options.Provider))
        {
            failures.Add("DistributedCache:Provider must be either Memory or Redis.");
        }

        if (options.Provider == CacheProvider.Redis && string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            failures.Add("DistributedCache:RedisConnectionString is required when Redis is selected.");
        }

        if (string.IsNullOrWhiteSpace(options.InstanceName))
        {
            failures.Add("DistributedCache:InstanceName is required.");
        }

        if (options.LockLeaseSeconds < 5)
        {
            failures.Add("DistributedCache:LockLeaseSeconds must be at least 5.");
        }

        if (options.LockWaitSeconds <= 0)
        {
            failures.Add("DistributedCache:LockWaitSeconds must be greater than zero.");
        }

        if (options.LockPollMilliseconds <= 0)
        {
            failures.Add("DistributedCache:LockPollMilliseconds must be greater than zero.");
        }
        else if (options.LockWaitSeconds > 0 && options.LockPoll >= options.LockWait)
        {
            failures.Add("DistributedCache:LockPollMilliseconds must be shorter than LockWaitSeconds.");
        }

        if (options.LocalCacheMilliseconds < 0)
        {
            failures.Add("DistributedCache:LocalCacheMilliseconds must not be negative.");
        }

        if (options.ReadTimeoutMilliseconds <= 0)
        {
            failures.Add("DistributedCache:ReadTimeoutMilliseconds must be greater than zero.");
        }

        if (options.ReadFailureBackoffMilliseconds < 0)
        {
            failures.Add("DistributedCache:ReadFailureBackoffMilliseconds must not be negative.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.PermitLimit <= 0)
        {
            failures.Add("RateLimiting:PermitLimit must be greater than zero.");
        }

        if (options.WindowSeconds <= 0)
        {
            failures.Add("RateLimiting:WindowSeconds must be greater than zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class TrustedProxyOptionsValidator : IValidateOptions<TrustedProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, TrustedProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                failures.Add($"ForwardedHeaders:KnownProxies contains '{proxy}', which is not an IP address.");
            }
        }

        foreach (var network in options.KnownNetworks)
        {
            if (!IPNetwork.TryParse(network, out _))
            {
                failures.Add($"ForwardedHeaders:KnownNetworks contains '{network}', which is not a CIDR network.");
            }
        }

        if (options.ForwardLimit < 1)
        {
            failures.Add("ForwardedHeaders:ForwardLimit must be at least 1.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
