using WebDavSync.Core.Model;

namespace WebDavSync.Core.Local;

/// <summary>
/// Rolling verify of local files (§5.3): each cycle re-hashes a slice of the synced files in key order, continuing
/// after the last verified key, so that all of them are read once per <c>localVerifyInterval</c> without one long scan.
/// </summary>
public static class RollingVerify
{
    /// <summary>Upper bound of one slice, so that one cycle is never held up for long.</summary>
    public const long MaxSliceBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Bytes due since <paramref name="lastUtc"/>: all synced bytes once per <paramref name="interval"/>, capped at
    /// <see cref="MaxSliceBytes"/>.
    /// </summary>
    public static double Budget(long totalBytes, DateTime lastUtc, DateTime nowUtc, TimeSpan interval) =>
        interval <= TimeSpan.Zero || nowUtc <= lastUtc
            ? 0
            : Math.Min(MaxSliceBytes, totalBytes * ((nowUtc - lastUtc) / interval));

    /// <summary>
    /// The next files after <paramref name="cursor"/> (wrapping around) whose sizes fit into <paramref name="budget"/>.
    /// A file bigger than the slice limit is taken alone once the budget reaches the limit. Empty: wait for more budget.
    /// </summary>
    public static IReadOnlyList<BaseEntry> Slice(IEnumerable<BaseEntry> files, string? cursor, double budget)
    {
        var ordered = files.Where(f => !f.IsDir && f.LocalHash is not null).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0 || budget <= 0)
        {
            return [];
        }

        var start = cursor is null ? 0 : ordered.FindIndex(f => string.CompareOrdinal(f.Key, cursor) > 0);
        if (start < 0)
        {
            start = 0;
        }

        var slice = new List<BaseEntry>();
        double taken = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var file = ordered[(start + i) % ordered.Count];
            var size = Math.Min(file.LocalSize, MaxSliceBytes);
            if (taken + size > budget)
            {
                break;
            }

            slice.Add(file);
            taken += size;
        }

        return slice;
    }
}
