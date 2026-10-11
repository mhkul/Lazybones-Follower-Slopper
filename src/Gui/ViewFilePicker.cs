using AutoFollower;

namespace AutoFollower.Gui;

// Enumerates in this process so CharGen reflects the same installed Data view
// as source resolution, including exports from multiple enabled MO2 providers.
internal sealed class ViewFilePicker : Form
{
    private readonly TextBox _directory = new() { Dock=DockStyle.Fill };
    private readonly ListBox _files = new() { Dock=DockStyle.Fill, DisplayMember="Label" };
    private readonly Label _error = new() { Dock=DockStyle.Fill, AutoEllipsis=true };
    private readonly string _extension;
    public string? FileName {get;private set;}
    public string? Backing {get;private set;}
    public ViewFilePicker(string title,string initial,string extension)
    {
        Text=title;_extension=extension;Size=new Size(780,520);MinimumSize=new Size(650,400);StartPosition=FormStartPosition.CenterParent;AppBranding.Apply(this);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(12)};
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));Controls.Add(root);
        var address=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3};address.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,55));address.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));address.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,70));
        var up=new ReadableButton{Text="Up",Dock=DockStyle.Fill};var go=new ReadableButton{Text="Open",Dock=DockStyle.Fill};address.Controls.Add(up,0,0);address.Controls.Add(_directory,1,0);address.Controls.Add(go,2,0);root.Controls.Add(address,0,0);
        up.Click+=(_,_)=>Navigate(Path.GetDirectoryName(_directory.Text)??_directory.Text);go.Click+=(_,_)=>Navigate(_directory.Text);_directory.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){Navigate(_directory.Text);e.SuppressKeyPress=true;}};
        root.Controls.Add(_files,0,1);root.Controls.Add(_error,0,2);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var choose=new ReadableButton{Text="Choose file",AutoSize=true};choose.Click+=(_,_)=>Choose();var cancel=new ReadableButton{Text="Cancel",AutoSize=true};cancel.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};actions.Controls.Add(choose);actions.Controls.Add(cancel);root.Controls.Add(actions,0,3);CancelButton=cancel;
        _files.DoubleClick+=(_,_)=>Choose();_files.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){Choose();e.SuppressKeyPress=true;}};
        DesktopTheme.Apply(this);Navigate(initial);
    }
    private void Navigate(string path)
    {
        try
        {
            path=Path.GetFullPath(path);_files.Items.Clear();_directory.Text=path;
            var entries=Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName).Select(x=>new Entry(x,true))
                .Concat(Directory.EnumerateFiles(path,"*"+_extension).OrderBy(Path.GetFileName).Select(x=>new Entry(x,false))).Take(10001).ToArray();
            if(entries.Length>10000)throw new InvalidDataException("Folder has more than 10,000 entries. Open a narrower folder.");
            _files.Items.AddRange(entries);_error.Text=entries.Any(x=>!x.Folder)?"":"No matching files in this folder; open another folder.";
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){_error.Text=e.Message;}
    }
    private void Choose()
    {
        if(_files.SelectedItem is not Entry entry)return;
        if(entry.Folder){Navigate(entry.Path);return;}
        try
        {
            using var input=new FileStream(entry.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            Backing=LeanMo2Sources.Backing(input);SafePaths.NoLinks(Backing);
            FileName=entry.Path;DialogResult=DialogResult.OK;Close();
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){_error.Text=e.Message;}
    }
    private sealed record Entry(string Path,bool Folder){public string Label=>(Folder?"[Folder] ":"")+System.IO.Path.GetFileName(Path);}
}
