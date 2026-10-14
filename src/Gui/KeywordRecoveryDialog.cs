using AutoFollower;
namespace AutoFollower.Gui;

internal sealed class KeywordRecoveryDialog : Form
{
    readonly LeanKeywordCandidate[] _candidates;
    readonly DataGridView _list=new(){Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false};
    readonly ReadableButton _remove=new(){Text="Remove selected keywords",AutoSize=true,Enabled=false};
    public LeanKeywordRemoval[] Removals {get;private set;}=[];
    public KeywordRecoveryDialog(LeanKeywordCandidate[] candidates)
    {
        _candidates=candidates;Text="Keep or remove keywords";AppBranding.Apply(this);
        Size=new(860,470);MinimumSize=new(680,370);StartPosition=FormStartPosition.CenterParent;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(14),RowCount=3,ColumnCount=1};
        layout.RowStyles.Add(new(SizeType.Absolute,78));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,46));Controls.Add(layout);
        layout.Controls.Add(new Label{Dock=DockStyle.Fill,Text="These keywords still need providers. Keep them and their dependencies, or keep unavailable keywords and build with warnings. Check a row to remove that keyword from the shown generated record. Removing a keyword may change compatibility or gameplay. Original mods stay unchanged."},0,0);
        _list.Columns.Add(new DataGridViewCheckBoxColumn{Name="remove",HeaderText="Remove keyword",FillWeight=70,SortMode=DataGridViewColumnSortMode.NotSortable});
        _list.ColumnHeadersDefaultCellStyle.WrapMode=DataGridViewTriState.True;
        _list.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        foreach(var column in new[]{("record","Generated record",150), ("keyword","Keyword",170), ("provider","Provider / namespace",125), ("status","Status",160)})_list.Columns.Add(new DataGridViewTextBoxColumn{Name=column.Item1,HeaderText=column.Item2,FillWeight=column.Item3,ReadOnly=true,SortMode=DataGridViewColumnSortMode.NotSortable});
        _list.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        foreach(var c in candidates)_list.Rows.Add(false,c.ConsumerName+"\n"+c.GeneratedRecord,(c.EditorId==null?"":c.EditorId+"\n")+c.Keyword,c.Provider,c.Status);
        _list.CurrentCellDirtyStateChanged+=(_,_)=>{if(_list.IsCurrentCellDirty)_list.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        _list.CellValueChanged+=(_,_)=>_remove.Enabled=_list.Rows.Cast<DataGridViewRow>().Any(r=>r.Cells[0].Value is true);layout.Controls.Add(_list,0,1);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var keep=new ReadableButton{Text="Keep keywords and build",AutoSize=true};keep.Click+=(_,_)=>{Removals=[];DialogResult=DialogResult.OK;Close();};
        _remove.Click+=(_,_)=>{_list.EndEdit();Removals=LeanKeywordRecovery.Select(_candidates,_candidates.Where((_,i)=>_list.Rows[i].Cells[0].Value is true).Select(c=>c.Removal).ToArray());DialogResult=DialogResult.OK;Close();};
        var cancel=new ReadableButton{Text="Cancel build",AutoSize=true};cancel.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};
        actions.Controls.Add(keep);actions.Controls.Add(_remove);actions.Controls.Add(cancel);layout.Controls.Add(actions,0,2);
        AcceptButton=keep;CancelButton=cancel;DesktopTheme.Apply(this);
    }
}
