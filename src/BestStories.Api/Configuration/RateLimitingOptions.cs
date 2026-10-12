namespace BestStories.Api.Configuration;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>The number of requests each client address may make per window.</summary>
    public int PermitLimit { get; set; } = 100;

    public int WindowSeconds { get; set; } = 10;

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
