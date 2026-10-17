using System.Text.RegularExpressions;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Config;

public enum FilterVerdict
{
    /// <summary>Synced.</summary>
    Include,

    /// <summary>Built-in ignore list (§12.1): invisible to the engine.</summary>
    Ignore,

    /// <summary>User exclusion (§12.2, §12.3): exists on the side, but is neither synced nor deleted.</summary>
    Exclude,
}

/// <summary>Built-in ignore list, excluded folders and user patterns (§12).</summary>
public sealed class SyncFilter
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        StatePaths.DirName, ".cloud.db", ".cloud.db-journal", ".cloud_ss", "desktop.ini", "Thumbs.db",
        "$RECYCLE.BIN", "System Volume Information",
    };

    private readonly string[] _excludedFolderKeys;
    private readonly (Regex Regex, bool ByPath)[] _patterns;

    public SyncFilter(IEnumerable<string>? excludedFolders = null, IEnumerable<string>? patterns = null)
    {
        _excludedFolderKeys = (excludedFolders ?? [])
            .Select(f => PathKey.FromPath(f.Replace('\\', '/').Trim('/')))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _patterns = (patterns ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/').Trim('/'))
            .Select(p => (GlobToRegex(p), p.Contains('/')))
            .ToArray();
    }

    public static SyncFilter Empty { get; } = new();

    /// <summary>Excluded folders as normalized keys.</summary>
    public IReadOnlyList<string> ExcludedFolderKeys => _excludedFolderKeys;

    public static SyncFilter For(PairSettings pair) => new(pair.ExcludedFolders, pair.ExcludePatterns);

    public static bool IsBuiltInIgnored(string name) =>
        IgnoredNames.Contains(name) ||
        name.StartsWith("~$", StringComparison.Ordinal) ||
        name.StartsWith(".webdavsync-probe-", StringComparison.OrdinalIgnoreCase);

    public FilterVerdict Check(string key, string name)
    {
        if (IsBuiltInIgnored(name))
        {
            return FilterVerdict.Ignore;
        }

        foreach (var excluded in _excludedFolderKeys)
        {
            if (PathKey.IsSelfOrUnder(key, excluded))
            {
                return FilterVerdict.Exclude;
            }
        }

        foreach (var (regex, byPath) in _patterns)
        {
            if (regex.IsMatch(byPath ? key : PathKey.Name(key)))
            {
                return FilterVerdict.Exclude;
            }
        }

        return FilterVerdict.Include;
    }

    /// <summary>True when the key or any of its ancestors is not synced.</summary>
    public bool IsSynced(string key)
    {
        for (var k = key; k.Length > 0; k = PathKey.Parent(k))
        {
            if (Check(k, PathKey.Name(k)) != FilterVerdict.Include)
            {
                return false;
            }
        }

        return true;
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(PathKey.FromPath(glob)).Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
