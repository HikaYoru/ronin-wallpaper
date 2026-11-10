namespace RoninHost;

/// <summary>A one-line question in the tray's charcoal and gold (layout names, new to-do items).</summary>
internal sealed class InputBox : Form
{
    readonly TextBox box;

    InputBox(string title, string prompt, string value)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = InkRenderer.Bg;
        ForeColor = InkRenderer.Text;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = TrayIcons.App;
        ClientSize = new Size(380, 128);
        Padding = new Padding(16);

        var label = new Label { Text = prompt, AutoSize = false, Dock = DockStyle.Top, Height = 26, ForeColor = InkRenderer.Text };
        box = new TextBox
        {
            Text = value, Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(0x12, 0x13, 0x15), ForeColor = InkRenderer.Text,
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 8, 0, 0) };
        var ok = Btn("OK", DialogResult.OK, primary: true);
        var cancel = Btn("Cancel", DialogResult.Cancel, primary: false);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        Controls.Add(buttons);
        Controls.Add(box);
        Controls.Add(label);
        AcceptButton = ok;
        CancelButton = cancel;

        // open next to the cursor, on its monitor
        var at = Cursor.Position;
        var wa = Screen.FromPoint(at).WorkingArea;
        Location = new Point(Math.Clamp(at.X - Width / 2, wa.Left, wa.Right - Width), Math.Clamp(at.Y - Height / 2, wa.Top, wa.Bottom - Height));
    }

    static Button Btn(string text, DialogResult result, bool primary) => new()
    {
        Text = text, DialogResult = result, AutoSize = true, FlatStyle = FlatStyle.Flat,
        ForeColor = primary ? InkRenderer.Bg : InkRenderer.Gold, BackColor = primary ? InkRenderer.Gold : InkRenderer.Bg,
        FlatAppearance = { BorderColor = InkRenderer.Gold }, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(10, 2, 10, 2),
    };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        box.Focus();
        box.SelectAll();
    }

    /// <summary>The text typed, or null if cancelled or left empty.</summary>
    public static string? Ask(string title, string prompt, string value = "")
    {
        using var f = new InputBox(title, prompt, value);
        return f.ShowDialog() == DialogResult.OK && f.box.Text.Trim().Length > 0 ? f.box.Text.Trim() : null;
    }
}
