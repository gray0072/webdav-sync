using System.Diagnostics;

namespace WebDavSync.Core.Model;

/// <summary>
/// Limits a sequential reader to <paramref name="bytesPerSecond"/> (e.g. hashing, §11), so a long scan does not
/// saturate the disk. Idle time earns at most one second of burst. Not thread-safe: one reader at a time.
/// </summary>
public sealed class ByteRateLimiter(double bytesPerSecond)
{
    private long _start = Stopwatch.GetTimestamp();
    private double _bytes;

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        if (bytesPerSecond <= 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_start).TotalSeconds;
        if (elapsed - (_bytes / bytesPerSecond) > 1)
        {
            // Long idle: restart the window instead of allowing a large burst.
            _start = Stopwatch.GetTimestamp();
            _bytes = 0;
            elapsed = 0;
        }

        _bytes += bytes;
        var due = _bytes / bytesPerSecond;
        if (due > elapsed)
        {
            await Task.Delay(TimeSpan.FromSeconds(due - elapsed), ct).ConfigureAwait(false);
        }
    }
}
