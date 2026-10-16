using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Remote;

/// <summary>Parses <c>207 Multi-Status</c> bodies.</summary>
public static class PropfindParser
{
    public static readonly XNamespace Dav = "DAV:";

    /// <summary>One <c>response</c> element. <see cref="Href"/> is still encoded.</summary>
    public sealed record Response(string Href, int? Status, IReadOnlyDictionary<XName, XElement> OkProps, IReadOnlyList<int> PropStatuses)
    {
        /// <summary>True when the server returned the properties we rely on with status 200.</summary>
        public bool IsOk => (Status is null or 200) && OkProps.ContainsKey(Dav + "resourcetype");

        public bool IsCollection =>
            OkProps.TryGetValue(Dav + "resourcetype", out var rt) && rt.Element(Dav + "collection") is not null;

        public string? Text(XName name) => OkProps.TryGetValue(name, out var e) ? e.Value.Trim() : null;
    }

    public static IReadOnlyList<Response> Parse(Stream body)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(body);
        }
        catch (XmlException ex)
        {
            throw new RemoteException(RemoteErrorKind.Transient, "Malformed PROPFIND response: " + ex.Message, inner: ex);
        }

        if (doc.Root?.Name != Dav + "multistatus")
        {
            throw new RemoteException(RemoteErrorKind.Offline, $"Unexpected PROPFIND root element '{doc.Root?.Name}'.");
        }

        var result = new List<Response>();
        foreach (var response in doc.Root.Elements(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var props = new Dictionary<XName, XElement>();
            var statuses = new List<int>();
            foreach (var propstat in response.Elements(Dav + "propstat"))
            {
                var status = ParseStatus(propstat.Element(Dav + "status")?.Value);
                statuses.Add(status ?? 0);
                if (status != 200)
                {
                    continue;
                }

                foreach (var prop in propstat.Element(Dav + "prop")?.Elements() ?? [])
                {
                    props[prop.Name] = prop;
                }
            }

            result.Add(new Response(href, ParseStatus(response.Element(Dav + "status")?.Value), props, statuses));
        }

        return result;
    }

    public static RemoteItem ToItem(Response response, string decodedPath, XName? hashProperty)
    {
        var isDir = response.IsCollection;
        var size = long.TryParse(response.Text(Dav + "getcontentlength"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var hash = hashProperty is null ? null : ContentHash.ParseOrNull(response.Text(hashProperty));
        return new RemoteItem(
            decodedPath,
            isDir,
            isDir ? 0 : size,
            ParseHttpDate(response.Text(Dav + "getlastmodified")),
            NormalizeETag(response.Text(Dav + "getetag")),
            hash,
            response.Text(Dav + "getcontenttype"));
    }

    public static DateTime? ParseHttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ||
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date))
        {
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        }

        return null;
    }

    public static string? NormalizeETag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
        {
            return null;
        }

        etag = etag.Trim();
        if (etag.StartsWith("W/", StringComparison.Ordinal))
        {
            etag = etag[2..];
        }

        return etag.Trim('"');
    }

    private static int? ParseStatus(string? statusLine)
    {
        // "HTTP/1.1 200 OK"
        var parts = statusLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
    }
}
