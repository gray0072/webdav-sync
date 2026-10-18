using Microsoft.Win32;
using WebDavSync.Core.Config;

namespace WebDavSync.App;

/// <summary>
/// Settings of an earlier copy of the app (§15.1): a new release unpacked into another folder starts with an empty
/// <c>data\</c>, so it copies <c>settings.json</c> of the copy that ran last. Passwords (Credential Manager, by pair id)
/// and pair state (inside the synced folders) do not depend on the app folder, so the settings are all it needs.
/// </summary>
internal static class PreviousInstall
{
    private const string AppKey = @"Software\WebDavSync";
    private const string DataDirValue = "DataDir";

    /// <summary>
    /// Takes over the previous settings when this copy has none; returns the folder they came from, or <c>null</c>.
    /// Only for the data folder next to this exe: an explicit other folder (tests, scripts) is left alone.
    /// </summary>
    public static string? Adopt(AppPaths paths)
    {
        if (!IsOwnDataDir(paths) || File.Exists(paths.SettingsFile))
        {
            return null;
        }

        foreach (var candidate in new[] { RecordedDataDir(), TrayContext.StartupDataDir() })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var previous = new AppPaths(candidate);
            if (string.Equals(previous.DataDir, paths.DataDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(previous.SettingsFile))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(paths.DataDir);
                File.Copy(previous.SettingsFile, paths.SettingsFile);
                return previous.DataDir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: try the next candidate, else start as a fresh install.
            }
        }

        return null;
    }

    /// <summary>Remembers this copy's data folder for the next one; only the data folder next to the exe.</summary>
    public static void Record(AppPaths paths)
    {
        if (!IsOwnDataDir(paths))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue(DataDirValue, paths.DataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best effort: without it the next copy still finds the autostart entry.
        }
    }

    private static string? RecordedDataDir()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppKey);
        return key?.GetValue(DataDirValue) as string;
    }

    private static bool IsOwnDataDir(AppPaths paths) =>
        string.Equals(paths.DataDir, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data")), StringComparison.OrdinalIgnoreCase);
}
