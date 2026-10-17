using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Planning;

namespace WebDavSync.App;

/// <summary>Approvals window (§14): the pending ops of a pair with checkboxes; approve executes, reject holds (§10).</summary>
internal sealed class ApprovalsForm : ScaledForm
{
    private const int MaxRows = 5000;

    private readonly PairService _service;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = false,
    };

    private readonly Label _header = new() { Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(8, 6, 8, 0) };
    private readonly Label _footer = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _approve = new() { Text = "Approve checked", AutoSize = true };
    private readonly Button _approveAll = new() { Text = "Approve all", AutoSize = true };
    private readonly Button _reject = new() { Text = "Reject checked (hold)", AutoSize = true };
    private readonly Button _checkAll = new() { Text = "Check all", AutoSize = true };
    private PendingApproval? _shown;
    private bool _busy;

    public ApprovalsForm(PairSettings pair, PairService service)
    {
        _service = service;
        Text = $"Approvals — {pair.Name} ({pair.LocalRoot})";
        Size = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _list.Columns.Add("Operation", 110);
        _list.Columns.Add("Path", 520);
        _list.Columns.Add("Reason", 200);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.AddRange([_checkAll, _reject, _approve, _approveAll]);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        bottom.Controls.Add(_footer);
        bottom.Controls.Add(buttons);

        Controls.Add(_list);
        Controls.Add(_header);
        Controls.Add(bottom);

        _checkAll.Click += (_, _) => SetAllChecked(!_list.Items.Cast<ListViewItem>().All(i => i.Checked));
        _approve.Click += async (_, _) => await ApproveAsync(CheckedOps());
        _approveAll.Click += async (_, _) => await ApproveAsync(_shown?.Ops ?? []);
        _reject.Click += async (_, _) => await RejectAsync(CheckedOps());
    }

    /// <summary>Shows the current pending approval; keeps the checkboxes while it is the same plan.</summary>
    public void Reload()
    {
        var pending = _service.Pending;
        if (ReferenceEquals(pending, _shown) && _list.Items.Count > 0)
        {
            UpdateButtons();
            return;
        }

        _shown = pending;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var op in (pending?.Ops ?? []).Take(MaxRows))
        {
            var size = op.Bytes > 0 ? SafetyGuards.Size(op.Bytes) : op.Affected > 1 ? $"{op.Affected} items" : string.Empty;
            var item = new ListViewItem([op.Type.ToString(), op.DisplayPath + (op.IsDir ? "/" : string.Empty), op.Reason, size]) { Tag = op };
            if (op.IsDelete || op.Overwrite || op.Type == OpType.Conflict)
            {
                item.ForeColor = Color.Firebrick;
            }

            _list.Items.Add(item);
        }

        _list.EndUpdate();
        if (pending is null)
        {
            _header.Text = "Nothing waits for approval.";
            _header.ForeColor = SystemColors.ControlText;
        }
        else
        {
            var more = pending.Ops.Count > MaxRows ? $" (first {MaxRows} shown)" : string.Empty;
            var union = pending.Cycle.UnionMode ? " Initial sync: nothing is deleted on either side." : string.Empty;
            _header.Text = $"{pending.Ops.Count} ops from the plan of {pending.CreatedUtc.ToLocalTime():HH:mm}{more}.{union}" +
                           (pending.GuardTripped ? "\nSAFETY GUARD: " + string.Join(" ", pending.Cycle.Guards.Trips) : string.Empty);
            _header.ForeColor = pending.GuardTripped ? Color.Firebrick : SystemColors.ControlText;
        }

        UpdateButtons();
    }

    private List<SyncOp> CheckedOps() => _list.CheckedItems.Cast<ListViewItem>().Select(i => (SyncOp)i.Tag!).ToList();

    private void SetAllChecked(bool value)
    {
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items)
        {
            item.Checked = value;
        }

        _list.EndUpdate();
    }

    private async Task ApproveAsync(IReadOnlyCollection<SyncOp> ops)
    {
        if (_shown is not { } pending || ops.Count == 0)
        {
            return;
        }

        var deletes = ops.Where(o => o.IsDelete).Sum(o => o.Affected);
        var question = $"Execute {ops.Count} ops{(deletes > 0 ? $", including {deletes} deletions" : string.Empty)}?";
        if (pending.GuardTripped)
        {
            question = "A safety guard tripped:\n" + string.Join("\n", pending.Cycle.Guards.Trips) + "\n\n" + question;
        }

        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, pending.GuardTripped ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _service.ApproveAsync(ops, acceptGuards: pending.GuardTripped);
            _footer.Text = result is null
                ? "The plan was too old: a new check runs; approve again when it is done."
                : $"Done {result.Done}, failed {result.Failed}, skipped {result.Skipped}.{(result.AbortReason is null ? string.Empty : " Aborted: " + result.AbortReason)}";
        });
    }

    private async Task RejectAsync(IReadOnlyCollection<SyncOp> ops)
    {
        if (ops.Count == 0)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _service.RejectAsync(ops);
            _footer.Text = $"{ops.Count} ops held: not proposed again until the item changes ('wds held' releases them).";
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        UpdateButtons();
        _footer.Text = "Working…";
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            _footer.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _shown = null;
            Reload();
        }
    }

    private void UpdateButtons()
    {
        var any = _shown is not null && !_busy;
        _approve.Enabled = _approveAll.Enabled = any && _service.Pair.Mode != SyncMode.DryRun;
        _reject.Enabled = _checkAll.Enabled = any;
    }
}
