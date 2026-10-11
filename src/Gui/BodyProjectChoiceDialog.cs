using AutoFollower;

namespace AutoFollower.Gui;

internal sealed class BodyProjectChoiceDialog : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill };
    private readonly Button _continue = new ReadableButton() { Text = "Use selected project", AutoSize = true, Enabled = false };
    private readonly LeanBodyProjectChoice _choice;
    public LeanBodyProjectSelection? Selection { get; private set; }

    public BodyProjectChoiceDialog(LeanBodyProjectChoice choice)
    {
        _choice = choice;
        Text = "Choose BodySlide project";
        AppBranding.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 420); MinimumSize = new Size(740, 360);
        Font = new Font("Segoe UI", 9F);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(new Label { Text = "Several installed projects build this mesh. Choose the project used in your setup. If unsure, cancel and check BodySlide. This choice affects the Prebuilt output.", Dock = DockStyle.Fill }, 0, 0);
        root.Controls.Add(new Label { Text = choice.Target, Dock = DockStyle.Fill }, 0, 1);
        _list.Items.AddRange(choice.Options.Select(x => x.Name + " · " + Path.GetFileName(x.DefinitionReference)).ToArray());
        _list.SelectedIndexChanged += (_, _) => _continue.Enabled = _list.SelectedIndex >= 0;
        root.Controls.Add(_list, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        _continue.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            var option = _choice.Options[_list.SelectedIndex];
            Selection = new(_choice.Target, option.Name, option.DefinitionReference, option.DefinitionSha256);
            DialogResult = DialogResult.OK; Close();
        };
        var cancel = new ReadableButton { Text = "Cancel build", AutoSize = true };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.Add(_continue); actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 3); Controls.Add(root);
        CancelButton = cancel;
    }
}
