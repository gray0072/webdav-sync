namespace WebDavSync.App;

/// <summary>
/// Base of the app's forms: sizes in code are given at 96 DPI and scaled to the monitor's DPI, so layouts keep their
/// proportions with Windows display scaling (e.g. 150 %). Like designer code, layout is suspended while the derived
/// constructor builds the form and resumed on load, which performs the auto-scaling; so every control must be in the
/// form by then (hidden ones too). Text containers size themselves (<see cref="Rows"/>): no fixed heights.
/// </summary>
internal class ScaledForm : Form
{
    public ScaledForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        var font = SystemFonts.MessageBoxFont ?? Font;
        Font = FontScale == 1f ? font : new Font(font.FontFamily, font.Size * FontScale, font.Style);
    }

    /// <summary>Text size factor on top of the system font (the UI gallery uses it to check larger text).</summary>
    internal static float FontScale { get; set; } = 1f;

    /// <summary>Pixels for <paramref name="logical"/> 96-DPI units, for sizes auto-scaling does not cover (list columns).</summary>
    protected int Px(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>
    /// A one-column table: rows size to their content, except <paramref name="fill"/> which takes the remaining space.
    /// Labels in auto-sized rows wrap to the table's width.
    /// </summary>
    protected static TableLayoutPanel Rows(Control? fill, params Control[] rows)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows)
        {
            table.RowStyles.Add(ReferenceEquals(row, fill) ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            row.Dock = DockStyle.Fill;
            table.Controls.Add(row);
        }

        return table;
    }

    /// <summary>A bottom bar: a wrapping status text on the left, buttons on the right, as tall as its content.</summary>
    protected static TableLayoutPanel ButtonBar(Label? status, params Button[] buttons)
    {
        var bar = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        foreach (var button in buttons)
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(88, 0);
            button.Padding = new Padding(6, 2, 6, 2);
            flow.Controls.Add(button);
        }

        if (status is not null)
        {
            status.AutoSize = true;
            status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            bar.Controls.Add(status, 0, 0);
        }

        bar.Controls.Add(flow, 1, 0);
        return bar;
    }

    /// <summary>A label that wraps inside a <see cref="Rows"/> table.</summary>
    protected static Label WrapLabel(string text = "", Padding? margin = null) =>
        new() { Text = text, AutoSize = true, Margin = margin ?? new Padding(8, 8, 8, 4) };

    protected override void OnLoad(EventArgs e)
    {
        ResumeLayout(true);
        base.OnLoad(e);
    }
}
