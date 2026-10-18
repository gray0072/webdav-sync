using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using WebDavSync.Core.Updates;

namespace WebDavSync.App;

/// <summary>"A new version is available" (§14.3): versions with dates, release notes, install and restart.</summary>
internal sealed class UpdateForm : ScaledForm
{
    private readonly ReleaseInfo _release;
    private readonly HttpClient _http;
    private readonly Label _status = WrapLabel(margin: new Padding(3));
    private readonly Button _install = new() { Text = "Install and restart" };
    private readonly Button _page = new() { Text = "Release page" };
    private readonly Button _later = new() { Text = "Later", DialogResult = DialogResult.Cancel };
    private CancellationTokenSource? _cts;

    public UpdateForm(ReleaseInfo release, HttpClient http)
    {
        _release = release;
        _http = http;
        Text = "WebDavSync update";
        Size = new Size(640, 520);
        MinimumSize = new Size(480, 360);
        StartPosition = FormStartPosition.CenterScreen;
        CancelButton = _later;

        var released = release.PublishedUtc is { } at ? $", released {at.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}" : string.Empty;
        var title = WrapLabel($"WebDavSync {release.Version} is available{released}.");
        title.Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold);
        var current = WrapLabel($"You have {UpdateChecker.CurrentDescription}. Your settings, passwords and sync state are kept.", new Padding(8, 0, 8, 8));
        var notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = PlainText(release.Notes),
            Margin = new Padding(8, 0, 8, 0),
            BackColor = SystemColors.Window,
        };
        _install.Enabled = release.ZipUrl is not null && release.ChecksumUrl is not null;
        Shown += (_, _) =>
        {
            notes.Select(0, 0);
            ActiveControl = _install.Enabled ? _install : _page;
        };
        Controls.Add(Rows(notes, title, current, notes, ButtonBar(_status, _install, _page, _later)));

        _page.Click += (_, _) => Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true })?.Dispose();
        _install.Click += async (_, _) => await InstallAsync();
        FormClosing += (_, _) => _cts?.Cancel();
    }

    /// <summary>The downloaded and verified exe (DialogResult OK): the caller swaps it in and restarts.</summary>
    public string? NewExe { get; private set; }

    /// <summary>Release notes (Markdown) as plain text: no heading marks, emphasis or code ticks; links keep their text.</summary>
    internal static string PlainText(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line =>
        {
            line = Regex.Replace(line, @"^#{1,6}\s+", string.Empty);
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return line.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        });
        return string.Join("\r\n", lines);
    }

    private async Task InstallAsync()
    {
        _install.Enabled = _page.Enabled = false;
        _cts = new CancellationTokenSource();
        try
        {
            NewExe = await Updater.DownloadAsync(_http, _release, new Progress<string>(s => _status.Text = s), _cts.Token);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "Update failed: " + ex.Message;
            _install.Enabled = _page.Enabled = true;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
