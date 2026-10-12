using BestStories.Api.Models;

namespace BestStories.Api.Services;

public sealed record StoryCacheSnapshot(
    IReadOnlyList<StoryResponse> Stories,
    DateTimeOffset PublishedAt);
