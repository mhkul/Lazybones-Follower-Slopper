namespace AutoFollower.Gui;

internal sealed class PresetSourceChoiceDialog : Form
{
    private readonly ListBox _sources = new() { Dock = DockStyle.Fill };
    private readonly Button _use = new ReadableButton { Text = "Use selected source", Width = 145, Height = 29, BackColor = Color.FromArgb(37, 99, 235), ForeColor = DesktopTheme.Text, FlatStyle = FlatStyle.Flat, Enabled = false };
    internal int SelectedIndex => _sources.SelectedIndex;

    internal PresetSourceChoiceDialog(string presetName, string[] sourceLabels)
    {
        Text = "Choose assigned preset source";
        Size = new Size(660, 320); MinimumSize = new Size(600, 300); StartPosition = FormStartPosition.CenterParent;
        AppBranding.Apply(this);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = "More than one installed XML defines “" + presetName + "”. Choose the authored source to use for this build.", Dock = DockStyle.Fill }, 0, 0);
        _sources.Items.AddRange(sourceLabels);
        layout.Controls.Add(_sources, 0, 1);
        _use.Click += (_, _) => DialogResult = DialogResult.OK;
        _sources.SelectedIndexChanged += (_, _) => _use.Enabled = _sources.SelectedIndex >= 0;
        layout.Controls.Add(_use, 0, 2); Controls.Add(layout); DesktopTheme.Apply(this);
    }
}
