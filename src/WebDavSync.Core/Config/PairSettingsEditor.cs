using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace WebDavSync.Core.Config;

/// <summary>
/// Changes pair settings from <c>name=value</c> assignments (CLI <c>wds pair set</c>, §15.5). Only settings that can
/// change without touching the pair's identity or base are editable; see <see cref="Fixed"/> and <see cref="Elsewhere"/>.
/// </summary>
public static class PairSettingsEditor
{
    /// <summary>Identity of the pair: changing them would point the base and the root markers at other data (§9 #1).</summary>
    public static readonly IReadOnlySet<string> Fixed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "localRoot", "remoteUrl", "remoteRoot" };

    /// <summary>
    /// Lists that are changed by other means, with the reason. Selective sync and patterns: excluding must drop the
    /// subtree's base and re-including must run in union mode (§12.2, safety invariant 10); not implemented yet.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Elsewhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["excludedFolders"] = "is changed with 'wds folders <pair> --exclude|--include' or the tray's 'Choose folders…'",
        ["excludePatterns"] = "cannot be changed here yet: excluding and re-including need base handling (§12.2)",
        ["autoApprove"] = "is changed with 'wds rule add|remove'",
    };

    private static readonly Dictionary<string, Action<PairSettings, string>> Setters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = (p, v) => p.Name = NotEmpty(v),
        ["user"] = (p, v) => p.User = NotEmpty(v),
        ["mode"] = (p, v) => p.Mode = ParseEnum<SyncMode>(v),
        ["maxOpsPerCycle"] = (p, v) => p.MaxOpsPerCycle = IsNone(v) ? null : (int)ParseLong(v, min: 1, max: int.MaxValue),
        ["conflictWinner"] = (p, v) => p.ConflictWinner = ParseEnum<ConflictWinner>(v),
        ["massDeleteCount"] = (p, v) => p.MassDeleteCount = (int)ParseLong(v, min: 0, max: int.MaxValue),
        ["massDeletePercent"] = (p, v) => p.MassDeletePercent = ParseDouble(v, min: 0, max: 100),
        ["maxCompareDownloadBytes"] = (p, v) => p.MaxCompareDownloadBytes = ParseSize(v),
        ["minFreeDiskSpace"] = (p, v) => p.MinFreeDiskSpace = ParseSize(v),
        ["keepOverwrittenVersions"] = (p, v) => p.KeepOverwrittenVersions = ParseBool(v),
        ["localDeleteTarget"] = (p, v) => p.LocalDeleteTarget = ParseEnum<LocalDeleteTarget>(v),
        ["importedBaseMatch"] = (p, v) => p.ImportedBaseMatch = ParseEnum<ImportedBaseMatch>(v),
        ["remoteHashProperty"] = (p, v) => p.RemoteHashProperty = IsNone(v) ? null : ParseHashProperty(v),
        ["maxRequestsPerSecond"] = (p, v) => p.MaxRequestsPerSecond = ParseDouble(v, min: 0, max: 1000),
        ["maxParallelRequests"] = (p, v) => p.MaxParallelRequests = (int)ParseLong(v, min: 1, max: 16),
        ["localQuietPeriod"] = (p, v) => p.LocalQuietPeriod = ParseDuration(v, TimeSpan.Zero, TimeSpan.FromMinutes(10)),
        ["maxBatchDelay"] = (p, v) => p.MaxBatchDelay = ParseDuration(v, TimeSpan.Zero, TimeSpan.FromHours(1)),
        ["remotePollInterval"] = (p, v) => p.RemotePollInterval = ParseDuration(v, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1)),
        ["localVerifyInterval"] = (p, v) => p.LocalVerifyInterval = IsNone(v) ? null : ParseDuration(v, TimeSpan.FromDays(1), TimeSpan.FromDays(3650)),
        ["hashLimitMBps"] = (p, v) => p.HashLimitMBps = ParseDouble(v, min: 0, max: 100_000),
        ["setLocalMtimeFromRemote"] = (p, v) => p.SetLocalMtimeFromRemote = ParseBool(v),
    };

    /// <summary>Names accepted by <see cref="Set"/>, in schema order.</summary>
    public static IEnumerable<string> Names => Setters.Keys;

    /// <summary>
    /// Applies all assignments or none: on an invalid one, <paramref name="pair"/> is left unchanged and
    /// <see cref="ArgumentException"/> names the culprit.
    /// </summary>
    public static void Set(PairSettings pair, IEnumerable<string> assignments)
    {
        var parsed = assignments.Select(Split).ToList();
        var copy = Clone(pair);
        foreach (var (name, value) in parsed)
        {
            Set(copy, name, value);
        }

        foreach (var (name, value) in parsed)
        {
            Set(pair, name, value);
        }
    }

    public static void Set(PairSettings pair, string name, string value)
    {
        if (Fixed.Contains(name))
        {
            throw new ArgumentException($"'{name}' identifies the pair and cannot be changed; create a new pair instead.");
        }

        if (Elsewhere.TryGetValue(name, out var reason))
        {
            throw new ArgumentException($"'{name}' {reason}.");
        }

        if (!Setters.TryGetValue(name, out var setter))
        {
            throw new ArgumentException($"Unknown setting '{name}'. Editable: {string.Join(", ", Names)}.");
        }

        try
        {
            setter(pair, value.Trim());
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new ArgumentException($"{name}: {ex.Message}", ex);
        }
    }

    /// <summary>Parses a byte size: a plain number or one with a <c>KB</c>/<c>MB</c>/<c>GB</c>/<c>TB</c> suffix (binary units).</summary>
    public static long ParseSize(string value)
    {
        var text = value.Trim();
        long unit = 1;
        foreach (var (suffix, multiplier) in new[] { ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1L) })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length].TrimEnd();
                unit = multiplier;
                break;
            }
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0 ||
            number * unit > long.MaxValue)
        {
            throw new FormatException($"'{value}' is not a size, e.g. 2GB, 64MB or 1048576.");
        }

        return (long)Math.Round(number * unit);
    }

    /// <summary>Parses a duration: <c>90s</c>, <c>15m</c>, <c>2h</c>, <c>1d</c> or <c>hh:mm:ss</c>.</summary>
    public static TimeSpan ParseDuration(string value, TimeSpan min, TimeSpan max)
    {
        var text = value.Trim().ToLowerInvariant();
        TimeSpan? result = null;
        if (text.Length > 1 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0)
        {
            result = text[^1] switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                _ => null,
            };
        }

        if (result is null && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
        }

        return result is { } r && r >= min && r <= max
            ? r
            : throw new FormatException($"'{value}' is not a duration in {min}..{max}, e.g. 5s, 15m, 2h.");
    }

    private static (string Name, string Value) Split(string assignment)
    {
        var eq = assignment.IndexOf('=', StringComparison.Ordinal);
        return eq > 0
            ? (assignment[..eq].Trim(), assignment[(eq + 1)..])
            : throw new ArgumentException($"'{assignment}' is not a name=value assignment.");
    }

    private static PairSettings Clone(PairSettings pair) =>
        System.Text.Json.JsonSerializer.Deserialize<PairSettings>(
            System.Text.Json.JsonSerializer.Serialize(pair, AppSettings.JsonOptions), AppSettings.JsonOptions)!;

    private static bool IsNone(string value) =>
        value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static string NotEmpty(string value) =>
        value.Length > 0 ? value : throw new FormatException("must not be empty.");

    private static T ParseEnum<T>(string value)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var result) && Enum.IsDefined(result) && !value.Any(char.IsDigit)
            ? result
            : throw new FormatException($"'{value}' is not one of {string.Join(", ", Enum.GetNames<T>())}.");

    private static bool ParseBool(string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => throw new FormatException($"'{value}' is not true or false."),
    };

    private static long ParseLong(string value, long min, long max) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : throw new FormatException($"'{value}' is not an integer in {min}..{max}.");

    private static double ParseDouble(string value, double min, double max) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : throw new FormatException($"'{value}' is not a number in {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}.");

    private static string ParseHashProperty(string value)
    {
        try
        {
            return XName.Get(value).ToString();
        }
        catch (Exception ex) when (ex is ArgumentException or XmlException)
        {
            throw new FormatException($"'{value}' is not a property name in Clark notation, e.g. {{DAV:}}getetag.");
        }
    }
}
