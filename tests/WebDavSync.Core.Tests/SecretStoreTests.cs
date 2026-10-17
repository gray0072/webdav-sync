using WebDavSync.Core.Config;

namespace WebDavSync.Core.Tests;

/// <summary>Credential Manager store (§15.1); uses its own target prefix and removes what it wrote.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly SecretStore _store = new("WebDavSync-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _store.Remove("pair-a");
        _store.Remove("pair-b");
    }

    [Fact]
    public void Stores_reads_and_removes_passwords_per_pair()
    {
        _store.Set("pair-a", "s3cret pässword", "user@example.org");
        _store.Set("pair-b", "other");

        Assert.Equal("s3cret pässword", _store.Get("PAIR-A"));
        Assert.Equal("other", _store.Get("pair-b"));
        Assert.Null(_store.Get("pair-c"));

        _store.Set("pair-a", "changed");
        Assert.Equal("changed", _store.Get("pair-a"));

        Assert.True(_store.Remove("pair-a"));
        Assert.Null(_store.Get("pair-a"));
        Assert.False(_store.Remove("pair-a"));
    }
}
