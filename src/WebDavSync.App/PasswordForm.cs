namespace WebDavSync.App;

/// <summary>Asks for an app password; it goes to the Windows Credential Manager (§15.1), never to settings or logs.</summary>
internal sealed class PasswordForm : ScaledForm
{
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Width = 360 };

    public PasswordForm(string prompt)
    {
        Text = "WebDavSync — password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(88, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 0) };
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(360, 0) });
        layout.Controls.Add(_password);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        buttons.Controls.AddRange([ok, cancel]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    public string Password => _password.Text;
}
