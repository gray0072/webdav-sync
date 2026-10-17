using WebDavSync.Core.Config;
using WebDavSync.Core.Remote;

namespace WebDavSync.App;

/// <summary>Picks a folder in the cloud: a tree loaded one level at a time as nodes are expanded.</summary>
internal sealed class RemoteFolderDialog : ScaledForm
{
    private const string Placeholder = "…";

    private readonly IRemoteStore _remote;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };

    public RemoteFolderDialog(IRemoteStore remote)
    {
        _remote = remote;
        Text = "Choose a cloud folder";
        Size = new Size(460, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;

        var ok = new Button { Text = "Choose", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(_tree);
        Controls.Add(_status);
        Controls.Add(buttons);

        var root = new TreeNode("Whole cloud (/)") { Tag = "/" };
        root.Nodes.Add(Placeholder);
        _tree.Nodes.Add(root);
        _tree.SelectedNode = root;
        _tree.BeforeExpand += async (_, e) => await LoadAsync(e.Node!);
        Shown += (_, _) => root.Expand();
    }

    /// <summary>The chosen folder, e.g. <c>/Documents</c>.</summary>
    public string SelectedPath => _tree.SelectedNode?.Tag as string ?? "/";

    private async Task LoadAsync(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Text != Placeholder)
        {
            return;
        }

        _status.Text = "Loading…";
        try
        {
            var listing = await _remote.ListAsync((string)node.Tag!, CancellationToken.None);
            node.Nodes.Clear();
            foreach (var child in listing.Children.Where(c => c.IsDir && !SyncFilter.IsBuiltInIgnored(c.Name)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new TreeNode(child.Name) { Tag = child.Path.TrimEnd('/') };
                item.Nodes.Add(Placeholder);
                node.Nodes.Add(item);
            }

            _status.Text = string.Empty;
        }
        catch (RemoteException ex)
        {
            _status.Text = "Cannot list: " + ex.Message;
        }
    }
}
