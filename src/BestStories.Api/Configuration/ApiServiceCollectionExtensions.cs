using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using BestStories.Api.Endpoints;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BestStories.Api.Configuration;

public static class ApiServiceCollectionExtensions
{
    public const string StoriesRateLimitPolicy = "stories";

    /// <summary>
    /// Registers forwarded-header handling, error handling, rate limiting and the OpenAPI document.
    /// </summary>
    public static IServiceCollection AddBestStoriesApi(this IServiceCollection services)
    {
        AddForwardedHeaders(services);

        services.AddExceptionHandler<SafeExceptionHandler>();
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions.TryAdd(
                    "traceId",
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier));

        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteRateLimitRejectionAsync;

            // Partitioned by client address. Behind a reverse proxy, RemoteIpAddress is the
            // client only when the proxy is listed in ForwardedHeaders (see AddForwardedHeaders).
            limiter.AddPolicy(StoriesRateLimitPolicy, httpContext =>
            {
                var settings = httpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitingOptions>>().Value;

                return RateLimitPartition.GetFixedWindowLimiter(
                    GetClientPartitionKey(httpContext.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitLimit,
                        Window = settings.Window,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        services.AddOpenApi();

        return services;
    }

    /// <summary>
    /// Gets the rate-limit partition for a client address. IPv4 addresses that arrive mapped into
    /// IPv6 (as they do on dual-stack sockets) are partitioned as IPv4, and IPv6 addresses by
    /// their /64 prefix, because one host or subscriber normally controls a whole /64 and could
    /// otherwise rotate addresses to get a fresh limit on every request.
    /// </summary>
    internal static string GetClientPartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return string.Create(CultureInfo.InvariantCulture, $"{new IPAddress(bytes)}/64");
    }

    /// <summary>
    /// Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only from loopback and the
    /// proxies configured in <see cref="TrustedProxyOptions"/>.
    /// </summary>
    private static void AddForwardedHeaders(IServiceCollection services)
    {
        services.AddOptions<TrustedProxyOptions>()
            .BindConfiguration(TrustedProxyOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TrustedProxyOptions>, TrustedProxyOptionsValidator>();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<TrustedProxyOptions>>((forwarded, trustedOptions) =>
            {
                var trusted = trustedOptions.Value;
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.ForwardLimit = trusted.ForwardLimit;

                foreach (var proxy in trusted.KnownProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in trusted.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });
    }

    private static async ValueTask WriteRateLimitRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests.",
            detail: "The request rate limit was exceeded.")
            .ExecuteAsync(context.HttpContext);
    }
}
