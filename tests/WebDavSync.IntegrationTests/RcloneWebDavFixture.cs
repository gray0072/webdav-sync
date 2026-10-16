using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WebDavSync.Core.Remote;

namespace WebDavSync.IntegrationTests;

/// <summary>
/// <c>rclone serve webdav</c> in Docker (§19.4). Tests are skipped when Docker is not running.
/// The credentials are local to the throw-away container.
/// </summary>
public sealed class RcloneWebDavFixture : IAsyncLifetime
{
    public const string User = "wds";
    public const string Password = "wds-test-password";
    private const string Image = "rclone/rclone:latest";

    private string? _containerId;

    public bool Available { get; private set; }

    public string SkipReason { get; private set; } = "Docker is not running.";

    public Uri Endpoint { get; private set; } = new("http://127.0.0.1/");

    public WebDavClient CreateClient(string? password = null) =>
        new(new WebDavOptions { Endpoint = Endpoint, User = User, Password = password ?? Password, MaxRequestsPerSecond = 0, MaxAttempts = 2 }, NullLogger.Instance);

    public async ValueTask InitializeAsync()
    {
        var (code, _) = await DockerAsync("version --format {{.Server.Version}}");
        if (code != 0)
        {
            return;
        }

        var (runCode, id) = await DockerAsync($"run -d --rm -p 127.0.0.1::8080 {Image} serve webdav /data --addr :8080 --user {User} --pass {Password}");
        if (runCode != 0)
        {
            SkipReason = "Cannot start the rclone container: " + id;
            return;
        }

        _containerId = id.Trim();
        var (_, port) = await DockerAsync($"port {_containerId} 8080");
        var hostPort = port.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Endpoint = new Uri($"http://{hostPort}/");

        using var client = CreateClient();
        for (var i = 0; i < 50; i++)
        {
            try
            {
                await client.StatAsync("/", CancellationToken.None);
                Available = true;
                return;
            }
            catch (RemoteException)
            {
                await Task.Delay(200);
            }
        }

        SkipReason = "The rclone WebDAV server did not start.";
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerId is not null)
        {
            await DockerAsync($"rm -f {_containerId}");
        }
    }

    private static async Task<(int Code, string Output)> DockerAsync(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, process.ExitCode == 0 ? output : error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RcloneCollection : ICollectionFixture<RcloneWebDavFixture>
{
    public const string Name = "rclone";
}

internal static class HttpStatusExtensions
{
    public static bool IsNotFound(this RemoteException ex) => ex.Status == HttpStatusCode.NotFound;
}
