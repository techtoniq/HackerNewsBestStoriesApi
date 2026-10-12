using BestStories.Api.Domain;

namespace BestStories.Api.Endpoints;

/// <summary>The public JSON contract for a story.</summary>
public sealed record StoryResponse(
    string Title,
    string Uri,
    string PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount)
{
    public static StoryResponse FromStory(Story story)
    {
        ArgumentNullException.ThrowIfNull(story);

        return new StoryResponse(
            story.Title,
            story.Url,
            story.PostedBy,
            story.PostedAt,
            story.Score,
            story.CommentCount);
    }
}
