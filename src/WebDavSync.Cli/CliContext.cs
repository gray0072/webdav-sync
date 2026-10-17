using System.Text.Json;
using System.Xml.Linq;
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

    public SecretStore Secrets { get; } = new();

    /// <summary>
    /// The password never comes from arguments or files in the repo: the Windows Credential Manager for a pair (§15.1), otherwise
    /// the environment variable (probe, scripts).
    /// </summary>
    public string RequirePassword(PairSettings? pair = null) =>
        (pair is null ? null : Secrets.Get(pair.Id)) ??
        (Environment.GetEnvironmentVariable(PasswordVariable) is { Length: > 0 } password
            ? password
            : throw new CliException(pair is null
                ? $"Set the WebDAV app password in the {PasswordVariable} environment variable."
                : $"No password for pair '{pair.Name}': run 'wds secret set {pair.Name}' (or set {PasswordVariable})."));

    public WebDavClient CreateClient(string url, string user, double maxRequestsPerSecond = 5, string? hashProperty = null, string? password = null) =>
        new(
            new WebDavOptions
            {
                Endpoint = new Uri(url),
                User = user,
                Password = password ?? RequirePassword(),
                MaxRequestsPerSecond = maxRequestsPerSecond,
                HashProperty = hashProperty is null ? null : XName.Get(hashProperty),
            },
            _factory.CreateLogger("webdav"));

    public WebDavClient CreateClient(PairSettings pair) =>
        CreateClient(pair.RemoteUrl, pair.User, pair.MaxRequestsPerSecond, pair.RemoteHashProperty, RequirePassword(pair));

    /// <summary>The content hash property from the newest stored probe report for the endpoint's host, if any.</summary>
    public string? FindProbedHashProperty(string url)
    {
        var latest = Directory.EnumerateFiles(Paths.DataDir, $"probe-{new Uri(url).Host}-*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        using var json = JsonDocument.Parse(File.ReadAllText(latest));
        return json.RootElement.TryGetProperty("Capabilities", out var caps) &&
               caps.TryGetProperty("HashProperty", out var hash) &&
               hash.ValueKind == JsonValueKind.String
            ? hash.GetString()
            : null;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _serilog.Dispose();
    }
}

internal sealed class CliException(string message) : Exception(message);
