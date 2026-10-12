using BestStories.Api.Application;
using Microsoft.Extensions.Time.Testing;

namespace BestStories.Api.UnitTests;

public sealed class LogThrottleTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public void GivenRepeatedFailures_WhenTheyAreLoggedWithinOneInterval_ThenOnlyTheFirstIsLogged()
    {
        // Given
        var throttle = new LogThrottle(_timeProvider, TimeSpan.FromSeconds(30));

        // When
        var first = throttle.ShouldLog(out var suppressedBeforeFirst);
        var second = throttle.ShouldLog(out _);
        var third = throttle.ShouldLog(out _);

        // Then
        Assert.True(first);
        Assert.Equal(0, suppressedBeforeFirst);
        Assert.False(second);
        Assert.False(third);
    }

    [Fact]
    public void GivenSuppressedFailures_WhenTheIntervalPasses_ThenTheNextLogReportsHowManyWereHeldBack()
    {
        // Given
        var throttle = new LogThrottle(_timeProvider, TimeSpan.FromSeconds(30));
        throttle.ShouldLog(out _);
        throttle.ShouldLog(out _);
        throttle.ShouldLog(out _);

        // When
        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        var logged = throttle.ShouldLog(out var suppressed);

        // Then
        Assert.True(logged);
        Assert.Equal(2, suppressed);
    }
}
