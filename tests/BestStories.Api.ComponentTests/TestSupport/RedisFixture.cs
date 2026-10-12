using StackExchange.Redis;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(BestStories.Api.ComponentTests.TestSupport.RedisFixture))]

namespace BestStories.Api.ComponentTests.TestSupport;

/// <summary>
/// Starts one Redis container for the test assembly. Requires a running Docker engine.
/// Tests isolate themselves with a unique key prefix rather than separate containers.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private ConnectionMultiplexer? _connection;

    public IConnectionMultiplexer Connection =>
        _connection ?? throw new InvalidOperationException("The Redis fixture has not been initialized.");

    public static string NewInstanceName() => $"tests:{Guid.NewGuid():N}:";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
