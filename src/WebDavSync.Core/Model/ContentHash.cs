using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebDavSync.Core.Model;

/// <summary>
/// Content hash of the legacy client and the cloud (§3.3): 40 uppercase hex chars.
/// </summary>
public readonly record struct ContentHash
{
    private readonly string? _hex;

    private ContentHash(string hex) => _hex = hex;

    public string Hex => _hex ?? throw new InvalidOperationException("Empty content hash.");

    public static ContentHash Parse(string hex) =>
        TryParse(hex, out var hash) ? hash : throw new FormatException($"Invalid content hash '{hex}'.");

    public static bool TryParse(string? hex, out ContentHash hash)
    {
        hash = default;
        if (hex is null || hex.Length != 40)
        {
            return false;
        }

        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        hash = new ContentHash(hex.ToUpperInvariant());
        return true;
    }

    public static ContentHash? ParseOrNull(string? hex) => TryParse(hex, out var hash) ? hash : null;

    public static ContentHash Compute(ReadOnlySpan<byte> content)
    {
        using var hasher = new ContentHasher();
        hasher.Append(content);
        return hasher.Finish();
    }

    public static async Task<ContentHash> ComputeAsync(Stream stream, CancellationToken ct, ByteRateLimiter? limiter = null)
    {
        using var hasher = new ContentHasher();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, read));
            if (limiter is not null)
            {
                await limiter.WaitAsync(read, ct).ConfigureAwait(false);
            }
        }

        return hasher.Finish();
    }

    public static async Task<ContentHash> ComputeFileAsync(string path, CancellationToken ct, ByteRateLimiter? limiter = null)
    {
        await using var stream = OpenShared(path);
        return await ComputeAsync(stream, ct, limiter).ConfigureAwait(false);
    }

    /// <summary>Opens a file for reading while allowing other writers (§6.6).</summary>
    public static FileStream OpenShared(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            BufferSize = 0,
        });

    public override string ToString() => _hex ?? string.Empty;
}

/// <summary>
/// Streaming content hasher: <c>size &lt; 21</c> → bytes zero-padded to 20 as hex;
/// otherwise <c>SHA1("mrCloud" + content + decimal size)</c>.
/// </summary>
public sealed class ContentHasher : IDisposable
{
    private const int SmallLimit = 21;
    private static readonly byte[] Prefix = "mrCloud"u8.ToArray();

    private readonly IncrementalHash _sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private readonly byte[] _small = new byte[20];
    private long _length;
    private bool _finished;

    public ContentHasher() => _sha1.AppendData(Prefix);

    public long Length => _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_length < _small.Length)
        {
            var take = (int)Math.Min(data.Length, _small.Length - _length);
            data[..take].CopyTo(_small.AsSpan((int)_length));
        }

        _sha1.AppendData(data);
        _length += data.Length;
    }

    /// <summary>Starts over, e.g. when a resumed download restarts from the beginning.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _sha1.GetHashAndReset();
        _sha1.AppendData(Prefix);
        Array.Clear(_small);
        _length = 0;
    }

    public ContentHash Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;
        if (_length < SmallLimit)
        {
            return ContentHash.Parse(Convert.ToHexString(_small));
        }

        _sha1.AppendData(Encoding.ASCII.GetBytes(_length.ToString(CultureInfo.InvariantCulture)));
        return ContentHash.Parse(Convert.ToHexString(_sha1.GetHashAndReset()));
    }

    public void Dispose() => _sha1.Dispose();
}

/// <summary>Read-only pass-through stream that feeds a <see cref="ContentHasher"/>.</summary>
public sealed class HashingStream(Stream inner, ContentHasher hasher) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        hasher.Append(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        hasher.Append(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
