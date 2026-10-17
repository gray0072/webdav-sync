using System.Net;
using System.Text;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using WebDavSync.Core.Model;
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

    /// <summary>Mail.ru response as recorded on 2026-10-16 (§4.4).</summary>
    private const string MailRuListing = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:multistatus xmlns:d="DAV:">
        	<d:response>
        		<d:href>/Sub%20Folder</d:href>
        		<d:propstat>
        			<d:status>HTTP/1.1 200 OK</d:status>
        			<d:prop>
        					<d:displayname>Sub Folder</d:displayname>
        					<d:resourcetype><d:collection/></d:resourcetype>
        			</d:prop>
        		</d:propstat>
        	</d:response>
        	<d:response>
        		<d:href>/Sub%20Folder/%D0%9E%D1%82%D1%87%D1%91%D1%82%20%231%20%2B%2050%25%20%26%20co.txt</d:href>
        		<d:propstat>
        			<d:status>HTTP/1.1 200 OK</d:status>
        			<d:prop>
        					<d:displayname>Отчёт #1 + 50% &amp; co.txt</d:displayname>
        					<d:getcontentlength>6</d:getcontentlength>
        					<d:getcontenttype>text/plain</d:getcontenttype>
        					<d:getetag>6E65737465640000000000000000000000000000</d:getetag>
        					<d:getlastmodified>Fri, 16 Oct 2026 17:53:09 GMT</d:getlastmodified>
        					<d:resourcetype/>
        			</d:prop>
        		</d:propstat>
        	</d:response>
        </d:multistatus>
        """;

    [Fact]
    public async Task MailRu_etag_is_the_content_hash()
    {
        using var client = Client(
            _ =>
            {
                var response = Xml(MailRuListing);
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/xml; charset=\"utf-8\"");
                return response;
            },
            XName.Get("{DAV:}getetag"),
            "https://webdav.cloud.mail.ru/");
        var listing = await client.ListAsync("/Sub Folder", CancellationToken.None);

        Assert.True(listing.Complete);
        Assert.True(listing.Self.IsDir);
        Assert.Null(listing.Self.ETag);
        var file = Assert.Single(listing.Children);
        Assert.Equal("Отчёт #1 + 50% & co.txt", file.Name);
        Assert.Equal(6, file.Size);
        Assert.Equal(ContentHash.Compute("nested"u8), file.Hash);
        Assert.Equal(new DateTime(2026, 10, 16, 17, 53, 9, DateTimeKind.Utc), file.MtimeUtc);
    }

    [Fact]
    public async Task Without_hash_property_the_remote_hash_is_unknown()
    {
        using var client = Client(_ => Xml(MailRuListing), endpoint: "https://webdav.cloud.mail.ru/");
        var listing = await client.ListAsync("/Sub Folder", CancellationToken.None);
        Assert.Null(Assert.Single(listing.Children).Hash);
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
    public async Task Put_dropped_mid_body_is_transient()
    {
        // Mail.ru, 2026-10-16: a 643 MB PUT failed with "Error while copying content to a stream" after ~3 minutes.
        using var client = Client(request =>
        {
            var body = request.Content!.ReadAsStream();
            body.ReadExactly(new byte[1000]);
            throw new HttpRequestException("Error while copying content to a stream.", new IOException("Connection reset by peer."));
        });
        var ex = await Assert.ThrowsAsync<RemoteException>(() =>
            client.PutAsync("/big.bin", new MemoryStream(new byte[5000]), 5000, (DateTime?)null, CancellationToken.None));
        Assert.Equal(RemoteErrorKind.Transient, ex.Kind);
        Assert.False(ex.StopsCycle);
        Assert.Contains("after 1000 of 5000 bytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Connection reset by peer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_without_a_connection_is_offline()
    {
        using var client = Client(_ => throw new HttpRequestException("No such host is known."));
        var ex = await Assert.ThrowsAsync<RemoteException>(() =>
            client.PutAsync("/big.bin", new MemoryStream(new byte[5000]), 5000, (DateTime?)null, CancellationToken.None));
        Assert.Equal(RemoteErrorKind.Offline, ex.Kind);
        Assert.True(ex.StopsCycle);
    }

    [Fact]
    public async Task Ranged_get_reports_where_the_content_starts()
    {
        RangeHeaderValue? requested = null;
        using var partial = Client(request =>
        {
            requested = request.Headers.Range;
            var content = new ByteArrayContent(new byte[60]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(40, 99, 100);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        });
        await using (var download = await partial.GetAsync("/big.bin", 40, CancellationToken.None))
        {
            Assert.Equal(40, download.Offset);
        }

        Assert.Equal(40, Assert.Single(requested!.Ranges).From);

        using var whole = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) });
        await using (var download = await whole.GetAsync("/big.bin", 40, CancellationToken.None))
        {
            Assert.Equal(0, download.Offset);
        }

        using var wrong = Client(_ =>
        {
            var content = new ByteArrayContent(new byte[10]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(90, 99, 100);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        });
        var ex = await Assert.ThrowsAsync<RemoteException>(() => wrong.GetAsync("/big.bin", 40, CancellationToken.None));
        Assert.Equal(RemoteErrorKind.Transient, ex.Kind);
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
    [InlineData(HttpStatusCode.GatewayTimeout, RemoteErrorKind.Transient)]
    [InlineData(HttpStatusCode.Locked, RemoteErrorKind.Transient)]
    [InlineData(HttpStatusCode.Forbidden, RemoteErrorKind.Permanent)]
    [InlineData(HttpStatusCode.PreconditionFailed, RemoteErrorKind.Conflict)]
    public void Status_classification(HttpStatusCode status, RemoteErrorKind kind) =>
        Assert.Equal(kind, RemoteException.Classify(status));

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.MultiStatus) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static WebDavClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, XName? hashProperty = null, string endpoint = "https://host/dav/") =>
        new(
            new WebDavOptions { Endpoint = new Uri(endpoint), User = "u", Password = "p", MaxRequestsPerSecond = 0, HashProperty = hashProperty },
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
