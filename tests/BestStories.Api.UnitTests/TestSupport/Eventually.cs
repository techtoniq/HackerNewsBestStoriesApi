using System.Diagnostics;

namespace BestStories.Api.UnitTests.TestSupport;

internal static class Eventually
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds, running <paramref name="beforeEachCheck"/>
    /// (typically advancing a fake clock) before every check.
    /// </summary>
    public static async Task HoldsAsync(Func<Task<bool>> condition, Action? beforeEachCheck = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            beforeEachCheck?.Invoke();
            if (await condition())
            {
                return;
            }

            if (elapsed.Elapsed > Timeout)
            {
                throw new TimeoutException("The expected condition was not reached.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public static Task HoldsAsync(Func<bool> condition, Action? beforeEachCheck = null) =>
        HoldsAsync(() => Task.FromResult(condition()), beforeEachCheck);
}
