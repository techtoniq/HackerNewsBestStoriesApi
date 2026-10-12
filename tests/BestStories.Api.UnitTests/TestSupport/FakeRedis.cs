using System.Reflection;
using StackExchange.Redis;

namespace BestStories.Api.UnitTests.TestSupport;

/// <summary>
/// A scripted stand-in for the few Redis calls <c>RedisRefreshLock</c> makes, so that lease
/// renewal and release failures (which a real Redis cannot be made to produce on demand) can be
/// tested deterministically.
/// </summary>
internal sealed class FakeRedis
{
    private int _renewals;
    private int _releases;

    public FakeRedis()
    {
        var database = DispatchProxy.Create<IDatabase, DatabaseProxy>();
        ((DatabaseProxy)(object)database).Owner = this;

        var connection = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>();
        ((ConnectionProxy)(object)connection).Database = database;
        Connection = connection;
    }

    public IConnectionMultiplexer Connection { get; }

    /// <summary>The result of each renewal: return 1 when renewed, 0 when another owner holds the lock, or throw.</summary>
    public Func<long> Renew { get; set; } = () => 1;

    /// <summary>Runs on each release; throw to simulate a failure.</summary>
    public Action Release { get; set; } = () => { };

    public int Renewals => Volatile.Read(ref _renewals);

    public int Releases => Volatile.Read(ref _releases);

    private Task<RedisResult> EvaluateScript(string script)
    {
        try
        {
            if (script.Contains("PEXPIRE", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _renewals);
                return Task.FromResult(RedisResult.Create((RedisValue)Renew()));
            }

            Interlocked.Increment(ref _releases);
            Release();
            return Task.FromResult(RedisResult.Create((RedisValue)1));
        }
        catch (Exception exception)
        {
            return Task.FromException<RedisResult>(exception);
        }
    }

#pragma warning disable CA1852 // DispatchProxy needs a type it can derive from.
    internal class DatabaseProxy : DispatchProxy
#pragma warning restore CA1852
    {
        internal FakeRedis? Owner { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
                nameof(IDatabase.ScriptEvaluateAsync) when args?[0] is string script =>
                    Owner!.EvaluateScript(script),
                _ => throw new NotSupportedException($"{targetMethod?.Name} is not faked.")
            };
    }

#pragma warning disable CA1852 // DispatchProxy needs a type it can derive from.
    internal class ConnectionProxy : DispatchProxy
#pragma warning restore CA1852
    {
        internal IDatabase? Database { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IConnectionMultiplexer.GetDatabase)
                ? Database
                : throw new NotSupportedException($"{targetMethod?.Name} is not faked.");
    }
}
