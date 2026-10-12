namespace BestStories.Api.Configuration;

/// <summary>
/// The reverse proxies and load balancers whose <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c> headers are trusted. The client address they report is used for
/// rate limiting, logs and traces. Headers from any other sender are ignored, so clients cannot
/// spoof their address.
/// </summary>
/// <remarks>
/// Loopback is always trusted. Without any other entries, the forwarded headers of a proxy in front
/// of the service are ignored and every client behind it shares the proxy's rate limit.
/// </remarks>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Individual proxy addresses, for example <c>10.0.0.4</c>.</summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Proxy networks in CIDR notation, for example <c>10.0.0.0/16</c>.</summary>
    public IList<string> KnownNetworks { get; } = [];

    /// <summary>
    /// How many entries of <c>X-Forwarded-For</c> to process from the right. Set it to the number
    /// of trusted proxies a request passes through.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}
