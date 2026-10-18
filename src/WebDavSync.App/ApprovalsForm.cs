using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Execution;
using WebDavSync.Core.Planning;

namespace WebDavSync.App;

/// <summary>What the approvals window works on: a pair's pending approval (§10).</summary>
internal interface IApprovalSource
{
    PendingApproval? Pending { get; }

    SyncMode Mode { get; }

    Task<ExecutionResult?> ApproveAsync(IReadOnlyCollection<SyncOp> ops, bool acceptGuards);

    Task RejectAsync(IReadOnlyCollection<SyncOp> ops);
}

internal sealed class ServiceApprovals(PairService service) : IApprovalSource
{
    public PendingApproval? Pending => service.Pending;

    public SyncMode Mode => service.Pair.Mode;

    public Task<ExecutionResult?> ApproveAsync(IReadOnlyCollection<SyncOp> ops, bool acceptGuards) => service.ApproveAsync(ops, acceptGuards);

    public Task RejectAsync(IReadOnlyCollection<SyncOp> ops) => service.RejectAsync(ops);
}

/// <summary>Approvals window (§14): the pending ops of a pair with checkboxes; approve executes, reject holds (§10).</summary>
internal sealed class ApprovalsForm : ScaledForm
{
    private const int MaxRows = 5000;

    private readonly IApprovalSource _source;
    private readonly ListView _list = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, Margin = new Padding(8, 0, 8, 0) };
    private readonly Label _header = WrapLabel();
    private readonly Label _footer = WrapLabel(margin: new Padding(3));
    private readonly Button _approve = new() { Text = "Approve checked" };
    private readonly Button _approveAll = new() { Text = "Approve all" };
    private readonly Button _reject = new() { Text = "Reject checked (hold)" };
    private readonly Button _checkAll = new() { Text = "Check all" };
    private PendingApproval? _shown;
    private bool _busy;

    public ApprovalsForm(PairSettings pair, IApprovalSource source)
    {
        _source = source;
        Text = $"Approvals — {pair.Name} ({pair.LocalRoot})";
        Size = new Size(1000, 600);
        MinimumSize = new Size(760, 360);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _list.Columns.Add("Operation", Px(120));
        _list.Columns.Add("Path", Px(500));
        _list.Columns.Add("Reason", Px(220));
        _list.Columns.Add("Size", Px(90), HorizontalAlignment.Right);
        Controls.Add(Rows(_list, _header, _list, ButtonBar(_footer, _checkAll, _reject, _approve, _approveAll)));

        _checkAll.Click += (_, _) => SetAllChecked(!_list.Items.Cast<ListViewItem>().All(i => i.Checked));
        _approve.Click += async (_, _) => await ApproveAsync(CheckedOps());
        _approveAll.Click += async (_, _) => await ApproveAsync(_shown?.Ops ?? []);
        _reject.Click += async (_, _) => await RejectAsync(CheckedOps());
        _list.Resize += (_, _) => FitColumns();
    }

    /// <summary>Operation, reason and size fit their text (any DPI or text size); the path takes the rest.</summary>
    private void FitColumns()
    {
        if (_list.Columns.Count < 4 || !_list.IsHandleCreated)
        {
            return;
        }

        foreach (var index in new[] { 0, 2, 3 })
        {
            _list.AutoResizeColumn(index, _list.Items.Count > 0 ? ColumnHeaderAutoResizeStyle.ColumnContent : ColumnHeaderAutoResizeStyle.HeaderSize);
            var header = TextRenderer.MeasureText(_list.Columns[index].Text, _list.Font).Width + Px(24);
            _list.Columns[index].Width = Math.Max(_list.Columns[index].Width, header);
        }

        var others = _list.Columns[0].Width + _list.Columns[2].Width + _list.Columns[3].Width;
        _list.Columns[1].Width = Math.Max(Px(200), _list.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth);
    }

    /// <summary>Shows the current pending approval; keeps the checkboxes while it is the same plan.</summary>
    public void Reload()
    {
        var pending = _source.Pending;
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
            if (op.IsDelete || op.Overwrite || op.Type == OpType.Conflict || op.NeedsReview)
            {
                item.ForeColor = Color.Firebrick;
            }

            _list.Items.Add(item);
        }

        _list.EndUpdate();
        FitColumns();
        if (pending is null)
        {
            _header.Text = "Nothing waits for approval.";
            _header.ForeColor = SystemColors.ControlText;
        }
        else
        {
            var more = pending.Ops.Count > MaxRows ? $" (first {MaxRows} shown)" : string.Empty;
            var union = pending.Cycle.UnionMode ? " First sync: nothing is deleted on either side." : string.Empty;
            _header.Text = $"{pending.Ops.Count} changes wait for your approval (checked at {pending.CreatedUtc.ToLocalTime():HH:mm}){more}.{union}" +
                           " Red: deletions, overwrites and conflicts." +
                           (pending.GuardTripped ? "\nSAFETY GUARD: " + string.Join(" ", pending.Cycle.Guards.Trips) : string.Empty);
            _header.ForeColor = pending.GuardTripped ? Color.Firebrick : SystemColors.ControlText;
        }

        UpdateButtons();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitColumns();
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
        var question = $"Execute {ops.Count} changes{(deletes > 0 ? $", including {deletes} deletions" : string.Empty)}?";
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
            var result = await _source.ApproveAsync(ops, acceptGuards: pending.GuardTripped);
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
            await _source.RejectAsync(ops);
            _footer.Text = $"{ops.Count} changes held: they are not proposed again until the file changes.";
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
        _approve.Enabled = _approveAll.Enabled = any && _source.Mode != SyncMode.DryRun;
        _reject.Enabled = _checkAll.Enabled = any;
    }
}
