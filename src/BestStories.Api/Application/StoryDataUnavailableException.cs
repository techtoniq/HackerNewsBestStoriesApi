namespace BestStories.Api.Application;

/// <summary>
/// No snapshot within the maximum staleness can be served. The API maps this to
/// <c>503 Service Unavailable</c>.
/// </summary>
public sealed class StoryDataUnavailableException : Exception
{
    public StoryDataUnavailableException()
    {
    }

    public StoryDataUnavailableException(string message)
        : base(message)
    {
    }

    public StoryDataUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// When it is worth asking again, for the <c>Retry-After</c> header, or <c>null</c> when the
    /// cause gives no better estimate than the API's default.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }
}
