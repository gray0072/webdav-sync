using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WebDavSync.Core.Remote;

namespace WebDavSync.Core.Tests;

/// <summary>PROPFIND parsing, href decoding (§13 #6), complete listing (§6.8), error taxonomy (§4.5).</summary>
public sealed class WebDavClientTests
{
    private const string Listing = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/dav/Root%20Folder/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:getlastmodified>Mon, 12 Jan 2026 09:25:56 GMT</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>https://host/dav/Root%20Folder/%D0%9E%D1%82%D1%87%D1%91%D1%82%20%231%20%2B%2050%25.txt</d:href>
            <d:propstat><d:prop><d:resourcetype/><d:getcontentlength>42</d:getcontentlength><d:getetag>W/"abc"</d:getetag><d:getlastmodified>Tue, 13 Jan 2026 10:00:00 GMT</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
            <d:propstat><d:prop><d:getcontenttype/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/dav/Root%20Folder/Sub/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public async Task Lists_children_with_decoded_names()
    {
        using var client = Client(_ => Xml(Listing));
        var listing = await client.ListAsync("/Root Folder", CancellationToken.None);

        Assert.True(listing.Complete);
        Assert.True(listing.Self.IsDir);
        var file = Assert.Single(listing.Children, c => !c.IsDir);
        Assert.Equal("Отчёт #1 + 50%.txt", file.Name);
        Assert.Equal(42, file.Size);
        Assert.Equal("abc", file.ETag);
        Assert.Equal(new DateTime(2026, 1, 13, 10, 0, 0, DateTimeKind.Utc), file.MtimeUtc);
        Assert.Equal("Sub", Assert.Single(listing.Children, c => c.IsDir).Name);
    }

    [Fact]
    public void Paths_are_escaped_per_segment()
    {
        using var client = Client(_ => Xml(Listing));
        Assert.Equal("https://host/dav/a%20b/%23c%2Bd%25.txt", client.ToUri("/a b/#c+d%.txt").AbsoluteUri);
        Assert.Equal("/a b/#c+d%.txt", client.FromHref("/dav/a%20b/%23c%2Bd%25.txt"));
    }

    [Fact]
    public async Task Edge24_Child_without_properties_makes_the_listing_incomplete()
    {
        var body = Listing.Replace("<d:resourcetype/><d:getcontentlength>42", "<d:getcontentlength>42", StringComparison.Ordinal);
        using var client = Client(_ => Xml(body));
        var listing = await client.ListAsync("/Root Folder", CancellationToken.None);
        Assert.False(listing.Complete);
        Assert.NotEmpty(listing.Problems);
    }

    [Fact]
    public async Task Edge23_Html_instead_of_xml_is_offline()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") });
        var ex = await Assert.ThrowsAsync<RemoteException>(() => client.ListAsync("/Root Folder", CancellationToken.None));
        Assert.Equal(RemoteErrorKind.Offline, ex.Kind);
    }

    [Fact]
    public async Task Edge25_Unauthorized_is_auth()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<RemoteException>(() => client.StatAsync("/x", CancellationToken.None));
        Assert.Equal(RemoteErrorKind.Auth, ex.Kind);
        Assert.True(ex.StopsCycle);
    }

    [Fact]
    public async Task Edge27_Transient_errors_are_retried_honoring_retry_after()
    {
        var calls = 0;
        using var client = Client(_ =>
        {
            if (++calls == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return busy;
            }

            return Xml(Listing);
        });
        var listing = await client.ListAsync("/Root Folder", CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.True(listing.Complete);
    }

    [Fact]
    public async Task Not_found_stat_returns_null()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await client.StatAsync("/missing", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.InsufficientStorage, RemoteErrorKind.Quota)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RemoteErrorKind.Transient)]
    [InlineData(HttpStatusCode.Locked, RemoteErrorKind.Transient)]
    [InlineData(HttpStatusCode.Forbidden, RemoteErrorKind.Permanent)]
    [InlineData(HttpStatusCode.PreconditionFailed, RemoteErrorKind.Conflict)]
    public void Status_classification(HttpStatusCode status, RemoteErrorKind kind) =>
        Assert.Equal(kind, RemoteException.Classify(status));

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.MultiStatus) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static WebDavClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new WebDavOptions { Endpoint = new Uri("https://host/dav/"), User = "u", Password = "p", MaxRequestsPerSecond = 0 },
            NullLogger.Instance,
            new FakeHandler(respond));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
