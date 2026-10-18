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
        LowPriority = settings.LowPriority;
        CpuLimitPercent = settings.CpuLimitPercent;
        ApplyLimits();
        _pairs = settings.Pairs.Select(p => new PairRuntime(p, this)).ToList();
        Logger.LogInformation("App started: {Pairs} pairs, data {DataDir}", Pairs.Count, paths.DataDir);
    }

    public AppPaths Paths { get; }

    public Microsoft.Extensions.Logging.ILogger Logger { get; }

    public SecretStore Secrets { get; }

    private readonly List<PairRuntime> _pairs;

    public IReadOnlyList<PairRuntime> Pairs => _pairs;

    /// <summary>
    /// Creates a pair from the setup wizard (§14): local state (or the reused state of an earlier setup), optional
    /// legacy import, <c>settings.json</c>, the password. The caller starts the returned runtime.
    /// </summary>
    public PairRuntime CreatePair(PairSettings pair, string password, bool importLegacy, bool resetState)
    {
        var settings = AppSettings.Load(Paths.SettingsFile);
        if (settings.Find(pair.Id) is not null || settings.Pairs.Any(p => string.Equals(Path.GetFullPath(p.LocalRoot), Path.GetFullPath(pair.LocalRoot), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{pair.LocalRoot}' is already synced by this app.");
        }

        PairSetup.InitializeLocal(pair, resetState);
        if (importLegacy)
        {
            PairSetup.ImportLegacy(pair, force: false);
        }

        Secrets.Set(pair.Id, password, pair.User);
        settings.Pairs.Add(pair);
        settings.Save(Paths.SettingsFile);
        Logger.LogInformation("Pair {Pair} ({Id}) created by the setup wizard: {Local} <-> {Url}{Root}, mode {Mode}, legacy import {Import}",
            pair.Name, pair.Id, pair.LocalRoot, pair.RemoteUrl, pair.RemoteRootNormalized, pair.Mode, importLegacy);
        var runtime = new PairRuntime(pair, this);
        _pairs.Add(runtime);
        return runtime;
    }

    public bool LowPriority { get; private set; }

    public int CpuLimitPercent { get; private set; }

    /// <summary>Changes and saves the app-level limits (§11).</summary>
    /// <summary>Changes app-level settings in <c>settings.json</c>, re-reading it first so CLI edits are not lost.</summary>
    public void SaveAppSettings(Action<AppSettings> change)
    {
        var settings = AppSettings.Load(Paths.SettingsFile);
        change(settings);
        settings.Save(Paths.SettingsFile);
    }

    public void SetLimits(bool lowPriority, int cpuLimitPercent)
    {
        var settings = AppSettings.Load(Paths.SettingsFile);
        settings.LowPriority = LowPriority = lowPriority;
        settings.CpuLimitPercent = CpuLimitPercent = cpuLimitPercent;
        settings.Save(Paths.SettingsFile);
        ApplyLimits();
    }

    private void ApplyLimits()
    {
        try
        {
            ProcessLimits.SetBackground(LowPriority);
            ProcessLimits.SetCpuLimit(CpuLimitPercent);
            Logger.LogInformation("Process limits: background mode {Background}, CPU cap {Cap}%", LowPriority, CpuLimitPercent);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Logger.LogWarning(ex, "Cannot apply process limits");
        }
    }

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

/// <summary>A pair's remote client and background service; restarted when the password changes.</summary>
internal sealed class PairRuntime(PairSettings pair, AppHost host)
{
    private IRemoteClient? _client;

    public PairSettings Pair { get; } = pair;

    public PairService? Service { get; private set; }

    /// <summary>The remote client, while the pair runs.</summary>
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
        _client = RemoteClientFactory.Create(Pair, password, host.CreateLogger("webdav"), host.CreateLogger("mailru"));
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
