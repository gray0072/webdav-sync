using System.Security.Cryptography;
using System.Text;
using WebDavSync.Core.Config;

namespace WebDavSync.App;

internal static class Program
{
    /// <summary>Tray app (§14). Arguments: <c>--data-dir &lt;path&gt;</c> (default: <c>&lt;app dir&gt;\data</c> or WDS_DATA_DIR).</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var index = Array.IndexOf(args, "--data-dir");
        var paths = index >= 0 && index + 1 < args.Length ? new AppPaths(args[index + 1]) : AppPaths.Default();
        if (!IsWritable(paths.DataDir, out var error))
        {
            // §15.1: no silent fallback to %APPDATA%.
            MessageBox.Show($"The data folder '{paths.DataDir}' is not writable:\n{error}\n\nRun the app from a writable folder.", "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // One instance per data folder (§15.1 app.lock + named mutex).
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDir.ToUpperInvariant())))[..16];
        using var mutex = new Mutex(initiallyOwned: true, $@"Local\WebDavSync-{id}", out var created);
        if (!created)
        {
            MessageBox.Show("WebDavSync is already running for this data folder (see the tray).", "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        using var host = new AppHost(paths);
        Application.Run(new TrayContext(host));
        return 0;
    }

    private static bool IsWritable(string dir, out string? error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
