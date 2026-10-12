using BestStories.Api.Domain;

namespace BestStories.Api.UnitTests.TestSupport;

internal static class TestStories
{
    public static Story Create(int id, int? score = null) =>
        new(
            id,
            $"Story {id}",
            $"https://example.com/{id}",
            "author",
            DateTimeOffset.UnixEpoch,
            score ?? id,
            0);

    public static StorySnapshot Snapshot(DateTimeOffset publishedAt, params int[] ids) =>
        StorySnapshot.Create(ids.Select(id => Create(id)), publishedAt);
}
