namespace BestStories.Api.Application;

/// <summary>
/// Lets a log message through at most once per interval and counts the ones it holds back, so a
/// failure that affects every request is logged once with a count rather than once per request.
/// The matching metric still counts every occurrence.
/// </summary>
public sealed class LogThrottle(TimeProvider timeProvider, TimeSpan interval)
{
    /// <summary>The interval used for per-request failure logs.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private const long NotLogged = long.MinValue;

    private long _lastLoggedTimestamp = NotLogged;
    private long _suppressed;

    public LogThrottle(TimeProvider timeProvider)
        : this(timeProvider, DefaultInterval)
    {
    }

    /// <summary>
    /// Returns <c>true</c> when the caller should log now, with the number of occurrences held
    /// back since the last message; otherwise counts this occurrence and returns <c>false</c>.
    /// </summary>
    public bool ShouldLog(out long suppressed)
    {
        var last = Interlocked.Read(ref _lastLoggedTimestamp);
        var now = timeProvider.GetTimestamp();

        if (last != NotLogged && timeProvider.GetElapsedTime(last, now) < interval)
        {
            Interlocked.Increment(ref _suppressed);
            suppressed = 0;
            return false;
        }

        if (Interlocked.CompareExchange(ref _lastLoggedTimestamp, now, last) != last)
        {
            // Another thread logged this interval's message.
            Interlocked.Increment(ref _suppressed);
            suppressed = 0;
            return false;
        }

        suppressed = Interlocked.Exchange(ref _suppressed, 0);
        return true;
    }
}
