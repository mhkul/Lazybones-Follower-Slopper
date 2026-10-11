using AutoFollower;
namespace AutoFollower.Gui;

internal sealed class EquipmentChoiceDialog : Form
{
    readonly LeanEquipmentChoice[] _choices;
    readonly CheckedListBox _list=new(){Dock=DockStyle.Fill,CheckOnClick=true,HorizontalScrollbar=true};
    public string[] Excluded=>_choices.Where((_,i)=>!_list.GetItemChecked(i)).Select(x=>x.Canonical).ToArray();
    public EquipmentChoiceDialog(LeanEquipmentChoice[] choices,IEnumerable<string> excluded)
    {
        _choices=choices;var omitted=excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Text="Equipment to include";AppBranding.Apply(this);Size=new(720,460);MinimumSize=new(560,330);StartPosition=FormStartPosition.CenterParent;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(14),RowCount=3,ColumnCount=1};layout.RowStyles.Add(new(SizeType.Absolute,52));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,45));Controls.Add(layout);
        layout.Controls.Add(new Label{Text="Checked items are included in every selected output. Uncheck an item to leave it out. Asset packaging choices remain separate.",Dock=DockStyle.Fill},0,0);
        foreach(var c in choices)_list.Items.Add(c.Name+" — "+c.Canonical,!omitted.Contains(c.Canonical));layout.Controls.Add(_list,0,1);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var save=new ReadableButton{Text="Apply",DialogResult=DialogResult.OK,Width=100};var cancel=new ReadableButton{Text="Cancel",DialogResult=DialogResult.Cancel,Width=100};var all=new ReadableButton{Text="Include all",Width=120};all.Click+=(_,_)=>{for(int i=0;i<_list.Items.Count;i++)_list.SetItemChecked(i,true);};buttons.Controls.Add(save);buttons.Controls.Add(cancel);buttons.Controls.Add(all);layout.Controls.Add(buttons,0,2);AcceptButton=save;CancelButton=cancel;DesktopTheme.Apply(this);
    }
}
