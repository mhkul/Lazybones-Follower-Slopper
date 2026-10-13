using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AutoFollower;

namespace AutoFollower.Gui;

internal sealed class MainForm : Form
{
    private readonly Panel _landing = new() { Dock = DockStyle.Fill };
    private readonly Panel _settingsPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _operationPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal) { ["Save"] = "", ["Cosave"] = "", ["HeadNif"] = "", ["TintDds"] = "", ["Jslot"] = "" };
    private readonly Dictionary<string, Label> _pathLabels = [];
    private readonly Label _environmentLabel = new();
    private readonly Label _landingError = new();
    private readonly Label _companionDetails = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button _prepareCancelButton = new() { Text = "Cancel checking", Visible = false, Width = 145, Height = 38 };
    private readonly Button _changeJslot = new ReadableButton { Text = "Change RaceMenu preset…", Width = 220, Height = 29 };
    private bool _explicitJslot;
    private readonly Label _deliveryFolderLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _cancelBuild = new ReadableButton { Text = "Cancel build", Width = 120, Height = 36 };
    private readonly Button _showZip = new ReadableButton { Text = "Show ZIP in folder", Width = 170, Height = 36 };
    private readonly Button _exit = new ReadableButton { Text = "Exit", Width = 80, Height = 36 };
    private readonly Button _editFollower = new ReadableButton { Text = "Edit follower settings", Width = 180, Height = 36 };
    private readonly Button _anotherCharacter = new ReadableButton { Text = "Choose another character", Width = 210, Height = 36 };
    private readonly Button _retryDelivery = new ReadableButton { Text = "Retry saving ZIP…", Width = 170, Height = 36 };
    private string _readyPackagePath = "";
    private CancellationTokenSource? _preparationCancel;
    private bool _preparing;
    private long _inputRevision;
    private readonly Label _settingsSummary = new();
    private readonly Label _settingsError = new();
    private readonly Button _equipmentDetails = new ReadableButton { Text = "Error report…", Dock = DockStyle.Fill, Visible = false };
    private readonly Button _buildFailureDetails = new ReadableButton { Text = "Error report…", AutoSize = true, Visible = false };
    private DiagnosticReport? _equipmentReport, _buildFailureReport;
    private DiagnosticReport? _landingReport;
    private readonly Button _landingReportButton = new ReadableButton { Text = "Error report…", AutoSize = true, Visible = false };
    private readonly Label _operationStatus = new();
    private TableLayoutPanel? _operationRoot;
    private Control? _operationHeader;
    private readonly Label _completionHeading = new() { Text = "Follower ready", Font = new Font("Segoe UI Semibold", 16F), AutoSize = true, Visible = false };
    private readonly Label _completionSummary = new() { Text = "Your install ZIP is ready.", AutoSize = true, Visible = false };
    private readonly TextBox _completionPath = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 48, Dock = DockStyle.Fill, Visible = false, AccessibleName = "Saved ZIP path" };
    private readonly Label _completionWarning = new() { AutoSize = true, MaximumSize = new Size(880, 0), ForeColor = Color.FromArgb(146, 91, 14), Visible = false };
    private readonly Button _detailsToggle = new ReadableButton { Text = "Build details ▸", AutoSize = true, Visible = false };
    private readonly TextBox _technicalDetails = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Visible = false, Height = 125, AccessibleName = "Build details" };
    private readonly Panel _detailsHost = new() { Dock = DockStyle.Fill, Visible = false, Height = 130 };
    private readonly ProgressBar _progress = new() { Visible = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<SettingsControls> _copies = [];
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 1200 };
    private readonly CancellationTokenSource _appLifetime = new();
    private ActiveMo2? _mo2;
    private InstalledSetup? _setup;
    private readonly ToolTip _tips=new(){AutoPopDelay=15000,ShowAlways=true};
    private FollowerInputPreview? _preview;
    private FollowerSettings? _settings;
    private bool _acknowledged;
    private LeanOutputSelection _outputs = new(true, false);
    private bool _obodyAvailable;
    private bool _syncing;
    private string _numericError = "";
    private string _equipmentError = "";
    private int? _relativeMaximum = 80;
    private double _relativeMultiplier = 1;
    private WorkDialog? _workDialog;
    private bool _closePending;
    private bool _operationActive;
    private CancellationTokenSource? _operationCancel;
    private string _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lazybones Follower Slopper", "ZIPs");
    private string _operationOutput = "";
    private string _deliveryPath = "";
    private string _requestPath = "";
    private LeanAssetSelection[]? _previousAssetSelections;
    private string _equipmentScope="";
    private string[] _excludedEquipment=[];
    private readonly Button _equipmentButton=new ReadableButton{Text="Choose items…"};
    private string EquipmentScope(FollowerInputPreview preview)
    {
        string environment=_setup!.Game+"|"+(_setup.Mo2?.InstanceIni??"Physical")+"|"+(_setup.Mo2?.Profile??"");
        if(_setup.Mo2!=null)foreach(string name in new[]{"plugins.txt","loadorder.txt","modlist.txt"})environment+="|"+Hashing.File(Path.Combine(_setup.Mo2.ProfileDirectory,name));
        else environment+="|"+string.Join("|",_setup.Snapshot!.Pins.Select(p=>p.Sha256));
        return preview.Pins[0].Path+"|"+preview.Pins[0].Sha256+"|"+environment;
    }
    private void ReconcileEquipment(FollowerInputPreview preview)
    {
        string scope=EquipmentScope(preview);if(scope!=_equipmentScope){_excludedEquipment=[];_equipmentScope=scope;_equipmentError="";}
        _equipmentButton.Text=_excludedEquipment.Length==0?"Choose items…":$"{_excludedEquipment.Length} excluded…";
    }
    private async Task ChooseEquipmentAsync()
    {
        if(_operationActive||_preparing||_preview==null||_setup==null)return;
        var preview=_preview;var setup=_setup;ReconcileEquipment(preview);
        _operationCancel=CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token);var token=_operationCancel.Token;
        _equipmentError="";_equipmentReport=null;_operationActive=true;UpdateBuildGate();_equipmentButton.Enabled=false;
        _workDialog=new WorkDialog("Checking equipment","Reading names and applicable models…",()=>_operationCancel.Cancel());_workDialog.Show(this);
        try{
            var catalog=await Task.Run(()=>BuildInputs.EquipmentCatalog(setup,preview,token),token);token.ThrowIfCancellationRequested();
            _workDialog.Complete();_workDialog=null;
            using var dialog=new EquipmentChoiceDialog(catalog,_excludedEquipment);
            if(dialog.ShowDialog(this)==DialogResult.OK){_excludedEquipment=dialog.Excluded;_previousAssetSelections=null;_readyPackagePath="";ResetResult();ReconcileEquipment(preview);}
        }catch(OperationCanceledException){}
        catch(Exception e){_equipmentError="Equipment: "+FailureSummary(e);_equipmentReport=FailureReport("Choose items",e);}
        finally{_workDialog?.Complete();_workDialog=null;_operationActive=false;_operationCancel.Dispose();_operationCancel=null;_equipmentButton.Enabled=true;UpdateBuildGate();if(_closePending)Close();}
    }
    private readonly Button[] _buildButtons = new Button[2];

    public MainForm()
    {
        Text = "Lazybones Follower Slopper";
        AppBranding.Apply(this);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 600);
        Size = new Size(960, 760);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Segoe UI", 9.5F);
        KeyPreview = true;
        KeyDown += OnKeyDown;
        FormClosing += (_, e) =>
        {
            _preparationCancel?.Cancel(); _operationCancel?.Cancel(); _appLifetime.Cancel();
            if (_preparing || _operationActive) { e.Cancel = true; _closePending = true; _workDialog?.SetStatus("Cancelling before closing…"); }
        };
        _setup=InstalledSetup.Discover(out string environment);_mo2=_setup?.Mo2;
        BuildLanding();
        BuildSettings();
        BuildOperationPage();
        Controls.Add(_landing); Controls.Add(_settingsPage); Controls.Add(_operationPage);
        _environmentLabel.Text = environment;
        _environmentLabel.ForeColor = _setup == null ? DesktopTheme.Error : Color.FromArgb(148, 210, 174);
        _progressTimer.Tick += (_, _) => RefreshProgress();
        Shown += async (_, _) => await Task.Run(() => { try { WorkingStorage.Sweep(BuildDestinations.DefaultRoot()); } catch (Exception) { /* Optional maintenance never blocks startup. */ } });
        ShowPage(_landing);
        DesktopTheme.Apply(this);
    }

    internal static void RenderEvidence(string outputDirectory, string fixtureDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string Get(string extension) => Directory.EnumerateFiles(fixtureDirectory, "*" + extension, SearchOption.TopDirectoryOnly).Single();
        using var form = new MainForm();
        form.Show();
        Application.DoEvents();
        form.SaveRender(Path.Combine(outputDirectory, "landing-screen.png"));
        var selection = new Dictionary<string, string>
        {
            ["Save"] = Get(".ess"), ["Cosave"] = Get(".skse"), ["HeadNif"] = Get(".nif"), ["TintDds"] = Get(".dds"), ["Jslot"] = Get(".jslot")
        };
        form._preview = form.PreparePreview(selection, CancellationToken.None);
        form._settings = form._preview.SavePreview.Context.Defaults;
        form.RefreshSettingsControls();
        form.ShowSettings();
        Application.DoEvents();
        form.SaveRender(Path.Combine(outputDirectory, "basic-settings.png"));
        int basicSelectedIndex = form._tabs.SelectedIndex;
        form._tabs.SelectedIndex = 1;
        Application.DoEvents();
        int advancedSelectedIndex = form._tabs.SelectedIndex;
        form.SaveRender(Path.Combine(outputDirectory, "advanced-settings.png"));
        File.WriteAllText(Path.Combine(outputDirectory, "render-state.json"), JsonSerializer.Serialize(new
        {
            Source = "WinForms Control.DrawToBitmap; control layout and initialized values only",
            UsesAcceptedInputFixture = true,
            AssetDiscoveryOrBuildRun = false,
            LandingPickers = form._pathLabels.Count,
            SettingsTabs = form._tabs.TabPages.Cast<TabPage>().Select(x => x.Text).ToArray(),
            BasicSelectedIndex = basicSelectedIndex,
            AdvancedSelectedIndexAfterSwitch = advancedSelectedIndex,
            SharedDisclosureCopies = form._copies.Count(x => x.Disclaimer != null),
            SharedAcknowledgmentInitiallyChecked = form._acknowledged,
            MarriageEligibleDefault = form._settings?.MarriageEligible,
            CurrentProcessHasSelectedMo2 = form._mo2 != null,
            BuildButtonsEnabledInRenderMode = form._buildButtons.Select(x => x.Enabled).ToArray()
        }, AutoFollower.Wire.Options));
        form.ShowPage(form._operationPage);
        form.ResetResult();
        form._deliveryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lazybones Follower Slopper", "ZIPs", "Sen-Follower-Prebuilt-and-OBody.zip");
        form._operationStatus.Visible = false;
        form.SetCompletionHeader(true);
        form._completionHeading.Visible = form._completionSummary.Visible = form._completionPath.Visible = form._completionWarning.Visible = form._detailsToggle.Visible = true;
        form._completionPath.Text = form._deliveryPath; form._completionPath.AccessibleDescription = form._deliveryPath;
        form._completionWarning.Text = "RaceMenu overlays may be omitted from this package.";
        form._technicalDetails.Text = "Structural checks passed. In-game behavior has not been tested.\r\nSaved destination: Whiterun · Breezehome.\r\nFollower level follows the player's level.\r\nOverlay details and runtime support evidence are shown here.";
        form._showZip.Visible = form._editFollower.Visible = form._anotherCharacter.Visible = form._exit.Visible = true;
        form._showZip.Enabled = form._editFollower.Enabled = form._anotherCharacter.Enabled = form._exit.Enabled = true;
        form.Size = new Size(960, 760); form.PerformLayout(); Application.DoEvents();
        form.SaveRender(Path.Combine(outputDirectory, "completion-normal.png"));
        // Exercise the actual button after a successful delivery whose ZIP was
        // moved. The error stays visible beside the saved path and actions.
        string savedPath = form._completionPath.Text;
        form._deliveryPath = Path.Combine(outputDirectory, "missing-delivered.zip");
        form._showZip.PerformClick(); Application.DoEvents();
        bool showZipErrorVisible = form._operationStatus.Visible && form._operationStatus.Text.StartsWith("Could not show ZIP:") && form._completionPath.Text == savedPath && form._showZip.Visible && form._editFollower.Visible && form._anotherCharacter.Visible && form._exit.Visible && !form._detailsHost.Visible;
        if (!showZipErrorVisible) throw new InvalidOperationException("Completion ZIP error is hidden or lost the delivery controls.");
        form.SaveRender(Path.Combine(outputDirectory, "completion-show-zip-error.png"));
        File.WriteAllText(Path.Combine(outputDirectory, "completion-error-check.json"), JsonSerializer.Serialize(new { ShowZipErrorVisible = showZipErrorVisible, SavedPathPreserved = true, CompletionActionsPreserved = true, BuildDetailsRequired = false }, AutoFollower.Wire.Options));
        form._operationStatus.Visible = false;
        form._completionPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), new string('L', 115), new string('D', 75), "Sen-Follower-Prebuilt-and-OBody.zip");
        form._detailsToggle.PerformClick();
        form.Size = form.MinimumSize; form.PerformLayout(); Application.DoEvents();
        form.SaveRender(Path.Combine(outputDirectory, "completion-long-path-details-minimum.png"));
        form.Size = new Size(960, 760); form._detailsToggle.PerformClick();
        form._deliveryPath = ""; form._readyPackagePath = Path.Combine(outputDirectory, "layout-only-fixture.zip"); File.WriteAllText(form._readyPackagePath, "layout-only fixture"); form.ResetResult(); form._operationStatus.Visible = true; form._operationStatus.Text = "Build finished, but saving the ZIP did not complete: destination is unavailable. Use Retry saving ZIP to choose a folder and save this finished build.";
        Application.DoEvents(); form.SaveRender(Path.Combine(outputDirectory, "delivery-failure.png"));
        using (var work = new WorkDialog("Building follower", "Building the plugin and installer package…", () => { }))
        { work.Show(form); Application.DoEvents(); using var bitmap = new Bitmap(work.ClientSize.Width, work.ClientSize.Height); work.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(outputDirectory, "progress-dialog.png")); }
        using (var picker = new ViewFilePicker("Choose RaceMenu preset", fixtureDirectory, ".jslot"))
        { picker.Show(form); Application.DoEvents(); using var bitmap = new Bitmap(picker.ClientSize.Width, picker.ClientSize.Height); picker.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(outputDirectory, "race-menu-picker-dialog.png")); }
        bool bodyProjectIcon, assetChoiceIcon;
        using (var body = new BodyProjectChoiceDialog(new LeanBodyProjectChoice("meshes\\armor\\example_0.nif", [new LeanBodyProjectOption("Example project", "CalienteTools/BodySlide/SliderSets/example.osp", new string('A', 64)), new LeanBodyProjectOption("Alternate project", "CalienteTools/BodySlide/SliderSets/alternate.osp", new string('B', 64))])))
        { DesktopTheme.Apply(body); body.Show(form); Application.DoEvents(); bodyProjectIcon = body.Icon is not null; using var bitmap = new Bitmap(body.ClientSize.Width, body.ClientSize.Height); body.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(outputDirectory, "bodyslide-project-dialog.png")); }
        using (var asset = new AssetChoiceDialog([new LeanAssetChoice("layout-example", "meshes\\armor\\example.nif", "Example armor mesh", "ARMO", "Armor", "shared", ["Prebuilt"], LeanPackagingChoices.Standalone, false, "Generated output is required", null, null, [], null)]))
        { asset.Show(form); Application.DoEvents(); assetChoiceIcon = asset.Icon is not null; using var bitmap = new Bitmap(asset.ClientSize.Width, asset.ClientSize.Height); asset.DrawToBitmap(bitmap, new Rectangle(Point.Empty, asset.ClientSize)); bitmap.Save(Path.Combine(outputDirectory, "asset-choice-dialog.png")); }
        bool presetSourceIcon;
        using (var preset = new PresetSourceChoiceDialog("Example preset", ["preset-a.xml · Example mod", "preset-b.xml · Alternate mod"]))
        { preset.Show(form); Application.DoEvents(); presetSourceIcon = preset.Icon is not null; using var bitmap = new Bitmap(preset.ClientSize.Width, preset.ClientSize.Height); preset.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(outputDirectory, "preset-source-dialog.png")); }
        File.WriteAllText(Path.Combine(outputDirectory, "completion-layout.json"), JsonSerializer.Serialize(new
        {
            NormalSize = new { Width = 960, Height = 760 }, MinimumSize = new { Width = form.MinimumSize.Width, Height = form.MinimumSize.Height },
            LongPathLength = form._completionPath.Text.Length, DetailsInitiallyClosed = true,
            DialogIcons = new { Main = form.Icon is not null, Progress = true, RaceMenuPicker = true, BodySlideProject = bodyProjectIcon, AssetChoice = assetChoiceIcon, AssignedPresetChooser = presetSourceIcon },
            DeliveryFailureKeepsRetryAvailable = true, ExistingFourCompletionActions = new[] { "Show ZIP in folder", "Edit follower settings", "Choose another character", "Exit" }
        }, AutoFollower.Wire.Options));
        form.Close();
    }

    private void SaveRender(string path)
    {
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void BuildLanding()
    {
        var root = PageRoot(); _landing.Controls.Add(root);
        root.Controls.Add(Header("Create a follower", "Choose the save and matching RaceMenu export files to begin."), 0, 0);
        root.RowStyles[1].Height=44;root.RowStyles[6].Height=66;
        var setup=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
        if (!LeanMo2Sources.HasContext)
        {
            var confirm=new ReadableButton{Text="Skyrim folder…",Size=new Size(150,28)};
            confirm.Click+=(_,_)=>ChooseEnvironment();setup.Controls.Add(confirm);
        }
        root.Controls.Add(setup,0,1);
        _environmentLabel.Dock = DockStyle.Fill;
        root.Controls.Add(_environmentLabel, 0, 2);

        var inputList = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 12, 0, 0) };
        for (int i = 0; i < 2; i++) inputList.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        AddPickerRow(inputList, 0, "Save", "Character save", "Save games (*.ess)|*.ess");
        AddPickerRow(inputList, 1, "HeadNif", "RaceMenu head", "Head meshes (*.nif)|*.nif");
        root.Controls.Add(inputList, 0, 3);
        root.RowStyles[3].SizeType = SizeType.Absolute; root.RowStyles[3].Height = 132;

        var association = new Label
        {
            Text = "Choose exports for this character. Continue resolves the co-save, tint and JSlot.",
            AutoSize = false, Height = 52, Dock = DockStyle.Fill, ForeColor = DesktopTheme.Muted, Padding = new Padding(0, 6, 0, 0)
        };
        var companionsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        companionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        companionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 225));
        companionsRow.Controls.Add(association, 0, 0);
        _changeJslot.Click += (_, _) => ChangeJslot();
        companionsRow.Controls.Add(_changeJslot, 1, 0);
        root.Controls.Add(companionsRow, 0, 4);
        _landingError.Dock = DockStyle.Fill; _landingError.ForeColor = DesktopTheme.Error; _landingError.AutoEllipsis = true;
        root.Controls.Add(_landingError, 0, 5);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        var next = PrimaryButton("Continue"); next.Click += async (_, _) => await PrepareInputsAsync();
        footer.Controls.Add(next);
        _prepareCancelButton.Click += (_, _) => { _preparationCancel?.Cancel(); _inputRevision++; };
        footer.Controls.Add(_prepareCancelButton);
        _landingReportButton.Click += (_, _) => ShowFailureReport(_landingReport);
        footer.Controls.Add(_landingReportButton);
        var history = new ReadableButton { Text = "Recent diagnostics…", AutoSize = true };
        history.Click += (_, _) => ShowRecentDiagnostics(); footer.Controls.Add(history);
        root.Controls.Add(footer, 0, 6);
        root.RowCount = 8; root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _companionDetails.ForeColor = DesktopTheme.Muted;
        root.Controls.Add(_companionDetails, 0, 7);
    }

    private void AddPickerRow(TableLayoutPanel parent, int row, string key, string label, string filter)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 4, 0, 4) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10F) }, 0, 0);
        var path = new Label { Text = "Choose a file", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = DesktopTheme.Muted, Padding = new Padding(7, 0, 7, 0) };
        _pathLabels[key] = path; layout.Controls.Add(path, 1, 0);
        var button = new ReadableButton { Text = "Browse…", Anchor = AnchorStyles.Left, Width = 95, Height = 28 };
        button.Click += (_, _) => PickFile(key, label, filter);
        layout.Controls.Add(button, 2, 0);
        parent.Controls.Add(layout, 0, row);
    }

    private void PickFile(string key, string label, string filter)
    {
        if (_preparing || _operationActive) return;
        string initial=!string.IsNullOrWhiteSpace(_paths[key])?Path.GetDirectoryName(_paths[key])!:PickerLocations.Default(key,_setup);
        if(key is "HeadNif" or "Jslot" or "TintDds"){
            if(_setup!=null&&!Directory.Exists(Path.Combine(_setup.Game,"Data","SKSE","Plugins","CharGen")))_landingError.Text="CharGen is absent; browsing the nearest selected-game folder.";
            using var picker=new ViewFilePicker("Choose "+label.ToLowerInvariant(),initial,key=="HeadNif"?".nif":key=="Jslot"?".jslot":".dds");
            if(picker.ShowDialog(_workDialog??(Form)this)!=DialogResult.OK)return;ReplaceAnchor(key,picker.FileName!);
        }else{using var picker=new OpenFileDialog{Title="Choose "+label.ToLowerInvariant(),Filter=filter+"|All files (*.*)|*.*",CheckFileExists=true,Multiselect=false,RestoreDirectory=true,InitialDirectory=initial};if(picker.ShowDialog(_workDialog??(Form)this)!=DialogResult.OK)return;ReplaceAnchor(key,picker.FileName);}
    }

    private void ReplaceAnchor(string key, string path)
    {
        path = Path.GetFullPath(path);
        if (_paths[key].Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        _inputRevision++; _preparationCancel?.Cancel();
        if(key=="Save"){_equipmentScope="";_excludedEquipment=[];_equipmentButton.Text="Choose items…";}
        _paths[key]=path;_pathLabels[key].Text=Path.GetFileName(path);_tips.SetToolTip(_pathLabels[key],path);_pathLabels[key].AccessibleDescription=path;
        foreach (string companion in new[] { "Cosave", "TintDds", "Jslot" }) _paths[companion] = "";
        _explicitJslot = false;
        _companionDetails.Text = "Companion files will be refreshed on Continue.";
        _landingError.Text = "";
        InvalidatePreparedInputs("An input changed. Continue again to refresh defaults and asset choices.");
    }

    private async Task PrepareInputsAsync()
    {
        if (_operationActive || _preparing) return;
        _landingError.Text = "";
        if (string.IsNullOrWhiteSpace(_paths["Save"]) || string.IsNullOrWhiteSpace(_paths["HeadNif"])) { _landingError.Text = "Choose the save and RaceMenu head file first."; return; }
        try { _setup?.Revalidate(); }
        catch (Exception e) { ClearEnvironmentCompanions(); InvalidatePreparedInputs("Environment changed. Reopen the app in the selected environment."); _landingError.Text = FriendlyError(e) + " Reopen the app in the intended environment."; return; }
        long revision = _inputRevision;
        var paths = new Dictionary<string, string>(_paths);
        _preparationCancel = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token);
        var token = _preparationCancel.Token;
        SetLandingBusy(true);
        _workDialog = new WorkDialog("Checking inputs", "Resolving and checking the required files…", () => _preparationCancel?.Cancel()); _workDialog.Show(this);
        try
        {
            if (_preview == null || !PreviewStillSelected())
            {
                var companions = await Task.Run(() => InputCompanions.Resolve(paths["Save"], paths["HeadNif"], token), token);
                foreach (var companion in companions)
                {
                    token.ThrowIfCancellationRequested();
                    string? choice = companion.Key == "Jslot" && _explicitJslot ? paths["Jslot"] : companion.Resolved;
                    if (choice == null)
                    {
                        choice = ChooseCompanion(companion.Key, companion.Label, paths);
                        if (choice == null) { _landingError.Text = "Companion selection cancelled. Your two selected files are kept."; return; }
                        token.ThrowIfCancellationRequested();
                        if (revision != _inputRevision) return;
                        if (companion.Key == "Jslot") { _explicitJslot = true; _paths["Jslot"] = choice; }
                    }
                    paths[companion.Key] = choice;
                }
            }
            token.ThrowIfCancellationRequested();
            if (revision != _inputRevision) return;
            foreach (var path in paths) _paths[path.Key] = path.Value;
            RefreshCompanionDetails();
            var preview = await Task.Run(() => PreparePreview(paths, token), token);
            bool available = _setup != null && await Task.Run(() => BuildInputs.ObodyAvailable(_setup), token);
            token.ThrowIfCancellationRequested();
            if (revision != _inputRevision || !paths["Save"].Equals(_paths["Save"], StringComparison.OrdinalIgnoreCase) || !paths["HeadNif"].Equals(_paths["HeadNif"], StringComparison.OrdinalIgnoreCase)) return;
            bool newCharacter = _preview != null && !_preview.Selection.Save.Equals(paths["Save"], StringComparison.OrdinalIgnoreCase);
            if (newCharacter)
            {
                _settings = preview.SavePreview.Context.Defaults;
                _acknowledged = false;
                foreach (var copy in _copies) copy.Disclaimer.Checked = false;
            }
            else if (_settings == null || _preview == null) _settings = preview.SavePreview.Context.Defaults;
            bool hasVoiceOptions = preview.SavePreview.Context.Catalog.Voices.Any(x => x.Sex == preview.SavePreview.SavedState.Actor.Sex);
            if (!hasVoiceOptions) _settings = _settings! with { VoiceId = "" };
            if (_preview == null) _outputs = new(true, available);
            else if (!available) _outputs = _outputs with { Obody = false };
            _obodyAvailable = available;
            ReconcileEquipment(preview);
            _preview = preview;
            foreach (var path in paths) _paths[path.Key] = path.Value;
            RefreshCompanionDetails();
            RefreshSettingsControls();
            ShowSettings();
        }
        catch (OperationCanceledException) { _landingError.Text = "Checking cancelled. Your selected files are kept."; }
        catch (Exception e) { InvalidatePreparedInputs("Selected inputs could not be prepared."); _landingError.Text = FollowerInputErrors.LandingMessage(e, paths["Jslot"]); _landingReport = FailureReport("Prepare inputs", e); _landingReportButton.Visible = true; }
        finally { _workDialog?.Complete(); _workDialog = null; SetLandingBusy(false); _preparationCancel?.Dispose(); _preparationCancel = null; if (_closePending) Close(); }
    }

    private string? ChooseCompanion(string key, string label, Dictionary<string, string> paths)
    {
        if (key == "Cosave")
        {
            using var picker = new OpenFileDialog { Title = "Choose " + label, Filter = "Co-save (*.skse)|*.skse", CheckFileExists = true, RestoreDirectory = true, InitialDirectory = Path.GetDirectoryName(paths["Save"]) };
            return picker.ShowDialog(_workDialog ?? (Form)this) == DialogResult.OK ? Path.GetFullPath(picker.FileName) : null;
        }
        string initial = _setup != null ? PickerLocations.Default(key, _setup) : Path.GetDirectoryName(paths["HeadNif"])!;
        if (_setup == null && key == "Jslot" && Directory.Exists(Path.Combine(initial, "Presets"))) initial = Path.Combine(initial, "Presets");
        using var view = new ViewFilePicker("Choose " + label, initial, key == "Jslot" ? ".jslot" : ".dds");
        return view.ShowDialog(_workDialog ?? (Form)this) == DialogResult.OK ? view.FileName : null;
    }

    private void ChangeJslot()
    {
        if (_preparing || _operationActive) return;
        if (_paths["Save"].Length == 0 || _paths["HeadNif"].Length == 0) { _landingError.Text = "Choose the save and RaceMenu head first."; return; }
        string? choice = ChooseCompanion("Jslot", "RaceMenu preset", _paths);
        if (choice == null) return;
        _inputRevision++;
        _paths["Jslot"] = choice; _explicitJslot = true;
        InvalidatePreparedInputs("Preset replaced. Continue to check it against your save and head.");
        RefreshCompanionDetails(); _landingError.Text = "Preset selected: " + Path.GetFileName(choice) + ". Continue to check it.";
    }

    private void RefreshCompanionDetails() => _companionDetails.Text = string.Join("\r\n", new[] { "Cosave", "TintDds", "Jslot" }.Select(key => key + ": " + _paths[key]));

    private FollowerInputPreview PreparePreview(Dictionary<string, string> p, CancellationToken token)
    {
        var selection = new FollowerInputSelection(p["Save"], p["Cosave"], p["HeadNif"], p["TintDds"], p["Jslot"],
            "Selected RaceMenu exports supplied for the selected character and current environment; the bundle has no unique save-session identifier.", "",
            _setup==null?"":FileVersionInfo.GetVersionInfo(Path.Combine(_setup.Game,"SkyrimSE.exe")).FileVersion??"");
        return FollowerInputPreparation.Prepare(selection, token, LeanExportInputs.RaceMenuTintReference);
    }

    private void InvalidatePreparedInputs(string message)
    {
        _preview = null;
        _equipmentError = "";
        _settings = null;
        _numericError = "";
        _acknowledged = false;
        _outputs = new(true, false); _obodyAvailable = false;
        _previousAssetSelections = null;
        _readyPackagePath = "";
        ResetResult();
        _settingsSummary.Text = message;
        _syncing = true;
        foreach (var copy in _copies)
        {
            copy.Disclaimer.Checked = false;
            copy.Prebuilt.Checked = true; copy.Obody.Checked = false; copy.Obody.Enabled = false;
        }
        _syncing = false;
        UpdateBuildGate();
    }

    private bool PreviewStillSelected() => _preview != null && _preview.Selection.Paths.SequenceEqual(
        new[] { _paths["Save"], _paths["Cosave"], _paths["HeadNif"], _paths["TintDds"], _paths["Jslot"] }, StringComparer.OrdinalIgnoreCase);

    private void BuildSettings()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16, 10, 16, 10), BackColor = DesktopTheme.Background };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _settingsPage.Controls.Add(root);
        root.Controls.Add(Header("Follower details", ""), 0, 0);
        var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var back = new ReadableButton { Text = "← Inputs", Width = 110, Height = 34 };
        back.Click += (_, _) => { ReadControls(_tabs.SelectedIndex); ShowPage(_landing); };
        nav.Controls.Add(back, 0, 0);
        _settingsSummary.AutoEllipsis=true;_settingsSummary.Dock = DockStyle.Fill; _settingsSummary.ForeColor = DesktopTheme.Muted; _settingsSummary.TextAlign = ContentAlignment.MiddleLeft;
        nav.Controls.Add(_settingsSummary, 1, 0);
        var destination = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        destination.Controls.Add(new Label { Text = "Save ZIP to", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        destination.Controls.Add(_deliveryFolderLabel, 1, 0);
        var outputButton = new ReadableButton { Text = "Change…", Width = 100, Height = 29 };
        outputButton.Click += (_, _) => PickOutputFolder(); destination.Controls.Add(outputButton, 2, 0);
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        navigation.Controls.Add(nav, 0, 0); navigation.Controls.Add(destination, 0, 1);
        root.Controls.Add(navigation, 0, 1); RefreshDeliveryFolder();
        _tabs.Dock = DockStyle.Fill; _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += (_, e) =>
        {
            using var brush = new SolidBrush(e.Index == _tabs.SelectedIndex ? DesktopTheme.Surface : DesktopTheme.Background);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, Font, e.Bounds, DesktopTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        _tabs.SelectedIndexChanged += (_, _) => { ReadControls(_copies.FindIndex(x => x.Tab == _tabs.SelectedTab)); UpdateBuildGate(); };
        _tabs.TabPages.Clear(); _copies.Clear();
        var basic = new TabPage("Basic") { BackColor = DesktopTheme.Background };
        var advanced = new TabPage("Advanced") { BackColor = DesktopTheme.Background };
        _copies.Add(CreateSettingsTab(basic, false, 0));
        _copies.Add(CreateSettingsTab(advanced, true, 1));
        _tabs.TabPages.Add(basic); _tabs.TabPages.Add(advanced);
        root.Controls.Add(_tabs, 0, 2);
        _settingsError.Dock = DockStyle.Fill; _settingsError.ForeColor = DesktopTheme.Error; _settingsError.AutoEllipsis = true;
        var errorRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        errorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); errorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        errorRow.Controls.Add(_settingsError, 0, 0); errorRow.Controls.Add(_equipmentDetails, 1, 0);
        _equipmentDetails.Click += (_, _) => ShowFailureReport(_equipmentReport);
        root.Controls.Add(errorRow, 0, 3);
    }

    private SettingsControls CreateSettingsTab(TabPage page, bool advanced, int index)
    {
        var tabLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(8,4,8,4)};
        tabLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));tabLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,56));tabLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));tabLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,52));page.Controls.Add(tabLayout);
        var notices=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};notices.RowStyles.Add(new RowStyle(SizeType.Percent,50));notices.RowStyles.Add(new RowStyle(SizeType.Percent,50));tabLayout.Controls.Add(notices,0,0);
        var columns=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1};columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));columns.RowStyles.Add(new RowStyle(SizeType.Absolute,0));columns.Layout+=(_,_)=>{if(columns.RowStyles[0].Height!=columns.ClientSize.Height)columns.RowStyles[0].Height=columns.ClientSize.Height;};tabLayout.Controls.Add(columns,0,1);
        TableLayoutPanel Column(){var t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=advanced?7:6,Padding=new Padding(2)};t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));for(int i=0;i<(advanced?7:6);i++)t.RowStyles.Add(new RowStyle(SizeType.Absolute,0));t.Layout+=(_,_)=>{float height=Math.Max(0,t.ClientSize.Height-t.Padding.Vertical)/(advanced?7f:6f);foreach(RowStyle row in t.RowStyles)if(Math.Abs(row.Height-height)>0.1f)row.Height=height;};return t;}
        var left=Column();var right=Column();columns.Controls.Add(left,0,0);columns.Controls.Add(right,1,0);
        void Field(TableLayoutPanel table,int row,string label,Control control,string? help=null){var r=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0,2,5,2)};r.RowStyles.Add(new RowStyle(SizeType.Absolute,0));r.Layout+=(_,_)=>{if(r.RowStyles[0].Height!=r.ClientSize.Height)r.RowStyles[0].Height=r.ClientSize.Height;};r.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));r.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));var caption=new Label{Text=label,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};r.Controls.Add(caption,0,0);control.Dock=DockStyle.Fill;control.Margin=new Padding(2,0,2,0);r.Controls.Add(control,1,0);table.Controls.Add(r,0,row);if(help!=null){_tips.SetToolTip(control,help);_tips.SetToolTip(caption,help);control.AccessibleDescription=help;}}
        ComboBox? raceMode = advanced ? Combo([("private", "Generate new custom race"), ("existing", "Use existing race")]) : null;
        if(raceMode!=null){Field(right,6,"Race",raceMode,"Generate a private copy of the captured race, or use its installed race and skeleton. Existing race packages no skeleton and retains that provider. Captured abilities and other independent records may still require their source plugins in either mode.");raceMode.SelectedValueChanged+=(_,_)=>{if(_syncing||_operationActive||_settings==null)return;_settings=_settings with{RaceMode=raceMode.SelectedValue?.ToString()??"private"};_previousAssetSelections=null;_readyPackagePath="";ResetResult();RefreshSettingsControls();};}
        if(advanced){Field(left,6,"Equipment",_equipmentButton,"Include or leave out whole armor pieces and weapons. All start included; shared inputs stay with their remaining consumers.");_equipmentButton.Click+=async(_,_)=>await ChooseEquipmentAsync();}
        var name = new TextBox();
        var character = new TextBox();
        var scale = Numeric(0.001m, 10m, 3);
        var min = Numeric(1, 1000, 0);
        var max = new TextBox { PlaceholderText = "No cap" };
        var mode = Combo([("player-relative", "Player-relative"), ("fixed", "Fixed level")]);
        var multiplier = Numeric(0.1m, 10m, 3);
        var klass = Combo(FollowerSettingsPolicy.Catalog.Classes.Select(x => (x.Id, x.Label)).ToArray());
        var voice = Combo([]);
        var marriage = new ReadableCheckBox { Text = "Marriage eligible", AutoSize = true };
        var prebuilt = new ReadableCheckBox { Text = "BodySlide build", AutoSize = true, Checked = true, AccessibleName = "BodySlide build" };
        var obody = new ReadableCheckBox { Text = "OBody NG (as-is)", AutoSize = true, Enabled = false, AccessibleName = "OBody NG (as-is)" };
        var outputHelp = new Label { AutoSize = true, ForeColor = DesktopTheme.Muted };
        var outputs = new FlowLayoutPanel { Height = 30, WrapContents = false };
        _tips.SetToolTip(prebuilt, "Builds affected body and outfit meshes with the saved preset in BodySlide, so the shape works without OBody NG.");
        prebuilt.AccessibleDescription = "Builds affected body and outfit meshes with the saved preset in BodySlide, so the shape works without OBody NG.";
        _tips.SetToolTip(obody, ObodyOutputHelp());
        obody.AccessibleDescription = ObodyOutputHelp();
        outputs.Controls.Add(prebuilt); outputs.Controls.Add(obody);
        outputHelp.Dock=DockStyle.Fill;outputHelp.AutoSize=false;outputHelp.AutoEllipsis=true;notices.Controls.Add(outputHelp,0,0);
        notices.Controls.Add(new Label{Text=LeanOverlayPolicy.Warning,Dock=DockStyle.Fill,ForeColor=Color.FromArgb(237,198,139),AutoEllipsis=true},0,1);
        var combat=Combo(FollowerSettingsPolicy.Catalog.CombatStyles.Select(x=>(x.Id,x.Label)).ToArray());
        var perks=Combo(FollowerSettingsPolicy.Catalog.PerkPackages.Select(x=>(x.Id,x.Label)).ToArray());
        var spells=Combo(FollowerSettingsPolicy.Catalog.SpellPackages.Select(x=>(x.Id,x.Label)).ToArray());
        Field(left,0,"Mod name",name,"Names the plugin, package and private asset folders.");
        Field(left,1,"Character",character,"Display name in game.");
        Field(left,2,"Scale",scale,"Starts with the selected save's scale.");
        var leveling=new FlowLayoutPanel{WrapContents=false};mode.Width=125;multiplier.Width=65;leveling.Controls.Add(mode);leveling.Controls.Add(new Label{Text="×",AutoSize=true,Padding=new Padding(0,5,0,0)});leveling.Controls.Add(multiplier);
        foreach(Control item in leveling.Controls)item.Margin=new Padding(1,0,1,0);
        Field(left,3,"Level",leveling,"Player-relative: multiplier × player level. Fixed: uses Minimum.");
        var limits=new FlowLayoutPanel{WrapContents=false};min.Width=57;max.Width=57;limits.Controls.Add(new Label{Text="Min",AutoSize=true,Padding=new Padding(0,5,0,0)});limits.Controls.Add(min);limits.Controls.Add(new Label{Text="Max",AutoSize=true,Padding=new Padding(0,5,0,0)});limits.Controls.Add(max);
        foreach(Control item in limits.Controls)item.Margin=new Padding(1,0,1,0);
        Field(left,4,"Limits",limits,"Leave Maximum blank for no user-set cap.");
        Field(left,5,"Outputs",outputs,"Select follower versions to include. One selected creates a plain ZIP; both build both versions for an installer choice, and both builds must succeed.");
        var classRow=new TableLayoutPanel{ColumnCount=2,RowCount=1};classRow.RowStyles.Add(new RowStyle(SizeType.Absolute,0));classRow.Layout+=(_,_)=>{if(classRow.RowStyles[0].Height!=classRow.ClientSize.Height)classRow.RowStyles[0].Height=classRow.ClientSize.Height;};classRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));classRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,38));klass.Dock=DockStyle.Fill;klass.Margin=Padding.Empty;classRow.Controls.Add(klass,0,0);
        var defaults=new ReadableButton{Text="↺",Dock=DockStyle.Fill,Margin=new Padding(1)};_tips.SetToolTip(defaults,"Use class defaults for combat, perks and spells.");defaults.AccessibleName="Use class defaults";defaults.Click+=(_,_)=>{ReadControls(index);_settings=FollowerSettingsPolicy.SelectClass(_settings??new(),klass.SelectedValue?.ToString()??"warrior");RefreshSettingsControls();};classRow.Controls.Add(defaults,1,0);
        Field(right,0,"Class",classRow,"Changing class applies its mapped combat, perks and spells. Advanced choices override them.");_tips.SetToolTip(klass,"Changing class applies its mapped combat, perks and spells. Advanced choices override them.");
        Label? basicMapping=null;
        if(advanced){Field(right,1,"Combat",combat);Field(right,2,"Perks",perks);Field(right,3,"Spells",spells);
            perks.SelectedValueChanged+=(_,_)=>_tips.SetToolTip(perks,FollowerSettingsPolicy.Catalog.PerkPackages.SingleOrDefault(x=>x.Id==perks.SelectedValue?.ToString())?.Description??"No added perks.");
            spells.SelectedValueChanged+=(_,_)=>_tips.SetToolTip(spells,FollowerSettingsPolicy.Catalog.SpellPackages.SingleOrDefault(x=>x.Id==spells.SelectedValue?.ToString())?.Description??"No added spells.");
        }else{basicMapping=new Label{Dock=DockStyle.Fill,AutoEllipsis=true};Field(right,1,"Mapping",basicMapping,"Adjust combat, perks and spells in Advanced.");right.SetRowSpan(basicMapping.Parent!,3);}
        Field(right,4,"Voice",voice,"Compatible follower voices for the selected sex.");Field(right,5,"",marriage);
        var disclaimer = new ReadableCheckBox { Text = "I understand that I am responsible for checking asset-sharing permissions.", AutoSize = false, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 9.5F) };
        var build = PrimaryButton("Build follower");
        build.Click += async (_, _) => await StartBuildAsync(index);
        _buildButtons[index] = build;
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Padding=new Padding(6)};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150));footer.Controls.Add(disclaimer,0,0);build.Dock=DockStyle.Fill;footer.Controls.Add(build,1,0);tabLayout.Controls.Add(footer,0,2);
        disclaimer.AccessibleDescription="You are responsible for checking permission to share every selected asset.";
        var controls = new SettingsControls(page, name, character, scale, min, max, mode, multiplier, klass, voice, marriage, combat, perks, spells, disclaimer, build, advanced, basicMapping, prebuilt, obody, outputs, outputHelp, raceMode);
        void OutputChanged()
        {
            if (_syncing || _operationActive) return;
            _outputs = new(prebuilt.Checked, obody.Checked && _obodyAvailable);
            UpdateOutputHelp(prebuilt, obody, outputs);
            ResetResult(); RefreshSettingsControls();
        }
        prebuilt.CheckedChanged += (_, _) => OutputChanged();
        obody.CheckedChanged += (_, _) => OutputChanged();
        UpdateOutputHelp(prebuilt, obody, outputs);
        Wire(controls, index);
        return controls;
    }

    private void Wire(SettingsControls c, int index)
    {
        c.ModName.TextChanged += (_, _) => Changed(index);
        c.CharacterName.TextChanged += (_, _) => Changed(index);
        c.Scale.ValueChanged += (_, _) => Changed(index);
        c.Minimum.ValueChanged += (_, _) => Changed(index);
        c.Maximum.TextChanged += (_, _) => Changed(index);
        c.LevelMode.SelectedValueChanged += (_, _) =>
        {
            if (_syncing || _settings == null) return;
            string mode = c.LevelMode.SelectedValue?.ToString() ?? "player-relative";
            _numericError = "";
            if (mode == "fixed") { _relativeMaximum = _settings.MaximumLevel; _relativeMultiplier = _settings.LevelMultiplier; }
            _settings = _settings with { LevelMode = mode, MaximumLevel = mode == "fixed" ? _settings.MinimumLevel : _relativeMaximum, LevelMultiplier = mode == "fixed" ? 1 : _relativeMultiplier };
            RefreshSettingsControls();
        };
        c.Multiplier.ValueChanged += (_, _) => Changed(index);
        c.Voice.SelectedValueChanged += (_, _) => Changed(index);
        c.Marriage.CheckedChanged += (_, _) => Changed(index);
        c.Disclaimer.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            _acknowledged = c.Disclaimer.Checked;
            _syncing = true; foreach (var item in _copies) if (item != c) item.Disclaimer.Checked = _acknowledged; _syncing = false;
            UpdateBuildGate();
        };
        c.Class.SelectedValueChanged += (_, _) =>
        {
            if (_syncing || _settings == null) return;
            ReadControls(index);
            try { _settings = FollowerSettingsPolicy.SelectClass(_settings, c.Class.SelectedValue?.ToString() ?? "warrior"); _settingsError.Text = ""; }
            catch (Exception e) { _settingsError.Text = FriendlyError(e); }
            RefreshSettingsControls();
        };
        if (c.Advanced)
        {
            c.Combat.SelectedValueChanged += (_, _) => Changed(index);
            c.Perks.SelectedValueChanged += (_, _) => Changed(index);
            c.Spells.SelectedValueChanged += (_, _) => Changed(index);
        }
    }

    private void Changed(int index)
    {
        if (_syncing) return;
        ReadControls(index);
        UpdateBuildGate();
    }

    private void ReadControls(int index)
    {
        if (_settings == null || index < 0 || index >= _copies.Count) return;
        var c = _copies[index];
        try
        {
            _settings = _settings with
            {
                ModName = c.ModName.Text,
                CharacterName = c.CharacterName.Text,
                Scale = (double)c.Scale.Value,
                MinimumLevel = decimal.ToInt32(c.Minimum.Value),
                MaximumLevel = ParseMaximum(c.Maximum.Text),
                LevelMode = c.LevelMode.SelectedValue?.ToString() ?? "player-relative",
                LevelMultiplier = (double)c.Multiplier.Value,
                ClassId = c.Class.SelectedValue?.ToString() ?? _settings.ClassId,
                VoiceId = c.Voice.SelectedValue?.ToString() ?? _settings.VoiceId,
                MarriageEligible = c.Marriage.Checked
            };
            if (_settings.LevelMode == "fixed")
                _settings = _settings with { MaximumLevel = _settings.MinimumLevel, LevelMultiplier = 1 };
            else { _relativeMaximum = _settings.MaximumLevel; _relativeMultiplier = _settings.LevelMultiplier; }
            if (c.Advanced)
                _settings = _settings with { CombatStyleId = c.Combat.SelectedValue?.ToString() ?? _settings.CombatStyleId, PerkPackageId = c.Perks.SelectedValue?.ToString() ?? _settings.PerkPackageId, SpellPackageId = c.Spells.SelectedValue?.ToString() ?? _settings.SpellPackageId };
            else
            {
                // Basic and Advanced present one value; when a Basic edit occurs, preserve existing Advanced package selections.
                foreach (var advanced in _copies.Where(x => x.Advanced))
                {
                    _settings = _settings with { CombatStyleId = advanced.Combat.SelectedValue?.ToString() ?? _settings.CombatStyleId, PerkPackageId = advanced.Perks.SelectedValue?.ToString() ?? _settings.PerkPackageId, SpellPackageId = advanced.Spells.SelectedValue?.ToString() ?? _settings.SpellPackageId };
                }
            }
        }
        catch (Exception e) { _numericError = FriendlyError(e); UpdateBuildGate(); return; }
        _numericError = "";
        RefreshSettingsControls();
    }

    private void RefreshSettingsControls()
    {
        if (_settings == null || _copies.Count == 0) return;
        _syncing = true;
        foreach (var c in _copies)
        {
            c.ModName.Text = _settings.ModName ?? "";
            c.CharacterName.Text = _settings.CharacterName ?? "";
            SetNumeric(c.Scale, _settings.Scale ?? 1);
            SetNumeric(c.Minimum, _settings.MinimumLevel);
            c.Maximum.Text = _settings.MaximumLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            c.Maximum.Enabled = _settings.LevelMode == "player-relative";
            c.Multiplier.Enabled = _settings.LevelMode == "player-relative";
            c.LevelMode.SelectedValue = _settings.LevelMode;
            SetNumeric(c.Multiplier, _settings.LevelMultiplier);
            c.Class.SelectedValue = _settings.ClassId;
            var actor = _preview?.SavePreview.SavedState.Actor;
            var voices = actor == null ? [] : _preview!.SavePreview.Context.Catalog.Voices.Where(x => x.Sex == actor.Sex).Select(x => (x.Id, x.Label)).ToArray();
            SetOptions(c.Voice, voices);
            if (voices.Any(x => x.Id.Equals(_settings.VoiceId, StringComparison.Ordinal))) c.Voice.SelectedValue = _settings.VoiceId;
            else c.Voice.SelectedIndex = -1;
            c.Marriage.Checked = _settings.MarriageEligible;
            bool confirmedAsIsRoute = _setup != null && (!_obodyAvailable || _preview?.Cosave.ObodyAssignment == "unassigned");
            c.Prebuilt.Text = confirmedAsIsRoute ? "As-is (no BodySlide)" : "BodySlide build";
            c.Prebuilt.AccessibleName = c.Prebuilt.Text;
            string prebuiltHelp = confirmedAsIsRoute
                ? "Uses the installed body and outfit meshes without running BodySlide."
                : "Builds affected body and outfit meshes with the saved preset in BodySlide, so the shape works without OBody NG. Unaffected meshes stay as installed.";
            _tips.SetToolTip(c.Prebuilt, prebuiltHelp);
            c.Prebuilt.AccessibleDescription = prebuiltHelp;
            c.Obody.AccessibleName = "OBody NG (as-is)";
            c.Obody.Text = "OBody NG (as-is)";
            string obodyHelp = ObodyOutputHelp();
            _tips.SetToolTip(c.Obody, obodyHelp);
            c.Obody.AccessibleDescription = obodyHelp;
            c.Prebuilt.Checked = _outputs.Prebuilt;
            c.Obody.Enabled = _obodyAvailable && !_operationActive;
            c.Obody.Checked = _outputs.Obody && _obodyAvailable;
            c.Prebuilt.Enabled = !_operationActive;
            UpdateOutputHelp(c.Prebuilt, c.Obody, c.Outputs);
            c.OutputHelp.Text = _setup == null ? "Environment unresolved. Choose the installed Skyrim folder on Inputs." : (_obodyAvailable ? (_preview?.Cosave.ObodyAssignment=="unassigned"?"This save has no OBody assignment. Installed body and outfits stay as-is.":"OBody is present in the selected Data view.") : "OBody is absent from the confirmed environment. Installed body and outfits stay as-is.");
            if(_preview?.MaterialConflicts is {Length:>0}){
                c.OutputHelp.Text="Mouth: finished export used; saved specular override omitted.";
                c.OutputHelp.ForeColor=Color.FromArgb(237,198,139);
                _tips.SetToolTip(c.OutputHelp,string.Join(Environment.NewLine,_preview.MaterialConflicts.Select(x=>x.Warning)));
            }else if(_preview?.RuntimeAssignmentWarnings is {Length:>0}){
                c.OutputHelp.Text=_preview.Cosave.Skee.Nodes.Any(LeanRuntimeAssignmentPolicy.CachedHeelOffset)
                    ? "Saved heel lift discarded; footwear applies it at runtime."
                    : "Saved XPMSE weapon scale omitted.";
                c.OutputHelp.ForeColor=Color.FromArgb(237,198,139);
                _tips.SetToolTip(c.OutputHelp,string.Join(Environment.NewLine,_preview.RuntimeAssignmentWarnings));
            }else{c.OutputHelp.ForeColor=DesktopTheme.Muted;_tips.SetToolTip(c.OutputHelp,c.OutputHelp.Text);}
            if (c.Advanced)
            {
                c.RaceMode!.SelectedValue = _settings.RaceMode;
                c.Combat.SelectedValue = _settings.CombatStyleId;
                c.Perks.SelectedValue = _settings.PerkPackageId;
                c.Spells.SelectedValue = _settings.SpellPackageId;
            }
            else if (c.BasicMapping != null)
            {
                var selectedClass = FollowerSettingsPolicy.Catalog.Classes.Single(x => x.Id == _settings.ClassId);
                var combat = FollowerSettingsPolicy.Catalog.CombatStyles.Single(x => x.Id == _settings.CombatStyleId).Label;
                var perk = FollowerSettingsPolicy.Catalog.PerkPackages.Single(x => x.Id == _settings.PerkPackageId).Label;
                var spell = FollowerSettingsPolicy.Catalog.SpellPackages.Single(x => x.Id == _settings.SpellPackageId).Label;
                c.BasicMapping.AccessibleDescription = _settings.RaceMode=="existing" ? "Uses the captured installed race; no skeleton packaged." : "Generates a private captured race.";
                c.BasicMapping.Text = $"{selectedClass.Label} · {combat} · {perk} · {spell} · {(_settings.RaceMode=="existing"?"Existing race; no skeleton":"New custom race")}";
            }
            c.Disclaimer.Checked = _acknowledged;
        }
        _syncing = false;
        UpdateBuildGate();
    }

    private void UpdateBuildGate()
    {
        bool valid = false;
        string message = "";
        if (_preview == null || _settings == null) message = "Return to Inputs and continue before building.";
        else if (!_preview.SavePreview.Context.Catalog.Voices.Any(x => x.Sex == _preview.SavePreview.SavedState.Actor.Sex))
        {
            valid = false;
            message = "No compatible follower voice choices are available for this selected character in the current build path. No substitute was selected.";
        }
        else
        {
            try { FollowerSettingsPolicy.Resolve(_settings, _preview.SavePreview.SavedState.Actor); valid = true; }
            catch (Exception e) { message = FriendlyError(e); }
        }
        if (_numericError.Length > 0) { valid = false; message = _numericError; }
        if(_setup==null){valid=false;message="Choose the Skyrim folder on Inputs before building.";}
        try { LeanOutputPolicy.Resolve(_outputs, _obodyAvailable);if(_preview!=null)LeanOutputPolicy.CapturedObody(_outputs,_obodyAvailable,_preview.Cosave); }
        catch (InvalidDataException e) { valid = false; message = e.Message; }
        if (_operationActive) { valid = false; message = "A build is running. Wait for it to finish or cancel it before editing settings."; }
        _settingsError.Text = string.Join("\n", new[] { _equipmentError, valid ? "" : message }.Where(x => x.Length > 0));
        _equipmentDetails.Visible = _equipmentError.Length > 0 && _equipmentReport != null;
        foreach (var button in _buildButtons) if (button != null)
        {
            bool enabled = valid && _acknowledged && !_operationActive;
            button.Enabled = enabled;
            button.BackColor = enabled ? Color.FromArgb(37, 99, 235) : Color.FromArgb(75, 79, 87);
            button.ForeColor = DesktopTheme.Text;
        }
    }

    private async Task StartBuildAsync(int tabIndex)
    {
        if (_operationActive || _preparing || !_acknowledged || _preview == null || _settings == null || _setup == null) return;
        ReadControls(tabIndex);
        ResetResult();
        _readyPackagePath = "";
        _operationOutput = ""; _requestPath = "";
        UpdateOperationActions();
        LeanBuildRequest request;
        ApplicationTools tools;
        try
        {
            FollowerSettingsPolicy.Resolve(_settings, _preview.SavePreview.SavedState.Actor); _setup.Revalidate();
            bool available = BuildInputs.ObodyAvailable(_setup);
            if (available != _obodyAvailable)
            {
                _obodyAvailable = available;
                if (!available) _outputs = _outputs with { Obody = false };
                RefreshSettingsControls(); throw new InvalidDataException("OBody availability changed. Review the output choices and start a fresh build.");
            }
            LeanOutputPolicy.Resolve(_outputs, available);LeanOutputPolicy.CapturedObody(_outputs,available,_preview.Cosave); if (!EnsureTools()) return; BuildInputs.Preflight();
            tools = ApplicationTools.Resolve();
            string root = BuildDestinations.DefaultRoot();
            _operationOutput = Path.Combine(root, "build-" + Guid.NewGuid().ToString("N"));
            _requestPath = Path.Combine(root, "requests", "request-" + Guid.NewGuid().ToString("N") + ".json");
            ReconcileEquipment(_preview);
            request=_setup.Request(_preview,tools,_settings,_outputs,_obodyAvailable,root) with{ExcludedEquipment=_excludedEquipment};
            // Input decoding and normal environment admission are independent.
            // Check admission before allocating/writing any GUI request.
            var runtime = SkyrimRuntimeProfile.Require(request.Store, FileVersionInfo.GetVersionInfo(Path.Combine(_setup.Game,"SkyrimSE.exe")).FileVersion??"");
            if(runtime.Version!=request.InputRuntime)throw new InvalidDataException("Input profile changed; return to the landing screen and refresh inputs.");
            // Validate the exact retained paths now, and repeat with the selected
            // preset immediately before the first request directory/file write.
            BuildDestinations.RequireRequestPaths(_requestPath, _operationOutput, root, BuildDestinations.Protection(request, tools));
        }
        catch (Exception e) { _settingsError.Text = FriendlyError(e); _equipmentReport = FailureReport("Build follower", e); _equipmentDetails.Visible = true; return; }
        _operationCancel = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token);
        _operationActive = true;
        UpdateBuildGate();
        var preview = _preview;
        var setup = _setup;
        var token = _operationCancel.Token;
        _operationStatus.Text = "Starting a new build through the selected Data view…";
        _progress.Style = ProgressBarStyle.Marquee; _progress.MarqueeAnimationSpeed = 25;
        ShowPage(_operationPage);
        _workDialog = new WorkDialog("Building follower", _operationStatus.Text, () => _operationCancel?.Cancel()); _workDialog.Show(this);
        _progress.Visible = false; _progressTimer.Start();
        try
        {
            var presets = await Task.Run(() => BuildInputs.ResolvePresets(setup, preview, token), token);
            token.ThrowIfCancellationRequested();
            var preset = presets.Length == 1 ? presets[0] : presets.Length > 1 ? ChoosePreset(presets) : null;
            if (presets.Length > 1 && preset == null) throw new OperationCanceledException(token);
            setup.Revalidate();
            request = request with { PresetXml = preset?.Path, PresetSha256 = preset?.Sha256, PresetBacking = preset?.Backing };
            BuildDestinations.WriteRequestFiles(_requestPath, _operationOutput, request.OutputRoot!, BuildDestinations.Protection(request, tools),
                JsonSerializer.Serialize(request, AutoFollower.Wire.Options),
                JsonSerializer.Serialize(new { Environment = setup, Preset = preset, Writer = tools.Writer, WriterSha256 = Hashing.File(tools.Writer), InputPins = preview.Pins }, AutoFollower.Wire.Options), token);
            int exitCode = await Task.Run(() => LeanExportBuilder.Run(_requestPath, _operationOutput, token,
                chooseAssets: choices => ChooseAssets(choices, token), inputPreview: preview, chooseBodyProject: choice => ChooseBodyProject(choice, token), recoverEquipment: choice => RecoverEquipment(choice, token)), token);
            token.ThrowIfCancellationRequested();
            _progressTimer.Stop();
            await FinalizeBuildAsync(exitCode);
        }
        catch (OperationCanceledException)
        {
            _operationStatus.Text = "Build cancelled.";
            _deliveryPath = "";
        }
        catch (Exception e)
        {
            ResetResult();
            _operationStatus.Text = "Build stopped: " + FailureSummary(e);
            _buildFailureReport = FailureReport("Build follower", e); _buildFailureDetails.Visible = true;
        }
        finally
        {
            _progressTimer.Stop();
            _workDialog?.Complete(); _workDialog = null;
            if (File.Exists(Path.Combine(_operationOutput, "storage-owner.json")))
            {
                _buildFailureReport ??= FailureReport("Build follower", new InvalidDataException("Build diagnostic evidence"));
                _buildFailureDetails.Text = "Diagnostic report…"; _buildFailureDetails.Visible = true;
                bool pending = _readyPackagePath.Length > 0 && _deliveryPath.Length == 0;
                string completedStatus = _deliveryPath.Length > 0 ? "success" : token.IsCancellationRequested ? "cancelled" : "failed";
                var retention = await Task.Run(() => WorkingStorage.Finish(_operationOutput, pending, completedStatus));
                if (_operationStatus.Visible) _operationStatus.Text += "\r\n" + retention.Notice;
                else _technicalDetails.Text += "\r\n" + retention.Notice;
                if (!pending && retention.Reclaimed) _readyPackagePath = "";
                await Task.Run(() => WorkingStorage.Sweep(Path.GetDirectoryName(_operationOutput)!));
            }
            _progressTimer.Stop(); _operationActive = false; _operationCancel?.Dispose(); _operationCancel = null;
            _progress.Style = ProgressBarStyle.Blocks; _progress.Value = 100;
            UpdateBuildGate();
            UpdateOperationActions();
            if (_closePending) Close();
        }
    }

    private PresetInput? ChoosePreset(PresetInput[] presets)
    {
        using var dialog = new PresetSourceChoiceDialog(presets[0].Name, presets.Select(x => Path.GetFileName(x.Path) + " · " + x.Provider).ToArray());
        return dialog.ShowDialog(_workDialog ?? (Form)this) == DialogResult.OK ? presets[dialog.SelectedIndex] : null;
    }

    private void ResetResult()
    {
        _buildFailureReport = null; _buildFailureDetails.Visible = false;
        _deliveryPath = "";
        SetCompletionHeader(false);
        _operationStatus.Visible = true;
        _completionHeading.Visible = _completionSummary.Visible = _completionPath.Visible = _completionWarning.Visible = _detailsToggle.Visible = false;
        _detailsHost.Visible = _technicalDetails.Visible = false;
        _detailsToggle.Text = "Build details";
        UpdateOperationActions();
    }

    private LeanAssetSelection[]? ChooseAssets(LeanAssetChoice[] choices, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        AssetChoiceDialog? dialog = null;
        var result = (DialogResult)Invoke(new Func<DialogResult>(() =>
        {
            dialog = new AssetChoiceDialog(choices, _previousAssetSelections); DesktopTheme.Apply(dialog);
            DesktopTheme.Apply(dialog);
        return dialog.ShowDialog(_workDialog ?? (Form)this);
        }));
        if (dialog?.CancelBuild == true) _operationCancel?.Cancel();
        if (result != DialogResult.OK) { _operationCancel?.Cancel(); return null; }
        if (token.IsCancellationRequested) return null;
        var selectedIds = dialog!.Selections.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _previousAssetSelections = (_previousAssetSelections ?? []).Where(x => !selectedIds.Contains(x.Id)).Concat(dialog.Selections).ToArray();
        return dialog.Selections;
    }

    private bool RecoverEquipment(LeanEquipmentRecoveryChoice choice,CancellationToken token)
    {
        if(token.IsCancellationRequested)return false;
        return (bool)Invoke(new Func<bool>(()=>{using var dialog=new EquipmentRecoveryDialog(choice);return dialog.ShowDialog(_workDialog??(Form)this)==DialogResult.OK;}));
    }

    private LeanBodyProjectSelection? ChooseBodyProject(LeanBodyProjectChoice choice, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        BodyProjectChoiceDialog? dialog = null;
        var result = (DialogResult)Invoke(new Func<DialogResult>(() =>
        {
            dialog = new BodyProjectChoiceDialog(choice); DesktopTheme.Apply(dialog);
            DesktopTheme.Apply(dialog);
        return dialog.ShowDialog(_workDialog ?? (Form)this);
        }));
        if (result != DialogResult.OK || token.IsCancellationRequested) { _operationCancel?.Cancel(); return null; }
        return dialog!.Selection;
    }

    private async Task FinalizeBuildAsync(int exitCode)
    {
        ResetResult();
        string resultPath = Path.Combine(_operationOutput, "result.json");
        if (!File.Exists(resultPath)) { _operationStatus.Text = $"Build ended with code {exitCode}. No result file was written; output kept at {_operationOutput}."; return; }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
        var root = document.RootElement;
        string status = root.TryGetProperty("status", out var state) ? state.GetString() ?? "unknown" : "unknown";
        if (exitCode == 0 && BestEffortBuild.DeliveredStatus(status) && root.TryGetProperty("distribution", out var distribution) && distribution.TryGetProperty("path", out var zipElement))
        {
            string sourceZip = Path.GetFullPath(zipElement.GetString() ?? "");
            SafePaths.NoLinks(sourceZip);
            if (!sourceZip.StartsWith(Path.GetFullPath(_operationOutput) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("The reported package is outside this attempt's owned output.");
            if (!File.Exists(sourceZip)) { _operationStatus.Text = "The build passed its structural checks, but the completed package was not found at the reported path."; return; }
            if (distribution.TryGetProperty("sha256", out var digest) && !Hashing.File(sourceZip).Equals(digest.GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("The completed package changed after validation.");
            _operationCancel?.Token.ThrowIfCancellationRequested();
            _readyPackagePath = sourceZip;
            try { _deliveryPath = DeliverZip(sourceZip); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                _operationStatus.Text = "Build finished, but saving the ZIP did not complete: " + FriendlyError(e) + "\r\nUse Retry saving ZIP to choose a folder and save this finished build.";
                UpdateOperationActions(); return;
            }
            string destinationText = "";
            if (root.TryGetProperty("placement", out var placementResult) && placementResult.ValueKind == JsonValueKind.Object)
            {
                destinationText = "\r\nSaved destination: " + (placementResult.TryGetProperty("displayLabel", out var label) ? label.GetString() : placementResult.Deserialize<LeanPlacement>(AutoFollower.Wire.Options)?.DisplayLabel ?? "saved location") + ".";
            }
            string levelText = _settings?.LevelMode == "fixed" ? $"Fixed level: {_settings.MinimumLevel}." : $"Player level × {_settings?.LevelMultiplier}; minimum {_settings?.MinimumLevel}; maximum {_settings?.MaximumLevel?.ToString() ?? "no user-set cap"}.";
            string assignmentWarnings=root.TryGetProperty("runtimeAssignmentWarnings",out var runtimeWarnings)?string.Join("\r\n",runtimeWarnings.EnumerateArray().Select(x=>x.GetString())):"";
            int omittedCount=root.TryGetProperty("equipmentOmissions",out var equipmentOmissions)?equipmentOmissions.GetArrayLength():0;
            string missingWarnings=root.TryGetProperty("missingReferences",out var missingReferences)?string.Join("\r\n",missingReferences.EnumerateArray().Select(x=>x.GetProperty("warning").GetString())):"";
            string equipmentWarnings=omittedCount>0?string.Join("\r\n",equipmentOmissions.EnumerateArray().Select(x=>x.GetProperty("warning").GetString())):"";
            string repairWarnings=root.TryGetProperty("assetRepairWarnings",out var repairs)?string.Join("\r\n",repairs.EnumerateArray().Select(x=>x.GetString())):"";
            string materialWarnings=root.TryGetProperty("materialConflicts",out var materialConflicts)?string.Join("\r\n",materialConflicts.EnumerateArray().Select(x=>x.GetProperty("warning").GetString())):"";
            string qualityWarnings=root.TryGetProperty("buildWarnings",out var buildWarnings)?string.Join("\r\n",buildWarnings.EnumerateArray().Select(x=>x.GetProperty("warning").GetString()).Distinct()):"";
            _operationStatus.Visible = false;
            SetCompletionHeader(true);
            _completionHeading.Visible = _completionSummary.Visible = _completionPath.Visible = _detailsToggle.Visible = true;
            if(omittedCount>0||missingWarnings.Length>0||qualityWarnings.Length>0)_completionHeading.Text="Built with warnings";
            _completionPath.Text = _deliveryPath;
            _completionPath.AccessibleDescription = _deliveryPath;
            var warnings = new List<string> { LeanOverlayPolicy.Warning };
            if (!string.IsNullOrWhiteSpace(materialWarnings)) warnings.Add("Some material settings were kept from the selected export; see Build details.");
            if (!string.IsNullOrWhiteSpace(assignmentWarnings)) warnings.Add("Some optional runtime assignments were omitted; see Build details.");
            if (!string.IsNullOrWhiteSpace(repairWarnings)) warnings.Add("Creator texture paths repaired; see Build details.");
            if(omittedCount>0)warnings.Insert(0,$"{omittedCount} equipment items excluded; see Build details.");
            if(missingWarnings.Length>0)warnings.Insert(0,"Some called assets are unavailable. Items retained with original references; see Build details.");
            if(qualityWarnings.Length>0)warnings.Insert(0,"Some output checks failed or requested transformations were unavailable. The follower may not work in Skyrim; see Build details.");
            _completionWarning.Text = string.Join(" ", warnings);
            _completionWarning.Visible = true;
            string phases = root.TryGetProperty("phases", out var phaseArray) && phaseArray.ValueKind == JsonValueKind.Array
                ? string.Join(Environment.NewLine, phaseArray.EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()}: {x.GetProperty("milliseconds").GetInt64()} ms")) : "Not recorded.";
            string omissions = root.TryGetProperty("runtimeAssignmentOmission", out var omissionValue) ? JsonSerializer.Serialize(omissionValue, new JsonSerializerOptions { WriteIndented = true }) : "None recorded.";
            string sourceFilesText = root.TryGetProperty("sourceFilesRead", out var sourceFiles) ? sourceFiles.ToString() : "not recorded";
            string sourceBytesText = root.TryGetProperty("sourceBytesRead", out var sourceBytes) ? sourceBytes.ToString() : "not recorded";
            string elapsedText = root.TryGetProperty("totalMilliseconds", out var elapsed) ? elapsed.ToString() : "not recorded";
            string metrics = $"Source files read: {sourceFilesText}; source bytes read: {sourceBytesText}; total: {elapsedText} ms.";
            string assessmentText=qualityWarnings.Length>0?"Built with warnings. Failed assessments remain failed; the available follower is delivered.":"Structural checks passed.";
            _technicalDetails.Text = $"{assessmentText}\r\nOutput warnings:\r\n{qualityWarnings} In-game behavior has not been tested.{destinationText}\r\n{levelText}\r\n\r\n{LeanOverlayPolicy.Warning}\r\nMaterial notes:\r\n{materialWarnings}\r\nRuntime assignment warnings:\r\n{assignmentWarnings}\r\nMissing assets kept as references:\r\n{missingWarnings}\r\nEquipment omissions:\r\n{equipmentWarnings}\r\nAsset repairs:\r\n{repairWarnings}\r\nRuntime assignment omissions:\r\n{omissions}\r\n\r\n{SkyrimRuntimeProfile.ForInputs(_preview?.Selection.Store??"unspecified",_preview?.Selection.InputRuntime??"").Notice}\r\nBuild measurements: {metrics}\r\nBuild stages:\r\n{phases}\r\nDetailed result: {resultPath}";
        }
        else
        {
            string error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() ?? "" : "";
            if (error.Length == 0 && root.TryGetProperty("gaps", out var gaps) && gaps.ValueKind == JsonValueKind.Array)
                error = string.Join(Environment.NewLine, gaps.EnumerateArray().Select(x => x.TryGetProperty("evidence", out var e) ? (x.TryGetProperty("input",out var input)?input.GetString()+": ":"")+e.GetString()+(x.TryGetProperty("nextAction",out var action)?". "+action.GetString():"") : null).Where(x => !string.IsNullOrWhiteSpace(x)).Take(5));
            _operationStatus.Text = status switch
            {
                "cancelled" => "Build cancelled. Its output has been kept for review.",
                "unsupported-required-input" => "This selected setup is not supported yet.\r\n" + error,
                _ => $"Build did not complete ({status}).\r\n{error}\r\nOutput kept at {_operationOutput}"
            };
            if (status != "cancelled")
            {
                _buildFailureReport = FailureReport("Build follower", new InvalidDataException(error), root);
                _buildFailureDetails.Visible = true;
            }
        }
    }

    private void CheckDeliveryTarget(string source, string destination)
    {
        string full = Path.GetFullPath(destination);
        SafePaths.NoLinks(full);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("That ZIP filename already exists. Retry to save with a new filename; existing files are preserved.");
        foreach(var p in _paths.Values.Append(source).Where(x=>!string.IsNullOrWhiteSpace(x)))if(Path.GetFullPath(p).Equals(full,StringComparison.OrdinalIgnoreCase))throw new IOException("Package destination conflicts with a selected input.");
        string[] directories=_setup!.Mo2 is {} active?SelectedEnvironment.ProtectedDirectories(ActiveMo2.ParseIni(File.ReadAllText(active.InstanceIni)),active.Game,active.InstanceIni):[_setup.Game,_setup.Physical!.IniDirectory,_setup.Physical.PluginDirectory];
        foreach(var directory in directories.Concat(new[] { AppContext.BaseDirectory, BuildDestinations.DefaultRoot(), Path.Combine(Path.GetTempPath(), "Lazybones Follower Slopper") }).Concat(_paths.Values.Where(x=>x.Length>0).Select(x=>Path.GetDirectoryName(Path.GetFullPath(x))!)))if(full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Choose a ZIP folder outside input, game, configuration and working folders.");
        if (Path.GetFullPath(source).Equals(full, StringComparison.OrdinalIgnoreCase)) throw new IOException("Package destination conflicts with the completed build artifact.");
    }

    private string DeliverZip(string source)
    {
        string destination = Path.Combine(_outputFolder, Path.GetFileName(source));
        if (File.Exists(destination) || Directory.Exists(destination)) destination = Path.Combine(_outputFolder, Path.GetFileNameWithoutExtension(source) + "-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        CheckDeliveryTarget(source, destination);
        _operationCancel?.Token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_outputFolder);
        bool created = false;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true; byte[] buffer = new byte[1024 * 1024]; int count;
                while ((count = input.Read(buffer)) != 0) { _operationCancel?.Token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            }
            _operationCancel?.Token.ThrowIfCancellationRequested();
            if (Hashing.File(destination) != Hashing.File(source)) throw new IOException("The saved ZIP did not match the validated build.");
            WorkingStorage.Delivered(_operationOutput, source, destination);
            return destination;
        }
        catch { if (created) File.Delete(destination); throw; }
    }

    private void RefreshDeliveryFolder()
    {
        _deliveryFolderLabel.Text = _outputFolder;
        _deliveryFolderLabel.AccessibleDescription = _outputFolder; _tips.SetToolTip(_deliveryFolderLabel, _outputFolder);
    }

    private bool PickOutputFolder()
    {
        if (_operationActive || _preparing) return false;
        using var picker = new FolderBrowserDialog { Description = "Save the finished install ZIP to this folder", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (_outputFolder.Length > 0) picker.SelectedPath = _outputFolder;
        if (picker.ShowDialog(_workDialog ?? (Form)this) != DialogResult.OK) return false;
        _outputFolder = Path.GetFullPath(picker.SelectedPath);
        RefreshDeliveryFolder();
        return true;
    }

    private void BuildOperationPage()
    {
        var root = PageRoot(); _operationRoot = root; _operationPage.Controls.Add(root);
        root.RowCount = 4; root.RowStyles.Clear();
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 93)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        _operationHeader = Header("Follower build", "Build status and diagnostic reports appear here. A ZIP is saved after a successful build.");
        root.Controls.Add(_operationHeader, 0, 0);
        _operationStatus.Dock = DockStyle.Top; _operationStatus.AutoSize = true; _operationStatus.TextAlign = ContentAlignment.TopLeft; _operationStatus.AutoEllipsis = false;
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 7, Padding = new Padding(0, 8, 0, 0) };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(_operationStatus, 0, 0); content.Controls.Add(_completionHeading, 0, 1); content.Controls.Add(_completionPath, 0, 2);
        content.Controls.Add(_completionWarning, 0, 3); content.Controls.Add(_completionSummary, 0, 4); content.Controls.Add(_detailsHost, 0, 5);
        _detailsHost.Controls.Add(_technicalDetails);
        content.Controls.Add(_detailsToggle, 0, 6);
        _detailsToggle.Click += (_, _) => { _detailsHost.Visible = !_detailsHost.Visible; _technicalDetails.Visible = _detailsHost.Visible; content.RowStyles[5].Height = _detailsHost.Visible ? 130 : 0; content.PerformLayout(); _detailsToggle.Text = _detailsHost.Visible ? "Hide build details" : "Build details"; };
        viewport.Controls.Add(content);
        viewport.SizeChanged+=(_,_)=>{int width=Math.Max(180,viewport.ClientSize.Width-24);_operationStatus.MaximumSize=new Size(width,0);_completionWarning.MaximumSize=new Size(width,0);_completionSummary.MaximumSize=new Size(width,0);content.PerformLayout();};
        root.Controls.Add(viewport, 0, 1);
        _progress.Dock = DockStyle.Fill;
        root.Controls.Add(_progress, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        _cancelBuild.Click += (_, _) => { _operationCancel?.Cancel(); _operationStatus.Text = "Cancelling this build…"; };
        _showZip.Click += (_, _) => { try { Process.Start(ZipExplorerStart(_deliveryPath)); } catch (Exception e) { _operationStatus.Text = "Could not show ZIP: " + FriendlyError(e); _operationStatus.Visible = true; } };
        _editFollower.Click += (_, _) => ShowSettings();
        _anotherCharacter.Click += (_, _) => ShowPage(_landing);
        _retryDelivery.Click += async (_, _) =>
        {
            if (_operationActive || !PickOutputFolder() || _readyPackagePath.Length == 0) return;
            _operationActive = true; UpdateOperationActions();
            try
            {
                await FinalizeBuildAsync(0);
                if (_deliveryPath.Length > 0)
                {
                    var retention = await Task.Run(() => WorkingStorage.Finish(_operationOutput, false, "success"));
                    _technicalDetails.Text += "\r\n" + retention.Notice;
                    if (retention.Reclaimed) _readyPackagePath = "";
                    _buildFailureReport = FailureReport("Build follower", new InvalidDataException("Build diagnostic evidence"));
                    _buildFailureDetails.Text = "Diagnostic report…"; _buildFailureDetails.Visible = true;
                }
            }
            catch (Exception e) { _operationStatus.Text = "Saving the finished ZIP stopped: " + FriendlyError(e); _operationStatus.Visible = true; }
            finally { _operationActive = false; UpdateOperationActions(); if (_closePending) Close(); }
        };
        actions.Controls.Add(_cancelBuild); actions.Controls.Add(_showZip); actions.Controls.Add(_retryDelivery); actions.Controls.Add(_editFollower); actions.Controls.Add(_anotherCharacter); _exit.Click += (_, _) => Close(); actions.Controls.Add(_exit);
        _buildFailureDetails.Click += (_, _) => ShowFailureReport(_buildFailureReport); actions.Controls.Add(_buildFailureDetails);
        root.Controls.Add(actions, 0, 3);
    }

    private void UpdateOperationActions()
    {
        _cancelBuild.Visible = _operationActive; _cancelBuild.Enabled = _operationActive;
        _showZip.Visible = !_operationActive && File.Exists(_deliveryPath); _showZip.Enabled = _showZip.Visible;
        _retryDelivery.Visible = !_operationActive && _deliveryPath.Length == 0 && File.Exists(_readyPackagePath); _retryDelivery.Enabled = _retryDelivery.Visible;
        _editFollower.Enabled = !_operationActive && _preview != null;
        _anotherCharacter.Enabled = !_operationActive; _exit.Visible = !_operationActive; _exit.Enabled = !_operationActive;
    }

    private void SetCompletionHeader(bool completed)
    {
        if (_operationRoot == null || _operationHeader == null) return;
        _operationHeader.Visible = !completed;
        _operationRoot.RowStyles[0].Height = completed ? 0 : 93;
    }

    private static ProcessStartInfo ZipExplorerStart(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The delivered ZIP is no longer in its saved folder.", path);
        return new ProcessStartInfo("explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"") { UseShellExecute = true };
    }

    private void RefreshProgress()
    {
        if (!_operationActive || !_progressTimer.Enabled || _operationOutput.Length == 0) return;
        var phases = new (string File, string Text)[]
        {
            ("result.json", "Finalizing the package and checking the generated files…"),
            ("package-stage-process.json", "Building the plugin and installer package…"),
            ("head-stage-process.json", "Preparing the selected head appearance…"),
            ("dependency-preview.json", "Checking the choices and preparing the selected environment…"),
            ("input-plan.json", "Reading the selected save, exports and selected Data view…")
        };
        var phase = phases.FirstOrDefault(x => File.Exists(Path.Combine(_operationOutput, x.File)));
        _operationStatus.Text = string.IsNullOrWhiteSpace(phase.Text) ? "Reading the selected save, exports and selected Data view…" : phase.Text;
        _workDialog?.SetStatus(_operationStatus.Text);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if(e.KeyCode==Keys.F1){var focused=Descendants(this).FirstOrDefault(x=>x.Focused);if(focused!=null){string help=HelpFor(focused);if(help.Length>0)_tips.Show(help,focused,0,focused.Height,10000);}e.Handled=true;return;}
        if ((e.Control && e.KeyCode == Keys.Enter) || e.KeyCode == Keys.F6)
        {
            e.Handled = true; e.SuppressKeyPress = true;
            int index = _tabs.SelectedIndex;
            if (_settingsPage.Visible && !_preparing && !_operationActive && _acknowledged && _buildButtons.ElementAtOrDefault(index)?.Enabled == true) _ = StartBuildAsync(index);
        }
    }

    private string HelpFor(Control? control){for(var c=control;c!=null;c=c.Parent){string help=_tips.GetToolTip(c)??"";if(help.Length==0)help=c.AccessibleDescription??"";if(help.Length>0)return help;}return "";}

    private void ShowSettings()
    {
        if (_preview == null || _settings == null) { ShowPage(_landing); return; }
        _settingsSummary.Text=_preview.SavePreview.SavedState.Actor.Name+" · Selected save";_tips.SetToolTip(_settingsSummary,_preview.Selection.Save);
        RefreshSettingsControls(); ShowPage(_settingsPage);
    }

    private void SetLandingBusy(bool busy)
    {
        _preparing = busy;
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
        foreach (var button in Descendants(_landing).OfType<Button>()) button.Enabled = !busy;
        _prepareCancelButton.Visible = busy; _prepareCancelButton.Enabled = busy;
        if (busy) _landingError.Text = "Resolving and checking the required files…";
    }

    private void ShowPage(Control page)
    {
        _landing.Visible = page == _landing; _settingsPage.Visible = page == _settingsPage; _operationPage.Visible = page == _operationPage;
        page.BringToFront(); UpdateOperationActions();
    }

    private static TableLayoutPanel PageRoot()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(20, 14, 20, 14), BackColor = DesktopTheme.Background };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        return root;
    }

    private static Control Header(string title, string subtitle)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 15F), ForeColor = DesktopTheme.Text, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        panel.Controls.Add(new Label { Text = subtitle, Dock = DockStyle.Fill, ForeColor = DesktopTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        return panel;
    }

    private static void AddField(TableLayoutPanel table, string label, Control control, string? help = null)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = help == null ? 1 : 2, Height = help == null ? 34 : 55, Margin = new Padding(0, 2, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, help == null ? 100 : 63));
        if (help != null) row.RowStyles.Add(new RowStyle(SizeType.Percent, 37));
        row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = DesktopTheme.Text }, 0, 0);
        control.Dock = DockStyle.None;
        control.Anchor = AnchorStyles.Left;
        control.Width = control is NumericUpDown or TextBox ? 260 : control is ComboBox ? 390 : control is CheckBox ? 420 : control is Label ? 540 : control.Width;
        if (control.Height < 28 && control is TextBox or ComboBox or NumericUpDown) control.Height = 30;
        row.Controls.Add(control, 1, 0);
        if (help != null) row.Controls.Add(new Label { Text = help, Dock = DockStyle.Fill, ForeColor = DesktopTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 1, 1);
        table.Controls.Add(row);
        table.RowCount++;
    }

    private static NumericUpDown Numeric(decimal min, decimal max, int decimals) => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : 0.1m, ThousandsSeparator = false, Dock = DockStyle.Left, Width = 210 };
    private static ComboBox Combo((string Id, string Label)[] values)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Label", ValueMember = "Id", Width = 350 };
        combo.DataSource = values.Select(x => new OptionItem(x.Id, x.Label)).ToList();
        return combo;
    }
    private static void SetOptions(ComboBox combo, (string Id, string Label)[] values)
    {
        var current = combo.Items.Cast<OptionItem>().Select(x => x.Id).ToArray();
        if (current.SequenceEqual(values.Select(x => x.Id), StringComparer.Ordinal)) return;
        combo.DataSource = values.Select(x => new OptionItem(x.Id, x.Label)).ToList();
    }
    private static void SetNumeric(NumericUpDown control, double value)
    {
        if (!double.IsFinite(value)) value = 1;
        decimal n = (decimal)value;
        control.Value = Math.Clamp(n, control.Minimum, control.Maximum);
    }
    private static Button PrimaryButton(string text) => new ReadableButton { Text = text, Width = 145, Height = 29, BackColor = Color.FromArgb(37, 99, 235), ForeColor = DesktopTheme.Text, FlatStyle = FlatStyle.Flat };
    internal static int? ParseMaximum(string text) => string.IsNullOrWhiteSpace(text) ? null : int.TryParse(text, out int value) && value > 0 ? value : throw new InvalidDataException("Maximum level must be blank or a positive whole number.");
    private void ChooseEnvironment()
    {
        if(_preparing||_operationActive)return;
        try {
            if(LeanMo2Sources.HasContext){_setup=InstalledSetup.Discover(out string status);_mo2=_setup?.Mo2;_environmentLabel.Text=status;}
            else {
                using var picker=new FolderBrowserDialog{Description="Choose Skyrim's folder containing SkyrimSE.exe and Data",UseDescriptionForTitle=true};
                if(picker.ShowDialog(this)!=DialogResult.OK)return;
                try{_setup=InstalledSetup.Direct(picker.SelectedPath);}
                catch(FileNotFoundException e) when(Path.GetFileName(e.FileName) is "Skyrim.ini" or "plugins.txt"){
                    using var ini=new FolderBrowserDialog{Description="Choose the effective Skyrim INI folder",UseDescriptionForTitle=true,SelectedPath=PhysicalEnvironment.Default.IniDirectory};if(ini.ShowDialog(this)!=DialogResult.OK)return;
                    using var plugins=new FolderBrowserDialog{Description="Choose the active plugins.txt/loadorder.txt folder",UseDescriptionForTitle=true,SelectedPath=PhysicalEnvironment.Default.PluginDirectory};if(plugins.ShowDialog(this)!=DialogResult.OK)return;
                    _setup=InstalledSetup.Direct(picker.SelectedPath,new(ini.SelectedPath,plugins.SelectedPath));
                }
                _mo2=null;_environmentLabel.Text="Physical Data · "+_setup.Game;
            }
            ClearEnvironmentCompanions(); InvalidatePreparedInputs("Refresh inputs for this environment.");
        }catch(Exception e){_setup=null;_mo2=null;_environmentLabel.Text=FriendlyError(e);ClearEnvironmentCompanions();InvalidatePreparedInputs("Environment unresolved.");}
    }
    private void ClearEnvironmentCompanions() { _equipmentScope="";_excludedEquipment=[];_equipmentButton.Text="Choose items…"; _inputRevision++; _explicitJslot = false; foreach (string key in new[] { "Cosave", "TintDds", "Jslot" }) _paths[key] = ""; RefreshCompanionDetails(); }
    private bool EnsureTools(){try{ApplicationTools.Resolve().Require();return true;}catch(Exception e){_settingsError.Text=FriendlyError(e);return false;}}
    private static IEnumerable<Control> Descendants(Control parent)=>parent.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(Descendants(x)));
    private static string FailureSummary(Exception error)
    {
        if (error is AggregateException aggregate) return FailureSummary(aggregate.GetBaseException());
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
            if (cause.Data["InputFile"] is string path) return "Could not read " + Path.GetFileName(path) + ". See Error report.";
        return FriendlyError(error);
    }
    private static DiagnosticReport FailureReport(string operation, Exception error, JsonElement? stored = null)
        => DiagnosticReport.Create(operation, error, stored);
    private void ShowFailureReport(DiagnosticReport? report)
    {
        if (report == null) return;
        ShowDiagnosticDialog(report, ReferenceEquals(report, _buildFailureReport) && File.Exists(Path.Combine(_operationOutput, "storage-owner.json")) ? _operationOutput : null);
    }
    private void ShowDiagnosticDialog(DiagnosticReport report, string? attempt)
    {
        using var dialog = CreateFailureReportDialog(report);
        var save = (Button)dialog.Controls.Find("SaveDiagnosticZip", true).Single();
        var privateFiles = (CheckBox)dialog.Controls.Find("IncludePrivateDiagnostics", true).Single();
        var feedback = (Label)dialog.Controls.Find("DiagnosticFeedback", true).Single();
        save.Click += (_, _) =>
        {
            using var picker = new SaveFileDialog { Title = "Save diagnostic ZIP", Filter = "Diagnostic ZIP (*.zip)|*.zip", FileName = privateFiles.Checked ? "Lazybones-diagnostics-private.zip" : "Lazybones-diagnostics-anonymous.zip", OverwritePrompt = false, RestoreDirectory = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            try { DiagnosticArchive.Save(picker.FileName, report, privateFiles.Checked, attempt, CheckDiagnosticTarget); feedback.Text = privateFiles.Checked ? "Private diagnostic ZIP saved. Share privately." : "Anonymous diagnostic ZIP saved."; }
            catch (Exception) { feedback.Text = "Could not save diagnostic ZIP. Choose an unused filename outside protected folders and retry. No raw fallback was used."; }
        };
        dialog.ShowDialog(this);
    }
    private void CheckDiagnosticTarget(string destination)
    {
        SafePaths.NoLinks(destination);
        var dirs = _paths.Values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetDirectoryName(Path.GetFullPath(x))!).Concat(new[] { AppContext.BaseDirectory, BuildDestinations.DefaultRoot(), Path.Combine(Path.GetTempPath(), "Lazybones Follower Slopper") });
        if (_setup != null)
        {
            string[] protectedDirs = _setup.Mo2 is {} active ? SelectedEnvironment.ProtectedDirectories(ActiveMo2.ParseIni(File.ReadAllText(active.InstanceIni)), active.Game, active.InstanceIni) : [_setup.Game, _setup.Physical!.IniDirectory, _setup.Physical.PluginDirectory];
            dirs = dirs.Concat(protectedDirs);
        }
        foreach (string dir in dirs) if (WorkingStorage.Same(destination, dir) || destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a diagnostic ZIP folder outside input, application and working folders.");
    }
    private void ShowRecentDiagnostics()
    {
        if (_preparing || _operationActive) return;
        try
        {
            string[] recent = WorkingStorage.Recent(BuildDestinations.DefaultRoot());
            using var chooser = new Form { Text = "Recent diagnostics", Size = new Size(480, 330), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
            var list = new ListBox { Dock = DockStyle.Fill };
            foreach (string path in recent) { var state = WorkingStorage.GetState(path); list.Items.Add(state.UpdatedUtc.ToLocalTime().ToString("g") + " · " + state.Status); }
            var open = new ReadableButton { Text = "Open diagnostic report", Dock = DockStyle.Bottom, Height = 40, Enabled = recent.Length > 0 };
            open.Click += (_, _) => { if (list.SelectedIndex >= 0) chooser.DialogResult = DialogResult.OK; };
            var retry = new ReadableButton { Text = "Save finished ZIP…", Dock = DockStyle.Bottom, Height = 40, Enabled = false };
            list.SelectedIndexChanged += (_, _) => retry.Enabled = list.SelectedIndex >= 0 && list.SelectedIndex < recent.Length && WorkingStorage.GetState(recent[list.SelectedIndex]).PendingDelivery;
            retry.Click += async (_, _) =>
            {
                if (list.SelectedIndex < 0 || list.SelectedIndex >= recent.Length) return;
                string attempt = recent[list.SelectedIndex]; retry.Enabled = open.Enabled = list.Enabled = false;
                _operationActive = true; _operationCancel = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token);
                try { await RecoverPendingDeliveryAsync(attempt, chooser); chooser.DialogResult = DialogResult.Cancel; }
                catch (Exception) { _landingError.Text = "The finished ZIP could not be saved. It remains available in Recent diagnostics; choose an unused filename outside protected folders."; }
                finally { _operationActive = false; _operationCancel.Dispose(); _operationCancel = null; list.Enabled = open.Enabled = true; retry.Enabled = WorkingStorage.GetState(attempt).PendingDelivery; chooser.DialogResult = DialogResult.Cancel; if (_closePending) Close(); }
            };
            chooser.FormClosing += (_, e) => { if (_operationActive) e.Cancel = true; };
            chooser.Controls.Add(list); chooser.Controls.Add(open); chooser.Controls.Add(retry); AppBranding.Apply(chooser); DesktopTheme.Apply(chooser);
            if (recent.Length > 0) list.SelectedIndex = 0; else list.Items.Add("No compact build diagnostics available.");
            if (chooser.ShowDialog(this) == DialogResult.OK) { string path = recent[list.SelectedIndex]; ShowDiagnosticDialog(DiagnosticArchive.FromHistory(path), path); }
        }
        catch (Exception) { _landingError.Text = "Recent diagnostics could not be opened. Existing working files have been kept."; }
    }
    private async Task RecoverPendingDeliveryAsync(string attempt, Form owner)
    {
        using (var lease = WorkingStorage.Lease(attempt))
        {
            if (!WorkingStorage.GetState(attempt).PendingDelivery) return;
            var marker = WorkingStorage.RequireOwner(attempt);
            string requests = SafePaths.Below(marker.Root, "requests");
            if (!WorkingStorage.Same(Path.GetDirectoryName(marker.Request)!, requests)) throw new IOException("Pending request ownership unavailable.");
            var request = JsonSerializer.Deserialize<LeanBuildRequest>(WorkingStorage.ReadBytes(marker.Request, 65536), AutoFollower.Wire.Options) ?? throw new IOException("Pending request unavailable.");
            var protection = BuildDestinations.Protection(request, new ApplicationTools(request.Python, request.PluginWriter, request.StagesDirectory ?? ""));
            var package = WorkingStorage.PendingPackage(attempt); string source = package.Path, hash = package.Sha256;
            using var picker = new SaveFileDialog { Title = "Save finished follower ZIP", FileName = Path.GetFileName(source), Filter = "Follower ZIP (*.zip)|*.zip", OverwritePrompt = false, RestoreDirectory = true };
            if (picker.ShowDialog(owner) != DialogResult.OK) return;
            string destination = Path.GetFullPath(picker.FileName); CheckDiagnosticTarget(destination);
            if (protection.Files.Any(p => WorkingStorage.Same(p, destination)) || protection.Directories.Any(p => destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new IOException("Protected package destination.");
            bool created = false;
            try
            {
                await Task.Run(() =>
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None); created = true;
                    byte[] buffer = new byte[1024 * 1024]; int count;
                    while ((count = input.Read(buffer)) != 0) { _operationCancel?.Token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                });
                _operationCancel?.Token.ThrowIfCancellationRequested();
                if (!Hashing.File(destination).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Saved package differs.");
                WorkingStorage.Delivered(attempt, source, destination);
            }
            catch { if (created) try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
            _landingError.Text = "Finished follower ZIP saved.";
        }
        await Task.Run(() => WorkingStorage.Finish(attempt, false, "success"));
    }
    private static Form CreateFailureReportDialog(DiagnosticReport report)
    {
        var dialog = new Form { Text = "Diagnostic report", Size = new Size(780, 610), MinimumSize = new Size(480, 420), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        AppBranding.Apply(dialog);
        var text = new TextBox { Text = report.Text, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, AccessibleName = "Anonymized failure report" };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 132, AutoScroll = true };
        var copy = new ReadableButton { Text = "Copy report", AutoSize = true }; var close = new ReadableButton { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        copy.Click += (_, _) => { try { Clipboard.SetText(report.Text); copy.Text = "Copied"; } catch (System.Runtime.InteropServices.ExternalException) { copy.Text = "Retry copy"; } };
        var save = new ReadableButton { Name = "SaveDiagnosticZip", Text = "Save diagnostic ZIP…", AutoSize = true };
        var privateFiles = new CheckBox { Name = "IncludePrivateDiagnostics", Text = "Include private files (not anonymized)", AutoSize = true, Checked = false, AccessibleDescription = "Optional. Files can reveal paths, character/mod names and settings. Share privately." };
        var notice = new Label { Text = "May reveal paths, character/mod names and settings. Share privately.", AutoSize = true };
        var feedback = new Label { Name = "DiagnosticFeedback", AutoSize = true, MaximumSize = new Size(690, 0) };
        controls.Controls.Add(copy); controls.Controls.Add(save); controls.Controls.Add(close); controls.SetFlowBreak(close, true);
        controls.Controls.Add(privateFiles); controls.SetFlowBreak(privateFiles, true); controls.Controls.Add(notice); controls.SetFlowBreak(notice, true); controls.Controls.Add(feedback);
        dialog.Controls.Add(text); dialog.Controls.Add(controls); dialog.CancelButton = close;
        controls.SizeChanged += (_, _) => { notice.MaximumSize = feedback.MaximumSize = new Size(Math.Max(300, controls.ClientSize.Width - 24), 0); };
        DesktopTheme.Apply(dialog); return dialog;
    }
    private static string FriendlyError(Exception e) => e is AggregateException aggregate ? FriendlyError(aggregate.GetBaseException()) : e.Message;
    private static void OpenPath(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private sealed record OptionItem(string Id, string Label);
    private void UpdateOutputHelp(CheckBox prebuilt, CheckBox obody, Control outputGroup)
    {
        string help = !prebuilt.Checked && !obody.Checked
            ? "No version is selected. Select at least one output version to build."
            : prebuilt.Checked && obody.Checked
            ? "Both versions are selected. Both builds must succeed; the ZIP offers an installer choice."
            : "One version is selected. The build creates a plain ZIP.";
        _tips.SetToolTip(outputGroup, help);
        outputGroup.AccessibleDescription = help;
    }

    private string ObodyOutputHelp()
    {
        if (_setup == null) return "OBody NG availability is not confirmed. Choose the installed Skyrim folder on Inputs first.";
        if (!_obodyAvailable) return "OBody NG is unavailable in the confirmed environment. The installed meshes stay as-is.";
        if (_preview?.Cosave.Preset is not null)
            return "Uses installed body and outfit meshes as-is and applies the saved preset through OBody NG in game, without running BodySlide.";
        if (_preview?.Cosave.ObodyAssignment == "unassigned")
            return "This save has no OBody preset assignment. Select As-is (no BodySlide) to use the installed meshes.";
        return "A supported OBody preset assignment is not confirmed. Refresh the selected inputs before choosing this output.";
    }

    private sealed record SettingsControls(TabPage Tab, TextBox ModName, TextBox CharacterName, NumericUpDown Scale, NumericUpDown Minimum, TextBox Maximum, ComboBox LevelMode, NumericUpDown Multiplier, ComboBox Class, ComboBox Voice, CheckBox Marriage, ComboBox Combat, ComboBox Perks, ComboBox Spells, CheckBox Disclaimer, Button Build, bool Advanced, Label? BasicMapping, CheckBox Prebuilt, CheckBox Obody, Control Outputs, Label OutputHelp, ComboBox? RaceMode);
}

