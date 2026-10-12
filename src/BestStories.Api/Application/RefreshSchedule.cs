namespace BestStories.Api.Application;

/// <summary>
/// When this instance's background service will next try to refresh, so that <c>503</c>
/// responses in automatic mode can tell clients when to come back.
/// </summary>
/// <remarks>
/// With several instances sharing Redis, another instance may refresh sooner; this is an upper
/// bound for this instance.
/// </remarks>
public sealed class RefreshSchedule(TimeProvider timeProvider)
{
    private const long Unknown = long.MinValue;

    private long _nextAttemptUnixMilliseconds = Unknown;

    /// <summary>An attempt is running, so the next one is not yet known.</summary>
    public void AttemptStarted() =>
        Interlocked.Exchange(ref _nextAttemptUnixMilliseconds, Unknown);

    public void SetNextAttempt(DateTimeOffset nextAttempt) =>
        Interlocked.Exchange(ref _nextAttemptUnixMilliseconds, nextAttempt.ToUnixTimeMilliseconds());

    /// <summary>
    /// The time until the next attempt, or <c>null</c> when it is not known or is already due
    /// (an attempt is running).
    /// </summary>
    public TimeSpan? TimeUntilNextAttempt()
    {
        var next = Interlocked.Read(ref _nextAttemptUnixMilliseconds);
        if (next == Unknown)
        {
            return null;
        }

        var remaining = TimeSpan.FromMilliseconds(next - timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return remaining > TimeSpan.Zero ? remaining : null;
    }
}
