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

        // After an update the new exe is started by the old one: wait until it has exited and released the mutex.
        var wait = Array.IndexOf(args, "--wait-for-exit");
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out var oldPid))
        {
            try
            {
                using var old = System.Diagnostics.Process.GetProcessById(oldPid);
                old.WaitForExit(TimeSpan.FromMinutes(5));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        Updater.CleanUp();

        // One instance per Windows user, whatever folder it runs from: two copies would sync the same folders twice.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\WebDavSync", out var created);
        if (!created)
        {
            MessageBox.Show("WebDavSync is already running — look for the cloud icon in the notification area (you may need to click ^ to see it).", "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // A new release unpacked into another folder: keep the settings of the copy that ran last (§15.1).
        var adoptedFrom = PreviousInstall.Adopt(paths);
        PreviousInstall.Record(paths);

        using var host = new AppHost(paths);
        Application.Run(new TrayContext(host, adoptedFrom));
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
