namespace BestStories.Api.Domain;

/// <summary>
/// A Hacker News story as the service understands it, independent of the upstream wire format
/// and of the public API contract.
/// </summary>
public sealed record Story(
    int Id,
    string Title,
    string Url,
    string PostedBy,
    DateTimeOffset PostedAt,
    int Score,
    int CommentCount);
