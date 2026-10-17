using WebDavSync.Core.Config;

namespace WebDavSync.Core.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wds-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Stores_passwords_per_pair_encrypted()
    {
        var path = Path.Combine(_dir, "secrets.dat");
        var store = new SecretStore(path);
        store.Set("pair-a", "s3cret pässword");
        store.Set("pair-b", "other");

        Assert.Equal("s3cret pässword", new SecretStore(path).Get("PAIR-A"));
        Assert.Equal("other", store.Get("pair-b"));
        Assert.Null(store.Get("pair-c"));
        Assert.DoesNotContain("s3cret", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.True(store.Remove("pair-a"));
        Assert.Null(store.Get("pair-a"));
        Assert.False(store.Remove("pair-a"));
    }

    [Fact]
    public void A_blob_of_another_pair_does_not_decrypt()
    {
        var path = Path.Combine(_dir, "secrets.dat");
        var store = new SecretStore(path);
        store.Set("pair-a", "secret");
        File.WriteAllText(path, File.ReadAllText(path).Replace("pair-a", "pair-x", StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => store.Get("pair-x"));
    }
}
