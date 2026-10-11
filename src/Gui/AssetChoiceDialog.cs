using AutoFollower;

namespace AutoFollower.Gui;

internal sealed class AssetChoiceDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly LeanAssetChoice[] _original;
    private readonly List<ChoiceRow> _rows;
    private bool _valid = true;
    private readonly Button _continue = new ReadableButton() { Text = "Continue build", Width = 130, Height = 34, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    public LeanAssetSelection[] Selections { get; private set; } = [];
    public bool CancelBuild { get; private set; }

    public AssetChoiceDialog(LeanAssetChoice[] choices, LeanAssetSelection[]? previous = null)
    {
        _original = choices;
        _rows = choices.Select(x => new ChoiceRow(x,
            x.RequiredMode ?? previous?.SingleOrDefault(p => p.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase))?.Mode
                ?? (x.CanRetain ? x.Mode : LeanPackagingChoices.Standalone))).ToList();
        Text = "Choose asset packaging";
        AppBranding.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 540);
        Size = new Size(1020, 680);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "Choose how each required file is included", Font = new Font("Segoe UI Semibold", 16F), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Asset", DataPropertyName = "Asset", ReadOnly = true, Width = 210 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Component", DataPropertyName = "Component", ReadOnly = true, Width = 155 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Provided by", DataPropertyName = "Provider", ReadOnly = true, Width = 165 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Include", DataPropertyName = "Mode", Width = 175, FlatStyle = FlatStyle.Flat });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mode details", DataPropertyName = "Details", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.CellBeginEdit += (_, e) => { if (e.ColumnIndex == 3 && (!_rows[e.RowIndex].Choice.CanRetain || _rows[e.RowIndex].Choice.RequiredMode!=null)) e.Cancel = true; };
        _grid.CellValueChanged += (_, _) => ValidateSelections();
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.DataError += (_, e) => e.ThrowException = false;
        root.Controls.Add(_grid, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        _summary.Dock = DockStyle.Fill;
        _summary.ForeColor = Color.FromArgb(55, 65, 81);
        _summary.Padding = new Padding(0, 7, 12, 0);
        bottom.Controls.Add(_summary, 0, 0);
        var bulk = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var allStandalone = new ReadableButton { Text = "Package all standalone", AutoSize = true };
        allStandalone.Click += (_, _) => SetAll(LeanPackagingChoices.Standalone);
        var allRetain = new ReadableButton { Text = "Retain available", AutoSize = true };
        allRetain.Click += (_, _) => SetAll(LeanPackagingChoices.Retain);
        bulk.Controls.Add(allStandalone); bulk.Controls.Add(allRetain);
        bottom.Controls.Add(bulk, 1, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        _continue.Click += (_, _) => { ValidateSelections(); if (_valid) { DialogResult = DialogResult.OK; Close(); } };
        var cancel = new ReadableButton { Text = "Cancel build", Width = 112, Height = 34 };
        cancel.Click += (_, _) => { CancelBuild = true; DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.Add(_continue); actions.Controls.Add(cancel);
        bottom.Controls.Add(actions, 0, 1); bottom.SetColumnSpan(actions, 2);
        root.Controls.Add(bottom, 0, 2);
        _grid.DataSource = _rows;
        _grid.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 3 && !_rows[e.RowIndex].Choice.CanRetain) e.ToolTipText = "Retaining this generated asset has no supported source equivalent."; };
        _grid.DataBindingComplete += (_, _) => ConfigureModeCells();
        ConfigureModeCells();
        ValidateSelections();
        DesktopTheme.Apply(this);
    }

    private void ConfigureModeCells()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_grid.Rows.Count <= i) continue;
            var cell = (DataGridViewComboBoxCell)_grid.Rows[i].Cells[3];
            cell.Items.Clear();
            if(_rows[i].Choice.RequiredMode is {} required)cell.Items.Add(required);
            else {cell.Items.Add(LeanPackagingChoices.Standalone);if (_rows[i].Choice.CanRetain) cell.Items.Add(LeanPackagingChoices.Retain);}
            cell.Value = _rows[i].Mode;
        }
    }

    private void SetAll(string mode)
    {
        for (int i = 0; i < _rows.Count; i++) _rows[i].Mode = _rows[i].Choice.RequiredMode ?? (mode == LeanPackagingChoices.Retain && !_rows[i].Choice.CanRetain ? LeanPackagingChoices.Standalone : mode);
        _grid.Refresh();
        ValidateSelections();
    }

    private void ValidateSelections()
    {
        if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        var selections = _rows.Select(x => new LeanAssetSelection(x.Choice.Id, x.Mode, x.Choice.SourceIdentity)).ToArray();
        _valid = selections.All(x => x.Mode == LeanPackagingChoices.Standalone || _original.Single(y => y.Id == x.Id).CanRetain);
        string[] retained = _original.Where(x => _rows.Single(y => y.Choice.Id == x.Id).Mode == LeanPackagingChoices.Retain).Select(x => x.ProviderName ?? x.Name).Distinct().ToArray();
        int bundled = _original.Count(x => _rows.Single(y => y.Choice.Id == x.Id).Mode == LeanPackagingChoices.Standalone && x.Layer == "shared");
        _summary.Text = $"Bundled: {bundled} file(s)  ·  Retained dependencies: {(retained.Length == 0 ? "None" : string.Join(", ", retained))}";
        if(_original.Any(x=>(x.Provider?.TextureRepairs?.Length??0)>0)) _summary.Text+="\r\nCreator texture paths will be repaired in packaged copies.";
        Selections = selections;
        _summary.ForeColor = _valid ? DesktopTheme.Text : DesktopTheme.Error;
        _continue.Enabled = _valid;
        _continue.BackColor = _valid ? Color.FromArgb(37, 99, 235) : Color.FromArgb(156, 163, 175);
        if (IsHandleCreated) _grid.Invalidate();
    }

    private sealed class ChoiceRow
    {
        public ChoiceRow(LeanAssetChoice choice, string mode) { Choice = choice; Mode = mode; }
        public LeanAssetChoice Choice { get; }
        public string Asset => Choice.Name;
        public string Component => Choice.Component;
        public string Provider => Choice.ProviderName ?? "Generated by build";
        public string Details => Choice.Constraint ?? (Choice.CanRetain ? "Either option is supported." : "Included in the package to preserve the generated result.");
        public string Mode { get; set; }
    }
}

