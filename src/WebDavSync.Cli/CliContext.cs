using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Remote;

namespace WebDavSync.Cli;

/// <summary>Per-invocation services: app paths, settings, file logging (§15.1), credentials.</summary>
internal sealed class CliContext : IDisposable
{
    public const string PasswordVariable = "WDS_PASSWORD";

    private readonly Serilog.Core.Logger _serilog;
    private readonly SerilogLoggerFactory _factory;

    public CliContext(string? dataDir, string command)
    {
        Paths = dataDir is null ? AppPaths.Default() : new AppPaths(dataDir);
        Directory.CreateDirectory(Paths.DataDir);
        Settings = AppSettings.Load(Paths.SettingsFile);
        _serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Command", command)
            .WriteTo.File(
                Path.Combine(Paths.LogsDir, "wds-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _factory = new SerilogLoggerFactory(_serilog);
        Logger = _factory.CreateLogger("wds");
    }

    public AppPaths Paths { get; }

    public AppSettings Settings { get; }

    public Microsoft.Extensions.Logging.ILogger Logger { get; }

    public void SaveSettings() => Settings.Save(Paths.SettingsFile);

    public PairSettings RequirePair(string idOrName) =>
        Settings.Find(idOrName) ?? throw new CliException($"Pair '{idOrName}' not found in {Paths.SettingsFile}. Use 'wds pair list'.");

    /// <summary>The password comes only from the environment (§19.6): never from arguments or files in the repo.</summary>
    public static string RequirePassword() =>
        Environment.GetEnvironmentVariable(PasswordVariable) is { Length: > 0 } password
            ? password
            : throw new CliException($"Set the WebDAV app password in the {PasswordVariable} environment variable.");

    public WebDavClient CreateClient(string url, string user, double maxRequestsPerSecond = 5) =>
        new(
            new WebDavOptions
            {
                Endpoint = new Uri(url),
                User = user,
                Password = RequirePassword(),
                MaxRequestsPerSecond = maxRequestsPerSecond,
            },
            _factory.CreateLogger("webdav"));

    public void Dispose()
    {
        _factory.Dispose();
        _serilog.Dispose();
    }
}

internal sealed class CliException(string message) : Exception(message);
