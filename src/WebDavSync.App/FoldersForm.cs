using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Remote;

namespace WebDavSync.App;

/// <summary>Choose folders (§12.2, §14): checkbox list of the top-level folders of both sides.</summary>
internal sealed class FoldersForm : Form
{
    private readonly PairSettings _pair;
    private readonly IRemoteStore _remote;
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly Label _info = new() { Dock = DockStyle.Top, AutoSize = false, Height = 52, Padding = new Padding(8, 6, 8, 0) };
    private readonly Button _ok = new() { Text = "Save", AutoSize = true, Enabled = false };
    private IReadOnlyList<TopFolder> _folders = [];

    public FoldersForm(PairSettings pair, IRemoteStore remote)
    {
        _pair = pair;
        _remote = remote;
        Text = $"Choose folders — {pair.Name}";
        Size = new Size(560, 560);
        StartPosition = FormStartPosition.CenterScreen;
        _info.Text = "Loading…";

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, _ok]);
        CancelButton = cancel;
        Controls.Add(_list);
        Controls.Add(_info);
        Controls.Add(buttons);

        _ok.Click += (_, _) => Save();
        Shown += async (_, _) => await LoadAsync();
    }

    /// <summary>The new excluded list after Save (OK); null otherwise.</summary>
    public List<string>? Excluded { get; private set; }

    private async Task LoadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (folders, error) = await SelectiveSync.ListAsync(_pair, _remote, cts.Token);
        _folders = folders;
        _list.BeginUpdate();
        foreach (var f in folders)
        {
            var where = (f.OnLocal, f.OnRemote) switch
            {
                (true, true) => "local + cloud",
                (true, false) => "local only",
                (false, true) => "cloud only",
                (true, null) => "local",
                _ => "not found",
            };
            _list.Items.Add($"{f.Name}   ({where})", !f.Excluded);
        }

        _list.EndUpdate();
        _info.Text = "Checked folders are synced. Unchecking keeps the folder as it is on both sides (nothing is deleted); " +
                     "checking it again merges both sides without deletes. Files in the root are always synced." +
                     (error is null ? string.Empty : $"\nCloud folders unknown: {error}");
        _ok.Enabled = true;
    }

    private void Save()
    {
        var excluded = _folders.Where((_, i) => !_list.GetItemChecked(i)).ToList();
        var newlyExcluded = excluded.Where(f => !f.Excluded).Select(f => f.Name).ToList();
        if (newlyExcluded.Count > 0 &&
            MessageBox.Show(this, $"Stop syncing {string.Join(", ", newlyExcluded)}?\n\nThe folders stay as they are locally and in the cloud; nothing is deleted.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        Excluded = SelectiveSync.WithTopLevelExcluded(_pair, excluded.Select(f => f.Name));
        DialogResult = DialogResult.OK;
        Close();
    }
}
