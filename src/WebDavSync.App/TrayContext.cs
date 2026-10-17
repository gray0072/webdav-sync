using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;

namespace WebDavSync.App;

/// <summary>The tray icon (§14): aggregated state of all pairs, context menu, approvals, notifications.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan NotifyEvery = TimeSpan.FromMinutes(10);

    private readonly AppHost _host;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 500 };
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, DateTime> _lastNotified = [];
    private readonly Dictionary<PairRuntime, ApprovalsForm> _approvals = [];
    private int _frame;
    private bool _exiting;

    public TrayContext(AppHost host)
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
        _tray.BalloonTipClicked += (_, _) => OpenPendingOrFolder();
        _menu.Opening += (_, _) => BuildMenu();
        _animation.Tick += (_, _) =>
        {
            _frame++;
            Refresh();
        };

        foreach (var pair in host.Pairs)
        {
            pair.Restarted += (_, _) => Attach(pair);
            pair.Start();
            Attach(pair);
        }

        Refresh();
        if (host.Pairs.Count == 0)
        {
            _tray.ShowBalloonTip(10_000, "WebDavSync", $"No pairs in {host.Paths.SettingsFile}. Create one with 'wds pair add'.", ToolTipIcon.Warning);
        }
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
        _tray.ShowBalloonTip(10_000, "WebDavSync", text, icon);
    }

    // ---------------------------------------------------------------- menu

    private void BuildMenu()
    {
        _menu.Items.Clear();
        if (_host.Pairs.Count == 1)
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
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = StartupCommand() is not null });
        _menu.Items.Add("Open log folder", null, (_, _) => Open(_host.Paths.LogsDir));
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

        var mode = new ToolStripMenuItem($"Mode: {pair.Pair.Mode}");
        foreach (var m in Enum.GetValues<SyncMode>())
        {
            mode.DropDownItems.Add(new ToolStripMenuItem(m.ToString(), null, (_, _) => SetMode(pair, m)) { Checked = pair.Pair.Mode == m });
        }

        items.Add(mode);
        items.Add("Set password…", null, async (_, _) => await SetPasswordAsync(pair));
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

    private async Task SetPasswordAsync(PairRuntime pair)
    {
        using var dialog = new PasswordForm($"App password for {pair.Pair.User} at {pair.Pair.RemoteUrl}");
        if (dialog.ShowDialog() != DialogResult.OK || dialog.Password.Length == 0)
        {
            return;
        }

        _host.Secrets.Set(pair.Pair.Id, dialog.Password);
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
            form = new ApprovalsForm(pair.Pair, service);
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

    /// <summary>Per-user autostart (HKCU Run) with this instance's data folder.</summary>
    private void ToggleStartup()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartupCommand() is not null)
        {
            key.DeleteValue("WebDavSync", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("WebDavSync", $"\"{Environment.ProcessPath}\" --data-dir \"{_host.Paths.DataDir}\"");
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
            _tray.Dispose();
            _menu.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
