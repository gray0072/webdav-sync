using System.Text;

namespace WebDavSync.Core.Model;

/// <summary>
/// Relative paths use <c>/</c> separators and no leading slash; the root is the empty string.
/// A key is <c>NFC(path).ToLowerInvariant()</c> — matches the legacy <c>key</c> column (§19.4).
/// </summary>
public static class PathKey
{
    public static string FromPath(string relativePath) =>
        relativePath.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    public static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? string.Empty : path[..i];
    }

    public static string Name(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string Combine(string parent, string name) =>
        parent.Length == 0 ? name : parent + "/" + name;

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="ancestor"/>.</summary>
    public static bool IsUnder(string path, string ancestor) =>
        ancestor.Length == 0
            ? path.Length > 0
            : path.Length > ancestor.Length && path[ancestor.Length] == '/' &&
              path.StartsWith(ancestor, StringComparison.Ordinal);

    public static bool IsSelfOrUnder(string path, string ancestor) =>
        string.Equals(path, ancestor, StringComparison.Ordinal) || IsUnder(path, ancestor);

    public static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;

    /// <summary>Replaces the prefix <paramref name="oldPrefix"/> of a self-or-descendant path.</summary>
    public static string Rebase(string path, string oldPrefix, string newPrefix)
    {
        if (string.Equals(path, oldPrefix, StringComparison.Ordinal))
        {
            return newPrefix;
        }

        if (!IsUnder(path, oldPrefix))
        {
            throw new ArgumentException($"'{path}' is not under '{oldPrefix}'.", nameof(path));
        }

        return oldPrefix.Length == 0 ? Combine(newPrefix, path) : newPrefix + path[oldPrefix.Length..];
    }

    public static string ToLocalFull(string root, string relativePath) =>
        relativePath.Length == 0 ? root : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string ToRemoteFull(string remoteRoot, string relativePath)
    {
        var root = remoteRoot.TrimEnd('/');
        return relativePath.Length == 0 ? (root.Length == 0 ? "/" : root) : root + "/" + relativePath;
    }
}
