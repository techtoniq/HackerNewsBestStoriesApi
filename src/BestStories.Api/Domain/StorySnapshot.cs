namespace BestStories.Api.Domain;

/// <summary>
/// An immutable, score-ordered set of best stories and the time it was published.
/// </summary>
/// <remarks>
/// Use <see cref="Create"/> to build a snapshot so that the ordering invariant is applied in one
/// place. The positional constructor exists for deserialization of snapshots that were created
/// with <see cref="Create"/>.
/// </remarks>
public sealed record StorySnapshot(IReadOnlyList<Story> Stories, DateTimeOffset PublishedAt)
{
    public static StorySnapshot Create(IEnumerable<Story> stories, DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(stories);

        var ordered = stories
            .OrderByDescending(story => story.Score)
            .ThenBy(story => story.Id)
            .ToArray();

        return new StorySnapshot(Array.AsReadOnly(ordered), publishedAt);
    }

    /// <summary>
    /// Gets the age of the snapshot at <paramref name="now"/>. Clock skew between instances can
    /// make a snapshot appear to come from the future, so the age is never negative.
    /// </summary>
    public TimeSpan AgeAt(DateTimeOffset now)
    {
        var age = now - PublishedAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
