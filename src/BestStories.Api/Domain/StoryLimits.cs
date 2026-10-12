namespace BestStories.Api.Domain;

public static class StoryLimits
{
    /// <summary>
    /// The maximum number of stories the service holds and returns, matching the size of the
    /// Hacker News best-stories list.
    /// </summary>
    public const int MaximumCount = 500;
}
