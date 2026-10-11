namespace AutoFollower.Gui;

internal sealed class WorkDialog : Form
{
    private readonly Label _status = new() { Dock = DockStyle.Fill };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25 };
    private readonly Action _cancel;
    private bool _completed;
    internal WorkDialog(string title, string status, Action cancel)
    {
        _cancel = cancel; Text = title; StartPosition = FormStartPosition.CenterParent; AppBranding.Apply(this);
        Size = new Size(510, 200); MinimumSize = new Size(410, 190); MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _status.Text = status; layout.Controls.Add(_status, 0, 0); layout.Controls.Add(_bar, 0, 1);
        var button = new Button { Text = "Cancel", Size = new Size(90, 28), Anchor = AnchorStyles.Right };
        button.Click += (_, _) => Cancel(); layout.Controls.Add(button, 0, 2); Controls.Add(layout); DesktopTheme.Apply(this);
        FormClosing += (_, e) => { if (!_completed) { e.Cancel = true; Cancel(); } };
    }
    private void Cancel() { _cancel(); _status.Text = "Cancelling… Please wait for this operation to stop."; }
    internal void SetStatus(string status) => _status.Text = status;
    internal void Complete() { _completed = true; Close(); Dispose(); }
}
