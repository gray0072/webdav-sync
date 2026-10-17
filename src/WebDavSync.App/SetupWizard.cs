using System.Diagnostics;
using System.Xml.Linq;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Legacy;
using WebDavSync.Core.Remote;

namespace WebDavSync.App;

/// <summary>
/// First-run and "Add folder" wizard (§14.2): cloud account → folders → how careful. Checks the connection, browses
/// the cloud, reuses the state of an earlier setup of the same folder and offers the legacy Mail.ru import.
/// </summary>
internal sealed class SetupWizard : ScaledForm
{
    private const string MailRuUrl = "https://webdav.cloud.mail.ru/";
    private const string MailRuPasswordsUrl = "https://account.mail.ru/user/2-step-auth/passwords/";

    private readonly AppHost _host;
    private readonly Label _title = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 13f, FontStyle.Bold) };
    private readonly Label _subtitle = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8) };
    private readonly Button _back = new() { Text = "< Back", AutoSize = true };
    private readonly Button _next = new() { Text = "Next >", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly List<(string Title, string Subtitle, Control Page)> _pages = [];
    private int _page;

    // Page 1: account.
    private readonly RadioButton _mailRu = new() { Text = "Mail.ru Cloud", AutoSize = true, Checked = true };
    private readonly RadioButton _otherServer = new() { Text = "Another WebDAV server", AutoSize = true };
    private readonly TextBox _url = new() { Width = 380, Text = MailRuUrl, Enabled = false };
    private readonly TextBox _login = new() { Width = 380, PlaceholderText = "you@mail.ru" };
    private readonly TextBox _password = new() { Width = 380, UseSystemPasswordChar = true };
    private readonly LinkLabel _passwordHelp = new() { AutoSize = true, Text = "Mail.ru needs an app password, not your usual one — create it here" };
    private readonly Label _accountStatus = new() { AutoSize = true, MaximumSize = new Size(540, 0) };

    // Page 2: folders.
    private readonly TextBox _local = new() { Width = 380 };
    private readonly TextBox _remoteRoot = new() { Width = 380, Text = "/" };
    private readonly Button _browseRemote = new() { Text = "Browse…", AutoSize = true };
    private readonly CheckBox _importLegacy = new() { AutoSize = true, Checked = true, Visible = false, Text = "Take over the old Mail.ru client's state (recommended: nothing is downloaded again)" };
    private readonly Label _folderInfo = new() { AutoSize = true, MaximumSize = new Size(540, 0) };

    // Page 3: how careful.
    private readonly RadioButton _approveAll = new() { AutoSize = true, Checked = true, Text = "Ask me before every change (recommended at the start)" };
    private readonly RadioButton _guarded = new() { AutoSize = true, Text = "Copy new files by itself; ask before deleting, overwriting or moving" };
    private readonly RadioButton _auto = new() { AutoSize = true, Text = "Sync everything by itself (safety guards still stop mass deletions)" };
    private readonly CheckBox _startup = new() { AutoSize = true, Checked = true, Text = "Start WebDavSync with Windows" };
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = SystemColors.GrayText };

    private WebDavClient? _client;
    private string? _hashProperty;
    private PairMarker? _existingState;

    public SetupWizard(AppHost host, bool firstRun)
    {
        _host = host;
        Text = firstRun ? "Welcome to WebDavSync" : "Add a folder — WebDavSync";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 470);

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 78, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20, 14, 20, 0), BackColor = SystemColors.Window };
        header.Controls.AddRange([_title, _subtitle]);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        footer.Controls.AddRange([_cancel, _next, _back]);
        Controls.Add(_content);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 1, BorderStyle = BorderStyle.FixedSingle });
        Controls.Add(header);
        Controls.Add(footer);
        CancelButton = _cancel;
        AcceptButton = _next;

        _pages.Add(("Connect your cloud", "Where should your files be synced to?", AccountPage()));
        _pages.Add(("Choose the folders", "Which folder on this computer, and which folder in the cloud?", FoldersPage()));
        _pages.Add(("How careful should it be?", "You can change this any time in the tray menu: Settings ▸ Mode.", ModePage()));

        _back.Click += (_, _) => Show(_page - 1);
        _next.Click += async (_, _) => await NextAsync();
        FormClosed += (_, _) => _client?.Dispose();
        Show(0);
    }

    /// <summary>The created pair (DialogResult OK).</summary>
    public PairRuntime? CreatedPair { get; private set; }

    public bool StartWithWindows => _startup.Checked;

    // ---------------------------------------------------------------- pages

    private Control AccountPage()
    {
        var page = Column();
        page.Controls.Add(_mailRu);
        page.Controls.Add(_otherServer);
        page.Controls.Add(Caption("Server address"));
        page.Controls.Add(_url);
        page.Controls.Add(Caption("Login"));
        page.Controls.Add(_login);
        page.Controls.Add(Caption("App password"));
        page.Controls.Add(_password);
        page.Controls.Add(_passwordHelp);
        page.Controls.Add(_accountStatus);
        _mailRu.CheckedChanged += (_, _) =>
        {
            _url.Enabled = !_mailRu.Checked;
            _passwordHelp.Visible = _mailRu.Checked;
            if (_mailRu.Checked)
            {
                _url.Text = MailRuUrl;
            }
        };
        _passwordHelp.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(MailRuPasswordsUrl) { UseShellExecute = true })?.Dispose();
        return page;
    }

    private Control FoldersPage()
    {
        var page = Column();
        page.Controls.Add(Caption("Folder on this computer"));
        var browseLocal = new Button { Text = "Browse…", AutoSize = true };
        page.Controls.Add(Row(_local, browseLocal));
        page.Controls.Add(Caption("Folder in the cloud"));
        page.Controls.Add(Row(_remoteRoot, _browseRemote));
        page.Controls.Add(_importLegacy);
        page.Controls.Add(_folderInfo);

        browseLocal.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Folder to keep in sync", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (Directory.Exists(_local.Text))
            {
                dialog.SelectedPath = _local.Text;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _local.Text = dialog.SelectedPath;
            }
        };
        _browseRemote.Click += (_, _) =>
        {
            using var dialog = new RemoteFolderDialog(_client!);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _remoteRoot.Text = dialog.SelectedPath;
            }
        };
        _local.TextChanged += (_, _) => AnalyzeFolder();
        return page;
    }

    private Control ModePage()
    {
        var page = Column();
        page.Controls.AddRange([_approveAll, _guarded, _auto, new Label { Height = 12 }, _startup, new Label { Height = 12 }, _summary]);
        return page;
    }

    // ---------------------------------------------------------------- navigation

    private void Show(int index)
    {
        _page = index;
        _content.Controls.Clear();
        _content.Controls.Add(_pages[index].Page);
        _title.Text = _pages[index].Title;
        _subtitle.Text = _pages[index].Subtitle;
        _back.Visible = index > 0;
        _next.Text = index == _pages.Count - 1 ? "Start syncing" : "Next >";
        if (index == 1 && _local.Text.Length == 0)
        {
            _local.Text = DefaultLocalFolder();
        }

        if (index == 2)
        {
            _summary.Text = $"{_local.Text}  ⇄  {_url.Text.TrimEnd('/')}{_remoteRoot.Text}\n\n" + (_existingState is not null
                ? "The existing sync state of this folder is reused."
                : _importLegacy.Visible && _importLegacy.Checked
                    ? "The old Mail.ru client's state is taken over; only differences are synced."
                    : "The first sync merges both sides and deletes nothing. You will see what it is going to do before it does it.");
        }
    }

    private async Task NextAsync()
    {
        _next.Enabled = _back.Enabled = false;
        UseWaitCursor = true;
        try
        {
            switch (_page)
            {
                case 0 when await CheckAccountAsync():
                    Show(1);
                    break;
                case 1 when CheckFolders():
                    Show(2);
                    break;
                case 2:
                    Finish();
                    break;
            }
        }
        finally
        {
            _next.Enabled = _back.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private async Task<bool> CheckAccountAsync()
    {
        if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http"))
        {
            return Fail(_accountStatus, "Enter the server address, e.g. https://webdav.example.com/");
        }

        if (_login.Text.Trim().Length == 0 || _password.Text.Length == 0)
        {
            return Fail(_accountStatus, "Enter the login and the app password.");
        }

        _accountStatus.ForeColor = SystemColors.ControlText;
        _accountStatus.Text = "Checking the connection…";
        _client?.Dispose();
        _client = null;
        var client = new WebDavClient(
            new WebDavOptions { Endpoint = endpoint, User = _login.Text.Trim(), Password = _password.Text, HashProperty = _mailRu.Checked ? XName.Get("{DAV:}getetag") : null },
            _host.CreateLogger("webdav"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (_mailRu.Checked)
            {
                // Mail.ru: getetag is the content hash (§4.4); one listing proves the login.
                await client.ListAsync("/", cts.Token);
                _hashProperty = "{DAV:}getetag";
            }
            else
            {
                // Other servers: the probe finds out what the server supports (it creates and deletes a test folder).
                var report = await new WebDavProbe(client, _host.CreateLogger("probe")).RunAsync(endpoint.ToString(), _login.Text.Trim(), "/", cts.Token);
                if (report.Capabilities.TryGetValue("AuthOk", out var ok) && ok is false)
                {
                    throw new RemoteException(RemoteErrorKind.Auth, report.Checks.FirstOrDefault()?.Detail ?? "login failed");
                }

                _hashProperty = report.Capabilities.GetValueOrDefault("HashProperty") as string;
                client.Dispose();
                client = new WebDavClient(
                    new WebDavOptions { Endpoint = endpoint, User = _login.Text.Trim(), Password = _password.Text, HashProperty = _hashProperty is null ? null : XName.Get(_hashProperty) },
                    _host.CreateLogger("webdav"));
            }

            _client = client;
            _accountStatus.ForeColor = Color.ForestGreen;
            _accountStatus.Text = "✔ Connected.";
            return true;
        }
        catch (Exception ex) when (ex is RemoteException or OperationCanceledException or HttpRequestException)
        {
            client.Dispose();
            return Fail(_accountStatus, ex is RemoteException { Kind: RemoteErrorKind.Auth }
                ? "The login or the app password is wrong." + (_mailRu.Checked ? " Mail.ru needs an app password (link below), not your usual password." : string.Empty)
                : "Cannot connect: " + ex.Message);
        }
    }

    /// <summary>Explains what will happen with the chosen local folder.</summary>
    private void AnalyzeFolder()
    {
        _existingState = null;
        _importLegacy.Visible = false;
        _remoteRoot.Enabled = _browseRemote.Enabled = true;
        _folderInfo.ForeColor = SystemColors.GrayText;
        var path = _local.Text.Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            _folderInfo.Text = string.Empty;
            return;
        }

        if (!Directory.Exists(path))
        {
            _folderInfo.Text = "The folder will be created, and the cloud folder's files are downloaded into it.";
            return;
        }

        var marker = new StatePaths(path).ReadMarker();
        if (marker is not null && Uri.TryCreate(marker.RemoteUrl, UriKind.Absolute, out var markerUrl) &&
            Uri.TryCreate(_url.Text, UriKind.Absolute, out var url) && markerUrl.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase))
        {
            // An earlier setup of the same folder (e.g. the app was moved): reuse its state, nothing starts over.
            _existingState = marker;
            _remoteRoot.Text = string.IsNullOrEmpty(marker.RemoteRoot) ? "/" : marker.RemoteRoot;
            _remoteRoot.Enabled = _browseRemote.Enabled = false;
            _folderInfo.Text = "✔ This folder was synced by WebDavSync before: its sync state is reused, nothing starts over.";
            return;
        }

        if (File.Exists(Path.Combine(path, LegacyImporter.DbFileName)))
        {
            _importLegacy.Visible = true;
            _remoteRoot.Text = "/";
            _folderInfo.Text = "This folder was synced by the Mail.ru Cloud app. Taking over its state means only the differences since then are synced.";
            return;
        }

        _folderInfo.Text = Directory.EnumerateFileSystemEntries(path).Any()
            ? "Files already in the folder are merged with the cloud: nothing is deleted on either side, differing files keep both versions."
            : "The cloud folder's files are downloaded into this empty folder.";
    }

    private bool CheckFolders()
    {
        var path = _local.Text.Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            return Fail(_folderInfo, "Choose a folder on this computer.");
        }

        if (_host.Pairs.Any(p => string.Equals(Path.GetFullPath(p.Pair.LocalRoot), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            return Fail(_folderInfo, "This folder is already synced.");
        }

        if (!_remoteRoot.Text.StartsWith('/'))
        {
            _remoteRoot.Text = "/" + _remoteRoot.Text;
        }

        return true;
    }

    private void Finish()
    {
        var path = Path.GetFullPath(_local.Text.Trim());
        try
        {
            Directory.CreateDirectory(path);
            var pair = new PairSettings
            {
                Id = _existingState?.PairId ?? Guid.NewGuid().ToString("N"),
                Name = UniqueName(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : "cloud"),
                LocalRoot = path,
                RemoteUrl = _url.Text.Trim(),
                RemoteRoot = _remoteRoot.Text.Trim(),
                User = _login.Text.Trim(),
                Mode = _auto.Checked ? SyncMode.Auto : _guarded.Checked ? SyncMode.Guarded : SyncMode.ApproveAll,
                RemoteHashProperty = _hashProperty,
            };
            CreatedPair = _host.CreatePair(pair, _password.Text, importLegacy: _existingState is null && _importLegacy.Visible && _importLegacy.Checked, resetState: false);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PairStoppedException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------------------------------------------------------- helpers

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; _host.Pairs.Any(p => string.Equals(p.Pair.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name}-{i}";
        }

        return candidate;
    }

    private static string DefaultLocalFolder()
    {
        // The old Mail.ru client's usual folder, if it is there; otherwise a new folder in the user profile.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true }))
        {
            var candidate = Path.Combine(drive.RootDirectory.FullName, "Cloud");
            if (File.Exists(Path.Combine(candidate, LegacyImporter.DbFileName)) || Directory.Exists(Path.Combine(candidate, StatePaths.DirName)))
            {
                return candidate;
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Cloud");
    }

    private static bool Fail(Label label, string text)
    {
        label.ForeColor = Color.Firebrick;
        label.Text = text;
        return false;
    }

    private static FlowLayoutPanel Column() =>
        new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 10, 3, 2) };
}
