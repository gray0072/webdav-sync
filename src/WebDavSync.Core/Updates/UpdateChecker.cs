using System.Net.Http.Headers;
using System.Text.Json;

namespace WebDavSync.Core.Updates;

/// <summary>How often the tray app looks for a new release (§14.3).</summary>
public enum UpdateCheckInterval
{
    Daily,
    Weekly,
    Monthly,
    Never,
}

/// <summary>A published release: version, page, notes and the download with its checksum file.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, DateTime? PublishedUtc, string PageUrl, string Notes, string? ZipUrl, string? ChecksumUrl);

/// <summary>Finds the latest release on GitHub (§14.3): <c>GET /repos/{owner}/{repo}/releases/latest</c>, no token needed.</summary>
public sealed class UpdateChecker(HttpClient http, string repository = UpdateChecker.Repository)
{
    public const string Repository = "gray0072/webdav-sync";

    /// <summary>The newest release, or null when there is none.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WebDavSync", CurrentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    /// <summary>Reads a GitHub release; null for drafts, prereleases and tags that are not a version.</summary>
    public static ReleaseInfo? Parse(JsonElement release)
    {
        if (Bool(release, "draft") || Bool(release, "prerelease") ||
            release.GetProperty("tag_name").GetString() is not { } tag || ParseVersion(tag) is not { } version)
        {
            return null;
        }

        string? zip = null, checksum = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zip = url;
                }
                else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksum = url;
                }
            }
        }

        return new ReleaseInfo(
            version,
            tag,
            release.TryGetProperty("published_at", out var published) && published.TryGetDateTime(out var at) ? at.ToUniversalTime() : null,
            release.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
            release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            zip,
            checksum);
    }

    /// <summary><c>v1.2.3</c>, <c>1.2.3</c> or <c>1.2.3+build</c> → 1.2.3.</summary>
    public static Version? ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var version) ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0)) : null;
    }

    /// <summary>The running version, from the informational version without the commit (e.g. <c>0.2.1</c>).</summary>
    public static Version CurrentVersion { get; } =
        ParseVersion(typeof(UpdateChecker).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0") ?? new Version(0, 0, 0);

    /// <summary>
    /// When the running build was made (UTC date, from the <c>BuildDate</c> assembly metadata set in
    /// <c>Directory.Build.props</c>); release builds are made when the release is published.
    /// </summary>
    public static DateTime? CurrentBuildDate { get; } =
        typeof(UpdateChecker).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value is { } text &&
        DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    /// <summary>"0.2.1 (17 Oct 2026)": the version with its build date.</summary>
    public static string CurrentDescription =>
        CurrentVersion + (CurrentBuildDate is { } d ? $" ({d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)})" : string.Empty);

    /// <summary>Whether an automatic check is due.</summary>
    public static bool IsDue(UpdateCheckInterval interval, DateTime? lastCheckUtc, DateTime nowUtc)
    {
        if (interval == UpdateCheckInterval.Never)
        {
            return false;
        }

        if (lastCheckUtc is not { } last)
        {
            return true;
        }

        var period = interval switch
        {
            UpdateCheckInterval.Daily => TimeSpan.FromDays(1),
            UpdateCheckInterval.Weekly => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(30),
        };
        return nowUtc - last >= period;
    }

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
