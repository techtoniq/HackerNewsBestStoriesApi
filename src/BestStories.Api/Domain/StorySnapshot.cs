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
    /// Checks a snapshot that was not built with <see cref="Create"/>, such as one deserialized
    /// from the shared cache: every story is complete, IDs are positive and distinct, there are no
    /// more than <see cref="StoryLimits.MaximumCount"/>, and the order is the one <see cref="Create"/>
    /// produces.
    /// </summary>
    public static bool IsValid(StorySnapshot? snapshot, out string? problem)
    {
        problem = Check(snapshot);
        return problem is null;
    }

    private static string? Check(StorySnapshot? snapshot)
    {
        if (snapshot?.Stories is null)
        {
            return "the snapshot or its story list is missing";
        }

        if (snapshot.Stories.Count > StoryLimits.MaximumCount)
        {
            return $"it holds {snapshot.Stories.Count} stories, more than {StoryLimits.MaximumCount}";
        }

        var ids = new HashSet<int>();
        Story? previous = null;
        foreach (var story in snapshot.Stories)
        {
            if (story is null ||
                story.Id <= 0 ||
                string.IsNullOrWhiteSpace(story.Title) ||
                string.IsNullOrWhiteSpace(story.Url) ||
                string.IsNullOrWhiteSpace(story.PostedBy))
            {
                return "a story is missing or incomplete";
            }

            if (!ids.Add(story.Id))
            {
                return "a story ID appears more than once";
            }

            if (previous is not null &&
                (story.Score > previous.Score || (story.Score == previous.Score && story.Id < previous.Id)))
            {
                return "the stories are not in score order";
            }

            previous = story;
        }

        return null;
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
