namespace WebDavSync.App;

/// <summary>
/// Base of the app's forms: sizes in code are given at 96 DPI and scaled to the monitor's DPI, so layouts keep their
/// proportions with Windows display scaling (e.g. 150 %). Like designer code, layout is suspended while the derived
/// constructor builds the form and resumed on load, which performs the auto-scaling.
/// </summary>
internal class ScaledForm : Form
{
    public ScaledForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
    }

    protected override void OnLoad(EventArgs e)
    {
        ResumeLayout(true);
        base.OnLoad(e);
    }
}
