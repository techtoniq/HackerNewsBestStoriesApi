using Microsoft.Extensions.Caching.Distributed;

namespace BestStories.Api.UnitTests.TestSupport;

/// <summary>An <see cref="IDistributedCache"/> whose reads can fail or hang on demand.</summary>
internal sealed class FakeDistributedCache : IDistributedCache
{
    private readonly Dictionary<string, byte[]> _entries = [];
    private int _reads;

    /// <summary>When set, reads throw this exception.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>When set, reads wait for this task before completing.</summary>
    public Task? ReadGate { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public void SetRaw(string key, byte[] value) => _entries[key] = value;

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref _reads);
        if (ReadGate is { } gate)
        {
            await gate;
        }

        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        return _entries.TryGetValue(key, out var value) ? value : null;
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        _entries[key] = value;
        return Task.CompletedTask;
    }

    public byte[]? Get(string key) => throw new NotSupportedException();

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();

    public void Refresh(string key) => throw new NotSupportedException();

    public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();

    public void Remove(string key) => throw new NotSupportedException();

    public Task RemoveAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
}
