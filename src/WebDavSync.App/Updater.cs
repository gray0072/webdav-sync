using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using WebDavSync.Core.Updates;

namespace WebDavSync.App;

/// <summary>
/// Installs a release over the running app (§14.3): download the zip, check its SHA-256 against the published
/// checksum, put the new <c>WebDavSync.exe</c> next to the running one, then swap and restart. A running exe can be
/// renamed on Windows, so the old one becomes <c>WebDavSync.exe.old</c> and is deleted by the next start.
/// </summary>
internal static class Updater
{
    public const string ExeName = "WebDavSync.exe";

    public static string OldExe => Environment.ProcessPath! + ".old";

    /// <summary>Downloads and verifies the release; returns the path of the new exe, ready to swap in.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseInfo release, IProgress<string> status, CancellationToken ct)
    {
        if (release.ZipUrl is null || release.ChecksumUrl is null)
        {
            throw new InvalidOperationException("This release has no Windows download; use the release page.");
        }

        var work = Directory.CreateTempSubdirectory("WebDavSync-update-");
        try
        {
            status.Report("Downloading…");
            var zip = Path.Combine(work.FullName, "release.zip");
            await using (var source = await http.GetStreamAsync(release.ZipUrl, ct))
            await using (var target = File.Create(zip))
            {
                await source.CopyToAsync(target, ct);
            }

            status.Report("Checking the download…");
            var expected = (await http.GetStringAsync(release.ChecksumUrl, ct)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            string actual;
            await using (var file = File.OpenRead(zip))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            }

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The download is damaged (checksum mismatch); nothing was changed.");
            }

            var newExe = Environment.ProcessPath! + ".new";
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.GetEntry(ExeName) ?? throw new InvalidDataException($"The download has no {ExeName}.");
                entry.ExtractToFile(newExe, overwrite: true);
            }

            return newExe;
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>Swaps the new exe in and starts it; the caller exits right after. The new process waits for this one.</summary>
    public static void SwapAndRestart(string newExe, string dataDir)
    {
        var current = Environment.ProcessPath!;
        File.Move(current, OldExe, overwrite: true);
        try
        {
            File.Move(newExe, current);
        }
        catch
        {
            File.Move(OldExe, current);
            throw;
        }

        Process.Start(new ProcessStartInfo(current)
        {
            ArgumentList = { "--data-dir", dataDir, "--wait-for-exit", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            UseShellExecute = false,
        })?.Dispose();
    }

    /// <summary>On start: removes the previous exe left by an update.</summary>
    public static void CleanUp()
    {
        try
        {
            File.Delete(OldExe);
            File.Delete(Environment.ProcessPath! + ".new");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
