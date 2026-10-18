using System.Net;
using System.Text;
using System.Text.Json;
using WebDavSync.Core.Updates;

namespace WebDavSync.Core.Tests;

/// <summary>Update check (§14.3): GitHub release parsing, versions, schedule.</summary>
public sealed class UpdateCheckerTests
{
    private const string Release = """
        {
          "tag_name": "v0.3.0",
          "html_url": "https://github.com/gray0072/webdav-sync/releases/tag/v0.3.0",
          "published_at": "2026-10-20T12:30:00Z",
          "draft": false,
          "prerelease": false,
          "body": "What's new",
          "assets": [
            { "name": "WebDavSync-0.3.0-win-x64.zip", "browser_download_url": "https://example.org/a.zip" },
            { "name": "WebDavSync-0.3.0-win-x64.zip.sha256", "browser_download_url": "https://example.org/a.zip.sha256" }
          ]
        }
        """;

    [Fact]
    public void Parses_a_release_with_its_download_and_checksum()
    {
        var release = UpdateChecker.Parse(JsonDocument.Parse(Release).RootElement)!;

        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal(new DateTime(2026, 10, 20, 12, 30, 0, DateTimeKind.Utc), release.PublishedUtc);
        Assert.Equal("https://example.org/a.zip", release.ZipUrl);
        Assert.Equal("https://example.org/a.zip.sha256", release.ChecksumUrl);
        Assert.Equal("What's new", release.Notes);
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("\"v0.3.0\"", "\"nightly\"")]
    public void Ignores_drafts_prereleases_and_non_version_tags(string from, string to) =>
        Assert.Null(UpdateChecker.Parse(JsonDocument.Parse(Release.Replace(from, to, StringComparison.Ordinal)).RootElement));

    [Theory]
    [InlineData("v0.2.1", "0.2.1")]
    [InlineData("0.2.1+dd4295c", "0.2.1")]
    [InlineData("V1.2", "1.2.0")]
    [InlineData("1.10.0-beta", "1.10.0")]
    public void Parses_versions(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(text));

    [Fact]
    public void Newer_versions_compare_numerically() =>
        Assert.True(UpdateChecker.ParseVersion("v0.10.0") > UpdateChecker.ParseVersion("v0.9.9"));

    [Fact]
    public void Running_version_and_build_date_are_known()
    {
        Assert.True(UpdateChecker.CurrentVersion > new Version(0, 0, 0));
        Assert.NotNull(UpdateChecker.CurrentBuildDate);
        Assert.StartsWith(UpdateChecker.CurrentVersion.ToString(), UpdateChecker.CurrentDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UpdateCheckInterval.Daily, null, true)]
    [InlineData(UpdateCheckInterval.Daily, 23.0, false)]
    [InlineData(UpdateCheckInterval.Daily, 25.0, true)]
    [InlineData(UpdateCheckInterval.Weekly, 24.0 * 6, false)]
    [InlineData(UpdateCheckInterval.Weekly, 24.0 * 7, true)]
    [InlineData(UpdateCheckInterval.Monthly, 24.0 * 29, false)]
    [InlineData(UpdateCheckInterval.Monthly, 24.0 * 30, true)]
    [InlineData(UpdateCheckInterval.Never, null, false)]
    public void Checks_are_due_by_interval(UpdateCheckInterval interval, double? hoursAgo, bool due)
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(due, UpdateChecker.IsDue(interval, hoursAgo is { } h ? now.AddHours(-h) : null, now));
    }

    [Fact]
    public async Task Asks_github_for_the_latest_release()
    {
        HttpRequestMessage? sent = null;
        using var http = new HttpClient(new Handler(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release, Encoding.UTF8, "application/json") };
        }));

        var release = await new UpdateChecker(http).GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Version(0, 3, 0), release!.Version);
        Assert.Equal("https://api.github.com/repos/gray0072/webdav-sync/releases/latest", sent!.RequestUri!.ToString());
        Assert.Contains("WebDavSync", sent.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_release_yet_is_null()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new UpdateChecker(http).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
