using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Remote.MailRu;

namespace WebDavSync.Core.Remote;

/// <summary>Creates a pair's remote client: WebDAV, plus the Mail.ru API on a Mail.ru endpoint (§4.7).</summary>
public static class RemoteClientFactory
{
    public static IRemoteClient Create(PairSettings pair, string password, ILogger webDavLogger, ILogger apiLogger)
    {
        var endpoint = new Uri(pair.RemoteUrl);
        var webDav = new WebDavClient(
            new WebDavOptions
            {
                Endpoint = endpoint,
                User = pair.User,
                Password = password,
                MaxRequestsPerSecond = pair.MaxRequestsPerSecond,
                HashProperty = pair.RemoteHashProperty is null ? null : XName.Get(pair.RemoteHashProperty),
            },
            webDavLogger);
        if (!pair.UseMailRuApi || !MailRuApi.IsMailRuEndpoint(endpoint))
        {
            return webDav;
        }

        var api = new MailRuApi(
            new MailRuApiOptions { User = pair.User, Password = password, MaxRequestsPerSecond = pair.MaxRequestsPerSecond },
            apiLogger);
        return new MailRuStore(webDav, api, hashKnown: pair.RemoteHashProperty is not null, apiLogger);
    }
}
