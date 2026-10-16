using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Remote;

public sealed record ProbeCheck(string Name, bool? Ok, string Detail);

public sealed class ProbeReport
{
    public string Endpoint { get; init; } = string.Empty;

    public string User { get; init; } = string.Empty;

    public DateTime AtUtc { get; init; } = DateTime.UtcNow;

    public List<ProbeCheck> Checks { get; } = [];

    /// <summary>Machine-readable results, persisted with the report (§4.2).</summary>
    public Dictionary<string, object?> Capabilities { get; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Capability probe (§4.2). Everything happens inside <c>&lt;root&gt;/.webdavsync-probe-&lt;random&gt;</c>, removed afterwards.
/// </summary>
public sealed class WebDavProbe(WebDavClient client, ILogger logger)
{
    private static readonly DateTime ProbeMtime = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly byte[] ProbeContent = Encoding.UTF8.GetBytes("WebDavSync capability probe file (longer than 20 bytes).\n");

    public async Task<ProbeReport> RunAsync(string endpoint, string user, string root, CancellationToken ct)
    {
        var report = new ProbeReport { Endpoint = endpoint, User = user };
        root = "/" + root.Trim('/');
        var dir = PathKey.ToRemoteFull(root, ".webdavsync-probe-" + Guid.NewGuid().ToString("N")[..8]);

        // 1. Auth + Depth 0 with allprop on the root.
        try
        {
            var (_, rootProps) = await client.PropfindAsync(root, "0", WebDavClient.AllPropBody, true, ct).ConfigureAwait(false);
            Add("Auth (Basic, app password)", true, "PROPFIND Depth: 0 accepted");
            report.Capabilities["RootProperties"] = rootProps.FirstOrDefault()?.OkProps.Keys.Select(k => k.ToString()).ToList();
        }
        catch (RemoteException ex)
        {
            Add("Auth (Basic, app password)", false, ex.Message);
            report.Capabilities["AuthOk"] = false;
            return report;
        }

        report.Capabilities["AuthOk"] = true;
        await Check("PROPFIND Depth: 1", "Depth1", async () =>
        {
            var listing = await client.ListAsync(root, ct).ConfigureAwait(false);
            return (true, $"{listing.Children.Count} children, complete={listing.Complete}");
        }).ConfigureAwait(false);

        var quota = await Try(() => client.GetQuotaAsync(ct)).ConfigureAwait(false);
        report.Capabilities["QuotaUsedBytes"] = quota?.UsedBytes;
        report.Capabilities["QuotaAvailableBytes"] = quota?.AvailableBytes;
        Add("Quota properties (RFC 4331)", quota is not null, quota is null ? "not returned" : $"used {quota.UsedBytes?.ToString(CultureInfo.InvariantCulture) ?? "?"}, available {quota.AvailableBytes?.ToString(CultureInfo.InvariantCulture) ?? "?"}");

        try
        {
            await client.MkdirAsync(dir, ct).ConfigureAwait(false);
            Add("MKCOL", true, dir);
            await RunFileChecksAsync(report, dir, ct).ConfigureAwait(false);
        }
        catch (RemoteException ex)
        {
            Add("Probe aborted", false, ex.Message);
        }
        finally
        {
            try
            {
                await client.DeleteAsync(dir, CancellationToken.None).ConfigureAwait(false);
            }
            catch (RemoteException ex)
            {
                Add("Cleanup", false, $"could not delete {dir}: {ex.Message}");
            }
        }

        Add("DELETE goes to the cloud trash", null, "check manually in the web UI (§18 #5)");
        Add("Maximum upload size / 507 on quota", null, "not tested");
        Add("Rate limits (429/503, Retry-After)", null, "not tested");
        return report;

        void Add(string name, bool? ok, string detail)
        {
            report.Checks.Add(new ProbeCheck(name, ok, detail));
            logger.LogInformation("Probe {Name}: {Ok} {Detail}", name, ok, detail);
        }

        async Task Check(string name, string capability, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action().ConfigureAwait(false);
                report.Capabilities[capability] = ok;
                Add(name, ok, detail);
            }
            catch (RemoteException ex)
            {
                report.Capabilities[capability] = false;
                Add(name, false, ex.Message);
            }
        }
    }

    private async Task RunFileChecksAsync(ProbeReport report, string dir, CancellationToken ct)
    {
        void Add(string name, bool? ok, string detail, string? capability = null, object? value = null)
        {
            report.Checks.Add(new ProbeCheck(name, ok, detail));
            if (capability is not null)
            {
                report.Capabilities[capability] = value ?? ok;
            }

            logger.LogInformation("Probe {Name}: {Ok} {Detail}", name, ok, detail);
        }

        var expectedHash = ContentHash.Compute(ProbeContent);
        var file = dir + "/Probe.txt";
        await PutAsync(file, [("X-OC-Mtime", ToUnix(ProbeMtime))]).ConfigureAwait(false);
        Add("PUT", true, file);

        var (_, fileProps) = await client.PropfindAsync(file, "0", WebDavClient.AllPropBody, false, ct).ConfigureAwait(false);
        var props = fileProps.FirstOrDefault()?.OkProps ?? new Dictionary<XName, XElement>();
        report.Capabilities["FileProperties"] = props.Keys.Select(k => k.ToString()).ToList();
        Add("File properties (allprop)", props.Count > 0, string.Join(", ", props.Keys.Select(k => k.LocalName)));
        Add("getetag on files", props.ContainsKey(PropfindParser.Dav + "getetag"), string.Empty, "FileETag");

        var hashLike = props.Where(p => ContentHash.TryParse(p.Value.Value.Trim(), out _)).ToList();
        var hashProp = hashLike.FirstOrDefault(p => ContentHash.Parse(p.Value.Value.Trim()) == expectedHash).Key;
        Add(
            "Content hash property (§3.3)",
            hashProp is not null,
            hashProp is not null ? hashProp.ToString() : hashLike.Count > 0 ? "40-hex properties found but none matches: " + string.Join(", ", hashLike.Select(p => p.Key.LocalName)) : "none",
            "HashProperty",
            hashProp?.ToString());

        var stat = await client.StatAsync(file, ct).ConfigureAwait(false);
        Add("mtime via X-OC-Mtime on PUT", stat?.MtimeUtc == ProbeMtime, $"getlastmodified = {stat?.MtimeUtc:u}", "MtimeViaOcHeader");

        var insensitive = await client.StatAsync(dir + "/probe.txt", ct).ConfigureAwait(false) is not null;
        Add("Case-insensitive names", insensitive, insensitive ? "probe.txt found Probe.txt" : "probe.txt not found: case-sensitive", "CaseInsensitive");

        try
        {
            var (status, _) = await client.PropfindAsync(dir, "infinity", WebDavClient.ListBody, true, ct).ConfigureAwait(false);
            Add("PROPFIND Depth: infinity", true, ((int)status).ToString(CultureInfo.InvariantCulture), "DepthInfinity");
        }
        catch (RemoteException ex)
        {
            Add("PROPFIND Depth: infinity", false, ex.Message, "DepthInfinity");
        }

        // Folder etag/mtime propagation (incremental crawl, §4.3).
        var sub = dir + "/sub";
        await client.MkdirAsync(sub, ct).ConfigureAwait(false);
        var dirBefore = await client.StatAsync(dir, ct).ConfigureAwait(false);
        var subBefore = await client.StatAsync(sub, ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
        await PutAsync(sub + "/x.txt", null).ConfigureAwait(false);
        var dirAfter = await client.StatAsync(dir, ct).ConfigureAwait(false);
        var subAfter = await client.StatAsync(sub, ct).ConfigureAwait(false);
        Add("Folder etag/mtime changes on child change", Changed(subBefore, subAfter), Describe(subBefore, subAfter), "FolderChangesOnChild");
        Add("... and on grandchild change", Changed(dirBefore, dirAfter), Describe(dirBefore, dirAfter), "FolderChangesOnDescendant");

        // Setting mtime after upload.
        var patched = dir + "/Patched.txt";
        await PutAsync(patched, null).ConfigureAwait(false);
        var win32 = $"""<?xml version="1.0" encoding="utf-8"?><d:propertyupdate xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:"><d:set><d:prop><z:Win32LastModifiedTime>{ProbeMtime:r}</z:Win32LastModifiedTime></d:prop></d:set></d:propertyupdate>""";
        var (patchStatus, _) = await client.ProppatchAsync(patched, win32, ct).ConfigureAwait(false);
        var afterPatch = await client.StatAsync(patched, ct).ConfigureAwait(false);
        Add("mtime via PROPPATCH Win32LastModifiedTime", afterPatch?.MtimeUtc == ProbeMtime, $"status {(int)patchStatus}, getlastmodified = {afterPatch?.MtimeUtc:u}", "MtimeViaProppatchWin32");

        var davPatch = $"""<?xml version="1.0" encoding="utf-8"?><d:propertyupdate xmlns:d="DAV:"><d:set><d:prop><d:getlastmodified>{ProbeMtime:r}</d:getlastmodified></d:prop></d:set></d:propertyupdate>""";
        var patched2 = dir + "/Patched2.txt";
        await PutAsync(patched2, null).ConfigureAwait(false);
        var (davStatus, _) = await client.ProppatchAsync(patched2, davPatch, ct).ConfigureAwait(false);
        var afterDav = await client.StatAsync(patched2, ct).ConfigureAwait(false);
        Add("mtime via PROPPATCH getlastmodified", afterDav?.MtimeUtc == ProbeMtime, $"status {(int)davStatus}, getlastmodified = {afterDav?.MtimeUtc:u}", "MtimeViaProppatchDav");

        // MOVE of a file keeps etag/mtime (remote move detection, §6.3)?
        var beforeMove = await client.StatAsync(patched, ct).ConfigureAwait(false);
        await client.MoveAsync(patched, dir + "/Moved.txt", false, ct).ConfigureAwait(false);
        var afterMove = await client.StatAsync(dir + "/Moved.txt", ct).ConfigureAwait(false);
        Add("MOVE file", afterMove is not null, Describe(beforeMove, afterMove), "MoveFile");
        Add("MOVE keeps etag", beforeMove?.ETag is not null && beforeMove.ETag == afterMove?.ETag, $"{beforeMove?.ETag} -> {afterMove?.ETag}", "MoveKeepsETag");
        Add("MOVE keeps mtime", beforeMove?.MtimeUtc == afterMove?.MtimeUtc, $"{beforeMove?.MtimeUtc:u} -> {afterMove?.MtimeUtc:u}", "MoveKeepsMtime");

        await client.MoveAsync(sub, dir + "/sub2", false, ct).ConfigureAwait(false);
        var folderMoved = await client.StatAsync(dir + "/sub2/x.txt", ct).ConfigureAwait(false) is not null &&
                          await client.StatAsync(sub, ct).ConfigureAwait(false) is null;
        Add("MOVE folder (server-side, with content)", folderMoved, string.Empty, "MoveFolder");

        await client.DeleteAsync(dir + "/Moved.txt", ct).ConfigureAwait(false);
        var deleted = await client.StatAsync(dir + "/Moved.txt", ct).ConfigureAwait(false) is null;
        Add("DELETE file", deleted, string.Empty, "Delete");

        Task PutAsync(string path, IEnumerable<(string, string)>? headers) =>
            client.PutAsync(path, new MemoryStream(ProbeContent), ProbeContent.Length, headers, ct);
    }

    private static bool Changed(RemoteItem? before, RemoteItem? after) =>
        before is not null && after is not null && (before.ETag != after.ETag || before.MtimeUtc != after.MtimeUtc);

    private static string Describe(RemoteItem? before, RemoteItem? after) =>
        $"etag {before?.ETag ?? "-"} -> {after?.ETag ?? "-"}, mtime {before?.MtimeUtc:u} -> {after?.MtimeUtc:u}";

    private static string ToUnix(DateTime utc) =>
        ((long)(utc - DateTime.UnixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static async Task<T?> Try<T>(Func<Task<T?>> action)
        where T : class
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (RemoteException)
        {
            return null;
        }
    }
}
