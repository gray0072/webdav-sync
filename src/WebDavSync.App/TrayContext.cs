using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Updates;

namespace WebDavSync.App;

/// <summary>The tray icon (§14): aggregated state of all pairs, context menu, approvals, notifications.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan NotifyEvery = TimeSpan.FromMinutes(10);

    private readonly AppHost _host;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 80 };
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, DateTime> _lastNotified = [];
    private readonly Dictionary<PairRuntime, ApprovalsForm> _approvals = [];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 2 * 60 * 1000 };
    private Action? _balloonClick;
    private ReleaseInfo? _available;
    private int _frame;
    private PairState _top;
    private bool _exiting;

    /// <param name="adoptedFrom">Data folder of an earlier copy whose settings this one took over at start, if any.</param>
    public TrayContext(AppHost host, string? adoptedFrom = null)
    {
        _host = host;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _tray = new NotifyIcon { Icon = _icons.Get(PairState.Idle), Text = "WebDavSync", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenPendingOrFolder();
            }
        };
        _tray.BalloonTipClicked += (_, _) => (_balloonClick ?? OpenPendingOrFolder)();

        // Automatic update check (§14.3): first two minutes after start, then every hour whether one is due.
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 60 * 60 * 1000;
            await AutoCheckForUpdatesAsync();
        };
        _updateTimer.Start();
        _menu.Opening += (_, _) => BuildMenu();
        _animation.Tick += (_, _) => _tray.Icon = _icons.Get(_top, ++_frame);

        foreach (var pair in host.Pairs)
        {
            pair.Restarted += (_, _) => Attach(pair);
            pair.Start();
            Attach(pair);
        }

        Refresh();
        if (adoptedFrom is not null)
        {
            // A new copy in another folder: autostart has to start this one from now on.
            if (StartupCommand() is not null)
            {
                SetStartup(true);
            }

            _tray.ShowBalloonTip(10_000, "WebDavSync", $"Settings taken over from the previous copy ('{adoptedFrom}').", ToolTipIcon.Info);
        }

        if (host.Pairs.Count == 0)
        {
            // First run: the setup wizard instead of an empty tray icon.
            _ui.Post(_ => ShowSetup(firstRun: true), null);
        }
    }

    private void ShowSetup(bool firstRun)
    {
        using var wizard = new SetupWizard(_host, firstRun);
        if (wizard.ShowDialog() != DialogResult.OK || wizard.CreatedPair is not { } pair)
        {
            if (_host.Pairs.Count == 0)
            {
                _tray.ShowBalloonTip(10_000, "WebDavSync", "Not set up yet. Right-click this icon and choose 'Set up sync…' when you are ready.", ToolTipIcon.Info);
            }

            return;
        }

        if (wizard.StartWithWindows)
        {
            SetStartup(true);
        }

        pair.Restarted += (_, _) => Attach(pair);
        pair.Start();
        Attach(pair);
        Refresh();
        _tray.ShowBalloonTip(
            10_000,
            "WebDavSync",
            $"Syncing {pair.Pair.LocalRoot}. The first check of the cloud can take a few minutes; " +
            (pair.Pair.Mode == SyncMode.ApproveAll ? "then click this icon to approve the changes." : "the icon pulses while files are copied."),
            ToolTipIcon.Info);
    }

    private void Attach(PairRuntime pair)
    {
        if (pair.Service is not { } service)
        {
            Notify(pair, "error", $"{pair.Pair.Name}: {pair.StartError}", ToolTipIcon.Error);
            return;
        }

        service.StatusChanged += (_, status) => _ui.Post(_ => OnStatus(pair, status), null);
    }

    private void OnStatus(PairRuntime pair, PairStatus status)
    {
        if (_exiting)
        {
            return;
        }

        Refresh();
        switch (status.State)
        {
            case PairState.NeedsAttention when status.PendingApprovals > 0:
                Notify(pair, "approval", $"{pair.Pair.Name}: {status.Text}", ToolTipIcon.Info);
                break;
            case PairState.NeedsAttention:
                Notify(pair, "failed", $"{pair.Pair.Name}: {status.Text}", ToolTipIcon.Warning);
                break;
            case PairState.Error:
                Notify(pair, "error", $"{pair.Pair.Name}: {status.Text}\n{status.Error}", ToolTipIcon.Error);
                break;
        }

        if (_approvals.TryGetValue(pair, out var form))
        {
            form.Reload();
        }
    }

    /// <summary>Icon and tooltip from the most important pair state.</summary>
    private void Refresh()
    {
        var states = _host.Pairs.Select(p => (Pair: p, State: p.Service?.Status.State ?? PairState.Error, Text: p.Service?.Status.Text ?? p.StartError ?? "Not started")).ToList();
        var top = states.Count == 0
            ? PairState.Idle
            : states.Select(s => s.State).MaxBy(Severity);
        _top = top;
        _tray.Icon = _icons.Get(top, _frame);
        _animation.Enabled = top == PairState.Syncing;

        var text = states.Count switch
        {
            0 => "WebDavSync: no pairs",
            1 => Describe(states[0].Pair, states[0].Text),
            _ => string.Join("\n", states.Select(s => $"{s.Pair.Pair.Name}: {Describe(s.Pair, s.Text)}")),
        };
        _tray.Text = text.Length > 127 ? text[..124] + "..." : text;
    }

    private static string Describe(PairRuntime pair, string text)
    {
        var status = pair.Service?.Status;
        return status is { State: PairState.Idle, LastSyncUtc: { } last }
            ? $"{text} · last sync {last.ToLocalTime():HH:mm}"
            : text;
    }

    private static int Severity(PairState state) => state switch
    {
        PairState.Error => 6,
        PairState.NeedsAttention => 5,
        PairState.Offline => 4,
        PairState.Syncing => 3,
        PairState.Paused => 2,
        PairState.Waiting => 1,
        PairState.Checking => 1,
        _ => 0,
    };

    /// <summary>Balloon notification, at most one per pair and category every 10 minutes (§14).</summary>
    private void Notify(PairRuntime pair, string category, string text, ToolTipIcon icon)
    {
        var key = pair.Pair.Id + "/" + category;
        if (_lastNotified.TryGetValue(key, out var last) && DateTime.UtcNow - last < NotifyEvery)
        {
            return;
        }

        _lastNotified[key] = DateTime.UtcNow;
        _balloonClick = OpenPendingOrFolder;
        _tray.ShowBalloonTip(10_000, "WebDavSync", text, icon);
    }

    // ---------------------------------------------------------------- updates (§14.3)

    private async Task AutoCheckForUpdatesAsync()
    {
        var settings = AppSettings.Load(_host.Paths.SettingsFile);
        if (!UpdateChecker.IsDue(settings.UpdateCheck, settings.LastUpdateCheckUtc, DateTime.UtcNow))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var latest = await new UpdateChecker(Http).GetLatestAsync(cts.Token);
            _host.SaveAppSettings(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
            if (latest is null || latest.Version <= UpdateChecker.CurrentVersion)
            {
                return;
            }

            _available = latest;
            if (settings.NotifiedVersion != latest.Version.ToString())
            {
                _host.SaveAppSettings(s => s.NotifiedVersion = latest.Version.ToString());
                _balloonClick = () => ShowUpdate(latest);
                _tray.ShowBalloonTip(15_000, "WebDavSync", $"Version {latest.Version} is available (you have {UpdateChecker.CurrentVersion}). Click here to see what is new and update.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or IOException)
        {
            // Offline or GitHub unavailable: try again at the next hourly tick (the last check time is not updated).
            _host.Logger.LogInformation("Update check failed: {Message}", ex.Message);
        }
    }

    private async Task CheckForUpdatesNowAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var latest = await new UpdateChecker(Http).GetLatestAsync(cts.Token);
            _host.SaveAppSettings(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
            if (latest is null || latest.Version <= UpdateChecker.CurrentVersion)
            {
                _available = null;
                MessageBox.Show($"You have the latest version: WebDavSync {UpdateChecker.CurrentDescription}.", "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _available = latest;
            ShowUpdate(latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or IOException)
        {
            MessageBox.Show("Cannot check for updates: " + ex.Message, "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void ShowUpdate(ReleaseInfo release)
    {
        using var form = new UpdateForm(release, Http);
        if (form.ShowDialog() != DialogResult.OK || form.NewExe is not { } newExe)
        {
            return;
        }

        try
        {
            _host.Logger.LogInformation("Updating to {Version}", release.Version);
            Updater.SwapAndRestart(newExe, _host.Paths.DataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("The update could not be installed: " + ex.Message, "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        await ExitAsync();
    }

    private ToolStripMenuItem UpdatesMenu()
    {
        var settings = AppSettings.Load(_host.Paths.SettingsFile);
        var menu = new ToolStripMenuItem("Updates");
        menu.DropDownItems.Add(new ToolStripMenuItem($"You have {UpdateChecker.CurrentDescription}") { Enabled = false });
        menu.DropDownItems.Add("Check for updates now…", null, async (_, _) => await CheckForUpdatesNowAsync());
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Check automatically:") { Enabled = false });
        foreach (var (interval, text) in new[]
                 {
                     (UpdateCheckInterval.Daily, "Every day"),
                     (UpdateCheckInterval.Weekly, "Every week"),
                     (UpdateCheckInterval.Monthly, "Every month"),
                     (UpdateCheckInterval.Never, "Never"),
                 })
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("    " + text, null, (_, _) => _host.SaveAppSettings(s => s.UpdateCheck = interval))
            {
                Checked = settings.UpdateCheck == interval,
            });
        }

        if (settings.LastUpdateCheckUtc is { } last)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem($"Last checked {last.ToLocalTime():g}") { Enabled = false });
        }

        return menu;
    }

    // ---------------------------------------------------------------- menu

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"WebDavSync {UpdateChecker.CurrentDescription}") { Enabled = false });
        if (_available is { } update && update.Version > UpdateChecker.CurrentVersion)
        {
            _menu.Items.Add(new ToolStripMenuItem($"Update to {update.Version}…", null, (_, _) => ShowUpdate(update)) { Font = new Font(_menu.Font, FontStyle.Bold) });
        }

        _menu.Items.Add(new ToolStripSeparator());
        if (_host.Pairs.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("Set up sync…", null, (_, _) => ShowSetup(firstRun: true)) { Font = new Font(_menu.Font, FontStyle.Bold) });
        }
        else if (_host.Pairs.Count == 1)
        {
            AddPairItems(_menu.Items, _host.Pairs[0]);
        }
        else
        {
            foreach (var pair in _host.Pairs)
            {
                var sub = new ToolStripMenuItem($"{pair.Pair.Name} — {pair.Service?.Status.Text ?? pair.StartError}");
                AddPairItems(sub.DropDownItems, pair);
                _menu.Items.Add(sub);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        if (_host.Pairs.Count > 0)
        {
            _menu.Items.Add("Add a folder…", null, (_, _) => ShowSetup(firstRun: false));
        }

        var performance = new ToolStripMenuItem("Performance");
        performance.DropDownItems.Add(new ToolStripMenuItem("Low priority", null, (_, _) => SetLimits(!_host.LowPriority, _host.CpuLimitPercent))
        {
            Checked = _host.LowPriority,
        });
        performance.DropDownItems.Add(Choice(
            $"CPU limit: {(_host.CpuLimitPercent > 0 ? _host.CpuLimitPercent + "%" : "none")}",
            new (string, object?)[] { ("None", 0), ("10%", 10), ("25%", 25), ("50%", 50) },
            _host.CpuLimitPercent,
            v => SetLimits(_host.LowPriority, (int)v!)));
        _menu.Items.Add(performance);
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartup(StartupCommand() is null)) { Checked = StartupCommand() is not null });
        _menu.Items.Add(UpdatesMenu());
        _menu.Items.Add("Open log folder", null, (_, _) => Open(_host.Paths.LogsDir));
        _menu.Items.Add("WebDavSync on GitHub…", null, (_, _) => Open("https://github.com/" + UpdateChecker.Repository));
        _menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
    }

    private void AddPairItems(ToolStripItemCollection items, PairRuntime pair)
    {
        var service = pair.Service;
        var status = service?.Status;
        items.Add(new ToolStripMenuItem(status?.Text ?? pair.StartError ?? "Not started") { Enabled = false });
        items.Add(new ToolStripSeparator());
        items.Add("Open folder", null, (_, _) => Open(pair.Pair.LocalRoot));
        if (WebUrl(pair.Pair) is { } web)
        {
            items.Add("Open cloud in browser", null, (_, _) => Open(web));
        }

        items.Add(new ToolStripMenuItem("Sync now", null, (_, _) => service?.SyncNow()) { Enabled = service is not null });
        if (status?.State == PairState.Paused)
        {
            items.Add("Resume", null, (_, _) => service?.Resume());
        }
        else
        {
            var pause = new ToolStripMenuItem("Pause") { Enabled = service is not null };
            pause.DropDownItems.Add("15 min", null, (_, _) => service?.Pause(TimeSpan.FromMinutes(15)));
            pause.DropDownItems.Add("1 hour", null, (_, _) => service?.Pause(TimeSpan.FromHours(1)));
            pause.DropDownItems.Add("Until tomorrow", null, (_, _) => service?.Pause(DateTime.Today.AddDays(1).AddHours(6) - DateTime.Now));
            pause.DropDownItems.Add("Until resumed", null, (_, _) => service?.Pause(null));
            items.Add(pause);
        }

        var pending = service?.Pending?.Ops.Count ?? 0;
        items.Add(new ToolStripMenuItem($"Approvals ({pending})…", null, (_, _) => ShowApprovals(pair)) { Enabled = service is not null });
        items.Add(new ToolStripMenuItem("Choose folders…", null, (_, _) => ChooseFolders(pair)) { Enabled = pair.Remote is not null });

        var settings = new ToolStripMenuItem("Settings");
        var mode = new ToolStripMenuItem($"Mode: {pair.Pair.Mode}");
        foreach (var m in Enum.GetValues<SyncMode>())
        {
            mode.DropDownItems.Add(new ToolStripMenuItem(m.ToString(), null, (_, _) => SetMode(pair, m)) { Checked = pair.Pair.Mode == m });
        }

        settings.DropDownItems.Add(mode);

        // The full remote check (§8): every cycle without local changes crawls the whole cloud.
        settings.DropDownItems.Add(Choice(
            $"Check cloud every {Format(pair.Pair.RemotePollInterval)}",
            new[] { 15, 30, 60, 180, 360, 720, 1440 }.Select(m => (Format(TimeSpan.FromMinutes(m)), (object?)TimeSpan.FromMinutes(m))),
            pair.Pair.RemotePollInterval,
            v => Save(pair, p => p.RemotePollInterval = (TimeSpan)v!)));

        // Rolling verify (§5.3): re-hash every local file once per period, a slice per cycle.
        settings.DropDownItems.Add(Choice(
            $"Verify local files: {(pair.Pair.LocalVerifyInterval is { } vi ? "every " + Format(vi) : "off")}",
            new (string, object?)[] { ("Off", null), ("Every 7 days", TimeSpan.FromDays(7)), ("Every 30 days", TimeSpan.FromDays(30)), ("Every 90 days", TimeSpan.FromDays(90)) },
            pair.Pair.LocalVerifyInterval,
            v => Save(pair, p => p.LocalVerifyInterval = (TimeSpan?)v)));

        settings.DropDownItems.Add(Choice(
            $"Hashing speed: {(pair.Pair.HashLimitMBps > 0 ? $"{pair.Pair.HashLimitMBps:0} MB/s" : "unlimited")}",
            new (string, object?)[] { ("Unlimited", 0d), ("20 MB/s", 20d), ("50 MB/s", 50d), ("100 MB/s", 100d) },
            pair.Pair.HashLimitMBps,
            v => Save(pair, p => p.HashLimitMBps = (double)v!)));
        settings.DropDownItems.Add("Set password…", null, async (_, _) => await SetPasswordAsync(pair));
        items.Add(settings);
    }

    private static ToolStripMenuItem Choice(string title, IEnumerable<(string Text, object? Value)> options, object? current, Action<object?> choose)
    {
        var menu = new ToolStripMenuItem(title);
        foreach (var (text, value) in options)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => choose(value)) { Checked = Equals(value, current) });
        }

        return menu;
    }

    private void Save(PairRuntime pair, Action<PairSettings> change)
    {
        try
        {
            _host.SaveSetting(pair.Pair, change);
            _host.Logger.LogInformation("Pair {Pair}: settings changed from the tray", pair.Pair.Id);
            pair.Service?.Reschedule();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetMode(PairRuntime pair, SyncMode mode)
    {
        try
        {
            _host.SaveSetting(pair.Pair, p => p.Mode = mode);
            _host.Logger.LogInformation("Pair {Pair}: mode set to {Mode} from the tray", pair.Pair.Id, mode);
            pair.Service?.SyncNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ChooseFolders(PairRuntime pair)
    {
        if (pair.Remote is not { } remote)
        {
            return;
        }

        using var form = new FoldersForm(pair.Pair, remote);
        if (form.ShowDialog() != DialogResult.OK || form.Excluded is not { } excluded)
        {
            return;
        }

        try
        {
            _host.SaveSetting(pair.Pair, p => p.ExcludedFolders = [.. excluded]);
            _host.Logger.LogInformation("Pair {Pair}: excluded folders set to {Folders} from the tray", pair.Pair.Id, string.Join(", ", excluded));
            pair.Service?.SyncNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{t.TotalDays:0} days" : t.TotalHours >= 1 ? $"{t.TotalHours:0} h" : $"{t.TotalMinutes:0} min";

    private void SetLimits(bool lowPriority, int cpuLimit)
    {
        try
        {
            _host.SetLimits(lowPriority, cpuLimit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SetPasswordAsync(PairRuntime pair)
    {
        using var dialog = new PasswordForm($"App password for {pair.Pair.User} at {pair.Pair.RemoteUrl}");
        if (dialog.ShowDialog() != DialogResult.OK || dialog.Password.Length == 0)
        {
            return;
        }

        _host.Secrets.Set(pair.Pair.Id, dialog.Password, pair.Pair.User);
        _host.Logger.LogInformation("Pair {Pair}: password stored from the tray", pair.Pair.Id);
        await pair.RestartAsync();
        Refresh();
    }

    private void ShowApprovals(PairRuntime pair)
    {
        if (pair.Service is not { } service)
        {
            return;
        }

        if (!_approvals.TryGetValue(pair, out var form) || form.IsDisposed)
        {
            form = new ApprovalsForm(pair.Pair, new ServiceApprovals(service));
            form.FormClosed += (_, _) => _approvals.Remove(pair);
            _approvals[pair] = form;
        }

        form.Reload();
        form.Show();
        form.Activate();
    }

    private void OpenPendingOrFolder()
    {
        var pair = _host.Pairs.FirstOrDefault(p => p.Service?.Pending is not null);
        if (pair is not null)
        {
            ShowApprovals(pair);
        }
        else if (_host.Pairs.Count > 0)
        {
            Open(_host.Pairs[0].Pair.LocalRoot);
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string? StartupCommand()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("WebDavSync") as string;
    }

    /// <summary>Data folder of the autostart entry (§15.1): its <c>--data-dir</c>, else <c>data</c> next to its exe.</summary>
    public static string? StartupDataDir()
    {
        var command = StartupCommand();
        if (command is null)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(command, "--data-dir \"([^\"]+)\"");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var exe = System.Text.RegularExpressions.Regex.Match(command, "^\"([^\"]+)\"");
        return exe.Success ? Path.Combine(Path.GetDirectoryName(exe.Groups[1].Value) ?? string.Empty, "data") : null;
    }

    /// <summary>Per-user autostart (HKCU Run) with this instance's data folder.</summary>
    private void SetStartup(bool on)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            key.SetValue("WebDavSync", $"\"{Environment.ProcessPath}\" --data-dir \"{_host.Paths.DataDir}\"");
        }
        else
        {
            key.DeleteValue("WebDavSync", throwOnMissingValue: false);
        }
    }

    /// <summary>Web UI of known providers (§4.4).</summary>
    private static string? WebUrl(PairSettings pair) =>
        Uri.TryCreate(pair.RemoteUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("webdav.cloud.mail.ru", StringComparison.OrdinalIgnoreCase)
            ? "https://cloud.mail.ru/home" + (pair.RemoteRootNormalized == "/" ? "/" : pair.RemoteRootNormalized)
            : null;

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "WebDavSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ExitAsync()
    {
        _exiting = true;
        _tray.Text = "WebDavSync: stopping…";
        foreach (var form in _approvals.Values.ToList())
        {
            form.Close();
        }

        // A running op finishes its current step; cancellation is safe (§6.7).
        await Task.WhenAll(_host.Pairs.Select(p => p.StopAsync()));
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
            _updateTimer.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
