using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Remote;

namespace WebDavSync.App;

/// <summary>App-wide services: data folder, settings, file log (§15.1) and one <see cref="PairRuntime"/> per pair.</summary>
internal sealed class AppHost : IDisposable
{
    private readonly Serilog.Core.Logger _serilog;
    private readonly SerilogLoggerFactory _factory;

    public AppHost(AppPaths paths)
    {
        Paths = paths;
        _serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Command", "app")
            .WriteTo.File(
                Path.Combine(paths.LogsDir, "wds-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _factory = new SerilogLoggerFactory(_serilog);
        Logger = _factory.CreateLogger("app");
        Secrets = new SecretStore();
        var settings = AppSettings.Load(paths.SettingsFile);
        Pairs = settings.Pairs.Select(p => new PairRuntime(p, this)).ToList();
        Logger.LogInformation("App started: {Pairs} pairs, data {DataDir}", Pairs.Count, paths.DataDir);
    }

    public AppPaths Paths { get; }

    public Microsoft.Extensions.Logging.ILogger Logger { get; }

    public SecretStore Secrets { get; }

    public IReadOnlyList<PairRuntime> Pairs { get; }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string name) => _factory.CreateLogger(name);

    /// <summary>Writes one setting change to <c>settings.json</c>, re-reading it first so CLI edits are not lost.</summary>
    public void SaveSetting(PairSettings pair, Action<PairSettings> change)
    {
        var settings = AppSettings.Load(Paths.SettingsFile);
        var stored = settings.Find(pair.Id) ?? throw new InvalidOperationException($"Pair {pair.Name} is no longer in settings.json.");
        change(stored);
        settings.Save(Paths.SettingsFile);
        change(pair);
    }

    public void Dispose()
    {
        Logger.LogInformation("App stopped");
        _factory.Dispose();
        _serilog.Dispose();
    }
}

/// <summary>A pair's WebDAV client and background service; restarted when the password changes.</summary>
internal sealed class PairRuntime(PairSettings pair, AppHost host)
{
    private WebDavClient? _client;

    public PairSettings Pair { get; } = pair;

    public PairService? Service { get; private set; }

    /// <summary>The WebDAV client, while the pair runs.</summary>
    public IRemoteStore? Remote => _client;

    /// <summary>Why the service is not running (e.g. no password).</summary>
    public string? StartError { get; private set; }

    public event EventHandler? Restarted;

    public void Start()
    {
        string? password;
        try
        {
            password = host.Secrets.Get(Pair.Id) ?? Environment.GetEnvironmentVariable("WDS_PASSWORD");
        }
        catch (InvalidDataException ex)
        {
            StartError = ex.Message;
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            StartError = "No password: use 'Set password…'";
            return;
        }

        StartError = null;
        _client = new WebDavClient(
            new WebDavOptions
            {
                Endpoint = new Uri(Pair.RemoteUrl),
                User = Pair.User,
                Password = password,
                MaxRequestsPerSecond = Pair.MaxRequestsPerSecond,
                HashProperty = Pair.RemoteHashProperty is null ? null : XName.Get(Pair.RemoteHashProperty),
            },
            host.CreateLogger("webdav"));
        Service = new PairService(Pair, _client, host.CreateLogger("pair"));
        Service.Start();
    }

    public async Task StopAsync()
    {
        if (Service is not null)
        {
            await Service.DisposeAsync().ConfigureAwait(false);
            Service = null;
        }

        _client?.Dispose();
        _client = null;
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(true);
        Start();
        Restarted?.Invoke(this, EventArgs.Empty);
    }
}
