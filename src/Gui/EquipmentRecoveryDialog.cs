using AutoFollower;
namespace AutoFollower.Gui;

internal sealed class EquipmentRecoveryDialog : Form
{
    public EquipmentRecoveryDialog(LeanEquipmentRecoveryChoice choice)
    {
        Text="Build with equipment issues";AppBranding.Apply(this);StartPosition=FormStartPosition.CenterParent;
        Size=new Size(780,510);MinimumSize=new Size(740,460);Font=new Font("Segoe UI",9F);
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(18)};
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,choice.Explanation==null?84:120));root.RowStyles.Add(new RowStyle(SizeType.Absolute,52));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,44));Controls.Add(root);
        root.Controls.Add(new Label{Dock=DockStyle.Fill,Text=choice.Explanation??"These equipment items call a missing file or an unsupported creator texture path. Continue to build without the listed items, or cancel to repair the input. Omitted items will be absent from both variants and recorded in your local build details."},0,0);
        root.Controls.Add(new Label{Dock=DockStyle.Fill,Text="Unavailable: "+string.Join(", ",choice.FailedReferences)},0,1);
        var items=new ListBox{Dock=DockStyle.Fill};items.Items.AddRange(choice.Items.Select(x=>x.Name+" · "+x.Canonical).ToArray());root.Controls.Add(items,0,2);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var proceed=new ReadableButton{Text=$"Build without {choice.Items.Length} items",AutoSize=true};proceed.Click+=(_,_)=>{DialogResult=DialogResult.OK;Close();};
        var cancel=new ReadableButton{Text="Cancel build",AutoSize=true};cancel.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};actions.Controls.Add(proceed);actions.Controls.Add(cancel);root.Controls.Add(actions,0,3);CancelButton=cancel;DesktopTheme.Apply(this);
    }
}
